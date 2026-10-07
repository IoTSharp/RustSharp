using System.Diagnostics;
using System.Globalization;
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
    /// <summary>Checks explicit managed import declarations against locked PE metadata.</summary>
    public static DotNetImportBindingResult CheckDotNetImports(string source, string sourcePath,
        IReadOnlyList<DotNetReferenceLock> references, CancellationToken cancellationToken = default) =>
        DotNetImportBinding.Bind(source, sourcePath, references, cancellationToken);

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
        => CheckFileWithDropProfile(sourcePath, SafeCoreDropCleanupProfile.LegacyV1, profile, cancellationToken);

    /// <summary>Checks every source-linked crate using one explicit Drop cleanup contract.</summary>
    public static CompilationResult CheckFileWithDropProfile(string sourcePath, SafeCoreDropCleanupProfile dropCleanupProfile,
        CompilationProfile profile = CompilationProfile.SafeCoreMirV2, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        Diagnostic? selection = ValidateDropCleanupSelection(sourcePath, profile, dropCleanupProfile);
        if (selection is not null) return CompilationResult.Failed([selection]);

        var fullSourcePath = Path.GetFullPath(sourcePath);
        if ((profile == CompilationProfile.SafeCoreGenerics || profile is CompilationProfile.SafeCoreMir or CompilationProfile.SafeCoreMirV2) && IsCargoManifest(fullSourcePath))
        {
            GenericPackageWorkspaceResult packages = GenericPackageWorkspace.Load(fullSourcePath, cancellationToken);
            if (!packages.IsSuccessful) return CompilationResult.Failed(packages.Diagnostics);
            CompilationResult checkedPackages = CheckCore(packages.SourceText, packages.RootSourcePath, profile,
                packages.Crates, cancellationToken, dropCleanupProfile: dropCleanupProfile);
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
            CompilationResult result = CheckCore(workspace.SourceText, fullSourcePath, profile, default,
                cancellationToken, dropCleanupProfile: dropCleanupProfile);
            return result with { Diagnostics = MapDiagnostics(result.Diagnostics, workspace.SourceMap!) };
        }

        var readResult = ReadSource(fullSourcePath);
        if (readResult.Diagnostic is not null)
        {
            return CompilationResult.Failed([readResult.Diagnostic]);
        }

        return CheckCore(readResult.Document!.Source, fullSourcePath, profile, default,
            cancellationToken, dropCleanupProfile: dropCleanupProfile);
    }

    public static CompilationResult Check(string source, string sourcePath = "<memory>",
        CompilationProfile profile = CompilationProfile.VerticalSlice, CancellationToken cancellationToken = default)
        => CheckCore(source, sourcePath, profile, default, cancellationToken);

    /// <summary>Checks the same versioned Drop policy used by source emission.</summary>
    public static CompilationResult CheckWithDropProfile(string source, string sourcePath,
        SafeCoreDropCleanupProfile dropCleanupProfile, CompilationProfile profile = CompilationProfile.SafeCoreMirV2,
        CancellationToken cancellationToken = default) =>
        CheckCore(source, sourcePath, profile, default, cancellationToken, dropCleanupProfile: dropCleanupProfile);

    /// <summary>Checks a local MIR program with an explicit, evidence-backed panic policy.</summary>
    public static CompilationResult CheckWithPanicStrategy(string source, string sourcePath,
        SafeCorePanicStrategy panicStrategy, CompilationProfile profile = CompilationProfile.SafeCoreMirV2,
        CancellationToken cancellationToken = default)
    {
        ValidatePanicStrategy(panicStrategy);
        if (profile is not (CompilationProfile.SafeCoreMir or CompilationProfile.SafeCoreMirV2))
            return RejectPanicStrategyProfile(sourcePath);
        return CheckCore(source, sourcePath, profile, default, cancellationToken, panicStrategy);
    }

    private static CompilationResult CheckCore(
        string source,
        string sourcePath,
        CompilationProfile profile,
        ImmutableArray<SafeCoreCrate> crates,
        CancellationToken cancellationToken,
        SafeCorePanicStrategy panicStrategy = SafeCorePanicStrategy.Unwind,
        SafeCoreDropCleanupProfile dropCleanupProfile = SafeCoreDropCleanupProfile.LegacyV1)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        Diagnostic? selection = ValidateDropCleanupSelection(sourcePath, profile, dropCleanupProfile);
        if (selection is not null) return CompilationResult.Failed([selection]);

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
                profile == CompilationProfile.SafeCoreMirV2, panicStrategy, dropCleanupProfile);
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
        => CheckWithMetadataReferences(source, sourcePath, profile, SafeCoreDropCleanupProfile.LegacyV1,
            metadataReferences, requiredFunctions, cancellationToken);

    /// <summary>Rejects producer and recursive owner cleanup-policy mismatches before checking source.</summary>
    public static CompilationResult CheckWithMetadataReferences(string source, string sourcePath,
        CompilationProfile profile, SafeCoreDropCleanupProfile dropCleanupProfile,
        IEnumerable<string> metadataReferences, IEnumerable<string>? requiredFunctions = null,
        CancellationToken cancellationToken = default)
    {
        Diagnostic? selection = ValidateDropCleanupSelection(sourcePath, profile, dropCleanupProfile);
        if (selection is not null) return CompilationResult.Failed([selection]);
        MetadataCrateLoadResult references = LoadMetadataCrates(
            metadataReferences, profile, requiredFunctions, dropCleanupProfile, cancellationToken);
        if (references.Diagnostics.Count != 0)
            return CompilationResult.Failed(references.Diagnostics);
        return CheckCore(source, sourcePath, profile, references.Crates, cancellationToken,
            dropCleanupProfile: dropCleanupProfile);
    }

    public static CompilationResult CompileFile(
        string sourcePath,
        string outputPath,
        string? assemblyName = null,
        CompilationProfile profile = CompilationProfile.VerticalSlice,
        CancellationToken cancellationToken = default)
        => CompileFileWithDropProfile(sourcePath, outputPath, SafeCoreDropCleanupProfile.LegacyV1,
            assemblyName, profile, cancellationToken);

    /// <summary>Emits a file or bounded Cargo source graph with one explicit cleanup contract.</summary>
    public static CompilationResult CompileFileWithDropProfile(string sourcePath, string outputPath,
        SafeCoreDropCleanupProfile dropCleanupProfile, string? assemblyName = null,
        CompilationProfile profile = CompilationProfile.SafeCoreMirV2, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        Diagnostic? selection = ValidateDropCleanupSelection(sourcePath, profile, dropCleanupProfile);
        if (selection is not null) return CompilationResult.Failed([selection]);

        cancellationToken.ThrowIfCancellationRequested();
        if (profile == CompilationProfile.SafeCoreTypes)
            return RejectTypeProfileEmission(sourcePath);

        var fullSourcePath = Path.GetFullPath(sourcePath);
        if ((profile == CompilationProfile.SafeCoreGenerics || profile is CompilationProfile.SafeCoreMir or CompilationProfile.SafeCoreMirV2) && IsCargoManifest(fullSourcePath))
        {
            GenericPackageWorkspaceResult packages = GenericPackageWorkspace.Load(fullSourcePath, cancellationToken);
            if (!packages.IsSuccessful) return CompilationResult.Failed(packages.Diagnostics);
            return CompileCore(packages.SourceText, packages.RootSourcePath, Path.GetFullPath(outputPath), assemblyName,
                packages.SourceMap!.Documents[0].Bytes, profile, cancellationToken, packages.SourceMap, packages.Crates,
                dropCleanupProfile: dropCleanupProfile);
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
                assemblyName, workspace.SourceMap!.Documents[0].Bytes, profile, cancellationToken, workspace.SourceMap,
                dropCleanupProfile: dropCleanupProfile);
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
            cancellationToken,
            dropCleanupProfile: dropCleanupProfile);
    }

    public static CompilationResult Compile(
        string source,
        string sourcePath,
        string outputPath,
        string? assemblyName = null,
        CompilationProfile profile = CompilationProfile.VerticalSlice,
        CancellationToken cancellationToken = default)
        => CompileWithPanicStrategy(source, sourcePath, outputPath, SafeCorePanicStrategy.Unwind,
            assemblyName, profile, cancellationToken);

    /// <summary>Emits local MIR calls and cleanup using the declared panic policy.</summary>
    public static CompilationResult CompileWithPanicStrategy(
        string source,
        string sourcePath,
        string outputPath,
        SafeCorePanicStrategy panicStrategy,
        string? assemblyName = null,
        CompilationProfile profile = CompilationProfile.SafeCoreMirV2,
        CancellationToken cancellationToken = default) =>
        CompileWithPolicies(source, sourcePath, outputPath, panicStrategy, SafeCoreDropCleanupProfile.LegacyV1,
            assemblyName, profile, cancellationToken);

    /// <summary>Emits a source program using an explicitly versioned Drop cleanup contract.</summary>
    public static CompilationResult CompileWithDropProfile(string source, string sourcePath, string outputPath,
        SafeCoreDropCleanupProfile dropCleanupProfile, SafeCorePanicStrategy panicStrategy = SafeCorePanicStrategy.Unwind,
        string? assemblyName = null, CompilationProfile profile = CompilationProfile.SafeCoreMirV2,
        CancellationToken cancellationToken = default) =>
        CompileWithPolicies(source, sourcePath, outputPath, panicStrategy, dropCleanupProfile,
            assemblyName, profile, cancellationToken);

    private static CompilationResult CompileWithPolicies(string source, string sourcePath, string outputPath,
        SafeCorePanicStrategy panicStrategy, SafeCoreDropCleanupProfile dropCleanupProfile,
        string? assemblyName, CompilationProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ValidatePanicStrategy(panicStrategy);
        Diagnostic? selection = ValidateDropCleanupSelection(sourcePath, profile, dropCleanupProfile);
        if (selection is not null) return CompilationResult.Failed([selection]);
        if (panicStrategy != SafeCorePanicStrategy.Unwind &&
            profile is not (CompilationProfile.SafeCoreMir or CompilationProfile.SafeCoreMirV2))
            return RejectPanicStrategyProfile(sourcePath);

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
            cancellationToken,
            panicStrategy: panicStrategy, dropCleanupProfile: dropCleanupProfile);
    }

    private static CompilationResult CompileCoreWithCrates(
        string source,
        string sourcePath,
        string outputPath,
        string? assemblyName,
        CompilationProfile profile,
        ImmutableArray<SafeCoreCrate> crates,
        SafeCoreDropCleanupProfile dropCleanupProfile,
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
            sourceBytes, profile, cancellationToken, crates: crates, dropCleanupProfile: dropCleanupProfile);
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
        => CompileWithMetadataReferences(source, sourcePath, outputPath, assemblyName, profile,
            SafeCoreDropCleanupProfile.LegacyV1, metadataReferences, requiredFunctions, cancellationToken);

    /// <summary>Emits only after all independent producers and nominal owners match the selected contract.</summary>
    public static CompilationResult CompileWithMetadataReferences(string source, string sourcePath, string outputPath,
        string? assemblyName, CompilationProfile profile, SafeCoreDropCleanupProfile dropCleanupProfile,
        IEnumerable<string> metadataReferences, IEnumerable<string>? requiredFunctions = null,
        CancellationToken cancellationToken = default)
    {
        Diagnostic? selection = ValidateDropCleanupSelection(sourcePath, profile, dropCleanupProfile);
        if (selection is not null) return CompilationResult.Failed([selection]);
        MetadataCrateLoadResult references = LoadMetadataCrates(
            metadataReferences, profile, requiredFunctions, dropCleanupProfile, cancellationToken);
        if (references.Diagnostics.Count != 0)
            return CompilationResult.Failed(references.Diagnostics);
        return CompileCoreWithCrates(source, sourcePath, outputPath, assemblyName, profile,
            references.Crates, dropCleanupProfile, cancellationToken);
    }

    private static CompilationResult CompileCore(
        string source,
        string sourcePath,
        string outputPath,
        string? assemblyName,
        ReadOnlyMemory<byte> sourceBytes,
        CompilationProfile profile,
        CancellationToken cancellationToken,
        SafeCoreSourceMap? sourceMap = null, ImmutableArray<SafeCoreCrate> crates = default,
        SafeCorePanicStrategy panicStrategy = SafeCorePanicStrategy.Unwind,
        SafeCoreDropCleanupProfile dropCleanupProfile = SafeCoreDropCleanupProfile.LegacyV1)
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
            safeCore = AnalyzeSafeCore(source, sourcePath, profile, cancellationToken, crates, panicStrategy, dropCleanupProfile);
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
                    safeCore.CleanupSnapshot,
                    BuildCallContracts(safeCore, cancellationToken),
                    sourceValueTypes: BuildSourceValueTypes(safeCore, cancellationToken),
                    sourceStructuralTypes: BuildSourceStructuralTypes(safeCore, cancellationToken));
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

    private static Diagnostic? ValidateDropCleanupSelection(string sourcePath, CompilationProfile profile,
        SafeCoreDropCleanupProfile dropCleanupProfile)
    {
        if (dropCleanupProfile is not (SafeCoreDropCleanupProfile.LegacyV1 or SafeCoreDropCleanupProfile.NativeV2))
            throw new ArgumentOutOfRangeException(nameof(dropCleanupProfile));
        return dropCleanupProfile == SafeCoreDropCleanupProfile.NativeV2 &&
            profile is not (CompilationProfile.SafeCoreMir or CompilationProfile.SafeCoreMirV2)
            ? new Diagnostic("RSC0010", "NativeV2 Drop cleanup requires a typed MIR compilation profile.",
                new TextSpan(0, 0)) { SourcePath = sourcePath }
            : null;
    }

    private static void ValidatePanicStrategy(SafeCorePanicStrategy strategy)
    {
        if (!Enum.IsDefined(strategy)) throw new ArgumentOutOfRangeException(nameof(strategy));
    }

    private static CompilationResult RejectPanicStrategyProfile(string sourcePath) =>
        CompilationResult.Failed([new Diagnostic("RSC0010",
            "An explicit panic strategy requires an executable safe-core MIR profile.", new TextSpan(0, 0))
        { SourcePath = sourcePath }]);

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
        bool enableRepeatedArrays = false, SafeCorePanicStrategy panicStrategy = SafeCorePanicStrategy.Unwind,
        SafeCoreDropCleanupProfile dropCleanupProfile = SafeCoreDropCleanupProfile.LegacyV1)
    {
        SafeCoreMirPipelineResult result = SafeCoreMirPipeline.Analyze(source, sourcePath,
            new SafeCoreMirPipelineOptions
            {
                CancellationToken = cancellationToken,
                RequireOwnershipEvidence = true,
                RequireCleanupEvidence = true,
                EnableRepeatedArrays = enableRepeatedArrays,
                EnableP1Extensions = enableRepeatedArrays,
                PanicStrategy = panicStrategy,
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
            result.Mir!.Program!, dropCleanupProfile, cancellationToken);
        backend = AttachMirEvidence(backend, result, requireOwnershipMetadata: true, cancellationToken);
        return backend.IsSuccessful
            ? new(true, [], null)
            : CompilationResult.Failed(backend.Diagnostics.Count == 0
                ? [new Diagnostic("RSM2102", "Typed MIR CLR lowering failed.", new TextSpan(0, 0))]
                : backend.Diagnostics);
    }

    private static SafeCoreClrResult AnalyzeSafeCore(string source, string sourcePath,
        CompilationProfile profile, CancellationToken cancellationToken, ImmutableArray<SafeCoreCrate> crates = default,
        SafeCorePanicStrategy panicStrategy = SafeCorePanicStrategy.Unwind,
        SafeCoreDropCleanupProfile dropCleanupProfile = SafeCoreDropCleanupProfile.LegacyV1)
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
                    PanicStrategy = panicStrategy,
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
                evidence.Mir!.Program!, dropCleanupProfile, cancellationToken);
            return AttachMirEvidence(emitted, evidence, requireOwnershipMetadata: true, cancellationToken);
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

        // Keep the primitive profile's accepted syntax, diagnostics and limits
        // as its compatibility gate. Emission consumes the same validated MIR
        // and ownership/cleanup evidence as the richer executable profiles;
        // a failed stage cannot fall back to a separate primitive backend.
        SafeCoreMirPipelineResult primitiveEvidence = SafeCoreMirPipeline.Analyze(syntax,
            new SafeCoreMirPipelineOptions
            {
                CancellationToken = cancellationToken,
                RequireOwnershipEvidence = true,
                RequireCleanupEvidence = true,
                Crates = crates.IsDefault ? [] : crates,
            });
        if (!primitiveEvidence.IsSuccessful)
            return new([], [], primitiveEvidence.Diagnostics);

        SafeCoreClrResult primitive = SafeCoreMirClrLowering.Lower(
            primitiveEvidence.Mir!.Program!, cancellationToken);
        if (!primitive.IsSuccessful)
            return primitive with
            {
                // Preserve the primitive v1 diagnostic for CLR budgets while
                // retaining rejection from the mandatory MIR backend.
                Diagnostics = primitive.Diagnostics.Select(static diagnostic =>
                    diagnostic.Code == SafeCoreMirClrLowering.LimitReached
                        ? diagnostic with
                        {
                            Code = "RST2001",
                            Message = "CLR lowering exceeded its local, block, work, nesting or time limit.",
                        }
                        : diagnostic).ToArray(),
            };
        // The richer type pass's body suffix is internal; the primitive v1
        // metadata ABI has always exposed the original source declaration.
        primitive = primitive with
        {
            Methods = primitive.Methods.Select(static method =>
                method.SourceQualifiedName is { } sourceName && sourceName.EndsWith("#value", StringComparison.Ordinal)
                    ? new ClrLirMethod(method.Name, method.ReturnType, method.Parameters, method.Locals, method.Blocks,
                        method.ExceptionCleanup, method.FaultTryBlockCount, method.GuardedExceptionCleanup, method.PanicHandling)
                    {
                        SourceQualifiedName = sourceName[..^6],
                        IsPublic = method.IsPublic,
                        IsCompilerGenerated = method.IsCompilerGenerated,
                    }
                    : method).ToArray(),
        };
        return AttachMirEvidence(primitive, primitiveEvidence, requireOwnershipMetadata: true, cancellationToken);
    }

    private static IEnumerable<RustSharpMetadataCallContract> BuildCallContracts(SafeCoreClrResult program,
        CancellationToken cancellationToken)
    {
        // Scalars have a checked language-level Copy ABI. Nominal values and
        // GC-owned references need semantic contracts; CLR value/object shapes
        // are deliberately not used to infer their ownership or lifetimes.
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        SafeCoreMirReferenceProvenanceResult? provenance = program.SourceMir is null ? null :
            SafeCoreMirReferenceProvenance.Analyze(program.SourceMir, new() { CancellationToken = cancellationToken });
        SafeCoreMirReturnedVariantsResult? variants = program.SourceMir is null ? null :
            SafeCoreMirReturnedVariants.Analyze(program.SourceMir, new() { CancellationToken = cancellationToken });
        if (variants is { IsSuccessful: false }) throw new InvalidOperationException("Source returned variant evidence could not be validated.");
        foreach (ClrLirMethod method in program.Methods)
        {
            CheckBudget();
            SafeCoreMirFunction? sourceFunction = program.SourceMir?.Functions.FirstOrDefault(function =>
                function.Name == method.SourceQualifiedName || function.Name == method.SourceQualifiedName + "#value");
            if (sourceFunction is not null &&
                (sourceFunction.Locals.Where(static local => local.Kind == SafeCoreMirLocalKind.Parameter)
                    .Any(static local => local.Type.Kind is not (SafeCoreSemanticTypeKind.I32 or SafeCoreSemanticTypeKind.Bool)) ||
                 sourceFunction.ReturnType.Kind is not (SafeCoreSemanticTypeKind.Unit or SafeCoreSemanticTypeKind.I32 or SafeCoreSemanticTypeKind.Bool)))
            {
                SafeCoreMirLocal[] parameters = sourceFunction.Locals.Where(static local => local.Kind == SafeCoreMirLocalKind.Parameter).ToArray();
                IReadOnlyList<SafeCoreMirReferenceOrigin> origins = provenance?.Functions.FirstOrDefault(value => value.FunctionId == sourceFunction.Id)?.ReturnOrigins ?? [];
                var encodedOrigins = new List<string>();
                foreach (SafeCoreMirReferenceOrigin origin in origins)
                {
                    CheckBudget();
                    encodedOrigins.Add(SafeCoreSourceOriginCodec.Format(origin, cancellationToken));
                }
                covered.Add(method.Name);
                covered.Add(sourceFunction.Name);
                if (sourceFunction.Name.EndsWith("#value", StringComparison.Ordinal)) covered.Add(sourceFunction.Name[..^6]);
                yield return new RustSharpMetadataCallContract(method.Name, sourceFunction.PanicStrategy == SafeCorePanicStrategy.Abort ? "abort" : "unwind",
                    parameters.Select(local => SourceEffect(local.Type)).ToArray(),
                    sourceFunction.ReturnType.Kind == SafeCoreSemanticTypeKind.Unit ? "unit" : SourceEffect(sourceFunction.ReturnType))
                {
                    Schema = RustSharpMetadataCallContract.SourceSchema,
                    SourceParameterTypes = parameters.Select(local => SafeCoreSourceTypeCodec.Format(local.Type, cancellationToken)).ToArray(),
                    SourceParameterStaticLifetimes = parameters.Select(static local => local.RequiresStaticLifetime).ToArray(),
                    SourceReturnType = SafeCoreSourceTypeCodec.Format(sourceFunction.ReturnType, cancellationToken),
                    ReturnOrigins = encodedOrigins,
                    SourceReturnVariants = variants?.Functions.FirstOrDefault(summary => summary.FunctionId == sourceFunction.Id)?.Variants
                        .SelectMany(variant => variant.VariantNames.Select(name => new RustSharpMetadataSourceReturnVariant(
                            variant.ValuePath.Select(projection => SafeCoreSourceOriginCodec.FormatProjection(projection, cancellationToken)).ToArray(), name))).ToArray() ?? [],
                };
                continue;
            }
            if (method.IsCompilerGenerated || !IsContractType(method.ReturnType, allowVoid: true) ||
                method.Parameters.Any(static type => !IsContractType(type, allowVoid: false)))
                continue;

            string identity = method.SourceQualifiedName ?? method.Name;
            covered.Add(method.Name);
            covered.Add(identity);
            if (identity.EndsWith("#value", StringComparison.Ordinal)) covered.Add(identity[..^6]);
            RustSharpMetadataOwnershipFunction? ownership = program.Ownership.FirstOrDefault(value =>
                string.Equals(value.FunctionId, identity, StringComparison.Ordinal) ||
                string.Equals(value.FunctionId, identity + "#value", StringComparison.Ordinal));
            // Generic instances may share the declaration's source identity.
            // Bind each contract to its actual emitted method first; metadata
            // linking retains a source alias only when it names one method.
            bool scalar = method.Parameters.All(static type => type.Kind is ClrLirTypeKind.I32 or ClrLirTypeKind.Bool) &&
                method.ReturnType.Kind is ClrLirTypeKind.Void or ClrLirTypeKind.I32 or ClrLirTypeKind.Bool;
            yield return new RustSharpMetadataCallContract(method.Name, ownership?.PanicStrategy ?? "unwind",
                method.Parameters.Select(static type => ContractTerm(type, isReturn: false)).ToArray(),
                method.ReturnType.Kind == ClrLirTypeKind.Void ? "unit" : ContractTerm(method.ReturnType, isReturn: true))
            {
                Schema = scalar ? RustSharpMetadataCallContract.ScalarSchema : null,
            };
        }

        foreach (RustSharpMetadataOwnershipFunction ownership in program.Ownership)
        {
            CheckBudget();
            string identity = ownership.FunctionId.EndsWith("#value", StringComparison.Ordinal)
                ? ownership.FunctionId[..^6] : ownership.FunctionId;
            if (!covered.Contains(identity))
                yield return new(identity, ownership.PanicStrategy);
        }

        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (program.Methods.Count > RustSharpMetadataDocument.MaximumFunctions ||
                program.Ownership.Count > RustSharpMetadataDocument.MaximumOwnershipFunctions ||
                clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("Source call metadata exceeded its bounded generation budget.");
        }

        static bool IsContractType(ClrLirType type, bool allowVoid) =>
            type.Kind is ClrLirTypeKind.I32 or ClrLirTypeKind.Bool ||
            (allowVoid && type.Kind == ClrLirTypeKind.Void) ||
            type == ClrLirType.Any ||
            type.Kind == ClrLirTypeKind.ByReference && (type.Name == "I32" || type.Name == "Bool");

        static string ContractTerm(ClrLirType type, bool isReturn) => type.Kind switch
        {
            ClrLirTypeKind.I32 or ClrLirTypeKind.Bool => "copy",
            ClrLirTypeKind.ByReference when type.IsMutable => "borrow:mut",
            ClrLirTypeKind.ByReference => "borrow:shared",
            ClrLirTypeKind.Any => "borrow:shared",
            ClrLirTypeKind.Void when isReturn => "unit",
            _ => throw new InvalidOperationException("The source call contract type is outside the bounded ABI."),
        };

        string SourceEffect(SafeCoreType type) => type.Kind switch
        {
            SafeCoreSemanticTypeKind.Reference => type.IsMutable ? "borrow:mut" : "borrow:shared",
            SafeCoreSemanticTypeKind.Adt or SafeCoreSemanticTypeKind.Tuple or SafeCoreSemanticTypeKind.Array => IsSourceCopy(type, 0) ? "copy" : "move",
            _ => "copy",
        };

        bool IsSourceCopy(SafeCoreType type, int depth)
        {
            CheckBudget();
            if (depth > 32) throw new ArgumentException("Source ownership type depth exceeded its package budget.");
            return type.Kind switch
            {
                SafeCoreSemanticTypeKind.Adt => program.SourceMir?.AdtLayouts.FirstOrDefault(layout => layout.Type == type)?.IsCopy == true,
                SafeCoreSemanticTypeKind.Reference => !type.IsMutable,
                SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Tuple => type.Elements.All(child => IsSourceCopy(child, depth + 1)),
                _ => true,
            };
        }
    }

    private static IEnumerable<RustSharpMetadataSourceValueType> BuildSourceValueTypes(SafeCoreClrResult program, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var privateModules = new HashSet<string>(StringComparer.Ordinal);
        foreach (SafeCoreHirNode node in program.SourceHir?.Nodes ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Source export visibility exceeded its package budget.");
            if (node.Kind == SafeCoreHirNodeKind.Module && node.DeclaredSymbol is { IsPublic: false } module)
                privateModules.Add(module.QualifiedName);
        }
        foreach (SafeCoreMirAdtLayout layout in program.SourceMir?.AdtLayouts ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Source layouts exceeded the package budget.");
            SafeCoreExternalValueType? externalLayout = layout.ExternalSourceLayout;
            string clrName = layout.ExternalAssemblyName is null ? "mir_value_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layout.Type.Kind + "|" + layout.Type))).ToLowerInvariant()[..24]
                : layout.ExternalAssemblyName + "::" + layout.ExternalClrName;
            if (externalLayout is null && !program.ValueTypes.Any(value => value.Name == clrName)) continue;
            SafeCoreMirFunction? destructor = program.SourceMir!.Functions.FirstOrDefault(function => function.IsDestructor &&
                function.Locals.Count > 0 && function.Locals[0].Type.ElementType == layout.Type);
            string? dropName = externalLayout?.DropFunction?.ClrName ?? (destructor is null ? null : program.Methods.FirstOrDefault(method => method.SourceQualifiedName == destructor.Name)?.Name);
            SafeCoreHirNode? declaration = program.SourceHir?.GetNode(layout.Source.HirNodeId);
            yield return new(layout.Type.Name!, clrName, layout.Fields.Select((field, index) => new RustSharpMetadataSourceField(field.Name,
                SafeCoreSourceTypeCodec.Format(field.Type, cancellationToken),
                externalLayout?.Fields[index].IsPublic ?? (layout.Variants.Count != 0 || (program.SourceHir?.GetNode(field.Source.HirNodeId).Modifiers.HasFlag(SafeCoreHirNodeModifiers.Public) ?? false)))
                { RequiresStaticLifetime = field.RequiresStaticLifetime }).ToArray(),
                layout.IsCopy, dropName)
            {
                Owner = externalLayout?.Owner is { } owner ? new RustSharpMetadataSourceOwner(owner.AssemblyName, owner.SourceName, owner.ClrName,
                    owner.ModuleVersionId, owner.SourceSha256, owner.AssemblySha256) : null,
                IsPublic = externalLayout is not null ? layout.ExternalSourceIsPublic : declaration?.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Public) == true &&
                    !privateModules.Any(module => layout.Type.Name!.StartsWith(module + "::", StringComparison.Ordinal)),
                ConstructorKind = layout.Variants.Count != 0 ? "enum" : externalLayout?.Kind == SafeCoreExternalTypeKind.TupleStruct || declaration?.Modifiers.HasFlag(SafeCoreHirNodeModifiers.TupleStruct) == true ? "tuple" :
                    externalLayout?.Kind == SafeCoreExternalTypeKind.UnitStruct ? "unit" :
                    declaration?.Modifiers.HasFlag(SafeCoreHirNodeModifiers.UnitStruct) == true ? "unit" : "named",
                Variants = layout.Variants.Count == 0 ? null : layout.Variants.Select((variant, index) => new RustSharpMetadataSourceVariant(
                    variant.Name, variant.Discriminant, variant.FieldOffset,
                    variant.Fields.Select(field => new RustSharpMetadataSourceField(field.Name,
                        SafeCoreSourceTypeCodec.Format(field.Type, cancellationToken), true)
                        { RequiresStaticLifetime = field.RequiresStaticLifetime }).ToArray())
                {
                    ConstructorKind = externalLayout?.Variants[index].Kind == SafeCoreExternalTypeKind.TupleStruct || program.SourceHir?.GetNode(variant.Source.HirNodeId).Modifiers.HasFlag(SafeCoreHirNodeModifiers.TupleStruct) == true ? "tuple" :
                        externalLayout?.Variants[index].Kind == SafeCoreExternalTypeKind.UnitStruct ? "unit" :
                        program.SourceHir?.GetNode(variant.Source.HirNodeId).Modifiers.HasFlag(SafeCoreHirNodeModifiers.UnitStruct) == true ? "unit" : "named",
                }).ToArray(),
            };
        }
    }

    private static IEnumerable<RustSharpMetadataSourceStructuralType> BuildSourceStructuralTypes(
        SafeCoreClrResult program, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        IReadOnlyList<SafeCoreMirImportedStructuralType> importedTypes = program.SourceMir?.ImportedStructuralTypes ?? [];
        if (importedTypes.Count > RustSharpMetadataDocument.MaximumValueTypes)
            throw new ArgumentException("Imported source structural bindings exceed their package count budget.");
        foreach (SafeCoreMirImportedStructuralType structural in importedTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("Source structural bindings exceeded their package time budget.");
            string clrName = structural.AssemblyName + "::" + structural.ClrName;
            if (!program.ValueTypes.Any(layout => layout.Name == clrName)) continue;
            SafeCoreExternalOwner owner = structural.Owner ??
                throw new InvalidDataException("A re-exported anonymous source layout requires its independently checked original producer proof.");
            yield return new(SafeCoreSourceTypeCodec.Format(structural.Type, cancellationToken), clrName,
                new(owner.AssemblyName, owner.SourceName, owner.ClrName, owner.ModuleVersionId, owner.SourceSha256, owner.AssemblySha256));
        }
    }

    private static SafeCoreClrResult AttachMirEvidence(
        SafeCoreClrResult emitted,
        SafeCoreMirPipelineResult evidence,
        bool requireOwnershipMetadata = false,
        CancellationToken cancellationToken = default)
    {
        if (!emitted.IsSuccessful)
            return emitted;
        if (evidence.Mir is not { IsSuccessful: true } mir || mir.Program is null)
            return emitted;

        SafeCoreMirReturnedVariantsResult returnedVariants = SafeCoreMirReturnedVariants.Analyze(mir.Program,
            new() { CancellationToken = cancellationToken });
        if (!returnedVariants.IsSuccessful)
            return emitted with { Diagnostics = returnedVariants.Diagnostics };
        string snapshot = AddReturnedVariantEvidence(evidence.MirSnapshot ?? SafeCoreMirFormatting.Format(mir.Program), returnedVariants, cancellationToken);
        SafeCoreClrResult result = emitted with
        {
            MirSnapshot = snapshot,
            CleanupSnapshot = evidence.Cleanup?.Snapshot,
            SourceMir = mir.Program,
            SourceHir = evidence.Hir,
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

    private static string AddReturnedVariantEvidence(string snapshot, SafeCoreMirReturnedVariantsResult variants,
        CancellationToken cancellationToken)
    {
        if (snapshot.Length > RustSharpMetadataDocument.MaximumJsonCharacters)
            throw new TimeoutException("Returned variant snapshot exceeded its character budget.");
        string[] lines = snapshot.Split('\n');
        if (lines.Length > 65_536) throw new TimeoutException("Returned variant snapshot exceeded its line budget.");
        var clock = Stopwatch.StartNew();
        var result = new StringBuilder(snapshot.Length);
        foreach (string line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Returned variant snapshot exceeded its time budget.");
            result.Append(line).Append('\n');
            if (!line.StartsWith("fn @", StringComparison.Ordinal)) continue;
            int separator = line.IndexOf(' ', 4);
            if (separator < 0 || !int.TryParse(line.AsSpan(4, separator - 4), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                throw new InvalidOperationException("A validated MIR function has no canonical arena ID.");
            foreach (SafeCoreMirReturnedVariant variant in variants.Functions.First(summary => summary.FunctionId == id).Variants)
            foreach (string name in variant.VariantNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Returned variant snapshot exceeded its time budget.");
                result.Append("  return_variant ").Append(SafeCoreSourceOriginCodec.FormatReturnedVariant(variant.ValuePath, name, cancellationToken)).Append('\n');
                if (result.Length > RustSharpMetadataDocument.MaximumJsonCharacters) throw new TimeoutException("Returned variant snapshot exceeded its character budget.");
            }
        }
        if (result.Length > 0) result.Length--;
        return result.ToString();
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
        if (workspace.RootPackageOrNull is not { } rootPackage)
        {
            diagnostics = [new Diagnostic(CargoWorkspace.UnsupportedManifestDiagnostic,
                "A virtual Cargo workspace requires an explicit package selection.", new TextSpan(0, 0)) { SourcePath = path }];
            return null;
        }
        return rootPackage.SourcePath;
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
        if (generated.RequiresMirRuntime && string.Equals(Path.GetFileName(assemblyPath),
                "RustSharp.Runtime.dll", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The MIR output filename is reserved for its RustSharp.Runtime.dll dependency.");
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
        if (generated.RequiresMirRuntime)
        {
            string runtimePath = Path.Combine(outputDirectory, "RustSharp.Runtime.dll");
            artifacts = [.. artifacts, new PendingArtifact(runtimePath,
                Path.Combine(stagingDirectory, "RustSharp.Runtime.dll"),
                File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "RustSharp.Runtime.dll")))];
        }
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
        SafeCoreDropCleanupProfile dropCleanupProfile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadataReferences);
        const int maximumReferences = 256;
        var referenceClock = Stopwatch.StartNew();
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

        // Normalize and sort paths before importing. The caller may provide
        // equivalent references in any order; crate scope and alias
        // assignment must remain stable so the emitted PE/PDB and metadata
        // bytes are reproducible across independent builds.
        var normalizedReferences = new List<string>();
        int count = 0;
        foreach (string reference in metadataReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (referenceClock.Elapsed > TimeSpan.FromSeconds(10))
            {
                diagnostics.Add(new Diagnostic("RSC0011", "Metadata reference loading exceeded its ten second budget.", new TextSpan(0, 0)));
                return new([], diagnostics);
            }
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

            normalizedReferences.Add(fullPath);
        }

        foreach (string fullPath in normalizedReferences.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (referenceClock.Elapsed > TimeSpan.FromSeconds(10))
            {
                diagnostics.Add(new Diagnostic("RSC0011", "Metadata reference loading exceeded its ten second budget.", new TextSpan(0, 0)));
                return new([], diagnostics);
            }

            RustSharpMetadataImportResult imported = RustSharpMetadataConsumer.ReadAssembly(
                fullPath, dropCleanupProfile, expectedProfile, requiredFunctions, normalizedReferences, cancellationToken);
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
            string identity = "metadata:" + assemblyName + "@" + moduleVersionId.ToString("D");
            // Scope identity is derived from producer identity rather than its
            // checkout path. This keeps source-package HIR and generic
            // metadata stable when the same producer is built in another
            // workspace directory, while the full path remains available on
            // the external export for diagnostics and PE resolution.
            string scopePath = "crate::__rsc_ext_" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
            using FileStream producerStream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string producerSha256 = Convert.ToHexString(SHA256.HashData(producerStream));
            var ownerDocuments = new Dictionary<string, RustSharpMetadataDocument>(StringComparer.Ordinal) { [assemblyName] = imported.Document };
            RustSharpMetadataDocument OwnerDocument(RustSharpMetadataSourceOwner? owner)
            {
                if (owner is null) return imported.Document;
                if (ownerDocuments.TryGetValue(owner.AssemblyName, out RustSharpMetadataDocument? document)) return document;
                if (ownerDocuments.Count >= 64 || referenceClock.Elapsed > TimeSpan.FromSeconds(10))
                    throw new InvalidDataException("Imported owner reconstruction exceeded its work or time budget.");
                if (!imported.ResolvedOwnerPaths.TryGetValue(owner.AssemblyName, out string? ownerPath))
                    throw new InvalidDataException("The nominal owner has no verified current PE path.");
                RustSharpMetadataImportResult loaded = RustSharpMetadataConsumer.ReadAssembly(ownerPath, dropCleanupProfile,
                    dependencyPaths: normalizedReferences, cancellationToken: cancellationToken);
                if (!loaded.IsSuccessful || loaded.Document is null) throw new InvalidDataException("The nominal owner's current metadata is invalid.");
                ownerDocuments.Add(owner.AssemblyName, loaded.Document);
                return loaded.Document;
            }
            var exports = ImmutableArray.CreateBuilder<SafeCoreExternalFunction>(imported.Document.Functions.Length);
            ImmutableArray<SafeCoreExternalStructuralType> structuralTypes;
            try { structuralTypes = ReadStructuralTypes(imported.Document, assemblyName, scopePath, moduleVersionId,
                producerSha256, OwnerDocument, cancellationToken); }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or TimeoutException)
            {
                diagnostics.Add(new Diagnostic("RSC0011", exception.Message, new TextSpan(0, 0)) { SourcePath = fullPath });
                continue;
            }
            var sourceLayouts = imported.Document.SourceValueTypes.Select(layout => new SafeCoreExternalValueType(layout.Name, layout.Owner?.ClrName ?? layout.ClrName,
                layout.Fields.Select(field => new SafeCoreExternalField(field.Name, field.Type, field.IsPublic)
                { RequiresStaticLifetime = field.RequiresStaticLifetime }).ToImmutableArray(), layout.IsCopy, layout.Owner?.AssemblyName ?? assemblyName)
                {
                    DropClrName = layout.DropFunctionId, DropFunction = ReadDrop(layout), Kind = ExternalKind(layout.ConstructorKind),
                    Owner = layout.Owner is { } owner ? new(owner.AssemblyName, owner.SourceName, owner.ClrName, owner.ModuleVersionId,
                        owner.SourceSha256, owner.AssemblySha256) : new(assemblyName, layout.Name, layout.ClrName, moduleVersionId, imported.Document.SourceSha256, producerSha256),
                    NominalScope = layout.Owner is { } originalOwner ? OwnerScope(originalOwner) : scopePath,
                    NominalSourceName = layout.Owner?.SourceName ?? layout.Name,
                    Variants = (layout.Variants ?? []).Select(variant => new SafeCoreExternalVariant(variant.Name, variant.Discriminant,
                        variant.FieldOffset, variant.Fields.Select(field => new SafeCoreExternalField(field.Name, field.Type, field.IsPublic)
                        { RequiresStaticLifetime = field.RequiresStaticLifetime }).ToImmutableArray())
                        { Kind = ExternalKind(variant.ConstructorKind) }).ToImmutableArray(),
                }).ToImmutableArray();
            foreach (RustSharpMetadataFunction function in imported.Document.Functions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (imported.Document.SourceValueTypes.Any(layout => layout.Owner is null &&
                    layout.DropFunctionId is { } drop && RustSharpMetadataConsumer.FindFunction(imported.Document, drop)?.Name == function.Name))
                    continue;
                // MIR-backed producers retain an internal `#value` suffix on
                // their emitted source identity.  That suffix distinguishes
                // the lowered body inside the producer, but it is not a Rust
                // path component and therefore cannot be exposed as an
                // external source symbol. Keep the CLR method name and
                // metadata contract lookup intact while presenting the
                // source alias that consumers can actually import.
                string sourceName = function.SourceQualifiedName ?? function.Name;
                if (sourceName.EndsWith("#value", StringComparison.Ordinal))
                    sourceName = sourceName[..^"#value".Length];
                RustSharpMetadataCallContract? functionContract = imported.Document.CallContracts.FirstOrDefault(contract =>
                    RustSharpMetadataConsumer.FindFunction(imported.Document, contract.FunctionId)?.Name == function.Name);
                exports.Add(new SafeCoreExternalFunction(
                    sourceName,
                    function.Name,
                    function.Signature,
                    assemblyName,
                    fullPath,
                    function.IsPublic)
                {
                    CallPanicStrategy = functionContract?.PanicStrategy,
                    CallParameterContracts = (functionContract?.ParameterContracts ?? []).ToImmutableArray(),
                    CallReturnContract = functionContract?.ReturnContract,
                    SourceSchema = functionContract?.Schema == RustSharpMetadataCallContract.SourceSchema
                        ? RustSharpMetadataCallContract.SourceSchema : null,
                    SourceParameterTypes = (functionContract?.SourceParameterTypes ?? []).ToImmutableArray(),
                    SourceParameterStaticLifetimes = (functionContract?.SourceParameterStaticLifetimes ?? []).ToImmutableArray(),
                    SourceReturnType = functionContract?.SourceReturnType,
                    ReturnOrigins = (functionContract?.ReturnOrigins ?? []).ToImmutableArray(),
                    SourceReturnVariants = (functionContract?.SourceReturnVariants ?? []).Select(variant =>
                        new SafeCoreExternalReturnedVariant(variant.ValuePath.ToImmutableArray(), variant.VariantName)).ToImmutableArray(),
                    NominalScope = scopePath,
                    StructuralTypes = structuralTypes,
                    SourceValueTypes = sourceLayouts,
                });
            }

            SafeCoreExternalFunction? ReadDrop(RustSharpMetadataSourceValueType layout)
            {
                if (layout.DropFunctionId is null) return null;
                RustSharpMetadataDocument ownerDocument = OwnerDocument(layout.Owner);
                RustSharpMetadataFunction drop = RustSharpMetadataConsumer.FindFunction(ownerDocument, layout.DropFunctionId)
                    ?? throw new InvalidDataException("Imported destructor identity is missing.");
                RustSharpMetadataCallContract? contract = ownerDocument.CallContracts.FirstOrDefault(value =>
                    RustSharpMetadataConsumer.FindFunction(ownerDocument, value.FunctionId)?.Name == drop.Name);
                return new(drop.SourceQualifiedName ?? drop.Name, drop.Name, drop.Signature, layout.Owner?.AssemblyName ?? assemblyName,
                    layout.Owner is { } sourceOwner ? imported.ResolvedOwnerPaths[sourceOwner.AssemblyName] : fullPath, drop.IsPublic)
                {
                    SourceSchema = contract?.Schema, SourceParameterTypes = (contract?.SourceParameterTypes ?? []).ToImmutableArray(),
                    SourceParameterStaticLifetimes = (contract?.SourceParameterStaticLifetimes ?? []).ToImmutableArray(),
                    SourceReturnType = contract?.SourceReturnType, ReturnOrigins = (contract?.ReturnOrigins ?? []).ToImmutableArray(),
                    SourceReturnVariants = (contract?.SourceReturnVariants ?? []).Select(variant =>
                        new SafeCoreExternalReturnedVariant(variant.ValuePath.ToImmutableArray(), variant.VariantName)).ToImmutableArray(),
                    CallPanicStrategy = contract?.PanicStrategy, CallParameterContracts = (contract?.ParameterContracts ?? []).ToImmutableArray(),
                    CallReturnContract = contract?.ReturnContract, NominalScope = layout.Owner is { } sourceOwnerScope ? OwnerScope(sourceOwnerScope) : scopePath,
                };
            }

            crates.Add(new SafeCoreCrate(scopePath, identity,
                ImmutableDictionary<string, string>.Empty)
            {
                Exports = exports.ToImmutable(),
                StructuralTypes = structuralTypes,
                TypeExports = imported.Document.SourceValueTypes.Select((layout, index) =>
                    new SafeCoreExternalType(layout.Name, sourceLayouts[index], fullPath, scopePath, layout.IsPublic)
                    {
                        Kind = ExternalKind(layout.ConstructorKind),
                    }).ToImmutableArray(),
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

        static SafeCoreExternalTypeKind ExternalKind(string kind) => kind switch
        {
            "enum" => SafeCoreExternalTypeKind.Enum,
            "tuple" => SafeCoreExternalTypeKind.TupleStruct,
            "unit" => SafeCoreExternalTypeKind.UnitStruct,
            _ => SafeCoreExternalTypeKind.NamedStruct,
        };

        static string OwnerScope(RustSharpMetadataSourceOwner owner) => "crate::__rsc_ext_" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("metadata:" + owner.AssemblyName + "@" + owner.ModuleVersionId.ToString("D"))))[..32];

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

    private static ImmutableArray<SafeCoreExternalStructuralType> ReadStructuralTypes(RustSharpMetadataDocument document,
        string assemblyName, string scopePath, Guid moduleVersionId, string assemblySha256,
        Func<RustSharpMetadataSourceOwner?, RustSharpMetadataDocument> ownerDocument, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SafeCoreExternalStructuralType>(StringComparer.Ordinal);
        var visited = new HashSet<(string Source, string Clr)>();
        var clock = Stopwatch.StartNew();
        int operations = 0;
        foreach (RustSharpMetadataCallContract contract in document.CallContracts)
        {
            if (contract.Schema != RustSharpMetadataCallContract.SourceSchema) continue;
            RustSharpMetadataFunction function = RustSharpMetadataConsumer.FindFunction(document, contract.FunctionId)!;
            string[] signature = function.Signature.Split("->", StringSplitOptions.None);
            string[] parameters = signature[0].Length == 0 ? [] : signature[0].Split(',');
            string[] sourceParameters = contract.SourceParameterTypes!.ToArray();
            for (int index = 0; index < sourceParameters.Length; index++)
                Bind(SafeCoreSourceTypeCodec.Parse(sourceParameters[index], cancellationToken), parameters[index], 0);
            Bind(SafeCoreSourceTypeCodec.Parse(contract.SourceReturnType!, cancellationToken), signature[1], 0);
        }
        foreach (RustSharpMetadataSourceValueType layout in document.SourceValueTypes)
            Bind(SafeCoreType.Adt(layout.Name, cancellationToken: cancellationToken), "Value(" + layout.ClrName + ")", 0);
        foreach (RustSharpMetadataSourceStructuralType structural in document.SourceStructuralTypes)
            Bind(SafeCoreSourceTypeCodec.Parse(structural.Type, cancellationToken), "Value(" + structural.ClrName + ")", 0);
        return result.Values.OrderBy(static value => value.SourceType, StringComparer.Ordinal).ToImmutableArray();

        void Bind(SafeCoreType source, string clr, int depth, RustSharpMetadataDocument? clrDocument = null, string? clrAssembly = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++operations > 65536 || depth > 32 || clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidDataException("Imported structural ABI reconstruction exceeded its budget.");
            if (source.Kind is SafeCoreSemanticTypeKind.Reference or SafeCoreSemanticTypeKind.Slice)
            {
                Bind(source.ElementType!, "", depth + 1, clrDocument, clrAssembly);
                return;
            }
            string sourceIdentity = SafeCoreSourceTypeCodec.Format(source, cancellationToken);
            RustSharpMetadataSourceValueType? sourceLayout = source.Kind == SafeCoreSemanticTypeKind.Adt
                ? document.SourceValueTypes.FirstOrDefault(value => value.Name == source.Name) : null;
            if (!clr.StartsWith("Value(", StringComparison.Ordinal) || !clr.EndsWith(')'))
            {
                RustSharpMetadataSourceStructuralType? explicitBinding = document.SourceStructuralTypes.FirstOrDefault(value => value.Type == sourceIdentity);
                if (explicitBinding is not null) clr = "Value(" + explicitBinding.ClrName + ")";
                else if (sourceLayout is not null) clr = "Value(" + sourceLayout.ClrName + ")";
                else if (source.Kind is SafeCoreSemanticTypeKind.Tuple or SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Unit)
                {
                    string generated = "mir_value_" + Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(source.Kind + "|" + source))).ToLowerInvariant()[..24];
                    if (!(clrDocument ?? document).ValueTypes.Any(value => value.Name == generated)) return;
                    clr = "Value(" + generated + ")";
                }
                else return;
            }
            string name = clr[6..^1];
            RustSharpMetadataDocument actualDocument = sourceLayout?.Owner is { } verifiedOwner ? ownerDocument(verifiedOwner) : clrDocument ?? document;
            string actualAssembly = sourceLayout?.Owner?.AssemblyName ?? clrAssembly ?? assemblyName;
            string actualName = sourceLayout?.Owner?.ClrName ?? name;
            int ownerSeparator = actualName.IndexOf("::", StringComparison.Ordinal);
            if (ownerSeparator > 0)
            {
                actualAssembly = actualName[..ownerSeparator];
                actualName = actualName[(ownerSeparator + 2)..];
            }
            RustSharpMetadataSourceOwner? structuralOwner = null;
            if (source.Kind is SafeCoreSemanticTypeKind.Tuple or SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Unit)
            {
                RustSharpMetadataSourceStructuralType? binding = document.SourceStructuralTypes.FirstOrDefault(value =>
                    value.Type == sourceIdentity && value.ClrName == actualAssembly + "::" + actualName);
                structuralOwner = binding?.Owner;
                if (structuralOwner is not null) actualDocument = ownerDocument(structuralOwner);
                else if (actualAssembly != assemblyName)
                    throw new InvalidDataException("Imported anonymous structural ABI has no independently verified original owner binding.");
            }
            if (!visited.Add((source.ToString(), actualAssembly + "::" + actualName))) return;
            RustSharpMetadataValueType? layout = actualDocument.ValueTypes.FirstOrDefault(value => value.Name == actualName);
            if (layout is null) throw new InvalidDataException("Imported structural ABI has no checked CLR layout.");
            RustSharpMetadataField[] fields = (layout.Fields ?? []).ToArray();
            if (source.Kind is SafeCoreSemanticTypeKind.Tuple or SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Unit)
            {
                if (source.Kind == SafeCoreSemanticTypeKind.Array &&
                    source.Length is not (>= 0 and <= RustSharpMetadataDocument.MaximumFieldsPerValueType))
                    throw new InvalidDataException("Imported anonymous array exceeds its source layout field budget.");
                int expectedFields = source.Kind == SafeCoreSemanticTypeKind.Array ? (int)source.Length!.Value : source.Elements.Count;
                if (fields.Length != expectedFields || fields.Length > RustSharpMetadataDocument.MaximumFieldsPerValueType)
                    throw new InvalidDataException("Imported anonymous structural ABI contradicts its exact source field count.");
                SafeCoreExternalOwner proof = structuralOwner is { } original ? new SafeCoreExternalOwner(original.AssemblyName, original.SourceName,
                    original.ClrName, original.ModuleVersionId, original.SourceSha256, original.AssemblySha256) :
                    new(actualAssembly, sourceIdentity, actualName, moduleVersionId, document.SourceSha256, assemblySha256);
                var importedType = new SafeCoreExternalStructuralType(sourceIdentity, actualAssembly, actualName, scopePath) { Owner = proof };
                if (result.TryGetValue(sourceIdentity, out SafeCoreExternalStructuralType? previous) &&
                    (previous.AssemblyName != actualAssembly || previous.ClrName != actualName || previous.Owner != proof))
                    throw new InvalidDataException("One anonymous source shape has conflicting CLR owners or original producer proofs.");
                result[sourceIdentity] = importedType;
                for (int index = 0; index < fields.Length; index++)
                    Bind(source.Kind == SafeCoreSemanticTypeKind.Array ? source.ElementType : source.Elements[index], fields[index].Type, depth + 1, actualDocument, actualAssembly);
            }
            else if (source.Kind == SafeCoreSemanticTypeKind.Adt)
            {
                RustSharpMetadataSourceField[] sourceFields = (sourceLayout ?? throw new InvalidDataException("Imported nominal structural ABI has no source layout.")).Fields.ToArray();
                if (sourceFields.Length != fields.Length)
                    throw new InvalidDataException("Imported nominal structural ABI contradicts its source field count.");
                for (int index = 0; index < fields.Length; index++)
                    Bind(SafeCoreSourceTypeCodec.Parse(sourceFields[index].Type, cancellationToken), fields[index].Type, depth + 1, actualDocument, actualAssembly);
            }
        }
    }

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
