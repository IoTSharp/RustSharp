using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirAdtSourceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR v2 named ADT initializers evaluate in source order", ConstructionOrderAsync),
        new("MIR v2 nested nominal tuple array places retain their owner", NestedProjectionAsync),
        new("MIR v2 projected borrows mutate the original field", ProjectedBorrowAsync),
        new("MIR v2 dynamic projected writes evaluate the index once", DynamicProjectionAsync),
        new("MIR v2 compound dereference resolves the reference after its RHS", CompoundReferenceOrderAsync),
        new("MIR v2 named ADT values cross function boundaries", ValueCallAsync),
        new("MIR v2 struct updates preserve omitted fields", UpdateAsync),
        new("MIR v2 struct and tuple struct binding patterns project fields", PatternAsync),
        new("MIR v2 consuming an ADT prevents later owner reads", MoveAsync),
        new("MIR v2 projected mutable borrow conflicts are rejected", BorrowConflictAsync),
        new("MIR v2 nominal layout evidence is deterministic", LayoutEvidenceAsync),
        new("MIR v2 nominal array fields retain resolved constant lengths", ConstantLengthAsync),
        new("MIR v2 shared reference parameters read their caller owner", SharedReferenceParameterAsync),
        new("MIR v2 mutable field reference returns write back to the caller", ProjectedReferenceReturnAsync),
        new("MIR v2 consecutive mutable reference calls reborrow their argument", ImplicitReborrowCallsAsync),
        new("MIR v2 reference returns can select fields of one input owner", BranchReferenceReturnAsync),
        new("MIR v2 local reference joins retain both possible owners", LocalReferenceJoinAsync),
        new("MIR v2 local reference joins protect every possible owner", LocalReferenceJoinConflictAsync),
        new("MIR v2 rejects returning a reference to a local owner", LocalReturnEscapeAsync),
        new("MIR v2 rejects a branch local reference escaping through a join", BranchReferenceEscapeAsync),
        new("MIR v2 returned call references prevent caller owner mutation", ReturnedReferenceConflictAsync),
        new("MIR v2 rejects ambiguous reference return lifetime elision", AmbiguousReferenceElisionAsync),
        new("MIR v2 rejects reference returns without an input lifetime", MissingReferenceElisionAsync),
        new("MIR v2 counts nested input reference lifetimes independently", NestedReferenceElisionAsync),
        new("MIR v2 counts tuple input reference lifetimes independently", TupleReferenceElisionAsync),
        new("MIR v2 checks elision for references nested in return values", AggregateReturnElisionAsync),
        new("MIR v2 preserves the explicit lifetime source boundary", ExplicitLifetimeBoundaryAsync),
        new("MIR v2 permits multiple reference inputs for scalar returns", MultipleReferenceInputAsync),
        new("MIR v2 qualified nominal layouts keep distinct field identities", QualifiedLayoutAsync),
        new("MIR v2 imported tuple and unit constructors retain nominal identity", ImportedConstructorsAsync),
        new("MIR v2 struct updates evaluate their base after explicit fields", StructUpdateOrderAsync),
        new("MIR v2 reference arguments snapshot before later argument effects", ReferenceArgumentOrderAsync),
        new("MIR v2 rejects temporary owners escaping through a call", CallTemporaryEscapeAsync),
        new("MIR v2 direct let borrows extend their temporary owner", DirectTemporaryBorrowAsync),
        new("MIR v2 extending block tails retain the let scope", BlockTailTemporaryBorrowAsync),
        new("MIR v2 borrowing a block tail extends its temporary owner", BorrowBlockTemporaryAsync),
        new("MIR v2 temporary borrows may be consumed in the same statement", StatementTemporaryBorrowAsync),
        new("MIR v1 retains its named ADT rejection boundary", VersionBoundaryAsync),
    ];

    private static Task ConstructionOrderAsync() => RunAsync(
        "struct Pair { left: i32, right: i32 } fn value(x: i32) -> i32 { println!(\"{}\", x); x } " +
        "fn main() { let pair = Pair { right: value(2), left: value(1) }; println!(\"{}\", pair.left); println!(\"{}\", pair.right); }",
        "2\n1\n1\n2\n");

    private static Task NestedProjectionAsync() => RunAsync(
        "struct Leaf(i32, i32); struct Root { leaves: [Leaf; 2], pair: (i32, i32) } " +
        "fn main() { let mut root = Root { leaves: [Leaf(1, 2), Leaf(3, 4)], pair: (5, 6) }; " +
        "root.leaves[1].0 = 8; root.pair.1 += 3; println!(\"{}\", root.leaves[1].0); " +
        "println!(\"{}\", root.leaves[0].1); println!(\"{}\", root.pair.1); }",
        "8\n2\n9\n");

    private static Task ProjectedBorrowAsync() => RunAsync(
        "struct Pair { left: i32, right: i32 } fn main() { let mut pair = Pair { left: 1, right: 2 }; " +
        "let reference = &mut pair.left; *reference = 7; println!(\"{}\", *reference); " +
        "println!(\"{}\", pair.left); println!(\"{}\", pair.right); }", "7\n7\n2\n");

    private static Task DynamicProjectionAsync() => RunAsync(
        "fn index() -> usize { println!(\"index\"); 1 } fn rhs() -> i32 { println!(\"rhs\"); 9 } " +
        "fn main() { let mut values = [1, 2, 3]; values[index()] = rhs(); println!(\"{}\", values[1]); " +
        "values[index()] += rhs(); println!(\"{}\", values[1]); }", "rhs\nindex\n9\nrhs\nindex\n18\n");

    private static Task CompoundReferenceOrderAsync() => RunAsync(
        "fn main() { let mut first = 1; let mut second = 10; let mut reference = &mut first; " +
        "*reference += { reference = &mut second; 3 }; println!(\"{}\", first); println!(\"{}\", second); }",
        "1\n13\n");

    private static Task ValueCallAsync() => RunAsync(
        "struct Pair { left: i32, right: i32 } fn make(x: i32) -> Pair { Pair { left: x, right: 2 } } " +
        "fn sum(pair: Pair) -> i32 { pair.left + pair.right } fn main() { let pair = make(5); println!(\"{}\", sum(pair)); }",
        "7\n");

    private static Task UpdateAsync() => RunAsync(
        "struct Pair { left: i32, right: i32 } fn main() { let original = Pair { left: 1, right: 2 }; " +
        "let updated = Pair { left: 8, ..original }; println!(\"{}\", updated.left); println!(\"{}\", updated.right); }",
        "8\n2\n");

    private static Task ConstantLengthAsync() => RunAsync(
        "const LENGTH: usize = 1 + 2; struct Buffer { values: [i32; LENGTH] } fn main() { " +
        "let mut buffer = Buffer { values: [1, 2, 3] }; buffer.values[2] = 8; println!(\"{}\", buffer.values[2]); }",
        "8\n");

    private static Task PatternAsync() => RunAsync(
        "struct Pair { left: i32, right: i32 } struct Tuple(i32, i32); fn main() { " +
        "let Pair { left, right } = Pair { left: 2, right: 3 }; let Tuple(first, second) = Tuple(5, 7); " +
        "println!(\"{}\", left + right + first + second); }", "17\n");

    private static Task SharedReferenceParameterAsync() => RunAsync(
        "fn read(value: &i32) -> i32 { *value } fn main() { let owner = 17; let reference = &owner; " +
        "println!(\"{}\", read(reference)); println!(\"{}\", read(&owner)); println!(\"{}\", *reference); }",
        "17\n17\n17\n");

    private static Task ProjectedReferenceReturnAsync() => RunAsync(
        "struct Pair { value: i32, other: i32 } fn field(pair: &mut Pair) -> &mut i32 { &mut pair.value } " +
        "fn main() { let mut pair = Pair { value: 1, other: 2 }; let reference = field(&mut pair); " +
        "*reference = 13; println!(\"{}\", *reference); println!(\"{}\", pair.value); println!(\"{}\", pair.other); }",
        "13\n13\n2\n");

    private static Task ImplicitReborrowCallsAsync() => RunAsync(
        "fn mutate(reference: &mut i32) { *reference += 3; } fn main() { let mut owner = 1; " +
        "let reference = &mut owner; mutate(reference); mutate(reference); println!(\"{}\", *reference); " +
        "*reference += 2; println!(\"{}\", owner); }", "7\n9\n");

    private static Task BranchReferenceReturnAsync() => RunAsync(
        "struct Pair { left: i32, right: i32 } fn choose(pair: &Pair, left: bool) -> &i32 { " +
        "if left { &pair.left } else { &pair.right } } fn main() { let pair = Pair { left: 5, right: 9 }; " +
        "let left = choose(&pair, true); let right = choose(&pair, false); println!(\"{}\", *left); println!(\"{}\", *right); }",
        "5\n9\n");

    private static Task LocalReferenceJoinAsync() => RunAsync(
        "fn read(value: &i32) -> i32 { *value } fn main() { let first = 3; let second = 8; let mut choose_first = true; " +
        "let selected = if choose_first { &first } else { &second }; println!(\"{}\", read(selected)); " +
        "choose_first = false; let selected_again = if choose_first { &first } else { &second }; " +
        "println!(\"{}\", read(selected_again)); }", "3\n8\n");

    private static Task LocalReturnEscapeAsync() => RejectAsync(
        "fn dangling(input: &i32) -> &i32 { let local = 7; &local } fn main() { let owner = 1; " +
        "let escaped = dangling(&owner); println!(\"{}\", *escaped); }", SafeCoreOwnershipDiagnosticCodes.Escape);

    private static Task LocalReferenceJoinConflictAsync() => RejectAsync(
        "fn main() { let mut first = 3; let second = 8; let choose_first = false; " +
        "let selected = if choose_first { &first } else { &second }; first = 5; println!(\"{}\", *selected); }",
        SafeCoreOwnershipDiagnosticCodes.BorrowConflict);

    private static Task BranchReferenceEscapeAsync() => RejectAsync(
        "fn main() { let outer = 1; let choose_inner = true; let escaped = if choose_inner { " +
        "let inner = 2; &inner } else { &outer }; println!(\"{}\", *escaped); }", SafeCoreOwnershipDiagnosticCodes.Escape);

    private static Task ReturnedReferenceConflictAsync() => RejectAsync(
        "fn identity(input: &i32) -> &i32 { input } fn main() { let mut owner = 1; " +
        "let returned = identity(&owner); owner = 2; println!(\"{}\", *returned); }", SafeCoreOwnershipDiagnosticCodes.BorrowConflict);

    private static Task AmbiguousReferenceElisionAsync() => RejectAsync(
        "fn choose(left: &i32, right: &i32) -> &i32 { left } fn main() {}", SafeCoreMirLowering.InvalidLifetimeElision);

    private static Task MissingReferenceElisionAsync() => RejectAsync(
        "fn value() -> &i32 { &7 } fn main() {}", SafeCoreMirLowering.InvalidLifetimeElision);

    private static Task NestedReferenceElisionAsync() => RejectAsync(
        "fn inner(input: &&i32) -> &i32 { *input } fn main() {}", SafeCoreMirLowering.InvalidLifetimeElision);

    private static Task TupleReferenceElisionAsync() => RejectAsync(
        "fn first(input: (&i32, &i32)) -> &i32 { input.0 } fn main() {}", SafeCoreMirLowering.InvalidLifetimeElision);

    private static Task AggregateReturnElisionAsync() => RejectAsync(
        "fn pair() -> (&i32, &i32) { (&7, &9) } fn main() {}", SafeCoreMirLowering.InvalidLifetimeElision);

    private static Task ExplicitLifetimeBoundaryAsync() => RejectAsync(
        "fn identity(input: &'static i32) -> &'static i32 { input } fn main() {}", "RST2001");

    private static Task MultipleReferenceInputAsync() => RunAsync(
        "fn sum(left: &i32, right: &i32) -> i32 { *left + *right } " +
        "fn main() { let left = 3; let right = 8; println!(\"{}\", sum(&left, &right)); }", "11\n");

    private static Task QualifiedLayoutAsync() => RunAsync(
        "mod first { pub struct Item { pub value: i32 } } mod second { pub struct Item { pub pair: (i32, i32) } } " +
        "fn main() { let mut first = first::Item { value: 3 }; let second = second::Item { pair: (5, 7) }; " +
        "first.value = second.pair.1; println!(\"{}\", first.value); println!(\"{}\", second.pair.0); }", "7\n5\n");

    private static Task StructUpdateOrderAsync() => RunAsync(
        "struct Pair { left: i32, right: i32 } fn left() -> i32 { println!(\"left\"); 8 } " +
        "fn base() -> Pair { println!(\"base\"); Pair { left: 1, right: 2 } } fn main() { " +
        "let updated = Pair { left: left(), ..base() }; println!(\"{}\", updated.left); println!(\"{}\", updated.right); }",
        "left\nbase\n8\n2\n");

    private static Task ImportedConstructorsAsync() => RunAsync(
        "mod model { pub struct Pair(pub i32, pub i32); pub struct Unit; } use model::Pair as Alias; use model::Unit as Tag; " +
        "fn main() { let tag = Tag; let pair = Alias(2, 3); println!(\"{}\", pair.0 + pair.1); }", "5\n");

    private static Task ReferenceArgumentOrderAsync() => RunAsync(
        "fn read(first: &i32, ignored: i32) -> i32 { *first } fn main() { let first = 1; let second = 2; " +
        "let mut reference = &first; println!(\"{}\", read(reference, { reference = &second; 0 })); " +
        "println!(\"{}\", *reference); }", "1\n2\n");

    private static Task CallTemporaryEscapeAsync() => RejectAsync(
        "fn make() -> i32 { 7 } fn identity(input: &i32) -> &i32 { input } " +
        "fn main() { let reference = identity(&make()); println!(\"{}\", *reference); }", SafeCoreOwnershipDiagnosticCodes.Escape);

    private static Task DirectTemporaryBorrowAsync() => RunAsync(
        "fn make() -> i32 { 7 } fn main() { let reference = &make(); println!(\"{}\", *reference); }", "7\n");

    private static Task StatementTemporaryBorrowAsync() => RunAsync(
        "fn make() -> i32 { 7 } fn read(input: &i32) -> i32 { *input } " +
        "fn main() { println!(\"{}\", read(&make())); }", "7\n");

    private static Task BlockTailTemporaryBorrowAsync() => RunAsync(
        "fn make() -> i32 { 7 } fn main() { let reference = { &make() }; println!(\"{}\", *reference); }", "7\n");

    private static Task BorrowBlockTemporaryAsync() => RunAsync(
        "fn make() -> i32 { 7 } fn main() { let reference = &{ make() }; println!(\"{}\", *reference); }", "7\n");

    private static Task MoveAsync() => RejectAsync(
        "struct Pair { left: i32, right: i32 } fn take(pair: Pair) {} fn main() { let pair = Pair { left: 1, right: 2 }; " +
        "take(pair); println!(\"{}\", pair.left); }", SafeCoreOwnershipDiagnosticCodes.UseAfterMove);

    private static Task BorrowConflictAsync() => RejectAsync(
        "struct Pair { left: i32, right: i32 } fn main() { let mut pair = Pair { left: 1, right: 2 }; " +
        "let first = &mut pair.left; let second = &mut pair.left; println!(\"{}\", *first); println!(\"{}\", *second); }",
        SafeCoreOwnershipDiagnosticCodes.BorrowConflict);

    private static Task LayoutEvidenceAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string source = "struct Pair { right: [i32; 2], left: (i32, i32) } fn main() { let pair = Pair { left: (1, 2), right: [3, 4] }; println!(\"{}\", pair.right[0]); }";
        SafeCoreMirPipelineOptions options = new() { EnableP1Extensions = true, RequireOwnershipEvidence = true,
            CancellationToken = deadline.Token, Timeout = TimeSpan.FromSeconds(5) };
        SafeCoreMirPipelineResult first = SafeCoreMirPipeline.Analyze(source, "nominal-layout.rs", options);
        SafeCoreMirPipelineResult second = SafeCoreMirPipeline.Analyze(source, "nominal-layout.rs", options);
        AssertEx.True(first.IsSuccessful, Format(first.Diagnostics));
        AssertEx.True(second.IsSuccessful, Format(second.Diagnostics));
        AssertEx.Equal(first.MirSnapshot!, second.MirSnapshot!);
        AssertEx.True(first.MirSnapshot!.Contains("right", StringComparison.Ordinal) &&
            first.MirSnapshot.Contains("nominal-layout.rs:", StringComparison.Ordinal), "Layout and field source evidence must be retained.");
        return Task.CompletedTask;
    }

    private static Task VersionBoundaryAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        CompilationResult result = CompilerDriver.Check("struct Pair { value: i32 } fn main() { let pair = Pair { value: 1 }; }",
            "nominal-v1.rs", CompilationProfile.SafeCoreMir, deadline.Token);
        AssertEx.False(result.Success, "The v1 profile must retain its original named ADT rejection boundary.");
        AssertEx.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreMirLowering.UnsupportedSyntax), Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task RejectAsync(string source, string code) => WithWorkspaceAsync((directory, token) =>
    {
        CompilationResult checkedResult = CompilerDriver.Check(source, "adt-negative.rs", CompilationProfile.SafeCoreMirV2, token);
        AssertEx.False(checkedResult.Success, "Invalid nominal ownership must fail checking.");
        AssertEx.True(checkedResult.Diagnostics.Any(diagnostic => diagnostic.Code == code), Format(checkedResult.Diagnostics));
        string output = Path.Combine(directory, "rejected.dll");
        CompilationResult compiled = CompilerDriver.Compile(source, "adt-negative.rs", output,
            assemblyName: "AdtNegative", profile: CompilationProfile.SafeCoreMirV2, cancellationToken: token);
        AssertEx.False(compiled.Success, "Checking and compilation must enforce the same ownership contract.");
        AssertEx.True(compiled.Diagnostics.Any(diagnostic => diagnostic.Code == code), Format(compiled.Diagnostics));
        AssertEx.False(File.Exists(output), "A rejected ADT program must not produce an assembly.");
        return Task.CompletedTask;
    });

    private static Task RunAsync(string source, string expected) => WithWorkspaceAsync(async (directory, token) =>
    {
        string output = Path.Combine(directory, "adt.dll");
        CompilationResult result = CompilerDriver.Compile(source, "adt-runtime.rs", output,
            assemblyName: "AdtRuntime", profile: CompilationProfile.SafeCoreMirV2, cancellationToken: token);
        AssertEx.True(result.Success, Format(result.Diagnostics));
        BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
            new("dotnet", [output], directory, TimeSpan.FromSeconds(10)), token).ConfigureAwait(false);
        AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete, run.StandardError);
        AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
    });

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) => string.Join("; ",
        diagnostics.Select(static diagnostic => diagnostic.Code + ": " + diagnostic.Message));

    private static async Task WithWorkspaceAsync(Func<string, CancellationToken, Task> action)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(root, "mir-nominal-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(directory);
        try { await action(directory, deadline.Token).ConfigureAwait(false); }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Nominal test cleanup may delete only its own workspace.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
