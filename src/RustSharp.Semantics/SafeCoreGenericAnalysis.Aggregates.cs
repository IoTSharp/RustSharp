using System.Globalization;
using RustSharp.Syntax;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreGenericAnalysis
{
    private sealed partial class Checker
    {
        private sealed partial class BodyChecker
        {
            private RustType Construct(SafeCoreHirNode node, Declaration nominal,
                SafeCoreHirNode[] values, RustType? expected, int depth)
            {
                owner.Step(node, depth);
                if (values.Length != nominal.Fields.Count)
                    Fail(node, SafeCoreGenericDiagnosticCodes.InvalidCall, "A constructor must initialize every declared field exactly once.");
                SafeCoreHirNode path = node.Kind == N.CallExpression ? owner.Child(node, 0) : node;
                var bindings = new Dictionary<string, RustType>(StringComparer.Ordinal);
                var explicitTypes = new List<RustType>();
                foreach (SafeCoreHirNode segment in owner.Parts(path).Where(static part => part.Kind == N.PathSegment))
                    foreach (SafeCoreHirNode argument in owner.Parts(segment)) explicitTypes.Add(ReadType(argument));
                if (explicitTypes.Count != 0)
                {
                    if (explicitTypes.Count != nominal.Parameters.Length)
                        Fail(path, SafeCoreGenericDiagnosticCodes.InvalidCall, "The constructor has incorrect explicit type argument arity.");
                    for (int index = 0; index < explicitTypes.Count; index++) bindings.Add(nominal.Parameters[index], explicitTypes[index]);
                }
                RustType template = RustType.Named(Key(nominal.Node.DeclaredSymbol!),
                    [.. nominal.Parameters.Select(RustType.Parameter)]);
                if (expected is not null && !expected.Equals(Never)) Infer(template, expected, bindings, node, 0);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int index = 0; index < values.Length; index++)
                {
                    SafeCoreHirNode field = values[index];
                    owner.Step(field, depth);
                    string name = node.Kind == N.StructExpression ? field.Name! : index.ToString(CultureInfo.InvariantCulture);
                    SafeCoreGenericFieldDefinition declared = FindField(nominal, name, field);
                    if (!seen.Add(name)) Fail(field, SafeCoreGenericDiagnosticCodes.BodyMismatch, "A constructor field is initialized more than once.");
                    Visible(declared.Declaration, field);
                    SafeCoreHirNode expression = node.Kind == N.StructExpression ? owner.Child(field, 0) : field;
                    RustType substituted = owner.Substitute(declared.Type, bindings);
                    RustType? target = ContainsCalleeParameter(substituted, nominal.Parameters, 0) ? null : substituted;
                    RustType actual = Expression(expression, target, depth + 1);
                    if (!actual.Equals(Never)) Infer(declared.Type, actual, bindings, expression, 0);
                    if (node.Kind == N.StructExpression)
                    {
                        types[field.Id] = actual;
                        if (diverges.Contains(expression.Id)) diverges.Add(field.Id);
                    }
                }
                if (nominal.Parameters.Any(parameter => !bindings.ContainsKey(parameter)))
                    Fail(path, SafeCoreGenericDiagnosticCodes.InvalidCall, "Constructor type arguments cannot be inferred from fields or the expected type.");
                RustType result = owner.Substitute(template, bindings);
                owner.Step(node);
                owner.WellFormed(result, definition.Bounds, 0);
                return result;
            }

            private RustType Member(SafeCoreHirNode node, int depth)
            {
                RustType receiver = Expression(owner.Child(node, 0), null, depth + 1);
                if (receiver.Name == "$tuple" && int.TryParse(node.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
                    index >= 0 && index < receiver.Arguments.Length) return receiver.Arguments[index];
                if (owner.definitions.TryGetValue(receiver.Name, out Declaration? nominal) && nominal.Node.Kind == N.Struct)
                {
                    SafeCoreGenericFieldDefinition field = FindField(nominal, node.Name!, node);
                    Visible(field.Declaration, node);
                    return owner.Substitute(field.Type, owner.BindArguments(nominal.Parameters, receiver.Arguments));
                }
                Fail(node, SafeCoreGenericDiagnosticCodes.BodyMismatch, "The receiver has no field with this name or tuple index.");
                return null!;
            }

            private SafeCoreGenericFieldDefinition FindField(Declaration nominal, string name, SafeCoreHirNode use)
            {
                foreach (SafeCoreGenericFieldDefinition field in nominal.FieldDefinitions)
                {
                    owner.Step(use);
                    if (field.Name == name) return field;
                }
                Fail(use, SafeCoreGenericDiagnosticCodes.BodyMismatch, "The nominal type has no such field.");
                return null!;
            }

            private void Visible(SafeCoreHirNode field, SafeCoreHirNode use)
            {
                owner.Step(use);
                string? allowed = field.DeclaredSymbol?.VisibilityScopePath;
                if (allowed is null) return;
                string module = definition.Node.DeclaredSymbol!.ScopePath;
                if (owner.CrateOf(module) != owner.CrateOf(allowed) || module != allowed && !module.StartsWith(allowed + "::", StringComparison.Ordinal))
                    Fail(use, SafeCoreGenericDiagnosticCodes.BodyMismatch, "The field is private to its declaring module.");
            }
        }
    }
}
