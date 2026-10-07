namespace RustSharp.Tests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (BoundedProcessTests.IsChildInvocation(args))
        {
            return await BoundedProcessTests.RunChildModeAsync(args).ConfigureAwait(false);
        }

        TestCase[] tests =
            [.. SyntaxTests.All, .. LexerTests.All, .. LexerClosureTests.All, .. LexingManifestTests.All, .. SafeCoreSyntaxTests.All, .. SafeCoreTypeHirTests.All, .. SafeCoreTypeInferenceTests.All, .. SafeCoreTypeProfileTests.All, .. SafeCoreTypeAnalysisTests.All, .. SafeCoreTypeConformanceTests.All, .. SafeCoreRegressionTests.All, .. SafeCoreOwnershipTests.All, .. SafeCoreMirOwnershipAdapterTests.All, .. RustSharpMetadataTests.All, .. SyntaxGrammarTests.All, .. SyntaxModuleExpansionTests.All, .. SyntaxItemExpansionTests.All, .. SyntaxExpressionExpansionTests.All, .. SyntaxProfileBoundaryTests.All, .. SemanticAstBoundaryTests.All, .. SyntaxManifestTests.All, .. NameResolutionManifestTests.All, .. SafeCoreNameResolutionTests.All, .. SafeCoreModuleResolutionTests.All, .. SafeCoreHirTests.All, .. SafeCoreCompilationTests.All, .. SafeCoreWorkspaceTests.All, .. CargoWorkspaceTests.All, .. SafeCoreModuleCompilationTests.All, .. WorkspaceSourceMapTests.All, .. EmissionTests.All, .. NativeAotTests.All, .. BoundedProcessTests.All, .. ClrLirTests.All, .. VerticalProofTests.All, .. OwnershipTests.All];
        tests = [.. tests, .. P1ExitGateTests.All];
        tests = [.. tests, .. P1CoverageProfileTests.All];
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
        tests = [.. tests, .. P1EvidenceBindingTests.All];
        tests = [.. tests, .. P1SourcePackageContractTests.All, .. P1GeneratedUnwindEvidenceTests.All];
        tests = [.. tests, .. P1ExpandedSuiteTests.All];
        tests = [.. tests, .. P1ExpandedPlatformEvidenceTests.All, .. P1ExpandedOwnershipEvidenceTests.All,
            .. P1PlatformBindingContractTests.All, .. P1NestedDropUnwindTests.All];
        tests = [.. tests, .. P1OwnershipResourceContractTests.All];
        tests = [.. tests, .. P1OwnershipDiagnosticGoldenTests.All];
        tests = [.. tests, .. P1DropFlagGenerationTests.All, .. P1GeneratedDropFlagTests.All,
            .. P1DropReceiverTests.All, .. P1AggregateDropCodegenTests.All, .. P1ControlFlowDropTests.All];
        tests = [.. tests, .. P1DropDifferentialCodegenTests.All];
        tests = [.. tests, .. P1LabelResolutionTests.All];
        tests = [.. tests, .. P1MirReferenceDropStateTests.All];
        tests = [.. tests, .. P1SourceTypeMetadataTests.All, .. P1SourcePackageExecutionTests.All, .. P1SourceOriginTests.All, .. P1ImportedAggregateTests.All,
            .. P1StructuralOwnerPackageTests.All];
        tests = [.. tests, .. P1HarnessEvidenceTests.All];
        tests = [.. tests, .. P2CargoContractTests.All];
        tests = [.. tests, .. P2ToolingContractTests.All];
        tests = [.. tests, .. P2InteropContractTests.All];
        tests = [.. tests, .. P1NativeUnwindClosureTests.All];
        tests = [.. tests, .. P1GateCoverageContractTests.All];
        tests = [.. tests, .. P1HarnessCapacityTests.All];
        tests = [.. tests, .. P1DropProfileIntegrationTests.All];
        tests = [.. tests, .. P2CargoManifestTests.All];
        tests = [.. tests, .. P1SourcePackageEvidenceTests.All];
        tests = [.. tests, .. P1BackendCoverageTests.All];
        tests = [.. tests, .. P2CargoFeatureTests.All];
        return await RegressionHarness.RunAsync(tests, args).ConfigureAwait(false);
    }
}
