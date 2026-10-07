using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P1HarnessCapacityTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 harness capacity v2 inventories 1025 and 4096 registrations without executing fixtures", InventoryAsync),
        new("P1 harness capacity v2 rejects overflow and inflated completion", OverflowAsync),
        new("P1 harness capacity v2 accepts actual exited workers without forced termination", ExitedWorkerAsync),
        new("P1 harness capacity v2 rejects failed truncated and incompletely cleaned workers", RejectsWorkerAsync),
    ];

    private static Task InventoryAsync()
    {
        int executions = 0;
        long started = Stopwatch.GetTimestamp();
        foreach (int count in new[] { 1025, RegressionHarness.MaximumTestCount })
        {
            AssertEx.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10), "Inventory trial exceeded ten seconds.");
            // Unit fixtures exercise registration capacity only. They never claim program execution.
            TestCase[] cases = Enumerable.Range(0, count).Select(index => new TestCase("capacity-unit-" + index,
                () => { executions++; throw new InvalidOperationException("Inventory cannot execute a fixture."); })).ToArray();
            RegressionHarness.RegistrationInventory inventory = RegressionHarness.CreateInventory(cases);
            AssertEx.Equal(2, inventory.SchemaVersion);
            AssertEx.Equal(count, inventory.RegisteredDenominator);
            AssertEx.Equal(count, inventory.RegisteredIds.Distinct(StringComparer.Ordinal).Count());
            AssertEx.Equal(RegressionHarness.InventoryHash(inventory.RegisteredIds), inventory.RegisteredIdsSha256);
        }
        AssertEx.Equal(0, executions);
        AssertEx.Equal(1024, RegressionHarness.LegacyMaximumTestCount);
        return Task.CompletedTask;
    }

    private static Task OverflowAsync()
    {
        TestCase[] overflow = Enumerable.Range(0, 4097).Select(index => new TestCase("overflow-unit-" + index,
            () => Task.CompletedTask)).ToArray();
        AssertEx.Throws<ArgumentException>(() => RegressionHarness.CreateInventory(overflow));
        AssertEx.False(RegressionHarness.IsFullSuccess(true, new(4097, 4097, 4097, 4097, 0, 0, 0), true, true, null),
            "A self-declared count beyond the versioned bound cannot close.");
        return Task.CompletedTask;
    }

    private static Task ExitedWorkerAsync()
    {
        JsonObject actual = ActualExitedWorker();
        AssertEx.False(actual["processTreeCleanupAttempted"]!.GetValue<bool>(),
            "This retained real child exited normally, so no forced termination was needed.");
        ValidateWorker(actual);
        return Task.CompletedTask;
    }

    private static Task RejectsWorkerAsync()
    {
        JsonObject actual = ActualExitedWorker();
        string[] flags = ["outputTruncated", "outputReadTimedOut", "outputDrainTimedOut", "outputReadLimitReached", "processTreeCleanupIncomplete"];
        long started = Stopwatch.GetTimestamp();
        foreach (string flag in flags)
        {
            AssertEx.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10), "Worker mutation batch exceeded ten seconds.");
            JsonObject changed = actual.DeepClone().AsObject(); changed[flag] = true;
            AssertEx.Equal("Case has no successful owned worker execution record.",
                AssertEx.Throws<ArgumentException>(() => ValidateWorker(changed)).Message);
        }
        JsonObject failed = actual.DeepClone().AsObject(); failed["exitCode"] = 1;
        AssertEx.Throws<ArgumentException>(() => ValidateWorker(failed));
        return Task.CompletedTask;
    }

    private static JsonObject ActualExitedWorker()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string path = Path.Combine(root, "docs/evidence/p1/native-v2.harness.json");
        AssertEx.True(new FileInfo(path).Length <= 4 * 1024 * 1024, "Retained worker evidence exceeds its read bound.");
        return JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { MaxDepth = 32 })!["cases"]![0]!["process"]!.DeepClone().AsObject();
    }

    private static void ValidateWorker(JsonObject worker)
    {
        using JsonDocument document = JsonDocument.Parse(worker.ToJsonString(), new() { MaxDepth = 16 });
        P1GateCoverageContract.ValidateWorkerExecution(document.RootElement);
    }
}
