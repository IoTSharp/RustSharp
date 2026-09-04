namespace RustSharp.Syntax;

/// <summary>Classifies the lexical form of a Rust token.</summary>
public enum RustTokenKind
{
    /// <summary>A regular Unicode Rust identifier.</summary>
    Identifier,

    /// <summary>An identifier written with the <c>r#</c> raw-identifier prefix.</summary>
    RawIdentifier,

    /// <summary>A Rust keyword. The original spelling remains in <see cref="RustToken.Text"/>.</summary>
    Keyword,

    /// <summary>A lifetime name, including its leading apostrophe.</summary>
    Lifetime,

    /// <summary>An integer literal.</summary>
    IntegerLiteral,

    /// <summary>A floating-point literal.</summary>
    FloatLiteral,

    /// <summary>A regular UTF-8 string literal.</summary>
    StringLiteral,

    /// <summary>A raw string literal.</summary>
    RawStringLiteral,

    /// <summary>A byte string literal.</summary>
    ByteStringLiteral,

    /// <summary>A raw byte string literal.</summary>
    RawByteStringLiteral,

    /// <summary>A C string literal.</summary>
    CStringLiteral,

    /// <summary>A raw C string literal.</summary>
    RawCStringLiteral,

    /// <summary>A character literal.</summary>
    CharacterLiteral,

    /// <summary>A byte character literal.</summary>
    ByteCharacterLiteral,

    /// <summary>An opening delimiter such as <c>(</c>, <c>[</c>, or <c>{</c>.</summary>
    OpenDelimiter,

    /// <summary>A closing delimiter such as <c>)</c>, <c>]</c>, or <c>}</c>.</summary>
    CloseDelimiter,

    /// <summary>A punctuation or operator token.</summary>
    Punctuation,

    /// <summary>A source character that is not part of the Rust lexical profile.</summary>
    Unknown,
}

/// <summary>Classifies source text that is preserved outside lexical tokens.</summary>
public enum RustTriviaKind
{
    /// <summary>Whitespace, including line endings.</summary>
    Whitespace,

    /// <summary>A <c>//</c> line comment.</summary>
    LineComment,

    /// <summary>A possibly nested <c>/* ... */</c> block comment.</summary>
    BlockComment,

    /// <summary>A file-level shebang line beginning with <c>#!</c>.</summary>
    Shebang,
}

/// <summary>Identifies one of Rust's three grouping delimiters.</summary>
public enum RustDelimiterKind
{
    /// <summary>Parentheses.</summary>
    Parenthesis,

    /// <summary>Square brackets.</summary>
    Bracket,

    /// <summary>Braces.</summary>
    Brace,
}

/// <summary>Stable diagnostic identifiers emitted by <see cref="RustLexer"/>.</summary>
public static class RustLexDiagnosticCodes
{
    /// <summary>The source exceeded the configured source-length limit.</summary>
    public const string SourceTooLong = "RSL0001";

    /// <summary>The lexer stopped after reaching a configured work limit.</summary>
    public const string LimitReached = "RSL0002";

    /// <summary>The source contains an unrecognized character.</summary>
    public const string UnknownCharacter = "RSL1001";

    /// <summary>A closing delimiter has no matching opening delimiter.</summary>
    public const string UnmatchedClosingDelimiter = "RSL1002";

    /// <summary>An opening delimiter was not closed.</summary>
    public const string UnterminatedDelimiter = "RSL1003";

    /// <summary>Opening and closing delimiters do not have matching kinds.</summary>
    public const string MismatchedDelimiter = "RSL1004";

    /// <summary>A quoted or raw literal was not terminated.</summary>
    public const string UnterminatedLiteral = "RSL1005";

    /// <summary>A nested block comment was not terminated.</summary>
    public const string UnterminatedComment = "RSL1006";

    /// <summary>A numeric literal contains an invalid digit or suffix.</summary>
    public const string InvalidNumber = "RSL1007";

    /// <summary>A quoted literal contains an invalid lexical form.</summary>
    public const string InvalidLiteral = "RSL1008";

    /// <summary>The configured delimiter nesting limit was exceeded.</summary>
    public const string DelimiterDepthLimit = "RSL1009";
}

/// <summary>Options that bound a <see cref="RustLexer"/> invocation.</summary>
public sealed record RustLexerOptions
{
    /// <summary>Maximum source characters inspected by one invocation.</summary>
    public int MaximumSourceLength { get; init; } = RustLexer.DefaultMaximumSourceLength;

    /// <summary>Maximum non-trivia tokens retained by one invocation.</summary>
    public int MaximumTokens { get; init; } = RustLexer.DefaultMaximumTokens;

    /// <summary>Maximum trivia entries retained by one invocation.</summary>
    public int MaximumTrivia { get; init; } = RustLexer.DefaultMaximumTrivia;

    /// <summary>Maximum diagnostics retained by one invocation.</summary>
    public int MaximumDiagnostics { get; init; } = RustLexer.DefaultMaximumDiagnostics;

    /// <summary>Maximum delimiter nesting depth represented in the token tree.</summary>
    public int MaximumDelimiterDepth { get; init; } = RustLexer.DefaultMaximumDelimiterDepth;
}

/// <summary>A lossless trivia entry with its exact source span and text.</summary>
public readonly record struct RustTrivia(
    RustTriviaKind Kind,
    TextSpan Span,
    string Text,
    bool IsDocumentation = false)
{
    /// <summary>Gets the original source spelling of this trivia entry.</summary>
    public string RawText => Text;
}

/// <summary>A lossless non-trivia token with exact source spelling and span.</summary>
public sealed record RustToken
{
    /// <summary>Creates a token without attached leading trivia.</summary>
    public RustToken(RustTokenKind kind, TextSpan span, string text)
        : this(kind, span, text, false, null, Array.Empty<RustTrivia>())
    {
    }

    /// <summary>Creates a token and optionally associates leading trivia.</summary>
    public RustToken(
        RustTokenKind kind,
        TextSpan span,
        string text,
        bool isKeyword,
        RustDelimiterKind? delimiter,
        IReadOnlyList<RustTrivia>? leadingTrivia)
    {
        ArgumentNullException.ThrowIfNull(text);
        Kind = kind;
        Span = span;
        Text = text;
        IsKeyword = isKeyword || kind == RustTokenKind.Keyword;
        Delimiter = delimiter;
        LeadingTrivia = leadingTrivia ?? Array.Empty<RustTrivia>();
    }

    /// <summary>The lexical category.</summary>
    public RustTokenKind Kind { get; }

    /// <summary>The UTF-16 source span occupied by this token.</summary>
    public TextSpan Span { get; }

    /// <summary>The exact source spelling, including prefixes and escapes.</summary>
    public string Text { get; }

    /// <summary>An alias for <see cref="Text"/> useful to source-preserving consumers.</summary>
    public string RawText => Text;

    /// <summary>Whether this token's text is a recognized Rust keyword.</summary>
    public bool IsKeyword { get; }

    /// <summary>The delimiter kind for an opening or closing delimiter token.</summary>
    public RustDelimiterKind? Delimiter { get; }

    /// <summary>Whether this token opens or closes a delimiter group.</summary>
    public bool IsDelimiter => Kind is RustTokenKind.OpenDelimiter or RustTokenKind.CloseDelimiter;

    /// <summary>Trivia immediately preceding this token.</summary>
    public IReadOnlyList<RustTrivia> LeadingTrivia { get; }

    /// <summary>Gets the token text as a source value without decoding escapes.</summary>
    public string Value => Text;
}

/// <summary>Base type for a leaf or delimiter group in a lossless token tree.</summary>
public abstract record RustTokenTree
{
    /// <summary>The UTF-16 span covered by this tree node.</summary>
    public abstract TextSpan Span { get; }

    /// <summary>Whether this node is a delimiter group.</summary>
    public abstract bool IsDelimited { get; }

    /// <summary>Child nodes; leaves return an empty list.</summary>
    public abstract IReadOnlyList<RustTokenTree> Children { get; }

    /// <summary>The leaf token, or <see langword="null"/> for a group.</summary>
    public virtual RustToken? Token => null;
}

/// <summary>A leaf token in a <see cref="RustTokenTree"/>.</summary>
public sealed record RustLeafTokenTree : RustTokenTree
{
    /// <summary>Creates a leaf node.</summary>
    public RustLeafTokenTree(RustToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        TokenValue = token;
    }

    /// <summary>The token represented by this leaf.</summary>
    public RustToken TokenValue { get; }

    /// <summary>Alias for <see cref="TokenValue"/>.</summary>
    public override RustToken Token => TokenValue;

    /// <inheritdoc />
    public override TextSpan Span => TokenValue.Span;

    /// <inheritdoc />
    public override bool IsDelimited => false;

    /// <inheritdoc />
    public override IReadOnlyList<RustTokenTree> Children { get; } = Array.Empty<RustTokenTree>();
}

/// <summary>A delimiter group and its recursively grouped child tokens.</summary>
public sealed record RustDelimitedTokenTree : RustTokenTree
{
    /// <summary>Creates a delimiter group.</summary>
    public RustDelimitedTokenTree(
        RustDelimiterKind delimiter,
        RustToken openToken,
        IReadOnlyList<RustTokenTree> children,
        RustToken? closeToken,
        TextSpan span)
    {
        ArgumentNullException.ThrowIfNull(openToken);
        ArgumentNullException.ThrowIfNull(children);
        Delimiter = delimiter;
        OpenToken = openToken;
        Children = children;
        CloseToken = closeToken;
        Span = span;
    }

    /// <summary>The delimiter kind.</summary>
    public RustDelimiterKind Delimiter { get; }

    /// <summary>The opening delimiter token.</summary>
    public RustToken OpenToken { get; }

    /// <summary>The closing delimiter token, or <see langword="null"/> when missing.</summary>
    public RustToken? CloseToken { get; }

    /// <summary>Whether a matching closing token was found.</summary>
    public bool IsClosed => CloseToken is not null;

    /// <inheritdoc />
    public override TextSpan Span { get; }

    /// <inheritdoc />
    public override bool IsDelimited => true;

    /// <inheritdoc />
    public override IReadOnlyList<RustTokenTree> Children { get; }
}

/// <summary>The complete result of one bounded, source-preserving lexing pass.</summary>
public sealed class RustLexResult
{
    internal RustLexResult(
        string source,
        string sourcePath,
        IReadOnlyList<RustToken> tokens,
        IReadOnlyList<RustTrivia> trivia,
        IReadOnlyList<RustTrivia> trailingTrivia,
        IReadOnlyList<RustTokenTree> tokenTrees,
        IReadOnlyList<Diagnostic> diagnostics,
        bool isTruncated)
    {
        Source = source;
        SourcePath = sourcePath;
        Tokens = tokens;
        Trivia = trivia;
        TrailingTrivia = trailingTrivia;
        TokenTrees = tokenTrees;
        Diagnostics = diagnostics;
        IsTruncated = isTruncated;
    }

    /// <summary>The complete original source string supplied to the lexer.</summary>
    public string Source { get; }

    /// <summary>The optional source path supplied by the caller.</summary>
    public string SourcePath { get; }

    /// <summary>All non-trivia tokens in source order.</summary>
    public IReadOnlyList<RustToken> Tokens { get; }

    /// <summary>Alias for <see cref="Tokens"/>.</summary>
    public IReadOnlyList<RustToken> FlatTokens => Tokens;

    /// <summary>All whitespace and comments in source order.</summary>
    public IReadOnlyList<RustTrivia> Trivia { get; }

    /// <summary>Trivia after the final token.</summary>
    public IReadOnlyList<RustTrivia> TrailingTrivia { get; }

    /// <summary>Top-level delimiter groups and leaf tokens.</summary>
    public IReadOnlyList<RustTokenTree> TokenTrees { get; }

    /// <summary>Alias for <see cref="TokenTrees"/>.</summary>
    public IReadOnlyList<RustTokenTree> Trees => TokenTrees;

    /// <summary>All stable lexical and grouping diagnostics.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Whether a source or work limit prevented a complete pass.</summary>
    public bool IsTruncated { get; }

    /// <summary>Whether no diagnostics were emitted and the pass was complete.</summary>
    public bool IsSuccessful => !IsTruncated && Diagnostics.Count == 0;

    /// <summary>Returns the exact source text covered by a span.</summary>
    public string GetText(TextSpan span)
    {
        if (span.Start < 0 || span.Length < 0 || span.Start > Source.Length - span.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(span));
        }

        return Source.Substring(span.Start, span.Length);
    }

    /// <summary>Returns the original source, retaining every token and trivia character.</summary>
    public string ToSourceText() => Source;
}
