using System.Diagnostics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SyntaxGrammarTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("syntax validates import paths without joining unrelated tokens", ImportsAsync),
        new("syntax validates attribute paths arguments and placement", AttributesAsync),
        new("syntax splits nested generic closers with exact spans", GenericClosersAsync),
        new("syntax preserves Rust bitwise and comparison precedence", PrecedenceAsync),
        new("syntax represents tuple fields unit structs and variant spans", ItemsAsync),
        new("syntax preserves nested references and negative patterns", ReferencesAndPatternsAsync),
        new("syntax rejects malformed expressions and generic parameters", MalformedAsync),
        new("syntax explicitly rejects unsupported profile forms", UnsupportedAsync),
        new("syntax bounds recovery operations nodes and else-if nesting", LimitsAsync),
        new("syntax supports cancellation and wall-clock deadlines", CancellationAsync),
        new("syntax handles deterministic malformed token combinations", MalformedCorpusAsync),
    ];

    private static Task ImportsAsync()
    {
        SafeCoreSyntaxResult parsed = Pass("use crate :: model /* comment */ :: Item as Alias;");
        var import = (SafeCoreUseSyntax)parsed.Root!.Items.Single();
        AssertEx.Equal("crate::model::Item", import.Path);
        AssertEx.Equal("Alias", import.Alias!);
        foreach (string source in new[] { "use foo bar;", "use foo::;", "use foo as A as B;", "use foo as A bar;", "use ();", "use fn;" })
        {
            Fail(source);
        }

        return Task.CompletedTask;
    }

    private static Task AttributesAsync()
    {
        SafeCoreSyntaxResult result = Pass("#![no_std] #[tool /* c */ :: hint(a, [b], {c})] #[doc = \"text\"] fn f() {}");
        AssertEx.Equal("no_std", result.Root!.Attributes.Single().Path);
        var function = (SafeCoreFunctionSyntax)result.Root.Items.Single();
        AssertEx.Equal("tool::hint", function.Attributes[0].Path);
        AssertEx.Equal("(a, [b], {c})", function.Attributes[0].ArgumentsText);
        AssertEx.Equal("= \"text\"", function.Attributes[1].ArgumentsText);
        foreach (string source in new[] { "#[] fn f() {}", "#[123] fn f() {}", "#[a b] fn f() {}", "#[a::] fn f() {}", "#[a(x) y] fn f() {}", "#[a =] fn f() {}", "#[a] #![b] fn f() {}", "fn f() {} #![b] fn g() {}" })
        {
            Fail(source);
        }

        return Task.CompletedTask;
    }

    private static Task GenericClosersAsync()
    {
        const string source = "type A<T: Bound<Vec<i32>>> = Outer<Inner<T>>; fn f() { let a: Outer<Inner<i32>>=value; a >> 2; a >>= 1; }";
        SafeCoreSyntaxResult result = Pass(source);
        var alias = (SafeCoreTypeAliasSyntax)result.Root!.Items[0];
        var outer = (SafeCorePathTypeSyntax)alias.Type;
        var inner = (SafeCorePathTypeSyntax)outer.Segments[0].GenericArguments[0];
        AssertEx.Equal("Outer<Inner<T>>", result.GetText(outer.Segments[0].Span));
        AssertEx.Equal("Inner<T>", result.GetText(inner.Span));
        AssertEx.Equal("Bound<Vec<i32>>", result.GetText(alias.GenericParameters[0].Bounds[0].Span));
        var function = (SafeCoreFunctionSyntax)result.Root.Items[1];
        var local = (SafeCoreLetStatementSyntax)function.Body.Statements[0];
        AssertEx.Equal("Outer<Inner<i32>>", result.GetText(local.Type!.Span));
        AssertEx.True(local.Initializer is SafeCoreNameExpressionSyntax { Path: "value" }, "The '=' suffix of '>>=' must remain available.");
        AssertEx.Equal(">>", ((SafeCoreBinaryExpressionSyntax)((SafeCoreExpressionStatementSyntax)function.Body.Statements[1]).Expression).Operator);
        AssertEx.Equal(">>=", ((SafeCoreBinaryExpressionSyntax)((SafeCoreExpressionStatementSyntax)function.Body.Statements[2]).Expression).Operator);
        Pass("fn f() { let a: Vec<i32>=value; } type E = Vec<>;");
        Pass("fn empty<>() {} fn empty_bound<T:>() {} fn trailing_bound<T: Copy+>() {}");
        Fail("type A = Outer<T>>;");
        return Task.CompletedTask;
    }

    private static Task PrecedenceAsync()
    {
        SafeCoreBinaryExpressionSyntax comparison = (SafeCoreBinaryExpressionSyntax)Expression("a | b == c & d");
        AssertEx.Equal("==", comparison.Operator);
        AssertEx.Equal("|", ((SafeCoreBinaryExpressionSyntax)comparison.Left).Operator);
        AssertEx.Equal("&", ((SafeCoreBinaryExpressionSyntax)comparison.Right).Operator);
        SafeCoreBinaryExpressionSyntax shift = (SafeCoreBinaryExpressionSyntax)Expression("a << b + c * d");
        AssertEx.Equal("<<", shift.Operator);
        AssertEx.Equal("+", ((SafeCoreBinaryExpressionSyntax)shift.Right).Operator);
        AssertEx.True(((SafeCoreBinaryExpressionSyntax)shift.Right).Right is SafeCoreBinaryExpressionSyntax { Operator: "*" }, "Multiplication binds before addition.");
        foreach (string source in new[] { "a < b == c", "a == b < c", "a < b < c" })
        {
            Fail($"fn f() {{ {source}; }}");
        }

        Pass("fn f() { (a < b) == c; a == (b < c); }");
        return Task.CompletedTask;
    }

    private static Task ItemsAsync()
    {
        SafeCoreSyntaxResult result = Pass("struct Unit; struct Empty {} struct Tuple(pub i32, bool); enum E { Empty(), Pair(i32, bool), Unit }");
        AssertEx.True(result.Root!.Items[0] is SafeCoreStructSyntax { IsUnitStruct: true }, "Unit structs need a distinct form.");
        AssertEx.True(result.Root.Items[1] is SafeCoreStructSyntax { IsUnitStruct: false, IsTupleStruct: false }, "Braced empty structs stay distinct.");
        var tuple = (SafeCoreStructSyntax)result.Root.Items[2];
        AssertEx.True(tuple.IsTupleStruct && tuple.Fields[0].IsPublic && !tuple.Fields[1].IsPublic, "Tuple field visibility must be retained.");
        var variants = ((SafeCoreEnumSyntax)result.Root.Items[3]).Variants;
        AssertEx.Equal("Empty()", result.GetText(variants[0].Span));
        AssertEx.Equal("Pair(i32, bool)", result.GetText(variants[1].Span));
        return Task.CompletedTask;
    }

    private static Task ReferencesAndPatternsAsync()
    {
        var function = (SafeCoreFunctionSyntax)Pass("fn f(a: &&'static mut i32) { let -1 = a; return }").Root!.Items.Single();
        AssertEx.True(function.Parameters[0].Type is SafeCoreReferenceTypeSyntax { IsMutable: false, Inner: SafeCoreReferenceTypeSyntax { Lifetime: "'static", IsMutable: true } }, "Joint ampersands represent nested reference types.");
        AssertEx.True(Expression("&&mut a") is SafeCoreUnaryExpressionSyntax { Operator: "&", Operand: SafeCoreUnaryExpressionSyntax { Operator: "&mut" } }, "Joint ampersands represent two borrows.");
        AssertEx.True(function.Body.Statements[0] is SafeCoreLetStatementSyntax { Pattern: SafeCoreLiteralPatternSyntax { RawText: "-1" } }, "Negative literals remain patterns.");
        AssertEx.True(function.Body.Statements[1] is SafeCoreReturnStatementSyntax { Value: null }, "A final return need not have a semicolon.");
        Pass("fn f() -> i32 { return 1 }");
        return Task.CompletedTask;
    }

    private static Task MalformedAsync()
    {
        foreach (string source in new[] { "fn f() { +1; }", "fn f() { [1, 2; 3]; }", "fn f() { [1; ]; }", "fn f() { let x = ; }", "fn f() { call(,); }", "fn f() { let () = (;); }", "fn f<T: Copy++>() {}", "fn f<T T>() {}", "fn _() {}", "fn f() { _; }", "type A = ;", "const X: i32 =" })
        {
            Fail(source);
        }

        return Task.CompletedTask;
    }

    private static Task UnsupportedAsync()
    {
        foreach (string source in new[]
        {
            "fn f() { other!(); }", "type P = *const i32;", "unsafe fn f() {}",
            "extern \"C\" { fn f(); }", "async fn f() {}", "union U { x: i32 }",
        })
        {
            SafeCoreSyntaxResult result = Fail(source);
            AssertEx.True(result.Diagnostics.Any(d => d.Code == SafeCoreSyntaxDiagnosticCodes.UnsupportedSyntax), source + " needs RSP1003.");
        }

        return Task.CompletedTask;
    }
    private static Task LimitsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Each pass consumes at most 96 operations; the four-budget smoke precedes the full sweep.
        foreach (int maximum in new[] { 4, 96 })
        {
            for (int budget = 1; budget <= maximum; budget++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                SafeCoreSyntaxResult result = SafeCoreSyntax.Parse("fn f(a: (i32,)) { let x = [1, 2]; x[0] + 1; }", "limits.rs",
                    new SafeCoreSyntaxOptions { MaximumOperations = budget }, deadline.Token);
                AssertEx.True(result.IsSuccessful || result.IsTruncated, "Budget exhaustion must return a bounded result without throwing implementation errors.");
                SafeCoreSyntaxResult recovery = SafeCoreSyntax.Parse("fn f(a: [;]) {}", "recovery.rs",
                    new SafeCoreSyntaxOptions { MaximumNodes = budget, MaximumDiagnostics = 4, MaximumOperations = 96 }, deadline.Token);
                AssertEx.False(recovery.IsSuccessful, "Malformed recovery must terminate.");
            }
        }

        string chain = "fn f() { " + string.Concat(Enumerable.Repeat("if true {} else ", 64)) + "{} }";
        SafeCoreSyntaxResult nested = SafeCoreSyntax.Parse(chain, "else-if.rs", new SafeCoreSyntaxOptions { MaximumNestingDepth = 16 }, deadline.Token);
        AssertEx.True(nested.IsTruncated && nested.Diagnostics.Any(d => d.Code == "RSP0002"), "Else-if recursion must obey syntax depth bounds.");
        SafeCoreSyntaxResult lexical = SafeCoreSyntax.Parse("fn f() {}", "tokens.rs", new SafeCoreSyntaxOptions { MaximumTokens = 1, MaximumDiagnostics = 1 });
        AssertEx.True(lexical.IsTruncated && lexical.Diagnostics.Count == 1, "Lexical truncation must not overflow the diagnostic budget.");
        return Task.CompletedTask;
    }

    private static Task CancellationAsync()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool cancellationObserved = false;
        try { SafeCoreSyntax.Parse("fn f() {}", null, null, cancelled.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        AssertEx.True(cancellationObserved, "Pre-cancellation must propagate to the lexical pass.");
        bool timeoutObserved = false;
        try { SafeCoreSyntax.Parse("fn f() {}", null, new SafeCoreSyntaxOptions { Timeout = TimeSpan.FromTicks(1) }); }
        catch (TimeoutException) { timeoutObserved = true; }
        AssertEx.True(timeoutObserved, "The deadline must apply to the entire parse.");
        return Task.CompletedTask;
    }

    private static Task MalformedCorpusAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string[] tokens = ["fn", "(", ")", "{", "}", "[", "]", ";", ",", "let", "=", "<", ">>", "a", "1", "#"];
        var random = new Random(102);
        for (int index = 0; index < 128; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            string source = "fn f() { " + string.Join(" ", Enumerable.Range(0, 16).Select(_ => tokens[random.Next(tokens.Length)])) + " }";
            SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "generated.rs", new SafeCoreSyntaxOptions { MaximumOperations = 2048, MaximumDiagnostics = 8 }, deadline.Token);
            AssertEx.True(result.Diagnostics.Count <= 8, "Generated diagnostic output remains bounded.");
            AssertEx.True(result.Diagnostics.All(d => d.Span.Start >= 0 && d.Span.End <= source.Length), "Diagnostic spans remain inside the original source.");
        }

        return Task.CompletedTask;
    }

    private static SafeCoreExpressionSyntax Expression(string source) =>
        ((SafeCoreExpressionStatementSyntax)((SafeCoreFunctionSyntax)Pass($"fn f() {{ {source}; }}").Root!.Items.Single()).Body.Statements.Single()).Expression;

    private static SafeCoreSyntaxResult Pass(string source)
    {
        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source);
        AssertEx.True(result.IsSuccessful, source + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
        AssertEx.Equal(source, result.LexResult.ToSourceText());
        return result;
    }

    private static SafeCoreSyntaxResult Fail(string source)
    {
        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source);
        AssertEx.True(!result.IsSuccessful && result.Root is null && result.Diagnostics.Count > 0, source + " must produce diagnostics and no AST.");
        AssertEx.False(result.IsTruncated, source + " must fail on grammar, not a work limit.");
        return result;
    }
}
