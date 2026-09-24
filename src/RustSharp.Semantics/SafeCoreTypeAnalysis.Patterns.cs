using System.Globalization;
using System.Numerics;
using System.Text;
using RustSharp.Syntax;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

public static partial class SafeCoreTypeAnalysis
{
    private sealed partial class Checker
    {
        private enum BindingMode { Value, Shared, Mutable }
        private enum CoverageKind { Any, Constructor, Alternative, Interval, Opaque, Slice }
        private sealed record Coverage(CoverageKind Kind, IReadOnlyList<Coverage> Children, string? Tag = null,
            BigInteger? Lower = null, BigInteger? Upper = null, int Rest = -1)
        {
            public static Coverage Any { get; } = new(CoverageKind.Any, []);
        }
        private sealed record CoverageObligation(SafeCoreHirNode Node, SafeCoreType Type, IReadOnlyList<Coverage> Patterns, bool Match);
        private sealed record PatternConstructor(string Tag, IReadOnlyList<SafeCoreType> Fields, BigInteger? Scalar = null);
        private readonly List<CoverageObligation> _coverageObligations = [];
        private int _sharedPatternReferences;

        private void BindPattern(SafeCoreHirNode pattern, SafeCoreType type, bool refutable, int depth)
        {
            Coverage coverage = BindPatternCore(pattern, type, BindingMode.Value, depth);
            if (!refutable) _coverageObligations.Add(new(pattern, type, [coverage], false));
        }

        private SafeCoreType LetElse(SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            int initializerIndex = node.ChildIds.Count > 1 && IsType(Child(node, 1)) ? 2 : 1;
            SafeCoreType? annotation = initializerIndex == 2 ? Type(Child(node, 1), true, depth + 1) : null;
            SafeCoreType value = Expr(Child(node, initializerIndex), annotation, depth + 1);
            SafeCoreHirNode otherwise = Child(node, node.ChildIds.Count - 1);
            SafeCoreType elseType = Expr(otherwise, null, depth + 1);
            if (elseType.Kind != K.Never && !_diverges.Contains(otherwise.Id))
                Fail(otherwise, "RST2002", "The else block of a let-else declaration must diverge.");
            SafeCoreType binding = annotation ?? value;
            Sized(binding, node, depth + 1);
            ValidatePatternPlace(Child(node, 0), Child(node, initializerIndex), depth + 1);
            BindPattern(Child(node, 0), binding, refutable: true, depth + 1);
            return Primitive(K.Unit);
        }

        private SafeCoreType Match(SafeCoreHirNode node, SafeCoreType? expected, int depth)
        {
            Step(node, depth);
            SafeCoreType scrutinee = Expr(Child(node, 0), null, depth + 1);
            var coverage = new List<Coverage>();
            var bodies = new List<(SafeCoreHirNode Node, SafeCoreType Type)>();
            SafeCoreType result = Primitive(K.Never);
            bool allDiverge = true;
            for (var index = 1; index < node.ChildIds.Count; index++)
            {
                SafeCoreHirNode arm = Child(node, index);
                Step(arm, depth);
                ValidatePatternPlace(Child(arm, 0), Child(node, 0), depth + 1);
                Coverage armPattern = BindPatternCore(Child(arm, 0), scrutinee, BindingMode.Value, depth + 1);
                bool hasGuard = arm.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasGuard);
                if (hasGuard) Expr(Child(arm, 1), Primitive(K.Bool), depth + 1);
                else coverage.Add(armPattern);
                SafeCoreHirNode body = Child(arm, arm.ChildIds.Count - 1);
                SafeCoreType bodyType = Expr(body, expected, depth + 1);
                result = Join(result, bodyType, body, expected);
                bodies.Add((body, bodyType));
                allDiverge &= _diverges.Contains(body.Id);
                _types[arm.Id] = bodyType;
            }
            foreach (var body in bodies)
            {
                Step(body.Node, depth);
                Require(body.Type, result, body.Node);
            }
            _coverageObligations.Add(new(node, scrutinee, coverage, true));
            if (allDiverge || _diverges.Contains(Child(node, 0).Id)) _diverges.Add(node.Id);
            return result;
        }

        private Coverage BindPatternCore(SafeCoreHirNode pattern, SafeCoreType type, BindingMode mode, int depth)
        {
            Step(pattern, depth);
            _types[pattern.Id] = type;
            type = _inference.Resolve(type);
            if (pattern.Kind == N.TuplePattern && pattern.ChildIds.Count == 1 &&
                !pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma) && Child(pattern, 0).Kind != N.RestPattern)
                return BindPatternCore(Child(pattern, 0), type, mode, depth + 1);
            bool stringLiteral = pattern.Kind == N.LiteralPattern &&
                (pattern.Value!.StartsWith('"') || pattern.Value.StartsWith('r'));
            bool structural = pattern.Kind is N.TuplePattern or N.PathPattern or N.StructPattern or N.SlicePattern or N.LiteralPattern or N.RangePattern;
            if (type.Kind == K.Reference && structural && !(stringLiteral && type.ElementType.Kind == K.Str))
            {
                BindingMode nestedMode = mode == BindingMode.Shared || !type.IsMutable ? BindingMode.Shared : BindingMode.Mutable;
                Coverage child = BindReferencePatternInner(pattern, type.ElementType, nestedMode, !type.IsMutable, depth + 1);
                return new(CoverageKind.Constructor, [child], "$ref");
            }
            switch (pattern.Kind)
            {
                case N.WildcardPattern: return Coverage.Any;
                case N.IdentifierPattern:
                    if (pattern.DeclaredSymbol is null) return PathPattern(pattern, type, mode, depth + 1);
                    bool byReference = pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ByReference);
                    bool mutable = pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable);
                    if (byReference && mutable && _sharedPatternReferences != 0)
                        Fail(pattern, "RST2003", "A ref mut binding cannot borrow through a shared reference.");
                    SafeCoreType bindingType = byReference
                        ? SafeCoreType.Reference(type, mutable)
                        : mutable || mode == BindingMode.Value ? type : SafeCoreType.Reference(type, mode == BindingMode.Mutable);
                    bool bindingMutable = mutable && !byReference;
                    if (_bindings.TryGetValue(pattern.DeclaredSymbol, out Binding? prior))
                    {
                        if (prior.Mutable != bindingMutable) Fail(pattern, "RST2012", "Or-pattern bindings must use the same binding mode.");
                        Equal(prior.Type, bindingType, pattern);
                    }
                    else _bindings.Add(pattern.DeclaredSymbol, new(bindingType, bindingMutable));
                    _types[pattern.Id] = bindingType;
                    return Coverage.Any;
                case N.AtPattern:
                    BindPatternCore(Child(pattern, 0), type, mode, depth + 1);
                    return BindPatternCore(Child(pattern, 1), type, mode, depth + 1);
                case N.OrPattern:
                    var alternatives = new List<Coverage>();
                    foreach (SafeCoreHirNode alternative in Parts(pattern))
                        alternatives.Add(BindPatternCore(alternative, type, mode, depth + 1));
                    return new(CoverageKind.Alternative, alternatives);
                case N.ReferencePattern:
                    bool referenceMutable = pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.MutableReference);
                    if (type.Kind == K.Inference)
                    {
                        SafeCoreType inner = _inference.Fresh();
                        Equal(type, SafeCoreType.Reference(inner, referenceMutable), pattern);
                        type = _inference.Resolve(type);
                    }
                    if (type.Kind != K.Reference || type.IsMutable != referenceMutable)
                        Fail(pattern, "RST2002", "Reference pattern mutability and the matched reference type must agree.");
                    return new(CoverageKind.Constructor,
                        [BindReferencePatternInner(Child(pattern, 0), type.ElementType, BindingMode.Value, !type.IsMutable, depth + 1)], "$ref");
                case N.TuplePattern:
                    if (pattern.ChildIds.Count == 1 && !pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma)
                        && Child(pattern, 0).Kind != N.RestPattern)
                        return BindPatternCore(Child(pattern, 0), type, mode, depth + 1);
                    if (type.Kind == K.Inference)
                    {
                        if (Parts(pattern).Any(static p => p.Kind == N.RestPattern))
                            Fail(pattern, "RST2007", "A tuple rest pattern requires a known tuple size.");
                        var fields = new SafeCoreType[pattern.ChildIds.Count];
                        for (var index = 0; index < fields.Length; index++) { Step(pattern, depth); fields[index] = _inference.Fresh(); }
                        Equal(type, SafeCoreType.Tuple(fields), pattern);
                        type = _inference.Resolve(type);
                    }
                    if (type.Kind == K.Unit && pattern.ChildIds.Count == 0) return new(CoverageKind.Constructor, [], "$unit");
                    if (type.Kind != K.Tuple) Fail(pattern, "RST2002", "A tuple pattern requires a tuple of the matching arity.");
                    return new(CoverageKind.Constructor, PositionalPattern(pattern, type.Elements, mode, depth + 1), "$tuple");
                case N.PathPattern: return PathPattern(pattern, type, mode, depth + 1);
                case N.StructPattern: return StructPattern(pattern, type, mode, depth + 1);
                case N.SlicePattern: return SlicePattern(pattern, type, mode, depth + 1);
                case N.LiteralPattern: return LiteralPattern(pattern, type, depth + 1);
                case N.RangePattern: return RangePattern(pattern, type, depth + 1);
                default:
                    Fail(pattern, "RST2012", "This pattern is not valid in this position.");
                    return Coverage.Any;
            }
        }

        private Coverage BindReferencePatternInner(SafeCoreHirNode pattern, SafeCoreType type, BindingMode mode, bool shared, int depth)
        {
            if (shared) _sharedPatternReferences++;
            try { return BindPatternCore(pattern, type, mode, depth); }
            finally { if (shared) _sharedPatternReferences--; }
        }

        private void ValidatePatternPlace(SafeCoreHirNode pattern, SafeCoreHirNode value, int depth)
        {
            Step(pattern, depth);
            if (pattern.Kind == N.IdentifierPattern && pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ByReference)
                && pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable))
                RequireMutable(value, allowTemporary: true, depth + 1);
            foreach (SafeCoreHirNode child in Parts(pattern)) ValidatePatternPlace(child, value, depth + 1);
        }

        private Coverage[] PositionalPattern(SafeCoreHirNode pattern, IReadOnlyList<SafeCoreType> fields, BindingMode mode, int depth)
        {
            var result = new Coverage[fields.Count];
            int rest = -1;
            for (var index = 0; index < pattern.ChildIds.Count; index++)
            {
                Step(pattern, depth);
                if (Child(pattern, index).Kind != N.RestPattern) continue;
                if (rest >= 0) Fail(pattern, "RST2012", "A positional pattern can contain only one rest pattern.");
                rest = index;
            }
            int explicitCount = pattern.ChildIds.Count - (rest >= 0 ? 1 : 0);
            if (explicitCount > fields.Count || rest < 0 && explicitCount != fields.Count)
                Fail(pattern, "RST2002", "Pattern arity does not match the tuple or constructor.");
            for (var index = 0; index < fields.Count; index++)
            {
                Step(pattern, depth);
                if (rest >= 0 && index >= rest && index < fields.Count - (pattern.ChildIds.Count - rest - 1))
                    result[index] = Coverage.Any;
                else
                {
                    int child = rest < 0 || index < rest ? index : index - fields.Count + pattern.ChildIds.Count;
                    result[index] = BindPatternCore(Child(pattern, child), fields[index], mode, depth + 1);
                }
            }
            return result;
        }

        private AdtShape PatternShape(SafeCoreHirNode pattern, SafeCoreType type, int depth)
        {
            Step(pattern, depth);
            AdtShape? shape = null;
            if (pattern.ReferencedSymbol is { } symbol)
            {
                string key = Key(symbol);
                _constructors.TryGetValue(key, out shape);
                if (shape is null && _namedTypes.TryGetValue(key, out SafeCoreType? alias) && alias.Kind == K.Adt &&
                    _adts.TryGetValue(alias.Name!, out List<AdtShape>? shapes) && _declarations[alias.Name!].Kind == N.Struct)
                    shape = shapes[0];
            }
            if (shape is null) Fail(pattern, "RST2002", "Pattern path does not name an ADT constructor.");
            Equal(type, shape.Type, pattern);
            return shape;
        }

        private Coverage PathPattern(SafeCoreHirNode pattern, SafeCoreType type, BindingMode mode, int depth)
        {
            Step(pattern, depth);
            _types[pattern.Id] = type;
            if (pattern.ReferencedSymbol is { } symbol && _declarations.TryGetValue(Key(symbol), out SafeCoreHirNode? declaration)
                && declaration.Kind == N.Const)
            {
                Equal(type, _values[Key(symbol)], pattern);
                BigInteger? value = PatternConstantValue(symbol, pattern, depth + 1);
                if (!value.HasValue) Fail(pattern, "RST2012", "This constant cannot be used as a scalar pattern.");
                return ScalarCoverage(value.Value, _inference.Resolve(type));
            }
            AdtShape shape = PatternShape(pattern, type, depth + 1);
            bool tuple = pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.TupleStruct);
            if (tuple != shape.Node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.TupleStruct) ||
                !tuple && !shape.Node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.UnitStruct))
                Fail(pattern, "RST2002", "Pattern syntax does not match the ADT constructor shape.");
            foreach (Field field in shape.Fields)
            {
                Step(field.Node, depth);
                Visible(field.Node.DeclaredSymbol ?? shape.Node.DeclaredSymbol!, field.Node, pattern);
            }
            return new(CoverageKind.Constructor,
                PositionalPattern(pattern, shape.Fields.Select(static field => field.Type).ToArray(), mode, depth + 1),
                Key(shape.Node.DeclaredSymbol!));
        }

        private Coverage StructPattern(SafeCoreHirNode pattern, SafeCoreType type, BindingMode mode, int depth)
        {
            AdtShape shape = PatternShape(pattern, type, depth + 1);
            var fields = new Dictionary<string, SafeCoreHirNode>(StringComparer.Ordinal);
            foreach (SafeCoreHirNode field in Parts(pattern))
            {
                Step(field, depth);
                string name = Canonical(field.Name!);
                if (!fields.TryAdd(name, field)) Fail(field, "RST2012", "A field appears more than once in a struct pattern.");
                Field declared = FindField(shape, field.Name!, field);
                Visible(declared.Node.DeclaredSymbol ?? shape.Node.DeclaredSymbol!, declared.Node, field);
            }
            bool rest = pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasRest);
            if (!rest && fields.Count != shape.Fields.Count) Fail(pattern, "RST2012", "A struct pattern must name every field or contain a rest pattern.");
            var children = new Coverage[shape.Fields.Count];
            for (var index = 0; index < children.Length; index++)
            {
                Step(pattern, depth);
                Field field = shape.Fields[index];
                string name = field.Node.Name is null ? index.ToString(CultureInfo.InvariantCulture) : Canonical(field.Node.Name);
                if (fields.TryGetValue(name, out SafeCoreHirNode? fieldPattern))
                {
                    children[index] = BindPatternCore(Child(fieldPattern, 0), field.Type, mode, depth + 1);
                    _types[fieldPattern.Id] = field.Type;
                }
                else children[index] = Coverage.Any;
            }
            return new(CoverageKind.Constructor, children, Key(shape.Node.DeclaredSymbol!));
        }

        private Coverage SlicePattern(SafeCoreHirNode pattern, SafeCoreType type, BindingMode mode, int depth)
        {
            Step(pattern, depth);
            int rest = -1;
            for (var index = 0; index < pattern.ChildIds.Count; index++)
            {
                Step(pattern, depth);
                SafeCoreHirNode child = Child(pattern, index);
                if (child.Kind != N.RestPattern && !(child.Kind == N.AtPattern && Child(child, 1).Kind == N.RestPattern)) continue;
                if (rest >= 0) Fail(pattern, "RST2012", "A slice pattern can contain only one rest pattern.");
                rest = index;
            }
            int explicitCount = pattern.ChildIds.Count - (rest >= 0 ? 1 : 0);
            if (type.Kind == K.Inference && rest < 0)
            {
                Equal(type, SafeCoreType.Array(_inference.Fresh(), explicitCount), pattern);
                type = _inference.Resolve(type);
            }
            if (type.Kind is not (K.Array or K.Slice)) Fail(pattern, "RST2002", "A slice pattern requires an array or slice.");
            if (type.Kind == K.Array && (explicitCount > type.Length || rest < 0 && explicitCount != type.Length))
                Fail(pattern, "RST2002", "Slice pattern length does not match the array length.");
            var children = new List<Coverage>();
            for (var index = 0; index < pattern.ChildIds.Count; index++)
            {
                SafeCoreHirNode child = Child(pattern, index);
                Step(child, depth);
                if (index == rest)
                {
                    SafeCoreType restType = type.Kind == K.Array ? SafeCoreType.Array(type.ElementType, type.Length!.Value - explicitCount)
                        : SafeCoreType.Slice(type.ElementType);
                    _types[child.Id] = restType;
                    if (child.Kind == N.AtPattern) BindPatternCore(Child(child, 0), restType, mode, depth + 1);
                }
                else children.Add(BindPatternCore(child, type.ElementType, mode, depth + 1));
            }
            return new(CoverageKind.Slice, children, Rest: rest);
        }

        private Coverage LiteralPattern(SafeCoreHirNode pattern, SafeCoreType type, int depth)
        {
            Step(pattern, depth);
            string raw = pattern.Value!;
            bool negative = raw.StartsWith('-');
            SafeCoreHirNode literal = negative
                ? new(pattern.Id, N.LiteralExpression, pattern.Span, null, raw[1..], pattern.Modifiers, null, null, []) : pattern;
            int integerCount = _integers.Count;
            SafeCoreType literalType = Literal(literal, negative);
            Equal(type, literalType, pattern);
            if (raw is "true" or "false") return new(CoverageKind.Constructor, [], raw);
            if (_integers.Count != integerCount) return new(CoverageKind.Interval, [], Lower: _integers[^1].Value, Upper: _integers[^1].Value);
            if (raw.StartsWith('\'') || raw.StartsWith("b'", StringComparison.Ordinal))
            {
                BigInteger value = CharacterValue(raw);
                return new(CoverageKind.Interval, [], Lower: value, Upper: value);
            }
            Coverage opaque = new(CoverageKind.Opaque, [], raw);
            return literalType.Kind == K.Reference ? new(CoverageKind.Constructor, [opaque], "$ref") : opaque;
        }

        private Coverage RangePattern(SafeCoreHirNode pattern, SafeCoreType type, int depth)
        {
            Step(pattern, depth);
            bool hasStart = pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasRangeStart);
            bool hasEnd = pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasRangeEnd);
            BigInteger? lower = hasStart ? PatternBound(Child(pattern, 0), type, depth + 1) : null;
            BigInteger? upper = hasEnd ? PatternBound(Child(pattern, hasStart ? 1 : 0), type, depth + 1) : null;
            if (upper.HasValue && !pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.InclusiveRange)) upper--;
            if (lower.HasValue && upper.HasValue && lower > upper) Fail(pattern, "RST2012", "A range pattern cannot be empty or reversed.");
            return new(CoverageKind.Interval, [], Lower: lower, Upper: upper);
        }

        private BigInteger PatternBound(SafeCoreHirNode pattern, SafeCoreType type, int depth)
        {
            _types[pattern.Id] = type;
            Coverage coverage = pattern.Kind == N.LiteralPattern ? LiteralPattern(pattern, type, depth)
                : PathPattern(pattern, type, BindingMode.Value, depth);
            if (coverage.Kind != CoverageKind.Interval || !coverage.Lower.HasValue)
                Fail(pattern, "RST2012", "Range pattern bounds must be integer or character constants.");
            return coverage.Lower.Value;
        }

        private static Coverage ScalarCoverage(BigInteger value, SafeCoreType type) => type.Kind == K.Bool
            ? new(CoverageKind.Constructor, [], value.IsZero ? "false" : "true")
            : new(CoverageKind.Interval, [], Lower: value, Upper: value);

        private static BigInteger CharacterValue(string raw)
        {
            string content = raw.StartsWith("b'", StringComparison.Ordinal) ? raw[2..^1] : raw[1..^1];
            if (!content.StartsWith('\\')) return Rune.GetRuneAt(content, 0).Value;
            return content[1] switch
            {
                'n' => '\n', 'r' => '\r', 't' => '\t', '0' => 0, '\\' => '\\', '\'' => '\'', '"' => '"',
                'x' => int.Parse(content.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                'u' => int.Parse(content[3..^1].Replace("_", "", StringComparison.Ordinal), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException("Validated character literal has an unknown escape."),
            };
        }

        private void ValidatePatternCoverage()
        {
            foreach (CoverageObligation obligation in _coverageObligations)
            {
                Step(obligation.Node, 0);
                SafeCoreType type = _inference.Resolve(obligation.Type, defaultNumerics: true);
                var rows = new List<IReadOnlyList<Coverage>>();
                foreach (Coverage pattern in obligation.Patterns) { Step(obligation.Node, 0); rows.Add([pattern]); }
                if (!Exhaustive(rows, [type], obligation.Node, 0))
                    Fail(obligation.Node, obligation.Match ? "RST2009" : "RST2011", obligation.Match
                        ? "Match arms do not cover every possible value; guarded arms do not establish exhaustiveness."
                        : "A declaration or parameter requires an irrefutable pattern; use match or let-else for conditional binding.");
            }
        }

        private bool Exhaustive(IReadOnlyList<IReadOnlyList<Coverage>> inputRows, IReadOnlyList<SafeCoreType> types,
            SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (types.Count == 0) return inputRows.Count != 0;
            if (inputRows.Count > 4_096 || types.Count > 4_096) Fail(node, "RST0002", "Pattern coverage matrix exceeds its finite size budget.");
            var rows = new List<IReadOnlyList<Coverage>>();
            foreach (IReadOnlyList<Coverage> row in inputRows)
            {
                Step(node, depth);
                bool allWild = true;
                foreach (Coverage pattern in row) { Step(node, depth); allWild &= pattern.Kind == CoverageKind.Any; }
                if (allWild) return true;
                ExpandFirst(row, rows, node, depth + 1);
            }
            SafeCoreType first = _inference.Resolve(types[0]);
            IReadOnlyList<PatternConstructor> constructors = CoverageConstructors(first, rows, node, depth + 1);
            foreach (PatternConstructor constructor in constructors)
            {
                Step(node, depth);
                var specialized = new List<IReadOnlyList<Coverage>>();
                foreach (IReadOnlyList<Coverage> row in rows)
                {
                    Step(node, depth);
                    IReadOnlyList<Coverage>? fields = Specialize(row[0], constructor, node, depth + 1);
                    if (fields is not null) specialized.Add([.. fields, .. row.Skip(1)]);
                }
                if (!Exhaustive(specialized, [.. constructor.Fields, .. types.Skip(1)], node, depth + 1)) return false;
            }
            return true;
        }

        private void ExpandFirst(IReadOnlyList<Coverage> row, List<IReadOnlyList<Coverage>> output, SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (output.Count >= 4_096) Fail(node, "RST0002", "Pattern alternatives exceed the coverage row budget.");
            if (row[0].Kind != CoverageKind.Alternative) { output.Add(row); return; }
            foreach (Coverage alternative in row[0].Children)
            {
                Step(node, depth);
                ExpandFirst([alternative, .. row.Skip(1)], output, node, depth + 1);
            }
        }

        private IReadOnlyList<PatternConstructor> CoverageConstructors(SafeCoreType type,
            IReadOnlyList<IReadOnlyList<Coverage>> rows, SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (type.Kind == K.Never) return [];
            if (type.Kind == K.Bool) return [new("false", []), new("true", [])];
            if (type.Kind == K.Unit) return [new("$unit", [])];
            if (type.Kind == K.Tuple) return [new("$tuple", type.Elements)];
            if (type.Kind == K.Reference) return [new("$ref", [type.ElementType])];
            if (type.Kind == K.Adt && _adts.TryGetValue(type.Name!, out List<AdtShape>? shapes))
            {
                var constructors = new List<PatternConstructor>();
                foreach (AdtShape shape in shapes)
                {
                    Step(node, depth);
                    constructors.Add(new(Key(shape.Node.DeclaredSymbol!), shape.Fields.Select(static field => field.Type).ToArray()));
                }
                return constructors;
            }
            if (IsInteger(type.Kind) || type.Kind == K.Char) return IntegerConstructors(type, rows, node, depth + 1);
            if (type.Kind is K.Array or K.Slice)
            {
                int maximum = 0, prefix = 0, suffix = 0;
                foreach (IReadOnlyList<Coverage> row in rows)
                {
                    Step(node, depth);
                    Coverage pattern = row[0];
                    if (pattern.Kind != CoverageKind.Slice) continue;
                    maximum = Math.Max(maximum, pattern.Children.Count);
                    if (pattern.Rest >= 0)
                    {
                        prefix = Math.Max(prefix, pattern.Rest);
                        suffix = Math.Max(suffix, pattern.Children.Count - pattern.Rest);
                    }
                }
                long lastLength = type.Kind == K.Array ? type.Length!.Value : Math.Max(maximum, prefix + suffix) + 1;
                if (lastLength > 4_096) Fail(node, "RST0002", "Array or slice coverage exceeds its finite length budget.");
                int firstLength = type.Kind == K.Array ? (int)lastLength : 0;
                var lengths = new List<PatternConstructor>();
                for (int length = firstLength; length <= lastLength; length++)
                {
                    Step(node, depth);
                    var fields = new SafeCoreType[length];
                    for (var index = 0; index < fields.Length; index++) { Step(node, depth); fields[index] = type.ElementType; }
                    lengths.Add(new("$slice", fields));
                }
                return lengths;
            }
            // Open scalar domains (str and floating point) require a wildcard. The extra
            // constructor represents every value not explicitly named by a literal pattern.
            var opaque = new HashSet<string>(StringComparer.Ordinal) { "$other" };
            foreach (IReadOnlyList<Coverage> row in rows)
            {
                Step(node, depth);
                if (row[0].Kind == CoverageKind.Opaque) opaque.Add(row[0].Tag!);
            }
            return opaque.Select(static tag => new PatternConstructor(tag, [])).ToArray();
        }

        private List<PatternConstructor> IntegerConstructors(SafeCoreType type,
            IReadOnlyList<IReadOnlyList<Coverage>> rows, SafeCoreHirNode node, int depth)
        {
            int bits = type.Kind switch { K.I8 or K.U8 => 8, K.I16 or K.U16 => 16, K.I32 or K.U32 => 32, K.I128 or K.U128 => 128, _ => 64 };
            BigInteger minimum = type.Kind == K.Char || IsUnsigned(type.Kind) ? 0 : -(BigInteger.One << (bits - 1));
            BigInteger maximum = type.Kind == K.Char ? 0x10ffff : IsUnsigned(type.Kind) ? (BigInteger.One << bits) - 1 : (BigInteger.One << (bits - 1)) - 1;
            var boundaries = new SortedSet<BigInteger> { minimum, maximum + 1 };
            if (type.Kind == K.Char) { boundaries.Add(0xd800); boundaries.Add(0xe000); }
            foreach (IReadOnlyList<Coverage> row in rows)
            {
                Step(node, depth);
                Coverage pattern = row[0];
                if (pattern.Kind != CoverageKind.Interval) continue;
                if (pattern.Lower.HasValue) boundaries.Add(BigInteger.Clamp(pattern.Lower.Value, minimum, maximum + 1));
                if (pattern.Upper.HasValue) boundaries.Add(BigInteger.Clamp(pattern.Upper.Value + 1, minimum, maximum + 1));
            }
            var constructors = new List<PatternConstructor>();
            foreach (BigInteger boundary in boundaries)
            {
                Step(node, depth);
                if (boundary > maximum || type.Kind == K.Char && boundary >= 0xd800 && boundary < 0xe000) continue;
                constructors.Add(new("$scalar", [], boundary));
            }
            return constructors;
        }

        private IReadOnlyList<Coverage>? Specialize(Coverage pattern, PatternConstructor constructor, SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (pattern.Kind == CoverageKind.Any)
            {
                var wildcards = new Coverage[constructor.Fields.Count];
                for (var index = 0; index < wildcards.Length; index++) { Step(node, depth); wildcards[index] = Coverage.Any; }
                return wildcards;
            }
            if (pattern.Kind is CoverageKind.Constructor or CoverageKind.Opaque)
                return pattern.Tag == constructor.Tag ? pattern.Children : null;
            if (pattern.Kind == CoverageKind.Interval)
                return constructor.Scalar.HasValue && (!pattern.Lower.HasValue || constructor.Scalar >= pattern.Lower) &&
                    (!pattern.Upper.HasValue || constructor.Scalar <= pattern.Upper) ? [] : null;
            if (pattern.Kind == CoverageKind.Slice && constructor.Tag == "$slice")
            {
                int length = constructor.Fields.Count;
                if (pattern.Rest < 0) return pattern.Children.Count == length ? pattern.Children : null;
                if (pattern.Children.Count > length) return null;
                var fields = new Coverage[length];
                for (var index = 0; index < length; index++)
                {
                    Step(node, depth);
                    int suffix = pattern.Children.Count - pattern.Rest;
                    fields[index] = index < pattern.Rest ? pattern.Children[index]
                        : index >= length - suffix ? pattern.Children[pattern.Children.Count - length + index] : Coverage.Any;
                }
                return fields;
            }
            return null;
        }
    }
}
