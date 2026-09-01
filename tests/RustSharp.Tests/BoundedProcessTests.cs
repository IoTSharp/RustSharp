using System.Diagnostics;
using System.Text;
using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class BoundedProcessTests
{
    internal const string ChildSwitch = "--rustsharp-bounded-process-child";

    private const string NormalMode = "normal";
    private const string TimeoutMode = "timeout";
    private const string CancellationMode = "cancel";
    private const string StandardOutputMarker = "RSC bounded child stdout";
    private const string StandardErrorMarker = "RSC bounded child stderr";
    private const int ChildOutputCharacters = BoundedProcessRunner.MaximumOutputBytesPerStream + (256 * 1024);
    private const int ChildLifetimeSeconds = 3;
    private const int MaximumExitChecks = 40;
    private const int ExitCheckDelayMilliseconds = 50;

    private static readonly TimeSpan RunnerTerminationGracePeriod = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan NormalProcessTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TimeoutProcessTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CancellationProcessTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TestOperationTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ChildStartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupWaitTimeout = TimeSpan.FromSeconds(3);

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("bounded process captures stdout and stderr on normal exit", CapturesBothStreamsOnExitAsync),
        new("bounded process times out with bounded output and cleanup", TimesOutWithBoundedOutputAsync),
        new("bounded process reports caller cancellation and cleanup", ReportsCancellationAndCleanupAsync),
    ];

    internal static bool IsChildInvocation(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 &&
        string.Equals(arguments[0], ChildSwitch, StringComparison.Ordinal);

    internal static async Task<int> RunChildModeAsync(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 2)
        {
            Console.Error.WriteLine($"{ChildSwitch} requires exactly one mode argument.");
            return 64;
        }

        switch (arguments[1])
        {
            case NormalMode:
                Console.Out.WriteLine(StandardOutputMarker);
                Console.Out.Flush();
                Console.Error.WriteLine(StandardErrorMarker);
                Console.Error.Flush();
                return 0;

            case TimeoutMode:
                WriteLargeOutput();
                await Task.Delay(TimeSpan.FromSeconds(ChildLifetimeSeconds)).ConfigureAwait(false);
                return 0;

            case CancellationMode:
                Console.Out.WriteLine("RSC bounded child cancellation marker");
                Console.Out.Flush();
                await Task.Delay(TimeSpan.FromSeconds(ChildLifetimeSeconds)).ConfigureAwait(false);
                return 0;

            default:
                Console.Error.WriteLine($"Unknown bounded-process child mode '{arguments[1]}'.");
                return 64;
        }
    }

    private static async Task CapturesBothStreamsOnExitAsync()
    {
        BoundedProcessResult result = await RunChildAsync(
            NormalMode,
            NormalProcessTimeout,
            onStarted: null,
            CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(BoundedProcessTermination.Exited, result.Termination);
        AssertEx.True(result.Succeeded, "A zero-exit child must be reported as succeeded.");
        AssertEx.True(result.ExitCode == 0, "A normally exited child must expose exit code zero.");
        AssertEx.True(
            result.StandardOutput.Contains(StandardOutputMarker, StringComparison.Ordinal),
            "The normal child stdout marker must be captured.");
        AssertEx.True(
            result.StandardError.Contains(StandardErrorMarker, StringComparison.Ordinal),
            "The normal child stderr marker must be captured.");
        AssertEx.False(result.OutputTruncated, "Small normal output must not be truncated.");
        AssertEx.False(result.OutputReadTimedOut, "Small normal output must drain before the timeout.");
        AssertEx.False(result.ProcessTreeCleanupAttempted, "A normally exited child needs no forced cleanup.");
        AssertEx.True(result.StartedProcess.ProcessId > 0, "The started process must have a positive PID.");
        AssertEx.Equal(
            Environment.ProcessId,
            result.StartedProcess.ParentProcessId,
            "The child process must report this test process as its parent.");

        await AssertProcessExitedAsync(result.StartedProcess).ConfigureAwait(false);
    }

    private static async Task TimesOutWithBoundedOutputAsync()
    {
        BoundedProcessResult result = await RunChildAsync(
            TimeoutMode,
            TimeoutProcessTimeout,
            onStarted: null,
            CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(BoundedProcessTermination.TimedOut, result.Termination);
        AssertEx.False(result.Succeeded, "A timed-out child must not be reported as succeeded.");
        AssertEx.True(result.ProcessTreeCleanupAttempted, "Timeout must attempt owned process-tree cleanup.");
        AssertOutputIsBounded(result);
        AssertEx.True(
            result.OutputTruncated || result.OutputReadTimedOut || result.OutputReadLimitReached,
            "Large timeout output must expose a bounded-read or truncation signal.");

        await AssertProcessExitedAsync(result.StartedProcess).ConfigureAwait(false);
    }

    private static async Task ReportsCancellationAndCleanupAsync()
    {
        var startedSignal = new TaskCompletionSource<BoundedProcessStarted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        Task<BoundedProcessResult> runTask = RunChildAsync(
            CancellationMode,
            CancellationProcessTimeout,
            startedSignal.SetResult,
            cancellation.Token);

        BoundedProcessStarted started;
        try
        {
            started = await startedSignal.Task.WaitAsync(ChildStartTimeout).ConfigureAwait(false);
            cancellation.Cancel();
            BoundedProcessResult result = await runTask.WaitAsync(TestOperationTimeout).ConfigureAwait(false);

            AssertEx.Equal(BoundedProcessTermination.Cancelled, result.Termination);
            AssertEx.False(result.Succeeded, "A cancelled child must not be reported as succeeded.");
            AssertEx.True(result.ProcessTreeCleanupAttempted, "Cancellation must attempt owned process-tree cleanup.");
            AssertOutputIsBounded(result);
            await AssertProcessExitedAsync(result.StartedProcess).ConfigureAwait(false);
        }
        finally
        {
            cancellation.Cancel();
            await ObserveAndTerminateAfterFailureAsync(runTask, startedSignal).ConfigureAwait(false);
        }
    }

    private static async Task<BoundedProcessResult> RunChildAsync(
        string mode,
        TimeSpan processTimeout,
        Action<BoundedProcessStarted>? onStarted,
        CancellationToken cancellationToken)
    {
        ChildLauncher launcher = GetChildLauncher();
        var arguments = new List<string>(launcher.PrefixArguments.Count + 2);
        arguments.AddRange(launcher.PrefixArguments);
        arguments.Add(ChildSwitch);
        arguments.Add(mode);

        BoundedProcessStarted? started = null;
        var request = new BoundedProcessRequest(
            launcher.FileName,
            arguments,
            AppContext.BaseDirectory,
            processTimeout,
            metadata =>
            {
                started = metadata;
                onStarted?.Invoke(metadata);
            });

        Task<BoundedProcessResult> runTask = new BoundedProcessRunner(RunnerTerminationGracePeriod)
            .RunAsync(request, cancellationToken);
        try
        {
            return await runTask.WaitAsync(TestOperationTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (!runTask.IsCompleted && started is not null)
            {
                TryTerminateOwnedProcess(started);
            }

            await ObserveTaskBoundedAsync(runTask).ConfigureAwait(false);
        }
    }

    private static async Task ObserveAndTerminateAfterFailureAsync(
        Task<BoundedProcessResult> runTask,
        TaskCompletionSource<BoundedProcessStarted> startedSignal)
    {
        if (runTask.IsCompleted)
        {
            return;
        }

        if (startedSignal.Task.Status == TaskStatus.RanToCompletion)
        {
            TryTerminateOwnedProcess(startedSignal.Task.Result);
        }

        await ObserveTaskBoundedAsync(runTask).ConfigureAwait(false);
    }

    private static async Task ObserveTaskBoundedAsync(Task task)
    {
        try
        {
            await task.WaitAsync(CleanupWaitTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The child has already been terminated when this path is reached. Keep the test
            // cleanup wait bounded if the runner itself is still unwinding.
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected when a test is interrupted while starting the child.
        }
        catch (InvalidOperationException)
        {
            // A failed child launch is reported by the original test await.
        }
    }

    private static async Task AssertProcessExitedAsync(BoundedProcessStarted started)
    {
        for (var check = 0; check < MaximumExitChecks; check++)
        {
            if (!IsOwnedProcessAlive(started))
            {
                return;
            }

            await Task.Delay(ExitCheckDelayMilliseconds).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Child process {started.ProcessId} remained alive after the bounded cleanup wait.");
    }

    private static bool IsOwnedProcessAlive(BoundedProcessStarted started)
    {
        try
        {
            using Process process = Process.GetProcessById(started.ProcessId);
            if (process.HasExited)
            {
                return false;
            }

            return IsMatchingProcess(process, started);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void TryTerminateOwnedProcess(BoundedProcessStarted started)
    {
        if (started.ParentProcessId != Environment.ProcessId)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(started.ProcessId);
            if (process.HasExited || !IsMatchingProcess(process, started))
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit((int)CleanupWaitTimeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // The process exited between lookup and cleanup.
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state checks.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The test must not mask the original runner failure on a restricted platform.
        }
        catch (NotSupportedException)
        {
            // Process-tree termination is unavailable on this platform.
        }
    }

    private static bool IsMatchingProcess(Process process, BoundedProcessStarted started)
    {
        try
        {
            DateTimeOffset actualStart = process.StartTime.ToUniversalTime();
            TimeSpan startDifference = (actualStart - started.StartedAt).Duration();
            if (startDifference > TimeSpan.FromMinutes(1))
            {
                return false;
            }

            string expectedName = Path.GetFileNameWithoutExtension(started.FileName);
            return string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(process.ProcessName, "dotnet", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static void AssertOutputIsBounded(BoundedProcessResult result)
    {
        int standardOutputBytes = Encoding.UTF8.GetByteCount(result.StandardOutput);
        int standardErrorBytes = Encoding.UTF8.GetByteCount(result.StandardError);

        AssertEx.True(
            standardOutputBytes <= BoundedProcessRunner.MaximumOutputBytesPerStream,
            "Captured stdout must remain within the per-stream byte bound.");
        AssertEx.True(
            standardErrorBytes <= BoundedProcessRunner.MaximumOutputBytesPerStream,
            "Captured stderr must remain within the per-stream byte bound.");
        AssertEx.True(
            standardOutputBytes + standardErrorBytes <= BoundedProcessRunner.MaximumTotalOutputBytes,
            "Captured output must remain within the aggregate byte bound.");
    }

    private static void WriteLargeOutput()
    {
        string payload = new('x', ChildOutputCharacters);
        Console.Out.Write(payload);
        Console.Out.Flush();
        Console.Error.Write(payload);
        Console.Error.Flush();
    }

    private static ChildLauncher GetChildLauncher()
    {
        string assemblyName = typeof(Program).Assembly.GetName().Name ?? "RustSharp.Tests";
        string assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");

        string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(hostPath) &&
            File.Exists(hostPath) &&
            File.Exists(assemblyPath))
        {
            return new ChildLauncher(hostPath, [assemblyPath]);
        }

        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            IsDotnetHost(processPath) &&
            File.Exists(assemblyPath))
        {
            return new ChildLauncher(processPath, [assemblyPath]);
        }

        string appHostName = OperatingSystem.IsWindows() ? "RustSharp.Tests.exe" : "RustSharp.Tests";
        string appHostPath = Path.Combine(AppContext.BaseDirectory, appHostName);
        if (File.Exists(appHostPath))
        {
            return new ChildLauncher(appHostPath, []);
        }

        AssertEx.True(
            File.Exists(assemblyPath),
            $"The test assembly path must be available to launch a bounded child: '{assemblyPath}'.");
        return new ChildLauncher("dotnet", [assemblyPath]);
    }

    private static bool IsDotnetHost(string path) =>
        string.Equals(
            Path.GetFileNameWithoutExtension(path),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);

    private sealed record ChildLauncher(string FileName, IReadOnlyList<string> PrefixArguments);
}
