namespace RustSharp.Tests;

internal static class Program
{
    private const int MaximumTestCount = 128;

    public static async Task<int> Main(string[] args)
    {
        if (BoundedProcessTests.IsChildInvocation(args))
        {
            return await BoundedProcessTests.RunChildModeAsync(args).ConfigureAwait(false);
        }

        IReadOnlyList<TestCase> tests =
            [.. SyntaxTests.All, .. EmissionTests.All, .. NativeAotTests.All, .. BoundedProcessTests.All];
        if (tests.Count > MaximumTestCount)
        {
            Console.Error.WriteLine($"Test count {tests.Count} exceeds the safety limit {MaximumTestCount}.");
            return 2;
        }

        var failed = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.ExecuteAsync().ConfigureAwait(false);
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
            }
        }

        Console.WriteLine($"Executed {tests.Count} tests: {tests.Count - failed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
}
