using RustSharp.Compiler;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirReferenceAbiTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR reference ABI enum joins preserve active reference payloads", () => RunAsync(
            "enum Value { Number(i32), Ref(&'static i32) } fn pick(flag: bool) -> Value { " +
            "if flag { Value::Ref(&17) } else { Value::Number(3) } } " +
            "fn read(value: Value) -> i32 { match value { Value::Number(n) => n, Value::Ref(r) => *r } } " +
            "fn main() { println!(\"{}\", read(pick(true))); println!(\"{}\", read(pick(false))); }", "17\n3\n")),
        new("MIR reference ABI enum returns allow inactive reference variants", () => RunAsync(
            "enum Value { Number(i32), Ref(&'static i32) } fn value() -> Value { Value::Number(9) } " +
            "fn main() { let result = match value() { Value::Number(n) => n, Value::Ref(r) => *r }; println!(\"{}\", result); }", "9\n")),
        new("MIR reference ABI nested parameter preserves the original value", () => RunAsync(
            "fn read(value: &&i32) -> i32 { **value } fn main() { let owner = 17; let first = &owner; " +
            "let second = &first; println!(\"{}\", read(second)); }", "17\n")),
        new("MIR reference ABI reference tuple crosses calls and returns", () => RunAsync(
            "fn pick(values: (&i32, i32)) -> &i32 { values.0 } " +
            "fn main() { let first = 19; let values = (&first, 3); " +
            "let selected = pick(values); println!(\"{}\", *selected); }", "19\n")),
        new("MIR reference ABI writes preserve independent aggregate value copies", () => RunAsync(
            "fn main() { let original = (1, 2); let mut copy = original; let selected = &mut copy.0; " +
            "*selected = 5; println!(\"{}\", original.0); println!(\"{}\", copy.0); }", "1\n5\n")),
        new("MIR reference ABI nested mutable slots can rebind their referent", () => RunAsync(
            "fn main() { let first = 3; let second = 5; let mut reference = &first; let slot = &mut reference; " +
            "*slot = &second; println!(\"{}\", **slot); println!(\"{}\", *reference); }", "5\n5\n")),
        new("MIR reference ABI nested mutable referents preserve replacement writes", () => RunAsync(
            "fn main() { let mut first = 3; let mut second = 5; let mut reference = &mut first; " +
            "let slot = &mut reference; *slot = &mut second; **slot = 9; println!(\"{}\", second); }", "9\n")),
        new("MIR reference ABI dynamic arrays retain selected reference owners", () => RunAsync(
            "fn main() { let first = 3; let second = 5; let references = [&first, &second]; " +
            "let index: usize = 1; println!(\"{}\", *references[index]); }", "5\n")),
        new("MIR reference ABI dynamic mutable arrays write through selected owners", () => RunAsync(
            "fn main() { let mut first = 1; let mut second = 2; let mut references = [&mut first, &mut second]; " +
            "let index: usize = 1; *references[index] = 7; println!(\"{}\", second); }", "7\n")),
        new("MIR reference ABI fixed array rest returns retain window offsets and value copies", () => RunAsync(
            "fn tail(values: &mut [i32; 4]) -> &mut [i32; 3] { let [_, rest @ ..] = values; rest } " +
            "fn snapshot(values: &[i32; 3]) -> [i32; 3] { *values } " +
            "fn main() { let mut values = [1, 2, 3, 4]; let rest = tail(&mut values); let copy = snapshot(rest); " +
            "*rest = [5, 6, 7]; println!(\"{}\", copy[0]); println!(\"{}\", copy[2]); " +
            "println!(\"{}\", values[0]); println!(\"{}\", values[1]); println!(\"{}\", values[3]); }", "2\n4\n1\n5\n7\n")),
        new("MIR slice ABI nested fixed array rest unsizing retains its original window", () => RunAsync(
            "fn read(values: &[i32]) -> i32 { values[0] + values[1] } " +
            "fn main() { let values = [1, 2, 3, 4]; let [_, tail @ ..] = &values; " +
            "let [_, rest @ ..] = tail; println!(\"{}\", read(rest)); }", "7\n")),
        new("MIR slice ABI call and return preserve mutable subslice ownership", () => RunAsync(
            "fn tail(values: &mut [i32], start: usize) -> &mut [i32] { &mut values[start..] } " +
            "fn main() { let mut values = [1, 2, 3, 4]; let view: &mut [i32] = &mut values; " +
            "let selected = tail(view, 1); selected[1] = 42; println!(\"{}\", selected.len()); " +
            "println!(\"{}\", selected[1]); println!(\"{}\", values[2]); }", "3\n42\n42\n")),
        new("MIR slice ABI different owner lengths join across returns", () => RunAsync(
            "fn identity(values: &[i32]) -> &[i32] { values } fn main() { let short = [2]; let long = [5, 7, 11]; " +
            "let a: &[i32] = &short; let b: &[i32] = &long; let selected = if false { a } else { b }; " +
            "let view = identity(selected); " +
            "println!(\"{}\", view.len()); println!(\"{}\", view[2]); }", "3\n11\n")),
        new("MIR slice ABI inclusive ranges and empty suffix retain length", () => RunAsync(
            "fn main() { let values = [2, 4, 6, 8]; let view: &[i32] = &values; let middle = &view[1..=2]; " +
            "let empty = &view[4..]; println!(\"{}\", middle.len()); println!(\"{}\", middle[1]); " +
            "println!(\"{}\", empty.len()); }", "2\n6\n0\n")),
        new("MIR slice ABI aggregate elements preserve projected mutation", () => RunAsync(
            "fn read(values: &[(i32, bool)]) -> i32 { values[1].0 } " +
            "fn main() { let mut values = [(1, false), (3, true)]; let view: &mut [(i32, bool)] = &mut values; " +
            "view[1].0 = 13; println!(\"{}\", read(view)); println!(\"{}\", values[1].1); }", "13\ntrue\n")),
        new("MIR slice ABI dynamic suffix preserves reference element owners", () => RunAsync(
            "fn last(values: &[&i32]) -> i32 { match values { [.., last] => **last, _ => 0 } } " +
            "fn main() { let a = 3; let b = 5; let references = [&a, &b]; " +
            "println!(\"{}\", last(&references)); }", "5\n")),
        new("MIR slice ABI inverted dynamic range traps before access", () => RunAsync(
            "fn main() { let values = [2, 4, 6]; let view: &[i32] = &values; let start: usize = 2; " +
            "let end: usize = 1; let bad = &view[start..end]; println!(\"{}\", bad.len()); }", null)),
        new("MIR slice ABI subrange index cannot access adjacent owner elements", () => RunAsync(
            "fn main() { let values = [2, 4, 6]; let view: &[i32] = &values; let part = &view[1..2]; " +
            "let index: usize = 1; println!(\"{}\", part[index]); }", null)),
    ];

    private static async Task RunAsync(string source, string? expected)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RustSharp.Tests"));
        string directory = Path.Combine(root, "mir-reference-abi-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "reference-abi.dll");
            CompilationResult result = CompilerDriver.Compile(source, "reference-abi.rs", output,
                assemblyName: "ReferenceAbi", profile: CompilationProfile.SafeCoreMirV2, cancellationToken: deadline.Token);
            AssertEx.True(result.Success, Format(result.Diagnostics));
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(
                new("dotnet", [output], directory, TimeSpan.FromSeconds(10)), deadline.Token).ConfigureAwait(false);
            AssertEx.False(run.ProcessTreeCleanupIncomplete, "Reference ABI tests must reclaim their runtime process tree.");
            if (expected is null)
            {
                AssertEx.False(run.Succeeded, "Invalid slice bounds must trap.");
                AssertEx.True(run.StandardError.Contains("IndexOutOfRangeException", StringComparison.Ordinal), run.StandardError);
            }
            else
            {
                AssertEx.True(run.Succeeded, run.StandardError);
                AssertEx.Equal(expected, run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
            }
        }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "Reference ABI cleanup may delete only its own temporary workspace.");
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) => string.Join("; ",
        diagnostics.Select(static item => item.Code + ": " + item.Message));
}
