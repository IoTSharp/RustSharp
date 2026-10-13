using System.Diagnostics;
using System.Text.Json;

namespace RustSharp.Tests;

internal static class P2DocumentationDepthRunner
{
    internal static async Task<int> RunAsync(string reportPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        if (!Path.IsPathFullyQualified(reportPath) || File.Exists(reportPath))
            throw new ArgumentException("New absolute caller-owned report path required.", nameof(reportPath));
        var tests = P2DocumentationDepthTests.All;
        if (tests.Count != 7 || tests.Select(static t => t.Name).Distinct(StringComparer.Ordinal).Count() != 7)
            throw new InvalidOperationException("Frozen documentation component requires all seven depth cases.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += cancel;
        var clock = Stopwatch.StartNew();
        var results = new List<(string Name, string Status, double Duration, string? Exception)>(7);
        bool stop = false;
        try
        {
            // Fixed7/60s, cancel and5s per case; timeout stops dispatch rather than accumulating background cases.
            for (int index = 0; index < 7; index++)
            {
                if (stop || deadline.IsCancellationRequested || clock.Elapsed >= TimeSpan.FromSeconds(60))
                { results.Add((tests[index].Name, "not-run", 0, "Component deadline/cancellation reached.")); continue; }
                var elapsed = Stopwatch.StartNew();
                try
                {
                    await Task.Run(tests[index].ExecuteAsync, deadline.Token)
                        .WaitAsync(TimeSpan.FromSeconds(5), deadline.Token).ConfigureAwait(false);
                    results.Add((tests[index].Name, "passed", elapsed.Elapsed.TotalMilliseconds, null));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    results.Add((tests[index].Name, "failed", elapsed.Elapsed.TotalMilliseconds, exception.ToString()));
                    stop = exception is TimeoutException or OperationCanceledException;
                }
            }
        }
        finally { Console.CancelKeyPress -= cancel; }
        int passed = results.Count(static r => r.Status == "passed");
        using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject(); json.WriteNumber("schemaVersion", 1);
        json.WriteString("taskId", "P2-08.03"); json.WriteString("evidenceKind", "actual-documentation-seven-depth-component");
        json.WriteNumber("denominator", 7); json.WriteNumber("toolingDenominator", 91);
        json.WriteNumber("executed", results.Count(static r => r.Status != "not-run"));
        json.WriteNumber("passed", passed); json.WriteNumber("failed", results.Count(static r => r.Status == "failed"));
        json.WriteNumber("notRun", results.Count(static r => r.Status == "not-run"));
        json.WriteBoolean("componentPassed", passed == 7); json.WriteBoolean("fullLeafAccepted", false);
        json.WriteBoolean("fullP2Closure", false); json.WriteNumber("elapsedMilliseconds", clock.Elapsed.TotalMilliseconds);
        json.WriteStartArray("cases");
        foreach (var result in results)
        {
            json.WriteStartObject(); json.WriteString("name", result.Name); json.WriteString("status", result.Status);
            json.WriteNumber("elapsedMilliseconds", result.Duration);
            if (result.Exception is null) json.WriteNull("exception"); else json.WriteString("exception", result.Exception);
            json.WriteEndObject();
        }
        json.WriteEndArray(); json.WriteEndObject(); json.Flush();
        return passed == 7 ? 0 : 1;
    }
}
