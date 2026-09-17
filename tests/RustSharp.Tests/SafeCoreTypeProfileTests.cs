using RustSharp.Compiler;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreTypeProfileTests
{
    private const CompilationProfile Profile = CompilationProfile.SafeCoreTypes;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("type profile checks library functions without emitting IL", ChecksLibraryAsync),
        new("type profile loads file modules and Cargo package inputs", LoadsWorkspaceAsync),
        new("type profile maps errors to original module spans", MapsDiagnosticsAsync),
        new("type profile rejects emission before reading or creating output", RejectsEmissionAsync),
        new("type profile preserves cancellation and legacy profile boundaries", PreservesBoundariesAsync),
        new("CLI exposes check-only type profile and rejects executable commands", ChecksCliAsync),
    ];

    private static Task ChecksLibraryAsync()
    {
        const string source = """
            type Count = u16;
            struct Pair { left: Count, right: Count }
            fn total(pair: Pair) -> Count { pair.left + pair.right }
            fn first(values: &[Count]) -> Count { values[0] }
            fn callback(value: Count) -> Count { value }
            fn inspect() {
                let values: [Count; 2] = [20, 22];
                let view: &[Count] = &values;
                let call: fn(Count) -> Count = callback;
                let answer: (Count, bool) = (call(first(view)), true);
                let pair = Pair { left: answer.0, right: 22 };
                let sum: Count = total(pair);
            }
            """;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CompilationResult result = CompilerDriver.Check(source, "types.rs", Profile, deadline.Token);
        AssertEx.True(result.Success, string.Join("; ", result.Diagnostics));
        AssertEx.True(result.Output is null, "A type check must not produce executable artifacts.");
        return Task.CompletedTask;
    }

    private static Task LoadsWorkspaceAsync() => WithWorkspace((directory, token) =>
    {
        File.WriteAllText(Path.Combine(directory, "main.rs"), "mod child; fn main() { let value: u64 = child::value(); }");
        File.WriteAllText(Path.Combine(directory, "child.rs"), "pub fn value() -> u64 { 42 }");
        File.WriteAllText(Path.Combine(directory, "Cargo.toml"), "[package]\nname = \"type_check\"\nversion = \"0.1.0\"\n[lib]\npath = \"main.rs\"\n");
        CompilationResult file = CompilerDriver.CheckFile(Path.Combine(directory, "main.rs"), Profile, token);
        AssertEx.True(file.Success, string.Join("; ", file.Diagnostics));
        CompilationResult cargo = CompilerDriver.CheckFile(Path.Combine(directory, "Cargo.toml"), Profile, token);
        AssertEx.True(cargo.Success, string.Join("; ", cargo.Diagnostics));
        AssertEx.Equal(3, Directory.GetFiles(directory).Length, "Checking a package must not create artifacts.");
        return Task.CompletedTask;
    });

    private static Task MapsDiagnosticsAsync() => WithWorkspace((directory, token) =>
    {
        const string child = "pub fn value() -> bool { 42 }";
        string childPath = Path.Combine(directory, "child.rs");
        File.WriteAllText(Path.Combine(directory, "main.rs"), "mod child; fn main() { child::value(); }");
        File.WriteAllText(childPath, child);
        CompilationResult result = CompilerDriver.CheckFile(Path.Combine(directory, "main.rs"), Profile, token);
        AssertEx.False(result.Success, "Invalid module types must fail checking.");
        Diagnostic diagnostic = result.Diagnostics.First(static diagnostic => diagnostic.Code == "RST2002");
        AssertEx.Equal(childPath, diagnostic.SourcePath!);
        AssertEx.True(diagnostic.Span.Length > 0 && diagnostic.Span.End <= child.Length,
            "Type diagnostics must retain a nonempty span in the original module.");
        return Task.CompletedTask;
    });

    private static Task RejectsEmissionAsync() => WithWorkspace((directory, token) =>
    {
        string source = Path.Combine(directory, "missing.rs");
        string outputDirectory = Path.Combine(directory, "output");
        string output = Path.Combine(outputDirectory, "program.dll");
        CompilationResult file = CompilerDriver.CompileFile(source, output, profile: Profile, cancellationToken: token);
        CompilationResult memory = CompilerDriver.Compile("fn main() {}", source, output,
            profile: Profile, cancellationToken: token);
        AssertEx.Equal("RSC0009", file.Diagnostics.Single().Code);
        AssertEx.Equal("RSC0009", memory.Diagnostics.Single().Code);
        AssertEx.False(file.Success || memory.Success, "Type-only profiles must not enter the emission pipeline.");
        AssertEx.False(Directory.Exists(outputDirectory), "Rejection must precede output directory and lock creation.");
        return Task.CompletedTask;
    });

    private static Task PreservesBoundariesAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string source = "fn main() { let value: u64 = 42; }";
        CompilationResult types = CompilerDriver.Check(source, "types.rs", Profile, deadline.Token);
        AssertEx.True(types.Success, string.Join("; ", types.Diagnostics));
        CompilationResult legacy = CompilerDriver.Check(source, "types.rs", CompilationProfile.SafeCorePrimitives, deadline.Token);
        AssertEx.False(legacy.Success, "Type profile support must not widen the executable primitive profile.");
        CompilationResult external = CompilerDriver.Check("mod external;", "memory.rs", Profile, deadline.Token);
        AssertEx.False(external.Success, "String checks must not implicitly read external source files.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => CompilerDriver.Check(source, "cancelled.rs", Profile, cancelled.Token));
        AssertEx.Throws<OperationCanceledException>(() => CompilerDriver.CheckFile("cancelled.rs", Profile, cancelled.Token));
        AssertEx.Throws<OperationCanceledException>(() => CompilerDriver.CompileFile("cancelled.rs", "cancelled.dll",
            profile: Profile, cancellationToken: cancelled.Token));
        return Task.CompletedTask;
    }

    private static Task ChecksCliAsync() => WithWorkspace(async (directory, token) =>
    {
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string cli = Path.Combine(repository, "src", "RustSharp.Cli", "bin", configuration, "net10.0", "rsc.dll");
        AssertEx.True(File.Exists(cli), $"Build the solution before running CLI tests: {cli}");
        string source = Path.Combine(directory, "types.rs");
        File.WriteAllText(source, "fn main() { let value: u64 = 42; }");
        var runner = new BoundedProcessRunner();
        BoundedProcessResult help = await runner.RunAsync(new("dotnet", [cli, "--help"], directory,
            TimeSpan.FromSeconds(10)), token).ConfigureAwait(false);
        AssertEx.True(help.Succeeded && help.StandardOutput.Contains("safe-core-types-v1", StringComparison.Ordinal),
            "Help must describe the type-checking profile.");
        AssertEx.False(help.ProcessTreeCleanupIncomplete, "Help must reclaim its process tree.");
        BoundedProcessResult check = await runner.RunAsync(new("dotnet", [cli, "check", source, "--profile", "safe-core-types-v1"],
            directory, TimeSpan.FromSeconds(10)), token).ConfigureAwait(false);
        AssertEx.True(check.Succeeded, check.StandardError);
        AssertEx.False(check.ProcessTreeCleanupIncomplete, "CLI checking must reclaim its process tree.");
        string[] commands = ["build", "compile", "run", "publish"];
        foreach (string command in commands)
        {
            token.ThrowIfCancellationRequested();
            string output = Path.Combine(directory, command, "program.dll");
            BoundedProcessResult result = await runner.RunAsync(new("dotnet",
                [cli, command, source, "--profile", "safe-core-types-v1", "--output", output],
                directory, TimeSpan.FromSeconds(10)), token).ConfigureAwait(false);
            AssertEx.Equal(1, result.ExitCode!.Value, result.StandardError);
            AssertEx.True(result.StandardError.Contains("RSC0009", StringComparison.Ordinal), result.StandardError);
            AssertEx.False(Directory.Exists(Path.GetDirectoryName(output)), "Rejected commands must not create output directories.");
            AssertEx.False(result.ProcessTreeCleanupIncomplete, "Rejected CLI commands must reclaim their process trees.");
        }
    });

    private static async Task WithWorkspace(Func<string, CancellationToken, Task> action)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(root, "type-profile-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        Directory.CreateDirectory(directory);
        try { await action(directory, deadline.Token).ConfigureAwait(false); }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                Path.GetFileName(directory).StartsWith("type-profile-", StringComparison.Ordinal),
                "Cleanup must target this test's owned workspace.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
