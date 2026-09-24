using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirAdtLayoutTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR nominal layouts freeze field identity and validate nested places", LayoutPlacesAsync),
        new("MIR nominal layouts reject duplicate missing recursive and invalid fields", InvalidLayoutsAsync),
        new("MIR nominal Copy evidence cannot contain move-only fields", CopyEvidenceAsync),
        new("MIR ADT constructors preserve declared field types and order", ConstructorsAsync),
        new("MIR projected stores validate destination identity type and access", ProjectedStoresAsync),
        new("MIR dynamic place indices identify evaluated usize locals", DynamicIndicesAsync),
        new("MIR layout validation shares time work depth and size budgets", LayoutBudgetsAsync),
        new("MIR extended snapshots retain nominal layouts and storage destinations", SnapshotsAsync),
    ];

    private static readonly SafeCoreType Integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
    private static readonly SafeCoreType Boolean = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Bool);
    private static readonly SafeCoreType Unit = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Unit);
    private static readonly SafeCoreType Usize = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Usize);
    private static readonly SafeCoreType Pair = SafeCoreType.Adt("crate::Pair");
    private static readonly SafeCoreMirSource Source = new("layouts.rs", new(0, 20), 0, 20);

    private static SafeCoreMirAdtLayout Layout() => new(Pair,
        [new("number", Integer, Source), new("flag", Boolean, Source)], Source);

    private static SafeCoreMirProgram Program(IReadOnlyList<SafeCoreMirAdtLayout> layouts,
        IReadOnlyList<SafeCoreMirLocal>? locals = null, IReadOnlyList<SafeCoreMirStatement>? statements = null,
        SafeCoreMirOperand? returned = null) => new([
            new(0, "crate::main", returned?.Type ?? Unit, locals ?? [],
                [new(0, statements ?? [], SafeCoreMirTerminator.Return(returned, Source), Source)], 0, Source),
        ], layouts);

    private static void Valid(SafeCoreMirProgram program)
    {
        SafeCoreMirValidationResult result = SafeCoreMirValidation.Validate(program);
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics));
    }

    private static void Invalid(SafeCoreMirProgram program, string code)
    {
        SafeCoreMirValidationResult result = SafeCoreMirValidation.Validate(program);
        AssertEx.False(result.IsSuccessful, "Invalid layout/place metadata must fail validation.");
        AssertEx.True(result.Diagnostics.Any(item => item.Code == code), string.Join("; ", result.Diagnostics));
        AssertEx.Equal(0, result.Places.Count);
    }

    private static Task LayoutPlacesAsync()
    {
        SafeCoreType holder = SafeCoreType.Adt("crate::Holder");
        SafeCoreMirAdtField[] fields = [new("pairs", SafeCoreType.Array(Pair, 2), Source)];
        SafeCoreMirAdtLayout layout = new(holder, fields, Source);
        fields[0] = new("forged", Boolean, Source);
        SafeCoreMirAdtLayout[] layouts = [layout, Layout()];
        SafeCoreMirPlace place = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.Dereference())
            .Append(SafeCoreMirProjection.Field("pairs")).Append(SafeCoreMirProjection.ArrayIndex(1))
            .Append(SafeCoreMirProjection.Field("number"));
        SafeCoreMirProgram program = Program(layouts,
            [new(0, "owner", SafeCoreType.Reference(holder, true), SafeCoreMirLocalKind.Parameter, false, Source)],
            returned: SafeCoreMirOperand.PlaceValue(place, Integer, Source));
        layouts[0] = Layout();
        SafeCoreMirValidationResult result = SafeCoreMirValidation.Validate(program);
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics));
        AssertEx.Equal("pairs", program.AdtLayouts[0].Fields[0].Name);
        AssertEx.Equal(Integer, result.Places.Single().Type);
        AssertEx.True(result.Places.Single().IsMutable, "Projected mutable referents retain access through nominal fields.");
        AssertEx.True(ReferenceEquals(place, result.Places.Single().Place), "Typed evidence must retain the exact owner path.");
        Invalid(Program([Layout()], [new(0, "owner", Pair, SafeCoreMirLocalKind.Parameter, false, Source)],
            returned: SafeCoreMirOperand.PlaceValue(SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.Field("missing")), Integer, Source)),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        return Task.CompletedTask;
    }

    private static Task InvalidLayoutsAsync()
    {
        Invalid(Program([Layout(), Layout()]), SafeCoreMirDiagnosticCodes.InvalidInput);
        Invalid(Program([new(Pair, [new("x", Integer, Source), new("x", Boolean, Source)], Source)]),
            SafeCoreMirDiagnosticCodes.InvalidInput);
        Invalid(Program([new(Integer, [], Source)]), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Program([new(Pair, [new("x", SafeCoreType.Slice(Integer), Source)], Source)]), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Program([new(Pair, [new("x", SafeCoreType.Adt("crate::Missing"), Source)], Source)]), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Program([new(Pair, [new("x", SafeCoreType.Tuple([SafeCoreType.Array(Pair, 1)]), Source)], Source)]),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        SafeCoreType second = SafeCoreType.Adt("crate::Second");
        Invalid(Program([new(Pair, [new("x", second, Source)], Source), new(second, [new("x", Pair, Source)], Source)]),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        Valid(Program([new(Pair, [new("next", SafeCoreType.Reference(Pair, false), Source)], Source)]));
        Invalid(Program([new(Pair, [new("x", Integer, Source with { SourceLength = 1 })], Source)]),
            SafeCoreMirDiagnosticCodes.InvalidSource);
        return Task.CompletedTask;
    }

    private static Task CopyEvidenceAsync()
    {
        Valid(Program([new(Pair, [new("x", Integer, Source)], Source, isCopy: true)]));
        Invalid(Program([new(Pair, [new("x", SafeCoreType.Reference(Integer, true), Source)], Source, isCopy: true)]),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        SafeCoreType wrapper = SafeCoreType.Adt("crate::Wrapper");
        Invalid(Program([Layout(), new(wrapper, [new("pair", Pair, Source)], Source, isCopy: true)]),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Program([new(Pair, [], Source, isCopy: true)],
            [new(0, "owner", Pair, SafeCoreMirLocalKind.User, false, Source) { IsUnitAdt = true, DestructorFunctionId = 0 }]),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        return Task.CompletedTask;
    }

    private static Task ConstructorsAsync()
    {
        SafeCoreMirOperand number = SafeCoreMirOperand.Constant(Integer, "7", Source);
        SafeCoreMirOperand flag = SafeCoreMirOperand.Constant(Boolean, "true", Source);
        SafeCoreMirLocal[] locals = [new(0, "pair", Pair, SafeCoreMirLocalKind.User, false, Source)];
        Valid(Program([Layout()], locals, [new(0, SafeCoreMirRvalue.Adt([number, flag], Pair, Source), Source)]));
        Invalid(Program([Layout()], locals, [new(0, SafeCoreMirRvalue.Adt([flag, number], Pair, Source), Source)]),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Program([Layout()], locals, [new(0, SafeCoreMirRvalue.Adt([number], Pair, Source), Source)]),
            SafeCoreMirDiagnosticCodes.InvalidOperand);
        Invalid(Program([Layout()], locals, [new(0, SafeCoreMirRvalue.Use(SafeCoreMirOperand.Constant(Pair, "()", Source), Source), Source)]),
            SafeCoreMirDiagnosticCodes.InvalidOperand);
        Invalid(Program([Layout()], [locals[0] with { IsUnitAdt = true }]), SafeCoreMirDiagnosticCodes.TypeMismatch);
        return Task.CompletedTask;
    }

    private static Task ProjectedStoresAsync()
    {
        SafeCoreMirPlace field = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.Field("number"));
        SafeCoreMirStatement store = new(0, SafeCoreMirRvalue.Use(SafeCoreMirOperand.Constant(Integer, "9", Source), Source), Source)
            { DestinationPlace = field };
        SafeCoreMirLocal owner = new(0, "pair", Pair, SafeCoreMirLocalKind.Parameter, true, Source);
        Valid(Program([Layout()], [owner], [store]));
        Invalid(Program([Layout()], [owner with { IsMutable = false }], [store]), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Program([Layout()], [owner], [store with { DestinationLocalId = 1 }]), SafeCoreMirDiagnosticCodes.InvalidOperand);
        Invalid(Program([Layout()], [owner], [store with { Value = SafeCoreMirRvalue.Use(SafeCoreMirOperand.Constant(Boolean, "true", Source), Source) }]),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        SafeCoreMirPlace referenced = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.Dereference()).Append(SafeCoreMirProjection.Field("number"));
        Invalid(Program([Layout()], [owner with { Type = SafeCoreType.Reference(Pair, false) }], [store with { DestinationPlace = referenced }]),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        Valid(Program([Layout()], [owner with { Type = SafeCoreType.Reference(Pair, true), IsMutable = false }], [store with { DestinationPlace = referenced }]));
        return Task.CompletedTask;
    }

    private static Task DynamicIndicesAsync()
    {
        SafeCoreType array = SafeCoreType.Array(Integer, 2);
        SafeCoreMirLocal[] locals = [new(0, "array", array, SafeCoreMirLocalKind.Parameter, true, Source),
            new(1, "index", Usize, SafeCoreMirLocalKind.Parameter, false, Source)];
        SafeCoreMirPlace place = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.DynamicIndex(1));
        SafeCoreMirProgram dynamicRead = Program([], locals, returned: SafeCoreMirOperand.PlaceValue(place, Integer, Source));
        Valid(dynamicRead);
        AssertEx.True(SafeCoreMirFormatting.Format(dynamicRead).StartsWith("safe-core-mir-v2\n", StringComparison.Ordinal),
            "A dynamic projection alone requires the extended snapshot version.");
        Invalid(Program([], [locals[0], locals[1] with { Type = Integer }], returned: SafeCoreMirOperand.PlaceValue(place, Integer, Source)),
            SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Program([], locals, returned: SafeCoreMirOperand.PlaceValue(SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.DynamicIndex(2)), Integer, Source)),
            SafeCoreMirDiagnosticCodes.InvalidOperand);
        AssertEx.Equal("0[%1]", place.ToString());
        return Task.CompletedTask;
    }

    private static Task LayoutBudgetsAsync()
    {
        SafeCoreMirProgram program = Program([Layout()]);
        SafeCoreMirValidationResult complete = SafeCoreMirValidation.Validate(program);
        AssertEx.True(complete.IsSuccessful, "The budget baseline must be valid.");
        AssertEx.True(SafeCoreMirValidation.Validate(program, new() { MaximumOperations = complete.Operations }).IsSuccessful,
            "The measured inclusive work boundary must succeed.");
        AssertEx.True(SafeCoreMirValidation.Validate(program, new() { MaximumOperations = complete.Operations - 1 }).IsTruncated,
            "Layout work consumes the caller's shared budget.");
        AssertEx.True(SafeCoreMirValidation.Validate(program, new() { MaximumAdtFields = 1 }).IsTruncated, "Field arena bounds must be enforced.");
        SafeCoreMirProgram two = Program([Layout(), new(SafeCoreType.Adt("crate::Other"), [], Source)]);
        AssertEx.True(SafeCoreMirValidation.Validate(two, new() { MaximumAdtLayouts = 1 }).IsTruncated, "Layout arena bounds must be enforced.");
        AssertEx.True(SafeCoreMirValidation.Validate(program, new() { Timeout = TimeSpan.FromTicks(1) }).IsTruncated, "Elapsed time is bounded.");
        SafeCoreMirProgram nested = Program([new(Pair, [new("nested", SafeCoreType.Tuple([SafeCoreType.Tuple([Integer])]), Source)], Source)]);
        AssertEx.True(SafeCoreMirValidation.Validate(nested, new() { MaximumTypeDepth = 1 }).IsTruncated, "Nominal and structural layout depth is bounded.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreMirValidation.Validate(program, new() { CancellationToken = cancellation.Token }));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => SafeCoreMirValidation.Validate(program, new() { MaximumAdtLayouts = 0 }));
        return Task.CompletedTask;
    }

    private static Task SnapshotsAsync()
    {
        SafeCoreMirPlace place = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.Field("number"));
        SafeCoreMirProgram program = Program([Layout()],
            [new(0, "pair", Pair, SafeCoreMirLocalKind.User, true, Source) { StorageScope = Source }],
            [new(0, SafeCoreMirRvalue.Use(SafeCoreMirOperand.Constant(Integer, "9", Source), Source), Source) { DestinationPlace = place }]);
        Valid(program);
        string first = SafeCoreMirFormatting.Format(program);
        AssertEx.Equal(first, SafeCoreMirFormatting.Format(program));
        AssertEx.True(first.StartsWith("safe-core-mir-v2\n", StringComparison.Ordinal), "Extended layouts use a new format version.");
        AssertEx.True(first.Contains("adt crate::Pair move", StringComparison.Ordinal) && first.Contains("place 0.number = use", StringComparison.Ordinal) &&
            first.Contains(" storage [", StringComparison.Ordinal), "Snapshots must retain layout, mutation path and storage source.");
        AssertEx.True(SafeCoreMirFormatting.Format(Program([])).StartsWith("safe-core-mir-v1\n", StringComparison.Ordinal),
            "Legacy snapshots retain their version.");
        return Task.CompletedTask;
    }
}
