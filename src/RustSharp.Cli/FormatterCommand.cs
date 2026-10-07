using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Cli;

/// <summary>The bounded fmt command. All documents are verified before the first source file is replaced.</summary>
internal static class FormatterCommand
{
    private const int MaximumFiles = 1024;
    private const int MaximumEntries = 100_000;
    private const long MaximumInputBytes = 16_777_216;
    private const int MaximumFileBytes = 4_000_000;
    private const int ReadChunkBytes = 65_536;
    private const int MaximumReadChunks = (MaximumFileBytes / ReadChunkBytes) + 2;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static int Run(string[] args, CancellationToken cancellationToken = default)
    {
        bool checkOnly = args.Length == 3 && args[1] == "--check";
        if (!(args.Length == 2 || checkOnly) || args[^1].StartsWith('-'))
        {
            Console.Error.WriteLine("Usage: rsc fmt [--check] <source.rs|directory>");
            return 2;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ioCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, deadline.Token);
        var clock = Stopwatch.StartNew();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        var temporaries = new List<string>();
        int cleanupFailures = 0;
        var cleanupErrors = new List<Exception>();
        Exception? primaryFailure = null;
        void Guard()
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested || clock.Elapsed >= TimeSpan.FromSeconds(10))
                throw new TimeoutException("fmt command exceeded ten seconds.");
        }
        byte[] ReadBounded(string path, long maximumBytes)
        {
            Guard();
            if (maximumBytes is < 0 or > MaximumFileBytes)
                throw new InvalidDataException("fmt read byte bound is invalid.");
            RejectLink(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                ReadChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            RejectLink(path); // Recheck the actual path and ancestors after opening.
            using var output = new MemoryStream();
            var buffer = new byte[ReadChunkBytes];
            long actualBytes = 0;
            for (int chunk = 0; chunk < MaximumReadChunks; chunk++)
            {
                Guard();
                int requested = (int)Math.Min(buffer.Length, maximumBytes - actualBytes + 1);
                int read = input.ReadAsync(buffer.AsMemory(0, requested), ioCancellation.Token)
                    .AsTask().GetAwaiter().GetResult();
                Guard();
                if (read == 0) return output.ToArray();
                actualBytes = checked(actualBytes + read);
                if (actualBytes > maximumBytes)
                    throw new InvalidDataException("fmt input byte limit exceeded during reading.");
                output.Write(buffer, 0, read);
            }
            throw new InvalidDataException("fmt read chunk limit exceeded.");
        }
        FileAttributes RejectLink(string path)
        {
            Guard();
            string full = Path.GetFullPath(path);
            FileAttributes attributes = File.GetAttributes(full);
            Guard();
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("fmt refuses a linked or non-file source.");
            DirectoryInfo? parent = Directory.GetParent(full);
            for (int depth = 0; parent is not null && depth < 128; depth++)
            {
                Guard();
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("fmt refuses a linked source ancestor.");
                parent = parent.Parent;
            }
            if (parent is not null) throw new IOException("fmt source ancestry exceeds 128 levels.");
            return attributes;
        }
        void Recheck(string path, byte[] original, FileAttributes attributes, UnixFileMode? mode)
        {
            Guard();
            if (RejectLink(path) != attributes ||
                (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != mode))
                throw new IOException("Source permissions or attributes changed during formatting: " + path);
            if (!ReadBounded(path, original.Length).AsSpan().SequenceEqual(original))
                throw new IOException("Source changed during formatting: " + path);
            Guard();
        }
        void Cleanup()
        {
            // Cleanup has a separate finite budget so command timeout/cancellation cannot bypass it.
            // Exact paths enter this list only after this invocation successfully creates the file.
            var cleanupClock = Stopwatch.StartNew();
            int attempted = 0;
            for (int index = 0; index < temporaries.Count && index < MaximumFiles; index++)
            {
                if (cleanupClock.Elapsed >= TimeSpan.FromSeconds(20)) break;
                attempted++;
                try
                {
                    // File.Delete is harmless when a successful rename already consumed the owned path.
                    File.Delete(temporaries[index]);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    cleanupFailures++;
                    cleanupErrors.Add(new IOException($"Could not remove owned temporary '{temporaries[index]}'.", error));
                }
            }
            if (attempted < temporaries.Count)
            {
                cleanupFailures += temporaries.Count - attempted;
                cleanupErrors.Add(new TimeoutException($"Cleanup budget exhausted; {temporaries.Count - attempted} owned temporary path(s) require cleanup."));
            }
        }
        int exitCode;
        try
        {
            string root = Path.GetFullPath(args[^1]);
            List<string> paths = Discover(root, Guard);
            var prepared = new List<(string Path, byte[] Original, byte[] Output, FileAttributes Attributes, UnixFileMode? Mode)>();
            long bytes = 0;
            long outputBytes = 0;
            bool rejected = false;
            foreach (string path in paths)
            {
                Guard();
                FileAttributes attributes = RejectLink(path);
                UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);
                byte[] original = ReadBounded(path, Math.Min(MaximumFileBytes, MaximumInputBytes - bytes));
                bytes = checked(bytes + original.Length);
                string source = Utf8.GetString(original); // Retains U+FEFF; File.ReadAllText would consume the BOM.
                Guard();
                TimeSpan remaining = TimeSpan.FromSeconds(10) - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("fmt command exceeded ten seconds.");
                FormatterResult result = RustFormatter.Format(source, path,
                    new FormatterOptions { CheckOnly = checkOnly, Timeout = remaining },
                    ioCancellation.Token);
                Guard();
                if (!result.Success)
                {
                    rejected = true;
                    foreach (Diagnostic diagnostic in result.Diagnostics.Take(128))
                    {
                        Guard();
                        Console.Error.WriteLine($"{path}: {diagnostic.Code}: {diagnostic.Message}");
                    }
                }
                else if (result.Changed)
                {
                    byte[] output = Utf8.GetBytes(result.FormattedSource);
                    outputBytes = checked(outputBytes + output.Length);
                    if (outputBytes > MaximumInputBytes) throw new InvalidDataException("fmt aggregate output byte limit exceeded.");
                    prepared.Add((path, original, output, attributes, mode));
                }
            }
            if (rejected)
                exitCode = 1;
            else if (checkOnly)
            {
                Console.WriteLine($"Formatting checked {paths.Count} source file(s).");
                exitCode = 0;
            }
            else
            {
                // Prepare every output without mutating a source; replacements are atomic same-directory renames.
                foreach (var document in prepared)
                {
                    Recheck(document.Path, document.Original, document.Attributes, document.Mode);
                    string temporary = Path.Combine(Path.GetDirectoryName(document.Path)!,
                        ".rustsharp-fmt-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        ReadChunkBytes, FileOptions.Asynchronous);
                    temporaries.Add(temporary); // CreateNew succeeded: this path is now owned by this invocation.
                    if (!OperatingSystem.IsWindows() && document.Mode is UnixFileMode mode)
                        File.SetUnixFileMode(temporary, mode);
                    stream.WriteAsync(document.Output, ioCancellation.Token).AsTask().GetAwaiter().GetResult();
                    stream.FlushAsync(ioCancellation.Token).GetAwaiter().GetResult();
                    stream.Flush(flushToDisk: true);
                    Guard();
                    // Writing may clear set-ID bits: restore and verify mode after all bytes are written.
                    if (!OperatingSystem.IsWindows() && document.Mode is UnixFileMode finalMode)
                    {
                        File.SetUnixFileMode(temporary, finalMode);
                        if (File.GetUnixFileMode(temporary) != finalMode)
                            throw new IOException("fmt could not preserve the source Unix mode.");
                    }
                    Guard();
                }
                for (int index = 0; index < prepared.Count; index++)
                {
                    var document = prepared[index];
                    Recheck(document.Path, document.Original, document.Attributes, document.Mode);
                    File.Move(temporaries[index], document.Path, overwrite: true);
                    Guard();
                }
                Console.WriteLine($"Formatted {prepared.Count} of {paths.Count} source file(s).");
                exitCode = 0;
            }
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            primaryFailure = error;
            Console.Error.WriteLine($"rsc fmt: {FormatterDiagnosticCodes.LimitReached}: fmt command exceeded ten seconds.");
            exitCode = 1;
        }
        catch (OperationCanceledException error)
        {
            primaryFailure = error;
            Console.Error.WriteLine("rsc fmt: operation cancelled.");
            exitCode = 130;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException or TimeoutException)
        {
            primaryFailure = error;
            Console.Error.WriteLine($"rsc fmt: {FormatterDiagnosticCodes.LimitReached}: {error.Message}");
            exitCode = 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            try { Cleanup(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                cleanupFailures++;
                cleanupErrors.Add(error);
            }
        }
        // Aggregate cleanup failures outside finally, preserving the prior semantic/cancellation failure.
        if (cleanupErrors.Count != 0)
        {
            var errors = new List<Exception>(cleanupErrors.Count + 1);
            if (primaryFailure is not null) errors.Add(primaryFailure);
            errors.AddRange(cleanupErrors);
            var aggregate = new AggregateException("fmt owned temporary cleanup failed.", errors);
            Console.Error.WriteLine($"rsc fmt: {FormatterDiagnosticCodes.LimitReached}: {aggregate}");
        }
        // Preserve any source/cancellation diagnostic and its nonzero exit code.
        return exitCode == 0 && cleanupFailures > 0 ? 1 : exitCode;
    }

    private static List<string> Discover(string root, Action guard)
    {
        if (File.Exists(root))
        {
            if (!root.EndsWith(".rs", StringComparison.OrdinalIgnoreCase)) throw new IOException("fmt requires a .rs file or directory.");
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("fmt refuses a source symlink.");
            return [root];
        }
        if (!Directory.Exists(root)) throw new IOException("Formatting path does not exist: " + root);
        var pending = new Stack<string>();
        var files = new List<string>();
        pending.Push(root);
        int entries = 0;
        while (pending.Count > 0 && entries < MaximumEntries)
        {
            guard();
            string directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("fmt refuses a directory symlink.");
            foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                guard();
                if (++entries > MaximumEntries) throw new InvalidDataException("fmt directory entry limit exceeded.");
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("fmt refuses a linked directory entry.");
                if (entry is DirectoryInfo)
                {
                    if (entry.Name is not (".git" or "bin" or "obj" or "target")) pending.Push(entry.FullName);
                }
                else if (entry.Name.EndsWith(".rs", StringComparison.OrdinalIgnoreCase))
                {
                    if (files.Count >= MaximumFiles) throw new InvalidDataException("fmt file count limit exceeded.");
                    files.Add(entry.FullName);
                }
            }
        }
        if (pending.Count > 0) throw new InvalidDataException("fmt directory entry limit exceeded.");
        if (files.Count == 0) throw new IOException("Formatting path contains no .rs files.");
        files.Sort(StringComparer.Ordinal);
        return files;
    }
}
