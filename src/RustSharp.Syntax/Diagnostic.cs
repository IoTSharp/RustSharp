namespace RustSharp.Syntax;

public sealed record Diagnostic(string Code, string Message, TextSpan Span)
{
    /// <summary>The originating file when a compilation contains multiple source documents.</summary>
    public string? SourcePath { get; init; }
}
