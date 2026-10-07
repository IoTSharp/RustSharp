using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Compiler;

/// <summary>Projects admitted source declarations to equal-length text before binding or module discovery.</summary>
public static class CargoCfgSourceSelector
{
    public static CargoCfgSourceResult Select(string source, string sourcePath, CargoCfgEnvironment environment,
        CargoWorkspaceOptions? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath); ArgumentNullException.ThrowIfNull(environment);
        cancellationToken.ThrowIfCancellationRequested();
        CargoWorkspaceOptions normalized = CargoWorkspace.NormalizeV1Options(limits ?? new());
        return SelectCore(source, sourcePath, environment, normalized, new(normalized, cancellationToken), cancellationToken);
    }

    internal static CargoCfgSourceResult SelectCore(string source, string sourcePath, CargoCfgEnvironment environment,
        CargoWorkspaceOptions limits, CargoLoadBudget budget, CancellationToken cancellationToken)
    {
        try
        {
            budget.Step(sourcePath, default);
            if (source.Length > limits.MaximumManifestBytes || Encoding.UTF8.GetByteCount(source) > limits.MaximumManifestBytes)
                throw new CargoLoadException(CargoWorkspace.LimitDiagnostic, "Cargo cfg source exceeds its input byte bound.", sourcePath, default);
            SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, sourcePath, new SafeCoreSyntaxOptions
            {
                Timeout = limits.Timeout, MaximumSourceLength = limits.MaximumManifestBytes,
                MaximumOperations = limits.MaximumOperations, MaximumTokens = limits.MaximumOperations,
                MaximumNodes = limits.MaximumOperations,
            }, cancellationToken);
            budget.Check(sourcePath, default);
            if (!syntax.IsSuccessful)
            {
                ValidateCfgTokens(source, sourcePath, syntax, limits, budget);
                Diagnostic[] diagnostics = syntax.Diagnostics.Select(d => d with
                {
                    Code = d.Code is SafeCoreSyntaxDiagnosticCodes.LimitReached or SafeCoreSyntaxDiagnosticCodes.LexicalTruncation ? CargoWorkspace.LimitDiagnostic : d.Code,
                    SourcePath = sourcePath,
                }).ToArray();
                return new(string.Empty, Array.AsReadOnly(diagnostics), budget.OperationsConsumed);
            }
            foreach (RustToken token in syntax.LexResult.Tokens) budget.Step(sourcePath, token.Span);
            return new Projection(source, sourcePath, environment, limits, budget, syntax).Run();
        }
        catch (CargoLoadException exception)
        { return new(string.Empty, Array.AsReadOnly(new[] { new Diagnostic(exception.Code, exception.Message, exception.Span) { SourcePath = exception.SourcePath } }), budget.OperationsConsumed); }
    }

    private static void ValidateCfgTokens(string source, string path, SafeCoreSyntaxResult syntax, CargoWorkspaceOptions limits, CargoLoadBudget budget)
    {
        IReadOnlyList<RustToken> tokens = syntax.LexResult.Tokens;
        for (int index = 0; index < tokens.Count && index < limits.MaximumOperations; index++)
        {
            budget.Step(path, tokens[index].Span);
            if (tokens[index].Text != "#") continue;
            int bracket = index + 1;
            if (bracket < tokens.Count && tokens[bracket].Text == "!") bracket++;
            int name = bracket + 1;
            if (name >= tokens.Count || tokens[bracket].Text != "[" || tokens[name].Text != "cfg") continue;
            int close = -1;
            for (int scanned = name + 1; scanned < tokens.Count && scanned < limits.MaximumOperations; scanned++)
            {
                budget.Step(path, tokens[scanned].Span);
                if (tokens[scanned].Text == "]") { close = scanned; break; }
            }
            if (close < 0)
                throw new CargoLoadException(CargoCfgResolver.InvalidCfgDiagnostic, "Unterminated source cfg attribute.", path,
                    new(tokens[index].Span.Start, source.Length - tokens[index].Span.Start));
            TextSpan attribute = new(tokens[index].Span.Start, tokens[close].Span.End - tokens[index].Span.Start);
            string arguments = source[tokens[name].Span.End..tokens[close].Span.Start];
            _ = new CargoCfgPredicateParser(arguments, path, attribute, limits, budget).Parse(attributeArguments: true);
            index = close;
        }
    }

    private sealed record Work(object Node, bool CommaTerminated = false);
    private sealed class Projection(string source, string path, CargoCfgEnvironment environment,
        CargoWorkspaceOptions limits, CargoLoadBudget budget, SafeCoreSyntaxResult syntax)
    {
        private readonly List<TextSpan> _masked = [];
        private readonly Stack<Work> _work = new();
        internal CargoCfgSourceResult Run()
        {
            _work.Push(new(syntax.Root!));
            for (int index = 0; _work.Count > 0 && index < limits.MaximumOperations; index++)
            {
                Work work = _work.Pop(); TextSpan span = SpanOf(work.Node); budget.Step(path, span);
                IReadOnlyList<SafeCoreAttributeSyntax> attributes = AttributesOf(work.Node);
                bool admitted = Evaluate(attributes);
                IReadOnlyList<SafeCoreAttributeSyntax> inner = work.Node switch
                {
                    SafeCoreModuleSyntax module => module.InnerAttributes,
                    SafeCoreTraitSyntax trait => trait.InnerAttributes,
                    SafeCoreImplSyntax implementation => implementation.InnerAttributes,
                    _ => Array.Empty<SafeCoreAttributeSyntax>(),
                };
                admitted &= Evaluate(inner);
                if (!admitted)
                {
                    int start = attributes.Count == 0 ? span.Start : Math.Min(span.Start, attributes.Min(static attribute => attribute.Span.Start));
                    int end = span.End;
                    if (work.CommaTerminated)
                    {
                        foreach (RustToken token in syntax.LexResult.Tokens)
                        {
                            budget.Step(path, token.Span);
                            if (token.Span.Start < end) continue;
                            if (token.Text == ",") end = token.Span.End;
                            break;
                        }
                    }
                    Mask(new(start, end - start));
                }
                PushChildren(work.Node); // Validate nested conditions even inside disabled declarations.
            }
            if (_work.Count != 0) throw new CargoLoadException(CargoWorkspace.LimitDiagnostic, "Cargo source cfg traversal exceeded its work limit.", path, default);
            char[] projected = source.ToCharArray();
            foreach (TextSpan span in _masked.OrderBy(static span => span.Start))
            {
                budget.Step(path, span);
                for (int offset = span.Start; offset < span.End && offset < limits.MaximumManifestBytes; offset++)
                {
                    if ((offset & 255) == 0) budget.Check(path, span);
                    if (projected[offset] is not '\r' and not '\n') projected[offset] = ' ';
                }
            }
            budget.Check(path, default);
            return new(new string(projected), Array.Empty<Diagnostic>(), budget.OperationsConsumed);
        }
        private bool Evaluate(IReadOnlyList<SafeCoreAttributeSyntax> attributes)
        {
            bool admitted = true;
            foreach (SafeCoreAttributeSyntax attribute in attributes)
            {
                budget.Step(path, attribute.Span);
                if (attribute.Path == "cfg_attr") throw new CargoLoadException(CargoCfgResolver.InvalidCfgDiagnostic, "cfg_attr is outside the frozen cargo-v1 cfg forms.", path, attribute.Span);
                if (attribute.Path != "cfg" || attribute.IsDocumentation) continue;
                CargoCfgPredicate predicate = new CargoCfgPredicateParser(attribute.ArgumentsText, path, attribute.Span, limits, budget).Parse(attributeArguments: true);
                admitted &= predicate.Evaluate(environment, budget, path, attribute.Span);
                Mask(attribute.Span);
            }
            return admitted;
        }
        private void Mask(TextSpan span)
        {
            budget.Step(path, span);
            if (span.Start < 0 || span.End > source.Length || _masked.Count >= limits.MaximumOperations)
                throw new CargoLoadException(CargoWorkspace.LimitDiagnostic, "Cargo source cfg spans exceeded their item or source bound.", path, span);
            _masked.Add(span);
        }
        private void Push(object node, bool comma = false)
        {
            budget.Step(path, SpanOf(node));
            if (_work.Count >= limits.MaximumOperations) throw new CargoLoadException(CargoWorkspace.LimitDiagnostic, "Cargo source cfg queue exceeded its item bound.", path, SpanOf(node));
            _work.Push(new(node, comma));
        }
        private void PushChildren(object node)
        {
            switch (node)
            {
                case SafeCoreCompilationUnitSyntax unit: foreach (SafeCoreItemSyntax item in unit.Items) Push(item); break;
                case SafeCoreModuleSyntax module: foreach (SafeCoreItemSyntax item in module.Items) Push(item); break;
                case SafeCoreTraitSyntax trait: foreach (SafeCoreAssociatedItemSyntax item in trait.Items) Push(item); break;
                case SafeCoreImplSyntax implementation: foreach (SafeCoreAssociatedItemSyntax item in implementation.Items) Push(item); break;
                case SafeCoreStructSyntax structure: foreach (SafeCoreFieldSyntax field in structure.Fields) Push(field, true); break;
                case SafeCoreEnumSyntax enumeration: foreach (SafeCoreEnumVariantSyntax variant in enumeration.Variants) Push(variant, true); break;
                case SafeCoreEnumVariantSyntax variant: foreach (SafeCoreFieldSyntax field in variant.Fields) Push(field, true); break;
                case SafeCoreFunctionSyntax function: foreach (SafeCoreParameterSyntax parameter in function.Parameters) Push(parameter, true); break;
                case SafeCoreAssociatedFunctionSyntax function: foreach (SafeCoreParameterSyntax parameter in function.Parameters) Push(parameter, true); break;
            }
        }
        private static IReadOnlyList<SafeCoreAttributeSyntax> AttributesOf(object node) => node switch
        {
            SafeCoreCompilationUnitSyntax unit => unit.Attributes, SafeCoreItemSyntax item => item.Attributes,
            SafeCoreAssociatedItemSyntax item => item.Attributes, SafeCoreFieldSyntax field => field.Attributes,
            SafeCoreEnumVariantSyntax variant => variant.Attributes, SafeCoreParameterSyntax parameter => parameter.Attributes,
            _ => throw new InvalidOperationException("Unexpected source cfg declaration node."),
        };
        private static TextSpan SpanOf(object node) => node switch
        {
            SafeCoreCompilationUnitSyntax unit => unit.Span, SafeCoreItemSyntax item => item.Span,
            SafeCoreAssociatedItemSyntax item => item.Span, SafeCoreFieldSyntax field => field.Span,
            SafeCoreEnumVariantSyntax variant => variant.Span, SafeCoreParameterSyntax parameter => parameter.Span,
            _ => throw new InvalidOperationException("Unexpected source cfg declaration node."),
        };
    }
}
