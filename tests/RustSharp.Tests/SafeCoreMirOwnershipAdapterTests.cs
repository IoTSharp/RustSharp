using RustSharp.Semantics;
using RustSharp.Syntax;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class SafeCoreMirOwnershipAdapterTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("typed MIR ownership adapter materializes scalar constants", ConstantReturnAsync),
        new("typed MIR ownership adapter applies local limits per function", PerFunctionLimitAsync),
        new("typed MIR ownership adapter bounds materialized constants", MaterializedConstantLimitAsync),
        new("typed MIR ownership adapter reports use before initialization", UseBeforeInitializationAsync),
        new("typed MIR ownership adapter preserves returned parameter provenance", ParameterReferenceAsync),
        new("typed MIR ownership adapter accepts finite Copy tuple aggregates", CopyTupleAsync),
        new("typed MIR ownership adapter lowers non-Copy place reads as moves", NonCopyMoveAsync),
        new("typed MIR ownership adapter rejects a second non-Copy source use", NonCopyUseAfterMoveAsync),
        new("P1-09 typed MIR non-Copy Field retains projected move and sibling Drop", NonCopyFieldMoveAsync),
        new("P1-09 typed MIR non-Copy Field rejects a second projected move", NonCopyFieldUseAfterMoveAsync),
        new("P1-09 typed MIR non-Copy Field executes distinct resource Drop exactly once", NonCopyFieldRuntimeAsync),
        new("P1-09 aggregate parameter tail and early returns Drop only the unmoved sibling", AggregateParameterReturnsAsync),
        new("P1-09 aggregate parameter unwind Drops moved destination and sibling once", AggregateParameterUnwindAsync),
        new("P1-09 typed MIR Field moves mutable loans and preserves shared reference copies", FieldReferenceKindsAsync),
        new("ownership preserves a shared-reference self-copy loan", SelfReferenceCopyAsync),
        new("typed MIR ownership adapter preserves projected MIR places", ProjectedPlaceAsync),
        new("typed MIR ownership adapter preserves projected borrow provenance", ProjectedBorrowAsync),
        new("typed MIR ownership evidence rejects projected source drift", ProjectedEvidenceSourceDriftAsync),
        new("typed MIR ownership evidence correlates non-copy source facts", ExplicitEvidenceAsync),
        new("typed MIR ownership evidence rejects source drift", EvidenceMismatchAsync),
        new("typed MIR ownership evidence rejects extra local facts", ExtraLocalEvidenceAsync),
        new("typed MIR ownership evidence rejects missing blocks", MissingBlockEvidenceAsync),
        new("typed MIR ownership evidence rejects scalar move spoof", ScalarMoveSpoofAsync),
        new("typed MIR ownership evidence rejects scalar terminator scope and source drift", ScalarTerminatorDriftAsync),
        new("typed MIR ownership evidence carries forward its operation budget", EvidenceOperationBudgetAsync),
    ];

    private static readonly SafeCoreType Integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
    private static readonly SafeCoreType Reference = SafeCoreType.Reference(Integer, mutable: false);
    private static readonly SafeCoreMirSource Source = new("ownership-adapter.rs", new TextSpan(0, 16), 0, 16);

    private static Task ConstantReturnAsync()
    {
        SafeCoreMirProgram program = new([
            new SafeCoreMirFunction(
                0,
                "crate::constant",
                Integer,
                [],
                [new SafeCoreMirBlock(
                    0,
                    [],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Constant(Integer, "7", Source), Source),
                    Source)],
                0,
                Source),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(program);
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.True(result.Program!.Functions[0].Locals.Count == 1,
            "A scalar constant return must be represented by one explicit adapter local.");
        AssertEx.Equal(SafeCoreOwnershipOutcome.Returned, result.Ownership!.Paths.Single().Outcome);
        return Task.CompletedTask;
    }

    private static Task UseBeforeInitializationAsync()
    {
        SafeCoreMirProgram program = new([
            new SafeCoreMirFunction(
                0,
                "crate::uninitialized",
                Integer,
                [new SafeCoreMirLocal(0, "value", Integer, SafeCoreMirLocalKind.Temporary, false, Source)],
                [new SafeCoreMirBlock(
                    0,
                    [],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(0, Integer, Source), Source),
                    Source)],
                0,
                Source),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(program);
        AssertEx.False(result.IsSuccessful, "Typed MIR structural validity must not imply definite initialization.");
        AssertEx.True(result.Ownership!.Diagnostics.Any(diagnostic =>
            diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.UseAfterMove),
            "The ownership phase must report a use of an uninitialized typed-MIR local.");
        return Task.CompletedTask;
    }

    private static Task MaterializedConstantLimitAsync()
    {
        SafeCoreMirProgram program = new([
            new SafeCoreMirFunction(
                0,
                "crate::bounded_constant",
                Integer,
                [new SafeCoreMirLocal(0, "slot", Integer, SafeCoreMirLocalKind.User, false, Source)],
                [new SafeCoreMirBlock(
                    0,
                    [],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Constant(Integer, "7", Source), Source),
                    Source)],
                0,
                Source),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(
            program,
            new SafeCoreMirOwnershipOptions { MaximumLocalsPerFunction = 1 });
        AssertEx.False(result.IsSuccessful, "Synthetic locals must obey the configured arena limit.");
        AssertEx.True(result.Program is null, "A bounded adaptation failure must not publish a partial program.");
        AssertEx.True(result.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreMirOwnershipDiagnosticCodes.LimitReached),
            "The adapter must report the bounded local limit.");
        return Task.CompletedTask;
    }

    private static Task PerFunctionLimitAsync()
    {
        SafeCoreMirProgram program = new([
            ConstantFunction(0, "crate::first"),
            ConstantFunction(1, "crate::second"),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(
            program,
            new SafeCoreMirOwnershipOptions { MaximumLocalsPerFunction = 1 });
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.Equal(2, result.Program!.Functions.Count);
        AssertEx.True(result.Program.Functions.All(function => function.Locals.Count == 1),
            "The local arena limit must apply independently to each function.");
        return Task.CompletedTask;
    }

    private static SafeCoreMirFunction ConstantFunction(int id, string name) =>
        new(
            id,
            name,
            Integer,
            [],
            [new SafeCoreMirBlock(
                0,
                [],
                SafeCoreMirTerminator.Return(SafeCoreMirOperand.Constant(Integer, "7", Source), Source),
                Source)],
            0,
            Source);

    private static Task ParameterReferenceAsync()
    {
        SafeCoreMirProgram program = new([
            new SafeCoreMirFunction(
                0,
                "crate::reference",
                Reference,
                [new SafeCoreMirLocal(0, "view", Reference, SafeCoreMirLocalKind.Parameter, false, Source)],
                [new SafeCoreMirBlock(
                    0,
                    [],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(0, Reference, Source), Source),
                    Source)],
                0,
                Source),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(program);
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        SafeCoreMirReferenceProvenanceResult provenance = SafeCoreMirReferenceProvenance.Analyze(program);
        AssertEx.True(provenance.IsSuccessful, string.Join(Environment.NewLine, provenance.Diagnostics));
        AssertEx.True(provenance.Functions.Single().ReturnOrigins.Single() is { LocalId: 0, IsParameter: true },
            "A returned input reference retains its checked parameter origin.");
        return Task.CompletedTask;
    }

    private static Task CopyTupleAsync()
    {
        SafeCoreType boolean = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Bool);
        SafeCoreType tuple = SafeCoreType.Tuple([Integer, boolean]);
        SafeCoreMirProgram program = new([
            new SafeCoreMirFunction(
                0,
                "crate::copy_tuple",
                tuple,
                [
                    new SafeCoreMirLocal(0, "value", tuple, SafeCoreMirLocalKind.Parameter, false, Source),
                    new SafeCoreMirLocal(1, "copy", tuple, SafeCoreMirLocalKind.Temporary, false, Source),
                ],
                [new SafeCoreMirBlock(
                    0,
                    [new SafeCoreMirStatement(
                        1,
                        SafeCoreMirRvalue.Use(SafeCoreMirOperand.Local(0, tuple, Source), Source),
                        Source)],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(1, tuple, Source), Source),
                    Source)],
                0,
                Source),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(program);
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.Equal(SafeCoreOwnershipOutcome.Returned, result.Ownership!.Paths.Single().Outcome);
        AssertEx.True(result.Program!.Functions[0].Locals.All(local => local.Kind == SafeCoreOwnershipKind.Copy),
            "A tuple composed only of scalar Copy values must remain Copy in the ownership bridge.");
        return Task.CompletedTask;
    }

    private static Task NonCopyMoveAsync()
    {
        SafeCoreType marker = SafeCoreType.Adt("crate::Marker");
        SafeCoreMirFunction function = new(
            0,
            "crate::move_value",
            marker,
            [
                new SafeCoreMirLocal(0, "source", marker, SafeCoreMirLocalKind.Parameter, false, Source)
                {
                    IsUnitAdt = true,
                    DestructorFunctionId = 7,
                },
                new SafeCoreMirLocal(1, "destination", marker, SafeCoreMirLocalKind.Temporary, false, Source)
                {
                    IsUnitAdt = true,
                    DestructorFunctionId = 7,
                },
            ],
            [new SafeCoreMirBlock(
                0,
                [new SafeCoreMirStatement(
                    1,
                    SafeCoreMirRvalue.Use(SafeCoreMirOperand.Local(0, marker, Source), Source),
                    Source)],
                SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(1, marker, Source), Source),
                Source)],
            0,
            Source);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(new([function]));
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        SafeCoreOwnershipBlock block = result.Program!.Functions[0].Blocks[0];
        AssertEx.Equal(SafeCoreOwnershipInstructionKind.Move, block.Instructions[0].Kind);
        AssertEx.True(result.Ownership!.Paths.Single().Trace.Any(trace => trace.StartsWith("move source -> destination", StringComparison.Ordinal)),
            "A non-Copy MIR Use must become an ownership move with source and destination evidence.");
        AssertEx.False(result.Ownership.Paths.Single().Trace.Any(trace => trace == "assign destination"),
            "A move must not be represented as a copy assignment.");
        return Task.CompletedTask;
    }

    private static Task NonCopyUseAfterMoveAsync()
    {
        SafeCoreType marker = SafeCoreType.Adt("crate::Marker");
        SafeCoreMirFunction function = new(
            0,
            "crate::move_then_use",
            marker,
            [
                new SafeCoreMirLocal(0, "source", marker, SafeCoreMirLocalKind.Parameter, false, Source)
                {
                    IsUnitAdt = true,
                    DestructorFunctionId = 7,
                },
                new SafeCoreMirLocal(1, "first", marker, SafeCoreMirLocalKind.Temporary, false, Source)
                {
                    IsUnitAdt = true,
                    DestructorFunctionId = 7,
                },
                new SafeCoreMirLocal(2, "second", marker, SafeCoreMirLocalKind.Temporary, false, Source)
                {
                    IsUnitAdt = true,
                    DestructorFunctionId = 7,
                },
            ],
            [new SafeCoreMirBlock(
                0,
                [
                    new SafeCoreMirStatement(1,
                        SafeCoreMirRvalue.Use(SafeCoreMirOperand.Local(0, marker, Source), Source), Source),
                    new SafeCoreMirStatement(2,
                        SafeCoreMirRvalue.Use(SafeCoreMirOperand.Local(0, marker, Source), Source), Source),
                ],
                SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(1, marker, Source), Source),
                Source)],
            0,
            Source);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(new([function]));
        AssertEx.False(result.IsSuccessful, "A second non-Copy source use must remain a move/use-after-move error.");
        AssertEx.True(result.Diagnostics.Any(diagnostic =>
            diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.UseAfterMove),
            string.Join(Environment.NewLine, result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task SelfReferenceCopyAsync()
    {
        SafeCoreOwnershipProgram ownership = new([
            new SafeCoreOwnershipFunction(
                "crate::self-copy",
                [
                    new SafeCoreOwnershipLocal(0, "owner", Integer, SafeCoreOwnershipKind.Move,
                        HasDrop: false, ScopeId: 0, IsReference: false, InitiallyInitialized: true, Source),
                    new SafeCoreOwnershipLocal(1, "view", Reference, SafeCoreOwnershipKind.Copy,
                        HasDrop: false, ScopeId: 0, IsReference: true, InitiallyInitialized: false, Source),
                ],
                [new SafeCoreOwnershipScope(0, -1, Source)],
                [new SafeCoreOwnershipBlock(
                    0,
                    0,
                    [
                        SafeCoreOwnershipInstruction.Borrow(0, 1, mutable: false, Source),
                        SafeCoreOwnershipInstruction.AssignReference(1, 1, Source),
                    ],
                    SafeCoreOwnershipTerminator.ReturnUnit(Source),
                    Source)],
                0,
                SafeCorePanicStrategy.Unwind,
                Source),
        ]);

        SafeCoreOwnershipAnalysisResult result = SafeCoreOwnershipAnalysis.Analyze(ownership);
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.True(result.Paths.Single().Trace.Any(trace => trace.Contains("(no-op)", StringComparison.Ordinal)),
            "A shared-reference self-copy must preserve its active loan as a no-op.");
        return Task.CompletedTask;
    }

    private static Task NonCopyFieldMoveAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        (SafeCoreMirProgram original, SafeCoreMirProgram projected, string functionName, int ownerId) =
            NonCopyFieldProgram(duplicateRead: false, deadline.Token);
        SafeCoreMirOwnershipResult baseline = SafeCoreMirOwnershipAdapter.Analyze(original,
            new() { Timeout = TimeSpan.FromSeconds(5), CancellationToken = deadline.Token });
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(projected,
            new() { Timeout = TimeSpan.FromSeconds(5), CancellationToken = deadline.Token });
        AssertEx.True(baseline.IsSuccessful && result.IsSuccessful,
            string.Join(Environment.NewLine, baseline.Diagnostics.Concat(result.Diagnostics)));
        SafeCoreOwnershipInstruction[] moves = result.Program!.Functions.Single(function => function.Name == functionName)
            .Blocks.SelectMany(block => block.Instructions).Where(instruction => instruction.Kind == SafeCoreOwnershipInstructionKind.Move).ToArray();
        AssertEx.True(moves.Any(instruction => instruction.Place is { } place && place.LocalId == ownerId &&
            place.Projections.SequenceEqual([SafeCoreOwnershipProjection.TupleIndex(0)])),
            "A non-Copy Field read must move the exact tuple projection into its destination.");
        SafeCoreOwnershipPath path = result.Ownership!.Paths.Single(value => value.FunctionName == functionName);
        SafeCoreOwnershipPath baselinePath = baseline.Ownership!.Paths.Single(value => value.FunctionName == functionName);
        AssertEx.Equal(string.Join('|', baselinePath.DropOrder), string.Join('|', path.DropOrder));
        AssertEx.Equal(2, path.DropOrder.Length);
        AssertEx.True(path.DropOrder.Contains("pair.tuple[1]") && !path.DropOrder.Contains("pair.tuple[0]"),
            "The unmoved sibling must Drop once, while the moved first field belongs only to its destination.");
        SafeCoreMirCleanupResult cleanup = SafeCoreMirCleanupLowering.Lower(projected, result,
            new() { Timeout = TimeSpan.FromSeconds(5), CancellationToken = deadline.Token });
        AssertEx.True(cleanup.IsSuccessful, string.Join(Environment.NewLine, cleanup.Diagnostics));
        AssertEx.Equal(2, cleanup.Functions.Single(function => function.Name == functionName).Paths.Single().Actions.Count(
            action => action.Kind == SafeCoreMirCleanupActionKind.Drop));
        return Task.CompletedTask;
    }

    private static Task NonCopyFieldUseAfterMoveAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, projected, _, _) = NonCopyFieldProgram(duplicateRead: true, deadline.Token);
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(projected,
            new() { Timeout = TimeSpan.FromSeconds(5), CancellationToken = deadline.Token });
        AssertEx.False(result.IsSuccessful, "A second ownership-bearing Field read must not reuse the moved tuple projection.");
        AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.UseAfterMove),
            string.Join(Environment.NewLine, result.Diagnostics));
        return Task.CompletedTask;
    }

    private static (SafeCoreMirProgram Original, SafeCoreMirProgram Projected, string FunctionName, int OwnerId)
        NonCopyFieldProgram(bool duplicateRead, CancellationToken cancellationToken, bool hasPayload = false, string? sourceOverride = null)
    {
        string source = sourceOverride ?? (hasPayload ? FieldPayloadSource : "struct Marker; impl Drop for Marker { fn drop(&mut self) {} } " +
            "fn project(pair: (Marker, Marker)) { let first = pair.0; } fn main() {}");
        SafeCoreMirPipelineResult proof = SafeCoreMirPipeline.Analyze(source, "noncopy-field-adapter.rs",
            new() { EnableP1Extensions = true, RequireOwnershipEvidence = true, RequireCleanupEvidence = true,
                Timeout = TimeSpan.FromSeconds(5), CancellationToken = cancellationToken });
        AssertEx.True(proof.IsSuccessful, string.Join(Environment.NewLine, proof.Diagnostics));
        SafeCoreMirProgram original = proof.Mir!.Program!;
        AssertEx.True(original.Functions.Count <= 4, "The Field regression has a fixed function inspection budget.");
        SafeCoreMirFunction function = original.Functions.Single(value => value.Name.EndsWith("::project", StringComparison.Ordinal) ||
            value.Name.EndsWith("::project#value", StringComparison.Ordinal));
        SafeCoreMirLocal owner = function.Locals.Single(local => local.Kind == SafeCoreMirLocalKind.Parameter);
        AssertEx.True(function.Blocks.Count <= 64 && function.Blocks.All(block => block.Statements.Count <= 128),
            "The Field regression has fixed block and statement inspection budgets.");
        int replacements = 0;
        SafeCoreMirBlock[] blocks = function.Blocks.Select(block =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var statements = new List<SafeCoreMirStatement>();
            foreach (SafeCoreMirStatement statement in block.Statements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (statement.Value.Kind != SafeCoreMirRvalueKind.Use || statement.Value.Operands.Count != 1 ||
                    statement.Value.Operands[0].Place is not { } place || place.LocalId != owner.Id ||
                    !place.Projections.SequenceEqual([SafeCoreMirProjection.TupleIndex(0)]))
                {
                    statements.Add(statement);
                    continue;
                }
                replacements++;
                SafeCoreMirStatement field = statement with { Value = SafeCoreMirRvalue.Field(
                    SafeCoreMirOperand.Local(owner.Id, owner.Type, statement.Value.Operands[0].Source), 0,
                    statement.Value.Type, statement.Value.Source) };
                statements.Add(field);
                if (duplicateRead) statements.Add(field);
            }
            return new SafeCoreMirBlock(block.Id, statements, block.Terminator, block.Source, cancellationToken);
        }).ToArray();
        AssertEx.Equal(1, replacements);
        SafeCoreMirFunction changed = new(function.Id, function.Name, function.ReturnType, function.Locals, blocks,
            function.EntryBlockId, function.Source, function.IsPublic, cancellationToken)
        { ReturnsStaticReference = function.ReturnsStaticReference, IsDestructor = function.IsDestructor };
        SafeCoreMirProgram projected = new(original.Functions.Select(value => value.Id == function.Id ? changed : value).ToArray(),
            original.AdtLayouts, original.ExternalFunctions, cancellationToken)
        { ImportedStructuralTypes = original.ImportedStructuralTypes };
        return (original, projected, function.Name, owner.Id);
    }

    private const string FieldPayloadSource = "struct Marker { value: i32 } " +
        "impl Drop for Marker { fn drop(&mut self) { println!(\"{}\", self.value); } } " +
        "fn project(pair: (Marker, Marker)) { let first = pair.0; println!(\"body\"); } " +
        "fn main() { project((Marker { value: 1 }, Marker { value: 2 })); }";

    private const string ParameterResource = "struct Resource { value: i32 } " +
        "impl Drop for Resource { fn drop(&mut self) { println!(\"{}\", self.value); } } ";

    private static Task FieldReferenceKindsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        foreach (bool mutable in new[] { false, true })
        {
            deadline.Token.ThrowIfCancellationRequested();
            string source = "fn project(pair: (" + (mutable ? "&mut i32" : "&i32") + ", &i32)) { let first = pair.0; " +
                "println!(\"{}\", *first); println!(\"{}\", *pair.1); } fn main() {}";
            var (_, projected, functionName, ownerId) = NonCopyFieldProgram(false, deadline.Token, sourceOverride: source);
            SafeCoreMirOwnershipResult once = SafeCoreMirOwnershipAdapter.Analyze(projected,
                new() { Timeout = TimeSpan.FromSeconds(5), CancellationToken = deadline.Token });
            AssertEx.True(once.IsSuccessful, string.Join(Environment.NewLine, once.Diagnostics));
            SafeCoreOwnershipFunction function = once.Program!.Functions.Single(value => value.Name == functionName);
            SafeCoreOwnershipLocal slot = function.Locals.Single(local => local.StoragePlace is { } place && place.LocalId == ownerId &&
                place.Projections.SequenceEqual([SafeCoreOwnershipProjection.TupleIndex(0)]));
            AssertEx.Equal(mutable, function.Blocks.SelectMany(block => block.Instructions).Any(instruction =>
                instruction.Kind == SafeCoreOwnershipInstructionKind.Move && instruction.LocalId == slot.Id));
            var (_, repeated, _, _) = NonCopyFieldProgram(true, deadline.Token, sourceOverride: source);
            SafeCoreMirOwnershipResult twice = SafeCoreMirOwnershipAdapter.Analyze(repeated,
                new() { Timeout = TimeSpan.FromSeconds(5), CancellationToken = deadline.Token });
            if (mutable) AssertEx.True(!twice.IsSuccessful && twice.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.UseAfterMove), string.Join(Environment.NewLine, twice.Diagnostics));
            else AssertEx.True(twice.IsSuccessful, "The same shared reference Field may be copied again: " + string.Join(Environment.NewLine, twice.Diagnostics));
        }
        return Task.CompletedTask;
    }

    private static async Task NonCopyFieldRuntimeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (_, projected, _, _) = NonCopyFieldProgram(false, deadline.Token, hasPayload: true);
        SafeCoreMirOwnershipResult ownership = SafeCoreMirOwnershipAdapter.Analyze(projected,
            new() { Timeout = TimeSpan.FromSeconds(5), CancellationToken = deadline.Token });
        AssertEx.True(ownership.IsSuccessful, string.Join(Environment.NewLine, ownership.Diagnostics));
        SafeCoreClrResult lowered = SafeCoreMirClrLowering.Lower(projected, deadline.Token);
        AssertEx.True(lowered.IsSuccessful, string.Join(Environment.NewLine, lowered.Diagnostics));
        GeneratedAssembly assembly = ClrLirAssemblyEmitter.EmitProgram(lowered, "NonCopyFieldRuntime", FieldPayloadSource,
            "noncopy-field-adapter.rs", "NonCopyFieldRuntime.pdb", cancellationToken: deadline.Token);
        string directory = NewFieldDirectory();
        try
        {
            string output = Path.Combine(directory, "NonCopyFieldRuntime.dll");
            File.WriteAllBytes(output, assembly.PeImage);
            File.WriteAllText(Path.ChangeExtension(output, ".runtimeconfig.json"), assembly.RuntimeConfigJson);
            if (assembly.PdbImage is { } pdb) File.WriteAllBytes(Path.ChangeExtension(output, ".pdb"), pdb);
            if (assembly.RequiresMirRuntime) File.Copy(Path.Combine(AppContext.BaseDirectory, "RustSharp.Runtime.dll"),
                Path.Combine(directory, "RustSharp.Runtime.dll"));
            await RunFieldAssemblyAsync(output, directory, "body\n1\n2\n", false, deadline.Token).ConfigureAwait(false);
        }
        finally { DeleteFieldDirectory(directory); }
    }

    private static async Task AggregateParameterReturnsAsync()
    {
        string[] bodies = ["pair.0", "let first = pair.0; return first;"];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        foreach (string body in bodies)
        {
            deadline.Token.ThrowIfCancellationRequested();
            string source = ParameterResource + "fn project(pair: (Resource, Resource)) -> Resource { " + body + " } " +
                "fn main() { let first = project((Resource { value: 1 }, Resource { value: 2 })); println!(\"returned\"); }";
            await CompileFieldSourceAsync(source, "2\nreturned\n1\n", false, deadline.Token).ConfigureAwait(false);
        }
    }

    private static Task AggregateParameterUnwindAsync() => CompileFieldSourceAsync(ParameterResource +
        "fn project(pair: (Resource, Resource)) { let first = pair.0; let maximum = 2147483647; let failed = maximum + 1; } " +
        "fn main() { println!(\"start\"); project((Resource { value: 1 }, Resource { value: 2 })); }",
        "start\n1\n2\n", true, CancellationToken.None);

    private static async Task CompileFieldSourceAsync(string source, string expected, bool unwinds, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        string directory = NewFieldDirectory();
        try
        {
            string output = Path.Combine(directory, "ParameterDropRuntime.dll");
            CompilationResult compiled = CompilerDriver.Compile(source, Path.Combine(directory, "program.rs"), output,
                "ParameterDropRuntime", CompilationProfile.SafeCoreMirV2, deadline.Token);
            AssertEx.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
            await RunFieldAssemblyAsync(output, directory, expected, unwinds, deadline.Token).ConfigureAwait(false);
        }
        finally { DeleteFieldDirectory(directory); }
    }

    private static async Task RunFieldAssemblyAsync(string output, string directory, string expected, bool unwinds,
        CancellationToken cancellationToken)
    {
        BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(new("dotnet", [output], directory,
            TimeSpan.FromSeconds(5), started => Console.WriteLine("P1-09 parameter/Field Drop runtime PID=" + started.ProcessId +
                "; parent=" + started.ParentProcessId + "; started=" + started.StartedAt.ToString("O") + "; command=" + started.CommandLine)),
            cancellationToken).ConfigureAwait(false);
        AssertEx.True(run.Termination == BoundedProcessTermination.Exited &&
            (unwinds ? run.ExitCode is not null and not 0 : run.ExitCode == 0) &&
            !run.OutputTruncated && !run.OutputReadTimedOut && !run.ProcessTreeCleanupIncomplete, run.StandardError);
        if (unwinds) AssertEx.True(run.StandardError.Contains("OverflowException", StringComparison.Ordinal) &&
            !run.StandardError.Contains("RustSharp panic abort:", StringComparison.Ordinal), run.StandardError);
        AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static string NewFieldDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rustsharp-p1-field-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteFieldDirectory(string directory)
    {
        string resolved = Path.GetFullPath(directory);
        AssertEx.True(Path.GetDirectoryName(resolved) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
            Path.GetFileName(resolved).StartsWith("rustsharp-p1-field-drop-", StringComparison.Ordinal),
            "Cleanup must target the exact task-owned Field Drop fixture directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        AssertEx.False(Directory.Exists(resolved), "The Field Drop fixture must reclaim its task-owned directory.");
    }

    private static Task ProjectedPlaceAsync()
    {
        SafeCoreType tuple = SafeCoreType.Tuple([Integer, Integer]);
        SafeCoreMirPlace first = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.TupleIndex(0));
        SafeCoreMirProgram program = new([
            new SafeCoreMirFunction(
                0,
                "crate::projected-use",
                Integer,
                [
                    new SafeCoreMirLocal(0, "pair", tuple, SafeCoreMirLocalKind.Parameter, false, Source),
                    new SafeCoreMirLocal(1, "value", Integer, SafeCoreMirLocalKind.Temporary, false, Source),
                ],
                [new SafeCoreMirBlock(
                    0,
                    [new SafeCoreMirStatement(1,
                        SafeCoreMirRvalue.Use(SafeCoreMirOperand.PlaceValue(first, Integer, Source), Source), Source)],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(1, Integer, Source), Source), Source)],
                0,
                Source),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(program);
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        SafeCoreOwnershipInstruction use = result.Program!.Functions[0].Blocks[0].Instructions[0];
        AssertEx.Equal(SafeCoreOwnershipInstructionKind.Use, use.Kind);
        AssertEx.True(use.Place is not null && use.Place.LocalId == 0 &&
            use.Place.Projections.Single().Kind == SafeCoreOwnershipProjectionKind.TupleIndex &&
            use.Place.Projections.Single().Index == 0,
            "A projected MIR use must retain its root local and structural projection.");
        return Task.CompletedTask;
    }

    private static Task ProjectedBorrowAsync()
    {
        SafeCoreType tuple = SafeCoreType.Tuple([Integer, Integer]);
        SafeCoreType mutableReference = SafeCoreType.Reference(Integer, mutable: true);
        SafeCoreMirPlace second = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.TupleIndex(1));
        SafeCoreMirProgram program = new([
            new SafeCoreMirFunction(
                0,
                "crate::projected-borrow",
                SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Unit),
                [
                    new SafeCoreMirLocal(0, "pair", tuple, SafeCoreMirLocalKind.Parameter, true, Source),
                    new SafeCoreMirLocal(1, "view", mutableReference, SafeCoreMirLocalKind.Temporary, false, Source),
                ],
                [new SafeCoreMirBlock(
                    0,
                    [new SafeCoreMirStatement(1,
                        SafeCoreMirRvalue.Unary("&mut",
                            SafeCoreMirOperand.PlaceValue(second, Integer, Source), mutableReference, Source), Source)],
                    SafeCoreMirTerminator.Return(null, Source), Source)],
                0,
                Source),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(program);
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        SafeCoreOwnershipInstruction borrow = result.Program!.Functions[0].Blocks[0].Instructions.Single();
        AssertEx.Equal(SafeCoreOwnershipInstructionKind.Borrow, borrow.Kind);
        AssertEx.True(borrow.Place is not null && borrow.Place.Projections.Single().Index == 1 &&
            borrow.RelatedPlace is not null && borrow.RelatedPlace.IsRoot && borrow.RelatedPlace.LocalId == 1,
            "A projected borrow must retain the owner place and reference destination separately.");
        return Task.CompletedTask;
    }

    private static Task ProjectedEvidenceSourceDriftAsync()
    {
        SafeCoreType tuple = SafeCoreType.Tuple([Integer, Integer]);
        SafeCoreMirSource drifted = new("other.rs", new TextSpan(0, 16), 0, 16);
        SafeCoreMirPlace first = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.TupleIndex(0));
        SafeCoreMirProgram mir = new([
            new SafeCoreMirFunction(
                0,
                "crate::projected-evidence",
                Integer,
                [
                    new SafeCoreMirLocal(0, "pair", tuple, SafeCoreMirLocalKind.Parameter, false, Source),
                    new SafeCoreMirLocal(1, "value", Integer, SafeCoreMirLocalKind.Temporary, false, Source),
                ],
                [new SafeCoreMirBlock(
                    0,
                    [new SafeCoreMirStatement(1,
                        SafeCoreMirRvalue.Use(SafeCoreMirOperand.PlaceValue(first, Integer, Source), Source), Source)],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(1, Integer, Source), Source), Source)],
                0,
                Source),
        ]);
        SafeCoreOwnershipPlace ownershipPlace = SafeCoreOwnershipPlace.Root(0)
            .Append(SafeCoreOwnershipProjection.TupleIndex(0));
        SafeCoreOwnershipFunction ownershipFunction = new(
            "crate::projected-evidence",
            [
                new SafeCoreOwnershipLocal(0, "pair", tuple, SafeCoreOwnershipKind.Copy, false, 0, false, true, Source),
                new SafeCoreOwnershipLocal(1, "value", Integer, SafeCoreOwnershipKind.Copy, false, 0, false, false, Source),
            ],
            [new SafeCoreOwnershipScope(0, -1, Source)],
            [new SafeCoreOwnershipBlock(0, 0,
                [SafeCoreOwnershipInstruction.Use(ownershipPlace, drifted)],
                SafeCoreOwnershipTerminator.Return(1, Source), Source)],
            0,
            SafeCorePanicStrategy.Unwind,
            Source);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(
            mir, new SafeCoreOwnershipProgram([ownershipFunction]));
        AssertEx.False(result.IsSuccessful, "Projected ownership source drift must not reach analysis.");
        AssertEx.True(result.Diagnostics.Any(diagnostic =>
            diagnostic.Code == SafeCoreMirOwnershipAdapter.EvidenceMismatch),
            "Projected instruction source drift must retain the stable RSM3004 diagnostic.");
        return Task.CompletedTask;
    }

    private static Task ExplicitEvidenceAsync()
    {
        SafeCoreMirSource source = new("ownership-evidence.rs", new TextSpan(0, 1), 0, 1);
        SafeCoreType reference = SafeCoreType.Reference(Integer, mutable: false);
        SafeCoreMirProgram mir = new([
            new SafeCoreMirFunction(
                0,
                "crate::reference",
                reference,
                [new SafeCoreMirLocal(0, "view", reference, SafeCoreMirLocalKind.Parameter, false, source)],
                [new SafeCoreMirBlock(
                    0,
                    [],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(0, reference, source), source),
                    source)],
                0,
                source),
        ]);
        SafeCoreOwnershipProgram ownership = new([
            new SafeCoreOwnershipFunction(
                "crate::reference",
                [new SafeCoreOwnershipLocal(0, "view", reference, SafeCoreOwnershipKind.Copy,
                    HasDrop: false, ScopeId: 0, IsReference: true, InitiallyInitialized: true, Source: source)],
                [new SafeCoreOwnershipScope(0, -1, source)],
                [new SafeCoreOwnershipBlock(0, 0, [], SafeCoreOwnershipTerminator.Return(0, source), source)],
                0,
                SafeCorePanicStrategy.Unwind,
                source),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(mir, ownership);
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.Equal(SafeCoreOwnershipOutcome.Returned, result.Ownership!.Paths.Single().Outcome);
        return Task.CompletedTask;
    }

    private static Task EvidenceMismatchAsync()
    {
        SafeCoreMirSource mirSource = new("ownership-evidence.rs", new TextSpan(0, 1), 0, 1);
        SafeCoreMirSource drifted = new("other.rs", new TextSpan(0, 1), 0, 1);
        SafeCoreMirProgram mir = new([
            new SafeCoreMirFunction(
                0,
                "crate::scalar",
                Integer,
                [],
                [new SafeCoreMirBlock(0, [],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Constant(Integer, "7", mirSource), mirSource),
                    mirSource)],
                0,
                mirSource),
        ]);
        SafeCoreOwnershipProgram ownership = new([
            new SafeCoreOwnershipFunction(
                "crate::scalar",
                [],
                [new SafeCoreOwnershipScope(0, -1, drifted)],
                [new SafeCoreOwnershipBlock(0, 0, [], SafeCoreOwnershipTerminator.ReturnUnit(drifted), drifted)],
                0,
                SafeCorePanicStrategy.Unwind,
                drifted),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(mir, ownership);
        AssertEx.False(result.IsSuccessful, "Source drift must not publish ownership evidence.");
        AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreMirOwnershipAdapter.EvidenceMismatch),
            "Source drift must have a stable adapter diagnostic.");
        return Task.CompletedTask;
    }

    private static Task MissingBlockEvidenceAsync()
    {
        SafeCoreMirProgram mir = new([
            new SafeCoreMirFunction(
                0,
                "crate::missing-block",
                Integer,
                [],
                [
                    new SafeCoreMirBlock(
                        0,
                        [],
                        SafeCoreMirTerminator.Return(SafeCoreMirOperand.Constant(Integer, "7", Source), Source),
                        Source),
                    new SafeCoreMirBlock(
                        1,
                        [],
                        SafeCoreMirTerminator.Unreachable(Source),
                        Source),
                ],
                0,
                Source),
        ]);
        SafeCoreOwnershipProgram ownership = new([
            new SafeCoreOwnershipFunction(
                "crate::missing-block",
                [],
                [new SafeCoreOwnershipScope(0, -1, Source)],
                [new SafeCoreOwnershipBlock(0, 0, [],
                    SafeCoreOwnershipTerminator.ReturnUnit(Source), Source)],
                0,
                SafeCorePanicStrategy.Unwind,
                Source),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(mir, ownership);
        AssertEx.False(result.IsSuccessful, "Ownership evidence must cover every typed-MIR block.");
        AssertEx.True(result.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreMirOwnershipAdapter.MissingEvidence),
            "A missing typed-MIR block must produce MissingEvidence.");
        return Task.CompletedTask;
    }

    private static Task ExtraLocalEvidenceAsync()
    {
        SafeCoreType reference = SafeCoreType.Reference(Integer, mutable: false);
        SafeCoreMirProgram mir = new([
            new SafeCoreMirFunction(
                0,
                "crate::extra-local",
                reference,
                [new SafeCoreMirLocal(0, "view", reference, SafeCoreMirLocalKind.Parameter, false, Source)],
                [new SafeCoreMirBlock(
                    0,
                    [],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(0, reference, Source), Source),
                    Source)],
                0,
                Source),
        ]);
        SafeCoreOwnershipProgram ownership = new([
            new SafeCoreOwnershipFunction(
                "crate::extra-local",
                [
                    new SafeCoreOwnershipLocal(0, "view", reference, SafeCoreOwnershipKind.Move,
                        HasDrop: false, ScopeId: 0, IsReference: true, InitiallyInitialized: true, Source: Source),
                    new SafeCoreOwnershipLocal(1, "extra", reference, SafeCoreOwnershipKind.Move,
                        HasDrop: false, ScopeId: 0, IsReference: true, InitiallyInitialized: true, Source: Source),
                ],
                [new SafeCoreOwnershipScope(0, -1, Source)],
                [new SafeCoreOwnershipBlock(0, 0, [], SafeCoreOwnershipTerminator.Return(0, Source), Source)],
                0,
                SafeCorePanicStrategy.Unwind,
                Source),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(mir, ownership);
        AssertEx.False(result.IsSuccessful, "An ownership fact for a local absent from MIR must be rejected.");
        AssertEx.True(result.Ownership is null, "Extra evidence must not reach the ownership analyzer.");
        AssertEx.True(result.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreMirOwnershipAdapter.EvidenceMismatch &&
                diagnostic.Message.Contains("extra local fact 1", StringComparison.Ordinal)),
            "Extra local IDs must have a stable EvidenceMismatch diagnostic.");
        return Task.CompletedTask;
    }

    private static Task EvidenceOperationBudgetAsync()
    {
        SafeCoreMirProgram mir = new([
            new SafeCoreMirFunction(
                0,
                "crate::budget",
                Integer,
                [],
                [new SafeCoreMirBlock(
                    0,
                    [],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Constant(Integer, "7", Source), Source),
                    Source)],
                0,
                Source),
        ]);
        SafeCoreOwnershipProgram ownership = new([
            new SafeCoreOwnershipFunction(
                "crate::budget",
                [],
                [new SafeCoreOwnershipScope(0, -1, Source)],
                [new SafeCoreOwnershipBlock(0, 0, [], SafeCoreOwnershipTerminator.ReturnUnit(Source), Source)],
                0,
                SafeCorePanicStrategy.Unwind,
                Source),
        ]);

        SafeCoreMirValidationResult baseline = SafeCoreMirValidation.Validate(mir,
            new SafeCoreMirValidationOptions { MaximumOperations = 100_000 });
        AssertEx.True(baseline.IsSuccessful, "The budget fixture must be structurally valid.");
        // The fixture consumes a bounded, deterministic validation/correlation
        // prefix.  End exactly at the hand-off boundary so the adapter must
        // reject an exhausted shared budget rather than inventing one step.
        int budget = checked(baseline.OperationsUsed + 4);
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(mir, ownership,
            new SafeCoreMirOwnershipOptions { MaximumOperations = budget });
        AssertEx.True(result.IsTruncated, "The combined adapter must report exhaustion of its shared operation budget.");
        AssertEx.True(result.Ownership is null || result.Ownership.IsTruncated,
            "The remaining ownership budget must be bounded after validation and correlation work.");
        AssertEx.True(result.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreMirOwnershipAdapter.LimitReached),
            "Exhausting the shared budget must retain the stable limit diagnostic.");
        return Task.CompletedTask;
    }

    private static Task ScalarMoveSpoofAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreOwnershipFunction ownership) = ScalarEvidenceFixture();
        SafeCoreOwnershipBlock block = ownership.Blocks[0];
        var spoofed = new SafeCoreOwnershipBlock(block.Id, block.ScopeId,
            [SafeCoreOwnershipInstruction.Move(0, 1, Source), block.Instructions[1]],
            block.Terminator, block.Source);
        SafeCoreOwnershipFunction function = new(ownership.Name, ownership.Locals, ownership.Scopes,
            [spoofed], ownership.EntryBlockId, ownership.PanicStrategy, ownership.Source);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(mir,
            new SafeCoreOwnershipProgram([function]));
        AssertEx.False(result.IsSuccessful, "Scalar ownership effects must not be replaceable by a fabricated move.");
        AssertEx.True(result.Ownership is null, "Mismatched scalar evidence must not reach ownership analysis.");
        AssertEx.True(result.Diagnostics.Any(diagnostic =>
            diagnostic.Code == SafeCoreMirOwnershipAdapter.EvidenceMismatch),
            "A scalar Use-to-Move substitution must report RSM3004.");
        return Task.CompletedTask;
    }

    private static Task ScalarTerminatorDriftAsync()
    {
        (SafeCoreMirProgram mir, SafeCoreOwnershipFunction ownership) = ScalarEvidenceFixture();
        SafeCoreMirSource drifted = new("other.rs", Source.Span, Source.HirNodeId, Source.SourceLength);
        SafeCoreOwnershipBlock block = ownership.Blocks[0];
        var spoofed = new SafeCoreOwnershipBlock(block.Id, 1, block.Instructions,
            SafeCoreOwnershipTerminator.Return(1, drifted), block.Source);
        SafeCoreOwnershipFunction function = new(ownership.Name, ownership.Locals,
            [new SafeCoreOwnershipScope(0, -1, Source), new SafeCoreOwnershipScope(1, 0, Source)],
            [spoofed], ownership.EntryBlockId, ownership.PanicStrategy, ownership.Source);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(mir,
            new SafeCoreOwnershipProgram([function]));
        AssertEx.False(result.IsSuccessful, "Scalar terminator scope/source drift must be rejected.");
        AssertEx.True(result.Ownership is null, "Scope/source drift must not reach ownership analysis.");
        AssertEx.True(result.Diagnostics.Any(diagnostic =>
            diagnostic.Code == SafeCoreMirOwnershipAdapter.EvidenceMismatch),
            "Terminator scope/source drift must report RSM3004.");
        return Task.CompletedTask;
    }

    private static (SafeCoreMirProgram Mir, SafeCoreOwnershipFunction Ownership) ScalarEvidenceFixture()
    {
        SafeCoreMirStatement assignment = new(1,
            SafeCoreMirRvalue.Use(SafeCoreMirOperand.Local(0, Integer, Source), Source), Source);
        SafeCoreMirFunction mirFunction = new(
            0,
            "crate::scalar-evidence",
            Integer,
            [
                new SafeCoreMirLocal(0, "input", Integer, SafeCoreMirLocalKind.Parameter, false, Source),
                new SafeCoreMirLocal(1, "copy", Integer, SafeCoreMirLocalKind.Temporary, false, Source),
            ],
            [new SafeCoreMirBlock(0, [assignment],
                SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(1, Integer, Source), Source), Source)],
            0,
            Source);
        SafeCoreOwnershipFunction ownership = new(
            "crate::scalar-evidence",
            [
                new SafeCoreOwnershipLocal(0, "input", Integer, SafeCoreOwnershipKind.Copy,
                    HasDrop: false, ScopeId: 0, IsReference: false, InitiallyInitialized: true, Source),
                new SafeCoreOwnershipLocal(1, "copy", Integer, SafeCoreOwnershipKind.Copy,
                    HasDrop: false, ScopeId: 0, IsReference: false, InitiallyInitialized: false, Source),
            ],
            [new SafeCoreOwnershipScope(0, -1, Source)],
            [new SafeCoreOwnershipBlock(0, 0,
                [SafeCoreOwnershipInstruction.Use(0, Source), SafeCoreOwnershipInstruction.Assign(1, Source)],
                SafeCoreOwnershipTerminator.Return(1, Source), Source)],
            0,
            SafeCorePanicStrategy.Unwind,
            Source);
        return (new SafeCoreMirProgram([mirFunction]), ownership);
    }
}
