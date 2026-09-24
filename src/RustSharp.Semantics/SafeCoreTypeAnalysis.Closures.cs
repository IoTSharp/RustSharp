using RustSharp.Syntax;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreTypeAnalysis
{
    private sealed partial class Checker
    {
        private SafeCoreType Closure(SafeCoreHirNode node, SafeCoreType? expected, int depth)
        {
            Step(node, depth);
            SafeCoreType? signature = expected is null ? null : _inference.Resolve(expected);
            if (signature?.Kind != K.Function) signature = null;
            SafeCoreHirNode[] parameters = Parts(node).Where(static child => child.Kind == N.Parameter).ToArray();
            if (signature is not null && signature.ParameterTypes.Count != parameters.Length)
                Fail(node, "RST2004", "The closure has a different parameter count from the expected function pointer.");
            SafeCoreType[] parameterTypes = new SafeCoreType[parameters.Length];
            SafeCoreType? explicitReturn = null;
            foreach (SafeCoreHirNode child in Parts(node))
            {
                Step(child, depth);
                if (IsType(child)) explicitReturn = Type(child, true, depth + 1);
            }
            SafeCoreType result = explicitReturn ?? signature?.ReturnType ?? _inference.Fresh();
            if (explicitReturn is not null && signature is not null) Equal(explicitReturn, signature.ReturnType, node);
            var savedBindings = new Dictionary<SafeCoreSymbol, Binding>(_bindings);
            SafeCoreType savedReturn = _returnType;
            LoopContext[] savedLoops = _loops.ToArray();
            SafeCoreHirNode body = Parts(node).Last();
            var captures = new HashSet<SafeCoreSymbol>();
            var mutableCaptures = new HashSet<SafeCoreSymbol>();
            CaptureUses(body, savedBindings, captures, mutableCaptures, depth + 1);
            try
            {
                _returnType = result;
                _loops.Clear();
                for (var index = 0; index < parameters.Length; index++)
                {
                    SafeCoreHirNode parameter = parameters[index];
                    Step(parameter, depth);
                    SafeCoreType type = parameter.ChildIds.Count > 1
                        ? Type(Child(parameter, 1), true, depth + 1)
                        : signature?.ParameterTypes[index] ?? _inference.Fresh();
                    if (signature is not null) Equal(type, signature.ParameterTypes[index], parameter);
                    Sized(type, parameter, depth + 1);
                    BindPattern(Child(parameter, 0), type, refutable: false, depth + 1);
                    parameterTypes[index] = type;
                    _types[parameter.Id] = type;
                }
                SafeCoreType bodyType = Expr(body, result, depth + 1);
                if (bodyType.Kind == K.Never && _inference.Resolve(result).Kind == K.Inference)
                    Equal(result, Primitive(K.Never), body);
                Sized(_inference.Resolve(result), node, depth + 1);
            }
            finally
            {
                _returnType = savedReturn;
                _loops.Clear();
                _loops.AddRange(savedLoops);
                _bindings.Clear();
                foreach (var binding in savedBindings) _bindings.Add(binding.Key, binding.Value);
            }
            return SafeCoreType.Closure(parameterTypes, result, $"{_module}::closure#{node.Id}",
                captures.Count != 0, mutableCaptures.Count != 0, _cancellation);
        }

        private void CaptureUses(SafeCoreHirNode node, IReadOnlyDictionary<SafeCoreSymbol, Binding> outer,
            HashSet<SafeCoreSymbol> captures, HashSet<SafeCoreSymbol> mutableCaptures, int depth)
        {
            Step(node, depth);
            if (node.Kind == N.NameExpression && node.ReferencedSymbol is { } symbol && outer.ContainsKey(symbol))
                captures.Add(symbol);
            bool writes = node.Kind == N.BinaryExpression && node.Value is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=";
            bool borrows = node.Kind == N.UnaryExpression && node.Value == "&mut";
            if (writes || borrows)
            {
                SafeCoreSymbol? root = CaptureRoot(Child(node, 0), depth + 1);
                if (root is not null && outer.ContainsKey(root)) mutableCaptures.Add(root);
            }
            if (node.Kind == N.CallExpression)
            {
                SafeCoreSymbol? root = CaptureRoot(Child(node, 0), depth + 1);
                if (root is not null && outer.TryGetValue(root, out Binding? binding))
                {
                    SafeCoreType type = _inference.Resolve(binding.Type);
                    if (type.Kind == K.Closure && type.IsMutable) mutableCaptures.Add(root);
                }
                if (Child(node, 0).ReferencedSymbol is { } callee && _values.TryGetValue(Key(callee), out SafeCoreType? signature) &&
                    signature.Kind == K.Function)
                {
                    for (int argumentIndex = 1; argumentIndex < node.ChildIds.Count && argumentIndex <= signature.ParameterTypes.Count; argumentIndex++)
                    {
                        Step(node, depth);
                        SafeCoreType parameter = signature.ParameterTypes[argumentIndex - 1];
                        if (parameter.Kind != K.Reference || !parameter.IsMutable) continue;
                        SafeCoreSymbol? argumentRoot = CaptureRoot(Child(node, argumentIndex), depth + 1);
                        if (argumentRoot is not null && outer.ContainsKey(argumentRoot)) mutableCaptures.Add(argumentRoot);
                    }
                }
            }
            foreach (SafeCoreHirNode child in Parts(node)) CaptureUses(child, outer, captures, mutableCaptures, depth + 1);
        }

        private SafeCoreSymbol? CaptureRoot(SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (node.Kind == N.NameExpression) return node.ReferencedSymbol;
            if (node.Kind is N.MemberExpression or N.IndexExpression || node.Kind == N.UnaryExpression && node.Value == "*" ||
                node.Kind == N.TupleExpression && node.ChildIds.Count == 1 && !node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma))
                return CaptureRoot(Child(node, 0), depth + 1);
            return null;
        }

        private void ValidateClosureCall(SafeCoreType callee, SafeCoreHirNode calleeNode, int depth)
        {
            Step(calleeNode, depth);
            if (callee.Kind != K.Closure || !callee.IsMutable) return;
            SafeCoreType actual = _inference.Resolve(_types[calleeNode.Id]);
            bool throughReference = false;
            for (var index = 0; index <= _options.MaximumNestingDepth && actual.Kind == K.Reference; index++)
            {
                Step(calleeNode, depth + index);
                if (!actual.IsMutable) Fail(calleeNode, "RST2003", "A mutating closure cannot be called through a shared reference.");
                throughReference = true;
                actual = _inference.Resolve(actual.ElementType);
            }
            if (!throughReference) RequireMutable(calleeNode, allowTemporary: true, depth + 1);
        }
    }
}
