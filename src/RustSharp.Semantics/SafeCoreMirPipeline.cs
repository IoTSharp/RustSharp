using System.Collections.Immutable;
using System.Diagnostics;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>
/// Shared limits for the source-to-MIR evidence pipeline.  The individual
/// passes still enforce their own hard ceilings; these values provide one
/// caller-visible budget for the complete P1 chain.
/// </summary>
public sealed record SafeCoreMirPipelineOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumOperations { get; init; } = 1_000_000;
    /// <summary>Maximum deterministic MIR snapshot characters published by the pipeline.</summary>
    public int MaximumSnapshotCharacters { get; init; } = 4_000_000;
    public int MaximumNestingDepth { get; init; } = 128;
    public int MaximumFunctions { get; init; } = 1_024;
    public int MaximumBlocksPerFunction { get; init; } = 16_384;
    public int MaximumLocalsPerFunction { get; init; } = 65_536;
    public bool RequireOwnershipEvidence { get; init; }
    /// <summary>
    /// Requires the P1-08 cleanup projection to succeed after ownership
    /// evidence has been produced.  The projection is optional for callers
    /// that only need typed MIR, but executable evidence-producing profiles
    /// can turn it into a hard gate.
    /// </summary>
    public bool RequireCleanupEvidence { get; init; }
    /// <summary>Enables the v2 structural-Copy repeated-array lowering contract.</summary>
    public bool EnableRepeatedArrays { get; init; }
    /// <summary>Enables the versioned P1 source extensions (patterns, match and closures).</summary>
    public bool EnableP1Extensions { get; init; }
    public int MaximumPatternAlternatives { get; init; } = 256;
    public bool InferNonLexicalLifetimes { get; init; } = true;
    /// <summary>
    /// Independently validated metadata-backed crates available to name
    /// resolution.  Source-linked package callers can supply the same crate
    /// graph used by the compiler; the default keeps the in-memory boundary
    /// closed.
    /// </summary>
    public ImmutableArray<SafeCoreCrate> Crates { get; init; } = [];
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// The complete bounded source/HIR/type/MIR/ownership evidence chain.  A
/// failed stage never publishes a later-stage program, while an ownership
/// limitation is retained as a diagnostic result for callers that use the MIR
/// profile without claiming ownership completeness.
/// </summary>
public sealed record SafeCoreMirPipelineResult(
    SafeCoreHirResult? Hir,
    SafeCoreTypeAnalysisProgram? Types,
    SafeCoreMirLoweringResult? Mir,
    SafeCoreMirOwnershipResult? Ownership,
    string? MirSnapshot,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool IsTruncated,
    bool OwnershipRequired)
{
    /// <summary>Deterministic P1-08 cleanup projection, when ownership was available.</summary>
    public SafeCoreMirCleanupResult? Cleanup { get; init; }

    public bool CleanupRequired { get; init; }

    public bool IsSuccessful => !IsTruncated && Diagnostics.Count == 0 &&
        Mir is { IsSuccessful: true } &&
        (!OwnershipRequired || Ownership is { IsSuccessful: true }) &&
        (!CleanupRequired || Cleanup is { IsSuccessful: true });
}

/// <summary>
/// Runs the same bounded passes used by the compiler and conformance tools.
/// Keeping this orchestration in Semantics prevents individual consumers from
/// silently skipping source mapping, MIR validation, or ownership correlation.
/// </summary>
public static class SafeCoreMirPipeline
{
    public const string Profile = "safe-core-mir-p1-v1";
    public const string ProfileV2 = "safe-core-mir-p1-v2";

    public static SafeCoreMirPipelineResult Analyze(
        string source,
        string sourcePath = "<memory>",
        SafeCoreMirPipelineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        options ??= new();
        ValidateOptions(options);
        options.CancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        try
        {
            SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(
                source,
                sourcePath,
                new SafeCoreSyntaxOptions
                {
                    Timeout = Remaining(options.Timeout, clock),
                    MaximumOperations = options.MaximumOperations,
                    MaximumNestingDepth = options.MaximumNestingDepth,
                },
                options.CancellationToken);
            return AnalyzeCore(syntax, options, clock);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException exception)
        {
            return TimeoutFailure(exception, sourcePath, source.Length, options);
        }
    }

    public static SafeCoreMirPipelineResult Analyze(
        SafeCoreSyntaxResult syntax,
        SafeCoreMirPipelineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        options ??= new();
        ValidateOptions(options);
        options.CancellationToken.ThrowIfCancellationRequested();
        return AnalyzeCore(syntax, options, Stopwatch.StartNew());
    }

    private static SafeCoreMirPipelineResult AnalyzeCore(
        SafeCoreSyntaxResult syntax,
        SafeCoreMirPipelineOptions options,
        Stopwatch clock)
    {
        ArgumentNullException.ThrowIfNull(syntax);

        if (!syntax.IsSuccessful || syntax.Root is null)
            return Failure(null, null, null, null, syntax.Diagnostics, syntax.IsTruncated, options);

        try
        {
            SafeCoreHirResult hir = SafeCoreHirLowering.Lower(syntax, new SafeCoreHirLoweringOptions
            {
                Timeout = Remaining(options.Timeout, clock),
                CancellationToken = options.CancellationToken,
                MaximumOperations = options.MaximumOperations,
                MaximumNestingDepth = options.MaximumNestingDepth,
                MaximumNodes = Math.Min(500_000, Math.Max(1, options.MaximumOperations)),
                NameResolution = new SafeCoreNameResolutionOptions
                {
                    Timeout = Remaining(options.Timeout, clock),
                    CancellationToken = options.CancellationToken,
                    MaximumOperations = options.MaximumOperations,
                    MaximumNestingDepth = options.MaximumNestingDepth,
                    EnableTypeSystemExtensions = true,
                    EnableGenericExtensions = options.EnableP1Extensions,
                    EnableDropImplementations = options.EnableP1Extensions,
                    Crates = options.Crates.IsDefault ? [] : options.Crates,
                },
            });
            if (!hir.IsSuccessful)
                return Failure(hir, null, null, null, hir.Diagnostics, hir.IsTruncated, options);

            SafeCoreTypeAnalysisResult typed = SafeCoreTypeAnalysis.Check(hir, new SafeCoreTypeAnalysisOptions
            {
                Timeout = Remaining(options.Timeout, clock),
                MaximumOperations = options.MaximumOperations,
                MaximumNestingDepth = options.MaximumNestingDepth,
                EnableUninitializedBindings = options.EnableP1Extensions,
            }, options.CancellationToken);
            if (!typed.IsSuccessful)
                return Failure(hir, null, null, null, typed.Diagnostics, false, options);

            SafeCoreMirLoweringResult mir = SafeCoreMirLowering.Lower(typed.Program!, new SafeCoreMirLoweringOptions
            {
                Timeout = Remaining(options.Timeout, clock),
                MaximumOperations = options.MaximumOperations,
                MaximumNestingDepth = options.MaximumNestingDepth,
                MaximumFunctions = options.MaximumFunctions,
                MaximumBlocksPerFunction = options.MaximumBlocksPerFunction,
                MaximumLocalsPerFunction = options.MaximumLocalsPerFunction,
                EnableRepeatedArrays = options.EnableRepeatedArrays,
                EnableP1Extensions = options.EnableP1Extensions,
                MaximumPatternAlternatives = options.MaximumPatternAlternatives,
            }, options.CancellationToken);
            if (!mir.IsSuccessful)
                return Failure(hir, typed.Program, mir, null, mir.Diagnostics, mir.IsTruncated, options);

            string snapshot;
            try
            {
                snapshot = SafeCoreMirFormatting.Format(mir.Program!, new SafeCoreMirFormattingOptions
                {
                    Timeout = Remaining(options.Timeout, clock),
                    MaximumOperations = options.MaximumOperations,
                    MaximumCharacters = options.MaximumSnapshotCharacters,
                    CancellationToken = options.CancellationToken,
                });
            }
            catch (SafeCoreMirLimitException exception)
            {
                // Formatting is part of the published pipeline evidence, so a
                // formatter limit must remain a bounded pipeline result rather
                // than escaping after MIR lowering succeeded.
                return Failure(hir, typed.Program, mir, null,
                    [new Diagnostic(SafeCoreMirDiagnosticCodes.LimitReached,
                        exception.Message, syntax.Root!.Span) { SourcePath = syntax.SourcePath }],
                    true, options);
            }
            SafeCoreMirOwnershipResult ownership = SafeCoreMirOwnershipAdapter.Analyze(mir.Program!,
                new SafeCoreMirOwnershipOptions
                {
                    Timeout = Remaining(options.Timeout, clock),
                    MaximumOperations = options.MaximumOperations,
                    MaximumFunctions = options.MaximumFunctions,
                    MaximumLocalsPerFunction = Math.Min(4_096, options.MaximumLocalsPerFunction),
                    MaximumBlocksPerFunction = options.MaximumBlocksPerFunction,
                    InferNonLexicalLifetimes = options.InferNonLexicalLifetimes,
                    InferNll = options.InferNonLexicalLifetimes,
                    CancellationToken = options.CancellationToken,
                });

            if (options.RequireOwnershipEvidence && !ownership.IsSuccessful)
                return Failure(hir, typed.Program, mir, ownership, ownership.Diagnostics,
                    ownership.IsTruncated, options, snapshot);

            SafeCoreMirCleanupResult? cleanup = null;
            if (ownership.IsSuccessful)
            {
                cleanup = SafeCoreMirCleanupLowering.Lower(mir.Program!, ownership,
                    new SafeCoreMirCleanupLoweringOptions
                    {
                        Timeout = Remaining(options.Timeout, clock),
                        MaximumOperations = options.MaximumOperations,
                        MaximumFunctions = options.MaximumFunctions,
                        MaximumActionsPerPath = Math.Min(65_536, options.MaximumOperations),
                        CancellationToken = options.CancellationToken,
                    });
                if (options.RequireCleanupEvidence && !cleanup.IsSuccessful)
                    return Failure(hir, typed.Program, mir, ownership, cleanup.Diagnostics,
                        cleanup.IsTruncated, options, snapshot, cleanup);
            }

            return new(hir, typed.Program, mir, ownership, snapshot, [], false, options.RequireOwnershipEvidence)
            {
                Cleanup = cleanup,
                CleanupRequired = options.RequireCleanupEvidence,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException exception)
        {
            return Failure(null, null, null, null,
                [new Diagnostic(SafeCoreMirOwnershipDiagnosticCodes.LimitReached,
                    exception.Message, syntax.Root.Span) { SourcePath = syntax.SourcePath }], true, options);
        }
    }

    private static SafeCoreMirPipelineResult Failure(
        SafeCoreHirResult? hir,
        SafeCoreTypeAnalysisProgram? types,
        SafeCoreMirLoweringResult? mir,
        SafeCoreMirOwnershipResult? ownership,
        IReadOnlyList<Diagnostic> diagnostics,
        bool truncated,
        SafeCoreMirPipelineOptions options,
        string? snapshot = null,
        SafeCoreMirCleanupResult? cleanup = null) =>
        new(hir, types, mir, ownership, snapshot, diagnostics, truncated, options.RequireOwnershipEvidence)
        {
            Cleanup = cleanup,
            CleanupRequired = options.RequireCleanupEvidence,
        };

    private static SafeCoreMirPipelineResult TimeoutFailure(
        TimeoutException exception,
        string sourcePath,
        int sourceLength,
        SafeCoreMirPipelineOptions options) =>
        Failure(null, null, null, null,
            [new Diagnostic(SafeCoreMirOwnershipDiagnosticCodes.LimitReached,
                exception.Message, new TextSpan(0, Math.Min(sourceLength, 1))) { SourcePath = sourcePath }],
            true, options);

    private static TimeSpan Remaining(TimeSpan budget, Stopwatch clock)
    {
        TimeSpan remaining = budget - clock.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("Safe-core MIR pipeline exceeded its time budget.");
        return remaining;
    }

    private static void ValidateOptions(SafeCoreMirPipelineOptions options)
    {
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1) ||
            options.MaximumOperations is < 1 or > 4_000_000 ||
            options.MaximumSnapshotCharacters is < 1 or > 4_000_000 ||
            options.MaximumNestingDepth is < 1 or > 128 ||
            options.MaximumFunctions is < 1 or > 4_096 ||
            options.MaximumBlocksPerFunction is < 1 or > 65_536 ||
            options.MaximumLocalsPerFunction is < 1 or > 262_144 ||
            options.MaximumPatternAlternatives is < 1 or > 4_096)
            throw new ArgumentOutOfRangeException(nameof(options));
    }
}
