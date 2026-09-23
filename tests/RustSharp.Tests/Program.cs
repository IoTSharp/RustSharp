namespace RustSharp.Tests;

internal static class Program
{
    private const int MaximumTestCount = 512;

    public static async Task<int> Main(string[] args)
    {
        if (BoundedProcessTests.IsChildInvocation(args))
        {
            return await BoundedProcessTests.RunChildModeAsync(args).ConfigureAwait(false);
        }

        IReadOnlyList<TestCase> tests =
            [.. SyntaxTests.All, .. LexerTests.All, .. LexerClosureTests.All, .. LexingManifestTests.All, .. SafeCoreSyntaxTests.All, .. SafeCoreTypeHirTests.All, .. SafeCoreTypeInferenceTests.All, .. SafeCoreTypeProfileTests.All, .. SafeCoreTypeAnalysisTests.All, .. SafeCoreTypeConformanceTests.All, .. SafeCoreRegressionTests.All, .. SafeCoreOwnershipTests.All, .. SafeCoreMirOwnershipAdapterTests.All, .. RustSharpMetadataTests.All, .. SyntaxGrammarTests.All, .. SyntaxModuleExpansionTests.All, .. SyntaxItemExpansionTests.All, .. SyntaxExpressionExpansionTests.All, .. SyntaxProfileBoundaryTests.All, .. SemanticAstBoundaryTests.All, .. SyntaxManifestTests.All, .. NameResolutionManifestTests.All, .. SafeCoreNameResolutionTests.All, .. SafeCoreModuleResolutionTests.All, .. SafeCoreHirTests.All, .. SafeCoreCompilationTests.All, .. SafeCoreWorkspaceTests.All, .. CargoWorkspaceTests.All, .. SafeCoreModuleCompilationTests.All, .. WorkspaceSourceMapTests.All, .. EmissionTests.All, .. NativeAotTests.All, .. BoundedProcessTests.All, .. ClrLirTests.All, .. VerticalProofTests.All, .. OwnershipTests.All];
        tests = [.. tests, .. P1ExitGateTests.All];
        tests = [.. tests, .. SafeCoreAdvancedTypeHirTests.All, .. SafeCorePatternClosureTests.All, .. SafeCoreConstantTests.All];
        tests = [.. tests, .. SafeCoreMirPatternExecutionTests.All];
        tests = [.. tests, .. GenericFoundationTests.All];
        tests = [.. tests, .. SafeCoreGenericAnalysisTests.All, .. SafeCoreGenericProfileTests.All, .. SafeCoreGenericConformanceTests.All];
        tests = [.. tests, .. SafeCoreGenericAggregateTests.All];
        tests = [.. tests, .. ClrLirValueTypeTests.All];
        tests = [.. tests, .. SafeCoreGenericCompilationTests.All];
        tests = [.. tests, .. SafeCoreGenericPackageTests.All];
        tests = [.. tests, .. SafeCoreGenericHirBindingTests.All];
        tests = [.. tests, .. SafeCoreMirValidationTests.All, .. SafeCoreMirLoweringTests.All, .. SafeCoreMirCleanupTests.All, .. SafeCoreMirV2ProfileTests.All];
        tests = [.. tests, .. SafeCoreMirReferenceExecutionTests.All];
        tests = [.. tests, .. P1DifferentialProfileTests.All];
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
