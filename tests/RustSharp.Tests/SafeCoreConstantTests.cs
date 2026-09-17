using System.Diagnostics;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreConstantTests
{
    private sealed record Fixture(string Source, string? Diagnostic = null);
    private static Fixture Pass(string source) => new(source);
    private static Fixture Fail(string source, string diagnostic) => new(source, diagnostic);

    public static IReadOnlyList<TestCase> All { get; } =
    [
        Group("constant evaluation arithmetic ordering and short circuit",
            Pass("const N: usize = 2 + 3 * 4; fn f() -> [i32; N] { [0; 14] }"),
            Pass("const N: usize = (!0u8) as usize; fn f() -> [i32; N] { [0; 255] }"),
            Pass("const ORDER: bool = false < true; const N: usize = if ORDER { 2 } else { 3 }; fn f() -> [u8; N] { [0; 2] }"),
            Pass("const N: usize = { let (x, y) = (2usize, 3usize); x + y }; fn f() -> [u8; N] { [0; 5] }"),
            Pass("const N: usize = if false { 1 / 0 } else { 2 }; fn f() -> [u8; N] { [0; 2] }"),
            Pass("const FLAG: bool = false && (1 / 0 == 0); const N: usize = if FLAG { 1 } else { 2 }; fn f() -> [u8; N] { [0; 2] }")),
        Group("constant evaluation direct const functions locals and recursion",
            Pass("const N: usize = twice(3); const fn twice(x: usize) -> usize { x * 2 } fn f() -> [u8; N] { [0; 6] }"),
            Pass("const fn sum(n: usize) -> usize { if n == 0 { return 0; } n + sum(n - 1) } const N: usize = sum(3); fn f() -> [u8; N] { [0; 6] }"),
            Pass("const fn choose(flag: bool) -> usize { if flag { return 2; } 3 } fn f() -> [u8; choose(true)] { [0; 2] }"),
            Pass("const fn pair((x, y): (usize, usize)) -> usize { x + y } fn f() -> [u8; pair((2, 3))] { [0; 5] }"),
            Pass("const fn nested() -> usize { let x = 2; { let x = 4; } x } fn f() -> [u8; nested()] { [0; 2] }")),
        Group("constant evaluation purity is checked in all branches",
            Fail("fn runtime() -> i32 { 1 } const fn bad() -> i32 { runtime() }", "RST2010"),
            Fail("fn runtime() -> i32 { 1 } const fn bad() -> i32 { if false { runtime() } else { 0 } } const VALUE: i32 = bad();", "RST2010"),
            Fail("fn runtime() -> usize { 1 } const N: usize = runtime();", "RST2010"),
            Fail("fn f(value: usize) { let result = const { value }; }", "RST2010"),
            Fail("fn f(n: usize) { let values: [u8; n] = []; }", "RST2010")),
        Group("constant evaluation overflow and invalid indexes",
            Fail("const VALUE: i8 = 127 + 1;", "RST2006"),
            Fail("const VALUE: i8 = -128 / -1;", "RST2006"),
            Fail("const VALUE: i8 = -128 % -1;", "RST2006"),
            Fail("const VALUE: usize = 1 / 0;", "RST2006"),
            Fail("const VALUE: u8 = 1u8 << 8;", "RST2006"),
            Fail("const VALUE: i32 = [1, 2][2];", "RST2006"),
            Fail("const VALUE: i64 = (2147483647 + 1) as i64;", "RST2006")),
        Group("constant evaluation casts rounding and IEEE values",
            Pass("const N: usize = (257u16 as u8) as usize; fn f() -> [u8; N] { [0; 1] }"),
            Pass("const N: usize = ((-1i8) as u8) as usize; fn f() -> [u8; N] { [0; 255] }"),
            Pass("const N: usize = '\\u{41}' as usize; fn f() -> [u8; N] { [0; 65] }"),
            Pass("const N: usize = (0.0 / 0.0) as usize; fn f() -> [u8; N] { [] }"),
            Pass("const N: usize = ((1.0 / 0.0) as u8) as usize; fn f() -> [u8; N] { [0; 255] }"),
            Pass("const N: usize = match 0.1f32 { 0.1f32 => 1, _ => 2 }; fn f() -> [u8; N] { [0] }"),
            Pass("const X: f32 = 16777216.0 + 1.0; const N: usize = (X - 16777216.0) as usize; fn f() -> [u8; N] { [] }")),
        Group("constant evaluation bounded loops dependencies and materialization",
            Pass("const N: usize = { let mut x = 0; while x < 4 { x += 1; } x }; fn f() -> [u8; N] { [0; 4] }"),
            Pass("const N: usize = { let mut x = 0; loop { x += 1; if x < 2 { continue; } break x; } }; fn f() -> [u8; N] { [0; 2] }"),
            Fail("const N: usize = loop {};", "RST0002"),
            Fail("const fn recurse() -> usize { recurse() } const N: usize = recurse();", "RST0002"),
            Fail("const A: usize = B; const B: usize = A;", "RST2008"),
            Fail("const VALUES: [u8; 4097] = [0; 4097];", "RST0002")),
        Group("constant evaluation aggregate projections aliases and module context",
            Pass("const PAIR: (usize, bool) = (3, true); const VALUES: [usize; 2] = [2, 4]; fn f() -> [u8; PAIR.0 + VALUES[1]] { [0; 7] }"),
            Pass("struct Size { n: usize } const SIZE: Size = Size { n: 2 }; fn f() -> [u8; SIZE.n] { [0; 2] }"),
            Pass("struct Size(usize); const SIZE: Size = Size(3); fn f() -> [u8; SIZE.0] { [0; 3] }"),
            Pass("struct Size { n: usize, m: usize } const BASE: Size = Size { n: 1, m: 3 }; const NEXT: Size = Size { n: 2, ..BASE }; fn f() -> [u8; NEXT.n + NEXT.m] { [0; 5] }"),
            Pass("mod m { pub struct S { x: usize } pub const N: usize = S { x: 2 }.x; } fn f() -> [u8; m::N] { [0; 2] }"),
            Pass("struct S { n: usize } type A = [u8; S { n: 2 }.n]; fn f() -> A { [0; 2] }")),
        Group("constant evaluation match scalar enum and struct patterns",
            Pass("const N: usize = match 2 { 0 => 9, 1..=3 => 2, _ => 8 }; fn f() -> [u8; N] { [0; 2] }"),
            Pass("enum E { A, B(usize) } const N: usize = match E::B(3) { E::A => 0, E::B(x) => x }; fn f() -> [u8; N] { [0; 3] }"),
            Pass("enum E { A { x: usize }, B { x: usize } } const N: usize = match (E::B { x: 3 }) { E::A { x } => x + 1, E::B { x } => x }; fn f() -> [u8; N] { [0; 3] }"),
            Pass("struct S { n: usize } const N: usize = match (S { n: 2 }) { S { n } => n }; fn f() -> [u8; N] { [0; 2] }"),
            Pass("const N: usize = match (2usize, 3usize) { (x, y) if x < y => y, _ => 1 }; fn f() -> [u8; N] { [0; 3] }")),
        Group("constant contexts validate dead branches and isolate control flow",
            Fail("fn runtime() -> usize { 2 } fn f() -> [u8; { if true { 1 } else { runtime() } }] { [0] }", "RST2010"),
            Fail("fn f(x: usize) -> usize { const { if true { 1 } else { x } } }", "RST2010"),
            Fail("fn f() { loop { const { break; }; } }", "RST2010"),
            Fail("fn f() -> usize { const { if false { return 1; } 2 } }", "RST2010"),
            Pass("fn f() { let x = const { 1 + 2 }; let y: u8 = x; }"),
            Pass("const fn f(x: usize) -> usize { (const { 1 }) + x } const N: usize = f(2); fn g() -> [u8; N] { [0; 3] }")),
        Group("constant evaluation inline blocks array limits and lexical isolation",
            Pass("fn f() -> [u8; const { let n = 2; n + 1 }] { [0; 3] }"),
            Pass("const A: usize = B + 1; const B: usize = 2; fn f() -> [u8; A] { [0; 3] }"),
            Fail("fn f() -> [u8; 2147483648usize] { [] }", "RST2006"),
            Fail("const N: usize = 0 - 1;", "RST2006"),
            Fail("fn f() -> [u8; const { let n = 2; n }] { let leaked = n; [0; 2] }", "RSN1003")),
    ];

    private static TestCase Group(string name, params Fixture[] fixtures) => new(name, () => RunAsync(fixtures));

    private static Task RunAsync(IReadOnlyList<Fixture> fixtures)
    {
        AssertEx.True(fixtures.Count is > 0 and <= 8, "Constant fixture groups are bounded to eight sources.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var clock = Stopwatch.StartNew();
        for (int index = 0; index < fixtures.Count; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(20), "Constant fixture group exceeded its deadline.");
            Fixture fixture = fixtures[index];
            SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(fixture.Source, "constant-test.rs",
                new() { Timeout = TimeSpan.FromSeconds(5) }, deadline.Token);
            AssertEx.True(syntax.IsSuccessful, $"Fixture {index + 1}: {fixture.Source}\n{Format(syntax.Diagnostics)}");
            SafeCoreHirResult hir = SafeCoreHirLowering.Lower(syntax, new()
            {
                Timeout = TimeSpan.FromSeconds(5), CancellationToken = deadline.Token,
                NameResolution = new() { EnableTypeSystemExtensions = true, Timeout = TimeSpan.FromSeconds(5), CancellationToken = deadline.Token },
            });
            IReadOnlyList<Diagnostic> diagnostics;
            bool success;
            if (!hir.IsSuccessful) { diagnostics = hir.Diagnostics; success = false; }
            else
            {
                SafeCoreTypeAnalysisResult result = SafeCoreTypeAnalysis.Check(hir,
                    new() { Timeout = TimeSpan.FromSeconds(5), MaximumOperations = 200_000 }, deadline.Token);
                diagnostics = result.Diagnostics;
                success = result.IsSuccessful;
            }
            string description = $"Fixture {index + 1}: {fixture.Source}\n{Format(diagnostics)}";
            if (fixture.Diagnostic is null) AssertEx.True(success, description);
            else
            {
                AssertEx.False(success, $"Expected {fixture.Diagnostic}. {description}");
                AssertEx.True(diagnostics.Any(d => d.Code == fixture.Diagnostic), description);
            }
        }
        return Task.CompletedTask;
    }

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(d => $"{d.Code}: {d.Message}"));
}
