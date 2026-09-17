using System.Numerics;
using RustSharp.Syntax;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreTypeAnalysis
{
    private sealed partial class Checker
    {
        private Constant EvaluateConstantMatch(SafeCoreHirNode node, ConstantEnvironment environment, int depth)
        {
            Constant value = EvaluateConstant(Child(node, 0), environment, depth + 1);
            for (int index = 1; index < node.ChildIds.Count; index++)
            {
                SafeCoreHirNode arm = Child(node, index);
                Step(arm, depth);
                if (!MatchConstantPattern(Child(arm, 0), value, environment, depth + 1)) continue;
                if (arm.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasGuard) &&
                    EvaluateConstant(Child(arm, 1), environment, depth + 1).Value is not true) continue;
                return EvaluateConstant(Child(arm, arm.ChildIds.Count - 1), environment, depth + 1);
            }
            Fail(node, "RST2009", "No match arm accepts the constant value.");
            return null!;
        }

        private bool MatchConstantPattern(SafeCoreHirNode pattern, Constant value, ConstantEnvironment environment, int depth)
        {
            Step(pattern, depth);
            switch (pattern.Kind)
            {
                case N.WildcardPattern: return true;
                case N.IdentifierPattern when pattern.DeclaredSymbol is not null:
                    if (pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ByReference))
                        Fail(pattern, "RST2010", "Reference bindings are outside the scalar and aggregate const interpreter.");
                    environment.Locals[pattern.DeclaredSymbol] = value;
                    return true;
                case N.AtPattern:
                    if (!MatchConstantPattern(Child(pattern, 1), value, environment, depth + 1)) return false;
                    return MatchConstantPattern(Child(pattern, 0), value, environment, depth + 1);
                case N.OrPattern:
                    foreach (SafeCoreHirNode alternative in Parts(pattern))
                        if (MatchConstantPattern(alternative, value, environment, depth + 1)) return true;
                    return false;
                case N.LiteralPattern:
                    string raw = pattern.Value!;
                    if (raw.StartsWith('"') || raw.StartsWith('r'))
                        Fail(pattern, "RST2010", "String matching is outside the scalar and aggregate const interpreter.");
                    bool negative = raw.StartsWith('-');
                    SafeCoreHirNode literal = negative
                        ? new(pattern.Id, N.LiteralExpression, pattern.Span, null, raw[1..], pattern.Modifiers, null, null, []) : pattern;
                    Constant expected = ConstantLiteral(literal, _inference.Resolve(value.Type, defaultNumerics: true));
                    if (negative) expected = expected with { Value = expected.Value is BigInteger integer ? -integer : -(double)expected.Value! };
                    if (_inference.Resolve(expected.Type).Kind == K.F32 && expected.Value is double floating)
                        expected = expected with { Value = (double)(float)floating };
                    return Equals(value.Value, expected.Value);
                case N.RangePattern:
                    if (value.Value is not BigInteger scalar) Fail(pattern, "RST2010", "Constant range matching requires an integer or character.");
                    scalar = (BigInteger)value.Value!;
                    bool hasStart = pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasRangeStart);
                    bool hasEnd = pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasRangeEnd);
                    if (hasStart && scalar < PatternBound(Child(pattern, 0), value.Type, depth + 1)) return false;
                    if (!hasEnd) return true;
                    BigInteger end = PatternBound(Child(pattern, hasStart ? 1 : 0), value.Type, depth + 1);
                    return pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.InclusiveRange) ? scalar <= end : scalar < end;
                case N.IdentifierPattern:
                case N.PathPattern:
                    if (pattern.ReferencedSymbol is { } symbol && _declarations.TryGetValue(Key(symbol), out var declaration) && declaration.Kind == N.Const)
                        return Equals(value.Value, ConstantItem(declaration, depth + 1).Value);
                    AdtShape shape = PatternShape(pattern, value.Type, depth + 1);
                    return value.Constructor == Key(shape.Node.DeclaredSymbol!) && MatchConstantSequence(pattern, value, environment, depth + 1);
                case N.StructPattern:
                    AdtShape structure = PatternShape(pattern, value.Type, depth + 1);
                    if (value.Constructor != Key(structure.Node.DeclaredSymbol!)) return false;
                    foreach (SafeCoreHirNode field in Parts(pattern))
                    {
                        Step(field, depth);
                        Constant fieldValue;
                        if (value.Value is IReadOnlyDictionary<string, Constant> named) fieldValue = named[Canonical(field.Name!)];
                        else if (value.Value is IReadOnlyList<Constant> positional && int.TryParse(field.Name, out int index)) fieldValue = positional[index];
                        else { Fail(field, "RST2010", "This constant has no named field value."); return false; }
                        if (!MatchConstantPattern(Child(field, 0), fieldValue, environment, depth + 1)) return false;
                    }
                    return true;
                case N.TuplePattern:
                    if (pattern.ChildIds.Count == 1 && !pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma) && Child(pattern, 0).Kind != N.RestPattern)
                        return MatchConstantPattern(Child(pattern, 0), value, environment, depth + 1);
                    if (pattern.ChildIds.Count == 0 && value.Type.Kind == K.Unit) return true;
                    return MatchConstantSequence(pattern, value, environment, depth + 1);
                case N.SlicePattern:
                    return MatchConstantSequence(pattern, value, environment, depth + 1);
                default:
                    Fail(pattern, "RST2010", "This binding pattern is outside the bounded const interpreter.");
                    return false;
            }
        }

        private bool MatchConstantSequence(SafeCoreHirNode pattern, Constant value, ConstantEnvironment environment, int depth)
        {
            Step(pattern, depth);
            if (value.Value is not IReadOnlyList<Constant> values) return false;
            int rest = -1;
            for (int index = 0; index < pattern.ChildIds.Count; index++)
            {
                Step(pattern, depth);
                SafeCoreHirNode child = Child(pattern, index);
                if (child.Kind == N.RestPattern || child.Kind == N.AtPattern && Child(child, 1).Kind == N.RestPattern) rest = index;
            }
            int explicitCount = pattern.ChildIds.Count - (rest >= 0 ? 1 : 0);
            if (explicitCount > values.Count || rest < 0 && explicitCount != values.Count) return false;
            for (int index = 0; index < pattern.ChildIds.Count; index++)
            {
                Step(pattern, depth);
                SafeCoreHirNode child = Child(pattern, index);
                if (index == rest)
                {
                    if (child.Kind == N.AtPattern)
                    {
                        int count = values.Count - explicitCount;
                        var tail = new List<Constant>();
                        for (int offset = 0; offset < count; offset++) { Step(child, depth); tail.Add(values[index + offset]); }
                        BindConstant(Child(child, 0), new(SafeCoreType.Array(value.Type.ElementType, count), tail.AsReadOnly()), environment, depth + 1);
                    }
                    continue;
                }
                int position = rest < 0 || index < rest ? index : values.Count - pattern.ChildIds.Count + index;
                if (!MatchConstantPattern(child, values[position], environment, depth + 1)) return false;
            }
            return true;
        }
    }
}
