using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RustSharp.Compiler;

public enum BoundedProcessTermination
{
    Exited,
    TimedOut,
    Cancelled,
}

public sealed record BoundedProcessStarted(
    int ProcessId,
    int ParentProcessId,
    DateTimeOffset StartedAt,
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory)
{
    public string CommandLine => FormatCommandLine(FileName, Arguments);

    private static string FormatCommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder(Quote(fileName));

        foreach (var argument in arguments)
        {
            _ = builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string Quote(string value)
    {
        if (value.Length != 0 && !value.Any(char.IsWhiteSpace) && value.IndexOf('"') < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}

public sealed record BoundedProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    Action<BoundedProcessStarted>? OnStarted = null);

public sealed record BoundedProcessResult(
    BoundedProcessStarted StartedProcess,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    BoundedProcessTermination Termination,
    TimeSpan Elapsed)
{
    public bool Succeeded => Termination == BoundedProcessTermination.Exited && ExitCode == 0;

    // These properties are additive so existing positional construction remains source compatible.
    public bool StandardOutputTruncated { get; init; }

    public bool StandardErrorTruncated { get; init; }

    public bool OutputTruncated => StandardOutputTruncated || StandardErrorTruncated;

    public bool OutputReadTimedOut { get; init; }

    public bool OutputDrainTimedOut { get; init; }

    public bool OutputReadLimitReached { get; init; }

    public bool ProcessTreeCleanupAttempted { get; init; }

    public bool ProcessTreeCleanupIncomplete { get; init; }

    public string? OutputDiagnostic { get; init; }

    public string? ProcessTreeCleanupDiagnostic { get; init; }
}

public sealed class BoundedProcessRunner
{
    /// <summary>Maximum captured UTF-8-equivalent bytes retained for either output stream.</summary>
    public const int MaximumOutputBytesPerStream = 1 * 1024 * 1024;

    /// <summary>Maximum captured UTF-8-equivalent bytes retained across both output streams.</summary>
    public const int MaximumTotalOutputBytes = 2 * 1024 * 1024;

    private const int OutputChunkCharacters = 16 * 1024;
    private const int MaximumOutputReadChunks = 8 * 1024;
    private const int OutputCancellationWaitMilliseconds = 250;
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private static readonly TimeSpan TerminationGracePeriod = TimeSpan.FromSeconds(5);

    private readonly TimeSpan terminationGracePeriod;

    public BoundedProcessRunner(TimeSpan? terminationGracePeriod = null)
    {
        var resolvedGracePeriod = terminationGracePeriod ?? TerminationGracePeriod;
        if (resolvedGracePeriod <= TimeSpan.Zero || resolvedGracePeriod > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(terminationGracePeriod),
                resolvedGracePeriod,
                $"The termination grace period must be greater than zero and no greater than {MaximumTimeout}.");
        }

        this.terminationGracePeriod = resolvedGracePeriod;
    }

    public async Task<BoundedProcessResult> RunAsync(
        BoundedProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);

        if (request.Timeout <= TimeSpan.Zero || request.Timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Timeout,
                $"The timeout must be greater than zero and no greater than {MaximumTimeout}.");
        }

        var workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"The working directory '{workingDirectory}' does not exist.");
        }

        var arguments = Array.AsReadOnly(request.Arguments.ToArray());
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        BoundedProcessStarted? startedProcess = null;
        Task<OutputCapture>? standardOutputTask = null;
        Task<OutputCapture>? standardErrorTask = null;
        using var outputCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var outputBudget = new OutputBudget();
        var stopwatch = Stopwatch.StartNew();
        var cleanupStatus = ProcessCleanupStatus.None;
        BoundedProcessResult? result = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startedAt = DateTimeOffset.UtcNow;
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process '{request.FileName}'.");
            }

            startedProcess = new BoundedProcessStarted(
                process.Id,
                Environment.ProcessId,
                startedAt,
                request.FileName,
                arguments,
                workingDirectory);
            request.OnStarted?.Invoke(startedProcess);

            // Read both redirected streams concurrently in fixed-size blocks. A shared budget
            // prevents one stream from consuming all retained memory while the other is drained.
            standardOutputTask = CaptureOutputAsync(
                process.StandardOutput,
                OutputChannel.StandardOutput,
                outputBudget,
                outputCancellation.Token);
            standardErrorTask = CaptureOutputAsync(
                process.StandardError,
                OutputChannel.StandardError,
                outputBudget,
                outputCancellation.Token);
            // Give each individual read the same upper bound as process execution. The drain
            // phase below gets a separate, short grace period after a timeout or cancellation.
            outputCancellation.CancelAfter(request.Timeout);

            using var timeoutSource = new CancellationTokenSource(request.Timeout);
            using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

            var termination = BoundedProcessTermination.Exited;
            try
            {
                await process.WaitForExitAsync(waitSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (waitSource.IsCancellationRequested)
            {
                termination = cancellationToken.IsCancellationRequested
                    ? BoundedProcessTermination.Cancelled
                    : BoundedProcessTermination.TimedOut;

                // This is deliberately called for both timeout and caller cancellation. The
                // framework implementation uses the platform's process-tree primitive.
                cleanupStatus = MergeCleanupStatus(
                    cleanupStatus,
                    await TerminateOwnedProcessTreeAsync(
                        process,
                        termination == BoundedProcessTermination.Cancelled ? "cancellation" : "timeout",
                        descendantsMayRemain: true).ConfigureAwait(false));
            }

            var outputDrain = await DrainOutputAsync(
                standardOutputTask,
                standardErrorTask,
                outputCancellation).ConfigureAwait(false);

            if (outputDrain.DrainTimedOut ||
                outputDrain.ReadLimitReached ||
                (termination == BoundedProcessTermination.Exited && outputDrain.ReadTimedOut))
            {
                // A normally exited root can still leave inherited pipe handles in a child. Try
                // the same owned-tree operation and expose the uncertain state to the caller.
                cleanupStatus = MergeCleanupStatus(
                    cleanupStatus,
                    await TerminateOwnedProcessTreeAsync(
                        process,
                        outputDrain.ReadTimedOut ? "output drain timeout" : "output read limit",
                        descendantsMayRemain: true).ConfigureAwait(false));
            }

            int? exitCode = TryGetExitCode(process);
            var outputDiagnostic = JoinDiagnostics(
                outputDrain.StandardOutput.Diagnostic,
                outputDrain.StandardError.Diagnostic,
                outputDrain.Diagnostic);

            result = new BoundedProcessResult(
                startedProcess,
                exitCode,
                outputDrain.StandardOutput.Text,
                outputDrain.StandardError.Text,
                termination,
                stopwatch.Elapsed)
            {
                StandardOutputTruncated = outputDrain.StandardOutput.Truncated,
                StandardErrorTruncated = outputDrain.StandardError.Truncated,
                OutputReadTimedOut = outputDrain.ReadTimedOut,
                OutputDrainTimedOut = outputDrain.DrainTimedOut,
                OutputReadLimitReached = outputDrain.ReadLimitReached,
                OutputDiagnostic = outputDiagnostic,
                ProcessTreeCleanupAttempted = cleanupStatus.Attempted,
                ProcessTreeCleanupIncomplete = cleanupStatus.Incomplete,
                ProcessTreeCleanupDiagnostic = cleanupStatus.Diagnostic,
            };
        }
        finally
        {
            stopwatch.Stop();

            // Cancellation is also needed when a caller abandons a normal output drain. Keep
            // the follow-up wait bounded; disposing the process below closes any remaining pipes.
            outputCancellation.Cancel();
            if (startedProcess is not null && !HasExited(process))
            {
                cleanupStatus = MergeCleanupStatus(
                    cleanupStatus,
                    await TerminateOwnedProcessTreeAsync(
                        process,
                        "runner finalization",
                        descendantsMayRemain: true).ConfigureAwait(false));
            }

            if (standardOutputTask is not null && standardErrorTask is not null)
            {
                await ObserveOutputTasksBoundedAsync(
                    standardOutputTask,
                    standardErrorTask).ConfigureAwait(false);
            }
        }

        // Cleanup can race with process exit in the finally block, so apply its final status only
        // after that block has completed. Exceptions before result creation are allowed to flow.
        return result is null
            ? throw new InvalidOperationException("The bounded process did not produce a result.")
            : result with
            {
                ProcessTreeCleanupAttempted = cleanupStatus.Attempted,
                ProcessTreeCleanupIncomplete = cleanupStatus.Incomplete,
                ProcessTreeCleanupDiagnostic = cleanupStatus.Diagnostic,
                Elapsed = stopwatch.Elapsed,
            };
    }

    private async Task<ProcessCleanupStatus> TerminateOwnedProcessTreeAsync(
        Process process,
        string reason,
        bool descendantsMayRemain)
    {
        var processId = TryGetProcessId(process);
        var rootWasExited = HasExited(process);
        var attempted = true;
        var incomplete = false;
        var diagnostics = new List<string>(capacity: 2);

        if (rootWasExited)
        {
            diagnostics.Add(
                $"Process {processId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} had exited before {reason} cleanup; " +
                "descendant processes retaining redirected pipes cannot be confirmed from the root handle.");
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process may have exited between HasExited and Kill. This is a normal race,
            // but it means descendants cannot be proven dead from this Process instance.
            diagnostics.Add("The process was no longer associated with a live root when tree termination was requested.");
            incomplete = descendantsMayRemain;
        }
        catch (PlatformNotSupportedException)
        {
            diagnostics.Add($"Process-tree termination is not supported on {GetPlatformName()}.");
            incomplete = true;
        }
        catch (NotSupportedException)
        {
            diagnostics.Add($"Process-tree termination is unavailable on {GetPlatformName()}.");
            incomplete = true;
        }
        catch (UnauthorizedAccessException)
        {
            diagnostics.Add("The operating system denied process-tree termination.");
            incomplete = true;
        }
        catch (Win32Exception exception)
        {
            diagnostics.Add($"The operating system rejected process-tree termination: {TrimDiagnostic(exception.Message)}");
            incomplete = true;
        }

        if (!rootWasExited)
        {
            using var terminationSource = new CancellationTokenSource(terminationGracePeriod);
            try
            {
                await process.WaitForExitAsync(terminationSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (terminationSource.IsCancellationRequested)
            {
                diagnostics.Add(
                    $"Process {processId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} did not exit within the {terminationGracePeriod} termination grace period.");
                incomplete = true;
            }
            catch (InvalidOperationException)
            {
                // It exited during the race between Kill and WaitForExitAsync.
            }
        }

        if (!rootWasExited && !HasExited(process))
        {
            incomplete = true;
        }

        return new ProcessCleanupStatus(
            attempted,
            incomplete || (rootWasExited && descendantsMayRemain),
            JoinDiagnostics(diagnostics.ToArray()));
    }

    private async Task<OutputDrainResult> DrainOutputAsync(
        Task<OutputCapture> standardOutputTask,
        Task<OutputCapture> standardErrorTask,
        CancellationTokenSource outputCancellation)
    {
        var allOutput = Task.WhenAll(standardOutputTask, standardErrorTask);
        using var drainSource = new CancellationTokenSource(terminationGracePeriod);
        var timedOut = false;

        try
        {
            await allOutput.WaitAsync(drainSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (drainSource.IsCancellationRequested)
        {
            timedOut = true;
            outputCancellation.Cancel();
            await ObserveOutputTasksBoundedAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
        }

        if (allOutput.Status == TaskStatus.RanToCompletion)
        {
            var captures = allOutput.Result;
            return new OutputDrainResult(
                captures[0],
                captures[1],
                timedOut || captures[0].ReadTimedOut || captures[1].ReadTimedOut,
                captures[0].ReadLimitReached || captures[1].ReadLimitReached,
                timedOut,
                null);
        }

        var standardOutput = GetIncompleteCapture(standardOutputTask, OutputChannel.StandardOutput);
        var standardError = GetIncompleteCapture(standardErrorTask, OutputChannel.StandardError);
        return new OutputDrainResult(
            standardOutput,
            standardError,
            timedOut || standardOutput.ReadTimedOut || standardError.ReadTimedOut,
            standardOutput.ReadLimitReached || standardError.ReadLimitReached,
            timedOut,
            "One or more redirected output readers did not complete within the drain bound.");
    }

    private async Task ObserveOutputTasksBoundedAsync(
        Task<OutputCapture> standardOutputTask,
        Task<OutputCapture> standardErrorTask)
    {
        var allOutput = Task.WhenAll(standardOutputTask, standardErrorTask);
        var waitMilliseconds = Math.Min(
            OutputCancellationWaitMilliseconds,
            Math.Max(1, (int)Math.Min(int.MaxValue, terminationGracePeriod.TotalMilliseconds)));

        try
        {
            await allOutput.WaitAsync(TimeSpan.FromMilliseconds(waitMilliseconds)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Process disposal closes the streams after this method returns. The reader tasks
            // only retain their bounded StringBuilder and are intentionally not left awaited.
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected during timeout/finalization.
        }
        catch (IOException)
        {
            // CaptureOutputAsync normally converts I/O failures into diagnostics; this catch
            // protects cleanup if a platform stream reports one outside that path.
        }
        catch (ObjectDisposedException)
        {
            // The process may close a redirected stream while the bounded wait is running.
        }
    }

    private static async Task<OutputCapture> CaptureOutputAsync(
        StreamReader reader,
        OutputChannel channel,
        OutputBudget budget,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(capacity: Math.Min(MaximumOutputBytesPerStream, OutputChunkCharacters));
        var buffer = new char[OutputChunkCharacters];
        var truncated = false;

        for (var chunkIndex = 0; chunkIndex < MaximumOutputReadChunks; chunkIndex++)
        {
            int charsRead;
            try
            {
                charsRead = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new OutputCapture(
                    builder.ToString(),
                    truncated,
                    ReadTimedOut: true,
                    ReadLimitReached: false,
                    "The redirected output reader was cancelled before EOF.");
            }
            catch (IOException exception)
            {
                return new OutputCapture(
                    builder.ToString(),
                    truncated,
                    ReadTimedOut: false,
                    ReadLimitReached: false,
                    $"{channel} reader failed: {TrimDiagnostic(exception.Message)}");
            }
            catch (ObjectDisposedException)
            {
                return new OutputCapture(
                    builder.ToString(),
                    truncated,
                    ReadTimedOut: true,
                    ReadLimitReached: false,
                    $"{channel} reader was disposed before EOF.");
            }

            if (charsRead == 0)
            {
                return new OutputCapture(builder.ToString(), truncated, false, false, null);
            }

            var capture = budget.Capture(channel, reader.CurrentEncoding, buffer.AsSpan(0, charsRead));
            if (capture.CapturedCharacters != charsRead)
            {
                truncated = true;
            }

            if (capture.CapturedCharacters != 0)
            {
                _ = builder.Append(buffer.AsSpan(0, capture.CapturedCharacters));
            }
        }

        return new OutputCapture(
            builder.ToString(),
            truncated,
            ReadTimedOut: false,
            ReadLimitReached: true,
            $"{channel} reader reached the bounded read-chunk limit before EOF.");
    }

    private static OutputCapture GetIncompleteCapture(
        Task<OutputCapture> task,
        OutputChannel channel)
    {
        if (task.Status == TaskStatus.RanToCompletion)
        {
            return task.Result;
        }

        var diagnostic = task.IsFaulted && task.Exception is not null
            ? $"{channel} reader failed: {TrimDiagnostic(task.Exception.GetBaseException().Message)}"
            : $"{channel} reader did not complete before the bounded drain ended.";
        return new OutputCapture(string.Empty, true, ReadTimedOut: true, ReadLimitReached: false, diagnostic);
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static int? TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    private static string GetPlatformName() =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsLinux() ? "Linux" :
        OperatingSystem.IsMacOS() ? "macOS" :
        "the current platform";

    private static ProcessCleanupStatus MergeCleanupStatus(
        ProcessCleanupStatus first,
        ProcessCleanupStatus second) =>
        new(
            first.Attempted || second.Attempted,
            first.Incomplete || second.Incomplete,
            JoinDiagnostics(first.Diagnostic, second.Diagnostic));

    private static string? JoinDiagnostics(params string?[] diagnostics)
    {
        var nonEmpty = diagnostics
            .Where(static diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
            .Select(static diagnostic => TrimDiagnostic(diagnostic!))
            .ToArray();
        return nonEmpty.Length == 0 ? null : string.Join("; ", nonEmpty);
    }

    private static string TrimDiagnostic(string value)
    {
        const int maximumDiagnosticCharacters = 512;
        return value.Length <= maximumDiagnosticCharacters
            ? value
            : value[..maximumDiagnosticCharacters] + "...";
    }

    private enum OutputChannel
    {
        StandardOutput,
        StandardError,
    }

    private readonly record struct OutputSlice(int CapturedCharacters);

    private sealed class OutputBudget
    {
        private readonly object gate = new();
        private int totalBytes;
        private int standardOutputBytes;
        private int standardErrorBytes;

        public OutputSlice Capture(OutputChannel channel, Encoding encoding, ReadOnlySpan<char> characters)
        {
            lock (gate)
            {
                var streamBytes = channel == OutputChannel.StandardOutput
                    ? standardOutputBytes
                    : standardErrorBytes;
                var availableBytes = Math.Min(
                    MaximumOutputBytesPerStream - streamBytes,
                    MaximumTotalOutputBytes - totalBytes);
                if (availableBytes <= 0)
                {
                    return new OutputSlice(0);
                }

                var capturedCharacters = GetPrefixCharacterCount(
                    encoding,
                    characters,
                    availableBytes,
                    out var capturedBytes);
                if (capturedCharacters == 0)
                {
                    return new OutputSlice(0);
                }

                if (channel == OutputChannel.StandardOutput)
                {
                    standardOutputBytes += capturedBytes;
                }
                else
                {
                    standardErrorBytes += capturedBytes;
                }

                totalBytes += capturedBytes;
                return new OutputSlice(capturedCharacters);
            }
        }

        private static int GetPrefixCharacterCount(
            Encoding encoding,
            ReadOnlySpan<char> characters,
            int availableBytes,
            out int capturedBytes)
        {
            capturedBytes = 0;
            var capturedCharacters = 0;
            for (var index = 0; index < characters.Length; index++)
            {
                var characterCount = index + 1 < characters.Length &&
                                     char.IsHighSurrogate(characters[index]) &&
                                     char.IsLowSurrogate(characters[index + 1])
                    ? 2
                    : 1;
                var characterBytes = encoding.GetByteCount(characters.Slice(index, characterCount));
                if (characterBytes > availableBytes - capturedBytes)
                {
                    break;
                }

                capturedBytes += characterBytes;
                capturedCharacters += characterCount;
                index += characterCount - 1;
            }

            return capturedCharacters;
        }
    }

    private sealed record OutputCapture(
        string Text,
        bool Truncated,
        bool ReadTimedOut,
        bool ReadLimitReached,
        string? Diagnostic);

    private sealed record OutputDrainResult(
        OutputCapture StandardOutput,
        OutputCapture StandardError,
        bool ReadTimedOut,
        bool ReadLimitReached,
        bool DrainTimedOut,
        string? Diagnostic);

    private readonly record struct ProcessCleanupStatus(
        bool Attempted,
        bool Incomplete,
        string? Diagnostic)
    {
        public static ProcessCleanupStatus None => new(false, false, null);
    }
}
