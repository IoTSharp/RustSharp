using RustSharp.Syntax;
using System.Text.Json;

namespace RustSharp.Tests;

internal static class SafeCoreSyntaxTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("safe-core parses modules items generics and attributes", ParsesItemsAsync),
        new("safe-core parses statements expressions patterns and types", ParsesCoreFormsAsync),
        new("safe-core preserves spans and source text", PreservesSpansAsync),
        new("safe-core rejects unsupported syntax explicitly", RejectsUnsupportedAsync),
        new("safe-core does not reinterpret keywords as names", RejectsKeywordsAsNamesAsync),
        new("safe-core rejects dangling item prefixes", RejectsDanglingItemPrefixesAsync),
        new("safe-core rejects unmodeled restricted visibility", RejectsRestrictedVisibilityAsync),
        new("safe-core preserves unary precedence and assignment associativity", ParsesOperatorBindingAsync),
        new("safe-core rejects dangling path-pattern separators", RejectsDanglingPathPatternSeparatorAsync),
        new("safe-core rejects external module declarations", RejectsExternalModuleDeclarationsAsync),
        new("safe-core reports malformed syntax with stable diagnostics", ReportsMalformedAsync),
        new("safe-core obeys explicit node and operation limits", ObeysLimitsAsync),
        new("safe-core published corpus has bounded outcomes", CorpusAsync),
    ];

    private static Task ParsesItemsAsync()
    {
        const string source =
            "#![no_std]\n" +
            "#[derive(Debug)] pub mod model {\n" +
            "    pub struct Pair<T: Copy> { pub first: T, second: i32 }\n" +
            "    pub enum Choice<T> { One(T), None, }\n" +
            "    pub type Count = usize;\n" +
            "    pub const LIMIT: usize = 4;\n" +
            "    pub fn identity<T: Copy>(value: T) -> T { return value; }\n" +
            "}\n" +
            "use crate::model::Pair as PublicPair;\n";

        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "items.rs");
        AssertSuccessful(result);
        SafeCoreCompilationUnitSyntax root = AssertEx.NotNull(result.Root, "A valid safe-core document needs a root.");
        AssertEx.Equal(1, root.Attributes.Count);
        AssertEx.Equal(2, root.Items.Count);
        SafeCoreModuleSyntax module = AssertEx.NotNull(root.Items.OfType<SafeCoreModuleSyntax>().SingleOrDefault(), "The module must parse.");
        AssertEx.Equal("model", module.Name);
        AssertEx.Equal(5, module.Items.Count);
        SafeCoreStructSyntax pair = AssertEx.NotNull(module.Items.OfType<SafeCoreStructSyntax>().SingleOrDefault(), "The struct must parse.");
        AssertEx.Equal("Pair", pair.Name);
        AssertEx.Equal(1, pair.GenericParameters.Count);
        AssertEx.Equal(2, pair.Fields.Count);
        SafeCoreFunctionSyntax identity = AssertEx.NotNull(module.Items.OfType<SafeCoreFunctionSyntax>().SingleOrDefault(), "The function must parse.");
        AssertEx.Equal("identity", identity.Name);
        AssertEx.Equal(1, identity.Parameters.Count);
        AssertEx.Equal("T", identity.GenericParameters[0].Name);
        return Task.CompletedTask;
    }

    private static Task ParsesCoreFormsAsync()
    {
        const string source =
            "fn compute(value: &mut [i32; 2], flag: bool) -> i32 {\n" +
            "    let mut result: i32 = value[0] + 2 * 3;\n" +
            "    if flag { result } else { return 0; }\n" +
            "}\n";

        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "forms.rs");
        AssertSuccessful(result);
        SafeCoreFunctionSyntax function = AssertEx.NotNull(result.Root!.Items.OfType<SafeCoreFunctionSyntax>().SingleOrDefault(), "Function must parse.");
        AssertEx.Equal(2, function.Parameters.Count);
        AssertEx.True(function.Parameters[0].Type is SafeCoreReferenceTypeSyntax, "Reference type must be represented.");
        SafeCoreBlockSyntax body = function.Body;
        AssertEx.Equal(1, body.Statements.Count);
        SafeCoreLetStatementSyntax let = AssertEx.NotNull(body.Statements.OfType<SafeCoreLetStatementSyntax>().SingleOrDefault(), "let must parse.");
        AssertEx.True(let.Pattern is SafeCoreIdentifierPatternSyntax { IsMutable: true }, "mut pattern must be represented.");
        AssertEx.True(let.Initializer is SafeCoreBinaryExpressionSyntax, "binary expression must be represented.");
        AssertEx.True(body.TailExpression is SafeCoreIfExpressionSyntax, "if expression must be represented.");
        return Task.CompletedTask;
    }

    private static Task PreservesSpansAsync()
    {
        const string source = "  #[inline] fn main() { let x: i32 = 1; }  ";
        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "spans.rs");
        AssertSuccessful(result);
        SafeCoreFunctionSyntax function = result.Root!.Items.OfType<SafeCoreFunctionSyntax>().Single();
        AssertEx.Equal("fn main() { let x: i32 = 1; }", result.GetText(function.Span));
        AssertEx.True(function.Span.Start > 0, "Item span should exclude leading trivia.");
        AssertEx.Equal(source, result.LexResult.ToSourceText());
        return Task.CompletedTask;
    }

    private static Task RejectsUnsupportedAsync()
    {
        (string Name, string Source, string Keyword)[] cases =
        [
            ("unsafe", "unsafe fn dangerous() {}", "unsafe"),
            ("loop", "fn main() { loop {} }", "loop"),
            ("break", "fn main() { break; }", "break"),
            ("continue", "fn main() { continue; }", "continue"),
        ];

        foreach ((string name, string source, string keyword) in cases)
        {
            SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, $"unsupported-{name}-safe-core.rs");
            AssertEx.False(result.IsSuccessful, $"Unsupported '{name}' syntax must not parse successfully.");
            AssertEx.True(result.Root is null, $"Unsupported '{name}' syntax must not expose a root.");
            Diagnostic? diagnostic = result.Diagnostics.FirstOrDefault(item =>
                item.Code == SafeCoreSyntaxDiagnosticCodes.UnsupportedSyntax);
            AssertEx.True(diagnostic is not null, $"Unsupported '{name}' syntax needs the stable RSP1003 code.");
            AssertEx.Equal(
                keyword,
                result.GetText(diagnostic!.Span),
                $"The RSP1003 diagnostic for '{name}' must identify the unsupported keyword.");
        }

        return Task.CompletedTask;
    }

    private static Task RejectsKeywordsAsNamesAsync()
    {
        SafeCoreSyntaxResult expression = SafeCoreSyntax.Parse(
            "fn main() { loop; }",
            "keyword-expression.rs");
        AssertEx.False(expression.IsSuccessful, "An unsupported keyword must not become a name expression.");
        AssertEx.True(
            expression.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreSyntaxDiagnosticCodes.UnsupportedSyntax),
            "Unsupported keyword expressions need the stable RSP1003 code.");

        SafeCoreSyntaxResult declaration = SafeCoreSyntax.Parse(
            "fn loop() {}",
            "keyword-name.rs");
        AssertEx.False(declaration.IsSuccessful, "A reserved keyword must not become a declared item name.");
        AssertEx.True(
            declaration.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreSyntaxDiagnosticCodes.ExpectedToken),
            "Reserved declaration names need the stable expected-token diagnostic.");
        return Task.CompletedTask;
    }

    private static Task RejectsDanglingItemPrefixesAsync()
    {
        foreach (string source in new[] { "#[inline]", "pub" })
        {
            SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "dangling-item-prefix.rs");
            AssertEx.False(result.IsSuccessful, "An item prefix at end of file must not be discarded.");
            AssertEx.True(
                result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreSyntaxDiagnosticCodes.ExpectedToken),
                "A dangling item prefix needs the stable expected-token diagnostic.");
        }

        return Task.CompletedTask;
    }

    private static Task RejectsRestrictedVisibilityAsync()
    {
        foreach (string visibility in new[] { "pub(crate)", "pub(self)", "pub(super)", "pub(in crate)", "pub(foo)" })
        {
            SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(
                $"{visibility} fn exposed() {{}}",
                "restricted-visibility.rs");
            AssertEx.False(
                result.IsSuccessful,
                $"'{visibility}' must not be treated as unrestricted public visibility.");
            AssertEx.True(result.Root is null, "Unsupported visibility must not expose a syntax root.");
            Diagnostic diagnostic = result.Diagnostics.Single();
            AssertEx.Equal(SafeCoreSyntaxDiagnosticCodes.UnsupportedSyntax, diagnostic.Code);
            AssertEx.Equal("(", result.GetText(diagnostic.Span));
        }

        return Task.CompletedTask;
    }

    private static Task ParsesOperatorBindingAsync()
    {
        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(
            "fn operators() { -left * right; target = first = second; }",
            "operator-binding.rs");
        AssertSuccessful(result);
        SafeCoreFunctionSyntax function = result.Root!.Items.OfType<SafeCoreFunctionSyntax>().Single();
        SafeCoreExpressionStatementSyntax[] statements = function.Body.Statements
            .OfType<SafeCoreExpressionStatementSyntax>()
            .ToArray();
        AssertEx.Equal(2, statements.Length);

        SafeCoreBinaryExpressionSyntax multiplication = AssertEx.NotNull(
            statements[0].Expression as SafeCoreBinaryExpressionSyntax,
            "Multiplication must remain outside its unary left operand.");
        AssertEx.Equal("*", multiplication.Operator);
        SafeCoreUnaryExpressionSyntax unary = AssertEx.NotNull(
            multiplication.Left as SafeCoreUnaryExpressionSyntax,
            "Unary negation must bind more tightly than multiplication.");
        AssertEx.Equal("-", unary.Operator);
        AssertEx.Equal("left", ((SafeCoreNameExpressionSyntax)unary.Operand).Path);
        AssertEx.Equal("right", ((SafeCoreNameExpressionSyntax)multiplication.Right).Path);

        SafeCoreBinaryExpressionSyntax assignment = AssertEx.NotNull(
            statements[1].Expression as SafeCoreBinaryExpressionSyntax,
            "The assignment expression must retain its outer operator.");
        AssertEx.Equal("=", assignment.Operator);
        AssertEx.Equal("target", ((SafeCoreNameExpressionSyntax)assignment.Left).Path);
        SafeCoreBinaryExpressionSyntax nestedAssignment = AssertEx.NotNull(
            assignment.Right as SafeCoreBinaryExpressionSyntax,
            "Assignments must associate from the right.");
        AssertEx.Equal("=", nestedAssignment.Operator);
        AssertEx.Equal("first", ((SafeCoreNameExpressionSyntax)nestedAssignment.Left).Path);
        AssertEx.Equal("second", ((SafeCoreNameExpressionSyntax)nestedAssignment.Right).Path);
        return Task.CompletedTask;
    }

    private static Task RejectsDanglingPathPatternSeparatorAsync()
    {
        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(
            "fn main() { let Option:: = 1; }",
            "dangling-pattern-path.rs");
        AssertEx.False(result.IsSuccessful, "A path pattern ending in '::' must be rejected.");
        AssertEx.True(result.Root is null, "A dangling path pattern must not expose a syntax root.");
        Diagnostic diagnostic = result.Diagnostics.Single();
        AssertEx.Equal(SafeCoreSyntaxDiagnosticCodes.ExpectedToken, diagnostic.Code);
        AssertEx.Equal("=", result.GetText(diagnostic.Span));
        return Task.CompletedTask;
    }

    private static Task RejectsExternalModuleDeclarationsAsync()
    {
        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(
            "mod platform; fn main() {}",
            "external-module.rs");
        AssertEx.False(
            result.IsSuccessful,
            "An external module must not be modeled as an empty inline module.");
        AssertEx.True(result.Root is null, "An unsupported external module must not expose a syntax root.");
        Diagnostic diagnostic = result.Diagnostics.Single();
        AssertEx.Equal(SafeCoreSyntaxDiagnosticCodes.UnsupportedSyntax, diagnostic.Code);
        AssertEx.Equal(";", result.GetText(diagnostic.Span));
        return Task.CompletedTask;
    }

    private static Task ReportsMalformedAsync()
    {
        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse("fn broken(value: i32 { let x = 1; }", "malformed-safe-core.rs");
        AssertEx.False(result.IsSuccessful, "Malformed syntax must fail.");
        AssertEx.True(
            result.Diagnostics.Any(diagnostic => diagnostic.Code is SafeCoreSyntaxDiagnosticCodes.ExpectedToken or SafeCoreSyntaxDiagnosticCodes.UnterminatedConstruct or RustLexDiagnosticCodes.UnterminatedDelimiter),
            "Malformed syntax must carry a stable expected/unterminated diagnostic. Found: " +
            string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.Code)));
        foreach (Diagnostic diagnostic in result.Diagnostics)
        {
            AssertEx.True(diagnostic.Span.Start >= 0 && diagnostic.Span.End <= result.Source.Length, "Diagnostic spans must stay in source bounds.");
        }

        return Task.CompletedTask;
    }

    private static Task ObeysLimitsAsync()
    {
        const string source = "fn main() { let value = 1 + 2 + 3 + 4; }";
        var options = new SafeCoreSyntaxOptions
        {
            MaximumNodes = 4,
            MaximumOperations = 64,
            MaximumDiagnostics = 8,
        };
        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "bounded-safe-core.rs", options);
        AssertEx.True(result.IsTruncated, "A node limit must mark the result as truncated.");
        AssertEx.True(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreSyntaxDiagnosticCodes.LimitReached),
            "The bounded parser must expose RSP0002.");
        AssertEx.True(result.Diagnostics.Count <= options.MaximumDiagnostics, "Diagnostics must remain bounded.");
        return Task.CompletedTask;
    }

    private static Task CorpusAsync()
    {
        string root = FindRepositoryRoot();
        string manifestPath = Path.Combine(root, "tools", "RustSharp.Conformance", "fixtures", "safe-core-syntax-manifest.json");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement cases = manifest.RootElement.GetProperty("cases");
        AssertEx.Equal(6, cases.GetArrayLength());

        var inspected = 0;
        foreach (JsonElement item in cases.EnumerateArray())
        {
            inspected++;
            AssertEx.True(inspected <= 8, "The syntax corpus must remain explicitly bounded.");
            string fileName = item.GetProperty("file").GetString()!;
            string source = File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifestPath)!, fileName));
            SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, fileName);
            string expected = item.GetProperty("expected").GetString()!;
            if (expected == "parse-pass")
            {
                AssertSuccessful(result);
            }
            else
            {
                AssertEx.False(result.IsSuccessful, fileName + " must be rejected.");
                string diagnosticCode = item.GetProperty("diagnosticCode").GetString()!;
                AssertEx.True(
                    result.Diagnostics.Any(diagnostic => diagnostic.Code == diagnosticCode),
                    fileName + " must contain " + diagnosticCode + ".");
            }
        }

        return Task.CompletedTask;
    }

    private static string FindRepositoryRoot()
    {
        string current = Path.GetFullPath(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8; depth++)
        {
            if (File.Exists(Path.Combine(current, "RustSharp.slnx")))
            {
                return current;
            }

            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        throw new DirectoryNotFoundException("Could not locate the RustSharp repository root.");
    }

    private static void AssertSuccessful(SafeCoreSyntaxResult result)
    {
        AssertEx.True(
            result.IsSuccessful,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message + " [" + diagnostic.Span.Start + "," + diagnostic.Span.Length + "]")));
    }
}
