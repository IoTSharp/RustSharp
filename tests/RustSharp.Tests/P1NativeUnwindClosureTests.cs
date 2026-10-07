using System.Text.Json.Nodes;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Conformance;
using RustSharp.Runtime;
using RustSharp.Semantics;

namespace RustSharp.Tests;

internal static class P1NativeUnwindClosureTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 native unwind tiny generated program carries explicit v2 PE metadata", NativeTinyAsync),
        new("P1 native unwind destructor failure visits owned fields and stops on first field failure", NativeNestedAsync),
        new("P1 native unwind field order and exactly once consumption survive nested failure", NativeOrderedAsync),
        new("P1 native unwind ordinary double panic and explicit abort retain their boundaries", NativeBoundariesAsync),
        new("P1 native unwind legacy production entry retains frozen v1 behavior", LegacyAsync),
        new("P1 native unwind preserves parent and nested child exception identity", ExceptionIdentityAsync),
        new("P1 native unwind rejects unknown profile and incompatible compiler input", RejectsProfilesAsync),
        new("P1 native unwind v5 fixed generated differential retains all 28 cases", NativeFixedDifferentialAsync),
    ];

    private const string NestedSource = """
        fn overflow(value: i32) -> i32 { value + 1 }
        fn divide(value: i32) -> i32 { 10 / value }
        struct Bad;
        impl Drop for Bad { fn drop(&mut self) { println!("bad"); println!("{}", overflow(2147483647)); } }
        struct Good;
        impl Drop for Good { fn drop(&mut self) { println!("good"); } }
        struct Owner { divisor: i32, bad: Bad, good: Good }
        impl Drop for Owner { fn drop(&mut self) { println!("owner"); println!("{}", divide(self.divisor)); } }
        fn main() {
            let owner = Owner { divisor: 0, bad: Bad, good: Good };
            println!("body"); println!("{}", overflow(2147483647)); println!("unreachable");
        }
        """;

    private static Task NativeTinyAsync() => ExecuteAsync("fn main() { println!(\"ready\"); }", "ready\n", expectAbort: false);

    private static Task NativeNestedAsync() => ExecuteAsync(NestedSource,
        OperatingSystem.IsLinux() ? "body\nowner\nbad\n" : "body\nowner\n", expectAbort: true);

    private static Task NativeOrderedAsync() => ExecuteAsync("""
        fn overflow(value: i32) -> i32 { value + 1 }
        fn divide(value: i32) -> i32 { 10 / value }
        struct First;
        impl Drop for First { fn drop(&mut self) { println!("first"); } }
        struct Bad;
        impl Drop for Bad { fn drop(&mut self) { println!("bad"); println!("{}", overflow(2147483647)); } }
        struct Last;
        impl Drop for Last { fn drop(&mut self) { println!("last"); } }
        struct Owner { divisor: i32, first: First, bad: Bad, last: Last }
        impl Drop for Owner { fn drop(&mut self) { println!("owner"); println!("{}", divide(self.divisor)); } }
        fn main() {
            let owner = Owner { divisor: 0, first: First, bad: Bad, last: Last };
            println!("body"); println!("{}", overflow(2147483647));
        }
        """, OperatingSystem.IsLinux() ? "body\nowner\nfirst\nbad\n" : "body\nowner\n", expectAbort: true);

    private static async Task NativeBoundariesAsync()
    {
        const string source = """
            fn overflow(value: i32) -> i32 { value + 1 }
            struct Bad;
            impl Drop for Bad { fn drop(&mut self) { println!("bad"); println!("{}", overflow(2147483647)); } }
            fn main() { let bad = Bad; println!("body"); println!("{}", overflow(2147483647)); }
            """;
        await ExecuteAsync(source, "body\nbad\n", expectAbort: true).ConfigureAwait(false);
        await ExecuteAsync(source, "body\n", expectAbort: true, panicStrategy: SafeCorePanicStrategy.Abort).ConfigureAwait(false);
    }

    private static Task LegacyAsync() => ExecuteAsync(NestedSource, "body\nowner\n", expectAbort: true,
        profile: SafeCoreDropCleanupProfile.LegacyV1);

    private static Task ExceptionIdentityAsync()
    {
        var body = new OverflowException("body");
        var owner = new DivideByZeroException("owner");
        var field = new InvalidOperationException("field");
        var child = (RustGeneratedAbortException)RustGeneratedPanic.DoublePanic(owner, field);
        var combined = (RustGeneratedAbortException)RustGeneratedPanic.PreserveNestedAbort(body, child);
        AssertEx.True(ReferenceEquals(body, combined.Panic), "The original body panic cannot be replaced by the child abort.");
        AssertEx.True(ReferenceEquals(child, combined.CleanupFailure), "The complete child abort must remain available.");
        AssertEx.True(ReferenceEquals(owner, child.Panic) && ReferenceEquals(field, child.CleanupFailure), "Both nested destructor failures retain identity.");
        AssertEx.Throws<ArgumentException>(() => RustGeneratedPanic.PreserveNestedAbort(body, field));
        AssertEx.Equal(OperatingSystem.IsLinux(), RustGeneratedPanic.NativeV2UnwindsDestructorBody());
        return Task.CompletedTask;
    }

    private static Task RejectsProfilesAsync()
    {
        AssertEx.Throws<ArgumentOutOfRangeException>(() => CompilerDriver.CompileWithDropProfile("fn main() {}", "input.rs", "output.dll",
            (SafeCoreDropCleanupProfile)99));
        CompilationResult result = CompilerDriver.CompileWithDropProfile("fn main() {}", "input.rs", "output.dll",
            SafeCoreDropCleanupProfile.NativeV2, profile: CompilationProfile.VerticalSlice);
        AssertEx.False(result.Success, "An incompatible compiler profile cannot silently ignore NativeV2.");
        AssertEx.Equal("RSC0010", result.Diagnostics.Single().Code);
        AssertEx.Equal("p1-drop-closure-v4", P1DropDifferentialRunner.ClosureProfile);
        AssertEx.Equal("p1-drop-closure-v5", P1DropDifferentialRunner.NativeClosureProfile);
        return Task.CompletedTask;
    }

    private static async Task NativeFixedDifferentialAsync()
    {
        string root = RepositoryRoot();
        string path = Path.Combine(root, "artifacts", "p1-drop", "native-v5-" + Guid.NewGuid().ToString("N") + ".json");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(270));
        P1DropDifferentialRunner.Result result = await P1DropDifferentialRunner.RunNativeV5Async(root, path,
            Tool("RUSTSHARP_RUSTC_PATH", "rustc"), Tool("RUSTSHARP_DOTNET_PATH", "dotnet"),
            cancellationToken: deadline.Token).ConfigureAwait(false);
        AssertEx.True(result.ExpectedContractSatisfied, "The complete v5 emitted/native oracle suite must pass without new differences: " + path);
        AssertEx.Equal(26, result.Passed);
        AssertEx.Equal(2, result.ContractDifferences);
        AssertEx.True(new FileInfo(path).Length <= 4 * 1024 * 1024, "Native v5 report read is bounded.");
        JsonObject report = JsonNode.Parse(await File.ReadAllTextAsync(path, deadline.Token).ConfigureAwait(false))!.AsObject();
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        P1DropDifferentialRunner.ValidateClosedReport(report, fullRoot, deadline.Token);
        P1DropDifferentialRunner.ValidateClosedReport(report, fullRoot + Path.DirectorySeparatorChar, deadline.Token);
        JsonObject outside = report.DeepClone().AsObject();
        outside["cases"]![0]!["rustcArtifact"]!["path"] = Path.Combine(fullRoot, "artifacts", "p1-drop-sibling", "oracle");
        InvalidOperationException outsideFailure = AssertEx.Throws<InvalidOperationException>(() =>
            P1DropDifferentialRunner.ValidateClosedReport(outside, fullRoot + Path.DirectorySeparatorChar, deadline.Token));
        AssertEx.Equal("A Drop artifact is outside its declared owned directory.", outsideFailure.Message,
            "Root normalization must still reject a sibling directory that shares the owned prefix.");
        JsonObject changed = report.DeepClone().AsObject();
        changed["profile"] = P1DropDifferentialRunner.ClosureProfile;
        AssertEx.Throws<InvalidOperationException>(() => P1DropDifferentialRunner.ValidateClosedReport(changed, root, deadline.Token));
        AssertEx.False(report["hostContract"]!["fullP1LanguageGateApproved"]!.GetValue<bool>(), "Generated suite acceptance is separate from the full P1 language gate.");
        Console.WriteLine("Native v5 retained review evidence: " + path);
    }

    private static async Task ExecuteAsync(string source, string expectedOutput, bool expectAbort,
        SafeCoreDropCleanupProfile profile = SafeCoreDropCleanupProfile.NativeV2,
        SafeCorePanicStrategy panicStrategy = SafeCorePanicStrategy.Unwind)
    {
        string root = Path.Combine(RepositoryRoot(), "artifacts", "tests");
        string directory = Path.Combine(root, "p1-native-unwind-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory)) throw new IOException("Native unwind fixture directory must be task-owned and new.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "program.rs");
            string outputPath = Path.Combine(directory, "program.dll");
            await File.WriteAllTextAsync(sourcePath, source, deadline.Token).ConfigureAwait(false);
            CompilationResult compiled = profile == SafeCoreDropCleanupProfile.LegacyV1
                ? CompilerDriver.CompileWithPanicStrategy(source, sourcePath, outputPath, panicStrategy,
                    "P1NativeUnwind", CompilationProfile.SafeCoreMirV2, deadline.Token)
                : CompilerDriver.CompileWithDropProfile(source, sourcePath, outputPath, profile, panicStrategy,
                    "P1NativeUnwind", CompilationProfile.SafeCoreMirV2, deadline.Token);
            AssertEx.True(compiled.Success, string.Join("; ", compiled.Diagnostics.Select(static item => item.Code + ":" + item.Message)));
            AssertEx.Equal(profile == SafeCoreDropCleanupProfile.NativeV2 ? SafeCoreDropCleanupProfiles.NativeV2MetadataValue : "legacy",
                P1DropDifferentialRunner.ReadDropCleanupMetadata(outputPath, deadline.Token) ?? "legacy");
            BoundedProcessResult ran = await new BoundedProcessRunner().RunAsync(new(Tool("RUSTSHARP_DOTNET_PATH", "dotnet"),
                [outputPath], directory, TimeSpan.FromSeconds(10), started => Console.WriteLine(
                    $"Native unwind process: pid={started.ProcessId} parent={started.ParentProcessId} start={started.StartedAt:O} command={started.CommandLine}")),
                deadline.Token).ConfigureAwait(false);
            AssertEx.Equal(BoundedProcessTermination.Exited, ran.Termination);
            AssertEx.False(ran.ProcessTreeCleanupIncomplete || ran.OutputTruncated || ran.OutputDrainTimedOut,
                "An incomplete process outcome is not semantic evidence.");
            AssertEx.Equal(expectedOutput, ran.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
            if (expectAbort)
            {
                AssertEx.Equal(RustGeneratedPanic.AbortExitCode, ran.ExitCode ?? -1);
                AssertEx.True(ran.StandardError.Contains(panicStrategy == SafeCorePanicStrategy.Abort
                    ? "RustSharp panic abort:" : "RustSharp double panic abort:", StringComparison.Ordinal), ran.StandardError);
            }
            else AssertEx.Equal(0, ran.ExitCode ?? -1);
        }
        finally
        {
            string fullPath = Path.GetFullPath(directory);
            AssertEx.True(fullPath.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                Path.GetFileName(fullPath).StartsWith("p1-native-unwind-", StringComparison.Ordinal), "Only this fixture's owned path can be removed.");
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
        }
    }

    private static string Tool(string configuration, string fallback) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(configuration))
            ? OperatingSystem.IsWindows() ? fallback + ".exe" : fallback
            : Environment.GetEnvironmentVariable(configuration)!;

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
}
