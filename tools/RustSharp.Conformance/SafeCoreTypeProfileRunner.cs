using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RustSharp.Compiler;

namespace RustSharp.Conformance;

/// <summary>A bounded, metadata-only rustc differential gate for the monomorphic type profile.</summary>
internal static class SafeCoreTypeProfileRunner
{
    internal const string ProfileName = "safe-core-types-v1";
    internal const int CatalogVersion = 2;
    private const int MaximumCases = 128;
    private const int MaximumFixtureSourceLength = 65_536;
    internal static IReadOnlyList<string> RequiredCategories { get; } = Array.AsReadOnly(new[]
    {
        "primitives", "tuples", "arrays", "slices", "references", "functions", "adts", "never",
        "inference", "coercions", "patterns", "match", "closures", "const", "aliases", "layout",
    });
    private static readonly Dictionary<string, string[]> RequiredCategoryCases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["primitives"] = ["signed-primitives", "unsigned-primitives", "bool-char-str-unit", "floating-primitives", "integer-range", "float-range"],
            ["tuples"] = ["tuple-destructuring", "tuple-reference-member", "tuple-arity"],
            ["arrays"] = ["fixed-array", "array-repetition", "empty-array", "array-length"],
            ["slices"] = ["mutable-slice", "unsized-parameter"],
            ["references"] = ["reference-weakening", "mutable-place", "shared-string-reference"],
            ["functions"] = ["function-pointer", "function-branch-coercion", "function-arity"],
            ["adts"] = ["struct-families", "enum-constructors", "nominal-mismatch"],
            ["never"] = ["never-coercion", "loop-result", "never-result-mismatch"],
            ["inference"] = ["numeric-inference", "local-inferred-annotation", "unresolved-inference"],
            ["coercions"] = ["array-slice-coercion", "dereference-coercion", "mutable-reborrow-coercion", "shared-boundary-coercion"],
            ["patterns"] = ["pattern-reference-ergonomics", "pattern-reference-explicit", "pattern-refutable-local", "pattern-struct-rest", "pattern-let-else", "pattern-refmut-immutable"],
            ["match"] = ["match-bool-exhaustive", "match-enum-exhaustive", "match-slice-exhaustive", "match-bool-nonexhaustive", "match-range-exhaustive", "match-or-pattern", "match-string-literal", "match-float-literal", "match-character-range"],
            ["closures"] = ["closure-local-inference", "closure-function-pointer", "closure-shared-capture", "closure-mutable-capture", "closure-capture-pointer", "closure-move-signature", "closure-branch-coercion"],
            ["const"] = ["const-arithmetic-length", "const-function-length", "const-block-local", "const-nonconst-call", "const-overflow", "const-cycle", "const-unused-nonconst-call", "const-dead-nonconst-call", "const-match-length", "const-while-length", "const-array-index", "const-tuple-struct-member"],
            ["aliases"] = ["transparent-alias", "named-alias-constructor", "enum-alias-constructor", "alias-cycle"],
            ["layout"] = ["recursive-layout", "function-indirected-layout", "unsized-field"],
        };
    private static readonly HashSet<string> KnownFailureCodes = new(StringComparer.Ordinal)
    {
        "RST2002", "RST2003", "RST2004", "RST2005", "RST2006", "RST2007", "RST2008",
        "RST2009", "RST2010", "RST2011", "RST2012",
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<BoundedProcessTermination>() },
    };

    internal sealed record Fixture(string Id, string Source, bool ExpectedSuccess,
        string? ExpectedDiagnosticCode = null, string? ExpectedDiagnosticText = null)
    {
        public string Category { get; init; } = string.Empty;
        public int? ExpectedDiagnosticStart { get; init; }
    }

    // Library inputs check types and metadata without executable artifacts or runtime dependencies.
    internal static IReadOnlyList<Fixture> Catalog { get; } = Array.AsReadOnly(new Fixture[]
    {
        Pass("signed-primitives", "fn f() { let a: i8 = -128; let b: i16 = -32768; let c: i32 = -2147483648; let d: i64 = -9223372036854775808; let e: i128 = -170141183460469231731687303715884105728; let n: isize = 1; }"),
        Pass("unsigned-primitives", "fn f() { let a: u8 = 255; let b: u16 = 65535; let c: u32 = 4294967295; let d: u64 = 18446744073709551615; let e: u128 = 340282366920938463463374607431768211455; let n: usize = 1; }"),
        Pass("bool-char-str-unit", "fn f(flag: bool, text: &str) { let letter: char = '界'; let empty: () = (); let value = !flag; }"),
        Pass("floating-primitives", "fn f() -> f32 { let a: f64 = 1.25e2; let b = 1.5f32; b + 2.0 }"),
        Pass("numeric-inference", "fn f(x: u64) -> u64 { let y = 2; x + y }"),
        Pass("tuple-destructuring", "fn f() -> i32 { let (a, (b,)): (i32, (i32,)) = (1, (2,)); a + b }"),
        Pass("tuple-reference-member", "fn f(p: &(i32, bool)) -> bool { p.1 }"),
        Pass("fixed-array", "fn f() -> [u16; 3] { [1, 2, 3] }"),
        Pass("array-repetition", "fn f() -> [bool; 2] { [true; 2] }"),
        Pass("empty-array", "fn f() -> [i32; 0] { [] }"),
        Pass("array-slice-coercion", "fn first(xs: &[i32]) -> i32 { xs[0] } fn f() -> i32 { first(&[1, 2]) }"),
        Pass("mutable-slice", "fn f() { let mut xs = [1, 2]; let slice: &mut [i32] = &mut xs; slice[0] = 3; }"),
        Pass("reference-weakening", "fn read(x: &i32) -> i32 { *x } fn f() -> i32 { let mut x = 1; read(&mut x) }"),
        Pass("function-pointer", "fn inc(x: i32) -> i32 { x + 1 } fn f() -> i32 { let op: fn(i32) -> i32 = inc; op(1) }"),
        Pass("function-branch-coercion", "fn a() {} fn b() {} fn f(flag: bool) { let op = if flag { a } else { b }; op(); }"),
        Pass("struct-families", "struct Pair { left: i32, right: bool } struct Tuple(i32); struct Unit; fn f() -> i32 { let p = Pair { left: 1, right: true }; let t = Tuple(p.left); let u = Unit; t.0 }"),
        Pass("enum-constructors", "enum E { A, B(i32), C { x: bool } } fn a() -> E { E::A } fn b() -> E { E::B(1) } fn c() -> E { E::C { x: true } }"),
        Pass("transparent-alias", "type Number = i64; type Pair = (Number, bool); fn f() -> Pair { (1, true) }"),
        Pass("never-coercion", "fn diverge() -> ! { loop {} } fn f(flag: bool) -> i32 { if flag { diverge() } else { 1 } }"),
        Pass("loop-result", "fn f() -> i32 { loop { break 3; } }"),
        Pass("numeric-cast", "fn f(x: i32) -> f64 { x as f64 }"),
        Pass("local-inferred-annotation", "fn f() -> (i32, bool) { let x: (_, bool) = (1, true); x }"),
        Fail("return-mismatch", "fn f() -> bool { 1 }", "RST2002", "1"),
        Fail("numeric-mismatch", "fn f() -> i32 { 1i32 + 2u32 }", "RST2002", "1i32 + 2u32"),
        Fail("integer-range", "fn f() { let x: u8 = 256; }", "RST2006", "256"),
        Fail("float-range", "fn f() { let x: f32 = 1e100; }", "RST2006", "1e100"),
        Fail("tuple-arity", "fn f() -> (i32,) { 1 }", "RST2002", "1"),
        Fail("array-length", "fn f() -> [i32; 2] { [1, 2, 3] }", "RST2002", "[1, 2, 3]"),
        Fail("unsized-parameter", "fn f(xs: [i32]) {}", "RST2005", "xs: [i32]"),
        Fail("mutable-place", "fn f() { let x = 1; let r = &mut x; }", "RST2003", "x"),
        Fail("reference-strengthening", "fn f() { let mut x = 1; let shared = &x; let r: &mut i32 = shared; }", "RST2002", "shared"),
        Fail("function-arity", "fn g(x: i32) {} fn f() { g(); }", "RST2004", "g()"),
        Fail("nominal-mismatch", "struct A(i32); struct B(i32); fn f() -> A { B(1) }", "RST2002", "B(1)"),
        Fail("alias-cycle", "type A = B; type B = A;", "RST2008", "type A = B;"),
        Fail("recursive-layout", "struct Node { next: Node }", "RST2008", "next: Node"),
        Fail("unresolved-inference", "fn f() { let xs = []; }", "RST2007", "[]"),
        Pass("never-return-branch", "fn f(flag: bool) -> i32 { if flag { return 1; } else { 2 } }", "never"),
        Fail("never-result-mismatch", "fn f() -> ! { 1 }", "RST2002", "1", "never"),
        Pass("function-indirected-layout", "struct Node { next: fn() -> Node }", "layout"),
        Fail("unsized-field", "struct S { value: str, tail: u8 }", "RST2005", "value: str", "layout"),
        Pass("shared-string-reference", "fn f(text: &str) { let view: &str = text; }", "references"),
        Fail("shared-reference-write", "fn f(value: &i32) { *value = 2; }", "RST2003", "*value", "references"),
        Pass("dereference-coercion", "fn f(value: &&i32) -> i32 { let view: &i32 = value; *view }", "coercions"),
        Pass("mutable-reborrow-coercion", "fn f(value: &mut &mut i32) { let view: &mut i32 = value; *view = 2; }", "coercions"),
        Pass("combined-unsizing-coercion", "fn f() { let mut values = [1, 2]; let view: &[i32] = &mut values; }", "coercions"),
        Fail("shared-boundary-coercion", "fn f(value: &&mut i32) { let view: &mut i32 = value; }", "RST2002", "value", "coercions"),
        Fail("implicit-numeric-widening", "fn f() { let value: i32 = 1; let wide: i64 = value; }", "RST2002", "value", "coercions"),
        Pass("named-alias-constructor", "struct Point { x: i32 } type Alias = Point; fn f() -> Alias { Alias { x: 1 } }", "aliases"),
        Pass("enum-alias-constructor", "enum Choice { Empty, Value(i32) } type Alias = Choice; fn f() -> Alias { Alias::Value(1) }", "aliases"),
        Fail("alias-field-mismatch", "struct Point { x: i32 } type Alias = Point; fn f() -> Alias { Alias { x: true } }", "RST2002", "true", "aliases"),
        Pass("pattern-reference-ergonomics", "fn f(pair: &(i32, bool)) -> i32 { let (value, _) = pair; *value }", "patterns"),
        Pass("pattern-reference-explicit", "fn f(value: &i32) -> i32 { let &copied = value; copied }", "patterns"),
        Fail("pattern-type-mismatch", "fn f() { let (a, b) = 1; }", "RST2002", "(a, b)", "patterns"),
        Fail("pattern-refutable-local", "fn f(value: bool) { let true = value; }", "RST2011", "true", "patterns"),
        Pass("match-bool-exhaustive", "fn f(value: bool) -> i32 { match value { true => 1, false => 2 } }", "match"),
        Pass("match-enum-exhaustive", "enum E { A(bool), B } fn f(value: E) -> i32 { match value { E::A(true) => 1, E::A(false) => 2, E::B => 3 } }", "match"),
        Pass("match-slice-exhaustive", "fn f(values: &[i32]) -> i32 { match values { [] => 0, [head, ..] => *head } }", "match"),
        Fail("match-bool-nonexhaustive", "fn f(value: bool) -> i32 { match value { true => 1 } }", "RST2009", "match value { true => 1 }", "match"),
        Fail("match-arm-type-mismatch", "fn f(value: bool) -> i32 { match value { true => 1, false => false } }", "RST2002", "false", "match"),
        Pass("closure-local-inference", "fn f() -> i32 { let inc = |x| x + 1; inc(2) }", "closures"),
        Pass("closure-function-pointer", "fn f() -> i32 { let op: fn(i32) -> i32 = |x| x + 1; op(2) }", "closures"),
        Pass("closure-shared-capture", "fn f() -> i32 { let base = 2; let add = |x| x + base; add(3) }", "closures"),
        Pass("closure-mutable-capture", "fn f() { let mut n = 0; let mut add = || { n += 1; }; add(); }", "closures"),
        Fail("closure-capture-pointer", "fn f() { let base = 2; let op: fn(i32) -> i32 = |x| x + base; }", "RST2002", "|x| x + base", "closures"),
        Fail("closure-mutable-receiver", "fn f() { let mut n = 0; let add = || { n += 1; }; add(); }", "RST2003", "add", "closures"),
        Pass("const-arithmetic-length", "const N: usize = 1 + 2; fn f() -> [i32; N] { [1; N] }", "const"),
        Pass("const-function-length", "const fn twice(n: usize) -> usize { n * 2 } fn f() -> [bool; twice(2)] { [true; twice(2)] }", "const"),
        Pass("const-block-local", "const N: usize = { let base = 2; base + 1 }; fn f() -> [u8; N] { [0; N] }", "const"),
        Pass("const-conditional", "const N: usize = if true { 2 } else { 3 }; fn f() -> [u8; N] { [0; N] }", "const"),
        Pass("const-dependency-chain", "const BASE: usize = 2; const N: usize = BASE + 1; fn f() -> [u8; N] { [0; N] }", "const"),
        Fail("const-nonconst-call", "fn size() -> usize { 2 } fn f() -> [u8; size()] { [0; 2] }", "RST2010", "size()", "const"),
        Fail("const-divide-zero", "const N: usize = 1 / 0; fn f() -> [u8; N] { [] }", "RST2006", "1 / 0", "const"),
        Fail("const-overflow", "const N: u8 = 255u8 + 1u8;", "RST2006", "255u8 + 1u8", "const"),
        Fail("const-cycle", "const A: usize = B; const B: usize = A;", "RST2008", "const A: usize = B;", "const"),
        Pass("pattern-struct-rest", "struct S { x: i32, y: bool } fn f(value: S) -> i32 { let S { x, .. } = value; x }", "patterns"),
        Pass("pattern-let-else", "fn f(values: &[i32]) -> i32 { let [head, ..] = values else { return 0; }; *head }", "patterns"),
        Fail("pattern-let-else-unit", "fn f(values: &[i32]) { let [head, ..] = values else {}; }", "RST2002", "{}", "patterns"),
        Fail("pattern-refmut-immutable", "fn f() { let value = 1; let ref mut view = value; }", "RST2003", "value", "patterns"),
        Pass("match-range-exhaustive", "fn f(value: u8) -> i32 { match value { 0..=127 => 1, 128..=255 => 2 } }", "match"),
        Pass("match-or-pattern", "fn f(pair: (bool, bool)) -> i32 { match pair { (true, _) | (false, true) => 1, (false, false) => 0 } }", "match"),
        Pass("match-guard-fallback", "fn f(value: bool) -> i32 { match value { true if value => 1, _ => 0 } }", "match"),
        Pass("match-at-binding", "fn f(value: i32) -> i32 { match value { x @ 0..=10 => x, _ => 0 } }", "match"),
        Fail("match-guard-incomplete", "fn f(value: bool) -> i32 { match value { true if value => 1, false => 0 } }", "RST2009", "match value { true if value => 1, false => 0 }", "match"),
        Fail("match-or-binding-type", "fn f(pair: (i32, bool)) { match pair { (x, _) | (_, x) => () } }", "RST2002", "x", "match"),
        Pass("match-string-literal", "fn f(value: &str) -> bool { match value { \"yes\" => true, _ => false } }", "match"),
        Pass("match-float-literal", "fn f(value: f64) -> bool { match value { 0.0 => true, _ => false } }", "match"),
        Pass("match-character-range", "fn f(value: char) -> bool { match value { 'a'..='z' => true, _ => false } }", "match"),
        Pass("closure-move-signature", "fn f() -> i32 { let base = 2; let add = move |x: i32| -> i32 { x + base }; add(3) }", "closures"),
        Pass("closure-branch-coercion", "fn f(flag: bool) -> i32 { let op = if flag { |x: i32| x + 1 } else { |x: i32| x - 1 }; op(3) }", "closures"),
        Fail("const-unused-nonconst-call", "fn value() -> i32 { 1 } const fn unused() -> i32 { value() }", "RST2010", "value()", "const"),
        Fail("const-dead-nonconst-call", "fn size() -> usize { 1 } const N: usize = if true { 2 } else { size() }; fn f() -> [u8; N] { [0; N] }", "RST2010", "size()", "const"),
        Pass("const-boolean-ordering", "const N: usize = if false < true { 2 } else { 1 }; fn f() -> [u8; N] { [0; N] }", "const"),
        Pass("const-match-length", "const N: usize = match true { true => 2, false => 1 }; fn f() -> [u8; N] { [0; N] }", "const"),
        Pass("const-while-length", "const fn size() -> usize { let mut n = 0; while n < 2 { n += 1; } n } fn f() -> [u8; size()] { [0; size()] }", "const"),
        Pass("const-array-index", "const N: usize = [1, 2, 3][1]; fn f() -> [u8; N] { [0; N] }", "const"),
        Pass("const-tuple-struct-member", "struct Size(usize); const N: usize = Size(2).0; fn f() -> [u8; N] { [0; N] }", "const"),
    });

    internal static void ValidateCatalog(IReadOnlyList<Fixture> catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        if (catalog.Count is < 1 or > MaximumCases) throw new ArgumentException("Type fixture count exceeds the 1..128 bound.", nameof(catalog));
        var clock = Stopwatch.StartNew();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Fixture fixture in catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Type catalog validation exceeded five seconds.");
            if (fixture is null) throw new ArgumentException("A fixture cannot be null.", nameof(catalog));
            if (string.IsNullOrEmpty(fixture.Id) || fixture.Id.Length > 96 ||
                !fixture.Id.All(static character => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character == '-') ||
                !ids.Add(fixture.Id)) throw new ArgumentException("Fixture IDs must be unique, bounded and safe file names.", nameof(catalog));
            if (string.IsNullOrWhiteSpace(fixture.Source) || fixture.Source.Length > MaximumFixtureSourceLength)
                throw new ArgumentException("Fixture source is empty or exceeds its size bound.", nameof(catalog));
            if (!RequiredCategories.Contains(fixture.Category, StringComparer.Ordinal))
                throw new ArgumentException("Fixture names an unknown type category.", nameof(catalog));
            if (fixture.ExpectedSuccess && (fixture.ExpectedDiagnosticCode is not null || fixture.ExpectedDiagnosticText is not null || fixture.ExpectedDiagnosticStart is not null) ||
                !fixture.ExpectedSuccess && (fixture.ExpectedDiagnosticCode is null || !KnownFailureCodes.Contains(fixture.ExpectedDiagnosticCode) ||
                    fixture.ExpectedDiagnosticText is null || fixture.ExpectedDiagnosticStart is null))
                throw new ArgumentException("A fixture must declare a valid type outcome and diagnostic code.", nameof(catalog));
            if (fixture.ExpectedDiagnosticText is not null && (fixture.ExpectedDiagnosticText.Length == 0 ||
                !fixture.Source.Contains(fixture.ExpectedDiagnosticText, StringComparison.Ordinal)))
                throw new ArgumentException("Expected diagnostic text must occur in the source.", nameof(catalog));
            if (fixture.ExpectedDiagnosticStart is int start && (start < 0 ||
                start > fixture.Source.Length - fixture.ExpectedDiagnosticText!.Length ||
                fixture.Source.Substring(start, fixture.ExpectedDiagnosticText.Length) != fixture.ExpectedDiagnosticText))
                throw new ArgumentException("Expected diagnostic start must locate the declared source text.", nameof(catalog));
        }
        foreach (string category in RequiredCategories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Type catalog validation exceeded five seconds.");
            if (!catalog.Any(fixture => fixture.Category == category && fixture.ExpectedSuccess) ||
                !catalog.Any(fixture => fixture.Category == category && !fixture.ExpectedSuccess))
                throw new ArgumentException($"Required category '{category}' needs compile-pass and compile-fail fixtures.", nameof(catalog));
            foreach (string requiredId in RequiredCategoryCases[category])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Type catalog validation exceeded five seconds.");
                if (!catalog.Any(fixture => fixture.Id == requiredId && fixture.Category == category))
                    throw new ArgumentException($"Required fixture '{requiredId}' is missing from category '{category}'.", nameof(catalog));
            }
        }
    }

    internal static bool Matches(Fixture fixture, CompilationResult result)
    {
        if (result.Success != fixture.ExpectedSuccess || result.Output is not null) return false;
        if (fixture.ExpectedSuccess) return result.Diagnostics.Count == 0;
        return result.Diagnostics.Count != 0 && result.Diagnostics.All(diagnostic =>
            diagnostic.Code == fixture.ExpectedDiagnosticCode && diagnostic.Span.Start >= 0 &&
            diagnostic.Span.Start == fixture.ExpectedDiagnosticStart &&
            diagnostic.Span.End <= fixture.Source.Length && diagnostic.Span.Length > 0 &&
            (fixture.ExpectedDiagnosticText is null ||
                fixture.Source.Substring(diagnostic.Span.Start, diagnostic.Span.Length) == fixture.ExpectedDiagnosticText));
    }

    public static async Task<int> RunAsync(string repositoryRoot, string reportPath,
        TimeSpan timeout, TimeSpan deadline, DateTimeOffset startedAtUtc, Stopwatch harnessClock)
    {
        ValidateCatalog(Catalog);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30) ||
            deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(180))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Type conformance requires a case timeout <=30s and overall deadline <=180s.");
        string reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportDirectory);
        string runDirectory = Path.Combine(reportDirectory, $".run-types-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        using var cancellation = new CancellationTokenSource(deadline);
        ConsoleCancelEventHandler cancelHandler = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        var cases = new List<CaseReport>(Catalog.Count);
        ProcessProbe? versionProbe = null;
        string? blockedReason = null;
        string? cleanupDiagnostic = null;
        bool oracleAvailable = false;
        try
        {
            var runner = new BoundedProcessRunner();
            versionProbe = await ProbeAsync(runner, ["+1.98.0", "--version"], repositoryRoot,
                timeout, cancellation.Token).ConfigureAwait(false);
            oracleAvailable = versionProbe.Result is { } version && CleanExit(version) && version.ExitCode == 0 &&
                version.StandardOutput.StartsWith("rustc 1.98.0", StringComparison.Ordinal);
            if (!oracleAvailable) blockedReason = versionProbe.Error ?? "The rustc +1.98.0 oracle is unavailable or reports a different version.";
            foreach (Fixture fixture in Catalog)
            {
                if (!oracleAvailable || cancellation.IsCancellationRequested)
                {
                    cases.Add(new(fixture.Id, fixture.ExpectedSuccess ? "compile-pass" : "compile-fail", "skipped",
                        blockedReason ?? "The overall deadline expired or execution was cancelled.", SourceHash(fixture),
                        fixture.ExpectedDiagnosticCode, fixture.ExpectedDiagnosticText, null, [], null)
                        { Category = fixture.Category, ExpectedDiagnosticStart = fixture.ExpectedDiagnosticStart });
                    continue;
                }
                cases.Add(await RunCaseAsync(runner, runDirectory, fixture, timeout, cancellation.Token).ConfigureAwait(false));
                Console.Error.WriteLine($"Type conformance {cases.Count}/{Catalog.Count}: {fixture.Id} {cases[^1].Status}.");
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            cleanupDiagnostic = await CleanupAsync(runDirectory, reportDirectory).ConfigureAwait(false);
        }

        int passed = cases.Count(static result => result.Status == "passed");
        int failed = cases.Count(static result => result.Status == "failed");
        int skipped = cases.Count(static result => result.Status == "skipped");
        string status = ReportStatus(Catalog.Count, passed, failed, skipped, oracleAvailable,
            cancellation.IsCancellationRequested, cleanupDiagnostic);
        var report = new
        {
            SchemaVersion = 2, Profile = ProfileName, CatalogVersion, CatalogValidated = true,
            RequiredCategories,
            CategoryCoverage = RequiredCategories.Select(category => new
            {
                Category = category,
                RequiredCases = RequiredCategoryCases[category],
                Cases = Catalog.Where(fixture => fixture.Category == category).Select(static fixture => fixture.Id).ToArray(),
                CompilePass = Catalog.Count(fixture => fixture.Category == category && fixture.ExpectedSuccess),
                CompileFail = Catalog.Count(fixture => fixture.Category == category && !fixture.ExpectedSuccess),
            }).ToArray(),
            GeneratedAtUtc = DateTimeOffset.UtcNow, StartedAtUtc = startedAtUtc,
            ElapsedMilliseconds = harnessClock.Elapsed.TotalMilliseconds,
            Host = new { Runtime = Environment.Version.ToString(), RuntimeInformation.RuntimeIdentifier,
                RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString() },
            Oracle = new { Requested = "rustc-1.98", Toolchain = "1.98.0", Available = oracleAvailable, VersionProbe = versionProbe },
            Compiler = new { Api = "CompilerDriver.Check", Profile = nameof(CompilationProfile.SafeCoreTypes),
                Version = typeof(CompilerDriver).Assembly.GetName().Version?.ToString() },
            Limits = new { MaximumCases, TimeoutSeconds = timeout.TotalSeconds, DeadlineSeconds = deadline.TotalSeconds,
                MaximumOutputBytes = BoundedProcessRunner.MaximumTotalOutputBytes },
            Summary = new { Status = status, Denominator = Catalog.Count, Total = Catalog.Count,
                Executed = cases.Count - skipped, Passed = passed, Failed = failed, Skipped = skipped },
            BlockedReason = status == "blocked" ? blockedReason ?? "Execution did not finish every declared fixture." : null,
            CancelledOrDeadlineExpired = cancellation.IsCancellationRequested, Cases = cases,
            RunDirectoryCleanupDiagnostic = cleanupDiagnostic,
        };
        string temporaryReport = reportPath + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        using var reportDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using (var stream = new FileStream(temporaryReport, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions, reportDeadline.Token).ConfigureAwait(false);
            File.Move(temporaryReport, reportPath, overwrite: true);
        }
        finally { if (File.Exists(temporaryReport)) File.Delete(temporaryReport); }
        Console.WriteLine($"Type conformance: {status}; {passed}/{Catalog.Count} passed, {failed} failed, {skipped} skipped. Report: {reportPath}");
        return status == "passed" ? 0 : status == "blocked" ? 2 : 1;
    }

    private static async Task<CaseReport> RunCaseAsync(BoundedProcessRunner runner, string directory,
        Fixture fixture, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var caseDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        caseDeadline.CancelAfter(timeout);
        string sourcePath = Path.Combine(directory, fixture.Id + ".rs");
        string metadataPath = Path.Combine(directory, fixture.Id + ".rmeta");
        CompilationResult? actual = null;
        ProcessProbe? oracle = null;
        string? difference = null;
        IReadOnlyList<DiagnosticEvidence> diagnostics = [];
        try
        {
            await File.WriteAllTextAsync(sourcePath, fixture.Source, caseDeadline.Token).ConfigureAwait(false);
            actual = CompilerDriver.Check(fixture.Source, sourcePath, CompilationProfile.SafeCoreTypes, caseDeadline.Token);
            diagnostics = actual.Diagnostics.Select(diagnostic => new DiagnosticEvidence(diagnostic.Code,
                diagnostic.Span.Start, diagnostic.Span.Length, diagnostic.SourcePath,
                diagnostic.Span.Start >= 0 && diagnostic.Span.End <= fixture.Source.Length
                    ? fixture.Source.Substring(diagnostic.Span.Start, diagnostic.Span.Length) : null)).ToArray();
            oracle = await ProbeAsync(runner,
                ["+1.98.0", "--edition=2024", "--crate-type=lib", "--emit=metadata", "--crate-name", "type_fixture",
                    sourcePath, "-o", metadataPath], directory, timeout, caseDeadline.Token).ConfigureAwait(false);
            if (!Matches(fixture, actual)) difference = "RustSharp outcome, diagnostic code or diagnostic span differs from the declared fixture.";
            else if (oracle.Result is not { } process || !CleanExit(process))
                difference = oracle.Error ?? "The oracle process did not exit cleanly within its bounds.";
            else if (process.ExitCode != (fixture.ExpectedSuccess ? 0 : 1))
                difference = "rustc outcome differs from the declared fixture and RustSharp outcome.";
            else if (fixture.ExpectedSuccess && !File.Exists(metadataPath))
                difference = "rustc reported success without producing metadata.";
            else if (!fixture.ExpectedSuccess && !process.StandardError.Contains("error", StringComparison.Ordinal))
                difference = "rustc failed without a compiler error diagnostic.";
        }
        catch (OperationCanceledException) { difference = "The case or overall deadline expired, or execution was cancelled."; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        { difference = exception.Message; }
        return new(fixture.Id, fixture.ExpectedSuccess ? "compile-pass" : "compile-fail",
            difference is null ? "passed" : "failed", difference, SourceHash(fixture), fixture.ExpectedDiagnosticCode,
            fixture.ExpectedDiagnosticText, actual?.Success, diagnostics, oracle)
            { Category = fixture.Category, ExpectedDiagnosticStart = fixture.ExpectedDiagnosticStart };
    }

    internal static string ReportStatus(int total, int passed, int failed, int skipped, bool oracleAvailable,
        bool cancelledOrDeadlineExpired, string? cleanupDiagnostic)
    {
        if (total is < 1 or > MaximumCases || passed < 0 || failed < 0 || skipped < 0 ||
            (long)passed + failed + skipped != total) return "failed";
        if (!oracleAvailable || skipped != 0 || cancelledOrDeadlineExpired) return "blocked";
        return failed != 0 || cleanupDiagnostic is not null ? "failed" : "passed";
    }

    private static async Task<ProcessProbe> ProbeAsync(BoundedProcessRunner runner, IReadOnlyList<string> arguments,
        string directory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            BoundedProcessResult result = await runner.RunAsync(new("rustc", arguments, directory, timeout,
                static process => Console.Error.WriteLine($"Started PID {process.ProcessId} at {process.StartedAt:O}; " +
                    $"parent PID {process.ParentProcessId}; command: {process.CommandLine}")), cancellationToken).ConfigureAwait(false);
            return new(result, null);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or OperationCanceledException)
        { return new(null, exception.Message); }
    }

    private static bool CleanExit(BoundedProcessResult result) => result.Termination == BoundedProcessTermination.Exited &&
        !result.OutputTruncated && !result.OutputReadTimedOut && !result.OutputDrainTimedOut &&
        !result.OutputReadLimitReached && !result.ProcessTreeCleanupIncomplete;

    private static async Task<string?> CleanupAsync(string directory, string parent)
    {
        string resolved = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.GetFullPath(parent), StringComparison.Ordinal) ||
            !Path.GetFileName(resolved).StartsWith($".run-types-{Environment.ProcessId}-", StringComparison.Ordinal))
            return "Cleanup ownership verification failed.";
        var clock = Stopwatch.StartNew();
        string? diagnostic = null;
        for (int attempt = 0; attempt < 8 && clock.Elapsed < TimeSpan.FromSeconds(5); attempt++)
        {
            try { if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true); return null; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { diagnostic = exception.Message; }
            await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false);
        }
        return "Owned run directory could not be removed: " + diagnostic;
    }

    private static string SourceHash(Fixture fixture) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Source)));
    private static Fixture Pass(string id, string source, string? category = null) =>
        new(id, source, true) { Category = category ?? BaselineCategory(id) };
    private static Fixture Fail(string id, string source, string code, string? text = null, string? category = null) =>
        new(id, source, false, code, text)
        {
            Category = category ?? BaselineCategory(id),
            ExpectedDiagnosticStart = text is null ? null : source.LastIndexOf(text, StringComparison.Ordinal),
        };
    private static string BaselineCategory(string id) => id switch
    {
        "signed-primitives" or "unsigned-primitives" or "bool-char-str-unit" or "floating-primitives" or
            "return-mismatch" or "numeric-mismatch" or "integer-range" or "float-range" => "primitives",
        "tuple-destructuring" or "tuple-reference-member" or "tuple-arity" => "tuples",
        "fixed-array" or "array-repetition" or "empty-array" or "array-length" => "arrays",
        "mutable-slice" or "unsized-parameter" => "slices",
        "reference-weakening" or "mutable-place" => "references",
        "function-pointer" or "function-branch-coercion" or "function-arity" => "functions",
        "struct-families" or "enum-constructors" or "nominal-mismatch" => "adts",
        "never-coercion" or "loop-result" => "never",
        "numeric-inference" or "local-inferred-annotation" or "unresolved-inference" => "inference",
        "array-slice-coercion" or "numeric-cast" or "reference-strengthening" => "coercions",
        "transparent-alias" or "alias-cycle" => "aliases",
        "recursive-layout" => "layout",
        _ => throw new ArgumentException("New fixtures must declare an explicit category.", nameof(id)),
    };
    private sealed record ProcessProbe(BoundedProcessResult? Result, string? Error);
    private sealed record DiagnosticEvidence(string Code, int Start, int Length, string? SourcePath, string? SourceText);
    private sealed record CaseReport(string Id, string Kind, string Status, string? Difference, string SourceSha256,
        string? ExpectedDiagnosticCode, string? ExpectedDiagnosticText, bool? RustSharpSuccess,
        IReadOnlyList<DiagnosticEvidence> RustSharpDiagnostics, ProcessProbe? Rustc)
    {
        public string Category { get; init; } = string.Empty;
        public int? ExpectedDiagnosticStart { get; init; }
    }
}
