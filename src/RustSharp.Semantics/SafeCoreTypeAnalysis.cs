using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using RustSharp.Syntax;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

/// <summary>Independent limits for the check-only P1 type profile.</summary>
public sealed record SafeCoreTypeAnalysisOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumOperations { get; init; } = 1_000_000;
    public int MaximumNestingDepth { get; init; } = 128;
    /// <summary>Enables the P1 source profile's one-time uninitialized binding form.</summary>
    public bool EnableUninitializedBindings { get; init; }
}

/// <summary>Resolved type evidence, indexed by stable HIR node identity.</summary>
public sealed record SafeCoreTypeAnalysisProgram(
    SafeCoreHirResult Hir,
    IReadOnlyDictionary<int, SafeCoreType> Types,
    IReadOnlyDictionary<int, SafeCoreType> Coercions);

public sealed record SafeCoreTypeAnalysisResult(
    SafeCoreTypeAnalysisProgram? Program, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsSuccessful => Program is not null && Diagnostics.Count == 0;
}

/// <summary>
/// Structural type checking, separate from primitive IL emission and from the
/// later ownership/lifetime and trait passes. Success is type evidence only.
/// </summary>
public static partial class SafeCoreTypeAnalysis
{
    public const string Profile = "safe-core-types-v1";

    public static SafeCoreTypeAnalysisResult Check(SafeCoreHirResult hir,
        SafeCoreTypeAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hir);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1) ||
            options.MaximumOperations is < 1 or > 4_000_000 || options.MaximumNestingDepth is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!hir.IsSuccessful) return new(null, hir.Diagnostics);
        try { return new(new Checker(hir, options, cancellationToken).Run(), []); }
        catch (AnalysisException exception) { return new(null, [exception.Diagnostic with { SourcePath = hir.SourcePath }]); }
        catch (SafeCoreTypeInferenceLimitException)
        {
            return new(null, [new Diagnostic("RST0002", "Type inference exceeded its work, depth or time limit.", hir.Root!.Span)
                { SourcePath = hir.SourcePath }]);
        }
    }

    private sealed class AnalysisException(Diagnostic diagnostic) : Exception(diagnostic.Message)
    {
        public Diagnostic Diagnostic { get; } = diagnostic;
    }

    private sealed partial class Checker
    {
        private readonly SafeCoreHirResult _hir;
        private readonly SafeCoreTypeAnalysisOptions _options;
        private readonly CancellationToken _cancellation;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly SafeCoreTypeInference _inference;
        private readonly Dictionary<int, SafeCoreType> _types = [];
        private readonly Dictionary<int, SafeCoreType> _coercions = [];
        private readonly Dictionary<string, SafeCoreHirNode> _declarations = new(StringComparer.Ordinal);
        private readonly HashSet<string> _valueOnlyNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SafeCoreType> _namedTypes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SafeCoreType> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AdtShape> _constructors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SafeCoreHirNode> _adtOwners = new(StringComparer.Ordinal);
        private readonly HashSet<string> _definingAdts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<AdtShape>> _adts = new(StringComparer.Ordinal);
        private readonly Dictionary<SafeCoreSymbol, Binding> _bindings = [];
        // Rust permits a local declaration without an initializer when the
        // first assignment appears later in the same scope. Keep this fact
        // separate from mutability: the one initialization is legal even for
        // an otherwise immutable binding, while every later assignment still
        // requires the normal mutable-place check.
        private readonly HashSet<SafeCoreSymbol> _uninitialized = [];
        private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);
        private readonly List<(SafeCoreHirNode Node, SafeCoreType Type, BigInteger Value)> _integers = [];
        private readonly List<(SafeCoreHirNode Node, SafeCoreType Type, double Value)> _floats = [];
        private readonly List<(SafeCoreHirNode Node, SafeCoreType Source, SafeCoreType Target)> _casts = [];
        private readonly List<(SafeCoreHirNode Node, SafeCoreType Type, string Category)> _requirements = [];
        private readonly List<LoopContext> _loops = [];
        private readonly HashSet<int> _diverges = [];
        private SafeCoreType _returnType = Primitive(K.Unit);
        private string _module = "crate";
        private int _steps;

        private sealed record Binding(SafeCoreType Type, bool Mutable);
        private sealed record Field(SafeCoreHirNode Node, SafeCoreType Type);
        private sealed record AdtShape(SafeCoreHirNode Node, SafeCoreType Type, IReadOnlyList<Field> Fields);
        private sealed class LoopContext(SafeCoreHirNode node, SafeCoreType result, bool isWhile, bool hasExpected)
        {
            public SafeCoreHirNode Node { get; } = node;
            public SafeCoreType Result { get; set; } = result;
            public bool IsWhile { get; } = isWhile;
            public bool HasExpected { get; } = hasExpected;
            public bool HasBreak { get; set; }
            public List<(SafeCoreHirNode Node, SafeCoreType Type)> Breaks { get; } = [];
        }

        public Checker(SafeCoreHirResult hir, SafeCoreTypeAnalysisOptions options, CancellationToken cancellation)
        {
            _hir = hir;
            _options = options;
            _cancellation = cancellation;
            _inference = new(new SafeCoreTypeInferenceOptions
            {
                Timeout = options.Timeout, CancellationToken = cancellation,
                MaximumOperations = options.MaximumOperations, MaximumNestingDepth = options.MaximumNestingDepth,
            });
        }

        public SafeCoreTypeAnalysisProgram Run()
        {
            Collect(_hir.Root!, 0);
            foreach (SafeCoreHirNode node in _declarations.Values)
            {
                Step(node, 0);
                if (node.Kind is N.Struct or N.Enum or N.TypeAlias) _ = NamedType(node, 0);
            }
            foreach (SafeCoreHirNode node in _declarations.Values)
            {
                Step(node, 0);
                _module = ModuleOf(node.DeclaredSymbol!);
                if (node.Kind is N.Struct or N.Enum) DefineAdt(node);
                else if (node.Kind == N.Function) DefineFunction(node);
                else if (node.Kind == N.Const)
                {
                    SafeCoreHirNode typeNode = Parts(node).First(IsType);
                    SafeCoreType type = Type(typeNode, false, 0);
                    Sized(type, typeNode, 0);
                    _values[Key(node.DeclaredSymbol!)] = type;
                }
            }
            foreach (var entry in _adts)
                Layout(entry.Key, new HashSet<string>(StringComparer.Ordinal), _declarations[entry.Key], 0);
            foreach (SafeCoreHirNode node in _declarations.Values)
            {
                Step(node, 0);
                _module = ModuleOf(node.DeclaredSymbol!);
                if (node.Kind == N.Const)
                {
                    Expr(Parts(node).Last(), _values[Key(node.DeclaredSymbol!)], 0);
                    _ = ConstantItem(node, 0);
                }
                if (node.Kind != N.Function) continue;
                _bindings.Clear();
                _uninitialized.Clear();
                SafeCoreType signature = _values[Key(node.DeclaredSymbol!)];
                _returnType = signature.ReturnType!;
                int index = 0;
                foreach (SafeCoreHirNode parameter in Parts(node).Where(static part => part.Kind == N.Parameter))
                    Bind(Child(parameter, 0), signature.ParameterTypes[index++], 0);
                Expr(Parts(node).Last(), _returnType, 0);
                if (node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ConstFunction))
                    ValidateConstContext(node, 0);
            }
            foreach (SafeCoreHirNode block in _constBlocks.Values.ToArray())
            {
                Step(block, 0);
                ValidateConstContext(block, 0);
                _ = EvaluateConstant(Child(block, 0), new(null), 0);
            }
            ValidatePatternCoverage();
            foreach (var integer in _integers)
            {
                Step(integer.Node, 0);
                SafeCoreType type = _inference.Resolve(integer.Type, defaultNumerics: true);
                if (!Fits(integer.Value, type.Kind)) Fail(integer.Node, "RST2006", "Integer literal is outside the range of its inferred type.");
            }
            foreach (var literal in _floats)
            {
                Step(literal.Node, 0);
                SafeCoreType type = _inference.Resolve(literal.Type, defaultNumerics: true);
                if (type.Kind == K.F32 && !float.IsFinite((float)literal.Value))
                    Fail(literal.Node, "RST2006", "Float literal is outside the range of its inferred type.");
            }
            foreach (var cast in _casts)
            {
                Step(cast.Node, 0);
                SafeCoreType source = _inference.Resolve(cast.Source, defaultNumerics: true);
                if (!(IsNumeric(source.Kind) && IsNumeric(cast.Target.Kind) ||
                    source.Kind is K.Bool or K.Char && IsInteger(cast.Target.Kind) ||
                    source.Kind == K.U8 && cast.Target.Kind == K.Char ||
                    source.Kind == K.Function && cast.Target.Kind == K.Function && _inference.Coerce(source, cast.Target)))
                    Fail(cast.Node, "RST2002", "This cast is not supported between the specified types.");
            }
            foreach (var requirement in _requirements)
            {
                Step(requirement.Node, 0);
                SafeCoreType type = _inference.Resolve(requirement.Type, defaultNumerics: true);
                bool valid = requirement.Category switch
                {
                    "integer" => IsInteger(type.Kind),
                    "numeric" => IsNumeric(type.Kind),
                    "negate" => IsNumeric(type.Kind) && !IsUnsigned(type.Kind),
                    "not" => IsInteger(type.Kind) || type.Kind == K.Bool,
                    "compare" => IsNumeric(type.Kind) || type.Kind is K.Bool or K.Char,
                    _ => false,
                };
                if (!valid) Fail(requirement.Node, "RST2002", "Operator is not defined for this type in the type profile.");
            }
            var resolved = new Dictionary<int, SafeCoreType>();
            foreach (var entry in _types)
            {
                SafeCoreHirNode node = _hir.GetNode(entry.Key);
                Step(node, 0);
                SafeCoreType type = _inference.Resolve(entry.Value, defaultNumerics: true);
                RequireResolved(type, node, 0);
                resolved.Add(entry.Key, type);
            }
            var coercions = new Dictionary<int, SafeCoreType>();
            foreach (var entry in _coercions)
            {
                Step(_hir.GetNode(entry.Key), 0);
                coercions.Add(entry.Key, _inference.Resolve(entry.Value, defaultNumerics: true));
            }
            return new(_hir, new System.Collections.ObjectModel.ReadOnlyDictionary<int, SafeCoreType>(resolved),
                new System.Collections.ObjectModel.ReadOnlyDictionary<int, SafeCoreType>(coercions));
        }

        private void Collect(SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (node.Kind is N.CompilationUnit or N.Module or N.ImportGroup)
            {
                foreach (SafeCoreHirNode child in Parts(node)) Collect(child, depth + 1);
                return;
            }
            if (node.Kind == N.Implementation)
            {
                // The P1 Drop bridge lowers the supported associated method to
                // an ordinary function child. Keep implementation metadata
                // itself out of the value declaration table.
                foreach (SafeCoreHirNode child in Parts(node))
                    if (child.Kind == N.Function) Collect(child, depth + 1);
                return;
            }
            if (node.Kind is N.Import or N.Attribute) return;
            if (node.Kind is not (N.Function or N.Struct or N.Enum or N.TypeAlias or N.Const)) Unsupported(node);
            foreach (SafeCoreHirNode child in Parts(node))
            {
                Step(child, depth + 1);
                if (child.Kind == N.GenericParameter) Unsupported(child);
            }
            if (node.Kind is N.Function or N.Const) _valueOnlyNames.Add(node.DeclaredSymbol!.QualifiedName);
            _declarations.Add(Key(node.DeclaredSymbol!), node);
            if (node.Kind is N.Struct or N.Enum)
            {
                _adtOwners.Add(Key(node.DeclaredSymbol!), node);
                if (node.Kind == N.Enum)
                    foreach (SafeCoreHirNode variant in Parts(node).Where(static p => p.Kind == N.EnumVariant))
                    { Step(variant, depth); _adtOwners.Add(Key(variant.DeclaredSymbol!), node); }
            }
        }

        private SafeCoreType NamedType(SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            string key = Key(node.DeclaredSymbol!);
            if (_namedTypes.TryGetValue(key, out SafeCoreType? found)) return found;
            if (!_resolving.Add(key)) Fail(node, "RST2008", "Recursive type aliases are not permitted.");
            string savedModule = _module;
            _module = ModuleOf(node.DeclaredSymbol!);
            SafeCoreType type;
            try { type = node.Kind == N.TypeAlias ? Type(Parts(node).First(IsType), false, depth + 1) : SafeCoreType.Adt(key); }
            finally { _module = savedModule; }
            _resolving.Remove(key);
            _namedTypes.Add(key, type);
            _types[node.Id] = type;
            return type;
        }

        private void DefineAdt(SafeCoreHirNode node)
        {
            string key = Key(node.DeclaredSymbol!);
            if (_adts.ContainsKey(key)) return;
            if (!_definingAdts.Add(key)) Fail(node, "RST2008", "An ADT field has a recursive constant dependency.");
            string savedModule = _module;
            _module = ModuleOf(node.DeclaredSymbol!);
            try { DefineAdtCore(node); }
            finally { _module = savedModule; _definingAdts.Remove(key); }
        }

        private void EnsureConstructor(SafeCoreSymbol? symbol)
        {
            if (symbol is null) return;
            string key = Key(symbol);
            if (_adtOwners.TryGetValue(key, out var owner)) DefineAdt(owner);
            else if (_declarations.TryGetValue(key, out var alias) && alias.Kind == N.TypeAlias)
            {
                SafeCoreType type = NamedType(alias, 0);
                if (type.Kind == K.Adt) DefineAdt(_declarations[type.Name!]);
            }
        }

        private void DefineAdtCore(SafeCoreHirNode node)
        {
            SafeCoreType type = NamedType(node, 0);
            var shapes = new List<AdtShape>();
            foreach (SafeCoreHirNode shapeNode in node.Kind == N.Struct ? [node] : Parts(node).Where(static p => p.Kind == N.EnumVariant))
            {
                Step(shapeNode, 0);
                var fields = new List<Field>();
                foreach (SafeCoreHirNode field in Parts(shapeNode).Where(static p => p.Kind == N.Field))
                {
                    SafeCoreType fieldType = Type(Parts(field).First(IsType), false, 0);
                    Sized(fieldType, field, 0);
                    fields.Add(new(field, fieldType));
                    _types[field.Id] = fieldType;
                }
                var shape = new AdtShape(shapeNode, type, fields.AsReadOnly());
                shapes.Add(shape);
                string key = Key(shapeNode.DeclaredSymbol!);
                _constructors.Add(key, shape);
                if (shapeNode.Modifiers.HasFlag(SafeCoreHirNodeModifiers.TupleStruct))
                    _values[key] = SafeCoreType.Function(fields.Select(static f => f.Type).ToArray(), type, key);
                else if (shapeNode.Modifiers.HasFlag(SafeCoreHirNodeModifiers.UnitStruct)) _values[key] = type;
            }
            _adts.Add(type.Name!, shapes);
        }

        private void DefineFunction(SafeCoreHirNode node)
        {
            string savedModule = _module;
            _module = ModuleOf(node.DeclaredSymbol!);
            try { DefineFunctionCore(node); }
            finally { _module = savedModule; }
        }

        private void DefineFunctionCore(SafeCoreHirNode node)
        {
            if (_values.ContainsKey(Key(node.DeclaredSymbol!))) return;
            if (!_definingFunctions.Add(Key(node.DeclaredSymbol!)))
                Fail(node, "RST2008", "A function signature has a recursive constant dependency.");
            var parameters = new List<SafeCoreType>();
            SafeCoreType result = Primitive(K.Unit);
            foreach (SafeCoreHirNode child in Parts(node))
            {
                Step(child, 0);
                if (child.Kind == N.Parameter)
                {
                    SafeCoreType type = Type(Child(child, 1), false, 0);
                    Sized(type, child, 0);
                    parameters.Add(type);
                }
                else if (IsType(child)) result = Type(child, false, 0);
            }
            Sized(result, node, 0);
            if (node.DeclaredSymbol!.Name == "main" && node.DeclaredSymbol.ScopePath == _hir.NameResolution!.RootScope!.Path &&
                (parameters.Count != 0 || result.Kind != K.Unit))
                Fail(node, "RST2002", "The root main function requires no parameters and a unit return type.");
            SafeCoreType signature = SafeCoreType.Function(parameters, result, Key(node.DeclaredSymbol!));
            _values.Add(Key(node.DeclaredSymbol!), signature);
            _types[node.Id] = signature;
            _definingFunctions.Remove(Key(node.DeclaredSymbol!));
        }

        private SafeCoreType Type(SafeCoreHirNode node, bool allowInference, int depth)
        {
            Step(node, depth);
            SafeCoreType type;
            switch (node.Kind)
            {
                case N.UnitType: type = Primitive(K.Unit); break;
                case N.NeverType: type = Primitive(K.Never); break;
                case N.InferredType:
                    if (!allowInference) Fail(node, "RST2007", "Item signatures require explicit types.");
                    type = _inference.Fresh(); break;
                case N.PathType:
                    foreach (SafeCoreHirNode segment in Parts(node))
                        if (segment.ChildIds.Count != 0) Unsupported(segment);
                    if (node.ReferencedSymbol is not null && _declarations.TryGetValue(Key(node.ReferencedSymbol), out var declaration))
                        type = NamedType(declaration, depth + 1);
                    else if (Enum.TryParse(node.Name, ignoreCase: true, out K kind) && IsPrimitive(kind)) type = Primitive(kind);
                    else { Unsupported(node); return null!; }
                    TypeVisible(type, node, depth + 1);
                    break;
                case N.ReferenceType:
                    if (node.Value is not null) Unsupported(node);
                    type = SafeCoreType.Reference(Type(Child(node, 0), allowInference, depth + 1),
                        node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.MutableReference)); break;
                case N.TupleType:
                    SafeCoreType[] tupleParts = Parts(node).Select(p => Type(p, allowInference, depth + 1)).ToArray();
                    for (int i = 0; i + 1 < tupleParts.Length; i++) Sized(tupleParts[i], node, depth + 1);
                    type = SafeCoreType.Tuple(tupleParts); break;
                case N.ArrayType:
                    SafeCoreType arrayElement = Type(Child(node, 0), allowInference, depth + 1);
                    Sized(arrayElement, node, depth + 1);
                    type = SafeCoreType.Array(arrayElement, Length(Child(node, 1), depth + 1)); break;
                case N.SliceType:
                    SafeCoreType sliceElement = Type(Child(node, 0), allowInference, depth + 1);
                    Sized(sliceElement, node, depth + 1);
                    type = SafeCoreType.Slice(sliceElement); break;
                case N.FunctionType:
                    SafeCoreType[] parts = Parts(node).Select(p => Type(p, allowInference, depth + 1)).ToArray();
                    foreach (SafeCoreType part in parts) Sized(part, node, depth + 1);
                    type = SafeCoreType.Function(parts[..^1], parts[^1]); break;
                default: Unsupported(node); return null!;
            }
            _types[node.Id] = type;
            return type;
        }

        private SafeCoreType Expr(SafeCoreHirNode node, SafeCoreType? expected, int depth)
        {
            Step(node, depth);
            SafeCoreType type;
            switch (node.Kind)
            {
                case N.Attribute: type = Primitive(K.Unit); break;
                case N.Block:
                    type = Primitive(K.Unit);
                    bool diverges = false;
                    for (int i = 0; i < node.ChildIds.Count; i++)
                    {
                        SafeCoreHirNode child = Child(node, i);
                        bool tail = i == node.ChildIds.Count - 1 && !IsStatement(child);
                        type = Expr(child, tail ? expected : null, depth + 1);
                        diverges |= _diverges.Contains(child.Id);
                    }
                    if (node.ChildIds.Count == 0 || IsStatement(Child(node, node.ChildIds.Count - 1)))
                        type = Primitive(diverges ? K.Never : K.Unit);
                    if (diverges) _diverges.Add(node.Id);
                    break;
                case N.BlockExpression: type = Expr(Child(node, 0), expected, depth + 1); break;
                case N.ConstBlockExpression:
                    type = Expr(Child(node, 0), expected, depth + 1);
                    _constBlocks[node.Id] = node;
                    break;
                case N.ClosureExpression: type = Closure(node, expected, depth + 1); break;
                case N.MatchExpression: type = Match(node, expected, depth + 1); break;
                case N.LetStatement when node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasElse):
                    type = LetElse(node, depth + 1); break;
                case N.LetStatement:
                    int valueIndex = node.ChildIds.Count > 1 && IsType(Child(node, 1)) ? 2 : 1;
                    SafeCoreType? annotation = valueIndex == 2 ? Type(Child(node, 1), true, depth + 1) : null;
                    if (valueIndex >= node.ChildIds.Count)
                    {
                        SafeCoreHirNode declarationPattern = Child(node, 0);
                        if (!_options.EnableUninitializedBindings || node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasElse) ||
                            declarationPattern.Kind is not (N.IdentifierPattern or N.WildcardPattern))
                            Unsupported(node);
                        SafeCoreType declarationType = annotation ?? _inference.Fresh();
                        if (annotation is not null) Sized(declarationType, node, depth + 1);
                        Bind(declarationPattern, declarationType, depth + 1);
                        if (declarationPattern.Kind == N.IdentifierPattern && declarationPattern.DeclaredSymbol is not null)
                            _uninitialized.Add(declarationPattern.DeclaredSymbol);
                        type = Primitive(K.Unit);
                        break;
                    }
                    SafeCoreType valueType = Expr(Child(node, valueIndex), annotation, depth + 1);
                    SafeCoreType bindingType = annotation ?? valueType;
                    Sized(bindingType, node, depth + 1);
                    ValidatePatternPlace(Child(node, 0), Child(node, valueIndex), depth + 1);
                    Bind(Child(node, 0), bindingType, depth + 1);
                    type = Primitive(K.Unit);
                    break;
                case N.ReturnStatement:
                case N.ReturnExpression:
                    if (node.ChildIds.Count == 0) Require(Primitive(K.Unit), _returnType, node);
                    else Expr(Child(node, 0), _returnType, depth + 1);
                    type = Primitive(K.Never); break;
                case N.ExpressionStatement:
                    SafeCoreType expression = Expr(Child(node, 0), null, depth + 1);
                    if (!node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasSemicolon)) Require(expression, Primitive(K.Unit), node);
                    type = Primitive(K.Unit); break;
                case N.LiteralExpression: type = Literal(node, false); break;
                case N.NameExpression:
                    EnsureConstructor(node.ReferencedSymbol);
                    if (node.ReferencedSymbol is not null && _bindings.TryGetValue(node.ReferencedSymbol, out Binding? binding)) type = binding.Type;
                    else if (node.ReferencedSymbol is not null && _values.TryGetValue(Key(node.ReferencedSymbol), out SafeCoreType? value)) type = value;
                    else if (node.ReferencedSymbol is not null && _declarations.TryGetValue(Key(node.ReferencedSymbol), out var item) &&
                        item.Kind is N.Function or N.Const)
                    {
                        if (item.Kind == N.Function) DefineFunction(item);
                        else _values[Key(node.ReferencedSymbol)] = Type(Parts(item).First(IsType), false, depth + 1);
                        type = _values[Key(node.ReferencedSymbol)];
                    }
                    else { Unsupported(node); return null!; }
                    break;
                case N.TupleExpression:
                    if (node.ChildIds.Count == 1 && !node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma))
                        type = Expr(Child(node, 0), expected, depth + 1);
                    else
                    {
                        SafeCoreType? tupleExpected = expected is null ? null : _inference.Resolve(expected);
                        var elements = new List<SafeCoreType>();
                        for (int i = 0; i < node.ChildIds.Count; i++)
                            elements.Add(Expr(Child(node, i), tupleExpected?.Kind == K.Tuple && tupleExpected.Elements.Count == node.ChildIds.Count
                                ? tupleExpected.Elements[i] : null, depth + 1));
                        type = SafeCoreType.Tuple(elements);
                    }
                    break;
                case N.ArrayExpression: type = Array(node, expected, depth + 1); break;
                case N.UnaryExpression: type = Unary(node, expected, depth + 1); break;
                case N.BinaryExpression: type = Binary(node, depth + 1); break;
                case N.CallExpression:
                    // `array.len()` and `slice.len()` are bounded intrinsic
                    // calls in the P1 profile. They return usize and retain
                    // the array/slice target as a typed place; no method item
                    // or dynamic dispatch is introduced.
                    if (Child(node, 0).Kind == N.MemberExpression &&
                        string.Equals(Child(node, 0).Name, "len", StringComparison.Ordinal))
                    {
                        SafeCoreHirNode member = Child(node, 0);
                        if (node.ChildIds.Count != 1)
                            Fail(node, "RST2004", "The len intrinsic does not take arguments.");
                        SafeCoreType lengthTarget = AutoDeref(Expr(Child(member, 0), null, depth + 1), member, depth + 1);
                        if (lengthTarget.Kind is not (K.Array or K.Slice))
                            Fail(member, "RST2002", "The len intrinsic requires an array or slice target.");
                        type = Primitive(K.Usize);
                        break;
                    }
                    SafeCoreType callee = AutoDeref(Expr(Child(node, 0), null, depth + 1), node, depth + 1);
                    if (callee.Kind is not (K.Function or K.Closure)) Fail(node, "RST2002", "The call target is not callable.");
                    if (callee.Kind == K.Closure) ValidateClosureCall(callee, Child(node, 0), depth + 1);
                    if (node.ChildIds.Count - 1 != callee.ParameterTypes.Count) Fail(node, "RST2004", "Incorrect number of call arguments.");
                    if (callee.Name is not null && _constructors.TryGetValue(callee.Name, out AdtShape? constructor))
                        foreach (Field field in constructor.Fields)
                        {
                            Step(field.Node, depth);
                            Visible(field.Node.DeclaredSymbol ?? constructor.Node.DeclaredSymbol!, field.Node, node);
                        }
                    for (int i = 1; i < node.ChildIds.Count; i++) Expr(Child(node, i), callee.ParameterTypes[i - 1], depth + 1);
                    type = callee.ReturnType!; break;
                case N.PrintExpression: type = Print(node, depth + 1); break;
                case N.IfExpression:
                    Expr(Child(node, 0), Primitive(K.Bool), depth + 1);
                    SafeCoreType then = Expr(Child(node, 1), node.ChildIds.Count == 2 ? Primitive(K.Unit) : expected, depth + 1);
                    SafeCoreType other = node.ChildIds.Count == 3 ? Expr(Child(node, 2), expected, depth + 1) : Primitive(K.Unit);
                    type = Join(then, other, node, expected);
                    Require(then, type, Child(node, 1));
                    if (node.ChildIds.Count == 3) Require(other, type, Child(node, 2));
                    if (_diverges.Contains(Child(node, 0).Id) || node.ChildIds.Count == 3 &&
                        _diverges.Contains(Child(node, 1).Id) && _diverges.Contains(Child(node, 2).Id)) _diverges.Add(node.Id);
                    break;
                case N.IndexExpression:
                    SafeCoreType target = AutoDeref(Expr(Child(node, 0), null, depth + 1), node, depth + 1);
                    Expr(Child(node, 1), Primitive(K.Usize), depth + 1);
                    if (target.Kind is not (K.Array or K.Slice)) Fail(node, "RST2002", "Only arrays and slices can be indexed in this profile.");
                    type = target.ElementType!; break;
                case N.MemberExpression: type = Member(node, depth + 1); break;
                case N.StructExpression: type = Construct(node, depth + 1); break;
                case N.CastExpression:
                    SafeCoreType source = Expr(Child(node, 0), null, depth + 1);
                    type = Type(Child(node, 1), false, depth + 1);
                    _casts.Add((node, source, type));
                    break;
                case N.LoopExpression:
                case N.WhileExpression:
                    bool isWhile = node.Kind == N.WhileExpression;
                    var loop = new LoopContext(node, isWhile ? Primitive(K.Unit) : expected ?? _inference.Fresh(), isWhile, expected is not null || isWhile);
                    _loops.Add(loop);
                    if (isWhile) Expr(Child(node, 0), Primitive(K.Bool), depth + 1);
                    Expr(Child(node, isWhile ? 1 : 0), Primitive(K.Unit), depth + 1);
                    _loops.RemoveAt(_loops.Count - 1);
                    type = isWhile || loop.HasBreak ? loop.Result : Primitive(K.Never);
                    foreach (var site in loop.Breaks)
                    {
                        Step(site.Node, depth);
                        Require(site.Type, type, site.Node);
                    }
                    break;
                case N.BreakExpression:
                case N.ContinueExpression:
                    LoopContext context = FindLoop(node);
                    if (node.Kind == N.BreakExpression)
                    {
                        if (context.IsWhile && node.ChildIds.Count != 0) Unsupported(node);
                        SafeCoreType breakType = node.ChildIds.Count == 0 ? Primitive(K.Unit) :
                            Expr(Child(node, 0), context.HasExpected ? context.Result : null, depth + 1);
                        if (context.HasExpected) Require(breakType, context.Result, node);
                        else context.Result = context.HasBreak ? Join(context.Result, breakType, node, null) : breakType;
                        context.HasBreak = true;
                        context.Breaks.Add((node.ChildIds.Count == 0 ? node : Child(node, 0), breakType));
                    }
                    type = Primitive(K.Never); break;
                default: Unsupported(node); return null!;
            }
            TypeVisible(type, node, depth + 1);
            _types[node.Id] = type;
            if (type.Kind == K.Never) _diverges.Add(node.Id);
            if (node.Kind is not (N.IfExpression or N.LoopExpression or N.WhileExpression or N.ClosureExpression or N.MatchExpression) &&
                !(node.Kind == N.LetStatement && node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasElse)) &&
                !(node.Kind == N.BinaryExpression && node.Value is "&&" or "||"))
                foreach (int child in node.ChildIds)
                {
                    Step(node, depth);
                    if (_diverges.Contains(child)) _diverges.Add(node.Id);
                }
            if (expected is not null) Require(type, expected, node);
            return expected is not null && type.Kind != K.Never ? expected : type;
        }

        private SafeCoreType Array(SafeCoreHirNode node, SafeCoreType? expected, int depth)
        {
            SafeCoreType? target = expected is null ? null : _inference.Resolve(expected);
            bool hasExpected = target?.Kind == K.Array;
            SafeCoreType element = hasExpected ? target!.ElementType : _inference.Fresh();
            bool repeated = node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.RepeatedArray);
            long length = repeated ? Length(Child(node, 1), depth + 1) : node.ChildIds.Count;
            int count = repeated ? 1 : node.ChildIds.Count;
            for (int i = 0; i < count; i++)
            {
                SafeCoreType item = Expr(Child(node, i), hasExpected ? element : null, depth + 1);
                if (!hasExpected) element = i == 0 ? item : Join(element, item, node, null);
            }
            for (int i = 0; i < count; i++)
            {
                SafeCoreHirNode item = Child(node, i);
                Step(item, depth);
                Require(_types[item.Id], element, item);
            }
            if (repeated && length > 1 && !CopyShape(_inference.Resolve(element), node, depth + 1))
                Unsupported(node); // Trait-based Copy obligations belong to P1-05.
            return SafeCoreType.Array(element, length);
        }

        private SafeCoreType Unary(SafeCoreHirNode node, SafeCoreType? expected, int depth)
        {
            SafeCoreHirNode operand = Child(node, 0);
            SafeCoreHirNode literalOperand = operand;
            for (int i = 0; i <= _options.MaximumNestingDepth && literalOperand.Kind == N.TupleExpression &&
                literalOperand.ChildIds.Count == 1 && !literalOperand.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma); i++)
            {
                Step(literalOperand, depth + i);
                literalOperand = Child(literalOperand, 0);
            }
            if (node.Value == "-" && literalOperand.Kind == N.LiteralExpression && char.IsAsciiDigit(literalOperand.Value![0]))
            {
                SafeCoreType literal = Literal(literalOperand, true);
                SafeCoreHirNode part = operand;
                for (int i = 0; i <= _options.MaximumNestingDepth; i++)
                {
                    Step(part, depth + i);
                    _types[part.Id] = literal;
                    if (part.Id == literalOperand.Id) break;
                    part = Child(part, 0);
                }
                _requirements.Add((node, literal, "negate"));
                return literal;
            }
            SafeCoreType? expectedInner = expected is null ? null : _inference.Resolve(expected);
            SafeCoreType type = Expr(operand,
                node.Value is "&" or "&mut" && expectedInner?.Kind == K.Reference && expectedInner.ElementType!.Kind != K.Slice
                    ? expectedInner.ElementType : node.Value is "-" or "!" ? expected : null, depth + 1);
            switch (node.Value)
            {
                case "&": return SafeCoreType.Reference(type, false);
                case "&mut":
                    RequireMutable(operand, allowTemporary: true, depth + 1);
                    return SafeCoreType.Reference(type, true);
                case "*":
                    SafeCoreType reference = _inference.Resolve(type);
                    if (reference.Kind != K.Reference) Fail(node, "RST2002", "Dereference requires a reference.");
                    return reference.ElementType!;
                case "-": _requirements.Add((node, type, "negate")); return type;
                case "!": _requirements.Add((node, type, "not")); return type;
                default: Unsupported(node); return null!;
            }
        }

        private SafeCoreType Binary(SafeCoreHirNode node, int depth)
        {
            string op = node.Value!;
            SafeCoreHirNode leftNode = Child(node, 0);
            SafeCoreHirNode rightNode = Child(node, 1);
            SafeCoreType left = Expr(leftNode, op is "&&" or "||" ? Primitive(K.Bool) : null, depth + 1);
            if (op == "=")
            {
                bool firstInitialization = leftNode.Kind == N.NameExpression && leftNode.ReferencedSymbol is not null &&
                    _uninitialized.Remove(leftNode.ReferencedSymbol);
                if (!firstInitialization) RequireMutable(leftNode, false, depth + 1);
                Expr(rightNode, left, depth + 1);
                return Primitive(K.Unit);
            }
            bool compound = op is "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=";
            if (compound) { RequireMutable(leftNode, false, depth + 1); op = op[..^1]; }
            SafeCoreType right = Expr(rightNode, op is "&&" or "||" ? Primitive(K.Bool) : null, depth + 1);
            if (left.Kind == K.Never && right.Kind == K.Never) return Primitive(K.Never);
            if (left.Kind == K.Never) { Require(left, right, leftNode); left = right; }
            if (right.Kind == K.Never) { Require(right, left, rightNode); right = left; }
            if (op is not ("<<" or ">>")) Equal(left, right, node);
            string requirement = op switch
            {
                "+" or "-" or "*" or "/" or "%" => "numeric",
                "&" or "|" or "^" => "not",
                "<<" or ">>" => "integer",
                "==" or "!=" or "<" or ">" or "<=" or ">=" => "compare",
                "&&" or "||" => "",
                _ => "unsupported",
            };
            if (requirement == "unsupported") Unsupported(node);
            if (requirement.Length != 0) _requirements.Add((node, left, requirement));
            if (op is "<<" or ">>") _requirements.Add((node, right, "integer"));
            return compound ? Primitive(K.Unit) : op is "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||"
                ? Primitive(K.Bool) : left;
        }

        private SafeCoreType Member(SafeCoreHirNode node, int depth)
        {
            SafeCoreType target = AutoDeref(Expr(Child(node, 0), null, depth + 1), node, depth + 1);
            if (target.Kind == K.Tuple && int.TryParse(node.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
                index >= 0 && index < target.Elements.Count) return target.Elements[index];
            if (target.Kind == K.Adt && _adts.TryGetValue(target.Name!, out var shapes) &&
                _declarations[target.Name!].Kind == N.Struct)
            {
                Field field = FindField(shapes[0], node.Name!, node);
                Visible(field.Node.DeclaredSymbol ?? shapes[0].Node.DeclaredSymbol!, field.Node, node);
                return field.Type;
            }
            Fail(node, "RST2002", "No such field exists on this type."); return null!;
        }

        private SafeCoreType Construct(SafeCoreHirNode node, int depth)
        {
            EnsureConstructor(node.ReferencedSymbol);
            AdtShape? shape = null;
            if (node.ReferencedSymbol is not null)
            {
                _constructors.TryGetValue(Key(node.ReferencedSymbol), out shape);
                if (shape is null && _namedTypes.TryGetValue(Key(node.ReferencedSymbol), out SafeCoreType? aliased) &&
                    aliased.Kind == K.Adt && _declarations[aliased.Name!].Kind == N.Struct)
                    shape = _adts[aliased.Name!][0];
            }
            if (shape is null)
                Fail(node, "RST2002", "Struct construction requires a struct or enum variant.");
            TypeVisible(shape.Type, node, depth + 1);
            if (shape.Node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.TupleStruct)) Unsupported(node);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (SafeCoreHirNode field in Parts(node))
            {
                Step(field, depth);
                if (field.Kind != N.StructExpressionField)
                {
                    if (shape.Node.Kind != N.Struct) Unsupported(node);
                    Expr(field, shape.Type, depth + 1); continue;
                }
                if (!seen.Add(Canonical(field.Name!))) Fail(field, "RST2002", "A field is initialized more than once.");
                Field declared = FindField(shape, field.Name!, field);
                Visible(declared.Node.DeclaredSymbol ?? shape.Node.DeclaredSymbol!, declared.Node, field);
                Expr(Child(field, 0), declared.Type, depth + 1);
                _types[field.Id] = declared.Type;
            }
            if (!node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.StructUpdate) && seen.Count != shape.Fields.Count)
                Fail(node, "RST2002", "Struct construction must initialize every field.");
            if (node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.StructUpdate))
                foreach (Field field in shape.Fields)
                {
                    Step(field.Node, depth);
                    Visible(field.Node.DeclaredSymbol ?? shape.Node.DeclaredSymbol!, field.Node, node);
                }
            return shape.Type;
        }

        private Field FindField(AdtShape shape, string name, SafeCoreHirNode use)
        {
            for (int i = 0; i < shape.Fields.Count; i++)
            {
                Step(use, 0);
                Field field = shape.Fields[i];
                if (field.Node.Name is not null && Canonical(field.Node.Name) == Canonical(name) ||
                    field.Node.Name is null && i.ToString(CultureInfo.InvariantCulture) == name) return field;
            }
            Fail(use, "RST2002", "No such field exists on this type."); return null!;
        }

        private void Visible(SafeCoreSymbol symbol, SafeCoreHirNode declaration, SafeCoreHirNode use)
        {
            if (declaration.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Public) ||
                declaration.DeclaredSymbol is not null && symbol.VisibilityScopePath is null || symbol.Kind == SafeCoreSymbolKind.EnumVariant) return;
            string module = declaration.DeclaredSymbol is null ? ModuleOf(symbol) : symbol.VisibilityScopePath ?? ModuleOf(symbol);
            if (_module != module && !_module.StartsWith(module + "::", StringComparison.Ordinal))
                Fail(use, "RST2002", "The field is private to its declaring module.");
        }

        private void TypeVisible(SafeCoreType type, SafeCoreHirNode use, int depth)
        {
            Step(use, depth);
            type = _inference.Resolve(type);
            if (type.Kind == K.Adt && _declarations.TryGetValue(type.Name!, out var declaration))
                Visible(declaration.DeclaredSymbol!, declaration, use);
            foreach (SafeCoreType part in type.Elements) TypeVisible(part, use, depth + 1);
        }

        private string ModuleOf(SafeCoreSymbol symbol)
        {
            foreach (SafeCoreScope scope in _hir.NameResolution!.Scopes)
            {
                Step(_hir.Root!, 0);
                if (scope.Path == symbol.ScopePath) return scope.ModulePath;
            }
            return symbol.ScopePath;
        }

        private void Bind(SafeCoreHirNode pattern, SafeCoreType type, int depth)
        {
            BindPattern(pattern, type, refutable: false, depth);
        }

        private void RequireMutable(SafeCoreHirNode node, bool allowTemporary, int depth)
        {
            Step(node, depth);
            if (node.Kind == N.NameExpression)
            {
                if (node.ReferencedSymbol is not null && _bindings.TryGetValue(node.ReferencedSymbol, out var binding) && binding.Mutable) return;
            }
            else if (node.Kind == N.TupleExpression && node.ChildIds.Count == 1 && !node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma))
            { RequireMutable(Child(node, 0), allowTemporary, depth + 1); return; }
            else if (node.Kind == N.UnaryExpression && node.Value == "*")
            {
                SafeCoreType reference = _inference.Resolve(_types[Child(node, 0).Id]);
                if (reference.Kind == K.Reference && reference.IsMutable)
                { MutableReferenceAccess(Child(node, 0), depth + 1); return; }
            }
            else if (node.Kind is N.MemberExpression or N.IndexExpression)
            {
                SafeCoreHirNode target = Child(node, 0);
                SafeCoreType targetType = _inference.Resolve(_types[target.Id]);
                bool dereferenced = false;
                for (int i = 0; i <= _options.MaximumNestingDepth && targetType.Kind == K.Reference; i++)
                {
                    Step(node, depth + i);
                    if (!targetType.IsMutable) Fail(node, "RST2003", "Cannot mutate through a shared reference.");
                    targetType = _inference.Resolve(targetType.ElementType!);
                    dereferenced = true;
                }
                if (dereferenced) { MutableReferenceAccess(target, depth + 1); return; }
                RequireMutable(target, allowTemporary, depth + 1); return;
            }
            else if (allowTemporary) return;
            Fail(node, "RST2003", "Assignment or mutable borrowing requires a mutable place.");
        }

        // A mutable reference behind a shared reference is not a writable place.
        // This checks place accessibility only, without proving loans or lifetimes.
        private void MutableReferenceAccess(SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (node.Kind == N.TupleExpression && node.ChildIds.Count == 1 &&
                !node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma))
            { MutableReferenceAccess(Child(node, 0), depth + 1); return; }
            if (node.Kind is N.MemberExpression or N.IndexExpression || node.Kind == N.UnaryExpression && node.Value == "*")
            {
                SafeCoreHirNode target = Child(node, 0);
                SafeCoreType type = _inference.Resolve(_types[target.Id]);
                for (int i = 0; i <= _options.MaximumNestingDepth && type.Kind == K.Reference; i++)
                {
                    Step(node, depth + i);
                    if (!type.IsMutable) Fail(node, "RST2003", "Cannot access a mutable referent through a shared reference.");
                    type = _inference.Resolve(type.ElementType);
                    if (node.Kind == N.UnaryExpression) break;
                }
                MutableReferenceAccess(target, depth + 1);
            }
        }

        private SafeCoreType Literal(SafeCoreHirNode node, bool negative)
        {
            string raw = node.Value!;
            if (raw is "true" or "false") return Primitive(K.Bool);
            if (raw.StartsWith('\'')) return Primitive(K.Char);
            if (raw.StartsWith("b'", StringComparison.Ordinal)) return Primitive(K.U8);
            if (raw.StartsWith('"') || raw.StartsWith('r')) return SafeCoreType.Reference(Primitive(K.Str), false);
            if (raw.StartsWith('b') || raw.StartsWith('c')) Unsupported(node);
            string text = raw.Replace("_", "", StringComparison.Ordinal);
            int radix = text.StartsWith("0x", StringComparison.Ordinal) ? 16 : text.StartsWith("0o", StringComparison.Ordinal) ? 8 :
                text.StartsWith("0b", StringComparison.Ordinal) ? 2 : 10;
            int start = radix == 10 ? 0 : 2;
            int end = start;
            for (; end < text.Length; end++)
            {
                Step(node, 0);
                int digit = Digit(text[end]);
                if (digit < 0 || digit >= radix) break;
            }
            string suffix = text[end..];
            bool floating = radix == 10 && (suffix.StartsWith('.') || suffix.StartsWith('e') || suffix.StartsWith('E') || suffix is "f32" or "f64");
            if (floating)
            {
                K? explicitFloat = text.EndsWith("f32", StringComparison.Ordinal) ? K.F32 : text.EndsWith("f64", StringComparison.Ordinal) ? K.F64 : null;
                string number = explicitFloat.HasValue ? text[..^3] : text;
                if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
                    !double.IsFinite(parsed) || explicitFloat == K.F32 && !float.IsFinite((float)parsed))
                    Fail(node, "RST2006", "Float literal is outside the supported finite range.");
                SafeCoreType floatType = explicitFloat.HasValue ? Primitive(explicitFloat.Value) : _inference.Fresh(SafeCoreInferenceKind.Float);
                _floats.Add((node, floatType, parsed));
                return floatType;
            }
            if (end == start) Unsupported(node);
            SafeCoreType type;
            if (suffix.Length == 0) type = _inference.Fresh(SafeCoreInferenceKind.Integer);
            else if (Enum.TryParse(suffix, true, out K kind) && IsInteger(kind)) type = Primitive(kind);
            else { Unsupported(node); return null!; }
            BigInteger value = 0;
            for (int i = start; i < end; i++)
            {
                Step(node, 0);
                value = value * radix + Digit(text[i]);
                if (value.GetBitLength() > 128) Fail(node, "RST2006", "Integer literal exceeds 128 bits.");
            }
            _integers.Add((node, type, negative ? -value : value));
            return type;
        }

        private long Length(SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            Expr(node, Primitive(K.Usize), depth + 1);
            ValidateConstContext(node, depth + 1);
            Constant value = EvaluateConstant(node, new ConstantEnvironment(null), depth + 1);
            if (value.Value is not BigInteger) Fail(node, "RST2010", "An array length requires an integer constant.");
            BigInteger length = (BigInteger)value.Value!;
            if (length < 0 || length > int.MaxValue) Fail(node, "RST2006", "Array lengths in this profile must fit a nonnegative i32.");
            return (long)length;
        }

        private SafeCoreType Join(SafeCoreType left, SafeCoreType right, SafeCoreHirNode node, SafeCoreType? expected)
        {
            if (expected is not null) { Require(left, expected, node); Require(right, expected, node); return expected; }
            if (left.Kind == K.Never) return right;
            if (right.Kind == K.Never) return left;
            if (_inference.Coerce(right, left)) return left;
            if (_inference.Coerce(left, right)) return right;
            SafeCoreType a = _inference.Resolve(left);
            SafeCoreType b = _inference.Resolve(right);
            if (a.Kind is K.Function or K.Closure && b.Kind is K.Function or K.Closure)
            {
                SafeCoreType pointer = SafeCoreType.Function(a.ParameterTypes, a.ReturnType!);
                if (_inference.Coerce(a, pointer) && _inference.Coerce(b, pointer)) return pointer;
            }
            Fail(node, "RST2002", "Conditional branches have incompatible types."); return null!;
        }

        private LoopContext FindLoop(SafeCoreHirNode node)
        {
            for (int i = _loops.Count - 1; i >= 0; i--)
            {
                Step(node, 0);
                if (node.Name is null || node.Name == _loops[i].Node.Name) return _loops[i];
            }
            Fail(node, "RST2002", "Break or continue has no matching enclosing loop."); return null!;
        }

        private SafeCoreType AutoDeref(SafeCoreType type, SafeCoreHirNode node, int depth)
        {
            type = _inference.Resolve(type);
            for (int i = 0; i <= _options.MaximumNestingDepth && type.Kind == K.Reference; i++)
            { Step(node, depth + i); type = _inference.Resolve(type.ElementType!); }
            return type;
        }

        private void Sized(SafeCoreType type, SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            type = _inference.Resolve(type);
            if (type.Kind is K.Str or K.Slice) Fail(node, "RST2005", "An unsized str or slice requires a reference in this profile.");
            if (type.Kind is K.Tuple or K.Array)
                foreach (SafeCoreType element in type.Elements) Sized(element, node, depth + 1);
        }

        private void Layout(string name, HashSet<string> path, SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (!path.Add(name)) Fail(node, "RST2008", "An ADT has an infinitely recursive value layout; use a reference for indirection.");
            foreach (AdtShape shape in _adts[name])
                foreach (Field field in shape.Fields) LayoutType(field.Type, path, field.Node, depth + 1);
            path.Remove(name);
        }

        private void LayoutType(SafeCoreType type, HashSet<string> path, SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (type.Kind == K.Adt) Layout(type.Name!, path, node, depth + 1);
            else if (type.Kind is K.Tuple or K.Array)
                foreach (SafeCoreType child in type.Elements) LayoutType(child, path, node, depth + 1);
        }

        private bool CopyShape(SafeCoreType type, SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (type.Kind == K.Closure) return !type.HasCaptures;
            if (type.Kind == K.Reference) return !type.IsMutable;
            if (type.Kind is K.Tuple or K.Array)
            {
                foreach (SafeCoreType child in type.Elements) if (!CopyShape(child, node, depth + 1)) return false;
                return true;
            }
            return type.Kind != K.Adt;
        }

        private void RequireResolved(SafeCoreType type, SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (type.Kind == K.Inference) Fail(node, "RST2007", "The type cannot be inferred; add an annotation.");
            foreach (SafeCoreType child in type.Elements) RequireResolved(child, node, depth + 1);
        }

        private void Require(SafeCoreType source, SafeCoreType target, SafeCoreHirNode node)
        {
            if (!_inference.Coerce(source, target)) Fail(node, "RST2002", "The expression cannot be coerced to the expected type.");
            _coercions[node.Id] = target;
        }

        private void Equal(SafeCoreType left, SafeCoreType right, SafeCoreHirNode node)
        {
            if (!_inference.Unify(left, right)) Fail(node, "RST2002", "The operands have incompatible types.");
        }

        private void Step(SafeCoreHirNode node, int depth)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (++_steps > _options.MaximumOperations || depth > _options.MaximumNestingDepth || _clock.Elapsed >= _options.Timeout)
                Fail(node, "RST0002", "Type analysis exceeded its work, nesting or time limit.");
        }

        private IEnumerable<SafeCoreHirNode> Parts(SafeCoreHirNode node) => node.ChildIds.Select(_hir.GetNode);
        private SafeCoreHirNode Child(SafeCoreHirNode node, int index) => _hir.GetNode(node.ChildIds[index]);
        private string Key(SafeCoreSymbol symbol)
        {
            string name = symbol.ResolvedImportTargetQualifiedName ?? symbol.QualifiedName;
            return symbol.Kind is SafeCoreSymbolKind.Function or SafeCoreSymbolKind.Const ||
                symbol.IsImport && symbol.Namespace == SafeCoreSymbolNamespace.Value && _valueOnlyNames.Contains(name)
                ? name + "#value" : name;
        }
        private static string Canonical(string name) => (name.StartsWith("r#", StringComparison.Ordinal) ? name[2..] : name).Normalize(NormalizationForm.FormC);
        private static SafeCoreType Primitive(K kind) => SafeCoreType.Primitive(kind);
        private static bool IsPrimitive(K kind) => kind is >= K.Unit and <= K.Never;
        private static bool IsInteger(K kind) => kind is K.I8 or K.I16 or K.I32 or K.I64 or K.I128 or K.Isize or K.U8 or K.U16 or K.U32 or K.U64 or K.U128 or K.Usize;
        private static bool IsUnsigned(K kind) => kind is K.U8 or K.U16 or K.U32 or K.U64 or K.U128 or K.Usize;
        private static bool IsNumeric(K kind) => IsInteger(kind) || kind is K.F32 or K.F64;
        private static bool Fits(BigInteger value, K kind)
        {
            if (!IsInteger(kind)) return false;
            int bits = kind switch { K.I8 or K.U8 => 8, K.I16 or K.U16 => 16, K.I32 or K.U32 => 32, K.I128 or K.U128 => 128, _ => 64 };
            return IsUnsigned(kind) ? value >= 0 && value < BigInteger.One << bits :
                value >= -(BigInteger.One << (bits - 1)) && value < BigInteger.One << (bits - 1);
        }
        private static int Digit(char c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
        private static bool IsType(SafeCoreHirNode node) => node.Kind is N.PathType or N.ReferenceType or N.TupleType or N.ArrayType or N.SliceType or N.UnitType or N.NeverType or N.FunctionType or N.InferredType;
        private static bool IsStatement(SafeCoreHirNode node) => node.Kind is N.LetStatement or N.ReturnStatement or N.ExpressionStatement or N.Attribute;
        [DoesNotReturn]
        private static void Unsupported(SafeCoreHirNode node) => Fail(node, "RST2001", $"This {node.Kind} form is outside {Profile}.");
        [DoesNotReturn]
        private static void Fail(SafeCoreHirNode node, string code, string message) => throw new AnalysisException(new(code, message, node.Span));
    }
}
