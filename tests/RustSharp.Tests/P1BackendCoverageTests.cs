using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json.Nodes;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Conformance;
using static RustSharp.Conformance.P1BackendCoverageEvidence;

namespace RustSharp.Tests;

internal static class P1BackendCoverageTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private const string Candidate = "1111111111111111111111111111111111111111", Tree = "2222222222222222222222222222222222222222";
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 exact backend coverage binds six frozen registrations and 24 unique cells", MappingAsync),
        new("P1 exact backend coverage preserves original repeated operand source bytes", SourceAsync),
        new("P1 exact backend coverage retains hand MIR and supplementary Rust witnesses", DualWitnessAsync),
        new("P1 exact backend coverage emits the frozen nested owner mutation result", HandMirAsync),
        new("P1 exact backend coverage rejects counterfeit candidate host and reduced denominators", CounterfeitAsync),
        new("P1 exact backend coverage rejects duplicate JSON and cancellation budgets", BoundsAsync),
        new("P1 exact backend coverage requires two independent physical native reports", PhysicalAsync),
        new("P1 exact backend coverage source PE preserves original repeated operand execution", SourcePeAsync),
    ];

    private static Task MappingAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string json = File.ReadAllText(Path.Combine(Root, "tools/RustSharp.Conformance/fixtures", P1GateCoverageContract.ManifestFileName));
        var manifest = P1GateCoverageContract.ParseManifest(json, Root, deadline.Token); ValidateMapping(manifest);
        string[] cells = Fixtures.SelectMany(f => Backends.SelectMany(b => Rids.Select(r => CellId(f, b, r)))).ToArray();
        AssertEx.Equal(6, Fixtures.Count); AssertEx.Equal(24, cells.Length); AssertEx.Equal(24, cells.Distinct(StringComparer.Ordinal).Count());
        var missing = manifest with { Requirements = manifest.Requirements.Where(r => r.Id != "P1-REQ-022").ToArray() };
        AssertEx.Throws<InvalidOperationException>(() => ValidateMapping(missing)); return Task.CompletedTask;
    }
    private static Task SourceAsync()
    {
        string original = (string)typeof(SafeCoreMirV2ProfileTests).GetField("Source", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        AssertEx.Equal(original, Fixtures[0].Source); AssertEx.Equal(HashText(original), Fixtures[0].SourceSha256);
        AssertEx.True(Fixtures.All(f => f.Source.Contains("fn main()", StringComparison.Ordinal) && f.ExpectedOutput.Length > 0), "All source witnesses must execute actual behavior."); return Task.CompletedTask;
    }
    private static Task DualWitnessAsync()
    {
        Fixture fixture = Fixtures.Single(f => f.RequirementId == "P1-REQ-022");
        AssertEx.True(fixture.HandMir, "The original nested place case is hand-authored MIR.");
        AssertEx.Equal(2, fixture.WitnessIds.Count); AssertEx.True(fixture.WitnessIds[0].EndsWith(":frozen-hand-mir", StringComparison.Ordinal), "The frozen registration cannot be replaced by source equivalence.");
        AssertEx.True(fixture.Source.Contains("&mut value.payload.1[index]", StringComparison.Ordinal) && fixture.Source.Contains("*selected = 41", StringComparison.Ordinal), "Source must exercise field, tuple, dynamic array and mutable indirect ownership.");
        AssertEx.True(NativeHostSource(true).Contains("Console.WriteLine(global::RustSharp.Generated.Program.Main())", StringComparison.Ordinal), "Native host must print the real generated return value."); return Task.CompletedTask;
    }
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The untrimmed test harness loads its freshly generated frozen MIR PE.")]
    private static Task HandMirAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); GeneratedAssembly pe = EmitFrozenHandMir("BackendFrozenTest", deadline.Token);
        var context = new AssemblyLoadContext("p1-backend-frozen-" + Guid.NewGuid().ToString("N"), true);
        try { using var stream = new MemoryStream(pe.PeImage, false); Assembly loaded = context.LoadFromStream(stream); AssertEx.Equal(41, (int)loaded.EntryPoint!.Invoke(null, [])!); }
        finally { context.Unload(); }
        return Task.CompletedTask;
    }
    private static JsonObject Prefix() => new()
    {
        ["schemaVersion"] = 1, ["profile"] = Profile, ["evidenceKind"] = "p1-exact-backend-gap-native-witnesses", ["candidateSha"] = Candidate, ["candidateTreeSha"] = Tree,
        ["targetRuntimeIdentifier"] = "win-x64", ["hostRuntimeIdentifier"] = "win-x64", ["processArchitecture"] = "X64", ["osArchitecture"] = "X64", ["nativeExecution"] = true,
        ["compilerProfile"] = "safe-core-mir-p1-v2", ["dropCleanupProfile"] = "legacy-v1",
        ["summary"] = new JsonObject { ["fixtureDenominator"] = 6, ["cellDenominator"] = 24, ["localCellDenominator"] = 12, ["maximumSelectedFixtures"] = 6, ["passed"] = 12, ["blocked"] = 12, ["failed"] = 0, ["localClosure"] = true },
    };
    private static Task CounterfeitAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        byte[] baseline = Encoding.UTF8.GetBytes(Prefix().ToJsonString());
        ValidationResult envelope = ValidateNativeEnvelope(baseline, new(Candidate, Tree, "win-x64"), deadline.Token);
        AssertEx.True(envelope.Valid, "The mutation baseline must have a valid identity, candidate, native host, profile and complete local summary envelope.");
        AssertEx.Equal(0, envelope.ClosedCells, "An envelope baseline does not assert any execution cells.");
        AssertEx.False(envelope.ArtifactContentVerified, "Envelope validation does not inspect physical artifacts.");
        AssertEx.False(envelope.SatisfiesNativeGate, "Envelope validation cannot supply physical execution evidence.");
        ValidationResult incomplete = ValidateNativeReport(baseline, new(Candidate, Tree, "win-x64"), deadline.Token);
        AssertEx.Equal("Backend object missing: execution", incomplete.Errors.Single(), "The unmutated envelope must reach the next execution obligation.");
        Action<JsonObject>[] changes = [n => n["candidateSha"] = new string('3', 40), n => n["nativeExecution"] = false,
            n => n["hostRuntimeIdentifier"] = "linux-x64", n => n["processArchitecture"] = "Arm64", n => n["dropCleanupProfile"] = "native-v2",
            n => n["summary"]!["cellDenominator"] = 23, n => n["summary"]!["maximumSelectedFixtures"] = 1, n => n["summary"]!["passed"] = 11];
        string[] expectedErrors = ["Backend candidate/tree binding is stale.", "Backend evidence must come from its native x64 host.",
            "Backend evidence must come from its native x64 host.", "Backend evidence must come from its native x64 host.", "Backend compiler or Drop policy changed.",
            "Filtered, reduced or incomplete backend evidence cannot close the local gate.", "Filtered, reduced or incomplete backend evidence cannot close the local gate.",
            "Filtered, reduced or incomplete backend evidence cannot close the local gate."];
        for (int i = 0; i < changes.Length && i < 8; i++)
        {
            deadline.Token.ThrowIfCancellationRequested(); JsonObject node = Prefix(); changes[i](node); ValidationResult result = ValidateNativeReport(Encoding.UTF8.GetBytes(node.ToJsonString()), new(Candidate, Tree, "win-x64"), deadline.Token);
            AssertEx.False(result.Valid, "A counterfeit backend envelope cannot count as evidence.");
            AssertEx.Equal(expectedErrors[i], result.Errors.Single(), $"Mutation {i} must reject its exact binding before missing execution or artifacts; observed: {string.Join("; ", result.Errors)}");
            AssertEx.False(result.SatisfiesNativeGate, "Shape alone never closes a backend gate.");
        }
        return Task.CompletedTask;
    }
    private static Task BoundsAsync()
    {
        foreach (string text in new[] { "{\"schemaVersion\":1,\"SchemaVersion\":1}", "[]", "{\"schemaVersion\":" })
            AssertEx.False(ValidateNativeReport(Encoding.UTF8.GetBytes(text), new(Candidate, Tree, "win-x64")).Valid, "Duplicate, malformed or nonobject JSON must reject.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        AssertEx.False(ValidateNativeReport(Encoding.UTF8.GetBytes(Prefix().ToJsonString()), new(Candidate, Tree, "win-x64"), cancellation.Token).Valid, "Cancelled validation cannot close.");
        AssertEx.Throws<ArgumentException>(() => Child(Path.Combine(Root, "artifacts", "p1-backend"), Path.Combine(Root, "artifacts", "p1-backend-sibling", "fake.json")));
        return Task.CompletedTask;
    }
    private static async Task PhysicalAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); string absent = Path.Combine(Root, "artifacts", "p1-backend", "absent.json");
        ValidationResult missing = await ValidateNativeReportAsync(Root, absent, new(Candidate, Tree, "win-x64"), deadline.Token).ConfigureAwait(false);
        AssertEx.False(missing.Valid, "Missing retained bytes cannot count as physical evidence.");
        ValidationResult duplicate = await ValidateClosedMatrixAsync(Root, absent, absent, Candidate, Tree, deadline.Token).ConfigureAwait(false);
        AssertEx.False(duplicate.SatisfiesMatrixGate, "The same local report cannot close two native hosts.");
    }
    private static async Task SourcePeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); string baseDirectory = Path.Combine(Root, "artifacts", "tests"), directory = Path.Combine(baseDirectory, "p1-backend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string pe = Path.Combine(directory, "source.dll"); Fixture fixture = Fixtures[0];
            CompilationResult compiled = CompilerDriver.Compile(fixture.Source, "backend-source.rs", pe, "BackendSourceTest", CompilationProfile.SafeCoreMirV2, deadline.Token);
            AssertEx.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
            AssertEx.Equal(fixture.SourceSha256, RustSharpMetadataReader.ReadAssembly(pe).SourceSha256);
            string dotnet = Environment.GetEnvironmentVariable("RUSTSHARP_P1_DOTNET_PATH") ?? (OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            BoundedProcessResult run = await new BoundedProcessRunner().RunAsync(new(dotnet, [pe], directory, TimeSpan.FromSeconds(10)), deadline.Token).ConfigureAwait(false);
            AssertEx.True(Complete(run) && run.Succeeded, "The task-owned original PE must execute with complete tree cleanup."); AssertEx.Equal(fixture.ExpectedOutput, Normalize(run.StandardOutput));
        }
        finally
        {
            AssertEx.True(Path.GetFullPath(directory).StartsWith(Path.GetFullPath(baseDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(directory).StartsWith("p1-backend-", StringComparison.Ordinal), "Test cleanup must retain task ownership.");
            Directory.Delete(directory, true);
        }
    }
}
