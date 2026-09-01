namespace RustSharp.Syntax;

public sealed record Diagnostic(string Code, string Message, TextSpan Span);
