using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SyntaxExpressionExpansionTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("syntax expands struct literals members methods and casts", AggregatesAsync),
        new("syntax uses context rather than case to disambiguate struct literals", ConditionContextAsync),
        new("syntax preserves range and cast precedence", OperatorsAsync),
        new("syntax parses match arms guards and alternative patterns", MatchAsync),
        new("syntax parses loops labels and control-flow expressions", LoopsAsync),
        new("syntax separates closure delimiters from pattern alternatives", ClosuresAsync),
        new("syntax represents let chains let-else and item statements", StatementsAsync),
        new("syntax represents reference rest struct and range patterns", PatternsAsync),
        new("syntax rejects missing children and malformed expanded grammar", MalformedAsync),
        new("syntax bounds expanded expression and pattern recursion", BoundsAsync),
    ];

    private static Task AggregatesAsync()
    {
        var structure = (SafeCoreStructExpressionSyntax)Expression("model::point { #[field] x: 1, y, ..base }");
        AssertEx.Equal("model::point", structure.Path.Path);
        AssertEx.Equal("field", structure.Fields[0].Attributes.Single().Path);
        AssertEx.True(structure.Fields[1].IsShorthand && structure.Base is SafeCoreNameExpressionSyntax { Path: "base" }, "Struct fields retain shorthand and update forms.");
        var cast = (SafeCoreCastExpressionSyntax)Expression("point.method::<i32>(1)?.field as u64");
        AssertEx.True(cast.Expression is SafeCoreMemberExpressionSyntax { Target: SafeCoreTryExpressionSyntax { Operand: SafeCoreCallExpressionSyntax { Callee: SafeCoreMemberExpressionSyntax { Member: "method", HasGenericArguments: true } } } }, "Method calls, propagation and fields retain their nesting.");
        var tuple = (SafeCoreMemberExpressionSyntax)Expression("value.0.1");
        AssertEx.True(tuple is { Member: "1", Target: SafeCoreMemberExpressionSyntax { Member: "0" } }, "Joint float tokens split into tuple member accesses.");
        var call = (SafeCoreCallExpressionSyntax)Expression("module::f::<>()");
        AssertEx.True(call.Callee is SafeCoreNameExpressionSyntax name && name.Segments[1].HasGenericArguments && name.Segments[1].GenericArguments.Count == 0, "Empty turbofish is preserved.");
        AssertEx.True(Expression("<T as Trait>::make::<i32>()") is SafeCoreCallExpressionSyntax { Callee: SafeCoreQualifiedNameExpressionSyntax { TraitType: not null } }, "Qualified expression paths preserve their self and trait types.");
        return Task.CompletedTask;
    }

    private static Task ConditionContextAsync()
    {
        const string source = "fn f() { if Upper {} while lower {} for x in Iterator {} match Value { _ => 0 } if (lower { x: 1 }).x {} if check(lower { x: 1 }) {} }";
        SafeCoreSyntaxResult result = Pass(source);
        var body = ((SafeCoreFunctionSyntax)result.Root!.Items.Single()).Body;
        AssertEx.True(((SafeCoreExpressionStatementSyntax)body.Statements[0]).Expression is SafeCoreIfExpressionSyntax { Condition: SafeCoreNameExpressionSyntax { Path: "Upper" } }, "Uppercase condition is a name.");
        AssertEx.True(((SafeCoreExpressionStatementSyntax)body.Statements[1]).Expression is SafeCoreWhileExpressionSyntax { Condition: SafeCoreNameExpressionSyntax { Path: "lower" } }, "Lowercase condition is a name.");
        var parenthesized = (SafeCoreIfExpressionSyntax)((SafeCoreExpressionStatementSyntax)body.Statements[4]).Expression;
        AssertEx.True(parenthesized.Condition is SafeCoreMemberExpressionSyntax { Target: SafeCoreTupleExpressionSyntax tuple } && tuple.Elements.Single() is SafeCoreStructExpressionSyntax, "Parentheses restore the struct-literal context.");
        foreach (string sourceText in new[] { "fn f() { if lower { x: 1 } {} }", "fn f() { while lower { x: 1 } {} }", "fn f() { match lower { x: 1 } { _ => 0 } }", "fn f() { for x in lower { x: 1 } {} }" }) Fail(sourceText);
        return Task.CompletedTask;
    }

    private static Task OperatorsAsync()
    {
        var assignment = (SafeCoreBinaryExpressionSyntax)Expression("x = 1 + 2..=3 * 4");
        AssertEx.Equal("=", assignment.Operator);
        AssertEx.True(assignment.Right is SafeCoreRangeExpressionSyntax { IsInclusive: true, Start: SafeCoreBinaryExpressionSyntax { Operator: "+" }, End: SafeCoreBinaryExpressionSyntax { Operator: "*" } }, "Ranges bind above assignment and below arithmetic.");
        AssertEx.True(Expression("-x as i64 + y") is SafeCoreBinaryExpressionSyntax { Left: SafeCoreCastExpressionSyntax { Expression: SafeCoreUnaryExpressionSyntax } }, "Casts bind below unary and above addition.");
        AssertEx.True(Expression("a as i32 + b") is SafeCoreBinaryExpressionSyntax { Operator: "+", Left: SafeCoreCastExpressionSyntax }, "The cast type does not consume a plus bound.");
        AssertEx.True(Expression("..") is SafeCoreRangeExpressionSyntax { Start: null, End: null }, "Full ranges have no endpoints.");
        AssertEx.True(Expression("1..") is SafeCoreRangeExpressionSyntax { Start: not null, End: null }, "Range-from preserves its open endpoint.");
        return Task.CompletedTask;
    }

    private static Task MatchAsync()
    {
        const string text = "match input { #[arm] | Some(x) | Other(x) if x > 0 => x, None => { 0 } _ => -1 }";
        var match = (SafeCoreMatchExpressionSyntax)Expression(text);
        AssertEx.Equal(3, match.Arms.Count);
        AssertEx.True(match.Arms[0].Pattern is SafeCoreOrPatternSyntax { Alternatives.Count: 2 } && match.Arms[0].Guard is SafeCoreBinaryExpressionSyntax, "Or-pattern and guard are separate nodes.");
        AssertEx.Equal("arm", match.Arms[0].Attributes.Single().Path);
        AssertEx.True(match.Arms[1].Body is SafeCoreBlockExpressionSyntax, "A block arm may omit its comma.");
        return Task.CompletedTask;
    }

    private static Task LoopsAsync()
    {
        var loop = (SafeCoreLoopExpressionSyntax)Expression("'outer: loop { for (x, y) in 0..3 { if x == y { continue 'outer; } } break 'outer 4; }");
        AssertEx.Equal("'outer", loop.Label!);
        AssertEx.True(((SafeCoreExpressionStatementSyntax)loop.Body.Statements[0]).Expression is SafeCoreForExpressionSyntax { Pattern: SafeCoreTuplePatternSyntax, Iterator: SafeCoreRangeExpressionSyntax }, "For loops keep patterns and iterator expressions.");
        AssertEx.True(((SafeCoreExpressionStatementSyntax)loop.Body.Statements[1]).Expression is SafeCoreBreakExpressionSyntax { Label: "'outer", Value: SafeCoreLiteralExpressionSyntax }, "Break labels and values survive parsing.");
        AssertEx.True(Expression("'scope: { break 'scope 2; }") is SafeCoreLabeledBlockExpressionSyntax, "Labeled block expressions are retained.");
        AssertEx.True(Expression("call(return 1)") is SafeCoreCallExpressionSyntax call && call.Arguments.Single() is SafeCoreReturnExpressionSyntax, "Return is an expression in nested positions.");
        return Task.CompletedTask;
    }

    private static Task ClosuresAsync()
    {
        AssertEx.True(Expression("|| 1") is SafeCoreClosureExpressionSyntax { Parameters.Count: 0 }, "Joint pipes introduce a zero-parameter closure.");
        var closure = (SafeCoreClosureExpressionSyntax)Expression("move |x: i32, (Some(y) | Other(y)): E| -> i32 { x + y }");
        AssertEx.True(closure.IsMove && closure.Parameters.Count == 2 && closure.ReturnType is not null, "Typed move closures retain their signature.");
        AssertEx.True(closure.Parameters[1].Pattern is SafeCoreTuplePatternSyntax tuple && tuple.Elements.Single() is SafeCoreOrPatternSyntax, "Parentheses permit an alternative inside a closure parameter.");
        AssertEx.True(Expression("|x| x | 1") is SafeCoreClosureExpressionSyntax { Body: SafeCoreBinaryExpressionSyntax { Operator: "|" } }, "A body pipe is a binary operator.");
        AssertEx.True(Expression("call(|| 1, |x| x)") is SafeCoreCallExpressionSyntax { Arguments.Count: 2 }, "Closure bodies stop at call commas.");
        return Task.CompletedTask;
    }

    private static Task StatementsAsync()
    {
        var function = (SafeCoreFunctionSyntax)Pass("fn f() { #![scope] fn nested() {} #[local] let Some(x) = source else { return; }; if let Some(y) = source && y > 0 && let Some(z) = next { z; } while let Some(x) = source { break; }; ; }").Root!.Items.Single();
        AssertEx.Equal("scope", function.Body.Attributes.Single().Path);
        AssertEx.True(function.Body.Statements[0] is SafeCoreItemStatementSyntax { Item: SafeCoreFunctionSyntax }, "Block item declarations are statements.");
        AssertEx.True(function.Body.Statements[1] is SafeCoreLetStatementSyntax { ElseBlock: not null } local && local.Attributes.Single().Path == "local", "Let-else and outer attributes are retained.");
        AssertEx.True(((SafeCoreExpressionStatementSyntax)function.Body.Statements[2]).Expression is SafeCoreIfExpressionSyntax { Condition: SafeCoreBinaryExpressionSyntax { Operator: "&&", Right: SafeCoreLetExpressionSyntax } }, "Edition 2024 let-chains preserve their final binding.");
        AssertEx.True(function.Body.Statements[^1] is SafeCoreEmptyStatementSyntax, "Empty statements are represented.");
        var boundary = ((SafeCoreFunctionSyntax)Pass("fn f() { if flag {} -1; {} -2; let x = if flag { 1 } else { 2 } + 3; }").Root!.Items.Single()).Body;
        AssertEx.True(boundary.Statements.Count == 5 && ((SafeCoreExpressionStatementSyntax)boundary.Statements[1]).Expression is SafeCoreUnaryExpressionSyntax, "Completed block expressions end a statement before an infix-looking token.");
        var delimited = ((SafeCoreFunctionSyntax)Pass("fn f() { if flag {} (1); {} [2]; }").Root!.Items.Single()).Body;
        AssertEx.True(delimited.Statements.Count == 4 && ((SafeCoreExpressionStatementSyntax)delimited.Statements[1]).Expression is SafeCoreTupleExpressionSyntax &&
            ((SafeCoreExpressionStatementSyntax)delimited.Statements[3]).Expression is SafeCoreArrayExpressionSyntax, "Tuple and array tokens after a completed block start their own statements.");
        return Task.CompletedTask;
    }

    private static Task PatternsAsync()
    {
        var function = (SafeCoreFunctionSyntax)Pass("fn f() { let ref mut x = y; let &[head, ref tail @ ..] = values; let Pair { #[field] first: n @ 1..=9, ref mut second, .. } = value; let (a, .., b) = tuple; }").Root!.Items.Single();
        AssertEx.True(((SafeCoreLetStatementSyntax)function.Body.Statements[0]).Pattern is SafeCoreIdentifierPatternSyntax { IsByReference: true, IsMutable: true }, "Reference bindings keep both modifiers.");
        AssertEx.True(((SafeCoreLetStatementSyntax)function.Body.Statements[1]).Pattern is SafeCoreReferencePatternSyntax { Pattern: SafeCoreSlicePatternSyntax slice } && slice.Elements[1] is SafeCoreAtPatternSyntax { Pattern: SafeCoreRestPatternSyntax }, "Slice binding of the rest is structured.");
        var structure = (SafeCoreStructPatternSyntax)((SafeCoreLetStatementSyntax)function.Body.Statements[2]).Pattern;
        AssertEx.True(structure.HasRest && structure.Fields[0].Pattern is SafeCoreAtPatternSyntax { Pattern: SafeCoreRangePatternSyntax { IsInclusive: true } }, "Struct fields preserve binding and range patterns.");
        AssertEx.Equal("field", structure.Fields[0].Attributes.Single().Path);
        var qualified = (SafeCoreFunctionSyntax)Pass("fn f() { let Some::<T>(x) = value; match value { <T as Trait>::VALUE => 0, _ => 1 }; }").Root!.Items.Single();
        AssertEx.True(((SafeCoreLetStatementSyntax)qualified.Body.Statements[0]).Pattern is SafeCorePathPatternSyntax path && path.Segments.Single().HasGenericArguments, "Pattern turbofish arguments are retained.");
        Pass("fn f() { let (..) = tuple; let Empty() = empty; }");
        Pass("fn f() { let &(1..=3) = number; match number { 0 => {} -1 => {} _ => {} } }");
        return Task.CompletedTask;
    }

    private static Task MalformedAsync()
    {
        string[] sources =
        [
            "fn f() { let x: = 1; }", "fn f() { let x = ; }", "fn f() { let x else {}; }",
            "fn f() { let x = {} else {}; }", "fn f() { let x = a && b else {}; }", "fn f() { let x = a + {} else {}; }",
            "fn f() { call(,); }", "fn f() { [1; ]; }", "fn f() { value.field::<>; }", "fn f() { value.1u8; }",
            "fn f() { value::<>::<T>(); }", "fn f() { S { field: }; }", "fn f() { S { ..base, }; }",
            "fn f() { match value { _ => , } }", "fn f() { match value { _ => 1 _ => 2 } }",
            "fn f() { for x value {} }", "fn f() { |x| -> i32 1; }", "fn f() { |x,|; }",
            "fn f() { let (.., ..) = value; }", "fn f() { let .. = value; }", "fn f() { let 1 @ x = value; }",
            "fn f() { let S { .., x } = value; }", "fn f() { 1..=; }", "fn f() { 1..2..3; }",
            "fn f() { if let x = y || z {} }", "fn f() { if a && let x = y || z {} }", "fn f() { let x = (let y = z); }",
            "fn f() { let &1..=3 = number; }", "fn f() { let &..=3 = number; }",
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (string source in sources) { deadline.Token.ThrowIfCancellationRequested(); Fail(source, deadline.Token); }
        SafeCoreSyntaxResult macro = SafeCoreSyntax.Parse("fn f() { let pattern!() = value; }", "pattern-macro.rs", new SafeCoreSyntaxOptions { Timeout = TimeSpan.FromSeconds(1) }, deadline.Token);
        AssertEx.True(macro.Diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreSyntaxDiagnosticCodes.UnsupportedSyntax), "Excluded pattern macros receive an explicit unsupported diagnostic.");
        return Task.CompletedTask;
    }

    private static Task BoundsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Pass("fn f() { let &x = y; }");
        foreach (string source in new[]
        {
            "fn f() { let " + new string('&', 64) + "x = y; }",
            "fn f() { " + string.Concat(Enumerable.Repeat("loop {", 12)) + "break;" + new string('}', 12) + " }",
            "fn f() { " + string.Concat(Enumerable.Repeat("|x| ", 48)) + "x; }",
        })
        {
            SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "expanded-limits.rs", new SafeCoreSyntaxOptions { MaximumNestingDepth = 16, MaximumOperations = 4096, Timeout = TimeSpan.FromSeconds(1) }, deadline.Token);
            AssertEx.True(result.IsTruncated && result.Diagnostics.Any(static d => d.Code == "RSP0002"), "Expanded recursive grammar must observe depth limits.");
        }
        return Task.CompletedTask;
    }

    private static SafeCoreExpressionSyntax Expression(string expression) =>
        ((SafeCoreExpressionStatementSyntax)((SafeCoreFunctionSyntax)Pass($"fn f() {{ {expression}; }}").Root!.Items.Single()).Body.Statements.Single()).Expression;

    private static SafeCoreSyntaxResult Pass(string source)
    {
        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "expression-expansion.rs", new SafeCoreSyntaxOptions { Timeout = TimeSpan.FromSeconds(1) });
        AssertEx.True(result.IsSuccessful, source + ": " + string.Join("; ", result.Diagnostics.Select(static d => d.Code + " " + d.Message)));
        AssertEx.Equal(source, result.LexResult.ToSourceText());
        return result;
    }

    private static void Fail(string source, CancellationToken cancellationToken = default)
    {
        SafeCoreSyntaxResult result = SafeCoreSyntax.Parse(source, "expression-expansion-fail.rs", new SafeCoreSyntaxOptions { Timeout = TimeSpan.FromSeconds(1) }, cancellationToken);
        AssertEx.True(!result.IsSuccessful && result.Root is null && result.Diagnostics.Count > 0 && !result.IsTruncated, source + " must produce a grammar diagnostic.");
        AssertEx.True(result.Diagnostics.All(d => d.Span.Start >= 0 && d.Span.End <= source.Length), "Expanded diagnostics must remain inside the source.");
    }
}
