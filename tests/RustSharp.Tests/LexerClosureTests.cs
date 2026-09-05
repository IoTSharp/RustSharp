using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class LexerClosureTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Lexer preserves BOM and distinguishes shebangs from inner attributes", SourcePreambleAsync),
        new("Lexer distinguishes all comment styles and carriage returns", CommentsAsync),
        new("Lexer covers the Edition 2024 keyword inventory", KeywordsAsync),
        new("Lexer rejects nondecimal floats with complete token spans", NumbersAsync),
        new("Lexer preserves multiline literals and raw terminator boundaries", LiteralBoundariesAsync),
        new("Lexer rejects invalid source scalars inside every lexical context", UnicodeAsync),
        new("Lexer cancels and enforces wall-clock budgets", CancellationAsync),
        new("Lexer freezes maximum-depth trees without CLR recursion", DeepTreesAsync),
        new("Lexer bounds each retained collection independently", LimitsAsync),
        new("Lexer reconstructs bounded deterministic malformed inputs", ReconstructionAsync),
    ];

    private static Task SourcePreambleAsync()
    {
        string[] attributes =
        [
            "#! [allow(dead_code)]", "#!\n[allow(dead_code)]",
            "#! /* outer /* nested */ done */ [allow(dead_code)]",
            "#! // ordinary\n [allow(dead_code)]", "#! /**/ [allow(dead_code)]",
            "\uFEFF#!\t[allow(dead_code)]",
        ];
        foreach (string source in attributes)
        {
            RustLexResult result = LexExact(source);
            AssertEx.True(result.IsSuccessful, source);
            AssertEx.Equal("#", result.Tokens[0].Text);
            AssertEx.False(result.Trivia.Any(item => item.Kind == RustTriviaKind.Shebang), source);
        }

        string[] shebangs = ["#!", "#!/usr/bin/rsc", "#! /bin/rsc\rignored", "#! /// docs [", "#! /** docs */ ["];
        foreach (string source in shebangs)
        {
            RustLexResult result = LexExact("\uFEFF" + source + "\nfn main() {}");
            AssertEx.True(result.IsSuccessful, source);
            AssertEx.Equal(RustTriviaKind.ByteOrderMark, result.Trivia[0].Kind);
            AssertEx.Equal(source, result.Trivia[1].Text);
            AssertEx.Equal("fn", result.Tokens[0].Text);
        }

        AssertEx.False(LexExact(" \uFEFF").IsSuccessful, "Only the initial BOM is accepted.");
        AssertEx.False(LexExact("\uFEFF\uFEFF").IsSuccessful, "Only one BOM is removed.");
        RustLexResult crlfShebang = LexExact("#!/bin/rsc\r\nfn main() {}");
        AssertEx.Equal("#!/bin/rsc", crlfShebang.Trivia[0].Text);
        AssertEx.Equal("\r\n", crlfShebang.Trivia[1].Text);
        return Task.CompletedTask;
    }

    private static Task CommentsAsync()
    {
        const string source = "/**/ /***/ /****/ /** docs */ /*! */ /* /* nested */ */\n//// plain\n/// docs\n//! docs\n// bare\rstill comment\n";
        RustLexResult result = LexExact(source);
        AssertEx.True(result.IsSuccessful, "Ordinary comments allow bare CR.");
        AssertEx.Equal(0, result.Tokens.Count);
        AssertEx.True(result.Trivia.Where(item => item.Kind == RustTriviaKind.BlockComment)
            .Select(item => item.IsDocumentation).SequenceEqual([false, false, false, true, true, false]),
            "Empty /**/ and comments starting /*** are ordinary block comments.");
        AssertEx.True(result.Trivia.Where(item => item.Kind == RustTriviaKind.LineComment)
            .Select(item => item.IsDocumentation).SequenceEqual([false, true, true, false]),
            "Exactly /// and //! introduce documentation.");
        AssertEx.True(LexExact("/// docs\r\n/*! docs\r\n*/").IsSuccessful, "CRLF is normalized once.");
        foreach (string invalid in new[] { "/// a\rb\n", "/*! a\rb */", "/// a\r\r\n" })
        {
            RustLexResult failed = LexExact(invalid);
            Diagnostic diagnostic = failed.Diagnostics.Single();
            AssertEx.Equal(RustLexDiagnosticCodes.InvalidDocumentationComment, diagnostic.Code);
            AssertEx.Equal("\r", failed.GetText(diagnostic.Span));
        }

        return Task.CompletedTask;
    }

    private static Task KeywordsAsync()
    {
        // Reference keywords, independently enumerated, including reserved and weak spellings.
        const string strictAndReserved = "as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while abstract become box do final gen macro override priv try typeof unsized virtual yield";
        RustLexResult keywords = LexExact(strictAndReserved);
        AssertEx.True(keywords.IsSuccessful, "All Edition 2024 keyword spellings lex.");
        AssertEx.Equal(52, keywords.Tokens.Count);
        AssertEx.True(keywords.Tokens.All(token => token.Kind == RustTokenKind.Keyword && token.IsKeyword), "Strict and reserved keywords are tagged.");
        RustLexResult contextual = LexExact("union macro_rules safe raw yeet _");
        AssertEx.True(contextual.IsSuccessful, "Contextual words remain available to the parser.");
        AssertEx.Equal(RustTokenKind.Keyword, contextual.Tokens[0].Kind);
        AssertEx.True(contextual.Tokens.Skip(1).All(token => token.Kind == RustTokenKind.Identifier), "Weak words and underscore retain their lexical identity.");
        return Task.CompletedTask;
    }

    private static Task NumbersAsync()
    {
        string[] invalid = ["0b1.0", "0o7.5", "0xA.5", "0b1e+2", "0o7E-2f32", "0b2.1"];
        foreach (string source in invalid)
        {
            RustLexResult result = LexExact(source);
            AssertEx.Equal(source, result.Tokens.Single().Text);
            AssertEx.Equal(RustTokenKind.FloatLiteral, result.Tokens[0].Kind);
            Diagnostic diagnostic = result.Diagnostics.Single();
            AssertEx.Equal(RustLexDiagnosticCodes.InvalidNumber, diagnostic.Code);
            AssertEx.Equal(source, result.GetText(diagnostic.Span));
        }

        RustLexResult boundaries = LexExact("0x1..2 0b1.field 1. 1e__2 1.e2 1._f 0xffu8 2f64 0x1p2");
        AssertEx.True(boundaries.IsSuccessful, "Ranges, field access, arbitrary suffixes and decimal exponents stay distinct.");
        AssertEx.Equal("u8", boundaries.Tokens.Single(token => token.Text == "0xffu8").LiteralSuffix!);
        AssertEx.Equal(RustTokenKind.IntegerLiteral, boundaries.Tokens.Single(token => token.Text == "2f64").Kind);
        return Task.CompletedTask;
    }

    private static Task LiteralBoundariesAsync()
    {
        const string source = "\"a\r\nb\" b\"a\\\r\n b\" c\"a\\\n b\" r##\"a\"#b\"##tag br\"a\r\nb\" cr#\"a\r\nb\"# '\u00e9' '\U00010400' b'\\xFF'";
        RustLexResult result = LexExact(source);
        AssertEx.True(result.IsSuccessful, "Multiline forms, supplementary scalars and shorter raw closers are valid.");
        AssertEx.Equal(9, result.Tokens.Count);
        RustToken raw = result.Tokens[3];
        AssertEx.Equal("tag", raw.LiteralSuffix!);
        AssertEx.Equal("tag", result.GetText(raw.LiteralSuffixSpan!.Value));
        AssertEx.False(LexExact("\"a\r\r\nb\"").IsSuccessful, "CRLF normalization must not repeat.");
        AssertEx.True(LexExact("b'\"'").IsSuccessful, "Double quotes are allowed inside byte character literals.");
        return Task.CompletedTask;
    }

    private static Task UnicodeAsync()
    {
        string[] contexts = ["\ud800", "\"\udc00\"", "r\"\ud800\"", "//\udc00", "/*\ud800*/"];
        foreach (string source in contexts)
        {
            RustLexResult result = LexExact(source);
            AssertEx.True(result.Diagnostics.Any(d => d.Code == RustLexDiagnosticCodes.UnknownCharacter), "UTF-16 cannot bypass Rust's scalar input contract.");
        }

        const string identifiers = "\u2118 a\u00b7b cafe\u0301 \U00010400 \U00031350";
        AssertEx.True(LexExact(identifiers).IsSuccessful, "XID exceptions, decomposed and supplementary identifiers are preserved.");
        AssertEx.False(LexExact("\u037a \u00a0 \u200d").IsSuccessful, "Non-XID and non-pattern whitespace are rejected.");
        return Task.CompletedTask;
    }

    private static Task CancellationAsync()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => RustLexer.Lex("fn main() {}", null, null, cancelled.Token));
        AssertEx.Throws<TimeoutException>(() => RustLexer.Lex("x", null, new RustLexerOptions { Timeout = TimeSpan.FromTicks(1) }));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => RustLexer.Lex("x", null, new RustLexerOptions { Timeout = TimeSpan.Zero }));
        return Task.CompletedTask;
    }

    private static Task DeepTreesAsync()
    {
        const int depth = 4096;
        // Tiny trial uses the same path before exercising the configured maximum.
        AssertEx.True(LexExact("([])").IsSuccessful, "Small tree trial.");
        string source = new string('(', depth) + "x" + new string(')', depth);
        RustLexResult result = RustLexer.Lex(source, null, new RustLexerOptions { MaximumDelimiterDepth = depth });
        AssertEx.True(result.IsSuccessful, "The absolute supported nesting depth must be usable.");
        RustTokenTree node = result.TokenTrees.Single();
        for (int index = 0; index < depth; index++)
        {
            var group = (RustDelimitedTokenTree)node;
            AssertEx.True(group.IsClosed, "Every nested group must close.");
            AssertEx.Equal(source.Length - 2 * index, group.Span.Length);
            node = group.Children.Single();
        }

        AssertEx.Equal("x", node.Token!.Text);
        return Task.CompletedTask;
    }

    private static Task LimitsAsync()
    {
        (string Source, RustLexerOptions Options)[] cases =
        [
            ("x y z", new() { MaximumTokens = 2 }),
            (" /*a*/ /*b*/ ", new() { MaximumTrivia = 2 }),
            ("` ` `", new() { MaximumDiagnostics = 2 }),
            ("((x))", new() { MaximumDelimiterDepth = 1 }),
            ("abcdef", new() { MaximumSourceLength = 2 }),
        ];
        foreach (var item in cases)
        {
            RustLexResult result = RustLexer.Lex(item.Source, null, item.Options);
            AssertEx.True(result.IsTruncated && !result.IsSuccessful, "A configured limit cannot produce successful evidence.");
            AssertEx.True(result.Tokens.Count <= item.Options.MaximumTokens, "Token bound.");
            AssertEx.True(result.Trivia.Count <= item.Options.MaximumTrivia, "Trivia bound.");
            AssertEx.True(result.Diagnostics.Count <= item.Options.MaximumDiagnostics, "Diagnostic bound.");
        }

        RustLexResult pounds = RustLexer.Lex(new string('#', 65_536), null, new RustLexerOptions { MaximumDiagnostics = 4096 });
        AssertEx.True(pounds.IsTruncated, "A long pound run stops at the diagnostic bound.");
        AssertEx.Equal(4096, pounds.Diagnostics.Count);
        AssertEx.True(pounds.Tokens.All(token => token.Text == "##"), "Cached lookahead must preserve pound-pair token boundaries.");

        return Task.CompletedTask;
    }

    private static Task ReconstructionAsync()
    {
        const string alphabet = "abc09_'\"#/*\\()[]{}\r\n\t\0\u03c0\u00a0";
        var random = new Random(1982024);
        var clock = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = LexExact("a");
        for (int sample = 0; sample < 256; sample++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var source = new StringBuilder();
            int length = random.Next(1, 257);
            for (int character = 0; character < length; character++)
            {
                source.Append(alphabet[random.Next(alphabet.Length)]);
            }

            _ = LexExact(source.ToString(), deadline.Token);
        }

        AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(5), "Malformed corpus respects its wall-clock budget.");
        return Task.CompletedTask;
    }

    private static RustLexResult LexExact(string source, CancellationToken cancellationToken = default)
    {
        RustLexResult result = RustLexer.Lex(source, "lexical-closure.rs", null, cancellationToken);
        AssertEx.False(result.IsTruncated, "Small fixtures must never truncate.");
        var pieces = result.Tokens.Select(token => (token.Span, token.Text))
            .Concat(result.Trivia.Select(trivia => (trivia.Span, trivia.Text))).OrderBy(piece => piece.Span.Start);
        var reconstructed = new StringBuilder();
        int end = 0;
        foreach (var piece in pieces)
        {
            AssertEx.Equal(end, piece.Span.Start, "Source coverage must have neither gaps nor overlaps.");
            AssertEx.Equal(source.Substring(piece.Span.Start, piece.Span.Length), piece.Text);
            reconstructed.Append(piece.Text);
            end = piece.Span.End;
        }

        AssertEx.Equal(source, reconstructed.ToString());
        AssertEx.Equal(source, string.Concat(result.Tokens.SelectMany(token => token.LeadingTrivia.Select(t => t.Text).Append(token.Text))) + string.Concat(result.TrailingTrivia.Select(t => t.Text)));
        AssertEx.True(result.Diagnostics.All(d => d.Span.Start >= 0 && d.Span.End <= source.Length), "Diagnostics must remain within the input.");
        return result;
    }
}
