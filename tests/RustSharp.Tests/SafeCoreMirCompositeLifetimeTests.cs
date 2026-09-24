using RustSharp.Compiler;
using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class SafeCoreMirCompositeLifetimeTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR aggregate lifetime rejects returning an inner local reference", () => RejectAsync(
            "fn bad(input: &i32) -> (&i32, i32) { let owner = 7; (&owner, 1) } fn main() {}", "RSO1005")),
        new("MIR aggregate lifetime retains loans when references are packed", () => RejectAsync(
            "fn main() { let mut owner = 7; let tuple = (&owner, 1); owner = 8; println!(\"{}\", *tuple.0); }", "RSO1002")),
        new("MIR aggregate lifetime rejects a mutable reference used after packing", () => RejectAsync(
            "fn main() { let mut owner = 7; let reference = &mut owner; let tuple = (reference, 1); *reference = 8; println!(\"{}\", *tuple.0); }", "RSO1001")),
        new("MIR nested lifetime rejects a reference to a local reference slot", () => RejectAsync(
            "fn bad(input: &i32) -> &&i32 { let local = input; &local } fn main() {}", "RSO1005")),
        new("MIR static lifetime accepts immutable promoted storage", () => AcceptAsync(
            "fn value() -> &'static i32 { &42 } fn main() { println!(\"{}\", *value()); }")),
        new("MIR static lifetime rejects strengthening an input lifetime", () => RejectAsync(
            "fn bad(input: &i32) -> &'static i32 { input } fn main() {}", "RSO1005")),
        new("MIR static lifetime rejects local storage passed to static parameter", () => RejectAsync(
            "fn read(input: &'static i32) -> i32 { *input } fn main() { let owner = 7; println!(\"{}\", read(&owner)); }", "RSO1005")),
        new("MIR slice lifetime rejects returning a local array view", () => RejectAsync(
            "fn bad(input: &i32) -> &[i32] { let owner = [1, 2]; &owner } fn main() {}", "RSO1005")),
        new("MIR slice lifetime rejects writes through a shared view", () => RejectAsync(
            "fn main() { let owner = [1, 2]; let view: &[i32] = &owner; view[0] = 3; }", null)),
        new("MIR nested lifetime rejects writing an unrelated elided input into caller storage", () => RejectAsync(
            "fn replace(slot: &mut &i32, value: &i32) { *slot = value; } fn main() {}", "RSO1005")),
        new("MIR static lifetime rejects local storage in an annotated binding", () => RejectAsync(
            "fn main() { let owner = 7; let reference: &'static i32 = &owner; println!(\"{}\", *reference); }", "RSO1005")),
        new("MIR static lifetime rejects local storage in a declared reference field", () => RejectAsync(
            "struct Holder { value: &'static i32 } fn main() { let owner = 7; let holder = Holder { value: &owner }; println!(\"{}\", *holder.value); }", "RSO1005")),
    ];

    private static Task AcceptAsync(string source)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = CompilerDriver.Check(source, "composite-lifetime.rs", CompilationProfile.SafeCoreMirV2, timeout.Token);
        AssertEx.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        return Task.CompletedTask;
    }

    private static Task RejectAsync(string source, string? code)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = CompilerDriver.Check(source, "composite-lifetime.rs", CompilationProfile.SafeCoreMirV2, timeout.Token);
        AssertEx.False(result.Success, "Invalid lifetime or loan use must fail before emission.");
        if (code is not null) AssertEx.True(result.Diagnostics.Any(d => d.Code == code),
            string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        return Task.CompletedTask;
    }
}
