using System.Diagnostics;
using System.Text.Json;

namespace RustSharp.Tests;

internal static class P2DocumentationPublicationRunner
{
    internal static async Task<int> RunAsync(string reportPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        if (!Path.IsPathFullyQualified(reportPath) || File.Exists(reportPath))
            throw new ArgumentException("New absolute report required.", nameof(reportPath));
        var tests = P2DocumentationPublicationTests.All;
        if (tests.Count != 21 || tests.Select(static test => test.Name).Distinct(StringComparer.Ordinal).Count() != 21)
            throw new InvalidOperationException("All twenty-one publication cases required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += cancel;
        var clock = Stopwatch.StartNew();
        var results = new List<(string Name, string Status, string? Error)>(21);
        int passed = 0, failed = 0, notRun = 0;
        bool stop = false;
        try
        {
            // Fixed21 /90s, cancellable5s per case. A timeout stops new dispatch.
            for (int index = 0; index < 21; index++)
            {
                if (stop || deadline.IsCancellationRequested || clock.Elapsed >= TimeSpan.FromSeconds(90))
                {
                    results.Add((tests[index].Name, "not-run", null));
                    notRun++;
                    continue;
                }
                try
                {
                    await Task.Run(tests[index].ExecuteAsync, deadline.Token)
                        .WaitAsync(TimeSpan.FromSeconds(5), deadline.Token).ConfigureAwait(false);
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
        if (results.Count != 21)
            throw new InvalidOperationException("Full component result denominator required.");
        var roots = P2DocumentationPublicationTests.ReceiptRoots;
        if (roots.Count > 21)
            throw new InvalidOperationException("Receipt root count exceeded component bound.");
        using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("taskId", "P2-08.03.atomic-output-v1");
        json.WriteString("evidenceKind", "actual-new-directory-publication-component");
        json.WriteNumber("denominator", 21);
        json.WriteNumber("executed", passed + failed);
        json.WriteNumber("passed", passed);
        json.WriteNumber("failed", failed);
        json.WriteNumber("notRun", notRun);
        json.WriteBoolean("fullLeafAccepted", false);
        json.WriteBoolean("fullP2Closure", false);
        json.WriteNumber("elapsedMilliseconds", clock.Elapsed.TotalMilliseconds);
        json.WriteStartArray("cases");
        for (int index = 0; index < 21; index++)
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
        for (int index = 0; index < roots.Count && index < 21; index++)
            json.WriteStringValue(roots[index]);
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
        return passed == 21 ? 0 : 1;
    }
}