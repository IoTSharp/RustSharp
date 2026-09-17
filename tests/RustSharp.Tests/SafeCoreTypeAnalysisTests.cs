using System.Diagnostics;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

/// <summary>
/// Bounded source fixtures cover the monomorphic type profile. These tests
/// deliberately assert structural typing only; borrow checking and execution are later gates.
/// </summary>
internal static class SafeCoreTypeAnalysisTests
{
    private sealed record SourceCase(string Source, string? Diagnostic = null);
    private static SourceCase Pass(string source) => new(source);
    private static SourceCase Fail(string source, string diagnostic = "RST2002") => new(source, diagnostic);

    public static IReadOnlyList<TestCase> All { get; } =
    [
        Group("type analysis primitive signatures and literals",
            Pass("fn f(a: bool, b: char, c: &str) -> bool { a }"),
            Pass("fn f() { let a: u128 = 340282366920938463463374607431768211455; let b: i128 = -170141183460469231731687303715884105728; }"),
            Pass("fn f() -> f64 { 1.25e2 }"),
            Fail("fn f() -> bool { 1 }"),
            Fail("fn f() -> char { 65u8 }")),
        Group("type analysis numeric constraints and operators",
            Pass("fn f(x: u64) -> u64 { let y = 2; x + y }"),
            Pass("fn f() -> i64 { 1i64 << 2u8 }"),
            Pass("fn f(a: bool, b: bool) -> bool { (a & b) | !a }"),
            Fail("fn f() -> i32 { 1i32 + 2u32 }"),
            Fail("fn f() -> bool { true + false }")),
        Group("type analysis numeric range validation",
            Pass("fn f() { let x: i8 = -128; let y: u8 = 255; let z = 0xffu16; }"),
            Fail("fn f() { let x: i8 = 128; }", "RST2006"),
            Fail("fn f() { let x: u8 = 256; }", "RST2006"),
            Fail("fn f() { let x: f32 = 1e100; }", "RST2006"),
            Fail("fn f() { let x = -1u8; }", "RST2006")),
        Group("type analysis tuples and destructuring",
            Pass("fn f() -> (i32, bool) { (1, true) }"),
            Pass("fn f() -> i32 { let (a, (b,)): (i32, (i32,)) = (1, (2,)); a + b }"),
            Pass("fn f(p: &(i32, bool)) -> bool { p.1 }"),
            Fail("fn f() -> (i32,) { 1 }"),
            Fail("fn f() { let (a, b) = (1,); }")),
        Group("type analysis fixed arrays and repetition",
            Pass("fn f() -> [u16; 3] { [1, 2, 3] }"),
            Pass("fn f() -> [bool; 2] { [true; 2] }"),
            Pass("fn f() -> [i32; 0] { [] }"),
            Fail("fn f() -> [i32; 2] { [1, 2, 3] }"),
            Fail("fn f() { let xs = [1, true]; }")),
        Group("type analysis slices and array unsizing",
            Pass("fn first(xs: &[i32]) -> i32 { xs[0] } fn f() -> i32 { first(&[1, 2]) }"),
            Pass("fn f() { let mut xs = [1, 2]; let slice: &mut [i32] = &mut xs; slice[0] = 3; }"),
            Pass("fn f() { let xs: &[i32] = &[]; }"),
            Fail("fn f(xs: [i32]) {}", "RST2005"),
            Fail("fn f() { let xs = [1, 2]; let x = xs[true]; }")),
        Group("type analysis shared and mutable references",
            Pass("fn read(x: &i32) -> i32 { *x } fn f() -> i32 { let mut x = 1; read(&mut x) }"),
            Pass("fn f() { let value = &mut 1; *value = 2; }"),
            Pass("fn f() { let text: &str = \"hello\"; }"),
            Fail("fn f() { let x = 1; let r = &mut x; }", "RST2003"),
            Fail("fn f() { let mut x = 1; let shared = &x; let r: &mut i32 = shared; }")),
        Group("type analysis function items and pointers",
            Pass("fn inc(x: i32) -> i32 { x + 1 } fn f() -> i32 { let op: fn(i32) -> i32 = inc; op(1) }"),
            Pass("fn a() {} fn b() {} fn f(flag: bool) { let op = if flag { a } else { b }; op(); }"),
            Pass("fn make() -> fn(i32) -> i32 { identity } fn identity(x: i32) -> i32 { x }"),
            Fail("fn a(x: i32) {} fn f() { a(); }", "RST2004"),
            Fail("fn a(x: i32) {} fn f() { let op: fn(bool) = a; }")),
        Group("type analysis function coercion and delayed numeric constraints",
            Pass("fn a() {} fn b() {} fn f() { let callbacks = [a, b]; callbacks[0](); }"),
            Pass("fn a() {} fn b() {} fn f(flag: bool) { let callback = loop { if flag { break a; } break b; }; callback(); }"),
            Pass("fn f() { let x = 1; let y = x as i32; let z: u8 = x; }")),
        Group("type analysis named tuple and unit structs",
            Pass("struct Pair { first: i32, second: bool } fn f() -> bool { let p = Pair { first: 1, second: true }; p.second }"),
            Pass("struct Pair(i32, bool); fn f() -> i32 { let p = Pair(1, true); p.0 }"),
            Pass("struct Unit; struct Empty {} fn f() { let u = Unit; let e = Empty {}; }"),
            Fail("struct Pair { x: i32, y: i32 } fn f() { let p = Pair { x: 1 }; }"),
            Fail("struct Pair { x: i32 } fn f() { let p = Pair { x: 1, x: 2 }; }")),
        Group("type analysis enum constructor identities",
            Pass("enum E { A, B(i32), C { x: bool } } fn a() -> E { E::A } fn b() -> E { E::B(1) } fn c() -> E { E::C { x: true } }"),
            Pass("enum E { A(), B {} } fn f() -> E { E::A() } fn g() -> E { E::B {} }"),
            Fail("enum E { A(i32) } fn f() -> E { E::A(true) }"),
            Fail("enum E { A() } fn f() -> E { E::A }"),
            Fail("enum E { A { x: i32 } } fn f() { let e = E::A { x: 1 }; let x = e.x; }")),
        Group("type analysis transparent aliases and nominal ADTs",
            Pass("type Number = i64; type Pair = (Number, bool); fn f() -> Pair { (1, true) }"),
            Pass("struct A(i32); type Alias = A; fn f() -> Alias { A(1) }"),
            Pass("type Callback = fn(i32) -> i32; fn id(x: i32) -> i32 { x } fn f() -> Callback { id }"),
            Fail("struct A(i32); struct B(i32); fn f() -> A { B(1) }"),
            Fail("type A = B; type B = A;", "RST2008")),
        Group("type analysis mutable aggregate places",
            Pass("struct S { x: i32 } fn f() { let mut s = S { x: 1 }; s.x += 2; }"),
            Pass("fn f() { let mut xs = [1, 2]; let r = &mut xs; r[1] = 3; }"),
            Pass("fn f() { let mut p = (1, true); p.0 = 2; }"),
            Fail("fn f() { let xs = [1, 2]; xs[0] = 2; }", "RST2003"),
            Fail("fn f() { let mut xs = [1, 2]; let r = &xs; r[0] = 2; }", "RST2003")),
        Group("type analysis nested reference places and signed literal grouping",
            Fail("fn f(x: &&mut i32) { **x = 1; }", "RST2003"),
            Fail("fn f(x: &&mut [i32; 1]) { (*x)[0] = 1; }", "RST2003"),
            Pass("fn f() { let mut x = 1; let r = &mut x; *r = 2; }"),
            Pass("struct Holder { value: &mut i32 } fn f() { let mut x = 1; let holder = Holder { value: &mut x }; *holder.value = 2; }"),
            Pass("fn f() -> i8 { -((128)) }")),
        Group("type analysis branches return and never coercion",
            Pass("fn diverge() -> ! { loop {} } fn f(flag: bool) -> i32 { if flag { diverge() } else { 1 } }"),
            Pass("fn f() -> i32 { loop { break 3; } }"),
            Pass("fn f(flag: bool) { while flag { continue; } }"),
            Fail("fn f() -> i32 { if true { 1 } else { false } }"),
            Fail("fn f() { break; }")),
        Group("type analysis numeric casts",
            Pass("fn f(x: i32) -> f64 { x as f64 }"),
            Pass("fn f() -> u8 { true as u8 }"),
            Pass("fn f(x: u8) -> char { x as char }"),
            Fail("fn f() -> bool { 1 as bool }"),
            Fail("fn f(x: u32) -> char { x as char }")),
        Group("type analysis finite ADT layouts",
            Pass("struct Node { next: &Node }"),
            Pass("enum Tree { Empty, Link(&Tree) }"),
            Fail("struct Node { next: Node }", "RST2008"),
            Fail("struct A { b: B } struct B { a: A }", "RST2008"),
            Fail("fn f(xs: &[str; 1]) {}", "RST2005")),
        Group("type analysis field visibility and canonical spelling",
            Pass("mod m { pub enum E { V { x: i32 } } } fn f() -> m::E { m::E::V { x: 1 } }"),
            Pass("mod m { pub struct S { pub(crate) x: i32 } } fn f() -> i32 { m::S { x: 1 }.x }"),
            Pass("struct S { r#x: i32 } fn f() -> i32 { S { x: 1 }.x }"),
            Fail("mod m { pub struct S { x: i32 } } fn f() { let s = m::S { x: 1 }; }"),
            Fail("struct S { x: i32 } fn f() { let s = S { x: 1, r#x: 2 }; }")),
        Group("type analysis tuple constructor and member visibility",
            Pass("mod m { pub struct S(pub(crate) i32); } fn f() -> i32 { m::S(1).0 }"),
            Pass("mod m { pub struct S(pub(super) i32); } fn f() -> i32 { m::S(1).0 }"),
            Fail("mod m { pub struct S(i32); } fn f() { let s = m::S(1); }"),
            Fail("mod m { pub struct S(i32); pub fn make() -> S { S(1) } } fn f() -> i32 { m::make().0 }")),
        Group("type analysis local inference and annotation sites",
            Pass("fn f() -> u64 { let x: _ = 1; x }"),
            Pass("fn f() -> (i32, bool) { let x: (_, bool) = (1, true); x }"),
            Pass("fn f() -> [u16; 2] { let x = [1, 2]; x }"),
            Fail("fn f(x: _) {}", "RST2007"),
            Fail("fn f() { let xs = []; }", "RST2007")),
        Group("type analysis explicit profile boundaries",
            Fail("fn f<T>(x: T) -> T { x }", "RST2001"),
            Fail("fn f(x: &'static i32) {}", "RST2001"),
            Pass("const N: usize = 2; fn f() -> [i32; N] { [1, 2] }"),
            Fail("struct S(i32); fn f() { let xs = [S(1); 2]; }", "RST2001"),
            Fail("fn f() { let x: i32; }", "RST2001")),
        Group("type analysis keeps type value namespaces and alias privacy distinct",
            Pass("struct S { x: i32 } fn S() -> i32 { 1 } fn f() { let s: S = S { x: S() }; }"),
            Pass("struct S { x: i32 } const S: i32 = 1; fn f() { let s: S = S { x: S }; }"),
            Pass("mod m { pub struct S { pub x: i32 } pub fn S() -> i32 { 1 } } use m::S; fn f() { let s: S = S { x: S() }; }"),
            Pass("mod m { pub struct S { pub x: i32 } } type A = m::S; fn f() { let s = A { x: 1 }; }"),
            Fail("mod m { struct S { pub x: i32 } pub type A = S; } fn f() { let s = m::A { x: 1 }; }", "RST2002"),
            Fail("mod m { struct S { pub x: i32 } pub type A = S; } fn f(x: m::A) {}", "RST2002"),
            Fail("mod m { struct S { pub x: i32 } pub fn make() -> S { S { x: 1 } } } fn f() { let x = m::make(); }", "RST2002")),
        new("type analysis returns deterministic resolved type and coercion evidence", PreservesEvidenceAsync),
        new("type analysis respects cancellation operation depth and time budgets", PreservesBudgetsAsync),
    ];

    private static TestCase Group(string name, params SourceCase[] sources) => new(name, () => RunCorpusAsync(sources));

    private static Task RunCorpusAsync(IReadOnlyList<SourceCase> sources)
    {
        AssertEx.True(sources.Count is > 0 and <= 8, "Each corpus group has a fixed maximum of eight sources.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var clock = Stopwatch.StartNew();
        for (int index = 0; index < sources.Count; index++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(20), "The source group exceeded its twenty-second deadline.");
            SourceCase source = sources[index];
            SafeCoreHirResult hir = Lower(source.Source, cancellation.Token);
            SafeCoreTypeAnalysisResult result = SafeCoreTypeAnalysis.Check(hir,
                new() { Timeout = TimeSpan.FromSeconds(5) }, cancellation.Token);
            string description = $"Source {index + 1}: {source.Source}\n{Format(result.Diagnostics)}";
            if (source.Diagnostic is null)
                AssertEx.True(result.IsSuccessful, description);
            else
            {
                AssertEx.False(result.IsSuccessful, $"Expected {source.Diagnostic}. {description}");
                AssertEx.True(result.Diagnostics.Any(d => d.Code == source.Diagnostic), description);
                AssertEx.True(result.Diagnostics.All(d => d.Span.Start >= 0 && d.Span.End <= source.Source.Length &&
                    d.SourcePath == "type-analysis.rs"), "Type diagnostics must retain valid source spans and paths.");
            }
        }
        return Task.CompletedTask;
    }

    private static Task PreservesEvidenceAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        const string source = "fn id(x: i32) -> i32 { x } fn f() { let int = 1; let float = 1.5; let ptr: fn(i32) -> i32 = id; let slice: &[i32] = &[1, 2]; }";
        SafeCoreHirResult hir = Lower(source, cancellation.Token);
        SafeCoreTypeAnalysisResult first = SafeCoreTypeAnalysis.Check(hir, cancellationToken: cancellation.Token);
        SafeCoreTypeAnalysisResult second = SafeCoreTypeAnalysis.Check(hir, cancellationToken: cancellation.Token);
        AssertEx.True(first.IsSuccessful, Format(first.Diagnostics));
        AssertEx.True(second.IsSuccessful, Format(second.Diagnostics));
        SafeCoreTypeAnalysisProgram program = first.Program!;
        SafeCoreHirNode integer = hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.IdentifierPattern && n.Name == "int");
        SafeCoreHirNode floating = hir.Nodes.Single(n => n.Kind == SafeCoreHirNodeKind.IdentifierPattern && n.Name == "float");
        AssertEx.Equal(SafeCoreSemanticTypeKind.I32, program.Types[integer.Id].Kind);
        AssertEx.Equal(SafeCoreSemanticTypeKind.F64, program.Types[floating.Id].Kind);
        AssertEx.True(program.Coercions.Values.Any(t => t.Kind == SafeCoreSemanticTypeKind.Function && t.Name is null),
            "Function reification must have explicit pointer coercion evidence.");
        AssertEx.True(program.Coercions.Values.Any(t => t.Kind == SafeCoreSemanticTypeKind.Reference &&
            t.ElementType.Kind == SafeCoreSemanticTypeKind.Slice), "Array unsizing must have explicit slice coercion evidence.");
        AssertEx.Equal(Snapshot(program), Snapshot(second.Program!), "Repeated checking must produce deterministic resolved type evidence.");
        return Task.CompletedTask;
    }

    private static Task PreservesBudgetsAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SafeCoreHirResult hir = Lower("fn f() -> i32 { 1 + 2 }", cancellation.Token);
        SafeCoreTypeAnalysisResult operations = SafeCoreTypeAnalysis.Check(hir, new() { MaximumOperations = 1 }, cancellation.Token);
        AssertEx.True(operations.Diagnostics.Any(d => d.Code == "RST0002"), "The analysis work limit must fail predictably.");
        SafeCoreTypeAnalysisResult depth = SafeCoreTypeAnalysis.Check(hir, new() { MaximumNestingDepth = 1 }, cancellation.Token);
        AssertEx.True(depth.Diagnostics.Any(d => d.Code == "RST0002"), "The analysis depth limit must fail predictably.");
        SafeCoreTypeAnalysisResult timeout = SafeCoreTypeAnalysis.Check(hir, new() { Timeout = TimeSpan.FromTicks(1) }, cancellation.Token);
        AssertEx.True(timeout.Diagnostics.Any(d => d.Code == "RST0002"), "The analysis time budget must fail predictably.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreTypeAnalysis.Check(hir, cancellationToken: cancelled.Token));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => SafeCoreTypeAnalysis.Check(hir, new() { Timeout = TimeSpan.Zero }));
        return Task.CompletedTask;
    }

    private static SafeCoreHirResult Lower(string source, CancellationToken cancellationToken)
    {
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "type-analysis.rs",
            new() { Timeout = TimeSpan.FromSeconds(5) }, cancellationToken);
        AssertEx.True(syntax.IsSuccessful, $"Syntax: {source}\n{Format(syntax.Diagnostics)}");
        SafeCoreHirResult hir = SafeCoreHirLowering.Lower(syntax, new()
        {
            Timeout = TimeSpan.FromSeconds(5), CancellationToken = cancellationToken,
            NameResolution = new() { EnableTypeSystemExtensions = true, Timeout = TimeSpan.FromSeconds(5) },
        });
        AssertEx.True(hir.IsSuccessful, $"HIR: {source}\n{Format(hir.Diagnostics)}");
        return hir;
    }

    private static string Snapshot(SafeCoreTypeAnalysisProgram program) => string.Join("\n",
        program.Types.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}={entry.Value}"));

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(d => $"{d.Code}: {d.Message}"));
}
