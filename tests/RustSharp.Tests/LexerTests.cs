using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class LexerTests
{
    private static readonly string[] UppercaseRadixPrefixTexts = ["0XFF", "0O77", "0B11"];
    private static readonly string[] OutOfRangeHexEscapeTexts = ["\\x80", "\\xFF"];
    private static readonly string[] AdjacentLifetimeTexts = ["'a", "'b", "'a", "'b"];
    private const int MaximumRawStringHashCountForTest = 255;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Lexer preserves token and trivia spans", PreservesSourceSpansAsync),
        new("Lexer recognizes Rust lexical forms", RecognizesLexicalFormsAsync),
        new("Lexer builds nested delimiter token trees", BuildsTokenTreesAsync),
        new("Lexer reports stable malformed-source diagnostics", ReportsDiagnosticsAsync),
        new("Lexer obeys explicit work limits", ObeysWorkLimitsAsync),
    ];

    private static Task PreservesSourceSpansAsync()
    {
        const string source = "  // note\r\nfn main() { let pi = \u03c0; }  ";
        RustLexResult result = RustLexer.Lex(source, "spans.rs");

        AssertEx.Equal("spans.rs", result.SourcePath);
        AssertEx.Equal(source, result.ToSourceText());
        AssertEx.True(
            result.Diagnostics.Count == 0,
            string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message} [{diagnostic.Span.Start},{diagnostic.Span.Length}]")));
        AssertEx.True(result.Trivia.Count >= 3, "Whitespace and comment trivia must be retained.");

        foreach (RustTrivia trivia in result.Trivia)
        {
            AssertEx.Equal(source.Substring(trivia.Span.Start, trivia.Span.Length), trivia.Text);
        }

        foreach (RustToken token in result.Tokens)
        {
            AssertEx.Equal(source.Substring(token.Span.Start, token.Span.Length), token.Text);
            foreach (RustTrivia leading in token.LeadingTrivia)
            {
                AssertEx.True(result.Trivia.Contains(leading), "Leading trivia must also be present globally.");
            }
        }

        RustToken function = FindToken(result, "fn");
        AssertEx.Equal(RustTokenKind.Keyword, function.Kind);
        AssertEx.True(function.IsKeyword, "Keyword text must remain available on the token.");
        AssertEx.Equal("fn", function.RawText);

        const string triviaSource =
            "#!/usr/bin/env rsc\n" +
            "//// ordinary line\n" +
            "/// outer docs\n" +
            "//! inner docs\n" +
            "/*** ordinary block */\n" +
            "/** outer block docs */\n" +
            "/*! inner block docs */\n" +
            "fn main() {}\n";
        RustLexResult triviaResult = RustLexer.Lex(triviaSource, "trivia.rs");
        AssertEx.Equal(0, triviaResult.Diagnostics.Count);
        AssertEx.False(
            triviaResult.Trivia.Single(item => item.Kind == RustTriviaKind.Shebang).IsDocumentation,
            "A shebang is trivia, not a documentation comment.");
        AssertEx.False(
            triviaResult.Trivia.Single(item => item.Text.StartsWith("////", StringComparison.Ordinal)).IsDocumentation,
            "Four-slash comments are not outer documentation comments.");
        AssertEx.True(
            triviaResult.Trivia.Single(item => item.Text.StartsWith("/// outer", StringComparison.Ordinal)).IsDocumentation,
            "Three-slash comments are outer documentation comments.");
        AssertEx.True(
            triviaResult.Trivia.Single(item => item.Text.StartsWith("//!", StringComparison.Ordinal)).IsDocumentation,
            "Bang line comments are inner documentation comments.");
        AssertEx.False(
            triviaResult.Trivia.Single(item => item.Text.StartsWith("/***", StringComparison.Ordinal)).IsDocumentation,
            "Three-star block comments are not documentation comments.");
        AssertEx.True(
            triviaResult.Trivia.Single(item => item.Text.StartsWith("/** outer", StringComparison.Ordinal)).IsDocumentation,
            "Two-star block comments are outer documentation comments.");
        AssertEx.True(
            triviaResult.Trivia.Single(item => item.Text.StartsWith("/*!", StringComparison.Ordinal)).IsDocumentation,
            "Bang block comments are inner documentation comments.");
        AssertEx.Equal("\n", triviaResult.TrailingTrivia.Single().Text);

        const string patternWhitespace = "\u0009\u000A\u000B\u000C\u000D\u0020\u0085\u200E\u200F\u2028\u2029";
        RustLexResult patternWhitespaceResult = RustLexer.Lex("alpha" + patternWhitespace + "omega");
        AssertEx.Equal(0, patternWhitespaceResult.Diagnostics.Count);
        AssertEx.Equal(patternWhitespace, patternWhitespaceResult.Trivia.Single().Text);
        return Task.CompletedTask;
    }

    private static Task RecognizesLexicalFormsAsync()
    {
        const string source =
            "r#type _name \u03c0 'a' 'static 42 1_u8 0xff_u16 0b1010 1.25e+2 " +
            "\"text\\n\" b\"bytes\" c\"cstr\" r###\"raw ) text\"### " +
            "br##\"raw bytes\"## cr\"raw c\" b'\\x41'";
        RustLexResult result = RustLexer.Lex(source);

        AssertEx.True(
            result.Diagnostics.Count == 0,
            string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message} [{diagnostic.Span.Start},{diagnostic.Span.Length}]")));
        AssertEx.Equal(RustTokenKind.RawIdentifier, FindToken(result, "r#type").Kind);
        AssertEx.Equal(RustTokenKind.Identifier, FindToken(result, "_name").Kind);
        AssertEx.Equal(RustTokenKind.Identifier, FindToken(result, "\u03c0").Kind);
        AssertEx.Equal(RustTokenKind.CharacterLiteral, FindToken(result, "'a'").Kind);
        AssertEx.Equal(RustTokenKind.Lifetime, FindToken(result, "'static").Kind);
        AssertEx.Equal(RustTokenKind.IntegerLiteral, FindToken(result, "1_u8").Kind);
        AssertEx.Equal(RustTokenKind.IntegerLiteral, FindToken(result, "0xff_u16").Kind);
        AssertEx.Equal(RustTokenKind.FloatLiteral, FindToken(result, "1.25e+2").Kind);
        AssertEx.Equal(RustTokenKind.StringLiteral, FindToken(result, "\"text\\n\"").Kind);
        AssertEx.Equal(RustTokenKind.ByteStringLiteral, FindToken(result, "b\"bytes\"").Kind);
        AssertEx.Equal(RustTokenKind.CStringLiteral, FindToken(result, "c\"cstr\"").Kind);
        AssertEx.Equal(RustTokenKind.RawStringLiteral, FindToken(result, "r###\"raw ) text\"###").Kind);
        AssertEx.Equal(RustTokenKind.RawByteStringLiteral, FindToken(result, "br##\"raw bytes\"##").Kind);
        AssertEx.Equal(RustTokenKind.RawCStringLiteral, FindToken(result, "cr\"raw c\"").Kind);
        AssertEx.Equal(RustTokenKind.ByteCharacterLiteral, FindToken(result, "b'\\x41'").Kind);

        RustLexResult adjacentLifetimes = RustLexer.Lex("fn f<'a,'b,T>() where T:'a+'b {}");
        AssertEx.Equal(0, adjacentLifetimes.Diagnostics.Count);
        AssertEx.True(
            adjacentLifetimes.Tokens
                .Where(token => token.Kind == RustTokenKind.Lifetime)
                .Select(token => token.Text)
                .SequenceEqual(AdjacentLifetimeTexts),
            "A plus sign between adjacent lifetimes must not turn them into a character literal.");

        RustLexResult restrictedRawTokens = RustLexer.Lex("r#crate r#self r#super r#Self r#_");
        AssertEx.Equal(0, restrictedRawTokens.Diagnostics.Count);
        AssertEx.Equal(5, restrictedRawTokens.Tokens.Count);
        AssertEx.True(
            restrictedRawTokens.Tokens.All(token => token.Kind == RustTokenKind.RawIdentifier),
            "Restricted raw spellings remain raw-identifier tokens for downstream validation.");

        RustLexResult hexEscapeBoundaries = RustLexer.Lex("\"\\x7F\" b\"\\x80\\xFF\"");
        AssertEx.Equal(0, hexEscapeBoundaries.Diagnostics.Count);
        AssertEx.Equal(RustTokenKind.StringLiteral, FindToken(hexEscapeBoundaries, "\"\\x7F\"").Kind);
        AssertEx.Equal(RustTokenKind.ByteStringLiteral, FindToken(hexEscapeBoundaries, "b\"\\x80\\xFF\"").Kind);

        string supplementaryIdentifier = char.ConvertFromUtf32(0x10400);
        RustLexResult supplementary = RustLexer.Lex($"fn {supplementaryIdentifier}() {{}}");
        AssertEx.Equal(0, supplementary.Diagnostics.Count);
        AssertEx.Equal(RustTokenKind.Identifier, FindToken(supplementary, supplementaryIdentifier).Kind);

        RustLexResult xidIdentifiers = RustLexer.Lex("fn \u2118(a\u00b7b: i32) {}");
        AssertEx.Equal(0, xidIdentifiers.Diagnostics.Count);
        AssertEx.Equal(RustTokenKind.Identifier, FindToken(xidIdentifiers, "\u2118").Kind);
        AssertEx.Equal(RustTokenKind.Identifier, FindToken(xidIdentifiers, "a\u00b7b").Kind);

        RustLexResult excludedIdentifier = RustLexer.Lex("fn \u037a() {}");
        Diagnostic excludedDiagnostic = excludedIdentifier.Diagnostics.Single(diagnostic =>
            diagnostic.Code == RustLexDiagnosticCodes.UnknownCharacter);
        AssertEx.Equal("\u037a", excludedIdentifier.GetText(excludedDiagnostic.Span));

        RustLexResult separatorForms = RustLexer.Lex("1_ 1__ 1.2_ 1e__2 0xff__f");
        AssertEx.Equal(0, separatorForms.Diagnostics.Count);
        AssertEx.True(
            separatorForms.Tokens.Count(token => token.Kind == RustTokenKind.FloatLiteral) == 2,
            "Decimal fraction and exponent forms with separators must remain float literals.");
        AssertEx.Equal(RustTokenKind.IntegerLiteral, FindToken(separatorForms, "0xff__f").Kind);

        RustLexResult fieldAccess = RustLexer.Lex("1._2 1.e2");
        AssertEx.Equal(0, fieldAccess.Diagnostics.Count);
        AssertEx.Equal(RustTokenKind.IntegerLiteral, FindToken(fieldAccess, "1").Kind);
        AssertEx.Equal(RustTokenKind.Identifier, FindToken(fieldAccess, "_2").Kind);
        AssertEx.Equal(RustTokenKind.Identifier, FindToken(fieldAccess, "e2").Kind);

        RustLexResult byteAndCString = RustLexer.Lex(
            "b\"é\" br\"é\" b\"\\u{41}\" b'\\u{41}' c\"\\0\" c\"\\x00\" c\"\\u{0}\" cr\"a\0b\"");
        AssertEx.True(
            byteAndCString.Diagnostics.Count >= 8 &&
            byteAndCString.Diagnostics.All(diagnostic => diagnostic.Code == RustLexDiagnosticCodes.InvalidLiteral),
            "Byte Unicode/non-ASCII and C NUL forms must be diagnosed as invalid literals.");

        RustLexResult nonAsciiDigit = RustLexer.Lex("١٢٣");
        AssertEx.True(
            nonAsciiDigit.Diagnostics.Any(diagnostic => diagnostic.Code == RustLexDiagnosticCodes.UnknownCharacter),
            "Non-ASCII digits must not be accepted as Rust numeric literals.");

        RustLexResult noBreakSpace = RustLexer.Lex("fn\u00a0main() {}");
        AssertEx.True(
            noBreakSpace.Diagnostics.Any(diagnostic =>
                diagnostic.Code == RustLexDiagnosticCodes.UnknownCharacter &&
                noBreakSpace.GetText(diagnostic.Span) == "\u00a0"),
            "NBSP must not be accepted as Rust lexical whitespace.");
        return Task.CompletedTask;
    }

    private static Task BuildsTokenTreesAsync()
    {
        const string source = "fn main(args: [u8; 2]) { println!(\"ok\"); }";
        RustLexResult result = RustLexer.Lex(source);

        AssertEx.Equal(0, result.Diagnostics.Count);
        RustDelimitedTokenTree outer =
            result.TokenTrees.OfType<RustDelimitedTokenTree>()
                .First(group => group.Delimiter == RustDelimiterKind.Parenthesis);
        AssertEx.Equal(RustDelimiterKind.Parenthesis, outer.Delimiter);
        AssertEx.True(outer.IsClosed, "The function parameter group must be closed.");
        AssertEx.Equal("(", outer.OpenToken.Text);
        AssertEx.Equal(")", outer.CloseToken!.Text);
        AssertEx.True(
            outer.Children.OfType<RustDelimitedTokenTree>().Any(group => group.Delimiter == RustDelimiterKind.Bracket),
            "Nested square brackets must be represented as a child group.");

        RustDelimitedTokenTree body =
            result.TokenTrees.OfType<RustDelimitedTokenTree>().Last(group => group.Delimiter == RustDelimiterKind.Brace);
        AssertEx.True(body.Children.OfType<RustDelimitedTokenTree>().Any(), "The body must contain a nested macro group.");
        AssertEx.Equal(")", body.Children.OfType<RustDelimitedTokenTree>().First().CloseToken!.Text);
        return Task.CompletedTask;
    }

    private static Task ReportsDiagnosticsAsync()
    {
        RustLexResult unknown = RustLexer.Lex("let value = \u00a7;");
        Diagnostic unknownDiagnostic = unknown.Diagnostics.Single(diagnostic =>
            diagnostic.Code == RustLexDiagnosticCodes.UnknownCharacter);
        AssertEx.Equal("\u00a7", unknown.GetText(unknownDiagnostic.Span));
        AssertEx.Equal(1, unknownDiagnostic.Span.Length);

        string emoji = char.ConvertFromUtf32(0x1F600);
        RustLexResult supplementaryUnknown = RustLexer.Lex(emoji);
        Diagnostic supplementaryDiagnostic = supplementaryUnknown.Diagnostics.Single();
        RustToken supplementaryToken = supplementaryUnknown.Tokens.Single();
        AssertEx.Equal(RustLexDiagnosticCodes.UnknownCharacter, supplementaryDiagnostic.Code);
        AssertEx.Equal(2, supplementaryDiagnostic.Span.Length);
        AssertEx.Equal(emoji, supplementaryUnknown.GetText(supplementaryDiagnostic.Span));
        AssertEx.Equal(RustTokenKind.Unknown, supplementaryToken.Kind);
        AssertEx.Equal(2, supplementaryToken.Span.Length);

        RustLexResult isolatedHighSurrogate = RustLexer.Lex("\uD800");
        AssertEx.Equal(1, isolatedHighSurrogate.Diagnostics.Count);
        AssertEx.Equal(RustLexDiagnosticCodes.UnknownCharacter, isolatedHighSurrogate.Diagnostics[0].Code);
        AssertEx.Equal(1, isolatedHighSurrogate.Diagnostics[0].Span.Length);
        AssertEx.Equal(1, isolatedHighSurrogate.Tokens.Single().Span.Length);

        RustLexResult isolatedLowSurrogate = RustLexer.Lex("\uDC00");
        AssertEx.Equal(1, isolatedLowSurrogate.Diagnostics.Count);
        AssertEx.Equal(RustLexDiagnosticCodes.UnknownCharacter, isolatedLowSurrogate.Diagnostics[0].Code);
        AssertEx.Equal(1, isolatedLowSurrogate.Diagnostics[0].Span.Length);
        AssertEx.Equal(1, isolatedLowSurrogate.Tokens.Single().Span.Length);

        RustLexResult malformed = RustLexer.Lex("fn main( { \"unterminated");
        AssertEx.True(
            malformed.Diagnostics.Any(diagnostic => diagnostic.Code == RustLexDiagnosticCodes.UnterminatedLiteral),
            "An unterminated quoted literal must be diagnosed.");
        AssertEx.True(
            malformed.Diagnostics.Any(diagnostic => diagnostic.Code == RustLexDiagnosticCodes.UnterminatedDelimiter),
            "An unclosed delimiter must be diagnosed.");

        RustLexResult comment = RustLexer.Lex("/* outer /* nested */");
        AssertEx.Equal(1, comment.Diagnostics.Count);
        AssertEx.Equal(RustLexDiagnosticCodes.UnterminatedComment, comment.Diagnostics[0].Code);

        RustLexResult mismatch = RustLexer.Lex("([)]");
        AssertEx.True(
            mismatch.Diagnostics.Any(diagnostic => diagnostic.Code == RustLexDiagnosticCodes.MismatchedDelimiter),
            "Mismatched delimiters must have a stable diagnostic code.");

        RustLexResult malformedNumbers = RustLexer.Lex("0x_ 0b__ 1e_ 1.0u8 1e2u8 0b1f32");
        AssertEx.True(
            malformedNumbers.Diagnostics.Count(diagnostic => diagnostic.Code == RustLexDiagnosticCodes.InvalidNumber) == 6,
            "Missing digits and incompatible numeric suffixes must be diagnosed without losing token spans.");

        RustLexResult uppercaseRadixPrefixes = RustLexer.Lex("0XFF 0O77 0B11");
        AssertEx.Equal(3, uppercaseRadixPrefixes.Diagnostics.Count);
        AssertEx.True(
            uppercaseRadixPrefixes.Diagnostics.All(diagnostic => diagnostic.Code == RustLexDiagnosticCodes.InvalidNumber),
            "Uppercase radix prefixes must be rejected as invalid numeric literals.");
        AssertEx.True(
            uppercaseRadixPrefixes.Tokens.Select(token => token.Text).SequenceEqual(UppercaseRadixPrefixTexts),
            "Invalid uppercase radix prefixes must remain complete lossless tokens.");

        RustLexResult outOfRangeHexEscapes = RustLexer.Lex("\"\\x80\" \"\\xFF\"");
        AssertEx.Equal(2, outOfRangeHexEscapes.Diagnostics.Count);
        AssertEx.True(
            outOfRangeHexEscapes.Diagnostics.All(diagnostic => diagnostic.Code == RustLexDiagnosticCodes.InvalidLiteral),
            "Ordinary string hex escapes above ASCII must be rejected.");
        AssertEx.True(
            outOfRangeHexEscapes.Diagnostics
                .Select(diagnostic => outOfRangeHexEscapes.GetText(diagnostic.Span))
                .SequenceEqual(OutOfRangeHexEscapeTexts),
            "Out-of-range hex diagnostics must cover the complete escape.");

        RustLexResult supplementaryByteText = RustLexer.Lex("b\"\U0001F600\" br\"\U0001F600\"");
        AssertEx.Equal(2, supplementaryByteText.Diagnostics.Count);
        AssertEx.True(
            supplementaryByteText.Diagnostics.All(diagnostic =>
                diagnostic.Code == RustLexDiagnosticCodes.InvalidLiteral &&
                diagnostic.Span.Length == 2 &&
                supplementaryByteText.GetText(diagnostic.Span) == "\U0001F600"),
            "Byte string diagnostics must cover the complete supplementary Unicode scalar.");

        const string bareCarriageReturnLiterals =
            "\"a\rb\" b\"a\rb\" c\"a\rb\" r\"a\rb\" br\"a\rb\" cr\"a\rb\"";
        RustLexResult bareCarriageReturns = RustLexer.Lex(bareCarriageReturnLiterals);
        AssertEx.Equal(6, bareCarriageReturns.Diagnostics.Count);
        AssertEx.True(
            bareCarriageReturns.Diagnostics.All(diagnostic =>
                diagnostic.Code == RustLexDiagnosticCodes.InvalidLiteral &&
                bareCarriageReturns.GetText(diagnostic.Span) == "\r"),
            "Cooked and raw string forms must reject isolated carriage returns.");

        const string carriageReturnLineFeedLiterals =
            "\"a\r\nb\" b\"a\r\nb\" c\"a\r\nb\" r\"a\r\nb\" br\"a\r\nb\" cr\"a\r\nb\"";
        AssertEx.Equal(0, RustLexer.Lex(carriageReturnLineFeedLiterals).Diagnostics.Count);

        RustLexResult bareTabs = RustLexer.Lex("'\t' b'\t'");
        AssertEx.Equal(2, bareTabs.Diagnostics.Count);
        AssertEx.True(
            bareTabs.Diagnostics.All(diagnostic => diagnostic.Code == RustLexDiagnosticCodes.InvalidLiteral),
            "Character and byte-character literals must reject unescaped tabs.");
        AssertEx.Equal(0, RustLexer.Lex("'\\t' b'\\t'").Diagnostics.Count);

        string maximumHashes = new('#', MaximumRawStringHashCountForTest);
        RustLexResult maximumRawString = RustLexer.Lex($"r{maximumHashes}\"ok\"{maximumHashes}");
        AssertEx.Equal(0, maximumRawString.Diagnostics.Count);
        AssertEx.Equal(RustTokenKind.RawStringLiteral, maximumRawString.Tokens.Single().Kind);

        string excessiveHashes = new('#', MaximumRawStringHashCountForTest + 1);
        RustLexResult excessiveRawString = RustLexer.Lex($"r{excessiveHashes}\"bad\"{excessiveHashes}");
        Diagnostic excessiveHashDiagnostic = excessiveRawString.Diagnostics.Single();
        AssertEx.Equal(RustLexDiagnosticCodes.InvalidLiteral, excessiveHashDiagnostic.Code);
        AssertEx.Equal(MaximumRawStringHashCountForTest + 1, excessiveHashDiagnostic.Span.Length);
        AssertEx.Equal(RustTokenKind.RawStringLiteral, excessiveRawString.Tokens.Single().Kind);
        return Task.CompletedTask;
    }

    private static Task ObeysWorkLimitsAsync()
    {
        var options = new RustLexerOptions
        {
            MaximumSourceLength = 8,
            MaximumTokens = 4,
            MaximumTrivia = 4,
            MaximumDiagnostics = 8,
            MaximumDelimiterDepth = 2,
        };
        RustLexResult result = RustLexer.Lex("fn main() { let x = 1; }", "bounded.rs", options);

        AssertEx.True(result.IsTruncated, "A source-length limit must mark the result as truncated.");
        AssertEx.True(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == RustLexDiagnosticCodes.SourceTooLong),
            "The source-length diagnostic must be stable.");
        AssertEx.True(result.Diagnostics.Count <= options.MaximumDiagnostics, "Diagnostics must remain bounded.");

        var singleDiagnosticOptions = options with { MaximumDiagnostics = 1 };
        RustLexResult singleDiagnostic = RustLexer.Lex("!", "single-diagnostic.rs", singleDiagnosticOptions);
        AssertEx.True(
            singleDiagnostic.Diagnostics.Count <= 1,
            "A caller-specified diagnostic limit of one must be honored.");
        return Task.CompletedTask;
    }

    private static RustToken FindToken(RustLexResult result, string text) =>
        result.Tokens.First(token => token.Text == text);
}
