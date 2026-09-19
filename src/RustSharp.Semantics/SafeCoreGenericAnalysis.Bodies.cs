using System.Collections.Immutable;
using RustSharp.Syntax;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreGenericAnalysis
{
    private sealed partial class Checker
    {
        private sealed partial class BodyChecker(Checker owner, Declaration definition)
        {
            private readonly Dictionary<SafeCoreSymbol, (RustType Type, bool Mutable)> locals = [];
            private readonly Dictionary<int, RustType> types = [];
            private readonly Dictionary<int, GenericFunctionInstance> calls = [];
            private readonly Dictionary<int, int> constants = [];
            private readonly HashSet<int> diverges = [];

            public SafeCoreGenericFunctionDefinition Run()
            {
                int index = 0;
                foreach (SafeCoreHirNode parameter in owner.Parts(definition.Node).Where(static p => p.Kind == N.Parameter))
                {
                    owner.Step(parameter);
                    Bind(owner.Child(parameter, 0), definition.ParameterTypes[index++]);
                }
                Expression(definition.Body, definition.ReturnType, 0);
                CollectSignatureEvidence(definition.Node, 0);
                var plan = new GenericFunctionDefinition(Key(definition.Node.DeclaredSymbol!), definition.Parameters,
                    definition.ParameterTypes, definition.ReturnType,
                    [.. calls.OrderBy(static entry => entry.Key).Select(static entry => entry.Value)], definition.Bounds);
                return new(plan.Id, definition.Node, definition.Body, plan,
                    types.ToImmutableDictionary(), calls.ToImmutableDictionary());
            }

            private void CollectSignatureEvidence(SafeCoreHirNode node, int depth)
            {
                owner.Step(node, depth);
                if (owner.signatureTypes.TryGetValue(node.Id, out RustType? type)) types[node.Id] = type;
                foreach (SafeCoreHirNode child in owner.Parts(node)) CollectSignatureEvidence(child, depth + 1);
            }

            private RustType Expression(SafeCoreHirNode node, RustType? expected, int depth)
            {
                owner.Step(node, depth);
                RustType type;
                switch (node.Kind)
                {
                    case N.Block:
                        type = RustType.Unit;
                        bool terminated = false;
                        for (int index = 0; index < node.ChildIds.Count; index++)
                        {
                            SafeCoreHirNode child = owner.Child(node, index);
                            bool tail = index == node.ChildIds.Count - 1 && !Statement(child);
                            type = Expression(child, tail ? expected : null, depth + 1);
                            terminated |= diverges.Contains(child.Id);
                        }
                        if (node.ChildIds.Count == 0 || Statement(owner.Child(node, node.ChildIds.Count - 1)))
                            type = terminated ? Never : RustType.Unit;
                        if (terminated) diverges.Add(node.Id);
                        break;
                    case N.Attribute:
                        Documentation(node);
                        type = RustType.Unit;
                        break;
                    case N.BlockExpression:
                        type = Expression(owner.Child(node, 0), expected, depth + 1);
                        if (diverges.Contains(node.ChildIds[0])) diverges.Add(node.Id);
                        break;
                    case N.LetStatement:
                        if (node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasElse)) Unsupported(node);
                        SafeCoreHirNode pattern = owner.Child(node, 0);
                        CheckPattern(pattern);
                        int valueIndex = node.ChildIds.Count > 1 && IsType(owner.Child(node, 1)) ? 2 : 1;
                        if (valueIndex >= node.ChildIds.Count) Unsupported(node);
                        RustType? annotation = valueIndex == 2 ? ReadType(owner.Child(node, 1)) : null;
                        RustType value = Expression(owner.Child(node, valueIndex), annotation, depth + 1);
                        if (value.Equals(Never) && annotation is null)
                            Fail(node, SafeCoreGenericDiagnosticCodes.InvalidCall, "A diverging initializer requires an explicit binding type in this profile.");
                        Bind(pattern, annotation ?? value);
                        type = diverges.Contains(node.ChildIds[valueIndex]) ? Never : RustType.Unit;
                        break;
                    case N.ReturnStatement:
                    case N.ReturnExpression:
                        if (node.ChildIds.Count == 0) Require(RustType.Unit, definition.ReturnType, node);
                        else Expression(owner.Child(node, 0), definition.ReturnType, depth + 1);
                        type = Never;
                        break;
                    case N.ExpressionStatement:
                        Expression(owner.Child(node, 0),
                            node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasSemicolon) ? null : RustType.Unit, depth + 1);
                        type = diverges.Contains(node.ChildIds[0]) ? Never : RustType.Unit;
                        break;
                    case N.NameExpression:
                        if (node.ReferencedSymbol is not null && owner.definitions.TryGetValue(Key(node.ReferencedSymbol), out Declaration? nominal) &&
                            nominal.Node.Kind == N.Struct && nominal.Node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.UnitStruct))
                        {
                            type = Construct(node, nominal, [], expected, depth);
                            break;
                        }
                        if (node.ChildIds.Count != 0) Unsupported(node);
                        if (node.ReferencedSymbol is null || !locals.TryGetValue(node.ReferencedSymbol, out var local))
                            Fail(node, SafeCoreGenericDiagnosticCodes.Unsupported, "Only bound local and parameter values are supported here.");
                        type = locals[node.ReferencedSymbol!].Type;
                        break;
                    case N.LiteralExpression:
                        if (node.Value is "true" or "false") type = RustType.Bool;
                        else { constants[node.Id] = Integer(node, false); type = RustType.I32; }
                        break;
                    case N.TupleExpression:
                        if (node.ChildIds.Count == 1 && !node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma))
                        {
                            type = Expression(owner.Child(node, 0), expected, depth + 1);
                            if (constants.TryGetValue(node.ChildIds[0], out int constant)) constants[node.Id] = constant;
                        }
                        else
                        {
                            if (node.ChildIds.Count > 16) Unsupported(node);
                            var elements = new List<RustType>();
                            for (int index = 0; index < node.ChildIds.Count; index++)
                            {
                                RustType? target = expected?.Name == "$tuple" && expected.Arguments.Length == node.ChildIds.Count
                                    ? expected.Arguments[index] : null;
                                elements.Add(Expression(owner.Child(node, index), target, depth + 1));
                            }
                            type = Tuple([.. elements]);
                        }
                        if (node.ChildIds.Any(diverges.Contains)) type = Never;
                        break;
                    case N.IfExpression:
                        Expression(owner.Child(node, 0), RustType.Bool, depth + 1);
                        RustType then = Expression(owner.Child(node, 1), node.ChildIds.Count == 2 ? RustType.Unit : expected, depth + 1);
                        RustType other = node.ChildIds.Count == 3 ? Expression(owner.Child(node, 2), expected, depth + 1) : RustType.Unit;
                        type = then.Equals(Never) ? other : then;
                        Require(other, type, node);
                        if (diverges.Contains(node.ChildIds[0]) || node.ChildIds.Count == 3 &&
                            diverges.Contains(node.ChildIds[1]) && diverges.Contains(node.ChildIds[2])) diverges.Add(node.Id);
                        break;
                    case N.UnaryExpression:
                        type = Unary(node, depth);
                        if (diverges.Contains(node.ChildIds[0])) type = Never;
                        break;
                    case N.BinaryExpression:
                        type = Binary(node, depth);
                        if (diverges.Contains(node.ChildIds[0]) || node.Value is not ("&&" or "||") && diverges.Contains(node.ChildIds[1]))
                            type = Never;
                        break;
                    case N.CallExpression:
                        type = Call(node, expected, depth);
                        if (node.ChildIds.Skip(1).Any(diverges.Contains)) type = Never;
                        break;
                    case N.StructExpression:
                        if (node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.StructUpdate) || node.ReferencedSymbol is null ||
                            !owner.definitions.TryGetValue(Key(node.ReferencedSymbol), out Declaration? structure) || structure.Node.Kind != N.Struct ||
                            structure.Node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.TupleStruct) || structure.Node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.UnitStruct))
                            Unsupported(node);
                        type = Construct(node, owner.definitions[Key(node.ReferencedSymbol!)],
                            [.. owner.Parts(node).Where(static part => part.Kind == N.StructExpressionField)], expected, depth);
                        if (node.ChildIds.Any(diverges.Contains)) type = Never;
                        break;
                    case N.MemberExpression:
                        type = Member(node, depth);
                        if (diverges.Contains(node.ChildIds[0])) type = Never;
                        break;
                    case N.PrintExpression:
                        type = Print(node, depth);
                        break;
                    default: Unsupported(node); return null!;
                }
                if (type.Equals(Never)) diverges.Add(node.Id);
                if (expected is not null) Require(type, expected, node);
                owner.budget.Count(types.Count + (types.ContainsKey(node.Id) ? 0 : 1));
                types[node.Id] = type;
                return type;
            }

            private RustType Call(SafeCoreHirNode node, RustType? expected, int depth)
            {
                SafeCoreHirNode callee = owner.Child(node, 0);
                owner.Step(callee, depth);
                if (callee.Kind == N.NameExpression && callee.ReferencedSymbol is not null &&
                    owner.definitions.TryGetValue(Key(callee.ReferencedSymbol), out Declaration? constructor) && constructor.Node.Kind == N.Struct &&
                    constructor.Node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.TupleStruct))
                    return Construct(node, constructor, [.. owner.Parts(node).Skip(1)], expected, depth);
                if (callee.Kind != N.NameExpression || callee.ReferencedSymbol is null ||
                    !owner.definitions.TryGetValue(Key(callee.ReferencedSymbol), out Declaration? target) || target.Node.Kind != N.Function)
                    Fail(callee, SafeCoreGenericDiagnosticCodes.InvalidCall, "Only direct calls to declared functions are supported.");
                Declaration function = owner.definitions[Key(callee.ReferencedSymbol!)];
                if (node.ChildIds.Count - 1 != function.ParameterTypes.Length)
                    Fail(node, SafeCoreGenericDiagnosticCodes.InvalidCall, "The argument count does not match the function signature.");
                var bindings = new Dictionary<string, RustType>(StringComparer.Ordinal);
                var explicitTypes = new List<RustType>();
                foreach (SafeCoreHirNode segment in owner.Parts(callee))
                    foreach (SafeCoreHirNode argument in owner.Parts(segment)) explicitTypes.Add(ReadType(argument));
                if (explicitTypes.Count != 0)
                {
                    if (explicitTypes.Count != function.Parameters.Length)
                        Fail(callee, SafeCoreGenericDiagnosticCodes.InvalidCall, "Incorrect number of explicit type arguments.");
                    for (int index = 0; index < explicitTypes.Count; index++) bindings.Add(function.Parameters[index], explicitTypes[index]);
                }
                // Rigid caller parameters and callee variables have distinct qualified identities.
                if (expected is not null && !expected.Equals(Never))
                    Infer(function.ReturnType, expected, bindings, node, 0);
                for (int index = 0; index < function.ParameterTypes.Length; index++)
                {
                    RustType template = function.ParameterTypes[index];
                    RustType substituted = owner.Substitute(template, bindings);
                    bool resolved = !ContainsCalleeParameter(substituted, function.Parameters, 0);
                    SafeCoreHirNode argumentNode = owner.Child(node, index + 1);
                    RustType actual = Expression(argumentNode,
                        resolved && argumentNode.Kind == N.CallExpression ? substituted : null, depth + 1);
                    if (!actual.Equals(Never)) Infer(template, actual, bindings, owner.Child(node, index + 1), 0);
                    if (resolved) Require(actual, substituted, owner.Child(node, index + 1));
                }
                if (function.Parameters.Any(parameter => !bindings.ContainsKey(parameter)))
                    Fail(callee, SafeCoreGenericDiagnosticCodes.InvalidCall, "Type arguments cannot be inferred; supply explicit arguments or a result type.");
                var instance = new GenericFunctionInstance(Key(function.Node.DeclaredSymbol!),
                    [.. function.Parameters.Select(parameter => bindings[parameter])]);
                foreach (RustType argument in instance.Arguments) owner.WellFormed(argument, definition.Bounds, 0);
                foreach (GenericTraitObligation bound in function.Bounds)
                {
                    owner.Step(node);
                    owner.traits.Prove(new(bound.Trait, owner.Substitute(bound.Target, bindings)), definition.Bounds);
                }
                owner.budget.Count(calls.Count + 1);
                calls.Add(node.Id, instance);
                return owner.Substitute(function.ReturnType, bindings);
            }

            private void Infer(RustType template, RustType actual, Dictionary<string, RustType> bindings, SafeCoreHirNode node, int depth)
            {
                owner.Step(node, depth);
                if (!GenericTypes.Match(template, actual, bindings, owner.budget, depth))
                    Fail(node, SafeCoreGenericDiagnosticCodes.InvalidCall, $"Call argument type '{actual}' does not match '{template}'.");
            }

            private bool ContainsCalleeParameter(RustType type, ImmutableArray<string> parameters, int depth)
            {
                owner.budget.Step(depth);
                return type.Kind == RustTypeKind.Parameter && parameters.Contains(type.Name) ||
                    type.Arguments.Any(argument => ContainsCalleeParameter(argument, parameters, depth + 1));
            }

            private RustType ReadType(SafeCoreHirNode node)
            {
                RustType type = owner.Type(node, definition, 0);
                owner.Step(node);
                owner.WellFormed(type, definition.Bounds, 0);
                return type;
            }

            private void Bind(SafeCoreHirNode pattern, RustType type)
            {
                owner.Step(pattern);
                CheckPattern(pattern);
                types[pattern.Id] = type;
                if (pattern.Kind == N.IdentifierPattern)
                {
                    owner.budget.Count(locals.Count + 1);
                    locals.Add(pattern.DeclaredSymbol!, (type, pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable)));
                }
            }

            private RustType Unary(SafeCoreHirNode node, int depth)
            {
                if (node.Value == "-" && owner.Child(node, 0).Kind == N.LiteralExpression && owner.Child(node, 0).Value is not ("true" or "false"))
                {
                    constants[node.Id] = Integer(owner.Child(node, 0), true);
                    types[node.ChildIds[0]] = RustType.I32;
                    return RustType.I32;
                }
                if (node.Value is not ("-" or "!")) Unsupported(node);
                RustType type = Expression(owner.Child(node, 0), null, depth + 1);
                if (node.Value == "-") Require(type, RustType.I32, node);
                else if (!Scalar(type)) Fail(node, SafeCoreGenericDiagnosticCodes.BodyMismatch, "The ! operator requires bool or i32.");
                if (constants.TryGetValue(node.ChildIds[0], out int value))
                {
                    if (node.Value == "-" && value == int.MinValue)
                        Fail(node, SafeCoreGenericDiagnosticCodes.BodyMismatch, "Constant negation overflows i32.");
                    constants[node.Id] = node.Value == "-" ? -value : ~value;
                }
                return type;
            }

            private RustType Binary(SafeCoreHirNode node, int depth)
            {
                SafeCoreHirNode left = owner.Child(node, 0);
                SafeCoreHirNode right = owner.Child(node, 1);
                if (node.Value == "=")
                {
                    if (left.Kind != N.NameExpression || left.ReferencedSymbol is null ||
                        !locals.TryGetValue(left.ReferencedSymbol, out var local) || !local.Mutable)
                        Fail(left, SafeCoreGenericDiagnosticCodes.BodyMismatch, "Assignment requires a mutable local or parameter.");
                    if (left.ChildIds.Count != 0) Unsupported(left);
                    RustType target = locals[left.ReferencedSymbol!].Type;
                    types[left.Id] = target;
                    Expression(right, target, depth + 1);
                    return RustType.Unit;
                }
                if (node.Value is not ("+" or "-" or "*" or "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||")) Unsupported(node);
                RustType lhs = Expression(left, null, depth + 1);
                RustType rhs = Expression(right, null, depth + 1);
                if (node.Value is "&&" or "||")
                {
                    Require(lhs, RustType.Bool, left); Require(rhs, RustType.Bool, right);
                }
                else if (node.Value is "==" or "!=")
                {
                    if (!Scalar(lhs) || !Scalar(rhs))
                        Fail(node, SafeCoreGenericDiagnosticCodes.BodyMismatch, "Equality on this type requires unsupported trait operations.");
                    Require(lhs, rhs.Equals(Never) ? lhs : rhs, node);
                }
                else { Require(lhs, RustType.I32, left); Require(rhs, RustType.I32, right); }
                if (node.Value is "+" or "-" or "*" && constants.TryGetValue(left.Id, out int a) && constants.TryGetValue(right.Id, out int b))
                {
                    long result = node.Value switch { "+" => (long)a + b, "-" => (long)a - b, _ => (long)a * b };
                    if (result is < int.MinValue or > int.MaxValue) Fail(node, SafeCoreGenericDiagnosticCodes.BodyMismatch, "Constant arithmetic overflows i32.");
                    constants[node.Id] = (int)result;
                }
                return node.Value is "+" or "-" or "*" ? RustType.I32 : RustType.Bool;
            }

            private RustType Print(SafeCoreHirNode node, int depth)
            {
                if (node.ChildIds.Count is < 1 or > 2) Unsupported(node);
                SafeCoreHirNode format = owner.Child(node, 0);
                if (format.Kind != N.LiteralExpression || format.Value is null || !SyntaxTree.TryDecodeStringLiteral(format.Value, out _)) Unsupported(format);
                SyntaxTree.TryDecodeStringLiteral(format.Value!, out string text);
                if (node.ChildIds.Count == 1)
                {
                    if (text.Contains('{', StringComparison.Ordinal) || text.Contains('}', StringComparison.Ordinal)) Unsupported(format);
                }
                else
                {
                    if (text != "{}") Unsupported(format);
                    RustType value = Expression(owner.Child(node, 1), null, depth + 1);
                    if (!Scalar(value)) Unsupported(owner.Child(node, 1));
                    if (diverges.Contains(node.ChildIds[1])) return Never;
                }
                return RustType.Unit;
            }

            private int Integer(SafeCoreHirNode node, bool negative)
            {
                string text = node.Value ?? string.Empty;
                if (text.Length > 128) Unsupported(node);
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
                    owner.Step(node);
                    int digit = character is >= '0' and <= '9' ? character - '0' : character is >= 'a' and <= 'f' ? character - 'a' + 10 :
                        character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;
                    if (digit < 0 || digit >= radix) Unsupported(node);
                    value = value * radix + digit;
                    if (value > (negative ? 2147483648L : int.MaxValue))
                        Fail(node, SafeCoreGenericDiagnosticCodes.BodyMismatch, "Integer literal is outside i32 range.");
                }
                return (int)(negative ? -value : value);
            }

            private static void Require(RustType actual, RustType expected, SafeCoreHirNode node)
            {
                if (!actual.Equals(Never) && !actual.Equals(expected))
                    Fail(node, SafeCoreGenericDiagnosticCodes.BodyMismatch, $"Expected '{expected}', found '{actual}'.");
            }
            private static bool Scalar(RustType type) => type.Equals(RustType.I32) || type.Equals(RustType.Bool) || type.Equals(Never);
            private static bool Statement(SafeCoreHirNode node) => node.Kind is N.LetStatement or N.ReturnStatement or N.ExpressionStatement or N.Attribute;
        }
    }
}
