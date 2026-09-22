using RustSharp.Compiler;
using RustSharp.CodeGen.IL;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreGenericProfileTests
{
    private const CompilationProfile Profile = CompilationProfile.SafeCoreGenerics;
    private const string ProfileName = "safe-core-generics-v1";
    private const string GenericSource = "fn id<T>(value: T) -> T { value } fn main() { println!(\"{}\", id::<i32>(42)); }";
    private static readonly string[] WorkspaceInputs = ["main.rs", "Cargo.toml"];
    private static readonly string[] EmissionCommands = ["build", "compile", "run", "publish"];

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("generic profile checks source without emitting artifacts", ChecksSourceAsync),
        new("generic profile checks and executes file modules and Cargo package inputs", LoadsWorkspaceAsync),
        new("generic profile maps body errors to original module spans", MapsDiagnosticsAsync),
        new("generic profile rejects invalid bodies and absent executable roots before output creation", RejectsEmissionAsync),
        new("generic profile preserves cancellation and previous profile boundaries", PreservesBoundariesAsync),
        new("CLI checks builds and runs generic programs and rejects invalid publish input", ChecksCliAsync),
    ];

    private static Task ChecksSourceAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CompilationResult result = CompilerDriver.Check(GenericSource, "generics.rs", Profile, deadline.Token);
        AssertEx.True(result.Success, string.Join("; ", result.Diagnostics));
        AssertEx.True(result.Output is null, "Generic checking must not produce executable artifacts.");
        CompilationResult library = CompilerDriver.Check("fn id<T>(value: T) -> T { value }",
            "generic-library.rs", Profile, deadline.Token);
        AssertEx.True(library.Success, string.Join("; ", library.Diagnostics));
        AssertEx.True(library.Output is null, "Library checking must not require executable output.");
        return Task.CompletedTask;
    }

    private static Task LoadsWorkspaceAsync() => WithWorkspace(async (directory, token) =>
    {
        File.WriteAllText(Path.Combine(directory, "main.rs"), "mod child; fn main() { println!(\"{}\", child::id::<i32>(42)); }");
        File.WriteAllText(Path.Combine(directory, "child.rs"), "pub fn id<T>(value: T) -> T { value }");
        File.WriteAllText(Path.Combine(directory, "Cargo.toml"),
            "[package]\nname = \"generic_check\"\nversion = \"0.1.0\"\n[lib]\npath = \"main.rs\"\n");
        CompilationResult file = CompilerDriver.CheckFile(Path.Combine(directory, "main.rs"), Profile, token);
        AssertEx.True(file.Success, string.Join("; ", file.Diagnostics));
        CompilationResult cargo = CompilerDriver.CheckFile(Path.Combine(directory, "Cargo.toml"), Profile, token);
        AssertEx.True(cargo.Success, string.Join("; ", cargo.Diagnostics));
        AssertEx.True(file.Output is null && cargo.Output is null, "File and Cargo checks must not emit artifacts.");
        AssertEx.Equal(3, Directory.GetFiles(directory).Length, "Checking must retain only the source inputs.");
        AssertEx.Equal(0, Directory.GetDirectories(directory).Length, "Checking must not create output directories.");
        foreach (string input in WorkspaceInputs)
        {
            string output = Path.Combine(directory, Path.GetFileNameWithoutExtension(input), "program.dll");
            CompilationResult compiled = CompilerDriver.CompileFile(Path.Combine(directory, input), output,
                profile: Profile, cancellationToken: token);
            AssertEx.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
            await AssertRunAsync(output, directory, token).ConfigureAwait(false);
        }
    });

    private static Task MapsDiagnosticsAsync() => WithWorkspace((directory, token) =>
    {
        const string child = "// original child source\npub fn id<T>(value: T) -> T { true }";
        string childPath = Path.Combine(directory, "child.rs");
        File.WriteAllText(Path.Combine(directory, "main.rs"), "mod child; fn main() { child::id::<i32>(42); }");
        File.WriteAllText(childPath, child);
        CompilationResult result = CompilerDriver.CheckFile(Path.Combine(directory, "main.rs"), Profile, token);
        AssertEx.False(result.Success, "A generic body returning bool for arbitrary T must fail checking.");
        Diagnostic diagnostic = result.Diagnostics.First(static value => value.Code == SafeCoreGenericDiagnosticCodes.BodyMismatch);
        AssertEx.Equal(childPath, diagnostic.SourcePath!);
        AssertEx.True(diagnostic.Span.Length > 0 && diagnostic.Span.Start >= 0 && diagnostic.Span.End <= child.Length,
            "The diagnostic must retain a nonempty span within the original child source.");
        AssertEx.True(child.Substring(diagnostic.Span.Start, diagnostic.Span.Length).Contains("true", StringComparison.Ordinal),
            "The mapped span must cover the invalid return value in the original child file.");
        return Task.CompletedTask;
    });

    private static Task RejectsEmissionAsync() => WithWorkspace((directory, token) =>
    {
        string missingSource = Path.Combine(directory, "missing.rs");
        string outputDirectory = Path.Combine(directory, "output");
        string output = Path.Combine(outputDirectory, "program.dll");
        CompilationResult file = CompilerDriver.CompileFile(missingSource, output, profile: Profile, cancellationToken: token);
        CompilationResult library = CompilerDriver.Compile("fn id<T>(value: T) -> T { value }", missingSource, output,
            profile: Profile, cancellationToken: token);
        CompilationResult invalid = CompilerDriver.Compile("fn bad<T>(value: T) -> T { true } fn main() {}", missingSource, output,
            profile: Profile, cancellationToken: token);
        AssertEx.Equal(SafeCoreGenericClrLowering.MissingEntryPoint, library.Diagnostics.Single().Code);
        AssertEx.Equal(SafeCoreGenericDiagnosticCodes.BodyMismatch, invalid.Diagnostics.Single().Code);
        foreach (CompilationResult result in new[] { file, library, invalid })
        {
            AssertEx.False(result.Success, "Invalid executable inputs must not enter emission.");
            AssertEx.True(result.Output is null, "Rejected emission must not expose output artifacts.");
            Diagnostic diagnostic = result.Diagnostics.Single();
            AssertEx.False(diagnostic.Code == "RSC0009", "Generic emission must diagnose the actual input failure.");
        }
        AssertEx.False(Directory.Exists(outputDirectory), "Rejection must precede output directory and lock creation.");
        AssertEx.Equal(0, Directory.GetFiles(directory).Length, "Rejected emission must not write artifacts or locks.");
        return Task.CompletedTask;
    });

    private static Task PreservesBoundariesAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CompilationProfile[] previous =
            [CompilationProfile.VerticalSlice, CompilationProfile.SafeCorePrimitives, CompilationProfile.SafeCoreTypes];
        foreach (CompilationProfile profile in previous)
        {
            CompilationResult result = CompilerDriver.Check(GenericSource, "generics.rs", profile, deadline.Token);
            AssertEx.False(result.Success, $"Generic support must not widen the {profile} profile.");
        }
        CompilationResult external = CompilerDriver.Check("mod external; fn main() {}", "memory.rs", Profile, deadline.Token);
        AssertEx.False(external.Success, "In-memory generic checking must not implicitly read external modules.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => CompilerDriver.Check(GenericSource, "cancelled.rs", Profile, cancelled.Token));
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
        string source = Path.Combine(directory, "generics.rs");
        File.WriteAllText(source, GenericSource);
        var runner = new BoundedProcessRunner();
        BoundedProcessResult help = await runner.RunAsync(new("dotnet", [cli, "--help"], directory,
            TimeSpan.FromSeconds(10)), token).ConfigureAwait(false);
        AssertEx.True(help.Succeeded && help.StandardOutput.Contains(ProfileName, StringComparison.Ordinal),
            "Help must describe the generic checking profile.");
        AssertEx.False(help.ProcessTreeCleanupIncomplete, "Help must reclaim its process tree.");
        BoundedProcessResult check = await runner.RunAsync(new("dotnet", [cli, "check", source, "--profile", ProfileName],
            directory, TimeSpan.FromSeconds(10)), token).ConfigureAwait(false);
        AssertEx.True(check.Succeeded, check.StandardError);
        AssertEx.False(check.ProcessTreeCleanupIncomplete, "CLI generic checking must reclaim its process tree.");
        string[] commands = ["build", "compile", "run"];
        foreach (string command in commands)
        {
            token.ThrowIfCancellationRequested();
            string outputDirectory = Path.Combine(directory, command);
            string output = Path.Combine(outputDirectory, "program.dll");
            BoundedProcessResult result = await runner.RunAsync(new("dotnet",
                [cli, command, source, "--profile", ProfileName, "--output", output],
                directory, TimeSpan.FromSeconds(10)), token).ConfigureAwait(false);
            AssertEx.True(result.Succeeded, result.StandardError);
            AssertEx.True(File.Exists(output), "Executable generic commands must emit an assembly.");
            AssertEx.False(result.ProcessTreeCleanupIncomplete, "CLI commands must reclaim their process trees.");
            if (command == "run")
                AssertEx.True(result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Contains("42"), result.StandardOutput);
            else await AssertRunAsync(output, directory, token).ConfigureAwait(false);
        }
        string invalidSource = Path.Combine(directory, "invalid.rs");
        File.WriteAllText(invalidSource, "fn bad<T>(value: T) -> T { true } fn main() {}");
        foreach (string command in EmissionCommands)
        {
            string outputDirectory = Path.Combine(directory, "invalid-" + command);
            string output = command == "publish" ? outputDirectory : Path.Combine(outputDirectory, "program.dll");
            BoundedProcessResult result = await runner.RunAsync(new("dotnet",
                [cli, command, invalidSource, "--profile", ProfileName, "--output", output],
                directory, TimeSpan.FromSeconds(10)), token).ConfigureAwait(false);
            AssertEx.Equal(1, result.ExitCode!.Value, result.StandardError);
            AssertEx.True(result.StandardError.Contains(SafeCoreGenericDiagnosticCodes.BodyMismatch, StringComparison.Ordinal), result.StandardError);
            AssertEx.False(Directory.Exists(outputDirectory), "Invalid generic source must fail before output or publish host creation.");
            AssertEx.False(result.ProcessTreeCleanupIncomplete, "Rejected CLI commands must reclaim their process trees.");
        }
    });

    private static async Task AssertRunAsync(string assembly, string directory, CancellationToken token)
    {
        BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(new("dotnet", [assembly], directory,
            TimeSpan.FromSeconds(10)), token).ConfigureAwait(false);
        AssertEx.True(run.Succeeded, run.StandardError);
        AssertEx.False(run.ProcessTreeCleanupIncomplete, "Generic execution must reclaim the process tree.");
        AssertEx.Equal("42\n", run.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static async Task WithWorkspace(Func<string, CancellationToken, Task> action)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string directory = Path.Combine(root, "generic-profile-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        Directory.CreateDirectory(directory);
        try { await action(directory, deadline.Token).ConfigureAwait(false); }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                Path.GetFileName(directory).StartsWith("generic-profile-", StringComparison.Ordinal),
                "Cleanup must target this test's owned workspace.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
