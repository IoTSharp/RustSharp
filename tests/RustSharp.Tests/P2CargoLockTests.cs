using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RustSharp.Compiler;

namespace RustSharp.Tests;

internal static class P2CargoLockTests
{
    private const string Prefix = "P2 cargo lock ";
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new(Prefix + "lock-serialize", Serialize), new(Prefix + "lock-reordered", Reordered),
        new(Prefix + "locked-current", Current), new(Prefix + "locked-missing", Missing),
        new(Prefix + "locked-stale", Stale), new(Prefix + "lock-version", Version),
        new(Prefix + "lock-duplicate", Duplicate), new(Prefix + "lock-identity", Identity),
        new(Prefix + "graph-cycle", Cycle), new(Prefix + "lock-byte-boundary", ByteBoundary),
    ];

    private static Task Serialize() => WithFixture(f =>
    {
        Inventory(f); f.Graph(); CargoLockPlan plan = Success(f.Resolve());
        const string expected = "version = 4\n\n[[package]]\nname = \"alpha\"\nversion = \"0.1.0\"\ndependencies = []\n\n[[package]]\nname = \"app\"\nversion = \"0.1.0\"\ndependencies = [\"alpha 0.1.0\", \"zed 0.1.0\"]\n\n[[package]]\nname = \"zed\"\nversion = \"0.1.0\"\ndependencies = [\"alpha 0.1.0\"]\n";
        AssertEx.Equal(expected, plan.LockText);
        AssertEx.Equal("alpha@0.1.0,app@0.1.0,zed@0.1.0", string.Join(',', plan.Packages.Select(static p => p.Identity)));
        AssertEx.True(plan.LockText.StartsWith("version = 4\n\n[[package]]\nname = \"alpha\"", StringComparison.Ordinal), "Sorted lock uses v4 and exact names.");
        AssertEx.True(plan.LockText.Contains("dependencies = [\"alpha 0.1.0\", \"zed 0.1.0\"]\n", StringComparison.Ordinal), "Edges use package identities rather than dependency aliases.");
        AssertEx.False(plan.LockText.Contains('\r') || plan.LockText.Contains('\ufeff') || plan.LockText.Contains(f.Root, StringComparison.Ordinal), "Lock bytes have LF, no BOM or paths.");
        AssertEx.False(File.Exists(f.PathOf("Cargo.lock")), "Plan mode creates no files.");
        AssertEx.Equal("alpha,zed,app", string.Join(',', plan.DependencyOrder.Select(static p => p.Name)));
        AssertEx.Equal(64, plan.ActivationFingerprint.Length);
        f.Package("optional", "optional");
        f.Package("", "app", "[dependencies]\noptional = { path = \"optional\", optional = true }\n[target.'cfg(unix)'.dependencies]\nalpha = { path = \"alpha\" }\n");
        CargoLockPlan inactive = Success(f.Resolve()); AssertEx.Equal(3, inactive.Packages.Count);
        AssertEx.Equal(1, inactive.Selection.Features!.Activations.Count);
        AssertEx.Equal("alpha,optional,app", string.Join(',', inactive.DependencyOrder.Select(static p => p.Name)));
    });

    private static Task Reordered() => WithFixture(f =>
    {
        f.Graph(); CargoLockPlan first = Success(f.Resolve());
        f.Graph(reverse: true); CargoLockPlan second = Success(f.Resolve());
        AssertEx.Equal(first.LockText, second.LockText); AssertEx.Equal(first.ResolutionText, second.ResolutionText);
        AssertEx.Equal(first.ActivationFingerprint, second.ActivationFingerprint);
        AssertEx.Equal(string.Join(',', first.DependencyOrder.Select(static p => p.Name)), string.Join(',', second.DependencyOrder.Select(static p => p.Name)));
        using var relocated = new Fixture(); relocated.Graph(reverse: true);
        CargoLockPlan portable = Success(relocated.Resolve()); AssertEx.Equal(first.LockText, portable.LockText); AssertEx.Equal(first.ResolutionText, portable.ResolutionText);
    });

    private static Task Current() => WithFixture(f =>
    {
        f.Graph(); CargoLockResolutionResult written = f.Resolve(CargoLockMode.Update); CargoLockPlan plan = Success(written);
        AssertEx.True(written.FilesWritten && written.CleanupComplete, "Validated pair was written with staging cleanup.");
        string before = f.PairHash(); DateTime lockTime = File.GetLastWriteTimeUtc(f.PathOf("Cargo.lock"));
        CargoLockResolutionResult read = f.Resolve(CargoLockMode.Locked); AssertEx.Equal(plan.ResolutionText, Success(read).ResolutionText);
        AssertEx.False(read.FilesWritten, "Locked resolution never writes."); AssertEx.Equal(before, f.PairHash()); AssertEx.Equal(lockTime, File.GetLastWriteTimeUtc(f.PathOf("Cargo.lock")));
        f.Write("Cargo.lock", "\ufeff# preserved original lock bytes\r\n" + plan.LockText.Replace("\n", "\r\n", StringComparison.Ordinal));
        string decoratedHash = f.PairHash();
        Success(f.Resolve(CargoLockMode.Locked));
        AssertEx.Equal(decoratedHash, f.PairHash(), "Accepted BOM/comment/CRLF lock bytes remain unchanged.");
        f.Write("Cargo.lock", plan.LockText);
        using JsonDocument metadata = JsonDocument.Parse(plan.ResolutionText);
        AssertEx.Equal("cargo-v1", metadata.RootElement.GetProperty("resolution").GetProperty("profile").GetString()!);
        AssertEx.Equal(plan.ActivationFingerprint, metadata.RootElement.GetProperty("activationFingerprint").GetString()!);
        f.Write(".rustsharp-lock.write.guard", "pre-existing guard");
        Failure(f.Resolve(CargoLockMode.Update), CargoLockResolver.IncompatibleLockDiagnostic);
        AssertEx.Equal("pre-existing guard", File.ReadAllText(f.PathOf(".rustsharp-lock.write.guard"))); AssertEx.Equal(before, f.PairHash());
        File.Delete(f.PathOf(".rustsharp-lock.write.guard"));
        string oldLock = f.Hash("Cargo.lock"); f.BlockCompanion(); f.Package("", "app", version: "0.2.0");
        Failure(f.Resolve(CargoLockMode.Update), CargoLockResolver.IncompatibleLockDiagnostic); AssertEx.Equal(oldLock, f.Hash("Cargo.lock"));
        AssertEx.Equal("owned blocker", File.ReadAllText(f.PathOf(CargoLockResolver.ResolutionFileName + "/blocker")));
    });

    private static Task Missing() => WithFixture(f =>
    {
        f.Package("", "app"); Failure(f.Resolve(CargoLockMode.Locked), CargoLockResolver.IncompatibleLockDiagnostic);
        AssertEx.False(File.Exists(f.PathOf("Cargo.lock")) || File.Exists(f.PathOf(CargoLockResolver.ResolutionFileName)), "Missing locked inputs are not created.");
        Success(f.Resolve(CargoLockMode.Update)); File.Delete(f.PathOf(CargoLockResolver.ResolutionFileName));
        string before = f.Hash("Cargo.lock"); Failure(f.Resolve(CargoLockMode.Locked), CargoLockResolver.IncompatibleLockDiagnostic); AssertEx.Equal(before, f.Hash("Cargo.lock"));
        AssertEx.False(File.Exists(f.PathOf(CargoLockResolver.ResolutionFileName)), "Missing companion is not repaired in locked mode.");
    });

    private static Task Stale() => WithFixture(f =>
    {
        f.Graph(); Success(f.Resolve(CargoLockMode.Update)); string before = f.PairHash();
        f.Package("zed", "zed", "[dependencies]\nalpha = { path = \"../alpha\" }\n", version: "0.2.0");
        Failure(f.Resolve(CargoLockMode.Locked), CargoLockResolver.IncompatibleLockDiagnostic); AssertEx.Equal(before, f.PairHash());
        f.Graph(); f.Package("", "app", "[dependencies]\nalpha = { path = \"alpha\" }\n");
        Failure(f.Resolve(CargoLockMode.Locked), CargoLockResolver.IncompatibleLockDiagnostic); AssertEx.Equal(before, f.PairHash());
        f.Graph(); Failure(f.Resolve(CargoLockMode.Locked, rid: "linux-x64"), CargoLockResolver.IncompatibleLockDiagnostic); AssertEx.Equal(before, f.PairHash());
        f.Package("", "app", "[features]\nextra = []\n"); Success(f.Resolve(CargoLockMode.Update)); string featureBefore = f.PairHash();
        Failure(f.Resolve(CargoLockMode.Locked, features: new() { Requests = [new("extra")] }), CargoLockResolver.IncompatibleLockDiagnostic); AssertEx.Equal(featureBefore, f.PairHash());
        f.Write(CargoLockResolver.ResolutionFileName, "{\"schemaVersion\":2}\n"); string malformed = f.PairHash();
        Failure(f.Resolve(CargoLockMode.Locked), CargoLockResolver.IncompatibleLockDiagnostic); AssertEx.Equal(malformed, f.PairHash());
    });

    private static Task Version() => WithFixture(f =>
    {
        f.Package("", "app"); CargoLockPlan plan = Success(f.Resolve(CargoLockMode.Update));
        string[] variants = [plan.LockText.Replace("version = 4", "version = 3", StringComparison.Ordinal), plan.LockText + "source = \"registry\"\n", plan.LockText + "checksum = \"0000\"\n", "version = \"4\"\n" + plan.LockText[12..], "version = 4\n[[package]]\nname = \"unterminated\n"];
        foreach (string text in variants) { f.Check(); f.Write("Cargo.lock", text); string before = f.PairHash(); Failure(f.Resolve(CargoLockMode.Locked), CargoLockResolver.IncompatibleLockDiagnostic); AssertEx.Equal(before, f.PairHash()); }
    });

    private static Task Duplicate() => WithFixture(f =>
    {
        f.Graph(); CargoLockPlan plan = Success(f.Resolve(CargoLockMode.Update));
        string[] variants = [plan.LockText + "\n[[package]]\nname = \"app\"\nversion = \"0.1.0\"\n", plan.LockText.Replace("[\"alpha 0.1.0\", \"zed 0.1.0\"]", "[\"alpha 0.1.0\", \"alpha 0.1.0\"]", StringComparison.Ordinal), plan.LockText + "unknown = true\n", plan.LockText.Replace("version = 4", "version = 4\nversion = 4", StringComparison.Ordinal)];
        foreach (string text in variants) { f.Check(); f.Write("Cargo.lock", text); string before = f.PairHash(); Failure(f.Resolve(CargoLockMode.Locked), CargoLockResolver.IncompatibleLockDiagnostic); AssertEx.Equal(before, f.PairHash()); }
        f.Write("Cargo.lock", variants[3]); CargoLockResolutionResult located = f.Resolve(CargoLockMode.Locked);
        AssertEx.Equal(f.PathOf("Cargo.lock"), located.Diagnostics[0].SourcePath!); AssertEx.Equal(12, located.Diagnostics[0].Span.Start); AssertEx.Equal(7, located.Diagnostics[0].Span.Length);
    });

    private static Task Identity() => WithFixture(f =>
    {
        f.Graph(); CargoLockPlan plan = Success(f.Resolve(CargoLockMode.Update));
        string[] variants = ["version = 4\n[[package]]\nname = \"app\"\nversion = \"0.1.0\"\n", plan.LockText + "\n[[package]]\nname = \"unexpected\"\nversion = \"0.1.0\"\n", plan.LockText.Replace("alpha 0.1.0", "alpha 9.9.9", StringComparison.Ordinal), plan.LockText.Replace("alpha 0.1.0", "alpha@0.1.0", StringComparison.Ordinal), plan.LockText.Replace("name = \"alpha\"", "name = \"9invalid\"", StringComparison.Ordinal)];
        foreach (string text in variants) { f.Check(); f.Write("Cargo.lock", text); string before = f.PairHash(); Failure(f.Resolve(CargoLockMode.Locked), CargoLockResolver.IncompatibleLockDiagnostic); AssertEx.Equal(before, f.PairHash()); }
    });

    private static Task Cycle() => WithFixture(f =>
    {
        f.Package("", "app", "[dependencies]\nself = { package = \"app\", path = \".\" }\n");
        Failure(f.Resolve(CargoLockMode.Update), CargoWorkspace.DependencyCycleDiagnostic);
        AssertEx.False(File.Exists(f.PathOf("Cargo.lock")), "Cycle creates no lock.");
        f.Package("", "app", "[dependencies]\nnext = { path = \"next\" }\n"); f.Package("next", "next", "[dependencies]\nback = { package = \"app\", path = \"..\" }\n");
        Failure(f.Resolve(CargoLockMode.Locked), CargoWorkspace.DependencyCycleDiagnostic);
        AssertEx.False(File.Exists(f.PathOf(CargoLockResolver.ResolutionFileName)), "Multi-node cycle creates no companion.");
    });

    private static Task ByteBoundary() => WithFixture(f =>
    {
        f.Package("", "app"); CargoLockPlan plan = Success(f.Resolve(CargoLockMode.Update)); const int boundary = 2048;
        int generatedByteLimit = Math.Max(Encoding.UTF8.GetByteCount(plan.LockText), Encoding.UTF8.GetByteCount(plan.ResolutionText));
        Success(f.Resolve(limits: new() { MaximumLockBytes = generatedByteLimit }));
        Failure(f.Resolve(limits: new() { MaximumLockBytes = generatedByteLimit - 1 }), CargoWorkspace.LimitDiagnostic);
        CargoLockResolutionResult measured = f.Resolve(); Success(measured);
        AssertEx.Equal(measured.OperationsConsumed, f.Resolve(limits: new() { MaximumOperations = measured.OperationsConsumed }).OperationsConsumed);
        Success(f.Resolve(limits: new() { MaximumOperations = measured.OperationsConsumed }));
        Failure(f.Resolve(limits: new() { MaximumOperations = measured.OperationsConsumed - 1 }), CargoWorkspace.LimitDiagnostic);
        f.Write("Cargo.lock", plan.LockText + new string(' ', boundary - Encoding.UTF8.GetByteCount(plan.LockText)));
        Success(f.Resolve(CargoLockMode.Locked, limits: new() { MaximumLockBytes = boundary }));
        byte[] oversized = File.ReadAllBytes(f.PathOf("Cargo.lock")).Concat(new byte[] { 255 }).ToArray(); f.WriteBytes("Cargo.lock", oversized);
        Failure(f.Resolve(CargoLockMode.Locked, limits: new() { MaximumLockBytes = boundary }), CargoWorkspace.LimitDiagnostic);
        f.Write("Cargo.lock", plan.LockText); f.WriteBytes("Cargo.lock", [255]); Failure(f.Resolve(CargoLockMode.Locked), CargoLockResolver.IncompatibleLockDiagnostic);
        f.Write("Cargo.lock", plan.LockText); Failure(f.Resolve(limits: new() { MaximumOperations = 1 }), CargoWorkspace.LimitDiagnostic);
        Failure(f.Resolve(limits: new() { Timeout = TimeSpan.FromTicks(1) }), CargoWorkspace.LimitDiagnostic);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => CargoLockResolver.ResolveV1(f.PathOf("Cargo.toml"), new() { RuntimeIdentifier = "win-x64" }, cancellationToken: cancelled.Token));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => f.Resolve(limits: new() { Timeout = TimeSpan.FromSeconds(11) }));
    });

    private static void Inventory(Fixture f)
    {
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/RustSharp.Conformance/fixtures"));
        using JsonDocument contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "p2-cargo-v1-manifest.json")));
        using JsonDocument map = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "p2-cargo-v1-lock-cases.json")));
        AssertEx.Equal(78, contract.RootElement.GetProperty("cases").GetArrayLength());
        JsonElement[] rows = contract.RootElement.GetProperty("cases").EnumerateArray().Where(static e => e.GetProperty("leafId").GetString() == "P2-04.05").ToArray();
        AssertEx.Equal(10, rows.Length); AssertEx.Equal(10, All.Count); AssertEx.Equal(10, map.RootElement.GetProperty("denominator").GetInt32());
        AssertEx.Equal(10, map.RootElement.GetProperty("cases").GetArrayLength());
        for (int index = 0; index < 10; index++)
        {
            f.Check(); JsonElement mapped = map.RootElement.GetProperty("cases")[index];
            AssertEx.Equal(rows[index].GetProperty("id").GetString()!, mapped.GetProperty("id").GetString()!);
            AssertEx.Equal(Prefix + rows[index].GetProperty("id").GetString()!, All[index].Name);
            AssertEx.Equal(All[index].Name, mapped.GetProperty("testName").GetString()!);
            AssertEx.Equal(rows[index].GetProperty("expectedDiagnostic").GetString()!, mapped.GetProperty("expectedDiagnostic").GetString()!);
        }
    }
    private static Task WithFixture(Action<Fixture> action) { using var f = new Fixture(); action(f); f.Check(); return Task.CompletedTask; }
    private static CargoLockPlan Success(CargoLockResolutionResult result)
    { AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics.Select(static d => d.Code + ": " + d.Message))); return result.Plan!; }
    private static void Failure(CargoLockResolutionResult result, string code)
    { AssertEx.False(result.IsSuccessful, "Rejected lock never succeeds."); AssertEx.True(result.Plan is null && !result.FilesWritten && result.CleanupComplete, "Rejected lock exposes no plan/writes and completed cleanup."); AssertEx.Equal(1, result.Diagnostics.Count); AssertEx.Equal(code, result.Diagnostics[0].Code); }

    private sealed class Fixture : IDisposable
    {
        private readonly string _parent = Path.GetFullPath(Path.GetTempPath());
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(20));
        private readonly HashSet<string> _files = new(StringComparer.Ordinal); private readonly HashSet<string> _dirs = new(StringComparer.Ordinal); private int _writes;
        internal string Root { get; }
        internal Fixture() { Root = Path.GetFullPath(Path.Combine(_parent, "rustsharp-p2-cargo-lock-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(Root); }
        internal void Check() => _deadline.Token.ThrowIfCancellationRequested();
        internal string PathOf(string relative)
        { string full = Path.GetFullPath(Path.Combine(Root, relative)); AssertEx.True(full.StartsWith(Root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "Only unique owned root paths."); return full; }
        internal void Write(string relative, string text) => WriteBytes(relative, new UTF8Encoding(false).GetBytes(text));
        internal void WriteBytes(string relative, byte[] bytes)
        {
            Check(); AssertEx.True(++_writes <= 128, "Scenario has at most 128 writes."); string path = PathOf(relative); string parent = Path.GetDirectoryName(path)!;
            for (int depth = 0; parent != Root && depth < 16; depth++) { Check(); _dirs.Add(parent); AssertEx.True(_dirs.Count <= 256, "Tracked dirs bounded."); parent = Path.GetDirectoryName(parent)!; }
            AssertEx.Equal(Root, parent); Directory.CreateDirectory(Path.GetDirectoryName(path)!); _files.Add(path); File.WriteAllBytes(path, bytes);
        }
        internal void Package(string relative, string name, string metadata = "", string version = "0.1.0")
        { string prefix = relative.Length == 0 ? "" : relative + "/"; Write(prefix + "Cargo.toml", $"[package]\nname = \"{name}\"\nversion = \"{version}\"\n" + metadata); Write(prefix + "src/main.rs", "fn main() {}\n"); }
        internal void Graph(bool reverse = false)
        {
            Package("alpha", "alpha"); Package("zed", "zed", "[dependencies]\nalpha = { path = \"../alpha\" }\n");
            string alpha = "renamed = { package = \"alpha\", path = \"alpha\" }\n"; string zed = "zed = { path = \"zed\" }\n";
            string members = reverse ? "[\"zed\", \"alpha\"]" : "[\"alpha\", \"zed\"]";
            Package("", "app", "[workspace]\nmembers = " + members + "\nresolver = \"2\"\n[dependencies]\n" + (reverse ? zed + alpha : alpha + zed));
        }
        internal CargoLockResolutionResult Resolve(CargoLockMode mode = CargoLockMode.Plan, string rid = "win-x64", CargoFeatureOptions? features = null, CargoWorkspaceOptions? limits = null)
        {
            Check(); _files.Add(PathOf("Cargo.lock")); if (!Directory.Exists(PathOf(CargoLockResolver.ResolutionFileName))) _files.Add(PathOf(CargoLockResolver.ResolutionFileName));
            CargoLockResolutionResult result = CargoLockResolver.ResolveV1(PathOf("Cargo.toml"), new() { RuntimeIdentifier = rid }, features, new() { Mode = mode }, limits, _deadline.Token);
            string[] rootFiles = Directory.EnumerateFiles(Root).Take(33).ToArray(); AssertEx.True(rootFiles.Length <= 32, "Owned root discovery is fixed at 32 files.");
            foreach (string file in rootFiles) { Check(); AssertEx.False(Path.GetFileName(file).StartsWith(".rustsharp-lock", StringComparison.Ordinal) && !_files.Contains(file), "No owned transaction staging or new guard remains; pre-existing tracked guards survive."); }
            foreach (string file in _files) { Check(); if (File.Exists(file)) { using var reopened = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); } }
            return result;
        }
        internal string Hash(string relative) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PathOf(relative))));
        internal void BlockCompanion()
        {
            string path = PathOf(CargoLockResolver.ResolutionFileName); File.Delete(path); _files.Remove(path);
            Write(CargoLockResolver.ResolutionFileName + "/blocker", "owned blocker");
        }
        internal string PairHash() => Hash("Cargo.lock") + ":" + Hash(CargoLockResolver.ResolutionFileName);
        public void Dispose()
        {
            Stopwatch cleanup = Stopwatch.StartNew();
            void Owned(string path) { AssertEx.True(cleanup.Elapsed < TimeSpan.FromSeconds(20), "Independent cleanup deadline."); AssertEx.True(Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Delete only tracked owned paths."); }
            try
            {
                AssertEx.True(Root.StartsWith(_parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Path.GetFileName(Root).StartsWith("rustsharp-p2-cargo-lock-", StringComparison.Ordinal), "Unique verified cleanup root.");
                AssertEx.True(_files.Count <= 130 && _dirs.Count <= 256, "Cleanup item counts remain fixed.");
                foreach (string file in _files) { Owned(file); File.Delete(file); }
                foreach (string dir in _dirs.OrderByDescending(static p => p.Length)) { Owned(dir); if (Directory.Exists(dir)) Directory.Delete(dir); }
                AssertEx.True(cleanup.Elapsed < TimeSpan.FromSeconds(20), "Root cleanup is bounded."); Directory.Delete(Root); AssertEx.False(Directory.Exists(Root), "Owned fixture root removed.");
            }
            finally { _deadline.Dispose(); }
        }
    }
}
