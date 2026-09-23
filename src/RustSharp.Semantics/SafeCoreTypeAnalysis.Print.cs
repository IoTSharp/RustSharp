using RustSharp.Syntax;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreTypeAnalysis
{
    private sealed partial class Checker
    {
        private SafeCoreType Print(SafeCoreHirNode node, int depth)
        {
            if (node.ChildIds.Count is < 1 or > 2)
                Fail(node, "RST2002", "println! requires one format literal and at most one value.");

            SafeCoreHirNode format = Child(node, 0);
            string text = string.Empty;
            if (format.Kind != N.LiteralExpression || format.Value is null ||
                !SyntaxTree.TryDecodeStringLiteral(format.Value, out text))
                Fail(format, "RST2002", "println! requires a regular string literal format.");

            if (node.ChildIds.Count == 1)
            {
                if (text.Contains('{', StringComparison.Ordinal) || text.Contains('}', StringComparison.Ordinal))
                    Fail(format, "RST2002", "A literal-only println! cannot contain format fields.");
                return Primitive(K.Unit);
            }

            if (!string.Equals(text, "{}", StringComparison.Ordinal))
                Fail(format, "RST2002", "This profile supports exactly one {} format field.");

            SafeCoreType value = _inference.Resolve(Expr(Child(node, 1), null, depth + 1), defaultNumerics: true);
            if (value.Kind == K.Never) return value;
            bool displayable = value.Kind is K.Unit or K.Bool or K.Char or
                K.I8 or K.I16 or K.I32 or K.I64 or K.I128 or K.Isize or
                K.U8 or K.U16 or K.U32 or K.U64 or K.U128 or K.Usize or
                K.F32 or K.F64;
            if (!displayable)
                Fail(Child(node, 1), "RST2002", "println! values must be scalar in this profile.");
            return Primitive(K.Unit);
        }
    }
}
