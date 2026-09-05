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
    RustSharp.Compiler.CompilationProfile Profile = RustSharp.Compiler.CompilationProfile.VerticalSlice);

internal sealed record CommandLineParseResult(
    CommandLineOptions? Options,
    string? Error)
{
    public bool Success => Options is not null;
}
