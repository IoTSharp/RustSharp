namespace RustSharp.Syntax;

/// <summary>Bounds and check behavior for one lossless formatting operation.</summary>
public sealed record FormatterOptions
{
    public bool CheckOnly { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumSourceLength { get; init; } = 1_000_000;
    public int MaximumTokens { get; init; } = 250_000;
    public int MaximumTrivia { get; init; } = 500_000;
    /// <summary>Maximum token/trivia entries visited by the layout pass; parser work is bounded separately.</summary>
    public int MaximumOperations { get; init; } = 1_000_000;
    public int MaximumOutputBytes { get; init; } = 16_777_216;
}

/// <summary>A verified formatted document, or the unchanged original on failure.</summary>
public sealed record FormatterResult(
    bool Success,
    bool Changed,
    string FormattedSource,
    IReadOnlyList<Diagnostic> Diagnostics);

public static class FormatterDiagnosticCodes
{
    public const string LimitReached = "RSTF0001";
    public const string PreservationFailed = "RSTF1001";
    public const string FormattingDrift = "RSTF1002";
}
