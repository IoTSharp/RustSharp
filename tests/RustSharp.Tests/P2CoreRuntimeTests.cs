using System.Text;
using RustSharp.Runtime.Core;

namespace RustSharp.Tests;

internal static class P2CoreValueTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P2-01.02 Option none and some preserve the discriminant", OptionDiscriminantAsync),
        new("P2-01.02 Option combinators preserve absence and values", OptionCombinatorsAsync),
        new("P2-01.02 Result ok and err preserve the discriminant", ResultDiscriminantAsync),
        new("P2-01.02 Result combinators preserve errors", ResultCombinatorsAsync),
        new("P2-01.02 unwrap and expect reject the wrong variant", WrongVariantAsync),
        new("P2-01.02 Option and Result equality is structural", EqualityAsync),
        new("P2-01.02 Result map error keeps the success value", MapErrorAsync),
        new("P2-01.02 Option fallback evaluates only when absent", FallbackAsync),
        new("P2-01.02 Result match selects one branch", MatchAsync),
        new("P2-01.02 Option and Result strings expose their variants", VariantTextAsync),
        new("P2-01.02 nested Option and Result values remain typed", NestedAsync),
        new("P2-01.02 invalid value paths produce stable failures", InvalidPathAsync),
    ];
    private static Task OptionDiscriminantAsync() { Option<int> none = Option<int>.None; Option<int> some = Option<int>.Some(42); AssertEx.True(none.IsNone && !none.IsSome); AssertEx.True(some.IsSome && !some.IsNone); AssertEx.Equal(42, some.Unwrap()); AssertEx.Equal(0, none.UnwrapOr(0)); return Task.CompletedTask; }
    private static Task OptionCombinatorsAsync() { AssertEx.Equal(43, Option<int>.Some(42).Map(value => value + 1).Unwrap()); AssertEx.True(Option<int>.None.Map(value => value + 1).IsNone); AssertEx.Equal(84, Option<int>.Some(42).AndThen(value => Option<int>.Some(value * 2)).Unwrap()); return Task.CompletedTask; }
    private static Task ResultDiscriminantAsync() { Result<int, string> ok = Result<int, string>.Ok(42); Result<int, string> err = Result<int, string>.Err("bad"); AssertEx.True(ok.IsOk && !ok.IsErr); AssertEx.True(err.IsErr && !err.IsOk); AssertEx.Equal(42, ok.Unwrap()); AssertEx.Equal("bad", err.UnwrapErr()); return Task.CompletedTask; }
    private static Task ResultCombinatorsAsync() { AssertEx.Equal(43, Result<int, string>.Ok(42).Map(value => value + 1).Unwrap()); AssertEx.True(Result<int, string>.Err("bad").AndThen(value => Result<int, string>.Ok(value + 1)).IsErr); return Task.CompletedTask; }
    private static Task WrongVariantAsync() { AssertEx.Throws<InvalidOperationException>(() => Option<int>.None.Unwrap()); AssertEx.Throws<InvalidOperationException>(() => Result<int, string>.Err("bad").Unwrap()); AssertEx.Throws<InvalidOperationException>(() => Result<int, string>.Ok(1).UnwrapErr()); return Task.CompletedTask; }
    private static Task EqualityAsync() { AssertEx.True(Option<int>.Some(1) == Option<int>.Some(1)); AssertEx.True(Option<int>.Some(1) != Option<int>.Some(2)); AssertEx.True(Result<int, string>.Err("x") == Result<int, string>.Err("x")); return Task.CompletedTask; }
    private static Task MapErrorAsync() { AssertEx.Equal("bad!", Result<int, string>.Err("bad").MapErr(error => error + "!").UnwrapErr()); AssertEx.Equal(4, Result<int, string>.Ok(4).MapErr(error => error.Length).Unwrap()); return Task.CompletedTask; }
    private static Task FallbackAsync() { int calls = 0; AssertEx.Equal(1, Option<int>.None.UnwrapOrElse(() => ++calls)); AssertEx.Equal(42, Option<int>.Some(42).UnwrapOrElse(() => ++calls)); AssertEx.Equal(1, calls); return Task.CompletedTask; }
    private static Task MatchAsync() { AssertEx.Equal("some", Option<int>.Some(1).Match(_ => "some", () => "none")); AssertEx.Equal("bad", Result<int, string>.Err("bad").Match(_ => "ok", error => error)); return Task.CompletedTask; }
    private static Task VariantTextAsync() { AssertEx.Equal("None", Option<int>.None.ToString()); AssertEx.Equal("Some(7)", Option<int>.Some(7).ToString()); AssertEx.Equal("Err(bad)", Result<int, string>.Err("bad").ToString()); return Task.CompletedTask; }
    private static Task NestedAsync() { Option<Result<int, string>> value = Option<Result<int, string>>.Some(Result<int, string>.Ok(9)); AssertEx.Equal(9, value.Unwrap().Unwrap()); return Task.CompletedTask; }
    private static Task InvalidPathAsync() { AssertEx.Throws<InvalidOperationException>(() => Option<int>.None.Expect("missing")); AssertEx.Throws<InvalidOperationException>(() => Result<int, string>.Err("bad").Expect("failed")); return Task.CompletedTask; }
}

internal static class P2CoreFormattingTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P2-01.03 display formats invariant scalars", DisplayAsync),
        new("P2-01.03 debug formats core variants", DebugAsync),
        new("P2-01.03 output is UTF-8 bounded", Utf8Async),
        new("P2-01.03 invalid format specifier rejects", InvalidSpecifierAsync),
        new("P2-01.03 output budget rejects", BudgetAsync),
        new("P2-01.03 null debug is stable", NullAsync),
    ];
    private static Task DisplayAsync() { AssertEx.Equal("-42", CoreFormatting.Display(-42)); return Task.CompletedTask; }
    private static Task DebugAsync() { AssertEx.Equal("Some(7)", CoreFormatting.Debug(Option<int>.Some(7))); return Task.CompletedTask; }
    private static Task Utf8Async() { string value = CoreFormatting.Display("你好"); AssertEx.Equal(6, Encoding.UTF8.GetByteCount(value)); return Task.CompletedTask; }
    private static Task InvalidSpecifierAsync() { AssertEx.Throws<FormatException>(() => CoreFormatting.Format(1, "invalid", 64)); return Task.CompletedTask; }
    private static Task BudgetAsync() { AssertEx.Throws<InvalidOperationException>(() => CoreFormatting.Format(new string('x', 20), "display", 4)); return Task.CompletedTask; }
    private static Task NullAsync() { AssertEx.Equal("null", CoreFormatting.Debug<string?>(null)); return Task.CompletedTask; }
}

internal static class P2CoreComparisonHashTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P2-01.04 comparison returns deterministic ordering", ComparisonAsync),
        new("P2-01.04 float partial comparison rejects NaN", FloatAsync),
        new("P2-01.04 equality is structural", EqualityAsync),
        new("P2-01.04 FNV hashing is stable", StableHashAsync),
        new("P2-01.04 hashing distinguishes inputs", DistinctHashAsync),
        new("P2-01.04 integer hashing is deterministic", IntegerHashAsync),
    ];
    private static Task ComparisonAsync() { AssertEx.Equal(Ordering.Less, CoreComparison.Compare(1, 2)); AssertEx.Equal(Ordering.Equal, CoreComparison.Compare("a", "a")); return Task.CompletedTask; }
    private static Task FloatAsync() { AssertEx.Equal(Ordering.Less, CoreComparison.PartialCompare(1, 2).Unwrap()); AssertEx.True(CoreComparison.PartialCompare(double.NaN, 1).IsNone); return Task.CompletedTask; }
    private static Task EqualityAsync() { AssertEx.True(CoreComparison.Equal(42, 42)); AssertEx.False(CoreComparison.Equal(42, 43)); return Task.CompletedTask; }
    private static Task StableHashAsync() { AssertEx.Equal(CoreHashing.HashString("rustsharp"), CoreHashing.HashString("rustsharp")); return Task.CompletedTask; }
    private static Task DistinctHashAsync() { AssertEx.True(CoreHashing.HashString("rustsharp") != CoreHashing.HashString("rustsharp!")); return Task.CompletedTask; }
    private static Task IntegerHashAsync() { AssertEx.True(CoreHashing.HashInt32(1) != CoreHashing.HashInt32(2)); return Task.CompletedTask; }
}

internal static class P2CoreIteratorTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P2-01.05 empty iterator has no elements", EmptyAsync),
        new("P2-01.05 once iterator has one element", OnceAsync),
        new("P2-01.05 map preserves order", MapAsync),
        new("P2-01.05 map is lazy", LazyAsync),
        new("P2-01.05 empty iterator is reusable", ReusableAsync),
        new("P2-01.05 iterator handles reference values", ReferenceAsync),
    ];
    private static Task EmptyAsync() { AssertEx.Equal(0, CoreIterator.Empty<int>().Count()); return Task.CompletedTask; }
    private static Task OnceAsync() { AssertEx.True(CoreIterator.Once(7).SequenceEqual([7])); return Task.CompletedTask; }
    private static Task MapAsync() { AssertEx.True(CoreIterator.Map([1, 2, 3], value => value * 2).SequenceEqual([2, 4, 6])); return Task.CompletedTask; }
    private static Task LazyAsync() { int calls = 0; IEnumerable<int> values = CoreIterator.Map([1, 2], value => ++calls + value); AssertEx.Equal(0, calls); _ = values.ToArray(); AssertEx.Equal(2, calls); return Task.CompletedTask; }
    private static Task ReusableAsync() { IEnumerable<int> values = CoreIterator.Empty<int>(); AssertEx.Equal(0, values.Count()); AssertEx.Equal(0, values.Count()); return Task.CompletedTask; }
    private static Task ReferenceAsync() { AssertEx.True(CoreIterator.Once("x").SequenceEqual(["x"])); return Task.CompletedTask; }
}

internal static class P2CoreConsumerTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P2-01.06 core runtime surface is independently consumable", ConsumerAsync),
        new("P2-01.06 composed core operations retain types", CompositionAsync),
    ];
    private static Task ConsumerAsync() { Result<int, string> result = Option<int>.Some(20).Map(value => value * 2).Match(value => Result<int, string>.Ok(value), () => Result<int, string>.Err("missing")); AssertEx.Equal(40, result.Unwrap()); return Task.CompletedTask; }
    private static Task CompositionAsync() { AssertEx.True(CoreIterator.Map(new[] { 1, 2 }, value => Option<int>.Some(value)).All(item => item.IsSome)); return Task.CompletedTask; }
}
