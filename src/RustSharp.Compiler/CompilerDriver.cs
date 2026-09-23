using System.Diagnostics;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using RustSharp.CodeGen.IL;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

public sealed class CompilerDriver
{
    private const int MaximumSourceBytes = 16 * 1024 * 1024;
    private const int SourceReadBufferBytes = 64 * 1024;
    // The sidecar lock is intentionally bounded. A compiler invocation that
    // cannot acquire its output lease within this window fails with a useful
    // diagnostic instead of waiting forever behind a crashed or hung writer.
    private const int OutputLockAttempts = 120;
    private const int OutputLockRetryMilliseconds = 50;
    private const long OutputLockRegionLength = 1;
    private const int MaximumTransactionDiagnosticCharacters = 512;
    // A stream is allowed to return one byte per read. Keep the upper bound
    // finite even for that worst case while normal files finish in a few reads.
    private const int MaximumSourceReadChunks = MaximumSourceBytes + 1;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly object OutputLockRegistryGate = new();
    private static readonly Dictionary<string, OutputLockEntry> OutputLockEntries = new(StringComparer.Ordinal);

    public static CompilationResult CheckFile(string sourcePath,
        CompilationProfile profile = CompilationProfile.VerticalSlice, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var fullSourcePath = Path.GetFullPath(sourcePath);
        if ((profile == CompilationProfile.SafeCoreGenerics || profile is CompilationProfile.SafeCoreMir or CompilationProfile.SafeCoreMirV2) && IsCargoManifest(fullSourcePath))
        {
            GenericPackageWorkspaceResult packages = GenericPackageWorkspace.Load(fullSourcePath, cancellationToken);
            if (!packages.IsSuccessful) return CompilationResult.Failed(packages.Diagnostics);
            CompilationResult checkedPackages = profile is CompilationProfile.SafeCoreMir or CompilationProfile.SafeCoreMirV2
                ? CheckSafeCoreMir(packages.SourceText, packages.RootSourcePath, cancellationToken, packages.Crates,
                    profile == CompilationProfile.SafeCoreMirV2)
                : CheckSafeCoreGenerics(packages.SourceText, packages.RootSourcePath, cancellationToken, packages.Crates);
            return checkedPackages with { Diagnostics = MapDiagnostics(checkedPackages.Diagnostics, packages.SourceMap!) };
        }
        var cargo = TryResolveCargo(fullSourcePath, cancellationToken, out var cargoDiagnostics);
        if (cargoDiagnostics is not null) return CompilationResult.Failed(cargoDiagnostics);
        fullSourcePath = cargo ?? fullSourcePath;
        if (profile is CompilationProfile.SafeCorePrimitives or CompilationProfile.SafeCoreTypes or
            CompilationProfile.SafeCoreGenerics or CompilationProfile.SafeCoreMir or CompilationProfile.SafeCoreMirV2)
        {
            SafeCoreWorkspaceResult workspace = SafeCoreWorkspace.Load(fullSourcePath, cancellationToken: cancellationToken);
            if (!workspace.IsSuccessful) return CompilationResult.Failed(workspace.Diagnostics);
            CompilationResult result = Check(workspace.SourceText, fullSourcePath, profile, cancellationToken);
            return result with { Diagnostics = MapDiagnostics(result.Diagnostics, workspace.SourceMap!) };
        }

        var readResult = ReadSource(fullSourcePath);
        if (readResult.Diagnostic is not null)
        {
            return CompilationResult.Failed([readResult.Diagnostic]);
        }

        return Check(readResult.Document!.Source, fullSourcePath, profile, cancellationToken);
    }

    public static CompilationResult Check(string source, string sourcePath = "<memory>",
        CompilationProfile profile = CompilationProfile.VerticalSlice, CancellationToken cancellationToken = default)
        => CheckCore(source, sourcePath, profile, default, cancellationToken);

    private static CompilationResult CheckCore(
        string source,
        string sourcePath,
        CompilationProfile profile,
        ImmutableArray<SafeCoreCrate> crates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        Diagnostic? sourceDiagnostic = ValidateSourceText(source, sourcePath);
        if (sourceDiagnostic is not null)
        {
            return CompilationResult.Failed([sourceDiagnostic]);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (profile == CompilationProfile.SafeCoreTypes)
            return CheckSafeCoreTypes(source, sourcePath, cancellationToken);
        if (profile == CompilationProfile.SafeCoreGenerics)
            return CheckSafeCoreGenerics(source, sourcePath, cancellationToken, crates);
        if (profile is CompilationProfile.SafeCoreMir or CompilationProfile.SafeCoreMirV2)
            return CheckSafeCoreMir(source, sourcePath, cancellationToken, crates,
                profile == CompilationProfile.SafeCoreMirV2);
        if (profile != CompilationProfile.VerticalSlice)
        {
            SafeCoreClrResult result = AnalyzeSafeCore(source, sourcePath, profile, cancellationToken, crates);
            return result.IsSuccessful ? new(true, [], null) : CompilationResult.Failed(result.Diagnostics);
        }

        var syntaxTree = SyntaxTree.Parse(source, sourcePath);
        return syntaxTree.Diagnostics.Count == 0
            ? new CompilationResult(true, [], null)
            : CompilationResult.Failed(syntaxTree.Diagnostics);
    }

    /// <summary>
    /// Checks a consumer source after importing one or more independently
    /// emitted Rust# assemblies.  Import validation is deliberately explicit;
    /// the source checker never loads producer code or uses reflection.
    /// </summary>
    public static CompilationResult CheckWithMetadataReferences(
        string source,
        string sourcePath,
        CompilationProfile profile,
        IEnumerable<string> metadataReferences,
        IEnumerable<string>? requiredFunctions = null,
        CancellationToken cancellationToken = default)
    {
        MetadataCrateLoadResult references = LoadMetadataCrates(
            metadataReferences, profile, requiredFunctions, cancellationToken);
        if (references.Diagnostics.Count != 0)
            return CompilationResult.Failed(references.Diagnostics);
        return CheckCore(source, sourcePath, profile, references.Crates, cancellationToken);
    }

    public static CompilationResult CompileFile(
        string sourcePath,
        string outputPath,
        string? assemblyName = null,
        CompilationProfile profile = CompilationProfile.VerticalSlice,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        cancellationToken.ThrowIfCancellationRequested();
        if (profile == CompilationProfile.SafeCoreTypes)
            return RejectTypeProfileEmission(sourcePath);

        var fullSourcePath = Path.GetFullPath(sourcePath);
        if ((profile == CompilationProfile.SafeCoreGenerics || profile is CompilationProfile.SafeCoreMir or CompilationProfile.SafeCoreMirV2) && IsCargoManifest(fullSourcePath))
        {
            GenericPackageWorkspaceResult packages = GenericPackageWorkspace.Load(fullSourcePath, cancellationToken);
            if (!packages.IsSuccessful) return CompilationResult.Failed(packages.Diagnostics);
            return CompileCore(packages.SourceText, packages.RootSourcePath, Path.GetFullPath(outputPath), assemblyName,
                packages.SourceMap!.Documents[0].Bytes, profile, cancellationToken, packages.SourceMap, packages.Crates);
        }
        var cargo = TryResolveCargo(fullSourcePath, cancellationToken, out var cargoDiagnostics);
        if (cargoDiagnostics is not null) return CompilationResult.Failed(cargoDiagnostics);
        fullSourcePath = cargo ?? fullSourcePath;
        if (profile is CompilationProfile.SafeCorePrimitives or CompilationProfile.SafeCoreGenerics or
            CompilationProfile.SafeCoreMir or CompilationProfile.SafeCoreMirV2)
        {
            SafeCoreWorkspaceResult workspace = SafeCoreWorkspace.Load(fullSourcePath, cancellationToken: cancellationToken);
            if (!workspace.IsSuccessful) return CompilationResult.Failed(workspace.Diagnostics);
            return CompileCore(workspace.SourceText, fullSourcePath, Path.GetFullPath(outputPath),
                assemblyName, workspace.SourceMap!.Documents[0].Bytes, profile, cancellationToken, workspace.SourceMap);
        }

        var readResult = ReadSource(fullSourcePath);
        if (readResult.Diagnostic is not null)
        {
            return CompilationResult.Failed([readResult.Diagnostic]);
        }

        return CompileCore(
            readResult.Document!.Source,
            fullSourcePath,
            Path.GetFullPath(outputPath),
            assemblyName,
            readResult.Document.Bytes,
            profile,
            cancellationToken);
    }

    public static CompilationResult Compile(
        string source,
        string sourcePath,
        string outputPath,
        string? assemblyName = null,
        CompilationProfile profile = CompilationProfile.VerticalSlice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        cancellationToken.ThrowIfCancellationRequested();
        if (profile == CompilationProfile.SafeCoreTypes)
            return RejectTypeProfileEmission(sourcePath);

        Diagnostic? sourceDiagnostic = ValidateSourceText(source, sourcePath);
        if (sourceDiagnostic is not null)
        {
            return CompilationResult.Failed([sourceDiagnostic]);
        }

        byte[] sourceBytes;
        try
        {
            sourceBytes = StrictUtf8.GetBytes(source);
        }
        catch (EncoderFallbackException exception)
        {
            return CompilationResult.Failed(
                [new Diagnostic(
                    "RSC0005",
                    $"Source text '{sourcePath}' is not valid UTF-8: {exception.Message}",
                    new TextSpan(0, 0))]);
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        return CompileCore(
            source,
            sourcePath,
            fullOutputPath,
            assemblyName,
            sourceBytes,
            profile,
            cancellationToken);
    }

    private static CompilationResult CompileCoreWithCrates(
        string source,
        string sourcePath,
        string outputPath,
        string? assemblyName,
        CompilationProfile profile,
        ImmutableArray<SafeCoreCrate> crates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (profile == CompilationProfile.SafeCoreTypes)
            return RejectTypeProfileEmission(sourcePath);

        Diagnostic? sourceDiagnostic = ValidateSourceText(source, sourcePath);
        if (sourceDiagnostic is not null)
            return CompilationResult.Failed([sourceDiagnostic]);

        byte[] sourceBytes;
        try
        {
            sourceBytes = StrictUtf8.GetBytes(source);
        }
        catch (EncoderFallbackException exception)
        {
            return CompilationResult.Failed([new Diagnostic(
                "RSC0005",
                $"Source text '{sourcePath}' is not valid UTF-8: {exception.Message}",
                new TextSpan(0, 0))]);
        }

        return CompileCore(source, sourcePath, Path.GetFullPath(outputPath), assemblyName,
            sourceBytes, profile, cancellationToken, crates: crates);
    }

    /// <summary>Compiles a consumer after validating independent Rust# metadata references.</summary>
    public static CompilationResult CompileWithMetadataReferences(
        string source,
        string sourcePath,
        string outputPath,
        string? assemblyName,
        CompilationProfile profile,
        IEnumerable<string> metadataReferences,
        IEnumerable<string>? requiredFunctions = null,
        CancellationToken cancellationToken = default)
    {
        MetadataCrateLoadResult references = LoadMetadataCrates(
            metadataReferences, profile, requiredFunctions, cancellationToken);
        if (references.Diagnostics.Count != 0)
            return CompilationResult.Failed(references.Diagnostics);
        return CompileCoreWithCrates(source, sourcePath, outputPath, assemblyName, profile,
            references.Crates, cancellationToken);
    }

    private static CompilationResult CompileCore(
        string source,
        string sourcePath,
        string outputPath,
        string? assemblyName,
        ReadOnlyMemory<byte> sourceBytes,
        CompilationProfile profile,
        CancellationToken cancellationToken,
        SafeCoreSourceMap? sourceMap = null, ImmutableArray<SafeCoreCrate> crates = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SyntaxTree? syntaxTree = null;
        SafeCoreClrResult? safeCore = null;
        if (profile == CompilationProfile.VerticalSlice)
        {
            syntaxTree = SyntaxTree.Parse(source, sourcePath);
            if (syntaxTree.Diagnostics.Count != 0 || syntaxTree.Root is null)
                return CompilationResult.Failed(syntaxTree.Diagnostics);
        }
        else
        {
            safeCore = AnalyzeSafeCore(source, sourcePath, profile, cancellationToken, crates);
            if (!safeCore.IsSuccessful) return CompilationResult.Failed(MapDiagnostics(safeCore.Diagnostics, sourceMap));
        }

        var resolvedAssemblyName = assemblyName ?? Path.GetFileNameWithoutExtension(outputPath);
        if (!IsValidAssemblyName(resolvedAssemblyName))
        {
            return CompilationResult.Failed(
                [new Diagnostic(
                    "RSC0002",
                    $"'{resolvedAssemblyName}' is not a valid generated assembly name.",
                    new TextSpan(0, 0))]);
        }

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var pdbPath = Path.ChangeExtension(fullOutputPath, ".pdb");
        var runtimeConfigPath = Path.ChangeExtension(fullOutputPath, ".runtimeconfig.json");
        if (sourceMap is not null)
        {
            // Every loaded document is an input, including modules whose names resemble output files.
            foreach (SafeCoreSourceDocument document in sourceMap.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (PathsCollide(document.Path, fullOutputPath) || PathsCollide(document.Path, pdbPath) ||
                    PathsCollide(document.Path, runtimeConfigPath))
                    return CompilationResult.Failed([new Diagnostic("RSC0006",
                        "The source and compiler output paths must be distinct.", new TextSpan(0, 0))
                        { SourcePath = document.Path }]);
            }
        }

        if (PathsCollide(fullSourcePath, fullOutputPath) ||
            PathsCollide(fullSourcePath, pdbPath) ||
            PathsCollide(fullSourcePath, runtimeConfigPath) ||
            PathsCollide(fullOutputPath, pdbPath) ||
            PathsCollide(fullOutputPath, runtimeConfigPath) ||
            PathsCollide(pdbPath, runtimeConfigPath))
        {
            return CompilationResult.Failed(
                [new Diagnostic(
                    "RSC0006",
                    "The source and compiler output paths must be distinct.",
                    new TextSpan(0, 0))]);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RustSharpMetadataDocument? metadataDocument = safeCore is null
                ? null
                : RustSharpMetadataDocument.ForProgram(
                    profile switch
                    {
                        CompilationProfile.SafeCoreGenerics => "safe-core-generics-v1",
                        CompilationProfile.SafeCoreMir => SafeCoreMirPipeline.Profile,
                        CompilationProfile.SafeCoreMirV2 => SafeCoreMirPipeline.ProfileV2,
                        _ => "safe-core-primitives-v1",
                    },
                    sourceBytes.Span,
                    safeCore.Methods,
                    safeCore.GenericInstances,
                    safeCore.TraitImplementations,
                    safeCore.MirSnapshot,
                    safeCore.Ownership,
                    safeCore.CleanupSnapshot);
            GeneratedAssembly generated;
            try
            {
                generated = safeCore is not null
                    ? ClrLirAssemblyEmitter.EmitProgram(safeCore, resolvedAssemblyName, source,
                        fullSourcePath, Path.GetFileName(pdbPath), sourceBytes, sourceMap,
                        metadataDocument, cancellationToken)
                    : IlAssemblyEmitter.Emit(
                    syntaxTree!.Root!,
                    source,
                    fullSourcePath,
                    resolvedAssemblyName,
                    Path.GetFileName(pdbPath),
                    sourceBytes);
            }
            catch (Exception exception) when ((profile == CompilationProfile.SafeCoreGenerics || profile is CompilationProfile.SafeCoreMir or CompilationProfile.SafeCoreMirV2) &&
                exception is ArgumentException or InvalidOperationException or TimeoutException)
            {
                return CompilationResult.Failed([new Diagnostic(SafeCoreGenericDiagnosticCodes.LimitReached,
                    "Generic CLR emission exceeded its supported layout or validation limits: " + TrimDiagnostic(exception.Message),
                    new TextSpan(0, 0)) { SourcePath = sourcePath }]);
            }

            WriteArtifactsTransactionally(
                fullOutputPath,
                pdbPath,
                runtimeConfigPath,
                generated);

            return new CompilationResult(
                true,
                [],
                new CompilationOutput(fullOutputPath, pdbPath, runtimeConfigPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CompilationResult.Failed(
                [new Diagnostic(
                    "RSC0003",
                    $"Could not write compiler output: {exception.Message}",
                    new TextSpan(0, 0))]);
        }
        catch (TimeoutException)
        {
            return CompilationResult.Failed([new Diagnostic("RSC0008",
                "Compiler emission exceeded its time budget.", new TextSpan(0, 0))]);
        }
    }

    private static CompilationResult RejectTypeProfileEmission(string sourcePath)
    {
        return CompilationResult.Failed([new Diagnostic("RSC0009",
            $"The {SafeCoreTypeAnalysis.Profile} profile supports type checking only. Use 'rsc check'; " +
            "build, compile, run and publish require an executable profile.", new TextSpan(0, 0))
            { SourcePath = sourcePath }]);
    }

    private static CompilationResult CheckSafeCoreTypes(string source, string sourcePath,
        CancellationToken cancellationToken)
    {
        SafeCoreSyntaxResult syntax;
        try { syntax = SafeCoreSyntax.Parse(source, sourcePath, null, cancellationToken); }
        catch (TimeoutException)
        {
            return CompilationResult.Failed([new Diagnostic(SafeCoreSyntaxDiagnosticCodes.LimitReached,
                "Safe-core parsing exceeded its time budget.", new TextSpan(0, 0))]);
        }
        if (!syntax.IsSuccessful) return CompilationResult.Failed(syntax.Diagnostics);
        cancellationToken.ThrowIfCancellationRequested();
        SafeCoreHirResult hir = SafeCoreHirLowering.Lower(syntax, new SafeCoreHirLoweringOptions
        {
            CancellationToken = cancellationToken,
            NameResolution = new SafeCoreNameResolutionOptions
            {
                CancellationToken = cancellationToken,
                EnableTypeSystemExtensions = true,
            },
        });
        if (!hir.IsSuccessful) return CompilationResult.Failed(hir.Diagnostics);
        var result = SafeCoreTypeAnalysis.Check(hir, cancellationToken: cancellationToken);
        return result.IsSuccessful ? new(true, [], null) : CompilationResult.Failed(result.Diagnostics);
    }

    private static CompilationResult CheckSafeCoreGenerics(string source, string sourcePath,
        CancellationToken cancellationToken, ImmutableArray<SafeCoreCrate> crates = default)
    {
        SafeCoreSyntaxResult syntax;
        try { syntax = SafeCoreSyntax.Parse(source, sourcePath, null, cancellationToken); }
        catch (TimeoutException)
        {
            return CompilationResult.Failed([new Diagnostic(SafeCoreSyntaxDiagnosticCodes.LimitReached,
                "Safe-core parsing exceeded its time budget.", new TextSpan(0, 0))]);
        }
        if (!syntax.IsSuccessful) return CompilationResult.Failed(syntax.Diagnostics);
        var result = SafeCoreGenericAnalysis.Check(syntax, new() { Crates = crates.IsDefault ? [] : crates }, cancellationToken);
        return result.IsSuccessful ? new(true, [], null) : CompilationResult.Failed(result.Diagnostics);
    }

    private static CompilationResult CheckSafeCoreMir(string source, string sourcePath,
        CancellationToken cancellationToken, ImmutableArray<SafeCoreCrate> crates = default,
        bool enableRepeatedArrays = false)
    {
        SafeCoreMirPipelineResult result = SafeCoreMirPipeline.Analyze(source, sourcePath,
            new SafeCoreMirPipelineOptions
            {
                CancellationToken = cancellationToken,
                RequireOwnershipEvidence = true,
                RequireCleanupEvidence = true,
                EnableRepeatedArrays = enableRepeatedArrays,
                EnableP1Extensions = enableRepeatedArrays,
                Crates = crates.IsDefault ? [] : crates,
            });
        if (!result.IsSuccessful)
        {
            return CompilationResult.Failed(result.Diagnostics.Count == 0
                ? result.Ownership?.Diagnostics ?? [new Diagnostic("RSC0007", "Typed MIR analysis failed.", new TextSpan(0, 0))]
                : result.Diagnostics);
        }

        // The MIR validator deliberately checks the language IR contract, while
        // the CLR backend has a smaller, representation-specific capability
        // boundary.  Run that same backend during `check` so a source cannot be
        // accepted here and then rejected by `compile` for an LIR-only reason.
        SafeCoreClrResult backend = SafeCoreMirClrLowering.Lower(
            result.Mir!.Program!, cancellationToken);
        backend = AttachMirEvidence(backend, result, requireOwnershipMetadata: true);
        return backend.IsSuccessful
            ? new(true, [], null)
            : CompilationResult.Failed(backend.Diagnostics.Count == 0
                ? [new Diagnostic("RSM2102", "Typed MIR CLR lowering failed.", new TextSpan(0, 0))]
                : backend.Diagnostics);
    }

    private static SafeCoreClrResult AnalyzeSafeCore(string source, string sourcePath,
        CompilationProfile profile, CancellationToken cancellationToken, ImmutableArray<SafeCoreCrate> crates = default)
    {
        if (profile is not (CompilationProfile.SafeCorePrimitives or CompilationProfile.SafeCoreGenerics or CompilationProfile.SafeCoreMir or CompilationProfile.SafeCoreMirV2))
            return new([], [], [new("RSC0007", "Unknown compilation profile.", new TextSpan(0, 0))]);
        cancellationToken.ThrowIfCancellationRequested();
        SafeCoreSyntaxResult syntax;
        try { syntax = SafeCoreSyntax.Parse(source, sourcePath, null, cancellationToken); }
        catch (TimeoutException)
        {
            return new([], [], [new Diagnostic(SafeCoreSyntaxDiagnosticCodes.LimitReached,
                "Safe-core parsing exceeded its time budget.", new TextSpan(0, 0))]);
        }
        if (!syntax.IsSuccessful) return new([], [], syntax.Diagnostics);
        cancellationToken.ThrowIfCancellationRequested();
        if (profile == CompilationProfile.SafeCoreGenerics)
        {
            SafeCoreGenericAnalysisResult generics = SafeCoreGenericAnalysis.Check(syntax,
                new() { Crates = crates.IsDefault ? [] : crates }, cancellationToken);
            return generics.IsSuccessful ? SafeCoreGenericClrLowering.Lower(generics.Program!, cancellationToken)
                : new([], [], generics.Diagnostics);
        }
        if (profile is CompilationProfile.SafeCoreMir or CompilationProfile.SafeCoreMirV2)
        {
            SafeCoreMirPipelineResult evidence = SafeCoreMirPipeline.Analyze(syntax,
                new SafeCoreMirPipelineOptions
                {
                    CancellationToken = cancellationToken,
                    RequireOwnershipEvidence = true,
                    RequireCleanupEvidence = true,
                    EnableRepeatedArrays = profile == CompilationProfile.SafeCoreMirV2,
                    EnableP1Extensions = profile == CompilationProfile.SafeCoreMirV2,
                    Crates = crates.IsDefault ? [] : crates,
                });
            if (!evidence.IsSuccessful)
            {
                IReadOnlyList<Diagnostic> diagnostics = evidence.Diagnostics.Count != 0
                    ? evidence.Diagnostics
                    : evidence.Ownership?.Diagnostics ??
                        [new Diagnostic("RSC0007", "Typed MIR analysis failed.", syntax.Root!.Span)
                            { SourcePath = sourcePath }];
                return new([], [], diagnostics);
            }

            // Consume the already validated MIR directly.  Re-running the
            // primitive HIR checker here used to make aggregate MIR evidence
            // observational only and could let backend behavior drift from the
            // source-to-MIR contract.
            SafeCoreClrResult emitted = SafeCoreMirClrLowering.Lower(
                evidence.Mir!.Program!, cancellationToken);
            return AttachMirEvidence(emitted, evidence, requireOwnershipMetadata: true);
        }
        SafeCoreHirResult hir = SafeCoreHirLowering.Lower(syntax, new SafeCoreHirLoweringOptions
        {
            CancellationToken = cancellationToken,
            NameResolution = new SafeCoreNameResolutionOptions
            {
                CancellationToken = cancellationToken,
                EnableTypeSystemExtensions = false,
                Crates = crates.IsDefault ? [] : crates,
            },
        });
        SafeCoreTypeCheckResult types = SafeCoreTypeChecking.Check(hir, cancellationToken);
        if (!types.IsSuccessful) return new([], [], types.Diagnostics);
        SafeCoreClrResult lowered = SafeCoreClrLowering.Lower(types.Program!, cancellationToken);

        // Primitive programs remain source-compatible with the existing
        // profile.  When the richer evidence pipeline accepts the same source,
        // attach its deterministic MIR/ownership facts without making the
        // legacy executable gate depend on optional constructs.
        try
        {
            SafeCoreMirPipelineResult evidence = SafeCoreMirPipeline.Analyze(syntax,
                new SafeCoreMirPipelineOptions
                {
                    CancellationToken = cancellationToken,
                    RequireOwnershipEvidence = true,
                    Crates = crates.IsDefault ? [] : crates,
                });
            if (evidence.Mir is { IsSuccessful: true })
                lowered = AttachMirEvidence(lowered, evidence);
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException) { }
        return lowered;
    }

    private static SafeCoreClrResult AttachMirEvidence(
        SafeCoreClrResult emitted,
        SafeCoreMirPipelineResult evidence,
        bool requireOwnershipMetadata = false)
    {
        if (!emitted.IsSuccessful)
            return emitted;
        if (evidence.Mir is not { IsSuccessful: true } mir || mir.Program is null)
            return emitted;

        string snapshot = evidence.MirSnapshot ?? SafeCoreMirFormatting.Format(mir.Program);
        SafeCoreClrResult result = emitted with
        {
            MirSnapshot = snapshot,
            CleanupSnapshot = evidence.Cleanup?.Snapshot,
        };
        if (evidence.Ownership is { IsSuccessful: true } ownership)
        {
            try { result = SafeCoreOwnershipMetadata.Attach(result, ownership); }
            catch (ArgumentException exception) when (requireOwnershipMetadata)
            {
                // SafeCoreMir is an evidence-carrying executable profile. A
                // metadata-shape failure must remain a compilation diagnostic;
                // silently emitting a MIR-only assembly would make ownership
                // claims depend on whether the caller used check or compile.
                result = result with
                {
                    Diagnostics = [new Diagnostic("RSM2104",
                        "Safe-core ownership metadata could not be attached: " +
                        TrimDiagnostic(exception.Message), mir.Program.Functions[0].Source.Span)
                    { SourcePath = mir.Program.Functions[0].Source.SourcePath }],
                };
            }
        }
        return result;
    }

    private static bool IsCargoManifest(string path) => string.Equals(Path.GetFileName(path), "Cargo.toml", StringComparison.OrdinalIgnoreCase);

    private static string? TryResolveCargo(string path, CancellationToken cancellationToken, out IReadOnlyList<Diagnostic>? diagnostics)
    {
        diagnostics = null;
        if (!string.Equals(Path.GetFileName(path), "Cargo.toml", StringComparison.OrdinalIgnoreCase)) return null;
        CargoWorkspaceResult workspace = CargoWorkspace.Load(path, cancellationToken: cancellationToken);
        if (!workspace.IsSuccessful)
        {
            diagnostics = workspace.Diagnostics;
            return null;
        }
        return workspace.RootPackage.SourcePath;
    }

    private static IReadOnlyList<Diagnostic> MapDiagnostics(
        IReadOnlyList<Diagnostic> diagnostics, SafeCoreSourceMap? sourceMap)
    {
        if (sourceMap is null || diagnostics.Count == 0) return diagnostics;
        var mapped = new Diagnostic[diagnostics.Count];
        for (var index = 0; index < diagnostics.Count; index++)
        {
            Diagnostic diagnostic = diagnostics[index];
            SafeCoreSourceLocation location = sourceMap.MapSpan(diagnostic.Span);
            mapped[index] = diagnostic with { SourcePath = location.Document.Path, Span = location.Span };
        }
        return mapped;
    }

    private static void WriteArtifactsTransactionally(
        string assemblyPath,
        string pdbPath,
        string runtimeConfigPath,
        GeneratedAssembly generated)
    {
        var outputDirectory = Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The output path has no parent directory.");
        Directory.CreateDirectory(outputDirectory);

        // FileStream.Lock is an OS-level advisory/mandatory lock (depending on
        // the platform), so it serializes compiler instances in this process
        // and in other processes without leaving an ownership lease to expire.
        // The directory scope also covers transactions whose derived artifact
        // paths overlap despite having different assembly output names.
        using var outputLock = AcquireOutputDirectoryLock(outputDirectory);

        string transactionId = $".rsc-transaction-{Environment.ProcessId}-{Guid.NewGuid():N}";
        string transactionDirectory = Path.Combine(outputDirectory, transactionId);
        string stagingDirectory = Path.Combine(outputDirectory, transactionId, "staged");
        string backupDirectory = Path.Combine(outputDirectory, transactionId, "backups");
        var artifacts = new[]
        {
            new PendingArtifact(assemblyPath, Path.Combine(stagingDirectory, Path.GetFileName(assemblyPath)), generated.PeImage),
            new PendingArtifact(pdbPath, Path.Combine(stagingDirectory, Path.GetFileName(pdbPath)), generated.PdbImage),
            new PendingArtifact(
                runtimeConfigPath,
                Path.Combine(stagingDirectory, Path.GetFileName(runtimeConfigPath)),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(generated.RuntimeConfigJson)),
        };
        var movedTargets = new List<string>(artifacts.Length);
        var backups = new List<(string Target, string Backup)>(artifacts.Length);
        var transactionDiagnostics = new List<string>(capacity: artifacts.Length + 1);
        Exception? failure = null;
        var committed = false;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            Directory.CreateDirectory(backupDirectory);

            foreach (PendingArtifact artifact in artifacts)
            {
                WriteBytesDurably(artifact.StagedPath, artifact.Content);
            }

            foreach (PendingArtifact artifact in artifacts)
            {
                if (File.Exists(artifact.TargetPath))
                {
                    string backupPath = Path.Combine(backupDirectory, Path.GetFileName(artifact.TargetPath));
                    File.Move(artifact.TargetPath, backupPath);
                    backups.Add((artifact.TargetPath, backupPath));
                }

                File.Move(artifact.StagedPath, artifact.TargetPath);
                movedTargets.Add(artifact.TargetPath);
            }

            // Once every target has been installed, the transaction is
            // committed. Removing old backups is cleanup only and must never
            // cause the newly committed files to be rolled back.
            committed = true;
            DeleteBackupsBestEffort(backups, transactionDiagnostics);
        }
        catch (Exception exception)
        {
            failure = exception;
            if (!committed)
            {
                transactionDiagnostics.AddRange(
                    RollbackArtifacts(movedTargets, backups));
            }
        }
        finally
        {
            string? transactionCleanupDiagnostic = TryDeleteDirectory(transactionDirectory);
            if (transactionCleanupDiagnostic is not null)
            {
                transactionDiagnostics.Add(transactionCleanupDiagnostic);
            }
        }

        if (transactionDiagnostics.Count != 0)
        {
            Trace.WriteLine(
                $"RustSharp output transaction '{transactionDirectory}' cleanup: " +
                string.Join("; ", transactionDiagnostics));
        }

        if (failure is not null)
        {
            if (!committed && transactionDiagnostics.Count != 0)
            {
                throw new ArtifactTransactionException(failure, transactionDiagnostics);
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void DeleteBackupsBestEffort(
        List<(string Target, string Backup)> backups,
        List<string> diagnostics)
    {
        foreach ((_, string backupPath) in backups)
        {
            try
            {
                File.Delete(backupPath);
            }
            catch (Exception exception) when (IsFileSystemCleanupException(exception))
            {
                diagnostics.Add(
                    $"Could not remove backup '{backupPath}': {TrimDiagnostic(exception.Message)}");
            }
        }
    }

    private static List<string> RollbackArtifacts(
        List<string> movedTargets,
        List<(string Target, string Backup)> backups)
    {
        var diagnostics = new List<string>(capacity: movedTargets.Count + backups.Count);

        // Delete only targets that this transaction moved. Each operation is
        // independent so a locked artifact cannot prevent other backups from
        // being restored.
        for (var index = movedTargets.Count - 1; index >= 0; index--)
        {
            string targetPath = movedTargets[index];
            try
            {
                File.Delete(targetPath);
            }
            catch (Exception exception) when (IsFileSystemCleanupException(exception))
            {
                diagnostics.Add(
                    $"Could not remove moved target '{targetPath}': {TrimDiagnostic(exception.Message)}");
            }
        }

        for (var index = backups.Count - 1; index >= 0; index--)
        {
            (string targetPath, string backupPath) = backups[index];
            try
            {
                if (File.Exists(backupPath) && !File.Exists(targetPath))
                {
                    File.Move(backupPath, targetPath);
                }
            }
            catch (Exception exception) when (IsFileSystemCleanupException(exception))
            {
                diagnostics.Add(
                    $"Could not restore backup '{backupPath}' to '{targetPath}': " +
                    TrimDiagnostic(exception.Message));
            }
        }

        return diagnostics;
    }

    private static OutputPathLock AcquireOutputDirectoryLock(string outputDirectory)
    {
        string canonicalPath = GetCanonicalOutputDirectory(outputDirectory);
        OutputLockEntry entry;
        lock (OutputLockRegistryGate)
        {
            if (!OutputLockEntries.TryGetValue(canonicalPath, out entry!))
            {
                entry = new OutputLockEntry();
                OutputLockEntries.Add(canonicalPath, entry);
            }

            entry.ReferenceCount++;
        }

        var monitorHeld = false;
        try
        {
            if (!Monitor.TryEnter(
                    entry.Gate,
                    OutputLockAttempts * OutputLockRetryMilliseconds))
            {
                throw new IOException(
                    $"Could not acquire the in-process compiler output lock for '{outputDirectory}' after " +
                    $"{OutputLockAttempts * OutputLockRetryMilliseconds} ms.");
            }

            monitorHeld = true;
            FileStream lockStream = AcquireCrossProcessOutputLock(outputDirectory);
            return new OutputPathLock(canonicalPath, entry, lockStream);
        }
        catch
        {
            if (monitorHeld)
            {
                Monitor.Exit(entry.Gate);
            }

            ReleaseOutputLockEntry(canonicalPath, entry);
            throw;
        }
    }

    private static FileStream AcquireCrossProcessOutputLock(string outputDirectory)
    {
        string lockPath = GetOutputLockPath(outputDirectory);
        IOException? lastException = null;

        for (var attempt = 0; attempt < OutputLockAttempts; attempt++)
        {
            FileStream? lockStream = null;
            try
            {
                lockStream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    OperatingSystem.IsMacOS() ? FileShare.None : FileShare.ReadWrite,
                    bufferSize: 1,
                    FileOptions.None);
                if (!OperatingSystem.IsMacOS())
                {
                    lockStream.Lock(0, OutputLockRegionLength);
                }
                return lockStream;
            }
            catch (IOException exception)
            {
                lastException = exception;
                DisposeLockStream(lockStream);
                if (attempt + 1 < OutputLockAttempts)
                {
                    Thread.Sleep(OutputLockRetryMilliseconds);
                }
            }
            catch (PlatformNotSupportedException exception)
            {
                DisposeLockStream(lockStream);
                throw new IOException(
                    "The current platform does not support output path locking.",
                    exception);
            }
            catch (NotSupportedException exception)
            {
                DisposeLockStream(lockStream);
                throw new IOException(
                    "The current file system does not support output path locking.",
                    exception);
            }
        }

        throw new IOException(
            $"Could not acquire the compiler output lock for '{outputDirectory}' after " +
            $"{OutputLockAttempts} attempts ({OutputLockAttempts * OutputLockRetryMilliseconds} ms).",
            lastException);
    }

    private static void DisposeLockStream(FileStream? lockStream)
    {
        if (lockStream is null)
        {
            return;
        }

        try
        {
            lockStream.Dispose();
        }
        catch (Exception exception) when (IsFileSystemCleanupException(exception))
        {
            Trace.WriteLine($"Could not dispose output lock stream: {TrimDiagnostic(exception.Message)}");
        }
    }

    private static string GetOutputLockPath(string outputDirectory)
    {
        string canonicalPath = GetCanonicalOutputDirectory(outputDirectory);
        string pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)));
        // Keep the sidecar instead of deleting it on release: deleting a lock
        // file races with a waiter that is opening the same path. OS locks are
        // released automatically when the owning process exits.
        return Path.Combine(outputDirectory, $".rsc-directory-{pathHash}.lock");
    }

    private static string GetCanonicalOutputDirectory(string outputDirectory)
    {
        string fullPath = Path.GetFullPath(outputDirectory);
        string withoutTrailingSeparator = Path.TrimEndingDirectorySeparator(fullPath);
        return OperatingSystem.IsWindows()
            ? withoutTrailingSeparator.ToUpperInvariant()
            : withoutTrailingSeparator;
    }

    private static void ReleaseOutputLockEntry(string canonicalPath, OutputLockEntry entry)
    {
        lock (OutputLockRegistryGate)
        {
            if (entry.ReferenceCount > 0)
            {
                entry.ReferenceCount--;
            }

            if (entry.ReferenceCount == 0 &&
                OutputLockEntries.TryGetValue(canonicalPath, out OutputLockEntry? current) &&
                ReferenceEquals(current, entry))
            {
                _ = OutputLockEntries.Remove(canonicalPath);
            }
        }
    }

    private static string? TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return null;
        }
        catch (Exception exception) when (IsFileSystemCleanupException(exception))
        {
            return $"Could not remove transaction directory '{path}': {TrimDiagnostic(exception.Message)}";
        }
    }

    private static bool IsFileSystemCleanupException(Exception exception) =>
        exception is IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        ArgumentException or
        System.Security.SecurityException;

    private static string TrimDiagnostic(string value) =>
        value.Length <= MaximumTransactionDiagnosticCharacters
            ? value
            : value[..MaximumTransactionDiagnosticCharacters] + "...";

    private sealed class OutputPathLock : IDisposable
    {
        private readonly string canonicalPath;
        private readonly OutputLockEntry entry;
        private readonly FileStream lockStream;
        private bool disposed;

        public OutputPathLock(
            string canonicalPath,
            OutputLockEntry entry,
            FileStream lockStream)
        {
            this.canonicalPath = canonicalPath;
            this.entry = entry;
            this.lockStream = lockStream;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                try
                {
                    if (!OperatingSystem.IsMacOS())
                    {
                        lockStream.Unlock(0, OutputLockRegionLength);
                    }
                }
                catch (Exception exception) when (IsFileSystemCleanupException(exception))
                {
                    Trace.WriteLine($"Could not unlock output path: {TrimDiagnostic(exception.Message)}");
                }

                DisposeLockStream(lockStream);
            }
            finally
            {
                Monitor.Exit(entry.Gate);
                ReleaseOutputLockEntry(canonicalPath, entry);
            }
        }
    }

    private sealed class OutputLockEntry
    {
        public object Gate { get; } = new();

        public int ReferenceCount { get; set; }
    }

    private sealed class ArtifactTransactionException : IOException
    {
        public ArtifactTransactionException(Exception original, IReadOnlyList<string> diagnostics)
            : base(
                $"Output transaction failed: {TrimDiagnostic(original.Message)}; " +
                $"rollback/cleanup diagnostics: {string.Join("; ", diagnostics)}",
                original)
        {
        }
    }

    private static void WriteBytesDurably(string path, ReadOnlyMemory<byte> content)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            SourceReadBufferBytes,
            FileOptions.SequentialScan);
        stream.Write(content.Span);
        stream.Flush(flushToDisk: true);
    }

    private static SourceReadResult ReadSource(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return SourceReadResult.Failed(new Diagnostic(
                    "RSC0001",
                    $"Source file '{sourcePath}' does not exist.",
                    new TextSpan(0, 0)));
            }

            using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                SourceReadBufferBytes,
                FileOptions.SequentialScan);
            long length = stream.Length;
            if (length > MaximumSourceBytes)
            {
                return SourceReadResult.Failed(new Diagnostic(
                    "RSC0004",
                    $"Source files larger than {MaximumSourceBytes} bytes are not supported.",
                    new TextSpan(0, 0)));
            }

            var bytes = new byte[(int)length];
            var read = 0;
            for (var chunk = 0; chunk < MaximumSourceReadChunks && read < bytes.Length; chunk++)
            {
                int count = stream.Read(bytes, read, bytes.Length - read);
                if (count == 0)
                {
                    return SourceReadResult.Failed(new Diagnostic(
                        "RSC0001",
                        $"Source file '{sourcePath}' ended before the declared length was read.",
                        new TextSpan(0, 0)));
                }

                read += count;
            }

            if (read != bytes.Length || stream.ReadByte() >= 0)
            {
                return SourceReadResult.Failed(new Diagnostic(
                    "RSC0004",
                    $"Source files larger than {MaximumSourceBytes} bytes are not supported.",
                    new TextSpan(0, 0)));
            }

            ReadOnlySpan<byte> utf8Payload = bytes;
            if (utf8Payload.Length >= 3 &&
                utf8Payload[0] == 0xef &&
                utf8Payload[1] == 0xbb &&
                utf8Payload[2] == 0xbf)
            {
                utf8Payload = utf8Payload[3..];
            }

            string source = StrictUtf8.GetString(utf8Payload);
            return SourceReadResult.Succeeded(new SourceDocument(source, bytes));
        }
        catch (DecoderFallbackException exception)
        {
            return SourceReadResult.Failed(new Diagnostic(
                "RSC0005",
                $"Source file '{sourcePath}' is not valid UTF-8: {exception.Message}",
                new TextSpan(0, 0)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SourceReadResult.Failed(new Diagnostic(
                "RSC0001",
                $"Could not read source file '{sourcePath}': {exception.Message}",
                new TextSpan(0, 0)));
        }
    }

    private static Diagnostic? ValidateSourceText(string source, string sourcePath)
    {
        if (source.Length > MaximumSourceBytes)
        {
            return new Diagnostic(
                "RSC0004",
                $"Source files larger than {MaximumSourceBytes} bytes are not supported.",
                new TextSpan(0, 0));
        }

        try
        {
            if (StrictUtf8.GetByteCount(source) > MaximumSourceBytes)
            {
                return new Diagnostic(
                    "RSC0004",
                    $"Source files larger than {MaximumSourceBytes} bytes are not supported.",
                    new TextSpan(0, 0));
            }
        }
        catch (EncoderFallbackException exception)
        {
            return new Diagnostic(
                "RSC0005",
                $"Source text '{sourcePath}' is not valid UTF-8: {exception.Message}",
                new TextSpan(0, 0));
        }

        return null;
    }

    private static MetadataCrateLoadResult LoadMetadataCrates(
        IEnumerable<string> metadataReferences,
        CompilationProfile profile,
        IEnumerable<string>? requiredFunctions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadataReferences);
        const int maximumReferences = 256;
        var diagnostics = new List<Diagnostic>();
        var crates = new List<SafeCoreCrate>();
        var dependencies = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        string? expectedProfile = profile switch
        {
            CompilationProfile.SafeCorePrimitives => "safe-core-primitives-v1",
            CompilationProfile.SafeCoreGenerics => "safe-core-generics-v1",
            CompilationProfile.SafeCoreMir => SafeCoreMirPipeline.Profile,
            CompilationProfile.SafeCoreMirV2 => SafeCoreMirPipeline.ProfileV2,
            _ => null,
        };

        int count = 0;
        foreach (string reference in metadataReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > maximumReferences)
            {
                diagnostics.Add(new Diagnostic("RSC0011",
                    "A consumer compilation accepts at most " + maximumReferences + " metadata references.",
                    new TextSpan(0, 0)));
                break;
            }

            if (string.IsNullOrWhiteSpace(reference))
            {
                diagnostics.Add(new Diagnostic("RSC0011",
                    "A Rust# metadata reference path cannot be empty.", new TextSpan(0, 0)));
                continue;
            }

            string fullPath;
            try { fullPath = Path.GetFullPath(reference); }
            catch (Exception exception) when (exception is ArgumentException or PathTooLongException)
            {
                diagnostics.Add(new Diagnostic("RSC0011", "Invalid metadata reference path: " + exception.Message,
                    new TextSpan(0, 0)) { SourcePath = reference });
                continue;
            }

            if (!seen.Add(fullPath))
            {
                diagnostics.Add(new Diagnostic("RSC0011",
                    "Duplicate Rust# metadata reference: " + fullPath, new TextSpan(0, 0))
                    { SourcePath = fullPath });
                continue;
            }

            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(
                fullPath, expectedProfile, requiredFunctions);
            foreach (string message in imported.Diagnostics)
            {
                int separator = message.IndexOf(':');
                string code = separator > 0 ? message[..separator] : RustSharpMetadataConsumer.InvalidMetadata;
                string text = separator > 0 ? message[(separator + 1)..].TrimStart() : message;
                diagnostics.Add(new Diagnostic(code, text, new TextSpan(0, 0)) { SourcePath = fullPath });
            }

            if (!imported.IsSuccessful || imported.Document is null) continue;
            string assemblyName = imported.AssemblyName ?? Path.GetFileNameWithoutExtension(fullPath);
            if (string.IsNullOrWhiteSpace(assemblyName) || assemblyName.Length > 256)
            {
                diagnostics.Add(new Diagnostic(RustSharpMetadataConsumer.InvalidMetadata,
                    "Producer assembly has an invalid CLR assembly name.", new TextSpan(0, 0))
                    { SourcePath = fullPath });
                continue;
            }

            string alias = MetadataAlias(assemblyName);
            string fileAlias = MetadataAlias(Path.GetFileNameWithoutExtension(fullPath));
            Guid moduleVersionId = imported.ModuleVersionId ?? Guid.Empty;
            string scopePath = "crate::__rsc_ext_" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(fullPath + "\0" + moduleVersionId.ToString("D"))))[..32];
            string identity = "metadata:" + assemblyName + "@" + moduleVersionId.ToString("D");
            var exports = ImmutableArray.CreateBuilder<SafeCoreExternalFunction>(imported.Document.Functions.Length);
            foreach (RustSharpMetadataFunction function in imported.Document.Functions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourceName = function.SourceQualifiedName ?? function.Name;
                exports.Add(new SafeCoreExternalFunction(
                    sourceName,
                    function.Name,
                    function.Signature,
                    assemblyName,
                    fullPath,
                    function.IsPublic));
            }

            crates.Add(new SafeCoreCrate(scopePath, identity,
                ImmutableDictionary<string, string>.Empty)
            {
                Exports = exports.ToImmutable(),
            });
            AddAlias(alias, scopePath);
            if (!string.Equals(fileAlias, alias, StringComparison.Ordinal)) AddAlias(fileAlias, scopePath);
            string lowerAlias = alias.ToLowerInvariant();
            if (!string.Equals(lowerAlias, alias, StringComparison.Ordinal)) AddAlias(lowerAlias, scopePath);
        }

        if (diagnostics.Count == 0 || crates.Count != 0)
        {
            crates.Insert(0, new SafeCoreCrate("crate", "consumer", dependencies.ToImmutable()));
        }

        return new(crates.ToImmutableArray(), diagnostics.AsReadOnly());

        void AddAlias(string alias, string target)
        {
            if (dependencies.TryGetValue(alias, out string? existing))
            {
                if (!string.Equals(existing, target, StringComparison.Ordinal))
                    diagnostics.Add(new Diagnostic("RSC0011",
                        "Metadata reference aliases collide: '" + alias + "'.", new TextSpan(0, 0)));
                return;
            }

            dependencies.Add(alias, target);
        }

        static string MetadataAlias(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "dependency";
            var builder = new StringBuilder(name.Length);
            foreach (char character in name)
                builder.Append(char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_');
            if (builder.Length == 0) builder.Append("dependency");
            if (char.IsAsciiDigit(builder[0])) builder.Insert(0, '_');
            return builder.ToString();
        }
    }

    private sealed record MetadataCrateLoadResult(
        ImmutableArray<SafeCoreCrate> Crates,
        IReadOnlyList<Diagnostic> Diagnostics);

    private static bool PathsCollide(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool IsValidAssemblyName(string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName) || assemblyName.Length > 128)
        {
            return false;
        }

        foreach (var character in assemblyName)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record SourceDocument(string Source, byte[] Bytes);

    private sealed record SourceReadResult(SourceDocument? Document, Diagnostic? Diagnostic)
    {
        public static SourceReadResult Succeeded(SourceDocument document) => new(document, null);

        public static SourceReadResult Failed(Diagnostic diagnostic) => new(null, diagnostic);
    }

    private sealed record PendingArtifact(
        string TargetPath,
        string StagedPath,
        ReadOnlyMemory<byte> Content);
}
