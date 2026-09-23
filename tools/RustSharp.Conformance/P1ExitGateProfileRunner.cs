using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RustSharp.CodeGen.IL;
using RustSharp.Compiler;
using RustSharp.Runtime;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

/// <summary>
/// Runs the bounded P1 exit-gate contract.  The gate deliberately exercises
/// in-process library contracts only; it does not claim Native AOT, cross
/// platform, or rustc-oracle evidence.
/// </summary>
internal static class P1ExitGateProfileRunner
{
    internal const string ProfileName = "p1-exit-gate-v1";
    internal const string ManifestFileName = "p1-exit-gate-manifest.json";
    internal const string Scope = "in-process-library-contracts";
    internal const int ManifestVersion = 1;
    internal const int MaximumCases = 8;
    internal const int MaximumManifestBytes = 256 * 1024;
    internal const int MaximumCaseTimeoutMilliseconds = 30_000;
    internal const int MaximumDeadlineSeconds = 180;
    private const int MaximumIdLength = 96;
    private const int MaximumJsonDepth = 16;
    private const int MaximumJsonTokens = 4096;
    private const int MaximumCleanupAttempts = 40;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal static IReadOnlyList<string> ProbeNames { get; } =
    [
        "typed-mir-validation",
        "ownership-bridge",
        "drop-order",
        "panic-unwind",
        "metadata-consumer",
    ];

    internal sealed record GateLimits(
        int MaximumCases,
        int CaseTimeoutMilliseconds,
        int DeadlineSeconds);

    internal sealed record GateCase(string Id, string Probe);

    internal sealed record GateManifest(
        string Profile,
        int Version,
        string Scope,
        int Denominator,
        GateLimits Limits,
        IReadOnlyList<GateCase> Cases);

    internal sealed record ManifestValidation(
        bool Validated,
        GateManifest? Manifest,
        string? Error,
        string Sha256,
        long ByteLength,
        string RelativePath);

    internal sealed record ProbeResult(bool Succeeded, string Evidence);

    internal sealed record GateReport(
        int SchemaVersion,
        string Contract,
        string EvidenceScope,
        DateTimeOffset GeneratedAtUtc,
        ManifestReport Manifest,
        HostReport Host,
        LimitsReport Limits,
        ExecutionReport Execution,
        SummaryReport Summary,
        IReadOnlyList<CaseReport> Cases,
        string? RunDirectoryCleanupDiagnostic,
        string? BlockedReason)
    {
        public PlatformEvidenceReport PlatformEvidence { get; init; } =
            new(false, false, "This report contains no Native AOT or cross-platform evidence.");
    }

    internal sealed record ManifestReport(
        string Path,
        int Version,
        int Denominator,
        int CaseCount,
        string Sha256,
        long ByteLength,
        bool Validated,
        string? Error);

    internal sealed record PlatformEvidenceReport(
        bool NativeAot,
        bool CrossPlatform,
        string Note);

    internal sealed record HostReport(
        string OperatingSystem,
        string OsArchitecture,
        string ProcessArchitecture,
        string Framework,
        string RuntimeIdentifier,
        string RuntimeVersion)
    {
        public static HostReport Current => new(
            RuntimeInformation.OSDescription.Trim(),
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription.Trim(),
            RuntimeInformation.RuntimeIdentifier,
            Environment.Version.ToString());
    }

    internal sealed record LimitsReport(
        double CaseTimeoutMilliseconds,
        double DeadlineSeconds,
        int MaximumCases);

    internal sealed record ExecutionReport(
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        double ElapsedMilliseconds,
        bool DeadlineExpired,
        string? DeadlineDiagnostic);

    internal sealed record SummaryReport(
        string Status,
        int Denominator,
        int Executed,
        int Passed,
        int Failed,
        int Skipped);

    internal sealed record CaseReport(
        string Id,
        string Probe,
        string Status,
        double ElapsedMilliseconds,
        string? Evidence,
        string? Difference);

    internal static GateManifest ParseManifest(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
        {
            throw new ArgumentException(
                "P1 exit-gate manifest exceeds " + MaximumManifestBytes + " bytes.",
                nameof(json));
        }

        ValidateNoDuplicateProperties(Encoding.UTF8.GetBytes(json));
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                MaxDepth = MaximumJsonDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        return ParseManifest(document.RootElement);
    }

    internal static void ValidateManifest(GateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Profile, ProfileName, StringComparison.Ordinal) ||
            manifest.Version != ManifestVersion ||
            !string.Equals(manifest.Scope, Scope, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "P1 exit-gate profile, version, or evidence scope is unsupported.",
                nameof(manifest));
        }

        if (manifest.Denominator is < 1 or > MaximumCases ||
            manifest.Cases.Count != manifest.Denominator)
        {
            throw new ArgumentException(
                "P1 exit-gate denominator must equal its 1.." + MaximumCases + " case count.",
                nameof(manifest));
        }

        GateLimits limits = manifest.Limits ??
            throw new ArgumentException("P1 exit-gate limits are required.", nameof(manifest));
        if (limits.MaximumCases is < 1 or > MaximumCases ||
            limits.CaseTimeoutMilliseconds is < 1 or > MaximumCaseTimeoutMilliseconds ||
            limits.DeadlineSeconds is < 1 or > MaximumDeadlineSeconds ||
            manifest.Denominator > limits.MaximumCases)
        {
            throw new ArgumentException(
                "P1 exit-gate limits must be positive, bounded, and cover the denominator.",
                nameof(manifest));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var probes = new HashSet<string>(StringComparer.Ordinal);
        foreach (GateCase gateCase in manifest.Cases)
        {
            if (gateCase is null ||
                string.IsNullOrWhiteSpace(gateCase.Id) || gateCase.Id.Length > MaximumIdLength ||
                !gateCase.Id.All(static character =>
                    character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character == '-') ||
                !ids.Add(gateCase.Id))
            {
                throw new ArgumentException(
                    "P1 exit-gate case IDs must be unique, lowercase, bounded names.",
                    nameof(manifest));
            }

            if (!ProbeNames.Contains(gateCase.Probe, StringComparer.Ordinal) ||
                !probes.Add(gateCase.Probe))
            {
                throw new ArgumentException(
                    "P1 exit-gate cases must contain each fixed probe exactly once.",
                    nameof(manifest));
            }
        }

        if (probes.Count != ProbeNames.Count ||
            ProbeNames.Any(probe => !probes.Contains(probe)))
        {
            throw new ArgumentException(
                "P1 exit-gate manifest is missing one or more fixed probes.",
                nameof(manifest));
        }
    }

    public static async Task<int> RunAsync(
        string repositoryRoot,
        string reportPath,
        TimeSpan requestedDeadline,
        DateTimeOffset startedAtUtc,
        Stopwatch harnessClock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        ArgumentNullException.ThrowIfNull(harnessClock);
        if (requestedDeadline <= TimeSpan.Zero ||
            requestedDeadline > TimeSpan.FromSeconds(MaximumDeadlineSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedDeadline),
                "P1 exit-gate deadline must be 1.." + MaximumDeadlineSeconds + " seconds.");
        }

        string root = Path.GetFullPath(repositoryRoot);
        string fullReportPath = Path.GetFullPath(reportPath, root);
        string reportDirectory = Path.GetDirectoryName(fullReportPath)
            ?? throw new ArgumentException("P1 exit-gate report path must include a directory.", nameof(reportPath));
        Directory.CreateDirectory(reportDirectory);
        string fixturesDirectory = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures");
        string manifestPath = Path.Combine(fixturesDirectory, ManifestFileName);
        string relativeManifestPath = Path.GetRelativePath(root, manifestPath)
            .Replace(Path.DirectorySeparatorChar, '/');

        using var cancellation = new CancellationTokenSource(requestedDeadline);
        ConsoleCancelEventHandler cancelHandler = (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        ManifestValidation validation;
        try
        {
            validation = await ReadManifestAsync(
                manifestPath,
                relativeManifestPath,
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            validation = new(
                false,
                null,
                "P1 exit-gate deadline or cancellation was requested before the manifest was read.",
                string.Empty,
                0,
                relativeManifestPath);
        }

        var cases = new List<CaseReport>(validation.Manifest?.Cases.Count ?? 0);
        string? runDirectory = null;
        string? cleanupDiagnostic = null;
        string? blockedReason = validation.Error;
        GateLimits effectiveLimits = validation.Manifest?.Limits ??
            new(MaximumCases, MaximumCaseTimeoutMilliseconds, (int)Math.Min(
                requestedDeadline.TotalSeconds, MaximumDeadlineSeconds));
        try
        {
            if (validation.Validated && validation.Manifest is not null)
            {
                effectiveLimits = validation.Manifest.Limits;
                TimeSpan effectiveDeadline = TimeSpan.FromSeconds(
                    Math.Min(requestedDeadline.TotalSeconds, effectiveLimits.DeadlineSeconds));
                if (effectiveDeadline < requestedDeadline)
                {
                    cancellation.CancelAfter(effectiveDeadline);
                }

                runDirectory = Path.Combine(
                    reportDirectory,
                    ".run-p1-exit-gate-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(runDirectory);
                foreach (GateCase gateCase in validation.Manifest.Cases)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        cases.Add(new(gateCase.Id, gateCase.Probe, "skipped", 0, null,
                            "P1 exit-gate deadline or cancellation was requested."));
                        continue;
                    }

                    cases.Add(RunCase(
                        root,
                        runDirectory,
                        gateCase,
                        effectiveLimits,
                        cancellation.Token));
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            blockedReason ??= "P1 exit-gate deadline or cancellation was requested.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or JsonException)
        {
            blockedReason ??= TrimDiagnostic(exception.Message);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            if (runDirectory is not null)
            {
                cleanupDiagnostic = TryDeleteDirectory(runDirectory);
            }
        }

        if (validation.Manifest is not null && cases.Count < validation.Manifest.Cases.Count)
        {
            string reason = cancellation.IsCancellationRequested
                ? "P1 exit-gate deadline or cancellation was requested before this case started."
                : blockedReason ?? "P1 exit-gate stopped before this case started.";
            for (int index = cases.Count;
                 index < validation.Manifest.Cases.Count && index < MaximumCases;
                 index++)
            {
                GateCase gateCase = validation.Manifest.Cases[index];
                cases.Add(new(gateCase.Id, gateCase.Probe, "skipped", 0, null, reason));
            }
        }

        harnessClock.Stop();
        int denominator = validation.Manifest?.Denominator ?? 0;
        int executed = cases.Count(static item => item.Status is "passed" or "failed");
        int passed = cases.Count(static item => item.Status == "passed");
        int failed = cases.Count(static item => item.Status == "failed");
        int skipped = cases.Count(static item => item.Status == "skipped");
        string status = !validation.Validated || blockedReason is not null || cancellation.IsCancellationRequested
            ? "blocked"
            : skipped > 0
                ? "blocked"
                : failed > 0
                    ? "failed"
                    : passed == denominator && denominator > 0 ? "passed" : "blocked";
        if (status == "blocked" && blockedReason is null)
        {
            blockedReason = cancellation.IsCancellationRequested
                ? "P1 exit-gate deadline or cancellation was requested."
                : skipped > 0
                    ? "One or more P1 exit-gate probes were skipped."
                    : "P1 exit-gate did not execute its complete denominator.";
        }

        var report = new GateReport(
            1,
            ProfileName,
            Scope,
            DateTimeOffset.UtcNow,
            new(
                validation.RelativePath,
                ManifestVersion,
                validation.Manifest?.Denominator ?? 0,
                validation.Manifest?.Cases.Count ?? 0,
                validation.Sha256,
                validation.ByteLength,
                validation.Validated,
                validation.Error),
            HostReport.Current,
            new(effectiveLimits.CaseTimeoutMilliseconds, effectiveLimits.DeadlineSeconds, effectiveLimits.MaximumCases),
            new(
                startedAtUtc,
                DateTimeOffset.UtcNow,
                harnessClock.Elapsed.TotalMilliseconds,
                cancellation.IsCancellationRequested,
                cancellation.IsCancellationRequested ? "P1 exit-gate deadline or cancellation was requested." : null),
            new(status, denominator, executed, passed, failed, skipped),
            cases,
            cleanupDiagnostic,
            status == "blocked" ? blockedReason : null);

        await WriteReportAsync(fullReportPath, report).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        return status switch
        {
            "passed" => 0,
            "failed" => 1,
            _ => 2,
        };
    }

    private static CaseReport RunCase(
        string repositoryRoot,
        string runDirectory,
        GateCase gateCase,
        GateLimits limits,
        CancellationToken overallCancellation)
    {
        var clock = Stopwatch.StartNew();
        using CancellationTokenSource caseCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(overallCancellation);
        caseCancellation.CancelAfter(limits.CaseTimeoutMilliseconds);
        string caseDirectory = Path.Combine(runDirectory, gateCase.Id);
        Directory.CreateDirectory(caseDirectory);
        try
        {
            ProbeResult result = ExecuteProbe(
                gateCase.Probe,
                repositoryRoot,
                caseDirectory,
                caseCancellation.Token);
            clock.Stop();
            if (caseCancellation.IsCancellationRequested && !overallCancellation.IsCancellationRequested)
            {
                return new(gateCase.Id, gateCase.Probe, "failed", clock.Elapsed.TotalMilliseconds,
                    null, "The fixed probe exceeded its bounded case timeout.");
            }

            return new(
                gateCase.Id,
                gateCase.Probe,
                result.Succeeded ? "passed" : "failed",
                clock.Elapsed.TotalMilliseconds,
                result.Succeeded ? result.Evidence : null,
                result.Succeeded ? null : result.Evidence);
        }
        catch (OperationCanceledException) when (overallCancellation.IsCancellationRequested)
        {
            clock.Stop();
            return new(gateCase.Id, gateCase.Probe, "skipped", clock.Elapsed.TotalMilliseconds, null,
                "P1 exit-gate deadline or cancellation was requested.");
        }
        catch (OperationCanceledException)
        {
            clock.Stop();
            return new(gateCase.Id, gateCase.Probe, "failed", clock.Elapsed.TotalMilliseconds, null,
                "The fixed probe exceeded its bounded case timeout.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or SafeCoreMirLimitException or
            SafeCoreTypeInferenceLimitException)
        {
            clock.Stop();
            return new(
                gateCase.Id,
                gateCase.Probe,
                "failed",
                clock.Elapsed.TotalMilliseconds,
                null,
                TrimDiagnostic(exception.Message));
        }
    }

    private static ProbeResult ExecuteProbe(
        string probe,
        string repositoryRoot,
        string caseDirectory,
        CancellationToken cancellationToken) => probe switch
        {
            "typed-mir-validation" => ProbeTypedMir(cancellationToken),
            "ownership-bridge" => ProbeOwnershipBridge(cancellationToken),
            "drop-order" => ProbeDropOrder(cancellationToken),
            "panic-unwind" => ProbePanicUnwind(cancellationToken),
            "metadata-consumer" => ProbeMetadataConsumer(repositoryRoot, caseDirectory, cancellationToken),
            _ => throw new ArgumentException("Unknown P1 exit-gate probe '" + probe + "'.", nameof(probe)),
        };

    private static ProbeResult ProbeTypedMir(CancellationToken cancellationToken)
    {
        const string sourcePath = "p1-exit-gate/typed-mir-source.rs";
        const string sourceText = "fn p1_gate(value: i32) -> i32 { value + 1 }";
        SafeCoreMirPipelineResult pipeline = SafeCoreMirPipeline.Analyze(
            sourceText,
            sourcePath,
            new SafeCoreMirPipelineOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                MaximumOperations = 10_000,
                MaximumFunctions = 8,
                MaximumBlocksPerFunction = 64,
                MaximumLocalsPerFunction = 128,
                RequireOwnershipEvidence = true,
                CancellationToken = cancellationToken,
            });
        Require(pipeline.IsSuccessful,
            "source-to-MIR pipeline failed: " + string.Join("; ", pipeline.Diagnostics));
        SafeCoreHirResult hir = pipeline.Hir ??
            throw new InvalidOperationException("source-to-MIR pipeline did not publish HIR evidence.");
        SafeCoreMirProgram program = pipeline.Mir?.Program ??
            throw new InvalidOperationException("source-to-MIR pipeline did not publish MIR evidence.");
        SafeCoreMirOwnershipResult ownership = pipeline.Ownership ??
            throw new InvalidOperationException("source-to-MIR pipeline did not publish ownership evidence.");
        Require(hir.IsSuccessful && string.Equals(hir.SourcePath, sourcePath, StringComparison.Ordinal),
            "source-to-MIR pipeline lost its HIR source path.");
        Require(program.Functions.Count == 1 && ownership.Program?.Functions.Count == 1,
            "source-to-MIR pipeline produced an unexpected function count.");

        SafeCoreMirFunction function = program.Functions.Single();
        SafeCoreOwnershipFunction ownershipFunction = ownership.Program!.Functions.Single();
        RequireSourceEvidence(function.Source, hir, sourceText.Length, "MIR function");
        RequireSourceEvidence(ownershipFunction.Source, hir, sourceText.Length, "ownership function");
        Require(function.Source == ownershipFunction.Source,
            "MIR and ownership function source evidence differ.");
        SafeCoreMirSource returnSource = function.Blocks
            .Select(static block => block.Terminator)
            .Select(static terminator => terminator.Source)
            .Last();
        RequireSourceEvidence(returnSource, hir, sourceText.Length, "MIR terminator");
        Require(ownership.IsSuccessful && ownership.Ownership is { IsSuccessful: true } &&
            ownership.Ownership.Paths.Any(path => path.Outcome == SafeCoreOwnershipOutcome.Returned),
            "source-to-MIR ownership evidence did not produce a returned path.");

        string snapshot = pipeline.MirSnapshot ?? string.Empty;
        Require(snapshot.StartsWith("safe-core-mir-v1\n", StringComparison.Ordinal),
            "typed MIR snapshot has an unexpected version header.");
        Require(snapshot.Contains("p1_gate", StringComparison.Ordinal) &&
            snapshot.Contains("i32", StringComparison.Ordinal),
            "typed MIR snapshot lost the lowered source function or type.");
        return new(
            true,
            "pipeline=successful; hirNodes=" + hir.Nodes.Count +
            "; functions=" + program.Functions.Count +
            "; ownershipPaths=" + ownership.Ownership!.Paths.Length +
            "; sourcePath=" + sourcePath +
            "; snapshotSha256=" + HashText(snapshot));
    }

    private static void RequireSourceEvidence(
        SafeCoreMirSource source,
        SafeCoreHirResult hir,
        int sourceLength,
        string label)
    {
        Require(string.Equals(source.SourcePath, hir.SourcePath, StringComparison.Ordinal),
            label + " source path does not match HIR evidence.");
        Require(source.SourceLength == sourceLength && source.Span.Start >= 0 &&
            source.Span.End <= sourceLength && source.HirNodeId >= 0,
            label + " source span is outside the bounded source document.");
        Require(hir.GetNode(source.HirNodeId).Span == source.Span,
            label + " source span does not resolve to the referenced HIR node.");
    }

    private static ProbeResult ProbeOwnershipBridge(CancellationToken cancellationToken)
    {
        SafeCoreMirSource source = Source("ownership-bridge", 0);
        SafeCoreType integer = SafeCoreType.Primitive(
            SafeCoreSemanticTypeKind.I32,
            cancellationToken);
        SafeCoreMirProgram program = new([
            new SafeCoreMirFunction(
                0,
                "crate::ownership_bridge",
                integer,
                [],
                [new SafeCoreMirBlock(
                    0,
                    [],
                    SafeCoreMirTerminator.Return(
                        SafeCoreMirOperand.Constant(integer, "11", source),
                        source),
                    source)],
                0,
                source,
                cancellationToken),
        ], cancellationToken);
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(
            program,
            new SafeCoreMirOwnershipOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                CancellationToken = cancellationToken,
                MaximumOperations = 10_000,
                MaximumPaths = 16,
            });
        Require(result.IsSuccessful,
            "typed-MIR ownership bridge failed: " + string.Join("; ", result.Diagnostics));
        SafeCoreOwnershipPath path = result.Ownership!.Paths.Single();
        Require(path.Outcome == SafeCoreOwnershipOutcome.Returned,
            "typed-MIR ownership bridge did not produce a returned path.");
        return new(
            true,
            "adapter=" + SafeCoreMirOwnershipAdapter.Profile +
            "; outcome=" + path.Outcome +
            "; paths=" + result.Ownership.Paths.Length);
    }

    private static ProbeResult ProbeDropOrder(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SafeCoreMirSource source = Source("drop-order", 0);
        SafeCoreType adt = SafeCoreType.Adt("GateResource", cancellationToken);
        SafeCoreOwnershipFunction function = new(
            "crate::drop_order",
            [
                new SafeCoreOwnershipLocal(0, "first", adt, SafeCoreOwnershipKind.Move,
                    HasDrop: true, ScopeId: 0, IsReference: false, InitiallyInitialized: true, Source: source),
                new SafeCoreOwnershipLocal(1, "second", adt, SafeCoreOwnershipKind.Move,
                    HasDrop: true, ScopeId: 0, IsReference: false, InitiallyInitialized: true, Source: source),
            ],
            [new SafeCoreOwnershipScope(0, -1, source)],
            [new SafeCoreOwnershipBlock(
                0,
                0,
                [],
                SafeCoreOwnershipTerminator.ReturnUnit(source),
                source)],
            0,
            SafeCorePanicStrategy.Unwind,
            source);
        SafeCoreOwnershipAnalysisResult result = SafeCoreOwnershipAnalysis.Analyze(
            new([function]),
            new()
            {
                Timeout = TimeSpan.FromSeconds(2),
                CancellationToken = cancellationToken,
                MaximumOperations = 10_000,
                MaximumPaths = 16,
            });
        Require(result.IsSuccessful,
            "ownership Drop analysis failed: " + string.Join("; ", result.Diagnostics));
        string order = string.Join(',', result.Paths.Single().DropOrder);
        Require(order == "second,first",
            "ownership Drop order was '" + order + "', expected 'second,first'.");
        return new(true, "outcome=Returned; dropOrder=" + order);
    }

    private static ProbeResult ProbePanicUnwind(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var order = new List<int>(capacity: 2);
        using var scope = new DropScope();
        scope.Track(new RecordingDisposable(() => order.Add(1)));
        scope.Track(new RecordingDisposable(() => order.Add(2)));
        RustPanicReport report = RustPanicBoundary.Run(
            () => RustPanicBoundary.Panic("p1-gate"),
            scope,
            RustPanicStrategy.Unwind);
        Require(report.Outcome == RustPanicOutcome.Unwound,
            "panic boundary did not report unwind.");
        Require(report.CleanupAttempted && report.CleanupCompleted && scope.IsDisposed,
            "panic unwind did not complete DropScope cleanup.");
        Require(string.Join(',', order) == "2,1",
            "panic unwind cleanup order was not deterministic.");
        return new(true, "outcome=Unwound; cleanupCompleted=true; dropOrder=2,1");
    }

    private static ProbeResult ProbeMetadataConsumer(
        string repositoryRoot,
        string caseDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string sourcePath = Path.Combine(caseDirectory, "producer.rs");
        string outputPath = Path.Combine(caseDirectory, "producer.dll");
        string consumerSourcePath = Path.Combine(caseDirectory, "consumer.rs");
        string consumerOutputPath = Path.Combine(caseDirectory, "consumer.dll");
        const string source =
            "pub fn helper(x: i32) -> i32 { x + 1 } pub fn negate(value: bool) -> bool { !value } fn main() { println!(\"{}\", helper(4)); }";
        const string consumerSource =
            "use P1ExitGateProducer::helper; use P1ExitGateProducer::negate; fn main() { println!(\"{}\", helper(4)); println!(\"{}\", negate(true)); }";
        File.WriteAllText(sourcePath, source, new UTF8Encoding(false));
        CompilationResult compilation = CompilerDriver.Compile(
            source,
            sourcePath,
            outputPath,
            "P1ExitGateProducer",
            CompilationProfile.SafeCorePrimitives,
            cancellationToken);
        Require(compilation.Success,
            "metadata producer compilation failed: " +
            string.Join("; ", compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(
            outputPath,
            "safe-core-primitives-v1",
            ["Main"]);
        Require(imported.IsSuccessful,
            "metadata consumer rejected the producer: " + string.Join("; ", imported.Diagnostics));
        RustSharpMetadataDocument document = imported.Document ??
            throw new InvalidOperationException("metadata consumer did not return a document.");
        Guid moduleVersionId = imported.ModuleVersionId ??
            throw new InvalidOperationException("metadata consumer did not return an MVID.");
        Require(document.Functions.Length >= 3,
            "metadata consumer returned an incomplete export set.");
        RustSharpMetadataFunction helperExport = document.Functions
            .FirstOrDefault(function => string.Equals(function.SourceQualifiedName, "crate::helper", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("metadata producer did not publish the i32 helper export.");
        RustSharpMetadataFunction negateExport = document.Functions
            .FirstOrDefault(function => string.Equals(function.SourceQualifiedName, "crate::negate", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("metadata producer did not publish the bool negate export.");
        Require(helperExport.IsPublic && negateExport.IsPublic,
            "metadata producer scalar exports must be public.");
        Require(string.Equals(helperExport.Signature, "I32->I32", StringComparison.Ordinal) &&
            string.Equals(negateExport.Signature, "Bool->Bool", StringComparison.Ordinal),
            "metadata producer scalar signatures were not preserved.");

        File.WriteAllText(consumerSourcePath, consumerSource, new UTF8Encoding(false));
        CompilationResult consumerCompilation = CompilerDriver.CompileWithMetadataReferences(
            consumerSource,
            consumerSourcePath,
            consumerOutputPath,
            "P1ExitGateConsumer",
            CompilationProfile.SafeCorePrimitives,
            [outputPath],
            ["crate::helper", "crate::negate"],
            cancellationToken);
        Require(consumerCompilation.Success,
            "metadata consumer compilation failed: " +
            string.Join("; ", consumerCompilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Require(consumerCompilation.Output is not null &&
            File.Exists(consumerCompilation.Output.AssemblyPath),
            "metadata consumer compilation did not produce an assembly.");
        RustSharpMetadataImportResult consumerImported = RustSharpMetadataConsumer.ReadAssembly(
            consumerOutputPath,
            "safe-core-primitives-v1",
            ["Main"]);
        Require(consumerImported.IsSuccessful,
            "compiled consumer metadata could not be read: " +
            string.Join("; ", consumerImported.Diagnostics));
        RustSharpMetadataDocument consumerDocument = consumerImported.Document ??
            throw new InvalidOperationException("compiled consumer metadata did not return a document.");
        Guid consumerModuleVersionId = consumerImported.ModuleVersionId ??
            throw new InvalidOperationException("compiled consumer metadata did not return an MVID.");
        string assemblyHash = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(outputPath)));
        string consumerAssemblyHash = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(consumerOutputPath)));
        return new(
            true,
            "profile=" + document.Profile +
            "; functions=" + document.Functions.Length +
            "; mvid=" + moduleVersionId.ToString("D", CultureInfo.InvariantCulture) +
            "; assemblySha256=" + assemblyHash +
            "; consumerCompiled=true" +
            "; consumerFunctions=" + consumerDocument.Functions.Length +
            "; consumerMvid=" + consumerModuleVersionId.ToString("D", CultureInfo.InvariantCulture) +
            "; consumerAssemblySha256=" + consumerAssemblyHash +
            "; scalarSignatures=" + helperExport.Signature + "," + negateExport.Signature +
            "; requiredExports=crate::helper,crate::negate" +
            "; repositoryRootProvided=" + (!string.IsNullOrWhiteSpace(repositoryRoot)));
    }

    private static SafeCoreMirSource Source(string name, int offset) =>
        new("p1-exit-gate/" + name + ".rs", new TextSpan(offset, 1), 0, 1);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<ManifestValidation> ReadManifestAsync(
        string manifestPath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        string sha256 = string.Empty;
        long byteLength = 0;
        try
        {
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("P1 exit-gate manifest was not found.", manifestPath);
            FileInfo info = new(manifestPath);
            byteLength = info.Length;
            if (byteLength <= 0 || byteLength > MaximumManifestBytes)
                throw new ArgumentException(
                    "P1 exit-gate manifest size must be 1.." + MaximumManifestBytes + " bytes.");
            byte[] bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            string json = Encoding.UTF8.GetString(bytes);
            GateManifest manifest = ParseManifest(json);
            return new(true, manifest, null, sha256, byteLength, relativePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            JsonException or NotSupportedException)
        {
            return new(false, null, TrimDiagnostic(exception.Message), sha256, byteLength, relativePath);
        }
    }

    private static GateManifest ParseManifest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("P1 exit-gate manifest root must be an object.");
        string profile = RequiredString(root, "profile");
        int version = RequiredInt(root, "version");
        string scope = RequiredString(root, "scope");
        int denominator = RequiredInt(root, "denominator");
        GateLimits limits = ParseLimits(root);
        if (!root.TryGetProperty("cases", out JsonElement casesElement) ||
            casesElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("P1 exit-gate manifest must contain a cases array.");

        var cases = new List<GateCase>(Math.Min(Math.Max(denominator, 0), MaximumCases));
        int index = 0;
        foreach (JsonElement item in casesElement.EnumerateArray())
        {
            if (++index > MaximumCases)
                throw new ArgumentException("P1 exit-gate manifest exceeds its case bound.");
            if (item.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("P1 exit-gate case " + index + " must be an object.");
            cases.Add(new(RequiredString(item, "id"), RequiredString(item, "probe")));
        }

        var manifest = new GateManifest(profile, version, scope, denominator, limits, cases);
        ValidateManifest(manifest);
        return manifest;
    }

    private static GateLimits ParseLimits(JsonElement root)
    {
        if (!root.TryGetProperty("limits", out JsonElement limits) ||
            limits.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("P1 exit-gate manifest must contain a limits object.");
        return new(
            RequiredInt(limits, "maximumCases"),
            RequiredInt(limits, "caseTimeoutMilliseconds"),
            RequiredInt(limits, "deadlineSeconds"));
    }

    private static string RequiredString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
            throw new ArgumentException("P1 exit-gate manifest property '" + property + "' must be a string.");
        string? result = value.GetString();
        if (string.IsNullOrWhiteSpace(result))
            throw new ArgumentException("P1 exit-gate manifest property '" + property + "' cannot be empty.");
        return result;
    }

    private static int RequiredInt(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw new ArgumentException("P1 exit-gate manifest property '" + property + "' must be an integer.");
        return result;
    }

    private static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumJsonDepth,
        });
        var objects = new Stack<HashSet<string>>();
        int tokenCount = 0;
        while (reader.Read())
        {
            if (++tokenCount > MaximumJsonTokens)
                throw new JsonException("P1 exit-gate manifest exceeds its JSON token limit.");
            if (reader.TokenType == JsonTokenType.StartObject)
                objects.Push(new HashSet<string>(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (objects.Count == 0 || !objects.Peek().Add(reader.GetString() ?? string.Empty))
                    throw new JsonException("P1 exit-gate manifest contains a duplicate property.");
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (objects.Count == 0) throw new JsonException("P1 exit-gate JSON structure is invalid.");
                objects.Pop();
            }
        }

        if (objects.Count != 0)
            throw new JsonException("P1 exit-gate JSON structure is incomplete.");
    }

    private static async Task WriteReportAsync(string path, GateReport report)
    {
        string temporaryPath = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string? TryDeleteDirectory(string path)
    {
        var clock = Stopwatch.StartNew();
        Exception? lastException = null;
        for (int attempt = 0;
             attempt < MaximumCleanupAttempts && clock.Elapsed < CleanupTimeout;
             attempt++)
        {
            if (!Directory.Exists(path)) return null;
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException exception) { lastException = exception; }
            catch (UnauthorizedAccessException exception) { lastException = exception; }
            if (!Directory.Exists(path)) return null;
            Thread.Sleep(50);
        }

        return "P1 exit-gate run directory cleanup failed after " +
            MaximumCleanupAttempts + " attempts or " + CleanupTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) +
            " seconds: " + (lastException?.Message ?? "directory still exists");
    }

    private static string TrimDiagnostic(string value) =>
        value.Length <= 512 ? value : value[..512] + "...";

    private sealed class RecordingDisposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

}
