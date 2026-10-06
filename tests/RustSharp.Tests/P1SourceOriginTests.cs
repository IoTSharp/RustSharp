using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class P1SourceOriginTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1-09 source origins preserve complete projection and flag evidence", CodecAsync),
        new("P1-09 source origins reject duplicate fields and structural budgets", InvalidCodecAsync),
        new("P1-09 composite source origins cover every returned reference slot", CompositeContractsAsync),
        new("P1-09 source origin paths enforce referent types and mutability", ProjectionContractsAsync),
        new("P1-09 enum origin coverage follows possible active variant payloads", EnumContractsAsync),
    ];

    private static Task CodecAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreMirReferenceOrigin[] origins =
        [
            new(0, true, [], false),
            new(-1, false, [], false) { IsStatic = true },
            new(1, true, [SafeCoreMirProjection.Field("value"), SafeCoreMirProjection.TupleIndex(2),
                SafeCoreMirProjection.ArrayIndex(1), SafeCoreMirProjection.Dereference(), SafeCoreMirProjection.DynamicIndex(4),
                SafeCoreMirProjection.Downcast(0), SafeCoreMirProjection.FromEndIndex(2, 3)], true)
            {
                ValuePath = [SafeCoreMirProjection.TupleIndex(0)],
                ParameterPath = [SafeCoreMirProjection.Field("borrow")],
                HasUnknownSliceOffset = true,
            },
        ];
        foreach (SafeCoreMirReferenceOrigin origin in origins)
        {
            deadline.Token.ThrowIfCancellationRequested();
            string text = SafeCoreSourceOriginCodec.Format(origin, deadline.Token);
            SafeCoreMirReferenceOrigin parsed = SafeCoreSourceOriginCodec.Parse(text, deadline.Token);
            AssertEx.Equal(origin.LocalId, parsed.LocalId);
            AssertEx.Equal(origin.IsParameter, parsed.IsParameter);
            AssertEx.Equal(origin.IsStatic, parsed.IsStatic);
            AssertEx.Equal(origin.IsMutable, parsed.IsMutable);
            AssertEx.Equal(origin.HasUnknownSliceOffset, parsed.HasUnknownSliceOffset);
            AssertEx.True(origin.Projections.SequenceEqual(parsed.Projections) && origin.ValuePath.SequenceEqual(parsed.ValuePath) &&
                origin.ParameterPath.SequenceEqual(parsed.ParameterPath), "All three ordered projection paths must remain lossless.");
            AssertEx.Equal(text, SafeCoreSourceOriginCodec.Format(parsed, deadline.Token));
        }
        AssertEx.Equal("parameter:0", SafeCoreSourceOriginCodec.Format(origins[0], deadline.Token));
        AssertEx.Equal("static", SafeCoreSourceOriginCodec.Format(origins[1], deadline.Token));
        AssertEx.True(SafeCoreSourceOriginCodec.Format(new(0, true, [], true), deadline.Token).StartsWith(
            SafeCoreSourceOriginCodec.Prefix, StringComparison.Ordinal), "Mutable origins must retain their explicit flag.");
        string staticMutable = SafeCoreSourceOriginCodec.Format(new(0, true, [], true) { IsStatic = true }, deadline.Token);
        SafeCoreMirReferenceOrigin checkedStaticMutable = SafeCoreSourceOriginCodec.Parse(staticMutable, deadline.Token);
        AssertEx.True(checkedStaticMutable.IsParameter && checkedStaticMutable.IsStatic && checkedStaticMutable.IsMutable,
            "An explicitly checked static mutable parameter root must retain its complete flags.");
        return Task.CompletedTask;
    }

    private static Task InvalidCodecAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string text = SafeCoreSourceOriginCodec.Format(new(0, true, [], true), deadline.Token);
        AssertEx.Throws<ArgumentException>(() => SafeCoreSourceOriginCodec.Parse(text.Replace("\"parameter\":true",
            "\"parameter\":true,\"parameter\":true", StringComparison.Ordinal), deadline.Token));
        AssertEx.Throws<ArgumentException>(() => SafeCoreSourceOriginCodec.Parse(text.Replace("\"parameter\":true",
            "\"parameter\":true,\"unknown\":true", StringComparison.Ordinal), deadline.Token));
        AssertEx.Throws<ArgumentException>(() => SafeCoreSourceOriginCodec.Format(new(0, true,
            Enumerable.Repeat(SafeCoreMirProjection.Dereference(), SafeCoreSourceOriginCodec.MaximumDepth + 1).ToArray(), false), deadline.Token));
        AssertEx.Throws<ArgumentException>(() => SafeCoreSourceOriginCodec.Format(new(0, true,
            [SafeCoreMirProjection.Field(new string('a', SafeCoreSourceOriginCodec.MaximumCharacters))], false), deadline.Token));
        AssertEx.Throws<ArgumentException>(() => SafeCoreSourceOriginCodec.Format(new(-1, false, [], true) { IsStatic = true }, deadline.Token));
        AssertEx.Throws<ArgumentException>(() => SafeCoreSourceOriginCodec.Parse("parameter:0.field:value", deadline.Token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreSourceOriginCodec.Parse("parameter:0", cancelled.Token));
        return Task.CompletedTask;
    }

    private static Task CompositeContractsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreType reference = SafeCoreSourceTypeCodec.Parse("&i32", deadline.Token);
        SafeCoreType result = SafeCoreType.Tuple([reference, reference], deadline.Token);
        string first = SafeCoreSourceOriginCodec.Format(new(0, true, [], false)
        { ValuePath = [SafeCoreMirProjection.TupleIndex(0)] }, deadline.Token);
        string second = SafeCoreSourceOriginCodec.Format(new(1, true, [], false)
        { ValuePath = [SafeCoreMirProjection.TupleIndex(1)] }, deadline.Token);
        AssertEx.Equal(0, SafeCoreSourceOriginCodec.ValidateContract([reference, reference], result,
            [first, first, second], static _ => [], deadline.Token).Count);
        AssertEx.True(SafeCoreSourceOriginCodec.ValidateContract([reference, reference], result,
            [first], static _ => [], deadline.Token).Count > 0, "A missing composite return slot must reject.");
        string inputTuple = SafeCoreSourceOriginCodec.Format(new(0, true, [], false)
        { ParameterPath = [SafeCoreMirProjection.TupleIndex(1)] }, deadline.Token);
        AssertEx.Equal(0, SafeCoreSourceOriginCodec.ValidateContract([result], reference,
            [inputTuple], static _ => [], deadline.Token).Count);
        return Task.CompletedTask;
    }

    private static Task ProjectionContractsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreType input = SafeCoreSourceTypeCodec.Parse("&(i32, bool)", deadline.Token);
        SafeCoreType result = SafeCoreSourceTypeCodec.Parse("&bool", deadline.Token);
        string projection = SafeCoreSourceOriginCodec.Format(new(0, true, [SafeCoreMirProjection.TupleIndex(1)], false), deadline.Token);
        AssertEx.Equal(0, SafeCoreSourceOriginCodec.ValidateContract([input], result, [projection], static _ => [], deadline.Token).Count);
        AssertEx.True(SafeCoreSourceOriginCodec.ValidateContract([input], SafeCoreSourceTypeCodec.Parse("&i32", deadline.Token),
            [projection], static _ => [], deadline.Token).Count > 0, "Referent type substitution must reject.");
        string mutable = SafeCoreSourceOriginCodec.Format(new(0, true, [SafeCoreMirProjection.TupleIndex(1)], true), deadline.Token);
        AssertEx.True(SafeCoreSourceOriginCodec.ValidateContract([input], SafeCoreSourceTypeCodec.Parse("&mut bool", deadline.Token),
            [mutable], static _ => [], deadline.Token).Count > 0, "Mutable returns must not traverse shared storage.");
        return Task.CompletedTask;
    }

    private static Task EnumContractsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreType reference = SafeCoreSourceTypeCodec.Parse("&i32", deadline.Token);
        SafeCoreType integer = reference.ElementType;
        SafeCoreType option = SafeCoreType.Adt("crate::BorrowOption");
        string origin = SafeCoreSourceOriginCodec.Format(new(0, true, [], false)
        { ValuePath = [SafeCoreMirProjection.Field("$v1$0")] }, deadline.Token);
        IReadOnlyList<(string Name, SafeCoreType Type)> Fields(SafeCoreType _) =>
            [("$tag", integer), ("$v1$0", reference)];
        AssertEx.Equal(0, SafeCoreSourceOriginCodec.ValidateContract([reference], option, [], Fields, null, null,
            returnedFields: static (_, _) => [], cancellationToken: deadline.Token).Count);
        AssertEx.Equal(0, SafeCoreSourceOriginCodec.ValidateContract([reference], option, [origin], Fields, null, null,
            returnedFields: (_, _) => [("$v1$0", reference)], cancellationToken: deadline.Token).Count);
        AssertEx.True(SafeCoreSourceOriginCodec.ValidateContract([reference], option, [], Fields, null, null,
            returnedFields: (_, _) => [("$v1$0", reference)], cancellationToken: deadline.Token).Count > 0,
            "A possible active reference payload cannot lose its required origin.");
        AssertEx.True(SafeCoreSourceOriginCodec.ValidateContract([reference], option, [origin], Fields, null, null,
            returnedFields: static (_, _) => [], cancellationToken: deadline.Token).Count > 0,
            "Inactive payloads cannot accept a claimed returned reference origin.");
        string formatted = SafeCoreSourceOriginCodec.FormatReturnedVariant([SafeCoreMirProjection.TupleIndex(0)], "crate::BorrowOption::None", deadline.Token);
        AssertEx.True(formatted.Contains("\"valuePath\":[", StringComparison.Ordinal) && formatted.Contains("\"variantName\":", StringComparison.Ordinal),
            "The independent MIR evidence must encode the same explicit path and qualified variant terms.");
        return Task.CompletedTask;
    }
}
