using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using RustSharp.Compiler.Documentation;

namespace RustSharp.Tests;

internal static class P2DocumentationResourceRunner
{
    private static readonly string[] Names = ["workspace-publish", "existing-target", "dependency-reject", "workspace-mid-cancel", "publish-mid-failure", "publish-mid-cancel"];
    private sealed record Fd(int Number, string? Target, string Kind, string? Error);
    private sealed record Snapshot(string Name, Fd[] Entries);
    private sealed record Row(string Id, bool Warmup, int Repetition, string Outcome, string? Error,
        Snapshot Before, Snapshot After, Snapshot Settled, Fd[] PersistentNew);

    internal static async Task<int> RunAsync(string outputPath)
    {
        string output = Path.GetFullPath(outputPath);
        if (File.Exists(output)) throw new IOException("Fresh resource report required.");
        string reportDirectory = Path.GetDirectoryName(output) ?? throw new InvalidDataException("Report directory absent.");
        Directory.CreateDirectory(reportDirectory);
        string root = Path.Combine(reportDirectory, "fd-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "OWNER.json"), "{\"owner\":\"P2DocumentationResourceRunner\",\"retained\":true}");
        var rows = new List<Row>(18);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var wall = Stopwatch.StartNew();
        string state = OperatingSystem.IsLinux() ? "FAIL" : "NOT_APPLICABLE";
        string? failure = null;
        Snapshot? baseline = null, final = null;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // Six explicit warmups include a one-case tiny trial before the remaining batch.
                for (int mode = 0; mode < 6 && wall.Elapsed.TotalSeconds < 60; mode++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    Row row = await RunCaseAsync(root, mode, true, 0, cancellation.Token).ConfigureAwait(false);
                    rows.Add(row);
                    if (row.Error is not null) throw new InvalidOperationException("Warmup failed: " + row.Error);
                    Console.Error.WriteLine("documentation FD warmup " + (mode + 1).ToString(CultureInfo.InvariantCulture) + "/6");
                }
                if (rows.Count != 6) throw new TimeoutException("Six warmups incomplete.");
                baseline = Capture("measured-baseline", root, cancellation.Token);
                for (int repetition = 0; repetition < 2 && wall.Elapsed.TotalSeconds < 60; repetition++)
                {
                    for (int mode = 0; mode < 6 && wall.Elapsed.TotalSeconds < 60; mode++)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        rows.Add(await RunCaseAsync(root, mode, false, repetition, cancellation.Token).ConfigureAwait(false));
                        Console.Error.WriteLine("documentation same-process FD case " + Names[mode] + " repetition " + repetition.ToString(CultureInfo.InvariantCulture));
                    }
                }
                final = Capture("measured-final", root, cancellation.Token);
                if (rows.Count != 18) throw new TimeoutException("Fixed twelve measured cases incomplete.");
                Row[] measured = rows.Where(static item => !item.Warmup).ToArray();
                Fd[] overall = NewPersistent(baseline, final, final);
                bool fixtureLeak = measured.Any(static row => row.PersistentNew.Any(static fd => fd.Kind.StartsWith("fixture-", StringComparison.Ordinal))) || overall.Any(static fd => fd.Kind.StartsWith("fixture-", StringComparison.Ordinal));
                bool unknown = measured.Any(static row => row.PersistentNew.Length != 0 || HasUnknown(row.Before) || HasUnknown(row.After) || HasUnknown(row.Settled)) || overall.Length != 0 || HasUnknown(baseline) || HasUnknown(final);
                state = measured.Any(static row => row.Error is not null) || fixtureLeak ? "FAIL" : unknown ? "UNKNOWN" : "PASS_PENDING_ROOT_REVIEW";
            }
        }
        catch (Exception error) { failure = error.ToString(); state = "FAIL"; }
        finally { WriteReport(output, root, state, failure, rows, baseline, final, wall.ElapsedMilliseconds); }
        return state == "PASS_PENDING_ROOT_REVIEW" ? 0 : state == "NOT_APPLICABLE" ? 2 : 1;
    }

    private static async Task<Row> RunCaseAsync(string root, int mode, bool warmup, int repetition, CancellationToken token)
    {
        string fixture = Path.Combine(root, (warmup ? "warmup-" : "measure-") + Names[mode] + "-" + repetition.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(fixture);
        WritePackage(Path.Combine(fixture, "a"), "alpha");
        WritePackage(Path.Combine(fixture, "b"), "beta");
        string manifest = Path.Combine(fixture, "Cargo.toml"), target = Path.Combine(fixture, "published");
        File.WriteAllText(manifest, "[workspace]\nmembers=[\"a\",\"b\"]\n");
        if (mode == 2) File.AppendAllText(Path.Combine(fixture, "b", "Cargo.toml"), "[dependencies]\nalpha={path=\"../a\"}\n");
        if (mode == 1) { Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "user.txt"), "original"); }
        string[] originals = [manifest, Path.Combine(fixture, "a", "Cargo.toml"), Path.Combine(fixture, "a", "src", "lib.rs"), Path.Combine(fixture, "b", "Cargo.toml"), Path.Combine(fixture, "b", "src", "lib.rs")];
        string[] hashes = originals.Select(Hash).ToArray();
        Snapshot before = Capture("before", root, token);
        string outcome = "FAIL"; string? failure = null;
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (mode == 3)
            {
                bool cancelled = false;
                try { _ = DocumentationWorkspace.Create(manifest, progress: (count, _) => { if (count == 1) operation.Cancel(); }, cancellationToken: operation.Token); }
                catch (OperationCanceledException) when (operation.IsCancellationRequested && !token.IsCancellationRequested) { cancelled = true; }
                Require(cancelled && !Directory.Exists(target), "Actual between-member cancellation required.");
                outcome = "EXPECTED_CANCEL";
            }
            else
            {
                var model = DocumentationWorkspace.Create(manifest, cancellationToken: operation.Token);
                if (mode == 2)
                {
                    Require(!model.IsSuccessful && model.Output is null && model.Diagnostics.Any(static item => item.Code == "RSTD3001"), "Actual unsupported dependency must reject.");
                    outcome = "EXPECTED_REJECTION";
                }
                else
                {
                    Require(model.IsSuccessful && model.Output is not null, "Actual workspace/model/render failed.");
                    Action<DocumentationPublicationProgress>? progress = mode == 4
                        ? state => { if (state.FilesStaged == 1) throw new IOException("FD fixture staged-file failure"); }
                        : mode == 5 ? state => { if (state.FilesStaged == 1) operation.Cancel(); } : null;
                    var result = await DocumentationPublisher.PublishNewAsync(model.Output!, target, new(), progress, operation.Token).ConfigureAwait(false);
                    if (mode == 0) Require(result.Published && result.Diagnostics.Count == 0, "Actual success must publish.");
                    else Require(!result.Published && result.RetainedLeasePath is null && result.Diagnostics.Any(static item => item.Code == "RSTD2001"), "Failure/cancel must reclaim its owned publication lease.");
                    if (mode == 1) Require(File.ReadAllText(Path.Combine(target, "user.txt")) == "original", "Existing target changed.");
                    if (mode is 4 or 5) Require(!Directory.Exists(target), "Failed/cancelled publication leaked partial target.");
                    outcome = mode == 0 ? "PUBLISHED" : mode == 5 ? "EXPECTED_CANCEL" : "EXPECTED_REJECTION";
                }
            }
            var checkClock = Stopwatch.StartNew(); int checkedFiles = 0;
            for (int index = 0; index < 5 && checkClock.ElapsedMilliseconds < 2000; index++)
            { token.ThrowIfCancellationRequested(); Require(Hash(originals[index]) == hashes[index], "Source changed."); checkedFiles++; }
            Require(checkedFiles == 5, "Five original hashes incomplete.");
            using var entries = Directory.EnumerateFileSystemEntries(fixture, ".rustsharp-docs-*", SearchOption.TopDirectoryOnly).GetEnumerator();
            Require(!entries.MoveNext(), "Owned staging path remains.");
        }
        catch (Exception error) { failure = error.ToString(); }
        Snapshot after = Capture("after", root, token);
        await Task.Delay(50, token).ConfigureAwait(false);
        Snapshot settled = Capture("settled", root, token);
        return new(Names[mode], warmup, repetition, outcome, failure, before, after, settled, NewPersistent(before, after, settled));
    }

    private static Snapshot Capture(string name, string fixtureRoot, CancellationToken token)
    {
        var clock = Stopwatch.StartNew(); var rows = new List<Fd>();
        string self = "/proc/" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "/fd";
        using var entries = Directory.EnumerateFileSystemEntries("/proc/self/fd").GetEnumerator();
        bool complete = false;
        for (int count = 0; count < 4096 && clock.ElapsedMilliseconds < 2000; count++)
        {
            token.ThrowIfCancellationRequested();
            if (!entries.MoveNext()) { complete = true; break; }
            int number = int.Parse(Path.GetFileName(entries.Current), CultureInfo.InvariantCulture);
            try
            {
                string? target = new FileInfo(entries.Current).LinkTarget;
                string kind = target == self || target == "/proc/self/fd" ? "snapshot-observer"
                    : target is null ? "unknown-target"
                    : target.StartsWith(fixtureRoot + "/", StringComparison.Ordinal) ? Directory.Exists(target) ? "fixture-directory" : "fixture-file"
                    : target.EndsWith(".dll", StringComparison.Ordinal) || target.EndsWith("/dotnet", StringComparison.Ordinal) ? "runtime-image-candidate"
                    : target.StartsWith("anon_inode:", StringComparison.Ordinal) ? "runtime-anonymous-candidate"
                    : target.StartsWith('/') ? Directory.Exists(target) ? "other-directory" : "other-file" : "unknown-owner";
                rows.Add(new(number, target, kind, target is null ? "readlink returned no target" : null));
            }
            catch (IOException error) { rows.Add(new(number, null, "unknown-readlink-race", error.Message)); }
        }
        if (!complete) throw new TimeoutException("FD snapshot4096 entries/2seconds incomplete.");
        return new(name, rows.OrderBy(static row => row.Number).ToArray());
    }

    private static Fd[] NewPersistent(Snapshot before, Snapshot after, Snapshot settled) => settled.Entries
        .Where(fd => fd.Kind != "snapshot-observer" && after.Entries.Any(item => item.Number == fd.Number && item.Target == fd.Target)
            && !before.Entries.Any(item => item.Number == fd.Number && item.Target == fd.Target)).ToArray();
    private static bool HasUnknown(Snapshot snapshot) => snapshot.Entries.Any(static fd => fd.Error is not null);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void WritePackage(string path, string name)
    {
        Directory.CreateDirectory(Path.Combine(path, "src"));
        File.WriteAllText(Path.Combine(path, "Cargo.toml"), "[package]\nname=\"" + name + "\"\nversion=\"0.1.0\"\nedition=\"2021\"\n[lib]\npath=\"src/lib.rs\"\n");
        File.WriteAllText(Path.Combine(path, "src", "lib.rs"), "pub fn f() {}\n");
    }

    private static void WriteReport(string output, string root, string state, string? failure, List<Row> rows, Snapshot? baseline, Snapshot? final, long elapsed)
    {
        using var process = Process.GetCurrentProcess();
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var json = new Utf8JsonWriter(stream, new() { Indented = true });
        json.WriteStartObject(); json.WriteString("taskId", "P2-08.03.same-process-linux-fd"); json.WriteString("state", state);
        json.WriteBoolean("nativeLinux", OperatingSystem.IsLinux()); json.WriteNumber("pid", Environment.ProcessId);
        json.WriteString("processStartUtc", process.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        json.WriteString("fixtureRoot", root); json.WriteBoolean("fixturesRetained", true); json.WriteNumber("warmupDenominator", 6);
        json.WriteNumber("measuredCaseDenominator", 6); json.WriteNumber("repetitions", 2); json.WriteNumber("measuredExecutionDenominator", 12);
        json.WriteNumber("warmupExecuted", rows.Count(static row => row.Warmup)); json.WriteNumber("measuredExecuted", rows.Count(static row => !row.Warmup));
        json.WriteNumber("elapsedMilliseconds", elapsed); json.WriteString("failure", failure); json.WriteBoolean("forcedGc", false);
        json.WriteString("runtimeFdPolicy", "Warmup deltas exposed; every persistent measured new runtime/unknown FD is UNKNOWN, never silently excluded.");
        json.WritePropertyName("baseline"); WriteSnapshot(json, baseline); json.WritePropertyName("final"); WriteSnapshot(json, final);
        json.WriteStartArray("cases");
        foreach (Row row in rows)
        {
            json.WriteStartObject(); json.WriteString("id", row.Id); json.WriteBoolean("warmup", row.Warmup); json.WriteNumber("repetition", row.Repetition);
            json.WriteString("outcome", row.Outcome); json.WriteString("error", row.Error);
            json.WritePropertyName("before"); WriteSnapshot(json, row.Before); json.WritePropertyName("after"); WriteSnapshot(json, row.After);
            json.WritePropertyName("settled"); WriteSnapshot(json, row.Settled); json.WritePropertyName("persistentNew"); WriteFds(json, row.PersistentNew); json.WriteEndObject();
        }
        json.WriteEndArray(); json.WriteBoolean("leafClosed", false); json.WriteBoolean("fullP2Closure", false); json.WriteEndObject(); json.Flush(); stream.Flush(true);
    }
    private static void WriteSnapshot(Utf8JsonWriter json, Snapshot? snapshot)
    {
        if (snapshot is null) { json.WriteNullValue(); return; }
        json.WriteStartObject(); json.WriteString("name", snapshot.Name); json.WriteNumber("rawCount", snapshot.Entries.Length);
        json.WriteNumber("observerCount", snapshot.Entries.Count(static fd => fd.Kind == "snapshot-observer"));
        json.WritePropertyName("entries"); WriteFds(json, snapshot.Entries); json.WriteEndObject();
    }
    private static void WriteFds(Utf8JsonWriter json, Fd[] entries)
    {
        json.WriteStartArray();
        foreach (Fd fd in entries)
        { json.WriteStartObject(); json.WriteNumber("fd", fd.Number); json.WriteString("target", fd.Target); json.WriteString("kind", fd.Kind); json.WriteString("error", fd.Error); json.WriteEndObject(); }
        json.WriteEndArray();
    }
}
