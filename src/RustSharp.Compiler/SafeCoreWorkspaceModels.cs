using RustSharp.Syntax;

namespace RustSharp.Compiler;

/// <summary>Budgets for a workspace rooted at the entry file's containing directory.</summary>
public sealed record SafeCoreWorkspaceOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumFiles { get; init; } = 128;
    public int MaximumFileBytes { get; init; } = 4_000_000;
    public int MaximumTotalBytes { get; init; } = 8_000_000;
    public int MaximumExpandedLength { get; init; } = 1_000_000;
    public int MaximumModuleDepth { get; init; } = 64;
    public int MaximumOperations { get; init; } = 1_000_000;
}

/// <summary>Stable workspace loading failures. Syntax diagnostics retain their original codes.</summary>
public static class SafeCoreWorkspaceDiagnosticCodes
{
    public const string LimitReached = "RSM0002";
    public const string MissingModule = "RSM1001";
    public const string AmbiguousModule = "RSM1002";
    public const string InvalidSource = "RSM1003";
    public const string UnsafePath = "RSM1004";
}

/// <summary>An expanded source document, or diagnostics with no partial expansion.</summary>
public sealed class SafeCoreWorkspaceResult
{
    internal SafeCoreWorkspaceResult(string sourceText, SafeCoreSourceMap? sourceMap, IReadOnlyList<Diagnostic> diagnostics)
    {
        SourceText = sourceText;
        SourceMap = sourceMap;
        Diagnostics = diagnostics;
    }

    public string SourceText { get; }
    public SafeCoreSourceMap? SourceMap { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool IsSuccessful => SourceMap is not null && Diagnostics.Count == 0;
}
