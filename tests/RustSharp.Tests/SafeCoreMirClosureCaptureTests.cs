using RustSharp.Compiler;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirClosureCaptureTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR closure mutable captures write back across calls", MutableAsync),
        new("MIR closure move captures use owned storage across calls", MoveStorageAsync),
        new("MIR closure captured shared references retain their owner", SharedReferenceAsync),
        new("MIR closure captured mutable references reborrow their referent", MutableReferenceAsync),
        new("MIR closure implicit mutable capture arguments reborrow across calls", MutableArgumentAsync),
        new("MIR closure captured aggregate projections retain original storage", AggregateAsync),
        new("MIR closure noncopy field captures move only their selected field", FieldMoveAsync),
        new("MIR closure disjoint field captures retain independent loans", IndependentFieldsAsync),
        new("MIR closure shared arguments reborrow mutable references", SharedArgumentAsync),
        new("MIR closure consumed aggregate fields cannot be reused", FieldConsumedAsync),
        new("MIR closure field loans reject mutation of the captured field", FieldConflictAsync),
        new("MIR closure immediate invocations collect their captures", ImmediateAsync),
        new("MIR closure shared captures protect owners until the last call", BorrowConflictAsync),
        new("MIR closure owned captures cannot consume values twice", ConsumedCaptureAsync),
        new("MIR closure moved captures prevent subsequent owner uses", MoveConflictAsync),
    ];

    private static Task MutableAsync() => RunAsync(
        "fn main() { let mut base = 2; let mut add = |value: i32| { base += value; base }; " +
        "println!(\"{}\", add(3)); println!(\"{}\", add(4)); println!(\"{}\", base); }", "5\n9\n9\n");

    private static Task MoveStorageAsync() => RunAsync(
        "fn main() { let mut base = 2; let mut add = move |value: i32| { base += value; base }; " +
        "println!(\"{}\", add(3)); println!(\"{}\", add(4)); println!(\"{}\", base); }", "5\n9\n2\n");

    private static Task SharedReferenceAsync() => RunAsync(
        "fn main() { let owner = 7; let reference = &owner; let read = || *reference; " +
        "println!(\"{}\", read()); println!(\"{}\", read()); }", "7\n7\n");

    private static Task MutableReferenceAsync() => RunAsync(
        "fn main() { let mut owner = 1; let reference = &mut owner; let mut change = || { *reference += 2; }; " +
        "change(); change(); println!(\"{}\", owner); }", "5\n");

    private static Task MutableArgumentAsync() => RunAsync(
        "fn bump(value: &mut i32) { *value += 1; } fn main() { let mut owner = 1; let reference = &mut owner; " +
        "let mut change = || bump(reference); change(); change(); println!(\"{}\", owner); }", "3\n");

    private static Task AggregateAsync() => RunAsync(
        "struct Pair { left: i32, right: i32 } fn main() { let mut pair = Pair { left: 2, right: 3 }; " +
        "let mut add = |n: i32| { pair.left += n; pair.right }; println!(\"{}\", add(4)); " +
        "println!(\"{}\", pair.left); }", "3\n6\n");

    private static Task ImmediateAsync() => RunAsync(
        "fn main() { let base = 8; println!(\"{}\", (|n: i32| base + n)(3)); }", "11\n");

    private static Task FieldMoveAsync() => RunAsync(
        "struct Token(i32); struct Pair { item: Token, other: i32 } fn take(value: Token) -> i32 { value.0 } " +
        "fn main() { let pair = Pair { item: Token(7), other: 3 }; let consume = || take(pair.item); " +
        "println!(\"{}\", pair.other); println!(\"{}\", consume()); println!(\"{}\", pair.other); }", "3\n7\n3\n");

    private static Task IndependentFieldsAsync() => RunAsync(
        "struct Pair { left: i32, right: i32 } fn main() { let mut pair = Pair { left: 1, right: 2 }; " +
        "let mut left = || { pair.left += 3; }; let mut right = || { pair.right += 4; }; " +
        "left(); right(); left(); println!(\"{}\", pair.left + pair.right); " +
        "let read = || pair.left; pair.right = 9; println!(\"{}\", read()); println!(\"{}\", pair.right); }", "13\n7\n9\n");

    private static Task SharedArgumentAsync() => RunAsync(
        "fn read(value: &i32) -> i32 { *value } fn main() { let mut owner = 1; let reference = &mut owner; " +
        "let get = || read(reference); println!(\"{}\", get()); println!(\"{}\", get()); " +
        "*reference = 7; println!(\"{}\", owner); }", "1\n1\n7\n");

    private static Task FieldConsumedAsync() => RejectAsync(
        "struct Token(i32); struct Pair { item: Token, other: i32 } fn take(value: Token) {} " +
        "fn main() { let pair = Pair { item: Token(7), other: 3 }; let consume = || take(pair.item); consume(); consume(); }",
        SafeCoreOwnershipDiagnosticCodes.UseAfterMove);

    private static Task FieldConflictAsync() => RejectAsync(
        "struct Pair { left: i32, right: i32 } fn main() { let mut pair = Pair { left: 1, right: 2 }; " +
        "let read = || pair.left; pair.left = 4; println!(\"{}\", read()); }",
        SafeCoreOwnershipDiagnosticCodes.BorrowConflict);

    private static Task BorrowConflictAsync() => RejectAsync(
        "fn main() { let mut owner = 1; let read = || owner; owner = 2; println!(\"{}\", read()); }",
        SafeCoreOwnershipDiagnosticCodes.BorrowConflict);

    private static Task ConsumedCaptureAsync() => RejectAsync(
        "struct Token(i32); fn take(value: Token) {} fn main() { let token = Token(1); let consume = move || take(token); consume(); consume(); }",
        SafeCoreOwnershipDiagnosticCodes.UseAfterMove);

    private static Task MoveConflictAsync() => RejectAsync(
        "struct Token(i32); fn take(value: Token) {} fn main() { let token = Token(1); let consume = move || take(token); take(token); consume(); }",
        SafeCoreOwnershipDiagnosticCodes.UseAfterMove);

    private static Task RejectAsync(string source, string code)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CompilationResult result = CompilerDriver.Check(source, "closure-negative.rs", CompilationProfile.SafeCoreMirV2, deadline.Token);
        AssertEx.False(result.Success, "Invalid capture ownership must be rejected.");
        AssertEx.True(result.Diagnostics.Any(item => item.Code == code), Format(result.Diagnostics));
        return Task.CompletedTask;
    }

    private static async Task RunAsync(string source, string expected)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RustSharp.Tests"));
        string directory = Path.Combine(root, "closure-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "closure.dll");
            CompilationResult result = CompilerDriver.Compile(source, "closure-runtime.rs", output,
                assemblyName: "ClosureRuntime", profile: CompilationProfile.SafeCoreMirV2, cancellationToken: deadline.Token);
            AssertEx.True(result.Success, Format(result.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [output], directory, TimeSpan.FromSeconds(10)), deadline.Token).ConfigureAwait(false);
            AssertEx.True(run.Succeeded && !run.ProcessTreeCleanupIncomplete, run.StandardError);
            AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Closure test cleanup must target only its unique owned directory.");
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(static item => item.Code + ": " + item.Message));
}
