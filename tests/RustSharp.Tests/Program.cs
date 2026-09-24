namespace RustSharp.Tests;

internal static class Program
{
    private const int MaximumTestCount = 768;

    public static async Task<int> Main(string[] args)
    {
        if (BoundedProcessTests.IsChildInvocation(args))
        {
            return await BoundedProcessTests.RunChildModeAsync(args).ConfigureAwait(false);
        }

        TestCase[] tests =
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
        tests = [.. tests, .. SafeCoreRegressionV2Tests.All, .. SafeCoreRegressionV3Tests.All];
        tests = [.. tests, .. SafeCoreMirReferenceExecutionTests.All];
        tests = [.. tests, .. SafeCoreMirSliceTests.All];
        tests = [.. tests, .. SafeCoreMirPlaceTests.All];
        tests = [.. tests, .. SafeCoreMirAdtLayoutTests.All];
        tests = [.. tests, .. SafeCoreMirAdtSourceTests.All, .. SafeCoreMirProjectionBackendTests.All];
        tests = [.. tests, .. SafeCoreMirReferenceProvenanceTests.All];
        tests = [.. tests, .. SafeCoreMirConstantExecutionTests.All];
        tests = [.. tests, .. SafeCoreMirEnumTests.All, .. SafeCoreMirReferenceAbiTests.All];
        tests = [.. tests, .. SafeCoreMirCompositeLifetimeTests.All];
        tests = [.. tests, .. SafeCoreMirReferenceStorageTests.All, .. SafeCoreMirFamilyEvidenceTests.All];
        tests = [.. tests, .. SafeCoreMirScalarExecutionTests.All];
        tests = [.. tests, .. SafeCoreMirClosureCaptureTests.All];
        tests = [.. tests, .. SafeCoreMirDropCodegenTests.All];
        tests = [.. tests, .. P1DifferentialProfileTests.All];
        if (args.Length != 0)
        {
            if (args.Length != 2 || args[0] != "--filter" || args[1].Length is 0 or > 256)
            {
                Console.Error.WriteLine("Usage: RustSharp.Tests [--filter name-fragment]");
                return 2;
            }
            tests = tests.Where(test => test.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase)).ToArray();
            if (tests.Length == 0) { Console.Error.WriteLine("The test filter matched no cases."); return 2; }
        }
        if (tests.Length > MaximumTestCount)
        {
            Console.Error.WriteLine($"Test count {tests.Length} exceeds the safety limit {MaximumTestCount}.");
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
                Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
            }
        }

        Console.WriteLine($"Executed {tests.Length} tests: {tests.Length - failed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
}
