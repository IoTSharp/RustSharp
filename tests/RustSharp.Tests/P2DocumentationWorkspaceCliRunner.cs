using System.Diagnostics;
using System.Text.Json;

namespace RustSharp.Tests;

internal static class P2DocumentationWorkspaceCliRunner
{
    internal static async Task<int> RunAsync(string reportPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        if (!Path.IsPathFullyQualified(reportPath) || File.Exists(reportPath))
            throw new ArgumentException("New absolute report required.", nameof(reportPath));
        var tests = P2DocumentationWorkspaceCliTests.All;
        if (tests.Count != 8 || tests.Select(static test => test.Name).Distinct(StringComparer.Ordinal).Count() != 8)
            throw new InvalidOperationException("All eight workspace CLI process cases required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(180));
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += cancel;
        var clock = Stopwatch.StartNew();
        var results = new List<(string Name, string Status, string? Error)>(8);
        int passed = 0, failed = 0, notRun = 0;
        bool stop = false;
        try
        {
            // Fixed8 /180s, cancellable20s per case. Cancellation is passed to the actual process runner and its full cleanup is awaited before any next dispatch.
            for (int index = 0; index < 8; index++)
            {
                if (stop || deadline.IsCancellationRequested || clock.Elapsed >= TimeSpan.FromSeconds(180))
                {
                    results.Add((tests[index].Name, "not-run", null));
                    notRun++;
                    continue;
                }
                try
                {
                    using var perCase=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    perCase.CancelAfter(TimeSpan.FromSeconds(20));
                    await P2DocumentationWorkspaceCliTests.RunCaseAsync(index,perCase.Token).ConfigureAwait(false);
                    results.Add((tests[index].Name, "passed", null));
                    passed++;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    results.Add((tests[index].Name, "failed", exception.ToString()));
                    failed++;
                    stop = exception is TimeoutException or OperationCanceledException;
                }
            }
        }
        finally { Console.CancelKeyPress -= cancel; }
        if (results.Count != 8)
            throw new InvalidOperationException("Full component result denominator required.");
        var roots = P2DocumentationWorkspaceCliTests.ReceiptRoots;
        if (roots.Count > 8)
            throw new InvalidOperationException("Receipt root count exceeded component bound.");
        using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("taskId", "P2-08.03.workspace-cli-v1");
        json.WriteString("evidenceKind", "actual-workspace-documentation-eight-child-process-component");
        json.WriteNumber("denominator", 8);
        json.WriteNumber("executed", passed + failed);
        json.WriteNumber("passed", passed);
        json.WriteNumber("failed", failed);
        json.WriteNumber("notRun", notRun);
        json.WriteBoolean("fullLeafAccepted", false);
        json.WriteBoolean("fullP2Closure", false);
        json.WriteNumber("elapsedMilliseconds", clock.Elapsed.TotalMilliseconds);
        json.WriteStartArray("cases");
        for (int index = 0; index < 8; index++)
        {
            var result = results[index];
            json.WriteStartObject();
            json.WriteString("name", result.Name);
            json.WriteString("status", result.Status);
            if (result.Error is not null) json.WriteString("error", result.Error);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("receiptRoots");
        for (int index = 0; index < roots.Count && index < 8; index++)
            json.WriteStringValue(roots[index]);
        json.WriteEndArray();
        json.WriteStartArray("processes");
        var processes=P2DocumentationWorkspaceCliTests.Processes;if(processes.Count>8)throw new InvalidOperationException("Process receipt count exceeded eight.");
        for(int index=0;index<processes.Count&&index<8;index++){var process=processes[index];json.WriteStartObject();json.WriteNumber("pid",process.StartedProcess.ProcessId);json.WriteNumber("parentPid",process.StartedProcess.ParentProcessId);json.WriteString("startedAt",process.StartedProcess.StartedAt);json.WriteString("commandLine",process.StartedProcess.CommandLine);json.WriteString("workingDirectory",process.StartedProcess.WorkingDirectory);if(process.ExitCode is int exit)json.WriteNumber("exitCode",exit);else json.WriteNull("exitCode");json.WriteString("termination",process.Termination.ToString());json.WriteBoolean("cleanupAttempted",process.ProcessTreeCleanupAttempted);json.WriteBoolean("cleanupIncomplete",process.ProcessTreeCleanupIncomplete);json.WriteString("stdout",process.StandardOutput);json.WriteString("stderr",process.StandardError);json.WriteEndObject();}json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
        return passed == 8 ? 0 : 1;
    }
}