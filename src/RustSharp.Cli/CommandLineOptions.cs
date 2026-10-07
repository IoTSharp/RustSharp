namespace RustSharp.Cli;

internal enum CommandKind
{
    Help,
    Version,
    Check,
    Compile,
    Run,
    Publish,
}

internal sealed record CommandLineOptions(
    CommandKind Command,
    string? SourcePath = null,
    string? OutputPath = null,
    string? RuntimeIdentifier = null,
    int TimeoutSeconds = 600,
    RustSharp.Compiler.CompilationProfile Profile = RustSharp.Compiler.CompilationProfile.VerticalSlice,
    IReadOnlyList<string>? MetadataReferences = null,
    IReadOnlyList<string>? RequiredFunctions = null,
    RustSharp.CodeGen.IL.SafeCoreDropCleanupProfile DropCleanupProfile = RustSharp.CodeGen.IL.SafeCoreDropCleanupProfile.LegacyV1);

internal sealed record CommandLineParseResult(
    CommandLineOptions? Options,
    string? Error)
{
    public bool Success => Options is not null;
}
