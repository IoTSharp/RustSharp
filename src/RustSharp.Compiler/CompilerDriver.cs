using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using RustSharp.CodeGen.IL;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

public sealed class CompilerDriver
{
    private const int MaximumSourceBytes = 16 * 1024 * 1024;
    private const int SourceReadBufferBytes = 64 * 1024;
    // The sidecar lock is intentionally bounded. A compiler invocation that
    // cannot acquire its output lease within this window fails with a useful
    // diagnostic instead of waiting forever behind a crashed or hung writer.
    private const int OutputLockAttempts = 120;
    private const int OutputLockRetryMilliseconds = 50;
    private const long OutputLockRegionLength = 1;
    private const int MaximumTransactionDiagnosticCharacters = 512;
    // A stream is allowed to return one byte per read. Keep the upper bound
    // finite even for that worst case while normal files finish in a few reads.
    private const int MaximumSourceReadChunks = MaximumSourceBytes + 1;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly object OutputLockRegistryGate = new();
    private static readonly Dictionary<string, OutputLockEntry> OutputLockEntries = new(StringComparer.Ordinal);

    public static CompilationResult CheckFile(string sourcePath,
        CompilationProfile profile = CompilationProfile.VerticalSlice, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var readResult = ReadSource(fullSourcePath);
        if (readResult.Diagnostic is not null)
        {
            return CompilationResult.Failed([readResult.Diagnostic]);
        }

        return Check(readResult.Document!.Source, fullSourcePath, profile, cancellationToken);
    }

    public static CompilationResult Check(string source, string sourcePath = "<memory>",
        CompilationProfile profile = CompilationProfile.VerticalSlice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        Diagnostic? sourceDiagnostic = ValidateSourceText(source, sourcePath);
        if (sourceDiagnostic is not null)
        {
            return CompilationResult.Failed([sourceDiagnostic]);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (profile != CompilationProfile.VerticalSlice)
        {
            SafeCoreClrResult result = AnalyzeSafeCore(source, sourcePath, profile, cancellationToken);
            return result.IsSuccessful ? new(true, [], null) : CompilationResult.Failed(result.Diagnostics);
        }

        var syntaxTree = SyntaxTree.Parse(source, sourcePath);
        return syntaxTree.Diagnostics.Count == 0
            ? new CompilationResult(true, [], null)
            : CompilationResult.Failed(syntaxTree.Diagnostics);
    }

    public static CompilationResult CompileFile(
        string sourcePath,
        string outputPath,
        string? assemblyName = null,
        CompilationProfile profile = CompilationProfile.VerticalSlice,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var readResult = ReadSource(fullSourcePath);
        if (readResult.Diagnostic is not null)
        {
            return CompilationResult.Failed([readResult.Diagnostic]);
        }

        return CompileCore(
            readResult.Document!.Source,
            fullSourcePath,
            Path.GetFullPath(outputPath),
            assemblyName,
            readResult.Document.Bytes,
            profile,
            cancellationToken);
    }

    public static CompilationResult Compile(
        string source,
        string sourcePath,
        string outputPath,
        string? assemblyName = null,
        CompilationProfile profile = CompilationProfile.VerticalSlice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        Diagnostic? sourceDiagnostic = ValidateSourceText(source, sourcePath);
        if (sourceDiagnostic is not null)
        {
            return CompilationResult.Failed([sourceDiagnostic]);
        }

        byte[] sourceBytes;
        try
        {
            sourceBytes = StrictUtf8.GetBytes(source);
        }
        catch (EncoderFallbackException exception)
        {
            return CompilationResult.Failed(
                [new Diagnostic(
                    "RSC0005",
                    $"Source text '{sourcePath}' is not valid UTF-8: {exception.Message}",
                    new TextSpan(0, 0))]);
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        return CompileCore(
            source,
            sourcePath,
            fullOutputPath,
            assemblyName,
            sourceBytes,
            profile,
            cancellationToken);
    }

    private static CompilationResult CompileCore(
        string source,
        string sourcePath,
        string outputPath,
        string? assemblyName,
        ReadOnlyMemory<byte> sourceBytes,
        CompilationProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SyntaxTree? syntaxTree = null;
        SafeCoreClrResult? safeCore = null;
        if (profile == CompilationProfile.VerticalSlice)
        {
            syntaxTree = SyntaxTree.Parse(source, sourcePath);
            if (syntaxTree.Diagnostics.Count != 0 || syntaxTree.Root is null)
                return CompilationResult.Failed(syntaxTree.Diagnostics);
        }
        else
        {
            safeCore = AnalyzeSafeCore(source, sourcePath, profile, cancellationToken);
            if (!safeCore.IsSuccessful) return CompilationResult.Failed(safeCore.Diagnostics);
        }

        var resolvedAssemblyName = assemblyName ?? Path.GetFileNameWithoutExtension(outputPath);
        if (!IsValidAssemblyName(resolvedAssemblyName))
        {
            return CompilationResult.Failed(
                [new Diagnostic(
                    "RSC0002",
                    $"'{resolvedAssemblyName}' is not a valid generated assembly name.",
                    new TextSpan(0, 0))]);
        }

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var pdbPath = Path.ChangeExtension(fullOutputPath, ".pdb");
        var runtimeConfigPath = Path.ChangeExtension(fullOutputPath, ".runtimeconfig.json");
        if (PathsCollide(fullSourcePath, fullOutputPath) ||
            PathsCollide(fullSourcePath, pdbPath) ||
            PathsCollide(fullSourcePath, runtimeConfigPath) ||
            PathsCollide(fullOutputPath, pdbPath) ||
            PathsCollide(fullOutputPath, runtimeConfigPath) ||
            PathsCollide(pdbPath, runtimeConfigPath))
        {
            return CompilationResult.Failed(
                [new Diagnostic(
                    "RSC0006",
                    "The source and compiler output paths must be distinct.",
                    new TextSpan(0, 0))]);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generated = safeCore is not null
                ? ClrLirAssemblyEmitter.EmitProgram(safeCore, resolvedAssemblyName, source,
                    fullSourcePath, Path.GetFileName(pdbPath), sourceBytes)
                : IlAssemblyEmitter.Emit(
                syntaxTree!.Root!,
                source,
                fullSourcePath,
                resolvedAssemblyName,
                Path.GetFileName(pdbPath),
                sourceBytes);

            WriteArtifactsTransactionally(
                fullOutputPath,
                pdbPath,
                runtimeConfigPath,
                generated);

            return new CompilationResult(
                true,
                [],
                new CompilationOutput(fullOutputPath, pdbPath, runtimeConfigPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CompilationResult.Failed(
                [new Diagnostic(
                    "RSC0003",
                    $"Could not write compiler output: {exception.Message}",
                    new TextSpan(0, 0))]);
        }
    }

    private static SafeCoreClrResult AnalyzeSafeCore(string source, string sourcePath,
        CompilationProfile profile, CancellationToken cancellationToken)
    {
        if (profile != CompilationProfile.SafeCorePrimitives)
            return new([], [], [new("RSC0007", "Unknown compilation profile.", new TextSpan(0, 0))]);
        cancellationToken.ThrowIfCancellationRequested();
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, sourcePath);
        if (!syntax.IsSuccessful) return new([], [], syntax.Diagnostics);
        cancellationToken.ThrowIfCancellationRequested();
        SafeCoreHirResult hir = SafeCoreHirLowering.Lower(syntax);
        SafeCoreTypeCheckResult types = SafeCoreTypeChecking.Check(hir, cancellationToken);
        return types.IsSuccessful ? SafeCoreClrLowering.Lower(types.Program!, cancellationToken)
            : new([], [], types.Diagnostics);
    }

    private static void WriteArtifactsTransactionally(
        string assemblyPath,
        string pdbPath,
        string runtimeConfigPath,
        GeneratedAssembly generated)
    {
        var outputDirectory = Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The output path has no parent directory.");
        Directory.CreateDirectory(outputDirectory);

        // FileStream.Lock is an OS-level advisory/mandatory lock (depending on
        // the platform), so it serializes compiler instances in this process
        // and in other processes without leaving an ownership lease to expire.
        // The directory scope also covers transactions whose derived artifact
        // paths overlap despite having different assembly output names.
        using var outputLock = AcquireOutputDirectoryLock(outputDirectory);

        string transactionId = $".rsc-transaction-{Environment.ProcessId}-{Guid.NewGuid():N}";
        string transactionDirectory = Path.Combine(outputDirectory, transactionId);
        string stagingDirectory = Path.Combine(outputDirectory, transactionId, "staged");
        string backupDirectory = Path.Combine(outputDirectory, transactionId, "backups");
        var artifacts = new[]
        {
            new PendingArtifact(assemblyPath, Path.Combine(stagingDirectory, Path.GetFileName(assemblyPath)), generated.PeImage),
            new PendingArtifact(pdbPath, Path.Combine(stagingDirectory, Path.GetFileName(pdbPath)), generated.PdbImage),
            new PendingArtifact(
                runtimeConfigPath,
                Path.Combine(stagingDirectory, Path.GetFileName(runtimeConfigPath)),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(generated.RuntimeConfigJson)),
        };
        var movedTargets = new List<string>(artifacts.Length);
        var backups = new List<(string Target, string Backup)>(artifacts.Length);
        var transactionDiagnostics = new List<string>(capacity: artifacts.Length + 1);
        Exception? failure = null;
        var committed = false;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            Directory.CreateDirectory(backupDirectory);

            foreach (PendingArtifact artifact in artifacts)
            {
                WriteBytesDurably(artifact.StagedPath, artifact.Content);
            }

            foreach (PendingArtifact artifact in artifacts)
            {
                if (File.Exists(artifact.TargetPath))
                {
                    string backupPath = Path.Combine(backupDirectory, Path.GetFileName(artifact.TargetPath));
                    File.Move(artifact.TargetPath, backupPath);
                    backups.Add((artifact.TargetPath, backupPath));
                }

                File.Move(artifact.StagedPath, artifact.TargetPath);
                movedTargets.Add(artifact.TargetPath);
            }

            // Once every target has been installed, the transaction is
            // committed. Removing old backups is cleanup only and must never
            // cause the newly committed files to be rolled back.
            committed = true;
            DeleteBackupsBestEffort(backups, transactionDiagnostics);
        }
        catch (Exception exception)
        {
            failure = exception;
            if (!committed)
            {
                transactionDiagnostics.AddRange(
                    RollbackArtifacts(movedTargets, backups));
            }
        }
        finally
        {
            string? transactionCleanupDiagnostic = TryDeleteDirectory(transactionDirectory);
            if (transactionCleanupDiagnostic is not null)
            {
                transactionDiagnostics.Add(transactionCleanupDiagnostic);
            }
        }

        if (transactionDiagnostics.Count != 0)
        {
            Trace.WriteLine(
                $"RustSharp output transaction '{transactionDirectory}' cleanup: " +
                string.Join("; ", transactionDiagnostics));
        }

        if (failure is not null)
        {
            if (!committed && transactionDiagnostics.Count != 0)
            {
                throw new ArtifactTransactionException(failure, transactionDiagnostics);
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void DeleteBackupsBestEffort(
        List<(string Target, string Backup)> backups,
        List<string> diagnostics)
    {
        foreach ((_, string backupPath) in backups)
        {
            try
            {
                File.Delete(backupPath);
            }
            catch (Exception exception) when (IsFileSystemCleanupException(exception))
            {
                diagnostics.Add(
                    $"Could not remove backup '{backupPath}': {TrimDiagnostic(exception.Message)}");
            }
        }
    }

    private static List<string> RollbackArtifacts(
        List<string> movedTargets,
        List<(string Target, string Backup)> backups)
    {
        var diagnostics = new List<string>(capacity: movedTargets.Count + backups.Count);

        // Delete only targets that this transaction moved. Each operation is
        // independent so a locked artifact cannot prevent other backups from
        // being restored.
        for (var index = movedTargets.Count - 1; index >= 0; index--)
        {
            string targetPath = movedTargets[index];
            try
            {
                File.Delete(targetPath);
            }
            catch (Exception exception) when (IsFileSystemCleanupException(exception))
            {
                diagnostics.Add(
                    $"Could not remove moved target '{targetPath}': {TrimDiagnostic(exception.Message)}");
            }
        }

        for (var index = backups.Count - 1; index >= 0; index--)
        {
            (string targetPath, string backupPath) = backups[index];
            try
            {
                if (File.Exists(backupPath) && !File.Exists(targetPath))
                {
                    File.Move(backupPath, targetPath);
                }
            }
            catch (Exception exception) when (IsFileSystemCleanupException(exception))
            {
                diagnostics.Add(
                    $"Could not restore backup '{backupPath}' to '{targetPath}': " +
                    TrimDiagnostic(exception.Message));
            }
        }

        return diagnostics;
    }

    private static OutputPathLock AcquireOutputDirectoryLock(string outputDirectory)
    {
        string canonicalPath = GetCanonicalOutputDirectory(outputDirectory);
        OutputLockEntry entry;
        lock (OutputLockRegistryGate)
        {
            if (!OutputLockEntries.TryGetValue(canonicalPath, out entry!))
            {
                entry = new OutputLockEntry();
                OutputLockEntries.Add(canonicalPath, entry);
            }

            entry.ReferenceCount++;
        }

        var monitorHeld = false;
        try
        {
            if (!Monitor.TryEnter(
                    entry.Gate,
                    OutputLockAttempts * OutputLockRetryMilliseconds))
            {
                throw new IOException(
                    $"Could not acquire the in-process compiler output lock for '{outputDirectory}' after " +
                    $"{OutputLockAttempts * OutputLockRetryMilliseconds} ms.");
            }

            monitorHeld = true;
            FileStream lockStream = AcquireCrossProcessOutputLock(outputDirectory);
            return new OutputPathLock(canonicalPath, entry, lockStream);
        }
        catch
        {
            if (monitorHeld)
            {
                Monitor.Exit(entry.Gate);
            }

            ReleaseOutputLockEntry(canonicalPath, entry);
            throw;
        }
    }

    private static FileStream AcquireCrossProcessOutputLock(string outputDirectory)
    {
        string lockPath = GetOutputLockPath(outputDirectory);
        IOException? lastException = null;

        for (var attempt = 0; attempt < OutputLockAttempts; attempt++)
        {
            FileStream? lockStream = null;
            try
            {
                lockStream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    OperatingSystem.IsMacOS() ? FileShare.None : FileShare.ReadWrite,
                    bufferSize: 1,
                    FileOptions.None);
                if (!OperatingSystem.IsMacOS())
                {
                    lockStream.Lock(0, OutputLockRegionLength);
                }
                return lockStream;
            }
            catch (IOException exception)
            {
                lastException = exception;
                DisposeLockStream(lockStream);
                if (attempt + 1 < OutputLockAttempts)
                {
                    Thread.Sleep(OutputLockRetryMilliseconds);
                }
            }
            catch (PlatformNotSupportedException exception)
            {
                DisposeLockStream(lockStream);
                throw new IOException(
                    "The current platform does not support output path locking.",
                    exception);
            }
            catch (NotSupportedException exception)
            {
                DisposeLockStream(lockStream);
                throw new IOException(
                    "The current file system does not support output path locking.",
                    exception);
            }
        }

        throw new IOException(
            $"Could not acquire the compiler output lock for '{outputDirectory}' after " +
            $"{OutputLockAttempts} attempts ({OutputLockAttempts * OutputLockRetryMilliseconds} ms).",
            lastException);
    }

    private static void DisposeLockStream(FileStream? lockStream)
    {
        if (lockStream is null)
        {
            return;
        }

        try
        {
            lockStream.Dispose();
        }
        catch (Exception exception) when (IsFileSystemCleanupException(exception))
        {
            Trace.WriteLine($"Could not dispose output lock stream: {TrimDiagnostic(exception.Message)}");
        }
    }

    private static string GetOutputLockPath(string outputDirectory)
    {
        string canonicalPath = GetCanonicalOutputDirectory(outputDirectory);
        string pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)));
        // Keep the sidecar instead of deleting it on release: deleting a lock
        // file races with a waiter that is opening the same path. OS locks are
        // released automatically when the owning process exits.
        return Path.Combine(outputDirectory, $".rsc-directory-{pathHash}.lock");
    }

    private static string GetCanonicalOutputDirectory(string outputDirectory)
    {
        string fullPath = Path.GetFullPath(outputDirectory);
        string withoutTrailingSeparator = Path.TrimEndingDirectorySeparator(fullPath);
        return OperatingSystem.IsWindows()
            ? withoutTrailingSeparator.ToUpperInvariant()
            : withoutTrailingSeparator;
    }

    private static void ReleaseOutputLockEntry(string canonicalPath, OutputLockEntry entry)
    {
        lock (OutputLockRegistryGate)
        {
            if (entry.ReferenceCount > 0)
            {
                entry.ReferenceCount--;
            }

            if (entry.ReferenceCount == 0 &&
                OutputLockEntries.TryGetValue(canonicalPath, out OutputLockEntry? current) &&
                ReferenceEquals(current, entry))
            {
                _ = OutputLockEntries.Remove(canonicalPath);
            }
        }
    }

    private static string? TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return null;
        }
        catch (Exception exception) when (IsFileSystemCleanupException(exception))
        {
            return $"Could not remove transaction directory '{path}': {TrimDiagnostic(exception.Message)}";
        }
    }

    private static bool IsFileSystemCleanupException(Exception exception) =>
        exception is IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        ArgumentException or
        System.Security.SecurityException;

    private static string TrimDiagnostic(string value) =>
        value.Length <= MaximumTransactionDiagnosticCharacters
            ? value
            : value[..MaximumTransactionDiagnosticCharacters] + "...";

    private sealed class OutputPathLock : IDisposable
    {
        private readonly string canonicalPath;
        private readonly OutputLockEntry entry;
        private readonly FileStream lockStream;
        private bool disposed;

        public OutputPathLock(
            string canonicalPath,
            OutputLockEntry entry,
            FileStream lockStream)
        {
            this.canonicalPath = canonicalPath;
            this.entry = entry;
            this.lockStream = lockStream;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                try
                {
                    if (!OperatingSystem.IsMacOS())
                    {
                        lockStream.Unlock(0, OutputLockRegionLength);
                    }
                }
                catch (Exception exception) when (IsFileSystemCleanupException(exception))
                {
                    Trace.WriteLine($"Could not unlock output path: {TrimDiagnostic(exception.Message)}");
                }

                DisposeLockStream(lockStream);
            }
            finally
            {
                Monitor.Exit(entry.Gate);
                ReleaseOutputLockEntry(canonicalPath, entry);
            }
        }
    }

    private sealed class OutputLockEntry
    {
        public object Gate { get; } = new();

        public int ReferenceCount { get; set; }
    }

    private sealed class ArtifactTransactionException : IOException
    {
        public ArtifactTransactionException(Exception original, IReadOnlyList<string> diagnostics)
            : base(
                $"Output transaction failed: {TrimDiagnostic(original.Message)}; " +
                $"rollback/cleanup diagnostics: {string.Join("; ", diagnostics)}",
                original)
        {
        }
    }

    private static void WriteBytesDurably(string path, ReadOnlyMemory<byte> content)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            SourceReadBufferBytes,
            FileOptions.SequentialScan);
        stream.Write(content.Span);
        stream.Flush(flushToDisk: true);
    }

    private static SourceReadResult ReadSource(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return SourceReadResult.Failed(new Diagnostic(
                    "RSC0001",
                    $"Source file '{sourcePath}' does not exist.",
                    new TextSpan(0, 0)));
            }

            using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                SourceReadBufferBytes,
                FileOptions.SequentialScan);
            long length = stream.Length;
            if (length > MaximumSourceBytes)
            {
                return SourceReadResult.Failed(new Diagnostic(
                    "RSC0004",
                    $"Source files larger than {MaximumSourceBytes} bytes are not supported.",
                    new TextSpan(0, 0)));
            }

            var bytes = new byte[(int)length];
            var read = 0;
            for (var chunk = 0; chunk < MaximumSourceReadChunks && read < bytes.Length; chunk++)
            {
                int count = stream.Read(bytes, read, bytes.Length - read);
                if (count == 0)
                {
                    return SourceReadResult.Failed(new Diagnostic(
                        "RSC0001",
                        $"Source file '{sourcePath}' ended before the declared length was read.",
                        new TextSpan(0, 0)));
                }

                read += count;
            }

            if (read != bytes.Length || stream.ReadByte() >= 0)
            {
                return SourceReadResult.Failed(new Diagnostic(
                    "RSC0004",
                    $"Source files larger than {MaximumSourceBytes} bytes are not supported.",
                    new TextSpan(0, 0)));
            }

            ReadOnlySpan<byte> utf8Payload = bytes;
            if (utf8Payload.Length >= 3 &&
                utf8Payload[0] == 0xef &&
                utf8Payload[1] == 0xbb &&
                utf8Payload[2] == 0xbf)
            {
                utf8Payload = utf8Payload[3..];
            }

            string source = StrictUtf8.GetString(utf8Payload);
            return SourceReadResult.Succeeded(new SourceDocument(source, bytes));
        }
        catch (DecoderFallbackException exception)
        {
            return SourceReadResult.Failed(new Diagnostic(
                "RSC0005",
                $"Source file '{sourcePath}' is not valid UTF-8: {exception.Message}",
                new TextSpan(0, 0)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SourceReadResult.Failed(new Diagnostic(
                "RSC0001",
                $"Could not read source file '{sourcePath}': {exception.Message}",
                new TextSpan(0, 0)));
        }
    }

    private static Diagnostic? ValidateSourceText(string source, string sourcePath)
    {
        if (source.Length > MaximumSourceBytes)
        {
            return new Diagnostic(
                "RSC0004",
                $"Source files larger than {MaximumSourceBytes} bytes are not supported.",
                new TextSpan(0, 0));
        }

        try
        {
            if (StrictUtf8.GetByteCount(source) > MaximumSourceBytes)
            {
                return new Diagnostic(
                    "RSC0004",
                    $"Source files larger than {MaximumSourceBytes} bytes are not supported.",
                    new TextSpan(0, 0));
            }
        }
        catch (EncoderFallbackException exception)
        {
            return new Diagnostic(
                "RSC0005",
                $"Source text '{sourcePath}' is not valid UTF-8: {exception.Message}",
                new TextSpan(0, 0));
        }

        return null;
    }

    private static bool PathsCollide(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool IsValidAssemblyName(string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName) || assemblyName.Length > 128)
        {
            return false;
        }

        foreach (var character in assemblyName)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record SourceDocument(string Source, byte[] Bytes);

    private sealed record SourceReadResult(SourceDocument? Document, Diagnostic? Diagnostic)
    {
        public static SourceReadResult Succeeded(SourceDocument document) => new(document, null);

        public static SourceReadResult Failed(Diagnostic diagnostic) => new(null, diagnostic);
    }

    private sealed record PendingArtifact(
        string TargetPath,
        string StagedPath,
        ReadOnlyMemory<byte> Content);
}
