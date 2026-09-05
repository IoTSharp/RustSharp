using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

public enum SafeCorePrimitiveType
{
    Unit,
    I32,
    Bool,
    Never,
}

public sealed record SafeCoreTypedParameter(SafeCoreHirNode Pattern, SafeCorePrimitiveType Type);

public sealed record SafeCoreTypedFunction(
    SafeCoreHirNode Declaration,
    SafeCoreHirNode Body,
    IReadOnlyList<SafeCoreTypedParameter> Parameters,
    SafeCorePrimitiveType ReturnType);

/// <summary>Type evidence tied to stable IDs in the name-bound HIR arena.</summary>
public sealed class SafeCoreTypedProgram
{
    internal SafeCoreTypedProgram(
        SafeCoreHirResult hir,
        IReadOnlyList<SafeCoreTypedFunction> functions,
        SafeCorePrimitiveType[] types,
        Dictionary<int, int> integers,
        Dictionary<int, string> printFormats)
    {
        Hir = hir;
        Functions = Array.AsReadOnly(functions.ToArray());
        Types = Array.AsReadOnly(types);
        Integers = new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(integers);
        PrintFormats = new System.Collections.ObjectModel.ReadOnlyDictionary<int, string>(printFormats);
    }

    public SafeCoreHirResult Hir { get; }
    public IReadOnlyList<SafeCoreTypedFunction> Functions { get; }
    public IReadOnlyList<SafeCorePrimitiveType> Types { get; }
    public IReadOnlyDictionary<int, int> Integers { get; }
    public IReadOnlyDictionary<int, string> PrintFormats { get; }
}

public sealed record SafeCoreTypeCheckResult(SafeCoreTypedProgram? Program, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsSuccessful => Program is not null && Diagnostics.Count == 0;
}

/// <summary>Checks the explicitly versioned Copy-only primitive language profile.</summary>
public static class SafeCoreTypeChecking
{
    public const string Profile = "safe-core-primitives-v1";

    public static SafeCoreTypeCheckResult Check(SafeCoreHirResult hir, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hir);
        cancellationToken.ThrowIfCancellationRequested();
        if (!hir.IsSuccessful)
        {
            return new(null, hir.Diagnostics);
        }

        try
        {
            return new(new Checker(hir, cancellationToken).Run(), []);
        }
        catch (TypeCheckException exception)
        {
            return new(null, [exception.Diagnostic]);
        }
    }

    private sealed class TypeCheckException(Diagnostic diagnostic) : Exception(diagnostic.Message)
    {
        public Diagnostic Diagnostic { get; } = diagnostic;
    }

    private sealed class Checker(SafeCoreHirResult hir, CancellationToken cancellationToken)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<SafeCoreTypedFunction> _functions = [];
        private readonly Dictionary<string, SafeCoreTypedFunction> _signatures = new(StringComparer.Ordinal);
        private readonly Dictionary<SafeCoreSymbol, (SafeCorePrimitiveType Type, bool Mutable)> _bindings = [];
        private readonly SafeCorePrimitiveType[] _types = new SafeCorePrimitiveType[hir.Nodes.Count];
        private readonly bool[] _diverges = new bool[hir.Nodes.Count];
        private readonly Dictionary<int, int> _integers = [];
        private readonly Dictionary<int, string> _printFormats = [];
        private SafeCorePrimitiveType _returnType;
        private int _steps;

        public SafeCoreTypedProgram Run()
        {
            Collect(hir.Root!, 0);
            SafeCoreTypedFunction? main = _functions.Find(function =>
                function.Declaration.DeclaredSymbol!.ScopePath == hir.NameResolution!.RootScope!.Path &&
                function.Declaration.DeclaredSymbol.Name == "main");
            if (main is null || main.Parameters.Count != 0 || main.ReturnType != SafeCorePrimitiveType.Unit)
            {
                Fail(hir.Root!, "RST1005", "An executable requires a root fn main() returning unit with no parameters.");
            }

            foreach (SafeCoreTypedFunction function in _functions)
            {
                Step(function.Declaration, 0);
                _bindings.Clear();
                _returnType = function.ReturnType;
                foreach (SafeCoreTypedParameter parameter in function.Parameters)
                {
                    Bind(parameter.Pattern, parameter.Type);
                }

                Require(function.ReturnType, Expression(function.Body, 0), function.Body);
            }

            return new(hir, _functions, _types, _integers, _printFormats);
        }

        private void Collect(SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            switch (node.Kind)
            {
                case SafeCoreHirNodeKind.CompilationUnit:
                case SafeCoreHirNodeKind.Module:
                    foreach (int child in node.ChildIds) Collect(hir.GetNode(child), depth + 1);
                    break;
                case SafeCoreHirNodeKind.Import:
                    foreach (int child in node.ChildIds) Unsupported(hir.GetNode(child));
                    break;
                case SafeCoreHirNodeKind.Function:
                    if (_functions.Count >= 128) Limit(node);
                    var parameters = new List<SafeCoreTypedParameter>();
                    SafeCorePrimitiveType result = SafeCorePrimitiveType.Unit;
                    SafeCoreHirNode? body = null;
                    foreach (int child in node.ChildIds)
                    {
                        SafeCoreHirNode part = hir.GetNode(child);
                        Step(part, depth + 1);
                        if (part.Kind == SafeCoreHirNodeKind.Parameter)
                        {
                            if (parameters.Count >= 128) Limit(part);
                            SafeCoreHirNode pattern = Child(part, 0);
                            CheckBindingPattern(pattern);
                            SafeCorePrimitiveType type = Type(Child(part, 1));
                            RequireStorable(type, part);
                            parameters.Add(new(pattern, type));
                        }
                        else if (part.Kind == SafeCoreHirNodeKind.Block) body = part;
                        else if (IsType(part)) result = Type(part);
                        else Unsupported(part);
                    }

                    var function = new SafeCoreTypedFunction(node, body!, parameters.AsReadOnly(), result);
                    _functions.Add(function);
                    _signatures.Add(node.DeclaredSymbol!.QualifiedName, function);
                    break;
                default:
                    Unsupported(node);
                    break;
            }
        }

        private SafeCorePrimitiveType Expression(SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            SafeCorePrimitiveType type;
            switch (node.Kind)
            {
                case SafeCoreHirNodeKind.Block:
                    type = SafeCorePrimitiveType.Unit;
                    bool diverges = false;
                    foreach (int child in node.ChildIds)
                    {
                        type = Expression(hir.GetNode(child), depth + 1);
                        diverges |= _diverges[child];
                    }

                    // An explicit tail still has to type-check even when earlier code returns.
                    bool hasTail = node.ChildIds.Count != 0 && Child(node, node.ChildIds.Count - 1).Kind is not
                        (SafeCoreHirNodeKind.LetStatement or SafeCoreHirNodeKind.ReturnStatement or SafeCoreHirNodeKind.ExpressionStatement);
                    if (diverges && !hasTail) type = SafeCorePrimitiveType.Never;
                    break;
                case SafeCoreHirNodeKind.BlockExpression:
                    type = Expression(Child(node, 0), depth + 1);
                    break;
                case SafeCoreHirNodeKind.LetStatement:
                    SafeCoreHirNode pattern = Child(node, 0);
                    CheckBindingPattern(pattern);
                    int valueIndex = node.ChildIds.Count > 1 && IsType(Child(node, 1)) ? 2 : 1;
                    if (valueIndex >= node.ChildIds.Count)
                        Fail(node, "RST1001", "This profile requires an initializer for every local binding.");
                    SafeCorePrimitiveType valueType = Expression(Child(node, valueIndex), depth + 1);
                    SafeCorePrimitiveType bindingType = valueIndex == 2 ? Type(Child(node, 1)) : valueType;
                    Require(bindingType, valueType, node);
                    RequireStorable(bindingType, node);
                    Bind(pattern, bindingType);
                    type = _diverges[node.ChildIds[valueIndex]] ? SafeCorePrimitiveType.Never : SafeCorePrimitiveType.Unit;
                    break;
                case SafeCoreHirNodeKind.ReturnStatement:
                    Require(_returnType, node.ChildIds.Count == 0 ? SafeCorePrimitiveType.Unit :
                        Expression(Child(node, 0), depth + 1), node);
                    type = SafeCorePrimitiveType.Never;
                    break;
                case SafeCoreHirNodeKind.ExpressionStatement:
                    type = Expression(Child(node, 0), depth + 1);
                    if (!node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasSemicolon))
                        Require(SafeCorePrimitiveType.Unit, type, node);
                    type = _diverges[node.ChildIds[0]] ? SafeCorePrimitiveType.Never : SafeCorePrimitiveType.Unit;
                    break;
                case SafeCoreHirNodeKind.LiteralExpression:
                    if (node.Value is "true" or "false") type = SafeCorePrimitiveType.Bool;
                    else
                    {
                        _integers[node.Id] = ParseInteger(node, negative: false);
                        type = SafeCorePrimitiveType.I32;
                    }

                    break;
                case SafeCoreHirNodeKind.NameExpression:
                    if (node.ReferencedSymbol is null || !_bindings.TryGetValue(node.ReferencedSymbol, out var binding))
                        Fail(node, "RST1001", "Only local or parameter values can be used here; function values are not supported.");
                    type = _bindings[node.ReferencedSymbol!].Type;
                    break;
                case SafeCoreHirNodeKind.TupleExpression:
                    if (node.ChildIds.Count == 0) type = SafeCorePrimitiveType.Unit;
                    else if (node.ChildIds.Count == 1 && !node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma))
                    {
                        type = Expression(Child(node, 0), depth + 1);
                        if (_integers.TryGetValue(node.ChildIds[0], out int grouped)) _integers[node.Id] = grouped;
                    }
                    else { Unsupported(node); return default; }
                    break;
                case SafeCoreHirNodeKind.UnaryExpression:
                    if (node.Value == "-" && Child(node, 0).Kind == SafeCoreHirNodeKind.LiteralExpression &&
                        Child(node, 0).Value is not ("true" or "false"))
                    {
                        _integers[node.Id] = ParseInteger(Child(node, 0), negative: true);
                        _types[node.ChildIds[0]] = SafeCorePrimitiveType.I32;
                        type = SafeCorePrimitiveType.I32;
                        break;
                    }

                    if (node.Value is not ("-" or "!")) Unsupported(node);
                    type = Expression(Child(node, 0), depth + 1);
                    if (node.Value == "-") Require(SafeCorePrimitiveType.I32, type, node);
                    else if (type is not (SafeCorePrimitiveType.I32 or SafeCorePrimitiveType.Bool or SafeCorePrimitiveType.Never))
                        Fail(node, "RST1002", "The ! operator requires bool or i32.");
                    if (type == SafeCorePrimitiveType.I32 && _integers.TryGetValue(Child(node, 0).Id, out int constant))
                    {
                        if (node.Value == "-" && constant == int.MinValue)
                            Fail(node, "RST1006", "Constant negation overflows i32.");
                        _integers[node.Id] = node.Value == "-" ? -constant : ~constant;
                    }
                    break;
                case SafeCoreHirNodeKind.BinaryExpression:
                    type = Binary(node, depth);
                    break;
                case SafeCoreHirNodeKind.IfExpression:
                    SafeCorePrimitiveType condition = Expression(Child(node, 0), depth + 1);
                    Require(SafeCorePrimitiveType.Bool, condition, Child(node, 0));
                    SafeCorePrimitiveType thenType = Expression(Child(node, 1), depth + 1);
                    SafeCorePrimitiveType elseType = node.ChildIds.Count == 3 ?
                        Expression(Child(node, 2), depth + 1) : SafeCorePrimitiveType.Unit;
                    type = thenType == SafeCorePrimitiveType.Never ? elseType : thenType;
                    Require(type, elseType, node);
                    if (node.ChildIds.Count == 2) Require(SafeCorePrimitiveType.Unit, thenType, node);
                    if (condition == SafeCorePrimitiveType.Never) type = condition;
                    break;
                case SafeCoreHirNodeKind.CallExpression:
                    SafeCoreHirNode callee = Child(node, 0);
                    if (callee.Kind != SafeCoreHirNodeKind.NameExpression || callee.ReferencedSymbol is null ||
                        !_signatures.ContainsKey(callee.ReferencedSymbol.ResolvedImportTargetQualifiedName ?? callee.ReferencedSymbol.QualifiedName))
                        Fail(callee, "RST1001", "Only direct calls to declared functions are supported.");
                    SafeCoreTypedFunction target = _signatures[callee.ReferencedSymbol!.ResolvedImportTargetQualifiedName ?? callee.ReferencedSymbol.QualifiedName];
                    if (node.ChildIds.Count - 1 != target.Parameters.Count)
                        Fail(node, "RST1004", "The argument count does not match the function signature.");
                    type = target.ReturnType;
                    for (int index = 0; index < target.Parameters.Count; index++)
                    {
                        SafeCorePrimitiveType argument = Expression(Child(node, index + 1), depth + 1);
                        Require(target.Parameters[index].Type, argument, Child(node, index + 1));
                        if (argument == SafeCorePrimitiveType.Never) type = argument;
                    }

                    break;
                case SafeCoreHirNodeKind.PrintExpression:
                    type = Print(node, depth);
                    break;
                default:
                    Unsupported(node);
                    return default;
            }

            _types[node.Id] = type;
            _diverges[node.Id] = node.Kind switch
            {
                SafeCoreHirNodeKind.ReturnStatement => true,
                SafeCoreHirNodeKind.Block => node.ChildIds.Any(child => _diverges[child]),
                SafeCoreHirNodeKind.LetStatement => _diverges[node.ChildIds[^1]],
                SafeCoreHirNodeKind.ExpressionStatement or SafeCoreHirNodeKind.BlockExpression or SafeCoreHirNodeKind.UnaryExpression
                    => _diverges[node.ChildIds[0]],
                SafeCoreHirNodeKind.TupleExpression => node.ChildIds.Count != 0 && _diverges[node.ChildIds[0]],
                SafeCoreHirNodeKind.BinaryExpression => _diverges[node.ChildIds[0]] ||
                    (node.Value is not ("&&" or "||") && _diverges[node.ChildIds[1]]),
                SafeCoreHirNodeKind.IfExpression => _diverges[node.ChildIds[0]] ||
                    (node.ChildIds.Count == 3 && _diverges[node.ChildIds[1]] && _diverges[node.ChildIds[2]]),
                SafeCoreHirNodeKind.CallExpression or SafeCoreHirNodeKind.PrintExpression => node.ChildIds.Any(child => _diverges[child]),
                _ => false,
            };
            return type;
        }

        private SafeCorePrimitiveType Binary(SafeCoreHirNode node, int depth)
        {
            SafeCoreHirNode left = Child(node, 0);
            SafeCoreHirNode right = Child(node, 1);
            if (node.Value == "=")
            {
                if (left.Kind != SafeCoreHirNodeKind.NameExpression || left.ReferencedSymbol is null ||
                    !_bindings.ContainsKey(left.ReferencedSymbol))
                    Fail(left, "RST1003", "Assignment requires a mutable local or parameter binding.");
                var binding = _bindings[left.ReferencedSymbol!];
                if (!binding.Mutable) Fail(left, "RST1003", "Cannot assign to an immutable binding; declare it with mut.");
                _types[left.Id] = binding.Type;
                SafeCorePrimitiveType assigned = Expression(right, depth + 1);
                Require(binding.Type, assigned, right);
                return assigned == SafeCorePrimitiveType.Never ? assigned : SafeCorePrimitiveType.Unit;
            }

            if (node.Value is not ("+" or "-" or "*" or "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||"))
                Unsupported(node);
            SafeCorePrimitiveType lhs = Expression(left, depth + 1);
            SafeCorePrimitiveType rhs = Expression(right, depth + 1);
            bool logical = node.Value is "&&" or "||";
            if (logical)
            {
                Require(SafeCorePrimitiveType.Bool, lhs, left);
                Require(SafeCorePrimitiveType.Bool, rhs, right);
            }
            else if (node.Value is "==" or "!=")
            {
                if (lhs == SafeCorePrimitiveType.Never) lhs = rhs;
                Require(lhs, rhs, right);
                RequireStorable(lhs, node);
            }
            else
            {
                Require(SafeCorePrimitiveType.I32, lhs, left);
                Require(SafeCorePrimitiveType.I32, rhs, right);
            }

            if (_types[left.Id] == SafeCorePrimitiveType.Never || (!logical && rhs == SafeCorePrimitiveType.Never))
                return SafeCorePrimitiveType.Never;
            if (node.Value is "+" or "-" or "*" && _integers.TryGetValue(left.Id, out int leftConstant) &&
                _integers.TryGetValue(right.Id, out int rightConstant))
            {
                long value = node.Value switch
                {
                    "+" => (long)leftConstant + rightConstant,
                    "-" => (long)leftConstant - rightConstant,
                    _ => (long)leftConstant * rightConstant,
                };
                if (value is < int.MinValue or > int.MaxValue) Fail(node, "RST1006", "Constant arithmetic overflows i32.");
                _integers[node.Id] = (int)value;
            }
            return node.Value is "+" or "-" or "*" ? SafeCorePrimitiveType.I32 : SafeCorePrimitiveType.Bool;
        }

        private SafeCorePrimitiveType Print(SafeCoreHirNode node, int depth)
        {
            if (node.ChildIds.Count is < 1 or > 2) Unsupported(node);
            SafeCoreHirNode format = Child(node, 0);
            if (format.Kind != SafeCoreHirNodeKind.LiteralExpression || format.Value is null ||
                !SyntaxTree.TryDecodeStringLiteral(format.Value, out _))
                Fail(format, "RST1001", "println! requires a regular string literal format.");
            _ = SyntaxTree.TryDecodeStringLiteral(format.Value!, out string text);
            if (node.ChildIds.Count == 2)
            {
                if (text != "{}") Fail(format, "RST1001", "This profile supports exactly one {} format field.");
                SafeCorePrimitiveType value = Expression(Child(node, 1), depth + 1);
                if (value != SafeCorePrimitiveType.Never) RequireStorable(value, node);
                _printFormats[node.Id] = text;
                return value == SafeCorePrimitiveType.Never ? value : SafeCorePrimitiveType.Unit;
            }

            if (text.Contains('{', StringComparison.Ordinal) || text.Contains('}', StringComparison.Ordinal))
                Fail(format, "RST1001", "Literal-only println! does not support braces in this profile.");
            _printFormats[node.Id] = text;
            return SafeCorePrimitiveType.Unit;
        }

        private SafeCorePrimitiveType Type(SafeCoreHirNode node)
        {
            Step(node, 0);
            if (node.Kind == SafeCoreHirNodeKind.UnitType) return SafeCorePrimitiveType.Unit;
            if (node.Kind == SafeCoreHirNodeKind.PathType && node.ChildIds.Count == 1 &&
                Child(node, 0).ChildIds.Count == 0)
            {
                if (node.Name == "i32") return SafeCorePrimitiveType.I32;
                if (node.Name == "bool") return SafeCorePrimitiveType.Bool;
            }

            Unsupported(node);
            return default;
        }

        private int ParseInteger(SafeCoreHirNode node, bool negative)
        {
            string text = node.Value ?? string.Empty;
            if (text.Length > 128) Limit(node);
            if (text.EndsWith("i32", StringComparison.Ordinal)) text = text[..^3];
            text = text.Replace("_", string.Empty, StringComparison.Ordinal);
            int radix = 10;
            if (text.StartsWith("0x", StringComparison.Ordinal)) { radix = 16; text = text[2..]; }
            else if (text.StartsWith("0o", StringComparison.Ordinal)) { radix = 8; text = text[2..]; }
            else if (text.StartsWith("0b", StringComparison.Ordinal)) { radix = 2; text = text[2..]; }
            if (text.Length == 0) Unsupported(node);
            long value = 0;
            foreach (char character in text)
            {
                Step(node, 0);
                int digit = character is >= '0' and <= '9' ? character - '0' :
                    character is >= 'a' and <= 'f' ? character - 'a' + 10 :
                    character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;
                if (digit < 0 || digit >= radix) Unsupported(node);
                value = value * radix + digit;
                if (value > (negative ? 2147483648L : int.MaxValue))
                    Fail(node, "RST1006", "Integer literal is out of range for i32.");
            }

            return (int)(negative ? -value : value);
        }

        private void Bind(SafeCoreHirNode pattern, SafeCorePrimitiveType type)
        {
            _types[pattern.Id] = type;
            if (pattern.Kind == SafeCoreHirNodeKind.IdentifierPattern)
                _bindings.Add(pattern.DeclaredSymbol!, (type, pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable)));
        }

        private static void CheckBindingPattern(SafeCoreHirNode node)
        {
            if (node.Kind is not (SafeCoreHirNodeKind.IdentifierPattern or SafeCoreHirNodeKind.WildcardPattern)) Unsupported(node);
        }

        private static void RequireStorable(SafeCorePrimitiveType type, SafeCoreHirNode node)
        {
            if (type is not (SafeCorePrimitiveType.I32 or SafeCorePrimitiveType.Bool))
                Fail(node, "RST1001", "Only i32 and bool values can be stored or displayed in this profile.");
        }

        private static void Require(SafeCorePrimitiveType expected, SafeCorePrimitiveType actual, SafeCoreHirNode node)
        {
            if (actual != expected && actual != SafeCorePrimitiveType.Never)
                Fail(node, "RST1002", $"Type mismatch: expected {expected}, found {actual}.");
        }

        private void Step(SafeCoreHirNode node, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_steps > 100_000 || depth > 128 || _clock.Elapsed > TimeSpan.FromSeconds(10)) Limit(node);
        }

        private SafeCoreHirNode Child(SafeCoreHirNode node, int index) => hir.GetNode(node.ChildIds[index]);
        private static bool IsType(SafeCoreHirNode node) => node.Kind is >= SafeCoreHirNodeKind.PathType and <= SafeCoreHirNodeKind.NeverType;
        [DoesNotReturn]
        private static void Unsupported(SafeCoreHirNode node) => Fail(node, "RST1001", $"{node.Kind} is outside {Profile}.");
        [DoesNotReturn]
        private static void Limit(SafeCoreHirNode node) => Fail(node, "RST0002", "Safe-core semantic work exceeded its count, nesting or time limit.");
        [DoesNotReturn]
        private static void Fail(SafeCoreHirNode node, string code, string message) => throw new TypeCheckException(new(code, message, node.Span));
    }
}
