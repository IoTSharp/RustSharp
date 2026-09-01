using RustSharp.Syntax;

namespace RustSharp.Compiler;

public sealed record CompilationResult(
    bool Success,
    IReadOnlyList<Diagnostic> Diagnostics,
    CompilationOutput? Output)
{
    public static CompilationResult Failed(IReadOnlyList<Diagnostic> diagnostics) =>
        new(false, diagnostics, null);
}
