using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirPlaceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("typed MIR resolves nested place projections from the root type", ProjectionChainsAsync),
        new("typed MIR publishes deterministic source-backed place facts only on success", PlaceFactsAsync),
        new("typed MIR rejects forged root and projected result types", PlaceTypesAsync),
        new("typed MIR rejects mismatched projection kinds and bounds", ProjectionShapesAsync),
        new("typed MIR rejects field projections without declaration layout", FieldsAsync),
        new("typed MIR confines place metadata to place operands", OperandMetadataAsync),
        new("typed MIR mutable borrowing respects binding and temporary storage", BorrowBindingsAsync),
        new("typed MIR mutable place access preserves shared reference barriers", BorrowProjectionsAsync),
        new("typed MIR reborrowing preserves reference permissions", ReborrowsAsync),
        new("typed MIR writes require accessible mutable references", WritesAsync),
        new("typed MIR place validation enforces an independent projection budget", ProjectionBudgetsAsync),
    ];

    private static readonly SafeCoreType Integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
    private static readonly SafeCoreType Boolean = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Bool);
    private static readonly SafeCoreType SharedInteger = SafeCoreType.Reference(Integer, false);
    private static readonly SafeCoreType MutableInteger = SafeCoreType.Reference(Integer, true);
    private static readonly SafeCoreMirSource Source = new("places.rs", new TextSpan(0, 12), 0, 12);

    private static SafeCoreMirOperand Place(SafeCoreType type, params SafeCoreMirProjection[] projections) =>
        SafeCoreMirOperand.PlaceValue(new(0, projections), type, Source);

    private static SafeCoreMirProgram Read(SafeCoreType rootType, SafeCoreMirOperand operand,
        bool mutable = false, SafeCoreMirLocalKind kind = SafeCoreMirLocalKind.Parameter) => new([
        new(0, "crate::main", operand.Type,
            [new(0, "root", rootType, kind, mutable, Source)],
            [new(0, [], SafeCoreMirTerminator.Return(operand, Source), Source)], 0, Source),
    ]);

    private static SafeCoreMirProgram Compute(SafeCoreType rootType, SafeCoreMirRvalue value,
        bool mutable = false, SafeCoreMirLocalKind kind = SafeCoreMirLocalKind.Parameter) => new([
        new(0, "crate::main", value.Type,
            [new(0, "root", rootType, kind, mutable, Source),
             new(1, "result", value.Type, SafeCoreMirLocalKind.Temporary, false, Source)],
            [new(0, [new(1, value, Source)],
                SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(1, value.Type, Source), Source), Source)], 0, Source),
    ]);

    private static SafeCoreMirRvalue Borrow(SafeCoreMirOperand operand, bool mutable) =>
        SafeCoreMirRvalue.Unary(mutable ? "&mut" : "&", operand,
            SafeCoreType.Reference(operand.Type, mutable), Source);

    private static void Valid(SafeCoreMirProgram program, string message)
    {
        SafeCoreMirValidationResult result = SafeCoreMirValidation.Validate(program);
        AssertEx.True(result.IsSuccessful,
            $"{message} Diagnostics: {string.Join("; ", result.Diagnostics.Select(item => item.Code + ": " + item.Message))}");
    }

    private static void Invalid(SafeCoreMirProgram program, string code)
    {
        SafeCoreMirValidationResult result = SafeCoreMirValidation.Validate(program);
        AssertEx.False(result.IsSuccessful, "Malformed place MIR must be rejected.");
        AssertEx.True(result.Diagnostics.Any(item => item.Code == code), $"Expected diagnostic {code}.");
        AssertEx.Equal(0, result.Places.Count, "Failed validation cannot expose partial place facts.");
    }

    private static Task ProjectionChainsAsync()
    {
        Valid(Read(Integer, Place(Integer)), "A root place retains its local type.");
        SafeCoreType tuple = SafeCoreType.Tuple([Boolean, SafeCoreType.Array(MutableInteger, 2)]);
        SafeCoreType root = SafeCoreType.Reference(tuple, true);
        SafeCoreMirOperand projected = Place(Integer,
            SafeCoreMirProjection.Dereference(), SafeCoreMirProjection.TupleIndex(1),
            SafeCoreMirProjection.ArrayIndex(1), SafeCoreMirProjection.Dereference());
        Valid(Read(root, projected), "Each mixed projection must consume the preceding result type.");
        Valid(Read(SafeCoreType.Tuple([Integer, Boolean]), Place(Boolean, SafeCoreMirProjection.TupleIndex(1))),
            "A tuple projection preserves the selected member type.");
        Valid(Read(SafeCoreType.Array(Integer, 1), Place(Integer, SafeCoreMirProjection.ArrayIndex(0))),
            "The last valid fixed-array index is accepted.");
        return Task.CompletedTask;
    }

    private static Task PlaceFactsAsync()
    {
        SafeCoreType rootType = SafeCoreType.Tuple([MutableInteger, SharedInteger]);
        SafeCoreType resultType = SafeCoreType.Tuple([Integer, Integer]);
        SafeCoreMirOperand mutable = Place(Integer, SafeCoreMirProjection.TupleIndex(0), SafeCoreMirProjection.Dereference());
        SafeCoreMirOperand shared = Place(Integer, SafeCoreMirProjection.TupleIndex(1), SafeCoreMirProjection.Dereference())
            with { Source = Source with { HirNodeId = 1, Span = new TextSpan(2, 4) } };
        SafeCoreMirFunction first = Compute(rootType, SafeCoreMirRvalue.Tuple([mutable, shared], resultType, Source)).Functions[0];
        SafeCoreMirFunction second = new(1, "crate::other", first.ReturnType, first.Locals, first.Blocks, 0, Source);
        SafeCoreMirProgram program = new([first, second]);
        SafeCoreMirValidationResult result = SafeCoreMirValidation.Validate(program);
        AssertEx.True(result.IsSuccessful, "Valid projection facts must be published.");
        AssertEx.Equal(4, result.Places.Count);
        SafeCoreMirPlaceType fact = result.Places[0];
        AssertEx.Equal(0, fact.FunctionId);
        AssertEx.Equal(0, fact.Place.LocalId);
        AssertEx.True(ReferenceEquals(mutable.Place, fact.Place), "Facts must retain the original owner and projection identity.");
        AssertEx.Equal(rootType, fact.RootType);
        AssertEx.Equal(Integer, fact.Type);
        AssertEx.Equal(mutable.Source, fact.Source);
        AssertEx.True(fact.IsMutable, "A mutable reference grants mutable access despite an immutable aggregate binding.");
        AssertEx.False(result.Places[1].IsMutable, "A shared dereference publishes shared access.");
        AssertEx.Equal(shared.Source, result.Places[1].Source);
        AssertEx.Equal(1, result.Places[2].FunctionId);
        AssertEx.Equal(1, result.Places[3].FunctionId);
        AssertEx.True(result.Places.SequenceEqual(SafeCoreMirValidation.Validate(program).Places),
            "Repeated validation must retain deterministic function and operand order.");

        SafeCoreMirOperand forged = shared with { Type = Boolean };
        Invalid(Compute(rootType, SafeCoreMirRvalue.Tuple([mutable, forged], SafeCoreType.Tuple([Integer, Boolean]), Source)),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        SafeCoreMirRvalue partiallyChecked = SafeCoreMirRvalue.Tuple(
            [Place(MutableInteger, SafeCoreMirProjection.TupleIndex(0)), shared],
            SafeCoreType.Tuple([MutableInteger, Integer]), Source);
        SafeCoreMirValidationResult limited = SafeCoreMirValidation.Validate(Compute(rootType, partiallyChecked),
            new() { MaximumProjectionDepth = 1 });
        AssertEx.True(limited.IsTruncated, "The second place must exceed the depth budget after the first was checked.");
        AssertEx.Equal(0, limited.Places.Count, "Truncation must discard already computed place facts.");

        SafeCoreType oneField = SafeCoreType.Tuple([Integer]);
        SafeCoreMirFunction reachable = Read(oneField, Place(Integer, SafeCoreMirProjection.TupleIndex(0))).Functions[0];
        SafeCoreMirSource deadSource = Source with { HirNodeId = 2, Span = new TextSpan(7, 1) };
        SafeCoreMirOperand deadOperand = Place(Integer, SafeCoreMirProjection.TupleIndex(1)) with { Source = deadSource };
        SafeCoreMirFunction withDeadBlock = new(0, reachable.Name, reachable.ReturnType, reachable.Locals,
            [reachable.Blocks[0], new(1, [], SafeCoreMirTerminator.Return(deadOperand, deadSource), deadSource)], 0, Source);
        SafeCoreMirValidationResult deadResult = SafeCoreMirValidation.Validate(new([withDeadBlock]));
        AssertEx.False(deadResult.IsSuccessful, "Unreachable blocks must still validate their projected operands.");
        AssertEx.Equal(0, deadResult.Places.Count, "A malformed dead block invalidates earlier reachable place facts.");
        SafeCoreMirDiagnostic diagnostic = deadResult.Diagnostics.Single(item => item.Code == SafeCoreMirDiagnosticCodes.TypeMismatch);
        AssertEx.Equal(deadSource, AssertEx.NotNull(diagnostic.Source, "The projected operand must retain source evidence."));
        AssertEx.Equal(0, diagnostic.FunctionId);
        AssertEx.Equal(1, diagnostic.BlockId);
        return Task.CompletedTask;
    }

    private static Task PlaceTypesAsync()
    {
        Invalid(Read(Integer, Place(Boolean)), SafeCoreMirDiagnosticCodes.TypeMismatch);
        SafeCoreType tuple = SafeCoreType.Tuple([Integer, Boolean]);
        Invalid(Read(tuple, Place(Integer, SafeCoreMirProjection.TupleIndex(1))), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Read(SharedInteger, Place(Boolean, SafeCoreMirProjection.Dereference())), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Read(SafeCoreType.Array(Integer, 2), Place(Boolean, SafeCoreMirProjection.ArrayIndex(1))),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        SafeCoreMirOperand wrongIdentity = Place(Integer) with { Id = 1 };
        Invalid(Read(Integer, wrongIdentity), SafeCoreMirDiagnosticCodes.InvalidOperand);
        SafeCoreMirOperand absent = SafeCoreMirOperand.PlaceValue(SafeCoreMirPlace.Root(1), Integer, Source);
        Invalid(Read(Integer, absent), SafeCoreMirDiagnosticCodes.InvalidOperand);
        Invalid(Read(Integer, new(SafeCoreMirOperandKind.Place, Integer, 0, null, Source)),
            SafeCoreMirDiagnosticCodes.InvalidOperand);
        return Task.CompletedTask;
    }

    private static Task ProjectionShapesAsync()
    {
        SafeCoreType tuple = SafeCoreType.Tuple([Integer]);
        SafeCoreType array = SafeCoreType.Array(Integer, 1);
        Invalid(Read(Integer, Place(Integer, SafeCoreMirProjection.TupleIndex(0))), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Read(Integer, Place(Integer, SafeCoreMirProjection.ArrayIndex(0))), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Read(Integer, Place(Integer, SafeCoreMirProjection.Dereference())), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Read(tuple, Place(Integer, SafeCoreMirProjection.ArrayIndex(0))), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Read(array, Place(Integer, SafeCoreMirProjection.TupleIndex(0))), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Read(tuple, Place(Integer, SafeCoreMirProjection.TupleIndex(1))), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Read(array, Place(Integer, SafeCoreMirProjection.ArrayIndex(1))), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Read(SafeCoreType.Array(Integer, 0), Place(Integer, SafeCoreMirProjection.ArrayIndex(0))),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Read(tuple, Place(Integer, SafeCoreMirProjection.TupleIndex(0), SafeCoreMirProjection.Dereference())),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        return Task.CompletedTask;
    }

    private static Task FieldsAsync()
    {
        Invalid(Read(SafeCoreType.Adt("crate::Pair"), Place(Integer, SafeCoreMirProjection.Field("value"))),
            SafeCoreMirDiagnosticCodes.UnsupportedNode);
        Invalid(Read(SafeCoreType.Tuple([Integer]), Place(Integer, SafeCoreMirProjection.Field("0"))),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        return Task.CompletedTask;
    }

    private static Task OperandMetadataAsync()
    {
        Invalid(Read(Integer, SafeCoreMirOperand.Local(0, Integer, Source) with { Place = SafeCoreMirPlace.Root(0) }),
            SafeCoreMirDiagnosticCodes.InvalidOperand);
        Invalid(Read(Integer, SafeCoreMirOperand.Constant(Integer, "1", Source) with { Place = SafeCoreMirPlace.Root(0) }),
            SafeCoreMirDiagnosticCodes.InvalidOperand);
        Invalid(Read(Integer, Place(Integer) with { Value = "1" }), SafeCoreMirDiagnosticCodes.InvalidOperand);

        SafeCoreType functionType = SafeCoreType.Function([], Integer, "crate::callee");
        SafeCoreMirOperand functionOperand = SafeCoreMirOperand.Function(1, functionType, Source);
        SafeCoreMirFunction callee = new(1, "crate::callee", Integer, [],
            [new(0, [], SafeCoreMirTerminator.Return(SafeCoreMirOperand.Constant(Integer, "1", Source), Source), Source)], 0, Source);
        SafeCoreMirFunction caller = Read(Integer, functionOperand).Functions[0];
        Valid(new([caller, callee]), "The function operand is valid before adding unrelated place metadata.");
        SafeCoreMirFunction forgedCaller = Read(Integer, functionOperand with { Place = SafeCoreMirPlace.Root(0) }).Functions[0];
        Invalid(new([forgedCaller, callee]), SafeCoreMirDiagnosticCodes.InvalidOperand);
        return Task.CompletedTask;
    }

    private static Task BorrowBindingsAsync()
    {
        SafeCoreMirOperand local = SafeCoreMirOperand.Local(0, Integer, Source);
        Valid(Compute(Integer, Borrow(local, false)), "Immutable parameter storage can be borrowed shared.");
        Invalid(Compute(Integer, Borrow(local, true)), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Valid(Compute(Integer, Borrow(local, true), mutable: true), "Mutable parameter storage can be borrowed mutably.");
        Invalid(Compute(Integer, Borrow(Place(Integer), true), kind: SafeCoreMirLocalKind.User),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        Valid(Compute(Integer, Borrow(Place(Integer), true), mutable: true, kind: SafeCoreMirLocalKind.User),
            "Mutable user storage permits mutable borrowing through a root place.");
        Valid(Compute(Integer, Borrow(local, true), kind: SafeCoreMirLocalKind.Temporary),
            "Lowering may promote an immutable temporary slot into mutable borrow storage.");
        Valid(Compute(Integer, Borrow(Place(Integer), true), kind: SafeCoreMirLocalKind.Temporary),
            "Temporary promotion also applies to explicit root places.");
        SafeCoreMirOperand literal = SafeCoreMirOperand.Constant(Integer, "1", Source);
        Valid(Compute(Integer, Borrow(literal, false)), "Shared literal promotion remains supported.");
        Invalid(Compute(Integer, Borrow(literal, true)), SafeCoreMirDiagnosticCodes.TypeMismatch);
        return Task.CompletedTask;
    }

    private static Task BorrowProjectionsAsync()
    {
        SafeCoreMirOperand dereferenced = Place(Integer, SafeCoreMirProjection.Dereference());
        Valid(Compute(MutableInteger, Borrow(dereferenced, true)),
            "An immutable binding containing &mut T still grants mutable access to T.");
        Invalid(Compute(SharedInteger, Borrow(dereferenced, true), mutable: true), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Valid(Compute(SharedInteger, Borrow(dereferenced, false)), "A shared referent can be borrowed shared.");

        SafeCoreMirOperand nested = Place(Integer, SafeCoreMirProjection.Dereference(), SafeCoreMirProjection.Dereference());
        Invalid(Compute(SafeCoreType.Reference(MutableInteger, false), Borrow(nested, true), mutable: true),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        Valid(Compute(SafeCoreType.Reference(MutableInteger, true), Borrow(nested, true)),
            "Two mutable dereferences grant mutable access through an immutable root binding.");
        Invalid(Compute(SafeCoreType.Reference(SharedInteger, true), Borrow(nested, true)),
            SafeCoreMirDiagnosticCodes.TypeMismatch);

        SafeCoreType tuple = SafeCoreType.Tuple([Integer]);
        SafeCoreMirOperand tupleMember = Place(Integer, SafeCoreMirProjection.TupleIndex(0));
        Invalid(Compute(tuple, Borrow(tupleMember, true)), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Valid(Compute(tuple, Borrow(tupleMember, true), mutable: true), "Aggregate members inherit mutable storage access.");
        SafeCoreMirOperand tupleReferent = Place(Integer, SafeCoreMirProjection.TupleIndex(0), SafeCoreMirProjection.Dereference());
        Valid(Compute(SafeCoreType.Tuple([MutableInteger]), Borrow(tupleReferent, true)),
            "An immutable aggregate can carry a mutable reference to separately mutable storage.");
        SafeCoreMirOperand arrayMember = Place(Integer, SafeCoreMirProjection.ArrayIndex(0));
        Valid(Compute(SafeCoreType.Array(Integer, 1), Borrow(arrayMember, true), mutable: true),
            "Fixed-array members inherit mutable storage access.");

        SafeCoreType sharedAggregate = SafeCoreType.Reference(
            SafeCoreType.Tuple([SafeCoreType.Array(MutableInteger, 1)]), false);
        SafeCoreMirOperand nestedAggregate = Place(Integer, SafeCoreMirProjection.Dereference(),
            SafeCoreMirProjection.TupleIndex(0), SafeCoreMirProjection.ArrayIndex(0), SafeCoreMirProjection.Dereference());
        SafeCoreMirValidationResult read = SafeCoreMirValidation.Validate(Read(sharedAggregate, nestedAggregate));
        AssertEx.True(read.IsSuccessful, "Mixed projections beyond a shared barrier remain readable.");
        AssertEx.False(read.Places.Single().IsMutable,
            "A shared barrier survives tuple and array projections followed by a mutable dereference.");
        Invalid(Compute(sharedAggregate, Borrow(nestedAggregate, true), mutable: true), SafeCoreMirDiagnosticCodes.TypeMismatch);
        return Task.CompletedTask;
    }

    private static Task ReborrowsAsync()
    {
        SafeCoreMirOperand shared = SafeCoreMirOperand.Local(0, SharedInteger, Source);
        SafeCoreMirOperand mutable = SafeCoreMirOperand.Local(0, MutableInteger, Source);
        Invalid(Compute(SharedInteger, SafeCoreMirRvalue.Unary("reborrow_mut", shared, MutableInteger, Source), mutable: true),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        Valid(Compute(MutableInteger, SafeCoreMirRvalue.Unary("reborrow_mut", mutable, MutableInteger, Source)),
            "A mutable reference may be reborrowed through an immutable binding.");
        Valid(Compute(MutableInteger, SafeCoreMirRvalue.Unary("reborrow", mutable, SharedInteger, Source)),
            "Mutable references permit shared reborrowing.");
        Valid(Compute(SharedInteger, SafeCoreMirRvalue.Unary("reborrow", shared, SharedInteger, Source)),
            "Shared references permit shared reborrowing.");
        SafeCoreMirOperand projected = Place(MutableInteger, SafeCoreMirProjection.Dereference());
        Invalid(Compute(SafeCoreType.Reference(MutableInteger, false),
            SafeCoreMirRvalue.Unary("reborrow_mut", projected, MutableInteger, Source)), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Valid(Compute(SafeCoreType.Reference(MutableInteger, true),
            SafeCoreMirRvalue.Unary("reborrow_mut", projected, MutableInteger, Source)),
            "A mutable reference reached through mutable dereferencing remains reborrowable.");
        return Task.CompletedTask;
    }

    private static Task WritesAsync()
    {
        SafeCoreMirOperand number = SafeCoreMirOperand.Constant(Integer, "7", Source);
        Invalid(Compute(SharedInteger, SafeCoreMirRvalue.Write(
            SafeCoreMirOperand.Local(0, SharedInteger, Source), number, Integer, Source), mutable: true),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        Valid(Compute(MutableInteger, SafeCoreMirRvalue.Write(
            SafeCoreMirOperand.Local(0, MutableInteger, Source), number, Integer, Source)),
            "Writing through &mut T does not require a mutable reference binding.");
        SafeCoreMirOperand projected = Place(MutableInteger, SafeCoreMirProjection.Dereference());
        Invalid(Compute(SafeCoreType.Reference(MutableInteger, false),
            SafeCoreMirRvalue.Write(projected, number, Integer, Source)), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Valid(Compute(SafeCoreType.Reference(MutableInteger, true),
            SafeCoreMirRvalue.Write(projected, number, Integer, Source)),
            "Writes can traverse a chain of mutable references.");
        return Task.CompletedTask;
    }

    private static Task ProjectionBudgetsAsync()
    {
        SafeCoreType nested = SafeCoreType.Tuple([SafeCoreType.Array(Integer, 1)]);
        SafeCoreMirProgram program = Read(nested, Place(Integer,
            SafeCoreMirProjection.TupleIndex(0), SafeCoreMirProjection.ArrayIndex(0)));
        SafeCoreMirValidationResult limited = SafeCoreMirValidation.Validate(program, new() { MaximumProjectionDepth = 1 });
        AssertEx.True(limited.IsTruncated, "Projection depth exhaustion must be reported as truncation.");
        AssertEx.True(limited.Diagnostics.Any(item => item.Code == SafeCoreMirDiagnosticCodes.LimitReached),
            "Projection limits must produce a limit diagnostic.");
        AssertEx.Equal(0, limited.Places.Count, "Truncated validation cannot publish place facts.");
        AssertEx.True(SafeCoreMirValidation.Validate(program, new() { MaximumProjectionDepth = 2 }).IsSuccessful,
            "Exactly the permitted number of projections must remain valid.");
        AssertEx.True(SafeCoreMirValidation.Validate(Read(Integer, Place(Integer)), new() { MaximumProjectionDepth = 1 }).IsSuccessful,
            "A root place consumes no projection depth.");
        AssertEx.Throws<ArgumentOutOfRangeException>(() => SafeCoreMirValidation.Validate(program, new() { MaximumProjectionDepth = 0 }));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => SafeCoreMirValidation.Validate(program, new() { MaximumProjectionDepth = 129 }));
        SafeCoreMirOperand projectedReference = Place(MutableInteger, SafeCoreMirProjection.TupleIndex(0));
        SafeCoreMirProgram reborrow = Compute(SafeCoreType.Tuple([MutableInteger]),
            SafeCoreMirRvalue.Unary("reborrow_mut", projectedReference, MutableInteger, Source));
        AssertEx.True(SafeCoreMirValidation.Validate(reborrow, new() { MaximumProjectionDepth = 1 }).IsTruncated,
            "The implicit mutable-reborrow dereference consumes the projection budget.");
        AssertEx.True(SafeCoreMirValidation.Validate(reborrow, new() { MaximumProjectionDepth = 2 }).IsSuccessful,
            "Explicit and implicit projections may exactly fill the projection budget.");
        SafeCoreMirValidationResult workLimited = SafeCoreMirValidation.Validate(program,
            new() { MaximumOperations = 1 });
        AssertEx.True(workLimited.IsTruncated, "Place traversal participates in the shared operation budget.");
        AssertEx.True(workLimited.Diagnostics.Any(item => item.Code == SafeCoreMirDiagnosticCodes.LimitReached),
            "An exhausted operation budget must report a limit diagnostic.");
        AssertEx.Equal(0, workLimited.Places.Count);
        SafeCoreMirValidationResult timeLimited = SafeCoreMirValidation.Validate(program,
            new() { Timeout = TimeSpan.FromTicks(1) });
        AssertEx.True(timeLimited.IsTruncated, "Place validation must obey the wall-clock deadline.");
        AssertEx.True(timeLimited.Diagnostics.Any(item => item.Code == SafeCoreMirDiagnosticCodes.LimitReached),
            "An elapsed deadline must report a limit diagnostic.");
        AssertEx.Equal(0, timeLimited.Places.Count);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreMirValidation.Validate(program,
            new() { MaximumProjectionDepth = 2, CancellationToken = cancellation.Token }));
        return Task.CompletedTask;
    }
}
