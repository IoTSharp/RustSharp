namespace RustSharp.Tests;

internal static class P1HarnessEvidenceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 harness rejects filtered full-suite closure", RejectsFilteredClosureAsync),
        new("P1 harness rejects reduced denominator and incomplete execution", RejectsReducedExecutionAsync),
        new("P1 harness rejects stale candidate and non-Release closure", RejectsStaleCandidateAsync),
        new("P1 harness bounds option parsing and requires candidate provenance", BoundsOptionsAsync),
        new("P1 harness list emits actual registration and assembly identity without executing cases", ListsWithoutExecutionAsync),
    ];

    private static Task RejectsFilteredClosureAsync()
    {
        var complete = new RegressionHarness.Summary(964, 964, 964, 964, 0, 0, 0);
        AssertEx.True(RegressionHarness.IsFullSuccess(true, complete, true, true, null), "Complete fixed Release inventory must close.");
        AssertEx.False(RegressionHarness.IsFullSuccess(false, complete, true, true, null), "A filter must never close the full inventory.");
        return Task.CompletedTask;
    }

    private static Task RejectsReducedExecutionAsync()
    {
        AssertEx.False(RegressionHarness.IsFullSuccess(true, new(463, 463, 463, 463, 0, 0, 0), true, true, null), "The historical minimum cannot shrink.");
        AssertEx.False(RegressionHarness.IsFullSuccess(true, new(964, 963, 963, 963, 0, 0, 0), true, true, null), "A selected subset cannot close.");
        AssertEx.False(RegressionHarness.IsFullSuccess(true, new(964, 964, 963, 963, 0, 0, 1), true, true, null), "Unexecuted tests cannot close.");
        AssertEx.False(RegressionHarness.IsFullSuccess(true, new(964, 964, 964, 963, 1, 0, 0), true, true, null), "Failed tests cannot close.");
        AssertEx.False(RegressionHarness.IsFullSuccess(true, new(964, 964, 964, 964, 0, 1, 0), true, true, null), "Skipped tests cannot close.");
        return Task.CompletedTask;
    }

    private static Task RejectsStaleCandidateAsync()
    {
        var complete = new RegressionHarness.Summary(964, 964, 964, 964, 0, 0, 0);
        AssertEx.False(RegressionHarness.IsFullSuccess(true, complete, false, true, null), "Source must match candidate.");
        AssertEx.False(RegressionHarness.IsFullSuccess(true, complete, true, false, null), "Debug is not Release evidence.");
        AssertEx.False(RegressionHarness.IsFullSuccess(true, complete, true, true, "deadline expired"), "A deadline failure cannot close.");
        return Task.CompletedTask;
    }

    private static Task BoundsOptionsAsync()
    {
        string sha = new('a', 40);
        RegressionHarness.Options parsed = RegressionHarness.ParseOptions(["--filter", "P1 harness", "--report", "report.json", "--candidate-sha", sha, "--timeout", "1", "--deadline", "1"]);
        AssertEx.Equal(sha, parsed.CandidateSha!);
        AssertEx.Equal(1, parsed.TimeoutSeconds);
        AssertEx.Throws<ArgumentException>(() => RegressionHarness.ParseOptions(["--report", "report.json"]));
        AssertEx.Throws<ArgumentException>(() => RegressionHarness.ParseOptions(["--timeout", "0"]));
        AssertEx.Throws<ArgumentException>(() => RegressionHarness.ParseOptions(["--deadline", "1801"]));
        AssertEx.Throws<ArgumentException>(() => RegressionHarness.ParseOptions(["--filter", "x", "--filter", "y"]));
        AssertEx.True(RegressionHarness.InventoryHash(["a", "b"]) != RegressionHarness.InventoryHash(["b", "a"]), "Registration identity includes exact ordering.");
        string[] duplicateIds = RegressionHarness.RegistrationIds([new("legacy", () => Task.CompletedTask), new("unique", () => Task.CompletedTask), new("legacy", () => Task.CompletedTask)]);
        AssertEx.Equal(3, duplicateIds.Distinct(StringComparer.Ordinal).Count());
        AssertEx.Equal("unique", duplicateIds[1]);
        AssertEx.True(duplicateIds[0] != duplicateIds[2], "Repeated historical display names must retain separate registered executions.");
        return Task.CompletedTask;
    }

    private static async Task ListsWithoutExecutionAsync()
    {
        int executions = 0;
        TestCase[] registered = [new("first", () => { executions++; throw new InvalidOperationException("Must not execute."); }),
            new("second", () => { executions++; return Task.CompletedTask; })];
        TextWriter previous = Console.Out;
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        int exitCode;
        try
        {
            Console.SetOut(output);
            exitCode = await RegressionHarness.RunAsync(registered, ["--list"]).ConfigureAwait(false);
        }
        finally { Console.SetOut(previous); }
        AssertEx.Equal(0, exitCode);
        AssertEx.Equal(0, executions, "Listing the candidate inventory must never execute a registered case.");
        var inventory = System.Text.Json.Nodes.JsonNode.Parse(output.ToString())!.AsObject();
        AssertEx.Equal("p1-regression-registration-inventory", inventory["evidenceKind"]!.GetValue<string>());
        AssertEx.Equal(2, inventory["registeredDenominator"]!.GetValue<int>());
        AssertEx.Equal("first", inventory["registeredIds"]![0]!.GetValue<string>());
        AssertEx.Equal("second", inventory["registeredIds"]![1]!.GetValue<string>());
        AssertEx.Equal(RegressionHarness.InventoryHash(["first", "second"]), inventory["registeredIdsSha256"]!.GetValue<string>());
        AssertEx.Equal(RegressionHarness.CreateInventory(registered).AssemblySha256, inventory["assemblySha256"]!.GetValue<string>());
        AssertEx.Throws<ArgumentException>(() => RegressionHarness.ParseOptions(["--list", "unexpected"]));
    }
}
