using RustSharp.Compiler;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirScalarExecutionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR scalar signed division remainder bitwise and shifts execute", SignedAsync),
        new("MIR scalar boolean bitwise operations evaluate both operands", BooleanAsync),
        new("MIR scalar bounded usize operators and conversions execute", UsizeAsync),
        new("MIR bounded usize left shifts truncate native-width bits before checking the result", () => RunAsync(
            "fn shift(value: usize, count: usize) -> usize { value << count } fn main() { " +
            "println!(\"{}\", shift(2, 63)); println!(\"{}\", shift(4, 62)); println!(\"{}\", shift(0, 63)); " +
            "println!(\"{}\", shift(1, 30)); }", "0\n0\n0\n1073741824\n")),
        new("MIR scalar division rejects zero", () => TrapAsync("fn f(a: i32, b: i32) -> i32 { a / b } fn main() { println!(\"{}\", f(1, 0)); }", "DivideByZeroException")),
        new("MIR scalar signed division rejects minimum divided by minus one", () => TrapAsync("fn f(a: i32, b: i32) -> i32 { a / b } fn main() { println!(\"{}\", f(-2147483648, -1)); }", "OverflowException")),
        new("MIR scalar signed remainder rejects minimum modulo minus one", () => TrapAsync("fn f(a: i32, b: i32) -> i32 { a % b } fn main() { println!(\"{}\", f(-2147483648, -1)); }", "OverflowException")),
        new("MIR scalar shifts reject overflowing dynamic counts", () => TrapAsync("fn f(a: i32, b: i32) -> i32 { a << b } fn main() { println!(\"{}\", f(1, 32)); }", "OverflowException")),
        new("MIR scalar shifts reject negative dynamic counts", () => TrapAsync("fn f(a: i32, b: i32) -> i32 { a >> b } fn main() { println!(\"{}\", f(1, -1)); }", "OverflowException")),
        new("MIR bounded usize subtraction rejects unsigned underflow", () => TrapAsync("fn f(a: usize, b: usize) -> usize { a - b } fn main() { println!(\"{}\", f(0, 1)); }", "OverflowException")),
        new("MIR bounded usize shifts reject unrepresentable values", () => TrapAsync("fn f(a: usize, b: usize) -> usize { a << b } fn main() { println!(\"{}\", f(1, 31)); }", "OverflowException")),
        new("MIR bounded usize shifts reject native-width counts", () => TrapAsync("fn f(a: usize, b: usize) -> usize { a << b } fn main() { println!(\"{}\", f(0, 64)); }", "OverflowException")),
        new("MIR bounded usize shifts reject native-width unrepresentable results", () => TrapAsync("fn f(a: usize, b: usize) -> usize { a << b } fn main() { println!(\"{}\", f(3, 63)); }", "OverflowException")),
        new("MIR bounded usize conversion rejects unrepresentable negatives", () => TrapAsync("fn f(a: i32) -> usize { a as usize } fn main() { println!(\"{}\", f(-1)); }", "OverflowException")),
    ];

    private static Task SignedAsync() => RunAsync(
        "fn main() { let a = -17; let b = 5; println!(\"{}\", a / b); println!(\"{}\", a % b); " +
        "println!(\"{}\", 6 & 3); println!(\"{}\", 6 | 3); println!(\"{}\", 6 ^ 3); println!(\"{}\", !6); " +
        "println!(\"{}\", 1 << 31); println!(\"{}\", -8 >> 2); let mut n = 21; n /= 3; n %= 5; n <<= 2; n |= 1; n ^= 3; " +
        "println!(\"{}\", n); }", "-3\n-2\n2\n7\n5\n-7\n-2147483648\n-2\n10\n");

    private static Task BooleanAsync() => RunAsync(
        "fn side() -> bool { println!(\"right\"); true } fn main() { println!(\"{}\", false & side()); " +
        "println!(\"{}\", true | side()); println!(\"{}\", true ^ true); println!(\"{}\", true as i32); println!(\"{}\", false as usize); " +
        "println!(\"{}\", false < true); println!(\"{}\", true <= false); println!(\"{}\", true >= true); }",
        "right\nfalse\nright\ntrue\nfalse\n1\n0\ntrue\nfalse\ntrue\n");

    private static Task UsizeAsync() => RunAsync(
        "fn main() { let a: usize = 15; let b: usize = 4; println!(\"{}\", a / b); println!(\"{}\", a % b); " +
        "println!(\"{}\", a & b); println!(\"{}\", a << 2); println!(\"{}\", a >> 40); println!(\"{}\", a + b); " +
        "println!(\"{}\", a - b); println!(\"{}\", a * b); println!(\"{}\", (a as i32) + 1); println!(\"{}\", 9i32 as usize); }",
        "3\n3\n4\n60\n0\n19\n11\n60\n16\n9\n");

    private static Task TrapAsync(string source, string exception) => ExecuteAsync(source, null, exception);
    private static Task RunAsync(string source, string expected) => ExecuteAsync(source, expected, null);

    private static async Task ExecuteAsync(string source, string? expected, string? exception)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RustSharp.Tests"));
        string directory = Path.Combine(root, "scalar-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "scalar.dll");
            CompilationResult result = CompilerDriver.Compile(source, "scalar-runtime.rs", output,
                assemblyName: "ScalarRuntime", profile: CompilationProfile.SafeCoreMirV2, cancellationToken: deadline.Token);
            AssertEx.True(result.Success, Format(result.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [output], directory, TimeSpan.FromSeconds(10)), deadline.Token).ConfigureAwait(false);
            AssertEx.False(run.ProcessTreeCleanupIncomplete, "A scalar test must collect its own process tree.");
            if (exception is not null)
            {
                AssertEx.False(run.Succeeded, "Invalid scalar operations must trap.");
                AssertEx.True(run.StandardError.Contains(exception, StringComparison.Ordinal), run.StandardError);
            }
            else
            {
                AssertEx.True(run.Succeeded, run.StandardError);
                AssertEx.Equal(expected!, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
            }
        }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Scalar test cleanup must target only its unique owned directory.");
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(static item => item.Code + ": " + item.Message));
}
