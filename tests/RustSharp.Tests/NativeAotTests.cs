using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class NativeAotTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Native AOT result requires complete host cleanup", RequiresCompleteHostCleanupAsync),
    ];

    private static Task RequiresCompleteHostCleanupAsync()
    {
        var started = new BoundedProcessStarted(
            ProcessId: 1,
            ParentProcessId: 2,
            StartedAt: DateTimeOffset.UtcNow,
            FileName: "dotnet",
            Arguments: Array.Empty<string>(),
            WorkingDirectory: ".");
        var process = new BoundedProcessResult(
            started,
            ExitCode: 0,
            StandardOutput: string.Empty,
            StandardError: string.Empty,
            Termination: BoundedProcessTermination.Exited,
            Elapsed: TimeSpan.Zero);

        var incomplete = new NativeAotPublishResult(
            HostDirectory: "host",
            HostSourcePath: "host/Program.cs",
            HostProjectPath: "host/Host.csproj",
            PublishDirectory: "publish",
            ExpectedExecutablePath: "publish/app.exe",
            ExecutablePath: "publish/app.exe",
            ProcessResult: process)
        {
            HostCleanupAttempted = true,
            HostCleanupIncomplete = true,
            HostCleanupDiagnostic = "test cleanup failure",
        };

        AssertEx.False(
            incomplete.Succeeded,
            "A publish with incomplete temporary-host cleanup must not report success.");

        NativeAotPublishResult complete = incomplete with
        {
            HostCleanupIncomplete = false,
            HostCleanupDiagnostic = null,
        };
        AssertEx.True(
            complete.Succeeded,
            "A successful process with a produced executable and complete cleanup must succeed.");
        return Task.CompletedTask;
    }
}
