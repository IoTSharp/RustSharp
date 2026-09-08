namespace RustSharp.Tests;

internal static class Program
{
    private const int MaximumTestCount = 256;

    public static async Task<int> Main(string[] args)
    {
        if (BoundedProcessTests.IsChildInvocation(args))
        {
            return await BoundedProcessTests.RunChildModeAsync(args).ConfigureAwait(false);
        }

        IReadOnlyList<TestCase> tests =
            [.. SyntaxTests.All, .. LexerTests.All, .. LexerClosureTests.All, .. LexingManifestTests.All, .. SafeCoreSyntaxTests.All, .. SyntaxGrammarTests.All, .. SyntaxModuleExpansionTests.All, .. SyntaxItemExpansionTests.All, .. SyntaxExpressionExpansionTests.All, .. SyntaxProfileBoundaryTests.All, .. SemanticAstBoundaryTests.All, .. SyntaxManifestTests.All, .. NameResolutionManifestTests.All, .. SafeCoreNameResolutionTests.All, .. SafeCoreModuleResolutionTests.All, .. SafeCoreHirTests.All, .. SafeCoreCompilationTests.All, .. SafeCoreWorkspaceTests.All, .. CargoWorkspaceTests.All, .. SafeCoreModuleCompilationTests.All, .. WorkspaceSourceMapTests.All, .. EmissionTests.All, .. NativeAotTests.All, .. BoundedProcessTests.All, .. ClrLirTests.All, .. VerticalProofTests.All, .. OwnershipTests.All];
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
