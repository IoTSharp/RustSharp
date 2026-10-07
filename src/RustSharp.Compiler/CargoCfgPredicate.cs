using RustSharp.Syntax;

namespace RustSharp.Compiler;

internal sealed record CargoCfgPredicate(string Name, string? Value, IReadOnlyList<CargoCfgPredicate> Children)
{
    internal bool Evaluate(CargoCfgEnvironment environment, CargoLoadBudget budget, string path, TextSpan span)
    {
        budget.Step(path, span);
        return Name switch
        {
            "windows" => environment.TargetOs == "windows",
            "unix" => environment.TargetOs == "linux",
            "target_os" => environment.TargetOs == Value,
            "target_arch" => environment.TargetArch == Value,
            "feature" => environment.Features.Contains(Value!),
            "all" => All(environment, budget, path, span),
            "any" => Any(environment, budget, path, span),
            "not" => !Children[0].Evaluate(environment, budget, path, span),
            _ => throw new InvalidOperationException("Only validated cfg predicates may be evaluated."),
        };
    }
    private bool All(CargoCfgEnvironment environment, CargoLoadBudget budget, string path, TextSpan span)
    { foreach (CargoCfgPredicate child in Children) if (!child.Evaluate(environment, budget, path, span)) return false; return true; }
    private bool Any(CargoCfgEnvironment environment, CargoLoadBudget budget, string path, TextSpan span)
    { foreach (CargoCfgPredicate child in Children) if (child.Evaluate(environment, budget, path, span)) return true; return false; }
}

internal sealed class CargoCfgPredicateParser(string text, string path, TextSpan span, CargoWorkspaceOptions limits, CargoLoadBudget budget)
{
    private int _position;
    internal CargoCfgPredicate Parse(bool attributeArguments = false)
    {
        if (text.Length > limits.MaximumManifestBytes) Limit("Cargo cfg expression exceeds the input bound.");
        SkipSpace();
        if (!attributeArguments && ReadName() != "cfg") Invalid("A conditional Cargo dependency requires cfg(...).");
        Expect('(');
        CargoCfgPredicate predicate = ReadPredicate(1);
        Expect(')'); SkipSpace();
        if (_position != text.Length) Invalid("Unexpected text after cfg predicate.");
        return predicate;
    }
    private CargoCfgPredicate ReadPredicate(int depth)
    {
        budget.Step(path, span);
        if (depth > limits.MaximumCfgDepth) Limit("Cargo cfg predicate exceeds its depth limit.");
        string name = ReadName(); SkipSpace();
        if (name is "all" or "any" or "not")
        {
            Expect('('); var children = new List<CargoCfgPredicate>(); SkipSpace();
            for (int index = 0; Peek() != ')' && index < limits.MaximumOperations; index++)
            {
                budget.Step(path, span);
                children.Add(ReadPredicate(depth + 1)); SkipSpace();
                if (Peek() == ')') break;
                Expect(','); SkipSpace();
            }
            if (Peek() != ')') Limit("Cargo cfg arguments exceeded their work limit.");
            Expect(')');
            if (name == "not" && children.Count != 1) Invalid("not requires exactly one cfg predicate.");
            return new(name, null, children.AsReadOnly());
        }
        if (name is "windows" or "unix") return new(name, null, Array.Empty<CargoCfgPredicate>());
        if (name is not "target_os" and not "target_arch" and not "feature") Invalid("Unsupported cfg predicate '" + name + "'.");
        Expect('='); string value = ReadString();
        if (name == "target_os" && value is not "windows" and not "linux" || name == "target_arch" && value != "x86_64" ||
            name == "feature" && !IsFeatureName(value)) Invalid("Unsupported cfg predicate value '" + value + "'.");
        return new(name, value, Array.Empty<CargoCfgPredicate>());
    }
    private string ReadName()
    {
        SkipSpace(); int start = _position;
        for (int scanned = 0; _position < text.Length && scanned < limits.MaximumManifestBytes; scanned++)
        {
            char ch = text[_position]; if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_')) break;
            budget.Step(path, span); _position++;
        }
        if (_position == start) Invalid("Expected cfg predicate name.");
        return text[start.._position];
    }
    private string ReadString()
    {
        Expect('"'); int start = _position;
        for (int scanned = 0; _position < text.Length && scanned < limits.MaximumManifestBytes; scanned++)
        {
            char ch = text[_position]; if (ch == '"') break;
            budget.Step(path, span);
            if (ch is '\\' or '\r' or '\n' || char.IsControl(ch)) Invalid("Cfg values require ordinary quoted strings without escapes.");
            _position++;
        }
        if (Peek() != '"') Invalid("Unterminated cfg string.");
        string value = text[start.._position]; _position++; return value;
    }
    private void Expect(char ch) { SkipSpace(); budget.Step(path, span); if (Peek() != ch) Invalid("Expected '" + ch + "' in cfg expression."); _position++; }
    private char Peek() => _position < text.Length ? text[_position] : '\0';
    private void SkipSpace()
    {
        for (int scanned = 0; _position < text.Length && scanned < limits.MaximumManifestBytes && char.IsWhiteSpace(text[_position]); scanned++)
        { budget.Step(path, span); _position++; }
    }
    private static bool IsFeatureName(string value) => value.Length is > 0 and <= 128 && (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');
    private void Invalid(string message) => throw new CargoLoadException(CargoCfgResolver.InvalidCfgDiagnostic, message, path, span);
    private void Limit(string message) => throw new CargoLoadException(CargoWorkspace.LimitDiagnostic, message, path, span);
}
