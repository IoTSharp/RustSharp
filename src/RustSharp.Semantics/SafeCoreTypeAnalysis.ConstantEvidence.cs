using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using RustSharp.Syntax;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreTypeAnalysis
{
    private sealed partial class Checker
    {
        private readonly Dictionary<int, Constant> _constantExpressions = [];

        private void EvaluateEnumDiscriminants()
        {
            foreach (SafeCoreHirNode declaration in _declarations.Values)
            {
                Step(declaration, 0);
                if (declaration.Kind != N.Enum) continue;
                _module = ModuleOf(declaration.DeclaredSymbol!);
                bool requiresRepresentation = false;
                for (int index = 0; index < declaration.ChildIds.Count; index++)
                {
                    SafeCoreHirNode variant = Child(declaration, index);
                    Step(variant, 1);
                    if (variant.Kind == N.EnumVariant && !variant.Modifiers.HasFlag(SafeCoreHirNodeModifiers.UnitStruct))
                        requiresRepresentation = true;
                }
                for (int variantIndex = 0; variantIndex < declaration.ChildIds.Count; variantIndex++)
                {
                    SafeCoreHirNode variant = Child(declaration, variantIndex);
                    Step(variant, 1);
                    if (variant.Kind != N.EnumVariant) continue;
                    for (int index = 0; index < variant.ChildIds.Count; index++)
                    {
                        SafeCoreHirNode expression = Child(variant, index);
                        Step(expression, 2);
                        if (expression.Kind is N.Field or N.Attribute) continue;
                        if (requiresRepresentation)
                            Fail(expression, "RST2001", "Explicit discriminants on tuple or data-carrying enum variants require a representation attribute outside this profile.");
                        Expr(expression, Primitive(K.Isize), 0);
                        ValidateConstContext(expression, 0);
                        Constant value = EvaluateConstant(expression, new(null), 0);
                        ValidateConstantRange(value, expression);
                        _constantExpressions.Add(expression.Id, value);
                    }
                }
            }
        }

        private (IReadOnlyDictionary<int, SafeCoreEvaluatedConstant> Constants,
            IReadOnlyDictionary<int, SafeCoreEvaluatedConstant> Promotions) PublishConstants()
        {
            var constants = new Dictionary<int, SafeCoreEvaluatedConstant>();
            var promotions = new Dictionary<int, SafeCoreEvaluatedConstant>();
            for (int index = 0; index < _hir.Nodes.Count; index++)
            {
                SafeCoreHirNode node = _hir.Nodes[index];
                Step(node, 0);
                _constantExpressions.TryGetValue(node.Id, out Constant? value);
                if (node.Kind == N.Const && node.DeclaredSymbol is { } declaration)
                    _constants.TryGetValue(Key(declaration), out value);
                else if (node.Kind == N.NameExpression && node.ReferencedSymbol is { } symbol)
                    _constants.TryGetValue(Key(symbol), out value);
                else if (node.Kind == N.ConstBlockExpression)
                    _constantBlocks.TryGetValue(node.Id, out value);
                if (value is not null) constants.Add(node.Id, ExportConstant(value, node, 0));

                if (node.Kind != N.UnaryExpression || node.Value != "&" ||
                    !IsClosedPromotable(Child(node, 0), 0)) continue;
                // Implicit promotion is optional. A non-evaluable expression
                // remains an ordinary runtime borrow, while interpreter work
                // and cancellation limits always remain fatal.
                try
                {
                    Constant promoted = EvaluateConstant(node, new(null), 0);
                    promotions.Add(node.Id, ExportConstant(promoted, node, 0));
                }
                catch (AnalysisException error) when (error.Diagnostic.Code != "RST0002") { }
            }
            return (new ReadOnlyDictionary<int, SafeCoreEvaluatedConstant>(constants),
                new ReadOnlyDictionary<int, SafeCoreEvaluatedConstant>(promotions));
        }

        private bool IsClosedPromotable(SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (node.Kind == N.NameExpression)
                return node.ReferencedSymbol is { } symbol &&
                    (_constants.ContainsKey(Key(symbol)) ||
                     _declarations.TryGetValue(Key(symbol), out SafeCoreHirNode? declaration) && declaration.Kind == N.Const ||
                     _constructors.TryGetValue(Key(symbol), out AdtShape? shape) && shape.Fields.Count == 0);
            if (node.Kind is N.LiteralExpression or N.ConstBlockExpression) return true;
            if (node.Kind == N.CallExpression)
            {
                SafeCoreHirNode callee = Child(node, 0);
                if (callee.ReferencedSymbol is null || !_constructors.ContainsKey(Key(callee.ReferencedSymbol))) return false;
                for (int index = 1; index < node.ChildIds.Count; index++)
                    if (!IsClosedPromotable(Child(node, index), depth + 1)) return false;
                return true;
            }
            if (node.Kind == N.UnaryExpression && node.Value is not ("&" or "-" or "!")) return false;
            if (node.Kind == N.BinaryExpression && node.Value is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=") return false;
            if (node.Kind is not (N.TupleExpression or N.ArrayExpression or N.StructExpression or N.StructExpressionField or
                N.UnaryExpression or N.BinaryExpression or N.CastExpression or N.MemberExpression or N.IndexExpression or
                N.PathType or N.TupleType or N.ArrayType or N.ReferenceType)) return false;
            for (int index = 0; index < node.ChildIds.Count; index++)
                if (!IsClosedPromotable(Child(node, index), depth + 1)) return false;
            return true;
        }

        private SafeCoreEvaluatedConstant ExportConstant(Constant value, SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            SafeCoreType type = _inference.Resolve(value.Type, defaultNumerics: true);
            var elements = new List<SafeCoreEvaluatedConstant>();
            var fields = new Dictionary<string, SafeCoreEvaluatedConstant>(StringComparer.Ordinal);
            string? scalar = value.Value switch
            {
                BigInteger integer => integer.ToString(CultureInfo.InvariantCulture),
                bool boolean => boolean ? "true" : "false",
                double number => number.ToString("R", CultureInfo.InvariantCulture),
                string text => text,
                _ when type.Kind == K.Unit => "()",
                _ => null,
            };
            if (value.Value is Constant referent)
                elements.Add(ExportConstant(referent, node, depth + 1));
            else if (value.Value is IReadOnlyList<Constant> values)
                for (int index = 0; index < values.Count; index++)
                    elements.Add(ExportConstant(values[index], node, depth + 1));
            else if (value.Value is IReadOnlyDictionary<string, Constant> members)
                foreach (KeyValuePair<string, Constant> member in members)
                    fields.Add(member.Key, ExportConstant(member.Value, node, depth + 1));
            return new(type, scalar, elements.AsReadOnly(), new ReadOnlyDictionary<string, SafeCoreEvaluatedConstant>(fields), value.Constructor);
        }
    }
}
