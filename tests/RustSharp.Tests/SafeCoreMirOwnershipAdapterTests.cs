using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirOwnershipAdapterTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("typed MIR ownership adapter materializes scalar constants", ConstantReturnAsync),
        new("typed MIR ownership adapter applies local limits per function", PerFunctionLimitAsync),
        new("typed MIR ownership adapter bounds materialized constants", MaterializedConstantLimitAsync),
        new("typed MIR ownership adapter reports use before initialization", UseBeforeInitializationAsync),
        new("typed MIR ownership adapter rejects reference evidence it cannot prove", UnsupportedReferenceAsync),
        new("typed MIR ownership adapter accepts finite Copy tuple aggregates", CopyTupleAsync),
        new("typed MIR ownership adapter lowers non-Copy place reads as moves", NonCopyMoveAsync),
        new("typed MIR ownership adapter preserves projected MIR places", ProjectedPlaceAsync),
        new("typed MIR ownership adapter preserves projected borrow provenance", ProjectedBorrowAsync),
        new("typed MIR ownership evidence rejects projected source drift", ProjectedEvidenceSourceDriftAsync),
        new("typed MIR ownership evidence correlates non-copy source facts", ExplicitEvidenceAsync),
        new("typed MIR ownership evidence rejects source drift", EvidenceMismatchAsync),
        new("typed MIR ownership evidence rejects extra local facts", ExtraLocalEvidenceAsync),
        new("typed MIR ownership evidence rejects missing blocks", MissingBlockEvidenceAsync),
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

    private static Task UnsupportedReferenceAsync()
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
        AssertEx.False(result.IsSuccessful, "Reference ownership cannot be inferred from typed MIR alone.");
        AssertEx.Equal(SafeCoreMirOwnershipDiagnosticCodes.Unsupported, result.Diagnostics.Single().Code);
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
                    new SafeCoreMirLocal(0, "pair", tuple, SafeCoreMirLocalKind.Parameter, false, Source),
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
}
