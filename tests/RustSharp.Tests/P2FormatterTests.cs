using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using RustSharp.Compiler;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class P2FormatterTests
{
    private const int MaximumComparisonItems = 1_000_000;
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string[] FrozenScenarioIds =
    ["format-basic", "format-comments", "format-doc-comments", "format-nested-comments", "format-literals",
        "format-crlf", "format-bom-shebang", "format-check-drift", "format-idempotent", "format-budget"];
    private static readonly ToolingContractManifest Manifest = ReadManifest();
    public static IReadOnlyList<TestCase> All { get; } = CreateCases();

    private static ToolingContractManifest ReadManifest()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string path = Path.Combine(Root, "tools/RustSharp.Conformance/fixtures", ToolingContract.ManifestFileName);
        AssertEx.True(new FileInfo(path).Length is > 0 and <= 131_072, "Frozen tooling manifest byte bound changed.");
        ToolingContractManifest manifest = ToolingContract.Parse(File.ReadAllText(path), cancellation.Token);
        ToolingContract.ValidateCorpus(manifest, Root, cancellation.Token);
        AssertEx.Equal(49, manifest.Corpus.Count);
        AssertEx.Equal(34, manifest.Corpus.Count(row => row.Expected == "format-preserve"));
        AssertEx.Equal(15, manifest.Corpus.Count(row => row.Expected == "format-reject"));
        AssertEx.True(manifest.Cases.Where(row => row.Area == "formatter").Select(row => row.Id)
            .Order(StringComparer.Ordinal).SequenceEqual(FrozenScenarioIds.Order(StringComparer.Ordinal)), "Frozen formatter obligations changed.");
        return manifest;
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<TestCase> CreateCases()
    {
        var clock = Stopwatch.StartNew();
        var cases = new List<TestCase>(59);
        for (int index = 0; index < 49; index++)
        {
            Guard(clock);
            ToolingCorpusCase row = Manifest.Corpus[index];
            cases.Add(new("P2-08.02 corpus " + row.Id, () => CorpusAsync(row)));
        }
        cases.AddRange([
            new("P2-08.02 format-basic", BasicAsync),
            new("P2-08.02 format-comments", CommentsAsync),
            new("P2-08.02 format-doc-comments", DocumentationAsync),
            new("P2-08.02 format-nested-comments", NestedAsync),
            new("P2-08.02 format-literals", LiteralsAsync),
            new("P2-08.02 format-crlf", CrlfAsync),
            new("P2-08.02 format-bom-shebang", PreambleAsync),
            new("P2-08.02 format-check-drift", CheckAsync),
            new("P2-08.02 format-idempotent", IdempotenceAsync),
            new("P2-08.02 format-budget", BudgetAsync),
        ]);
        AssertEx.Equal(59, cases.Count);
        AssertEx.Equal(59, cases.Select(row => row.Name).Distinct(StringComparer.Ordinal).Count());
        return cases.AsReadOnly();
    }

    private static Task CorpusAsync(ToolingCorpusCase row)
    {
        var clock = Stopwatch.StartNew();
        string source = ReadSource(row);
        string before = BytesHash(source);
        FormatterResult result = RustFormatter.Format(source, row.File);
        Guard(clock);
        AssertEx.Equal(row.Expected == "format-preserve", result.Success, row.Id);
        if (row.Expected == "format-preserve") AssertAccepted(source, result, row.File, clock);
        else
        {
            AssertEx.Equal(source, result.FormattedSource, "A rejected corpus source must return its original text: " + row.Id);
            AssertEx.False(result.Changed, "A rejected corpus source must not claim a rewrite: " + row.Id);
            AssertEx.True(result.Diagnostics.Any(d => d.Code == row.DiagnosticCode && d.Span.Start >= 0 && d.Span.Length >= 0 &&
                d.Span.End <= source.Length && source.Substring(d.Span.Start, d.Span.Length) == row.DiagnosticText), "Frozen rejection diagnostic/span changed: " + row.Id);
        }
        AssertEx.Equal(before, BytesHash(source));
        AssertEx.Equal(row.SourceSha256, ToolingContract.SourceHash(source));
        return Task.CompletedTask;
    }

    private static string ReadSource(ToolingCorpusCase row)
    {
        string path = Path.GetFullPath(row.File, Root);
        AssertEx.True(path.StartsWith(Path.GetFullPath(Path.Combine(Root, "tools/RustSharp.Conformance/fixtures")) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "Corpus source escaped its frozen directory.");
        AssertEx.True(new FileInfo(path).Length is > 0 and <= 4_000_000, "Frozen source exceeded its byte bound.");
        string source = File.ReadAllText(path);
        AssertEx.Equal(row.SourceSha256, ToolingContract.SourceHash(source), row.Id);
        return source;
    }

    private static Task BasicAsync()
    {
        const string source = "fn main(){let x=1;x;}";
        FormatterResult result = Accepted(source);
        AssertEx.True(result.Changed, "Compact source must report formatting drift.");
        AssertEx.Equal("fn main() {\n    let x = 1;\n    x;\n}\n", result.FormattedSource);
        return Task.CompletedTask;
    }
    private static Task CommentsAsync()
    {
        // Spaces before each CRLF belong to comment spelling and must survive.
        const string source = "// leading  \r\nfn f(){let x=1;/* inline  */x;} // trailing  \r\n";
        FormatterResult result = Accepted(source);
        AssertEx.True(result.FormattedSource.Contains("// leading  \n", StringComparison.Ordinal), "Leading comment trailing spaces were trimmed.");
        AssertEx.True(result.FormattedSource.Contains("// trailing  \n", StringComparison.Ordinal), "Trailing comment trailing spaces were trimmed.");
        AssertEx.True(result.FormattedSource.Contains("/* inline  */", StringComparison.Ordinal), "Block comment spelling changed.");
        // EOF trivia is not attached to a token, so it must be checked separately.
        Accepted("fn f(){}\n// eof comment  ");
        return Task.CompletedTask;
    }
    private static Task DocumentationAsync()
    {
        const string source = "//! crate docs\n/*! crate block */\n/// function docs\n/** function block */\nfn f(){//! body docs\n/*! body block */let x=1;}\n";
        Accepted(source);
        return Task.CompletedTask;
    }
    private static Task NestedAsync()
    {
        const string source = "fn f(){/* outer /* inner */ tail */let x=1;}";
        FormatterResult result = Accepted(source);
        AssertEx.True(result.FormattedSource.Contains("/* outer /* inner */ tail */", StringComparison.Ordinal), "Nested comment was decoded or split.");
        return Task.CompletedTask;
    }
    private static Task LiteralsAsync()
    {
        ToolingCorpusCase row = Manifest.Corpus.Single(item => item.Id == "syntax-literals");
        Accepted(ReadSource(row), row.File);
        Accepted("fn f(){let x=r#\"raw /* comment */ \\\" \r\n tail\"#;let y=\"utf8 🦀\\n\";let z=0xff_u8;}\n");
        return Task.CompletedTask;
    }
    private static Task CrlfAsync()
    {
        const string source = "fn f(){\r\nlet x=r#\"a\r\nb\"#;\r\n}\r\n";
        FormatterResult result = Accepted(source);
        AssertEx.True(result.FormattedSource.Contains("r#\"a\r\nb\"#", StringComparison.Ordinal), "Raw literal CRLF bytes changed.");
        RustLexResult output = Lex(result.FormattedSource, "crlf.rs");
        AssertEx.True(output.Trivia.Where(row => row.Kind == RustTriviaKind.Whitespace).All(row => !row.Text.Contains('\r')), "Outside-literal whitespace did not normalize to LF.");
        return Task.CompletedTask;
    }
    private static Task PreambleAsync()
    {
        const string source = "\uFEFF#!/usr/bin/env rsc\r\nfn main(){let x=1;}";
        FormatterResult result = Accepted(source);
        AssertEx.True(result.FormattedSource.StartsWith("\uFEFF#!/usr/bin/env rsc\n", StringComparison.Ordinal), "BOM/shebang order, position or spelling changed.");
        return Task.CompletedTask;
    }
    private static Task CheckAsync()
    {
        const string source = "fn main(){let x=1;}";
        string before = BytesHash(source);
        FormatterResult drift = RustFormatter.Format(source, "check.rs", new FormatterOptions { CheckOnly = true });
        AssertEx.False(drift.Success, "Check mode must reject formatting drift.");
        AssertEx.True(drift.Changed, "Check mode must identify formatting drift.");
        AssertEx.Equal(source, drift.FormattedSource, "Check mode must return original source without publishing its proposed rewrite.");
        AssertEx.True(drift.Diagnostics.Any(row => row.Code == "RSTF1002"), "Check drift diagnostic changed.");
        AssertEx.Equal(before, BytesHash(source));
        string canonical = Accepted(source).FormattedSource;
        FormatterResult clean = RustFormatter.Format(canonical, "check.rs", new FormatterOptions { CheckOnly = true });
        AssertEx.True(clean.Success && !clean.Changed && clean.Diagnostics.Count == 0, "Canonical check mode must pass without drift.");
        AssertEx.Equal(canonical, clean.FormattedSource);
        return Task.CompletedTask;
    }
    private static Task IdempotenceAsync()
    {
        var clock = Stopwatch.StartNew();
        // Tiny bounded trial precedes the finite corpus batch.
        Accepted("fn f(){}", "trial.rs");
        int accepted = 0;
        for (int index = 0; index < 49; index++)
        {
            Guard(clock, 20);
            ToolingCorpusCase row = Manifest.Corpus[index];
            if (row.Expected != "format-preserve") continue;
            FormatterResult first = RustFormatter.Format(ReadSource(row), row.File);
            AssertEx.True(first.Success, row.Id);
            FormatterResult second = RustFormatter.Format(first.FormattedSource, row.File);
            AssertEx.True(second.Success && !second.Changed, "Second pass reported drift: " + row.Id);
            AssertEx.Equal(BytesHash(first.FormattedSource), BytesHash(second.FormattedSource), row.Id);
            AssertEx.Equal(first.FormattedSource, second.FormattedSource, row.Id);
            accepted++;
        }
        AssertEx.Equal(34, accepted);
        return Task.CompletedTask;
    }
    private static Task BudgetAsync()
    {
        var clock = Stopwatch.StartNew();
        const string source = "fn f(){ /* budget */ let x=1;}";
        RustLexResult lex = Lex(source, "budget.rs");
        int operations = checked(lex.Tokens.Count + lex.Trivia.Count);
        FormatterOptions[] limits =
        [new() { MaximumSourceLength = source.Length }, new() { MaximumTokens = lex.Tokens.Count },
            new() { MaximumTrivia = lex.Trivia.Count }, new() { MaximumOperations = operations }];
        FormatterOptions[] exceeded =
        [new() { MaximumSourceLength = source.Length - 1 }, new() { MaximumTokens = lex.Tokens.Count - 1 },
            new() { MaximumTrivia = lex.Trivia.Count - 1 }, new() { MaximumOperations = operations - 1 }];
        for (int index = 0; index < 4; index++)
        {
            Guard(clock);
            FormatterResult atLimit = RustFormatter.Format(source, "budget.rs", limits[index]);
            AssertEx.True(atLimit.Success, "At-limit source/token/trivia/operation budget must succeed: " + index);
            AssertBudgetFailure(source, RustFormatter.Format(source, "budget.rs", exceeded[index]));
        }
        const string unicodeSource = "fn f(){let x=\"🦀\";}";
        int outputBytes = Encoding.UTF8.GetByteCount(Accepted(unicodeSource).FormattedSource);
        AssertEx.True(RustFormatter.Format(unicodeSource, "budget.rs", new FormatterOptions { MaximumOutputBytes = outputBytes }).Success, "At-limit UTF-8 output bytes must succeed.");
        AssertBudgetFailure(unicodeSource, RustFormatter.Format(unicodeSource, "budget.rs", new FormatterOptions { MaximumOutputBytes = outputBytes - 1 }));
        for (int attempt = 0; attempt < 32; attempt++)
        {
            Guard(clock);
            AssertBudgetFailure(source, RustFormatter.Format(source, "budget.rs",
                new FormatterOptions { Timeout = TimeSpan.FromTicks(1) }));
        }
        // First output may expand, but must remain acceptable to a default second pass.
        string expansion = "fn f(){/*" + new string('x', 999981) + "*/}";
        AssertEx.True(expansion.Length < 1_000_000, "Expansion input stays below the frozen source ceiling.");
        AssertBudgetFailure(expansion, RustFormatter.Format(expansion, "expanded-limit.rs"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => RustFormatter.Format(source, "cancel.rs", null, cancellation.Token));
        Guard(clock);
        return Task.CompletedTask;
    }
    private static void AssertBudgetFailure(string original, FormatterResult result)
    {
        AssertEx.False(result.Success || result.Changed, "Budget failure must not publish a rewrite.");
        AssertEx.Equal(original, result.FormattedSource);
        AssertEx.True(result.Diagnostics.Any(row => row.Code == "RSTF0001"), "Budget diagnostic changed.");
    }

    private static FormatterResult Accepted(string source, string path = "formatter-test.rs")
    {
        var clock = Stopwatch.StartNew();
        FormatterResult result = RustFormatter.Format(source, path);
        AssertAccepted(source, result, path, clock);
        return result;
    }
    private static void AssertAccepted(string source, FormatterResult result, string path, Stopwatch clock)
    {
        Guard(clock);
        AssertEx.True(result.Success, path + ": " + string.Join("; ", result.Diagnostics.Select(row => row.Code + ": " + row.Message)));
        AssertEx.Equal(0, result.Diagnostics.Count);
        AssertEx.Equal(source != result.FormattedSource, result.Changed);
        AssertEx.True(result.FormattedSource.EndsWith('\n') && !result.FormattedSource.EndsWith("\n\n", StringComparison.Ordinal), "Accepted output requires exactly one final newline.");
        SafeCoreSyntaxResult original = Parse(source, path);
        SafeCoreSyntaxResult formatted = Parse(result.FormattedSource, path);
        AssertEx.True(original.IsSuccessful && formatted.IsSuccessful, "Formatting changed parser acceptance: " + path);
        AssertSameTokensAndTrivia(original.LexResult, formatted.LexResult, clock);
        AssertSameAst(original.Root!, formatted.Root!, clock);
        FormatterResult second = RustFormatter.Format(result.FormattedSource, path);
        AssertEx.True(second.Success && !second.Changed, "Formatting must be idempotent: " + path);
        AssertEx.Equal(result.FormattedSource, second.FormattedSource);
        Guard(clock);
    }
    private static RustLexResult Lex(string source, string path) => RustLexer.Lex(source, path,
        new RustLexerOptions { Timeout = TimeSpan.FromSeconds(2), MaximumSourceLength = 1_000_000, MaximumTokens = 250_000, MaximumTrivia = 500_000 });
    private static SafeCoreSyntaxResult Parse(string source, string path) => SafeCoreSyntax.Parse(source, path,
        new SafeCoreSyntaxOptions { Timeout = TimeSpan.FromSeconds(2) });
    private static void AssertSameTokensAndTrivia(RustLexResult left, RustLexResult right, Stopwatch clock)
    {
        AssertEx.True(left.IsSuccessful && right.IsSuccessful, "Both lossless lexical passes must finish without diagnostics.");
        AssertEx.Equal(left.Tokens.Count, right.Tokens.Count);
        AssertEx.True(left.Tokens.Count <= 250_000 && left.Trivia.Count <= 500_000 && right.Trivia.Count <= 500_000, "Token/trivia inventory exceeded its finite comparison bounds.");
        for (int index = 0; index < left.Tokens.Count && index < 250_000; index++)
        {
            Guard(clock);
            RustToken a = left.Tokens[index], b = right.Tokens[index];
            AssertEx.Equal(a.Kind, b.Kind, "Token kind changed at " + index);
            AssertEx.Equal(a.Text, b.Text, "Token spelling changed at " + index);
            AssertEx.True(a.Delimiter == b.Delimiter, "Token delimiter kind changed.");
            AssertEx.Equal(a.IsKeyword, b.IsKeyword);
            AssertEx.True(string.Equals(a.LiteralSuffix, b.LiteralSuffix, StringComparison.Ordinal), "Literal suffix spelling changed.");
        }
        AssertEx.True(AnchoredTrivia(left, clock).SequenceEqual(AnchoredTrivia(right, clock)), "Comment/doc/BOM/shebang spelling, order or token attachment changed.");
        AssertLossless(left, clock);
        AssertLossless(right, clock);
    }
    private static List<(int Anchor, RustTriviaKind Kind, bool Documentation, string Text)> AnchoredTrivia(RustLexResult lex, Stopwatch clock)
    {
        var values = new List<(int, RustTriviaKind, bool, string)>();
        int visited = 0;
        for (int index = 0; index <= lex.Tokens.Count && index <= 250_000; index++)
        {
            Guard(clock);
            IReadOnlyList<RustTrivia> trivia = index == lex.Tokens.Count ? lex.TrailingTrivia : lex.Tokens[index].LeadingTrivia;
            AssertEx.True(trivia.Count <= 500_000, "Attached trivia exceeded its count bound.");
            for (int item = 0; item < trivia.Count && item < 500_000; item++)
            {
                Guard(clock);
                AssertEx.True(++visited <= 500_000, "Attached trivia exceeded its total bound.");
                RustTrivia row = trivia[item];
                if (row.Kind != RustTriviaKind.Whitespace) values.Add((index, row.Kind, row.IsDocumentation, row.Text));
            }
        }
        AssertEx.Equal(lex.Trivia.Count, visited, "Leading/trailing trivia duplicated or omitted a global trivia entry.");
        return values;
    }
    private static void AssertLossless(RustLexResult lex, Stopwatch clock)
    {
        var restored = new StringBuilder();
        int visited = 0;
        for (int index = 0; index <= lex.Tokens.Count && index <= 250_000; index++)
        {
            Guard(clock);
            IReadOnlyList<RustTrivia> trivia = index == lex.Tokens.Count ? lex.TrailingTrivia : lex.Tokens[index].LeadingTrivia;
            for (int item = 0; item < trivia.Count && item < 500_000; item++)
            {
                Guard(clock);
                AssertEx.True(++visited <= 500_000, "Lossless reconstruction exceeded its trivia bound.");
                restored.Append(trivia[item].Text);
            }
            if (index < lex.Tokens.Count) restored.Append(lex.Tokens[index].Text);
            AssertEx.True(restored.Length <= 1_000_000, "Lossless reconstruction exceeded its source bound.");
        }
        AssertEx.Equal(lex.Source, restored.ToString(), "Lossless leading/trailing trivia reconstruction changed source bytes.");
    }

    // Independent of the formatter's fingerprint: inspect every public AST property.
    // Span values are the only excluded data; type, shape, raw text and flags remain significant.
    private static void AssertSameAst(object left, object right, Stopwatch clock)
    {
        var pending = new Queue<(object? Left, object? Right, int Depth)>();
        pending.Enqueue((left,right,0));
        for (int visited = 0; visited < MaximumComparisonItems && pending.Count > 0; visited++)
        {
            Guard(clock);
            var pair = pending.Dequeue();
            AssertEx.True(pair.Depth <= 256, "AST structural comparison exceeded its depth bound.");
            if (pair.Left is null || pair.Right is null)
            {
                AssertEx.True(pair.Left is null && pair.Right is null, "AST nullability changed.");
                continue;
            }
            Type type = pair.Left.GetType();
            if (type == typeof(TextSpan)) continue;
            if (type.IsPrimitive || type.IsEnum || pair.Left is string or decimal)
            {
                AssertEx.Equal(type, pair.Right.GetType(), "AST scalar type changed.");
                AssertEx.Equal(pair.Left, pair.Right, "AST scalar/raw spelling changed.");
                continue;
            }
            if (pair.Left is IEnumerable a && pair.Right is IEnumerable b)
            {
                IEnumerator first = a.GetEnumerator(), second = b.GetEnumerator();
                bool complete = false;
                try
                {
                    for (int index = 0; index <= 250_000; index++)
                    {
                        Guard(clock);
                        bool hasFirst = first.MoveNext(), hasSecond = second.MoveNext();
                        AssertEx.Equal(hasFirst, hasSecond, "AST collection length changed.");
                        if (!hasFirst) { complete = true; break; }
                        AssertEx.True(index < 250_000 && pending.Count < MaximumComparisonItems, "AST collection/queue exceeded its item bound.");
                        pending.Enqueue((first.Current,second.Current,pair.Depth + 1));
                    }
                }
                finally { (first as IDisposable)?.Dispose(); (second as IDisposable)?.Dispose(); }
                AssertEx.True(complete, "AST collection comparison did not finish.");
                continue;
            }
            AssertEx.Equal(type, pair.Right.GetType(), "AST node type changed.");
            PropertyInfo[] properties = PublicAstType(pair.Left).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetMethod is not null && property.GetIndexParameters().Length == 0 &&
                    property.PropertyType != typeof(TextSpan) && Nullable.GetUnderlyingType(property.PropertyType) != typeof(TextSpan))
                .OrderBy(property => property.Name, StringComparer.Ordinal).Take(129).ToArray();
            AssertEx.True(properties.Length is > 0 and <= 128, "AST node public property inventory exceeded its bound.");
            for (int index = 0; index < properties.Length && index < 128; index++)
            {
                Guard(clock);
                AssertEx.True(pending.Count < MaximumComparisonItems, "AST structural queue exceeded its item bound.");
                PropertyInfo property = properties[index];
                pending.Enqueue((property.GetValue(pair.Left),property.GetValue(pair.Right),pair.Depth + 1));
            }
        }
        AssertEx.Equal(0, pending.Count, "AST structural comparison exhausted its item budget.");
    }
    // Static type roots retain the independent all-public-property oracle under trimming.
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    private static Type PublicAstType(object value) => value switch
    {
        SafeCoreSyntaxOptions => typeof(SafeCoreSyntaxOptions),
        SafeCoreCompilationUnitSyntax => typeof(SafeCoreCompilationUnitSyntax),
        SafeCoreAttributeSyntax => typeof(SafeCoreAttributeSyntax),
        SafeCoreVisibilitySyntax => typeof(SafeCoreVisibilitySyntax),
        SafeCoreModuleSyntax => typeof(SafeCoreModuleSyntax),
        SafeCoreUseSyntax => typeof(SafeCoreUseSyntax),
        SafeCoreUseTreeSyntax => typeof(SafeCoreUseTreeSyntax),
        SafeCorePathTypeSyntax => typeof(SafeCorePathTypeSyntax),
        SafeCorePathSegmentSyntax => typeof(SafeCorePathSegmentSyntax),
        SafeCoreTypeArgumentSyntax => typeof(SafeCoreTypeArgumentSyntax),
        SafeCoreLifetimeArgumentSyntax => typeof(SafeCoreLifetimeArgumentSyntax),
        SafeCoreConstArgumentSyntax => typeof(SafeCoreConstArgumentSyntax),
        SafeCoreAssociatedTypeArgumentSyntax => typeof(SafeCoreAssociatedTypeArgumentSyntax),
        SafeCoreAssociatedConstraintArgumentSyntax => typeof(SafeCoreAssociatedConstraintArgumentSyntax),
        SafeCoreLifetimeBoundSyntax => typeof(SafeCoreLifetimeBoundSyntax),
        SafeCoreTraitBoundSyntax => typeof(SafeCoreTraitBoundSyntax),
        SafeCoreReferenceTypeSyntax => typeof(SafeCoreReferenceTypeSyntax),
        SafeCoreTupleTypeSyntax => typeof(SafeCoreTupleTypeSyntax),
        SafeCoreArrayTypeSyntax => typeof(SafeCoreArrayTypeSyntax),
        SafeCoreSliceTypeSyntax => typeof(SafeCoreSliceTypeSyntax),
        SafeCoreUnitTypeSyntax => typeof(SafeCoreUnitTypeSyntax),
        SafeCoreNeverTypeSyntax => typeof(SafeCoreNeverTypeSyntax),
        SafeCoreFunctionTypeParameterSyntax => typeof(SafeCoreFunctionTypeParameterSyntax),
        SafeCoreFunctionTypeSyntax => typeof(SafeCoreFunctionTypeSyntax),
        SafeCoreInferredTypeSyntax => typeof(SafeCoreInferredTypeSyntax),
        SafeCoreBoundedTypeSyntax => typeof(SafeCoreBoundedTypeSyntax),
        SafeCoreQualifiedPathTypeSyntax => typeof(SafeCoreQualifiedPathTypeSyntax),
        SafeCoreParameterSyntax => typeof(SafeCoreParameterSyntax),
        SafeCoreReceiverSyntax => typeof(SafeCoreReceiverSyntax),
        SafeCoreGenericParameterSyntax => typeof(SafeCoreGenericParameterSyntax),
        SafeCoreWhereClauseSyntax => typeof(SafeCoreWhereClauseSyntax),
        SafeCoreLifetimeWherePredicateSyntax => typeof(SafeCoreLifetimeWherePredicateSyntax),
        SafeCoreTypeWherePredicateSyntax => typeof(SafeCoreTypeWherePredicateSyntax),
        SafeCoreFunctionSyntax => typeof(SafeCoreFunctionSyntax),
        SafeCoreFieldSyntax => typeof(SafeCoreFieldSyntax),
        SafeCoreStructSyntax => typeof(SafeCoreStructSyntax),
        SafeCoreEnumVariantSyntax => typeof(SafeCoreEnumVariantSyntax),
        SafeCoreEnumSyntax => typeof(SafeCoreEnumSyntax),
        SafeCoreTypeAliasSyntax => typeof(SafeCoreTypeAliasSyntax),
        SafeCoreConstSyntax => typeof(SafeCoreConstSyntax),
        SafeCoreTraitSyntax => typeof(SafeCoreTraitSyntax),
        SafeCoreImplSyntax => typeof(SafeCoreImplSyntax),
        SafeCoreAssociatedFunctionSyntax => typeof(SafeCoreAssociatedFunctionSyntax),
        SafeCoreAssociatedTypeSyntax => typeof(SafeCoreAssociatedTypeSyntax),
        SafeCoreAssociatedConstSyntax => typeof(SafeCoreAssociatedConstSyntax),
        SafeCoreLetStatementSyntax => typeof(SafeCoreLetStatementSyntax),
        SafeCoreReturnStatementSyntax => typeof(SafeCoreReturnStatementSyntax),
        SafeCoreExpressionStatementSyntax => typeof(SafeCoreExpressionStatementSyntax),
        SafeCoreBlockSyntax => typeof(SafeCoreBlockSyntax),
        SafeCorePrintExpressionSyntax => typeof(SafeCorePrintExpressionSyntax),
        SafeCoreNameExpressionSyntax => typeof(SafeCoreNameExpressionSyntax),
        SafeCoreLiteralExpressionSyntax => typeof(SafeCoreLiteralExpressionSyntax),
        SafeCoreUnaryExpressionSyntax => typeof(SafeCoreUnaryExpressionSyntax),
        SafeCoreBinaryExpressionSyntax => typeof(SafeCoreBinaryExpressionSyntax),
        SafeCoreCallExpressionSyntax => typeof(SafeCoreCallExpressionSyntax),
        SafeCoreTupleExpressionSyntax => typeof(SafeCoreTupleExpressionSyntax),
        SafeCoreArrayExpressionSyntax => typeof(SafeCoreArrayExpressionSyntax),
        SafeCoreBlockExpressionSyntax => typeof(SafeCoreBlockExpressionSyntax),
        SafeCoreIfExpressionSyntax => typeof(SafeCoreIfExpressionSyntax),
        SafeCoreIndexExpressionSyntax => typeof(SafeCoreIndexExpressionSyntax),
        SafeCoreIdentifierPatternSyntax => typeof(SafeCoreIdentifierPatternSyntax),
        SafeCoreWildcardPatternSyntax => typeof(SafeCoreWildcardPatternSyntax),
        SafeCoreLiteralPatternSyntax => typeof(SafeCoreLiteralPatternSyntax),
        SafeCoreTuplePatternSyntax => typeof(SafeCoreTuplePatternSyntax),
        SafeCorePathPatternSyntax => typeof(SafeCorePathPatternSyntax),
        SafeCoreQualifiedNameExpressionSyntax => typeof(SafeCoreQualifiedNameExpressionSyntax),
        SafeCoreItemStatementSyntax => typeof(SafeCoreItemStatementSyntax),
        SafeCoreEmptyStatementSyntax => typeof(SafeCoreEmptyStatementSyntax),
        SafeCoreExpressionPathSegmentSyntax => typeof(SafeCoreExpressionPathSegmentSyntax),
        SafeCoreStructExpressionFieldSyntax => typeof(SafeCoreStructExpressionFieldSyntax),
        SafeCoreStructExpressionSyntax => typeof(SafeCoreStructExpressionSyntax),
        SafeCoreMemberExpressionSyntax => typeof(SafeCoreMemberExpressionSyntax),
        SafeCoreCastExpressionSyntax => typeof(SafeCoreCastExpressionSyntax),
        SafeCoreRangeExpressionSyntax => typeof(SafeCoreRangeExpressionSyntax),
        SafeCoreMatchArmSyntax => typeof(SafeCoreMatchArmSyntax),
        SafeCoreMatchExpressionSyntax => typeof(SafeCoreMatchExpressionSyntax),
        SafeCoreLoopExpressionSyntax => typeof(SafeCoreLoopExpressionSyntax),
        SafeCoreWhileExpressionSyntax => typeof(SafeCoreWhileExpressionSyntax),
        SafeCoreForExpressionSyntax => typeof(SafeCoreForExpressionSyntax),
        SafeCoreClosureParameterSyntax => typeof(SafeCoreClosureParameterSyntax),
        SafeCoreClosureExpressionSyntax => typeof(SafeCoreClosureExpressionSyntax),
        SafeCoreReturnExpressionSyntax => typeof(SafeCoreReturnExpressionSyntax),
        SafeCoreBreakExpressionSyntax => typeof(SafeCoreBreakExpressionSyntax),
        SafeCoreContinueExpressionSyntax => typeof(SafeCoreContinueExpressionSyntax),
        SafeCoreTryExpressionSyntax => typeof(SafeCoreTryExpressionSyntax),
        SafeCoreLetExpressionSyntax => typeof(SafeCoreLetExpressionSyntax),
        SafeCoreLabeledBlockExpressionSyntax => typeof(SafeCoreLabeledBlockExpressionSyntax),
        SafeCoreConstBlockExpressionSyntax => typeof(SafeCoreConstBlockExpressionSyntax),
        SafeCoreReferencePatternSyntax => typeof(SafeCoreReferencePatternSyntax),
        SafeCoreSlicePatternSyntax => typeof(SafeCoreSlicePatternSyntax),
        SafeCoreRestPatternSyntax => typeof(SafeCoreRestPatternSyntax),
        SafeCoreAtPatternSyntax => typeof(SafeCoreAtPatternSyntax),
        SafeCoreOrPatternSyntax => typeof(SafeCoreOrPatternSyntax),
        SafeCoreRangePatternSyntax => typeof(SafeCoreRangePatternSyntax),
        SafeCoreStructPatternFieldSyntax => typeof(SafeCoreStructPatternFieldSyntax),
        SafeCoreStructPatternSyntax => typeof(SafeCoreStructPatternSyntax),
        _ => throw new InvalidOperationException("Unexpected AST property type: " + value.GetType().FullName),
    };
    private static string BytesHash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    private static void Guard(Stopwatch clock, int maximumSeconds = 10)
    {
        if (clock.Elapsed.TotalSeconds >= maximumSeconds) throw new TimeoutException("Formatter test exceeded its explicit wall-clock budget.");
    }
}
