using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private sealed class Budget(int maximum, CancellationToken token, int milliseconds = 2000)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private int count;
        internal void Step()
        {
            token.ThrowIfCancellationRequested();
            if (++count > maximum || clock.ElapsedMilliseconds >= milliseconds) throw new TimeoutException("Independent count/time budget exceeded.");
            if (count % 1024 == 0) { Console.Error.WriteLine("documentation FD bounded work " + count.ToString(CultureInfo.InvariantCulture)); Thread.Yield(); }
        }
    }

    internal static async Task<int> RunAsync(string outputPath)
    {
        string output = Path.GetFullPath(outputPath);
        if (File.Exists(output)) throw new IOException("Fresh resource report required.");
        string reportDirectory = Path.GetDirectoryName(output) ?? throw new InvalidDataException("Report directory absent.");
        Directory.CreateDirectory(reportDirectory);
        string nonce = Guid.NewGuid().ToString("N");
        string root = Path.Combine(reportDirectory, "fd-fixture-" + nonce);
        Directory.CreateDirectory(root);
        using var ownershipCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string ownership = CaptureOwnership(nonce, root, output, ownershipCancellation.Token);
        File.WriteAllText(Path.Combine(root, "OWNER.json"), ownership);
        var rows = new List<Row>(18);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var wall = Stopwatch.StartNew();
        string state = OperatingSystem.IsLinux() ? "FAIL" : "NOT_APPLICABLE";
        string? failure = null;
        Exception? reportFailure = null;
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
                Fd[] overall = NewPersistent(baseline, final, final, cancellation.Token);
                bool fixtureLeak = HasFixture(overall, cancellation.Token), operationFailed = false;
                bool unknown = overall.Length != 0 || HasUnknown(baseline, cancellation.Token) || HasUnknown(final, cancellation.Token);
                var analysis = new Budget(18, cancellation.Token);
                for (int index = 0; index < rows.Count && index < 18; index++)
                {
                    analysis.Step(); Row row = rows[index]; if (row.Warmup) continue;
                    operationFailed |= row.Error is not null;
                    fixtureLeak |= HasFixture(row.PersistentNew, cancellation.Token);
                    unknown |= row.PersistentNew.Length != 0 || HasUnknown(row.Before, cancellation.Token) || HasUnknown(row.After, cancellation.Token) || HasUnknown(row.Settled, cancellation.Token);
                }
                state = operationFailed || fixtureLeak ? "FAIL" : unknown ? "UNKNOWN" : "PASS_PENDING_ROOT_REVIEW";
            }
        }
        catch (Exception error) { failure = error.ToString(); state = "FAIL"; }
        finally
        {
            try { WriteReport(output, root, state, failure, rows, baseline, final, wall.ElapsedMilliseconds, ownership); }
            catch (Exception reportError)
            {
                // Preserve any partial original; never replace it with a successful report.
                reportFailure = reportError;
                try { File.WriteAllText(output + ".FAIL.txt", "FAIL: report incomplete\n" + reportError + "\npriorFailure=" + failure + "\nnonce=" + nonce + "\nfixture=" + root); }
                catch (Exception sidecarError) { reportFailure = new AggregateException("Report and failure sidecar both failed.", reportError, sidecarError); }
            }
        }
        if (reportFailure is not null) throw new IOException("Resource report failed; prior task failure: " + failure, reportFailure);
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
        string[] hashes = new string[5]; var hashing = new Budget(5, token);
        for (int index = 0; index < 5; index++) { hashing.Step(); hashes[index] = Hash(originals[index]); }
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
                    Require(!model.IsSuccessful && model.Output is null && HasDiagnostic(model.Diagnostics, "RSTD3001", token), "Actual unsupported dependency must reject.");
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
                    else Require(!result.Published && result.RetainedLeasePath is null && HasDiagnostic(result.Diagnostics, "RSTD2001", token), "Failure/cancel must reclaim its owned publication lease.");
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
        return new(Names[mode], warmup, repetition, outcome, failure, before, after, settled, NewPersistent(before, after, settled, token));
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
        return new(name, rows.ToArray());
    }

    private static Dictionary<int, string?> Index(Snapshot snapshot, CancellationToken token)
    {
        Require(snapshot.Entries.Length <= 4096, "FD inventory bound.");
        var result = new Dictionary<int, string?>(snapshot.Entries.Length); var work = new Budget(4096, token);
        for (int index = 0; index < snapshot.Entries.Length && index < 4096; index++)
        { work.Step(); Fd fd = snapshot.Entries[index]; Require(result.TryAdd(fd.Number, fd.Target), "Duplicate raw FD number."); }
        return result;
    }
    private static Fd[] NewPersistent(Snapshot before, Snapshot after, Snapshot settled, CancellationToken token)
    {
        var previous = Index(before, token); var observed = Index(after, token);
        Require(settled.Entries.Length <= 4096, "Settled FD inventory bound.");
        var result = new List<Fd>(); var work = new Budget(4096, token);
        for (int index = 0; index < settled.Entries.Length && index < 4096; index++)
        {
            work.Step(); Fd fd = settled.Entries[index];
            if (fd.Kind != "snapshot-observer" && observed.TryGetValue(fd.Number, out string? seen) && seen == fd.Target && (!previous.TryGetValue(fd.Number, out string? old) || old != fd.Target)) result.Add(fd);
        }
        return result.ToArray();
    }
    private static bool HasFixture(Fd[] entries, CancellationToken token)
    {
        Require(entries.Length <= 4096, "FD classification bound."); var work = new Budget(4096, token);
        for (int index = 0; index < entries.Length && index < 4096; index++) { work.Step(); if (entries[index].Kind.StartsWith("fixture-", StringComparison.Ordinal)) return true; }
        return false;
    }
    private static bool HasUnknown(Snapshot snapshot, CancellationToken token)
    {
        Require(snapshot.Entries.Length <= 4096, "FD unknown inventory bound."); var work = new Budget(4096, token);
        for (int index = 0; index < snapshot.Entries.Length && index < 4096; index++) { work.Step(); if (snapshot.Entries[index].Error is not null) return true; }
        return false;
    }
    private static bool HasDiagnostic(IReadOnlyList<RustSharp.Syntax.Diagnostic> diagnostics, string code, CancellationToken token)
    {
        Require(diagnostics.Count <= 4096, "Diagnostic inventory bound."); var work = new Budget(4096, token);
        for (int index = 0; index < diagnostics.Count && index < 4096; index++) { work.Step(); if (diagnostics[index].Code == code) return true; }
        return false;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void WritePackage(string path, string name)
    {
        Directory.CreateDirectory(Path.Combine(path, "src"));
        File.WriteAllText(Path.Combine(path, "Cargo.toml"), "[package]\nname=\"" + name + "\"\nversion=\"0.1.0\"\nedition=\"2021\"\n[lib]\npath=\"src/lib.rs\"\n");
        File.WriteAllText(Path.Combine(path, "src", "lib.rs"), "pub fn f() {}\n");
    }

    private static string ReadProc(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path); using var output = new MemoryStream(); var buffer = new byte[4096]; var work = new Budget(17, token);
        for (int chunk = 0; chunk < 17; chunk++)
        {
            work.Step(); int count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0) return Encoding.UTF8.GetString(output.ToArray());
            Require(output.Length + count <= 65536, "Native proc identity64KiB bound."); output.Write(buffer, 0, count);
        }
        throw new TimeoutException("Native proc identity chunk bound.");
    }
    private static string CaptureOwnership(string nonce, string root, string output, CancellationToken token)
    {
        using var self = Process.GetCurrentProcess(); using var bytes = new MemoryStream(); using var json = new Utf8JsonWriter(bytes);
        json.WriteStartObject(); json.WriteString("owner", nameof(P2DocumentationResourceRunner)); json.WriteString("nonce", nonce);
        json.WriteNumber("pid", Environment.ProcessId); json.WriteString("processStartUtc", self.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        json.WriteString("managedCommandLine", Environment.CommandLine); json.WriteString("fixtureRoot", root); json.WriteString("reportPath", output); json.WriteBoolean("retained", true);
        json.WriteStartArray("nativeParentChain"); int pid = Environment.ProcessId; bool complete = false; string? error = null;
        var work = new Budget(8, token, 5000);
        if (OperatingSystem.IsLinux())
        {
            for (int depth = 0; depth < 8 && pid > 0; depth++)
            {
                work.Step();
                try
                {
                    string prefix = "/proc/" + pid.ToString(CultureInfo.InvariantCulture);
                    string stat = ReadProc(prefix + "/stat", token), command = ReadProc(prefix + "/cmdline", token);
                    int boundary = stat.LastIndexOf(") ", StringComparison.Ordinal); Require(boundary > 0, "Native stat command delimiter absent.");
                    string[] fields = stat[(boundary + 2)..].Split(' ', 128, StringSplitOptions.RemoveEmptyEntries);
                    Require(fields.Length >= 20, "Native stat fields incomplete.");
                    int parent = int.Parse(fields[1], CultureInfo.InvariantCulture);
                    json.WriteStartObject(); json.WriteNumber("pid", pid); json.WriteNumber("parentPid", parent); json.WriteString("nativeStartTicks", fields[19]);
                    json.WriteString("rawStat", stat); json.WriteString("rawCommandLineNulSeparated", command); json.WriteEndObject();
                    if (parent == 0) { complete = true; break; } Require(parent != pid, "Native ancestry cycle."); pid = parent;
                }
                catch (Exception ancestorError) { error = ancestorError.ToString(); break; }
            }
        }
        else error = "NOT_APPLICABLE: Linux native /proc identity only.";
        json.WriteEndArray(); json.WriteBoolean("nativeParentChainComplete", complete); json.WriteString("nativeParentChainError", error); json.WriteEndObject(); json.Flush();
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
    private static void WriteReport(string output, string root, string state, string? failure, List<Row> rows, Snapshot? baseline, Snapshot? final, long elapsed, string ownership)
    {
        using var reportCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = reportCancellation.Token; var work = new Budget(18, token, 10000);
        Require(rows.Count <= 18, "Report row bound."); int warmups = 0, measured = 0;
        for (int index = 0; index < rows.Count && index < 18; index++) { work.Step(); if (rows[index].Warmup) warmups++; else measured++; }
        work = new Budget(18, token, 10000);
        using var process = Process.GetCurrentProcess();
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var json = new Utf8JsonWriter(stream, new() { Indented = true });
        json.WriteStartObject(); json.WriteString("taskId", "P2-08.03.same-process-linux-fd"); json.WriteString("state", state);
        json.WriteBoolean("nativeLinux", OperatingSystem.IsLinux()); json.WriteNumber("pid", Environment.ProcessId);
        json.WriteString("processStartUtc", process.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        using var owner = JsonDocument.Parse(ownership); json.WritePropertyName("ownership"); owner.RootElement.WriteTo(json);
        json.WriteString("fixtureRoot", root); json.WriteBoolean("fixturesRetained", true); json.WriteNumber("warmupDenominator", 6);
        json.WriteNumber("measuredCaseDenominator", 6); json.WriteNumber("repetitions", 2); json.WriteNumber("measuredExecutionDenominator", 12);
        json.WriteNumber("warmupExecuted", warmups); json.WriteNumber("measuredExecuted", measured);
        json.WriteNumber("elapsedMilliseconds", elapsed); json.WriteString("failure", failure); json.WriteBoolean("forcedGc", false);
        json.WriteString("runtimeFdPolicy", "Warmup deltas exposed; every persistent measured new runtime/unknown FD is UNKNOWN, never silently excluded.");
        json.WritePropertyName("baseline"); WriteSnapshot(json, baseline, token); json.WritePropertyName("final"); WriteSnapshot(json, final, token);
        json.WriteStartArray("cases");
        for (int index = 0; index < rows.Count && index < 18; index++)
        {
            work.Step(); Row row = rows[index];
            json.WriteStartObject(); json.WriteString("id", row.Id); json.WriteBoolean("warmup", row.Warmup); json.WriteNumber("repetition", row.Repetition);
            json.WriteString("outcome", row.Outcome); json.WriteString("error", row.Error);
            json.WritePropertyName("before"); WriteSnapshot(json, row.Before, token); json.WritePropertyName("after"); WriteSnapshot(json, row.After, token);
            json.WritePropertyName("settled"); WriteSnapshot(json, row.Settled, token); json.WritePropertyName("persistentNew"); WriteFds(json, row.PersistentNew, token); json.WriteEndObject();
        }
        json.WriteEndArray(); json.WriteBoolean("leafClosed", false); json.WriteBoolean("fullP2Closure", false); json.WriteEndObject(); json.Flush(); stream.Flush(true);
    }
    private static void WriteSnapshot(Utf8JsonWriter json, Snapshot? snapshot, CancellationToken token)
    {
        if (snapshot is null) { json.WriteNullValue(); return; }
        json.WriteStartObject(); json.WriteString("name", snapshot.Name); json.WriteNumber("rawCount", snapshot.Entries.Length);
        Require(snapshot.Entries.Length <= 4096, "Snapshot report bound."); int observers = 0; var work = new Budget(4096, token);
        for (int index = 0; index < snapshot.Entries.Length && index < 4096; index++) { work.Step(); if (snapshot.Entries[index].Kind == "snapshot-observer") observers++; }
        json.WriteNumber("observerCount", observers);
        json.WritePropertyName("entries"); WriteFds(json, snapshot.Entries, token); json.WriteEndObject();
    }
    private static void WriteFds(Utf8JsonWriter json, Fd[] entries, CancellationToken token)
    {
        Require(entries.Length <= 4096, "FD report bound."); var work = new Budget(4096, token);
        json.WriteStartArray();
        for (int index = 0; index < entries.Length && index < 4096; index++)
        { work.Step(); Fd fd = entries[index]; json.WriteStartObject(); json.WriteNumber("fd", fd.Number); json.WriteString("target", fd.Target); json.WriteString("kind", fd.Kind); json.WriteString("error", fd.Error); json.WriteEndObject(); }
        json.WriteEndArray();
    }
}
