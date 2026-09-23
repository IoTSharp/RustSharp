using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirCleanupTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("typed MIR cleanup lowers source return scope exit", SourceReturnAsync),
        new("typed MIR cleanup preserves unwind and abort boundaries", PanicStrategiesAsync),
        new("typed MIR cleanup rejects incomplete ownership evidence", RejectsIncompleteEvidenceAsync),
        new("typed MIR cleanup snapshot is deterministic and bounded", DeterministicSnapshotAsync),
        new("typed MIR cleanup snapshot round-trips through metadata", MetadataSnapshotAsync),
        new("compiler source wiring persists MIR ownership and cleanup evidence", CompilerSourceWiringAsync),
        new("compiler MIR backend executes fixed-array indexing", MirBackendArrayAsync),
        new("compiler MIR backend executes direct calls and inclusive comparisons", MirBackendCallAsync),
        new("compiler MIR check shares backend capability diagnostics", BackendCapabilityGateAsync),
    ];

    private static Task SourceReturnAsync()
    {
        SafeCoreMirPipelineResult pipeline = SafeCoreMirPipeline.Analyze(
            "fn main() {}",
            "cleanup-source.rs",
            new SafeCoreMirPipelineOptions
            {
                RequireOwnershipEvidence = true,
                RequireCleanupEvidence = true,
                Timeout = TimeSpan.FromSeconds(10),
            });
        AssertEx.True(pipeline.IsSuccessful,
            "The source-to-ownership pipeline must succeed: " +
            string.Join(Environment.NewLine, pipeline.Diagnostics));
        SafeCoreMirProgram mir = pipeline.Mir!.Program ??
            throw new InvalidOperationException("The pipeline did not publish typed MIR.");
        SafeCoreMirOwnershipResult ownership = pipeline.Ownership ??
            throw new InvalidOperationException("The pipeline did not publish ownership evidence.");

        SafeCoreMirCleanupResult cleanup = SafeCoreMirCleanupLowering.Lower(mir, ownership);
        AssertEx.True(cleanup.IsSuccessful,
            string.Join(Environment.NewLine, cleanup.Diagnostics));
        string snapshot = AssertEx.NotNull(cleanup.Snapshot,
            "Successful cleanup lowering must retain a deterministic snapshot.");
        AssertEx.True(snapshot.StartsWith("safe-core-mir-cleanup-p1-v1\n", StringComparison.Ordinal),
            "Cleanup snapshot must carry its version header.");
        AssertEx.True(snapshot.Contains("scope_exit", StringComparison.Ordinal),
            "Return cleanup must retain explicit scope-exit evidence.");
        AssertEx.True(snapshot.Contains("return_boundary", StringComparison.Ordinal),
            "Return cleanup must retain the function boundary event.");
        AssertEx.Equal(SafeCoreMirCleanupExitKind.Returned,
            cleanup.Functions.Single().Paths.Single().ExitKind);
        return Task.CompletedTask;
    }

    private static Task PanicStrategiesAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult unwind) = BuildPanicEvidence(
            SafeCorePanicStrategy.Unwind);
        SafeCoreMirCleanupResult unwound = SafeCoreMirCleanupLowering.Lower(mir, unwind);
        AssertEx.True(unwound.IsSuccessful, string.Join(Environment.NewLine, unwound.Diagnostics));
        SafeCoreMirCleanupPath unwindPath = unwound.Functions.Single().Paths.Single();
        AssertEx.Equal(SafeCoreMirCleanupExitKind.Unwound, unwindPath.ExitKind);
        AssertEx.True(unwindPath.Actions.Any(action =>
                action.Kind == SafeCoreMirCleanupActionKind.Drop && action.LocalName == "resource"),
            "Unwind must lower the live Drop value.");
        AssertEx.True(unwindPath.Actions.Any(action =>
                action.Kind == SafeCoreMirCleanupActionKind.PanicBoundary),
            "Unwind must retain an explicit panic boundary.");
        AssertEx.True(unwindPath.Actions.Any(action =>
                action.Kind == SafeCoreMirCleanupActionKind.ScopeExit),
            "Unwind must retain scope-exit cleanup evidence.");

        (mir, SafeCoreMirOwnershipResult abort) = BuildPanicEvidence(SafeCorePanicStrategy.Abort);
        SafeCoreMirCleanupResult aborted = SafeCoreMirCleanupLowering.Lower(mir, abort);
        AssertEx.True(aborted.IsSuccessful, string.Join(Environment.NewLine, aborted.Diagnostics));
        SafeCoreMirCleanupPath abortPath = aborted.Functions.Single().Paths.Single();
        AssertEx.Equal(SafeCoreMirCleanupExitKind.Aborted, abortPath.ExitKind);
        AssertEx.False(abortPath.Actions.Any(action => action.Kind == SafeCoreMirCleanupActionKind.Drop),
            "Abort must not lower unwind Drop actions.");
        AssertEx.False(abortPath.Actions.Any(action => action.Kind == SafeCoreMirCleanupActionKind.ScopeExit),
            "Abort must leave the cleanup scope untouched for the host policy.");
        AssertEx.True(abortPath.Actions.Any(action =>
                action.Kind == SafeCoreMirCleanupActionKind.PanicBoundary),
            "Abort must retain an explicit panic boundary.");
        return Task.CompletedTask;
    }

    private static Task RejectsIncompleteEvidenceAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership) = BuildPanicEvidence(
            SafeCorePanicStrategy.Unwind);
        SafeCoreMirOwnershipResult incomplete = ownership with
        {
            Program = null,
            Ownership = null,
            Diagnostics = [new Diagnostic("RSM3001", "missing ownership", Source(0).Span)],
            IsTruncated = false,
        };
        SafeCoreMirCleanupResult result = SafeCoreMirCleanupLowering.Lower(mir, incomplete);
        AssertEx.False(result.IsSuccessful, "Incomplete ownership must not publish cleanup evidence.");
        AssertEx.True(result.Functions.Count == 0 && result.Snapshot is null,
            "Failed cleanup lowering must not publish a partial plan.");
        AssertEx.Equal(SafeCoreMirCleanupLowering.InvalidEvidence, result.Diagnostics.Single().Code);
        return Task.CompletedTask;
    }

    private static Task DeterministicSnapshotAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership) = BuildPanicEvidence(
            SafeCorePanicStrategy.Unwind);
        SafeCoreMirCleanupLoweringOptions options = new()
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaximumOperations = 4096,
            MaximumCharacters = 32_768,
        };
        SafeCoreMirCleanupResult first = SafeCoreMirCleanupLowering.Lower(mir, ownership, options);
        SafeCoreMirCleanupResult second = SafeCoreMirCleanupLowering.Lower(mir, ownership, options);
        AssertEx.True(first.IsSuccessful && second.IsSuccessful,
            "Bounded cleanup lowering must succeed under the declared limits.");
        AssertEx.Equal(first.Snapshot!, second.Snapshot!,
            "Cleanup snapshots must be deterministic across repeated lowering.");

        SafeCoreMirCleanupResult limited = SafeCoreMirCleanupLowering.Lower(
            mir, ownership, options with { MaximumCharacters = 32 });
        AssertEx.True(limited.IsTruncated && !limited.IsSuccessful,
            "The cleanup snapshot character limit must be observable and bounded.");
        AssertEx.True(limited.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreMirCleanupLowering.LimitReached),
            "The cleanup limit diagnostic must be machine readable.");

        SafeCoreMirValidationResult validation = SafeCoreMirValidation.Validate(mir);
        int sharedBudget = checked(validation.OperationsUsed + 1);
        SafeCoreMirCleanupResult budgetLimited = SafeCoreMirCleanupLowering.Lower(
            mir,
            ownership,
            options with { MaximumOperations = sharedBudget, MaximumCharacters = 32_768 });
        AssertEx.True(budgetLimited.IsTruncated && !budgetLimited.IsSuccessful,
            "Cleanup validation and projection must consume one shared operation budget.");
        return Task.CompletedTask;
    }

    private static Task MetadataSnapshotAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreMirOwnershipResult ownership) = BuildPanicEvidence(
            SafeCorePanicStrategy.Unwind);
        SafeCoreMirCleanupResult cleanup = SafeCoreMirCleanupLowering.Lower(mir, ownership);
        AssertEx.True(cleanup.IsSuccessful, string.Join(Environment.NewLine, cleanup.Diagnostics));
        string snapshot = AssertEx.NotNull(cleanup.Snapshot,
            "The metadata fixture requires a cleanup snapshot.");
        var document = new RustSharpMetadataDocument(
            SafeCoreMirPipeline.Profile,
            new string('A', 64),
            [new RustSharpMetadataFunction("Main", "()->()")],
            cleanupSnapshot: snapshot);
        AssertEx.True(document.Json.Contains("cleanupSnapshot", StringComparison.Ordinal),
            "The metadata document must persist the cleanup snapshot field.");
        RustSharpMetadataDocument parsed = RustSharpMetadataDocument.Parse(document.Json);
        AssertEx.Equal(snapshot, parsed.CleanupSnapshot!,
            "Cleanup evidence must survive metadata canonicalization.");
        return Task.CompletedTask;
    }

    private static Task CompilerSourceWiringAsync()
    {
        const string source = "fn main() { let value: i32 = 7; println!(\"{}\", value); }";
        string directory = Path.GetFullPath(Path.Combine(
            "artifacts", "tests", "mir-source-evidence-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "program.rs");
        string outputPath = Path.Combine(directory, "program.dll");
        try
        {
            CompilationResult compiled = CompilerDriver.Compile(
                source,
                sourcePath,
                outputPath,
                "MirSourceEvidence",
                CompilationProfile.SafeCoreMir);
            AssertEx.True(compiled.Success,
                "Compiler-integrated MIR evidence failed: " +
                string.Join("; ", compiled.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(
                outputPath,
                SafeCoreMirPipeline.Profile,
                ["Main"]);
            AssertEx.True(imported.IsSuccessful,
                "Compiler-integrated metadata import failed: " +
                string.Join("; ", imported.Diagnostics));
            RustSharpMetadataDocument document = imported.Document ??
                throw new InvalidOperationException("Compiler-integrated metadata document is missing.");
            AssertEx.True(document.MirSnapshot?.StartsWith("safe-core-mir-v1\n", StringComparison.Ordinal) == true,
                "Source compilation must persist the typed-MIR snapshot.");
            RustSharpMetadataOwnershipFunction ownership = document.Ownership.Single(fact =>
                fact.Outcomes?.Contains("Returned", StringComparer.Ordinal) == true);
            AssertEx.True(ownership.Outcomes?.Contains("Returned", StringComparer.Ordinal) == true,
                "Source compilation must persist returned ownership evidence.");
            AssertEx.True(document.CleanupSnapshot?.StartsWith(
                    "safe-core-mir-cleanup-p1-v1\n", StringComparison.Ordinal) == true,
                "Source compilation must persist cleanup evidence.");
            AssertEx.True(document.CleanupSnapshot!.Contains("scope_exit", StringComparison.Ordinal) &&
                document.CleanupSnapshot.Contains("return_boundary", StringComparison.Ordinal),
                "Cleanup evidence must include the source function return boundary.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static async Task MirBackendArrayAsync()
    {
        const string source =
            "fn main() { let values: [i32; 3] = [4, 5, 6]; println!(\"{}\", values[1]); }";
        string directory = Path.GetFullPath(Path.Combine(
            "artifacts", "tests", "mir-array-runtime-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "program.rs");
        string outputPath = Path.Combine(directory, "program.dll");
        try
        {
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            CompilationResult compiled = CompilerDriver.Compile(
                source,
                sourcePath,
                outputPath,
                "MirArrayRuntime",
                CompilationProfile.SafeCoreMir,
                compileDeadline.Token);
            AssertEx.True(compiled.Success,
                "MIR array backend compilation failed: " +
                string.Join("; ", compiled.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            using var runDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [outputPath], directory, TimeSpan.FromSeconds(10)),
                runDeadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded, "MIR array runtime failed: " + run.StandardError);
            AssertEx.False(run.ProcessTreeCleanupIncomplete,
                "MIR array runtime process cleanup must complete.");
            AssertEx.Equal("5\n",
                run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task MirBackendCallAsync()
    {
        const string source =
            "fn add(left: i32, right: i32) -> i32 { left + right } " +
            "fn le(left: i32, right: i32) -> bool { left <= right } " +
            "fn ge(left: i32, right: i32) -> bool { left >= right } " +
            "fn main() { println!(\"{}\", add(2, 3)); " +
            "println!(\"{}\", le(1, 2)); println!(\"{}\", le(2, 1)); " +
            "println!(\"{}\", ge(2, 1)); println!(\"{}\", ge(1, 2)); }";
        string directory = Path.GetFullPath(Path.Combine(
            "artifacts", "tests", "mir-call-runtime-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "program.rs");
        string outputPath = Path.Combine(directory, "program.dll");
        try
        {
            using var compileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            CompilationResult compiled = CompilerDriver.Compile(
                source,
                sourcePath,
                outputPath,
                "MirCallRuntime",
                CompilationProfile.SafeCoreMir,
                compileDeadline.Token);
            AssertEx.True(compiled.Success,
                "MIR direct-call compilation failed: " +
                string.Join("; ", compiled.Diagnostics.Select(static diagnostic => diagnostic.Message)));

            using var runDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [outputPath], directory, TimeSpan.FromSeconds(10)),
                runDeadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded, "MIR direct-call runtime failed: " + run.StandardError);
            AssertEx.False(run.ProcessTreeCleanupIncomplete,
                "MIR direct-call runtime process cleanup must complete.");
            AssertEx.Equal("5\ntrue\nfalse\ntrue\nfalse\n",
                run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static Task BackendCapabilityGateAsync()
    {
        // SafeCoreMirValidation intentionally accepts the language-level MIR
        // contract, while the executable CLR value model is narrower. `check`
        // must nevertheless run the same backend capability gate as `compile`.
        (string Source, string Code)[] unsupported =
        [
            ("fn main() { let value: i32 = 6 / 2; println!(\"{}\", value); }", SafeCoreMirClrLowering.Unsupported),
            ("fn main() { let value: i32 = true as i32; println!(\"{}\", value); }", SafeCoreMirClrLowering.Unsupported),
            ("fn main() { println!(\"{}\", 'A'); }", SafeCoreMirClrLowering.Unsupported),
            ("fn identity(value: char) -> char { value } fn main() { identity('A'); }", SafeCoreMirClrLowering.Unsupported),
        ];

        foreach ((string source, string code) in unsupported)
        {
            CompilationResult result = CompilerDriver.Check(
                source, "mir-backend-capability.rs", CompilationProfile.SafeCoreMir);
            AssertEx.False(result.Success,
                "A source outside the executable MIR backend must not pass check.");
            AssertEx.Equal(code, result.Diagnostics.Single().Code,
                $"The check diagnostic must come from the same backend capability boundary ({source}): " +
                string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        }

        CompilationResult supported = CompilerDriver.Check(
            "fn main() { let value: i32 = 6 * 2; println!(\"{}\", value); }",
            "mir-backend-capability-ok.rs", CompilationProfile.SafeCoreMir);
        AssertEx.True(supported.Success,
            "A supported MIR scalar program must remain checkable after the backend gate.");
        return Task.CompletedTask;
    }

    private static (SafeCoreMirProgram Mir, SafeCoreMirOwnershipResult Ownership) BuildPanicEvidence(
        SafeCorePanicStrategy strategy)
    {
        SafeCoreType integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
        SafeCoreMirSource source = Source(0);
        SafeCoreMirProgram mir = new([
            new SafeCoreMirFunction(
                0,
                "crate::panic_probe",
                integer,
                [new SafeCoreMirLocal(0, "resource", integer, SafeCoreMirLocalKind.Parameter, false, source)],
                [new SafeCoreMirBlock(
                    0,
                    [],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(0, integer, source), source),
                    source)],
                0,
                source),
        ]);
        SafeCoreOwnershipFunction ownershipFunction = new(
            "crate::panic_probe",
            [new SafeCoreOwnershipLocal(
                0,
                "resource",
                integer,
                SafeCoreOwnershipKind.Move,
                HasDrop: true,
                ScopeId: 0,
                IsReference: false,
                InitiallyInitialized: true,
                source)],
            [new SafeCoreOwnershipScope(0, -1, source)],
            [new SafeCoreOwnershipBlock(
                0,
                0,
                [],
                SafeCoreOwnershipTerminator.Panic(source),
                source)],
            0,
            strategy,
            source);
        SafeCoreOwnershipProgram ownershipProgram = new([ownershipFunction]);
        SafeCoreMirValidationResult validation = SafeCoreMirValidation.Validate(mir);
        SafeCoreOwnershipAnalysisResult analysis = SafeCoreOwnershipAnalysis.Analyze(ownershipProgram);
        AssertEx.True(validation.IsSuccessful && analysis.IsSuccessful,
            "Panic evidence fixture must be valid before cleanup lowering.");
        return (mir, new SafeCoreMirOwnershipResult(
            ownershipProgram,
            analysis,
            validation,
            [],
            false));
    }

    private static SafeCoreMirSource Source(int start) =>
        new("cleanup.rs", new TextSpan(start, 1), start, 64);
}
