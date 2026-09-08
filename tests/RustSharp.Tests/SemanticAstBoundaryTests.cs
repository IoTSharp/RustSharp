using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SemanticAstBoundaryTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("public AST collection validates structured type arguments", RejectsHiddenTypeArgumentsAsync),
        new("public AST collection validates function path components without flags", RejectsHiddenFunctionArgumentsAsync),
        new("public AST collection validates pattern arguments without flags", RejectsHiddenPatternArgumentsAsync),
        new("public AST collection rejects hidden receiver and const parameter syntax", RejectsHiddenDeclarationPropertiesAsync),
    ];

    private static Task RejectsHiddenTypeArgumentsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreCompilationUnitSyntax root = ParseRoot("type Alias = i32;", deadline.Token);
        AssertEx.True(SafeCoreNameResolution.Collect(root).IsSuccessful, "The minimal public AST must resolve before checking extensions.");
        var alias = (SafeCoreTypeAliasSyntax)root.Items[0];
        var path = (SafeCorePathTypeSyntax)alias.Type;
        SafeCorePathTypeSyntax hidden = path with
        {
            Segments = [path.Segments[0] with
            {
                Arguments = [new SafeCoreTypeArgumentSyntax(new SafeCoreFunctionTypeSyntax([], [], null, path.Span), path.Span)],
            }],
        };
        AssertRejected(root with { Items = [alias with { Type = hidden }] }, deadline.Token);
        AssertRejected(DeferredBound(hidden, deadline.Token), deadline.Token);

        SafeCorePathTypeSyntax independent = path with
        {
            Segments = [path.Segments[0] with
            {
                GenericArguments = [path],
                Arguments = [new SafeCoreTypeArgumentSyntax(new SafeCoreInferredTypeSyntax(path.Span), path.Span)],
            }],
        };
        AssertRejected(root with { Items = [alias with { Type = independent }] }, deadline.Token);
        AssertRejected(DeferredBound(independent, deadline.Token), deadline.Token);
        return Task.CompletedTask;
    }

    private static Task RejectsHiddenFunctionArgumentsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreCompilationUnitSyntax root = ParseRoot("type Alias = i32;", deadline.Token);
        var alias = (SafeCoreTypeAliasSyntax)root.Items[0];
        var path = (SafeCorePathTypeSyntax)alias.Type;
        SafeCorePathTypeSyntax hiddenInputs = path with
        {
            Segments = [path.Segments[0] with { FunctionParameters = [path] }],
        };
        SafeCorePathTypeSyntax hiddenOutput = path with
        {
            Segments = [path.Segments[0] with { FunctionReturnType = path }],
        };
        AssertRejected(root with { Items = [alias with { Type = hiddenInputs }] }, deadline.Token);
        AssertRejected(root with { Items = [alias with { Type = hiddenOutput }] }, deadline.Token);
        AssertRejected(DeferredBound(hiddenInputs, deadline.Token), deadline.Token);
        AssertRejected(DeferredBound(hiddenOutput, deadline.Token), deadline.Token);
        return Task.CompletedTask;
    }

    private static Task RejectsHiddenPatternArgumentsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreCompilationUnitSyntax root = ParseRoot("enum E { V } fn f() { let E::V = 1; }", deadline.Token);
        var function = (SafeCoreFunctionSyntax)root.Items[1];
        var statement = (SafeCoreLetStatementSyntax)function.Body.Statements[0];
        var pattern = (SafeCorePathPatternSyntax)statement.Pattern;
        SafeCorePathPatternSyntax hidden = pattern with
        {
            Segments = [new SafeCoreExpressionPathSegmentSyntax("V", [new SafeCoreLifetimeArgumentSyntax("'static", pattern.Span)], pattern.Span)],
        };
        SafeCoreBlockSyntax body = function.Body with { Statements = [statement with { Pattern = hidden }] };
        AssertRejected(root with { Items = [root.Items[0], function with { Body = body }] }, deadline.Token);
        var array = new SafeCoreArrayTypeSyntax(
            new SafeCorePathTypeSyntax([new SafeCorePathSegmentSyntax("i32", [], pattern.Span)], pattern.Span),
            new SafeCoreBlockExpressionSyntax(body, body.Span), body.Span);
        AssertRejected(DeferredBound(array, deadline.Token), deadline.Token);
        return Task.CompletedTask;
    }

    private static Task RejectsHiddenDeclarationPropertiesAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SafeCoreCompilationUnitSyntax root = ParseRoot("fn f<T>(value: i32) {}", deadline.Token);
        var function = (SafeCoreFunctionSyntax)root.Items[0];
        SafeCoreParameterSyntax parameter = function.Parameters[0];
        AssertRejected(root with { Items = [function with
        {
            Parameters = [parameter with { Receiver = new SafeCoreReceiverSyntax(false, false, null, null, parameter.Span) }],
        }] }, deadline.Token);
        AssertRejected(root with { Items = [function with
        {
            GenericParameters = [function.GenericParameters[0] with { ConstType = parameter.Type }],
        }] }, deadline.Token);
        return Task.CompletedTask;
    }

    private static SafeCoreCompilationUnitSyntax DeferredBound(SafeCoreTypeSyntax bound, CancellationToken cancellationToken)
    {
        SafeCoreCompilationUnitSyntax root = ParseRoot("struct S<T>;", cancellationToken);
        var structure = (SafeCoreStructSyntax)root.Items[0];
        return root with { Items = [structure with
        {
            GenericParameters = [structure.GenericParameters[0] with { Bounds = [bound] }],
        }] };
    }

    private static SafeCoreCompilationUnitSyntax ParseRoot(string source, CancellationToken cancellationToken)
    {
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "semantic-ast-boundary.rs", null, cancellationToken);
        AssertEx.True(syntax.IsSuccessful, source + ": " + string.Join("; ", syntax.Diagnostics));
        return syntax.Root!;
    }

    private static void AssertRejected(SafeCoreCompilationUnitSyntax root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SafeCoreNameResolutionResult result = SafeCoreNameResolution.Collect(root, options: new SafeCoreNameResolutionOptions
        {
            MaximumOperations = 2_000,
            MaximumNestingDepth = 32,
        });
        cancellationToken.ThrowIfCancellationRequested();
        AssertEx.False(result.IsSuccessful, "The public AST overload must reject semantics hidden in structured properties.");
        AssertEx.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax),
            "Unsupported AST properties must produce RSN1007: " + string.Join("; ", result.Diagnostics));
    }
}
