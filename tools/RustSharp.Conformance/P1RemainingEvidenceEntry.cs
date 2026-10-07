using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RustSharp.Conformance;

/// <summary>Stable entry points for existing frozen acceptance contracts.</summary>
internal static class P1RemainingEvidenceEntry
{
    private static readonly JsonSerializerOptions ProofJsonOptions = new() { WriteIndented = true };
    internal static bool Handles(string[] args) => args.Length > 0 && args[0] is
        "--p1-gate-harness" or "--p1-drop-native-v5" or "--validate-p1-drop-generated";

    internal static async Task<int> RunAsync(string root, string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(270));
        try
        {
            if (args.Length == 6 && args[0] == "--p1-gate-harness")
            {
                string mapPath = Path.Combine(root, "tools/RustSharp.Conformance/fixtures", P1GateCoverageContract.ManifestFileName);
                CapturedFile mapping = await ReadAsync(root, mapPath, 262_144, deadline.Token).ConfigureAwait(false);
                P1GateCoverageContract.Manifest map = P1GateCoverageContract.ParseManifest(mapping.Text, root, deadline.Token);
                string input = Child(root, args[1]), output = Child(root, args[5]);
                CapturedFile harness = await ReadAsync(root, input, 16_777_216, deadline.Token).ConfigureAwait(false);
                P1GateCoverageContract.HarnessAudit audit = P1GateCoverageContract.ValidateHarnessEvidence(map,
                    harness.Text, args[2], args[3], args[4], deadline.Token);
                string english = (await ReadAsync(root, Path.Combine(root, "docs/roadmap/P1.md"), 1_048_576, deadline.Token).ConfigureAwait(false)).Text;
                string chinese = (await ReadAsync(root, Path.Combine(root, "docs/roadmap/P1_zh.md"), 1_048_576, deadline.Token).ConfigureAwait(false)).Text;
                var clock = Stopwatch.StartNew();
                foreach (P1GateCoverageContract.Requirement requirement in map.Requirements)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (clock.Elapsed >= TimeSpan.FromSeconds(15)) throw new InvalidOperationException("Leaf status reconciliation exceeded fifteen seconds.");
                    // Aggregate ownership is an exit obligation, not a self prerequisite.
                    foreach (string leaf in requirement.ImplementationLeaves)
                    {
                        string prefix = @"(?m)^\| " + Regex.Escape(leaf) + @" \| ";
                        if (!Regex.IsMatch(english, prefix + @"✅ Complete \|", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
                            !Regex.IsMatch(chinese, prefix + @"✅ 已完成 \|", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                            throw new InvalidOperationException("A required implementation leaf remains open: " + requirement.Id + ": " + leaf);
                    }
                }
                var proof = new
                {
                    schemaVersion = 1, evidenceKind = "p1-source-bound-harness-coverage", profile = "p1-gate-coverage-v1",
                    candidateSha = args[2], candidateTreeSha = args[3], runtimeIdentifier = args[4],
                    manifestSha256 = mapping.Sha256, inputHarnessPath = args[1], inputHarnessSha256 = harness.Sha256,
                    valid = true, harnessEvidenceClosed = audit.HarnessEvidenceClosed, nativeExecutionClaim = false,
                    requirementDenominator = 40, catalogueDenominator = 160, boundTestCount = audit.BoundTestCount,
                    pendingBackendCells = audit.PendingBackendCells, fullP1Closure = false,
                };
                await WriteAsync(root, output, JsonSerializer.Serialize(proof, ProofJsonOptions), deadline.Token).ConfigureAwait(false);
                return 0;
            }
            if (args.Length == 5 && args[0] == "--p1-drop-native-v5" &&
                int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out int limit) && limit is 1 or 28)
            {
                P1DropDifferentialRunner.Result result = await P1DropDifferentialRunner.RunNativeV5Async(root,
                    args[1], args[2], args[3], limit, deadline.Token).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new { result.ReportPath, result.Passed, result.ContractDifferences,
                    result.Failed, result.Blocked, result.Skipped, result.CleanupComplete, result.ExpectedContractSatisfied }));
                // A tiny probe is never a fixed-suite pass.
                return result.ExpectedContractSatisfied ? 0 : limit == 1 && result.Passed == 1 &&
                    result.Failed == 0 && result.Blocked == 0 && result.CleanupComplete ? 0 : 1;
            }
            if (args.Length == 2 && args[0] == "--validate-p1-drop-generated")
            {
                string input = Child(root, args[1]);
                CapturedFile captured = await ReadAsync(root, input, 4_194_304, deadline.Token).ConfigureAwait(false);
                JsonObject document = JsonNode.Parse(captured.Text, documentOptions: new JsonDocumentOptions { MaxDepth = 32 })?.AsObject()
                    ?? throw new InvalidOperationException("Drop evidence is not an object.");
                if (document["profile"]?.GetValue<string>() != P1DropDifferentialRunner.NativeClosureProfile)
                    throw new InvalidOperationException("This gate requires NativeV5; legacy evidence cannot substitute.");
                P1DropDifferentialRunner.ValidateClosedReport(document, root, deadline.Token);
                Console.WriteLine(JsonSerializer.Serialize(new { Valid = true, ArtifactContentVerified = true,
                    FixedCases = 28, ExactRustcMatches = 26, FrozenContractDifferences = 2, FullP1LanguageGateApproved = false,
                    InputReportSha256 = captured.Sha256 }));
                return 0;
            }
            throw new ArgumentException("Invalid remaining-evidence command or fixed case bound.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or JsonException or OperationCanceledException or RegexMatchTimeoutException or TimeoutException)
        {
            Console.Error.WriteLine("Remaining P1 evidence failed: " + exception.Message);
            return 2;
        }
    }

    private static string Child(string root, string relative)
    {
        string full = Path.GetFullPath(relative, root);
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Evidence path escaped the repository.");
        return full;
    }

    private sealed record CapturedFile(string Text, string Sha256);
    private static async Task<CapturedFile> ReadAsync(string root, string path, int bound, CancellationToken token)
    {
        string full = Child(root, path);
        GuardParents(root, full, token);
        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        readDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
            65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 1 || stream.Length > bound) throw new InvalidOperationException("Evidence is empty or oversized: " + full);
        GuardParents(root, full, readDeadline.Token);
        using var captured = new MemoryStream(Math.Min(bound, 65_536));
        byte[] buffer = new byte[65_536];
        int maximumReads = checked((bound + buffer.Length - 1) / buffer.Length + 1);
        bool complete = false;
        for (int iteration = 0; iteration < maximumReads; iteration++)
        {
            readDeadline.Token.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(buffer.AsMemory(), readDeadline.Token).ConfigureAwait(false);
            if (read == 0) { complete = true; break; }
            if (captured.Length + read > bound) throw new InvalidOperationException("Evidence grew past its actual byte bound.");
            captured.Write(buffer, 0, read);
        }
        if (!complete || captured.Length == 0) throw new InvalidOperationException("Evidence exceeded its fixed read-count bound.");
        readDeadline.Token.ThrowIfCancellationRequested();
        byte[] bytes = captured.ToArray();
        string text = new UTF8Encoding(false, true).GetString(bytes);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        readDeadline.Token.ThrowIfCancellationRequested();
        return new(text, sha256);
    }

    private static void GuardParents(string root, string path, CancellationToken token)
    {
        string expectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string current = Child(root, path);
        var clock = Stopwatch.StartNew();
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (int depth = 0; depth < 32; depth++)
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed >= TimeSpan.FromSeconds(2)) throw new IOException("Parent link verification exceeded two seconds.");
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Evidence cannot traverse a file or parent link: " + current);
            if (current.Equals(expectedRoot, comparison)) return;
            current = Path.GetDirectoryName(current) ?? throw new IOException("Evidence parent chain escaped the repository.");
        }
        throw new IOException("Evidence parent chain exceeded its 32-component bound.");
    }

    private static async Task WriteAsync(string root, string path, string json, CancellationToken token)
    {
        string full = Child(root, path);
        GuardParents(root, full, token);
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(json);
        if (bytes.Length > 16_777_216) throw new IOException("Output proof exceeded its fixed byte bound.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        GuardParents(root, full, token);
        string temporary = full + ".owned-" + Guid.NewGuid().ToString("N");
        bool created = false;
        Exception? primaryFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            writeDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            // Ownership starts only after CreateNew actually succeeds.
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65_536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                int maximumWrites = checked((bytes.Length + 65_535) / 65_536);
                for (int block = 0; block < maximumWrites; block++)
                {
                    writeDeadline.Token.ThrowIfCancellationRequested();
                    int offset = checked(block * 65_536);
                    await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(65_536, bytes.Length - offset)), writeDeadline.Token).ConfigureAwait(false);
                }
                await stream.FlushAsync(writeDeadline.Token).ConfigureAwait(false);
            }
            GuardParents(root, temporary, writeDeadline.Token);
            GuardParents(root, full, writeDeadline.Token);
            // Preserve an earlier report; retries must select another fresh path.
            File.Move(temporary, full, overwrite: false);
        }
        catch (Exception exception) { primaryFailure = exception; }
        finally
        {
            if (created) cleanupFailure = CleanupOwnedProof(root, temporary);
        }
        // Cleanup failure is fatal and retains the original failure as its inner
        // evidence; finally never hides that failure with a different throw.
        if (cleanupFailure is not null)
            throw new IOException("Owned temporary proof cleanup failed: " + cleanupFailure.Message,
                primaryFailure is null ? cleanupFailure : new AggregateException(primaryFailure, cleanupFailure));
        if (primaryFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();
    }

    private static Exception? CleanupOwnedProof(string root, string temporary)
    {
        try
        {
            GuardParents(root, temporary, CancellationToken.None);
            if (File.Exists(temporary)) File.Delete(temporary);
            return File.Exists(temporary) ? new IOException("The owned temporary proof remains after cleanup.") : null;
        }
        catch (Exception exception) { return exception; }
    }
}
