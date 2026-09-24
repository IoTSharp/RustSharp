using RustSharp.Semantics;
using RustSharp.Syntax;
using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class SafeCoreMirPatternExecutionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR v2 lowers scalar tuple match and guard through CFG", MatchAsync),
        new("MIR v2 expands a captured closure call into typed MIR", ClosureAsync),
        new("MIR v2 rejects closure escape at its source span", ClosureEscapeAsync),
        new("MIR v2 shared closure captures borrow mutable bindings", MutableCaptureAsync),
        new("MIR v2 binding or-patterns select consistent locals", BindingOrPatternAsync),
        new("MIR v2 bounds pattern alternatives", PatternBudgetAsync),
        new("MIR v2 executes match and closure through CoreCLR", CoreClrAsync),
        new("MIR patterns select nested or bindings and retry guards", OrGuardsAsync),
        new("MIR patterns execute let else and exhaustive or declarations", LetElseAsync),
        new("MIR patterns bind tuple struct and array rests", RestsAsync),
        new("MIR patterns mutate original tuple and ADT fields", RefBindingsAsync),
        new("MIR patterns compare negative literals ranges and named constants", RangesAsync),
        new("MIR patterns inspect slices only after successful length tests", SlicesAsync),
        new("MIR patterns borrow fixed array rest views and write full arrays", ArrayRestViewAsync),
        new("MIR patterns borrow disjoint mutable array fields and rests", DisjointRestAsync),
        new("MIR patterns borrow disjoint mutable dynamic slice prefixes and rests", DynamicRestAsync),
        new("MIR patterns borrow disjoint mutable dynamic slice suffixes and rests", DynamicSuffixAsync),
        new("MIR patterns defer noncopy payload moves until guards succeed", GuardMovesAsync),
        new("MIR patterns reject guard moves and mutable guard writes", InvalidGuardsAsync),
        new("MIR patterns validate partition bounds result lengths and mutability", PartitionValidationAsync),
        new("MIR patterns validate from end minimum length evidence", FromEndValidationAsync),
        new("MIR patterns destructure function and closure parameters with ref bindings", ParameterPatternsAsync),
    ];

    private static Task MatchAsync()
    {
        const string source = "fn choose(value: (bool, i32)) -> i32 { match value { (true, x) if x > 0 => x, (true, x) => x + 1, (false, x) => x - 1 } }";
        SafeCoreMirPipelineResult result = Analyze(source, "mir-v2-match.rs");
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics) + " | mir=" + Format(result.Mir?.Diagnostics ?? []));
        AssertEx.True(result.MirSnapshot!.Contains("branch", StringComparison.Ordinal), "Match must lower to explicit MIR branches.");
        AssertEx.True(result.MirSnapshot.Contains(".tuple[1]", StringComparison.Ordinal), "Tuple pattern bindings must lower through field projections.");
        AssertEx.True(result.MirSnapshot.Contains("mir-v2-match.rs:", StringComparison.Ordinal), "Match MIR must preserve source mapping.");
        SafeCoreMirPipelineResult alternatives = Analyze(
            "fn either(value: bool) -> i32 { match value { true | false => 1 } }",
            "mir-v2-or.rs");
        AssertEx.True(alternatives.IsSuccessful, Format(alternatives.Diagnostics));
        AssertEx.True(alternatives.MirSnapshot!.Contains("branch", StringComparison.Ordinal),
            "Or-patterns must lower through bounded CFG branches.");
        return Task.CompletedTask;
    }

    private static Task ClosureAsync()
    {
        const string source = "fn apply() -> i32 { let add = |value: i32| value + 1; add(2) }";
        SafeCoreMirPipelineResult result = Analyze(source, "mir-v2-closure.rs");
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics) + " | mir=" + Format(result.Mir?.Diagnostics ?? []));
        AssertEx.True(result.MirSnapshot!.Contains("binary +", StringComparison.Ordinal), "Closure body must be emitted into MIR.");
        AssertEx.False(result.MirSnapshot.Contains("closure#", StringComparison.OrdinalIgnoreCase), "Closure calls must not use an interpreter marker.");
        return Task.CompletedTask;
    }

    private static Task ClosureEscapeAsync()
    {
        const string source = "fn invalid() -> i32 { let add = |value: i32| value + 1; let escaped = add; 0 }";
        SafeCoreMirPipelineResult result = Analyze(source, "mir-v2-closure-escape.rs");
        AssertEx.False(result.IsSuccessful, "Escaping closure values are outside the static expansion contract.");
        AssertEx.Equal(SafeCoreMirLowering.UnsupportedSyntax, result.Diagnostics.Single().Code);
        AssertEx.True(result.Diagnostics.Single().Span.Start >= 0, "Closure escape diagnostic must retain a source span.");
        return Task.CompletedTask;
    }

    private static Task MutableCaptureAsync()
    {
        const string source = "fn apply() -> i32 { let mut base = 2; let add = |value: i32| value + base; add(1) }";
        SafeCoreMirPipelineResult result = Analyze(source, "mir-v2-mutable-capture.rs");
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
        AssertEx.True(result.MirSnapshot!.Contains("unary &", StringComparison.Ordinal),
            "A read capture must retain a shared borrow of the original binding.");
        return Task.CompletedTask;
    }

    private static Task BindingOrPatternAsync()
    {
        const string source = "fn choose(value: (i32, i32)) -> i32 { match value { (x, 0) | (0, x) => x, _ => -1 } }";
        SafeCoreMirPipelineResult result = Analyze(source, "mir-v2-binding-or.rs");
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task PatternBudgetAsync()
    {
        const string source = "fn choose(value: bool) -> i32 { match value { true => 1, false => 0 } }";
        SafeCoreMirPipelineResult result = SafeCoreMirPipeline.Analyze(source, "mir-v2-pattern-budget.rs", new()
        {
            EnableP1Extensions = true,
            MaximumPatternAlternatives = 1,
            Timeout = TimeSpan.FromSeconds(5),
        });
        AssertEx.False(result.IsSuccessful, "The fixed pattern alternative budget must be enforced.");
        AssertEx.Equal(SafeCoreMirLowering.LimitReached, result.Diagnostics.Single().Code);
        return Task.CompletedTask;
    }

    private static Task CoreClrAsync() => WithWorkspace(async (directory, token) =>
    {
        const string source = "fn choose(value: bool) -> i32 { match value { true => 1, false => 0 } } fn apply() -> i32 { let base = 2; let add = |value: i32| value + base; add(2) } fn main() { println!(\"{}\", choose(true)); println!(\"{}\", apply()); }";
        string output = Path.Combine(directory, "program.dll");
        CompilationResult compiled = CompilerDriver.Compile(source, "mir-v2-execution.rs", output,
            assemblyName: "MirV2Execution", profile: CompilationProfile.SafeCoreMirV2, cancellationToken: token);
        AssertEx.True(compiled.Success, Format(compiled.Diagnostics));
        BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
            new("dotnet", [output], directory, TimeSpan.FromSeconds(10)), token).ConfigureAwait(false);
        AssertEx.True(run.Succeeded, run.StandardError);
        AssertEx.Equal("1\n4\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
    });

    private static Task OrGuardsAsync() => RunAsync("""
        fn check(value: i32) -> bool { println!("{}", value); value == 2 }
        fn choose(value: (i32, i32)) -> i32 { match value {
            (x, _) | (_, x) if check(x) => x, _ => -1
        } }
        fn nested(value: ((i32, i32), bool)) -> i32 { match value {
            ((x, 0) | (0, x), true) => x, _ => -1
        } }
        fn main() { println!("{}", choose((1, 2))); println!("{}", nested(((0, 7), true))); }
        """, "1\n2\n2\n7\n");

    private static Task LetElseAsync() => RunAsync("""
        enum Item { None, Some(i32) }
        fn read(value: Item) -> i32 { let Item::Some(x) = value else { return 99; }; x }
        fn choose(value: (bool, i32)) -> i32 { let ((true, x) | (false, x)) = value; x }
        fn main() { println!("{}", read(Item::None)); println!("{}", read(Item::Some(5)));
            println!("{}", choose((true, 7))); println!("{}", choose((false, 9))); }
        """, "99\n5\n7\n9\n");

    private static Task RestsAsync() => RunAsync("""
        struct Row(i32, i32, i32, i32);
        struct Token(i32);
        fn main() { let (first, .., last) = (1, 2, 3, 4); println!("{}", first + last);
            let Row(first, .., last) = Row(5, 6, 7, 8); println!("{}", first + last);
            let [first, middle @ .., last] = [1, 2, 3, 4, 5]; println!("{}", first + last);
            println!("{}", middle[0] + middle[2]); let whole @ (x, _) = (7, 8); println!("{}", whole.1 + x);
            let [first, rest @ ..] = [Token(1), Token(2), Token(3)]; println!("{}", first.0 + rest[1].0); }
        """, "5\n13\n6\n6\n15\n4\n");

    private static Task RefBindingsAsync() => RunAsync("""
        struct Pair { left: i32, right: i32 }
        fn main() { let mut pair = (2, 3); let (ref mut left, ref right) = pair;
            *left += *right; println!("{}", pair.0);
            let mut record = Pair { left: 4, right: 5 };
            match &mut record { Pair { left, right } => { *left += *right; } };
            println!("{}", record.left); let &value = &7; println!("{}", value); }
        """, "5\n9\n7\n");

    private static Task RangesAsync() => RunAsync("""
        const LOW: i32 = 2;
        const HIGH: i32 = 4;
        fn read(value: i32) -> i32 { match value { -3 => 1, LOW..=HIGH => 2, 5.. => 3, ..0 => 4, LOW => 5, _ => 6 } }
        fn main() { println!("{}", read(-3)); println!("{}", read(3)); println!("{}", read(7));
            println!("{}", read(-1)); println!("{}", read(1)); }
        """, "1\n2\n3\n4\n6\n");

    private static Task SlicesAsync() => RunAsync("""
        fn read(values: &[i32]) -> i32 { match values {
            [] => 0, [only] => *only, [first, middle @ .., last] => *first + middle[0] + *last
        } }
        fn main() { let values = [1, 2, 3, 4]; println!("{}", read(&values[..0]));
            println!("{}", read(&values[..1])); println!("{}", read(&values)); }
        """, "0\n1\n7\n");

    private static Task ArrayRestViewAsync() => RunAsync("""
        fn main() { let mut values = [1, 2, 3, 4]; let [_, rest @ ..] = &mut values;
            rest[0] = 8; let copy = *rest; println!("{}", copy[0]); *rest = [5, 6, 7];
            println!("{}", values[1] + values[3]); }
        """, "8\n12\n");

    private static Task DisjointRestAsync() => RunAsync("""
        fn main() { let mut values = [1, 2, 3, 4]; let [first, rest @ ..] = &mut values;
            *first += 4; rest[0] += 6; println!("{}", values[0] + values[1]);
            let mut explicit = [2, 3, 4]; let [ref mut first, ref mut rest @ ..] = explicit;
            *first += 5; rest[1] += 6; println!("{}", explicit[0] + explicit[2]); }
        """, "13\n17\n");

    private static Task GuardMovesAsync() => RunAsync("""
        struct Token(i32);
        enum Item { None, Some(Token) }
        fn accept(token: &Token) -> bool { println!("{}", token.0); false }
        fn consume(token: Token) -> i32 { token.0 }
        fn read(value: Item) -> i32 { match value {
            Item::Some(token) if accept(&token) => consume(token),
            Item::Some(token) => consume(token), Item::None => 0
        } }
        fn main() { println!("{}", read(Item::Some(Token(7)))); }
        """, "7\n7\n");

    private static Task DynamicRestAsync() => RunAsync("""
        fn change(values: &mut [i32]) {
            match values { [first, second, rest @ ..] => { *first += 4; *second += 5; rest[0] += 6; }, _ => () };
        }
        fn main() { let mut values = [1, 2, 3, 4]; change(&mut values);
            println!("{}", values[0] + values[1] + values[2]); }
        """, "21\n");

    private static Task DynamicSuffixAsync() => RunAsync("""
        fn change(values: &mut [i32]) {
            match values { [first, middle @ .., last] if middle.len() > 0 => {
                *first += 1; *last += 3; middle[0] += 2;
            }, _ => () };
        }
        fn tail(values: &mut [i32]) {
            match values { [rest @ .., before, last] => {
                rest[0] += 1; *before += 2; *last += 3;
            }, _ => () };
        }
        fn main() { let mut first = [1, 2, 3, 4]; change(&mut first);
            println!("{}", first[0] + first[1] + first[3]);
            let mut second = [1, 2, 3, 4]; tail(&mut second);
            println!("{}", second[0] + second[2] + second[3]);
            let mut short = [9]; change(&mut short); tail(&mut short); println!("{}", short[0]); }
        """, "13\n14\n9\n");

    private static Task ParameterPatternsAsync() => RunAsync("""
        fn read(ref value: i32) -> i32 { *value }
        fn change(ref mut value: i32) -> i32 { *value += 1; *value }
        fn tuple((left, ref right): (i32, i32)) -> i32 { left + *right }
        fn nested((ref mut left, right): (i32, i32)) -> i32 { *left += right; *left }
        fn main() { println!("{}", read(2)); println!("{}", change(3)); println!("{}", tuple((4, 5)));
            let captured = 6;
            let unpack = |(left, ref right): (i32, i32)| left + *right + captured;
            println!("{}", unpack((1, 2)));
            let array = |[first, rest @ ..]: [i32; 3]| first + rest[1];
            println!("{}", array([1, 2, 3]));
            let ignore = |_: i32| captured; println!("{}", ignore(99));
            let mutate = |ref mut value: i32| { *value += captured; *value }; println!("{}", mutate(2));
            let fields = |(ref mut left, right): (i32, i32)| { *left += right; *left };
            println!("{}", fields((2, 3))); println!("{}", nested((4, 5))); }
        """, "2\n4\n9\n9\n4\n6\n8\n5\n9\n");

    private static Task InvalidGuardsAsync()
    {
        string[] sources =
        [
            "struct Token(i32); fn consume(token: Token) -> bool { true } fn main() { let x = Token(1); match x { token if consume(token) => (), _ => () }; }",
            "fn main() { let mut x = 1; match x { ref mut r if { *r = 2; true } => (), _ => () }; }",
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (int index = 0; index < sources.Length; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            CompilationResult result = CompilerDriver.Check(sources[index], "invalid-guard.rs", CompilationProfile.SafeCoreMirV2, deadline.Token);
            AssertEx.False(result.Success, "Guards must not consume or mutably access pending pattern bindings.");
            AssertEx.True(result.Diagnostics.Count != 0, Format(result.Diagnostics));
        }
        return Task.CompletedTask;
    }

    private static Task PartitionValidationAsync()
    {
        var source = new SafeCoreMirSource("partition.rs", new(0, 1), 0, 1);
        SafeCoreType integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
        SafeCoreType owner = SafeCoreType.Reference(SafeCoreType.Array(integer, 4), true);
        SafeCoreType rest = SafeCoreType.Reference(SafeCoreType.Array(integer, 2), true);
        SafeCoreType usize = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Usize);
        SafeCoreMirOperand reference = SafeCoreMirOperand.Local(0, owner, source);
        bool Valid(SafeCoreMirRvalue value)
        {
            var function = new SafeCoreMirFunction(0, "partition", value.Type,
                [new(0, "owner", value.Operands[0].Type, SafeCoreMirLocalKind.Parameter, false, source),
                 new(1, "bound", usize, SafeCoreMirLocalKind.Parameter, false, source),
                 new(2, "rest", value.Type, SafeCoreMirLocalKind.Temporary, false, source)],
                [new(0, [new(2, value, source)], SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(2, value.Type, source), source), source)], 0, source);
            return SafeCoreMirValidation.Validate(new([function])).IsSuccessful;
        }
        AssertEx.True(Valid(SafeCoreMirRvalue.PatternSubslice(reference, 1, 1, rest, source)), "A partition must retain its exact fixed result length.");
        AssertEx.False(Valid(SafeCoreMirRvalue.PatternSubslice(reference, 1, 0, rest, source)), "Forged fixed result lengths must reject.");
        AssertEx.False(Valid(SafeCoreMirRvalue.PatternSubslice(reference, -1, 1, rest, source)), "Negative prefix lengths must reject.");
        AssertEx.False(Valid(SafeCoreMirRvalue.PatternSubslice(reference, 3, 3, rest, source)), "A fixed owner cannot contain overlapping prefix and suffix.");
        SafeCoreMirOperand shared = SafeCoreMirOperand.Local(0, SafeCoreType.Reference(owner.ElementType, false), source);
        AssertEx.False(Valid(SafeCoreMirRvalue.PatternSubslice(shared, 1, 1, rest, source)), "A shared owner cannot produce a mutable pattern partition.");
        SafeCoreMirRvalue correct = SafeCoreMirRvalue.PatternSubslice(reference, 1, 1, rest, source);
        var dynamic = new SafeCoreMirRvalue(SafeCoreMirRvalueKind.Subslice, rest,
            [reference, SafeCoreMirOperand.Local(1, usize, source), correct.Operands[2]], "pattern", source);
        AssertEx.False(Valid(dynamic), "Pattern partitions require compile-time prefix and suffix evidence.");
        return Task.CompletedTask;
    }

    private static Task FromEndValidationAsync()
    {
        var source = new SafeCoreMirSource("suffix.rs", new(0, 1), 0, 1);
        SafeCoreType integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
        bool Valid(SafeCoreType owner, SafeCoreMirProjection projection)
        {
            SafeCoreMirPlace place = SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.Dereference()).Append(projection);
            var function = new SafeCoreMirFunction(0, "suffix", integer,
                [new(0, "owner", SafeCoreType.Reference(owner, false), SafeCoreMirLocalKind.Parameter, false, source)],
                [new(0, [], SafeCoreMirTerminator.Return(SafeCoreMirOperand.PlaceValue(place, integer, source), source), source)], 0, source);
            return SafeCoreMirValidation.Validate(new([function])).IsSuccessful;
        }
        AssertEx.True(Valid(SafeCoreType.Slice(integer), SafeCoreMirProjection.FromEndIndex(1, 2)), "Dynamic suffix access enforces the declared minimum at runtime.");
        AssertEx.True(Valid(SafeCoreType.Array(integer, 4), SafeCoreMirProjection.FromEndIndex(2, 3)), "Fixed suffix access retains its source element type.");
        AssertEx.False(Valid(SafeCoreType.Array(integer, 2), SafeCoreMirProjection.FromEndIndex(1, 3)), "A fixed owner cannot satisfy a larger minimum length.");
        AssertEx.False(Valid(SafeCoreType.Tuple([integer, integer]), SafeCoreMirProjection.FromEndIndex(1, 2)), "Only array and slice owners support from-end access.");
        AssertEx.Throws<ArgumentException>(() => SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.FromEndIndex(0, 1)));
        AssertEx.Throws<ArgumentException>(() => SafeCoreMirPlace.Root(0).Append(SafeCoreMirProjection.FromEndIndex(2, 1)));
        return Task.CompletedTask;
    }

    private static Task RunAsync(string source, string expected) => WithWorkspace(async (directory, token) =>
    {
        string output = Path.Combine(directory, "pattern.dll");
        CompilationResult compiled = CompilerDriver.Compile(source, "mir-patterns.rs", output,
            assemblyName: "MirPatterns", profile: CompilationProfile.SafeCoreMirV2, cancellationToken: token);
        AssertEx.True(compiled.Success, Format(compiled.Diagnostics));
        BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
            new("dotnet", [output], directory, TimeSpan.FromSeconds(10)), token).ConfigureAwait(false);
        AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete, run.StandardError);
        AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
    });
    private static SafeCoreMirPipelineResult Analyze(string source, string path) =>
        SafeCoreMirPipeline.Analyze(source, path, new()
        {
            EnableP1Extensions = true,
            EnableRepeatedArrays = true,
            RequireOwnershipEvidence = true,
            RequireCleanupEvidence = true,
            Timeout = TimeSpan.FromSeconds(5),
        });

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(static d => $"{d.Code}@{d.Span.Start}+{d.Span.Length}: {d.Message}"));

    private static async Task WithWorkspace(Func<string, CancellationToken, Task> action)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(root, "mir-v2-pattern-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(directory);
        try { await action(directory, deadline.Token).ConfigureAwait(false); }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Cleanup must target only this pattern test's owned workspace.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
