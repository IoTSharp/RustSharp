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
        private sealed record Constant(SafeCoreType Type, object? Value, string? Constructor = null);
        private sealed class ConstantEnvironment(SafeCoreType? returnType)
        {
            public Dictionary<SafeCoreSymbol, Constant> Locals { get; } = [];
            public SafeCoreType? ReturnType { get; } = returnType;
        }
        private sealed class ConstantReturn(Constant value) : Exception
        {
            public Constant Value { get; } = value;
        }
        private sealed class ConstantLoopControl(bool isContinue, Constant value) : Exception
        {
            public bool IsContinue { get; } = isContinue;
            public Constant Value { get; } = value;
        }
        private readonly Dictionary<string, Constant> _constants = new(StringComparer.Ordinal);
        private readonly HashSet<string> _constantDependencies = new(StringComparer.Ordinal);
        private readonly HashSet<string> _definingFunctions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _checkedConstBodies = new(StringComparer.Ordinal);
        private readonly Dictionary<int, SafeCoreHirNode> _constBlocks = [];
        private readonly Dictionary<int, Constant> _constantBlocks = [];

        private void ValidateConstContext(SafeCoreHirNode node, int depth)
        {
            var allowedLocals = new HashSet<SafeCoreSymbol>();
            CollectConstantLocals(node, allowedLocals, node.Id, depth + 1);
            ValidateConstNode(node, allowedLocals, node.Kind == N.Function, 0, node.Id, depth + 1);
        }

        private void CollectConstantLocals(SafeCoreHirNode node, HashSet<SafeCoreSymbol> locals, int root, int depth)
        {
            Step(node, depth);
            if (node.Kind == N.ConstBlockExpression && node.Id != root) return;
            if (node.DeclaredSymbol is { Kind: SafeCoreSymbolKind.Local or SafeCoreSymbolKind.Parameter } symbol)
                locals.Add(symbol);
            foreach (SafeCoreHirNode child in Parts(node)) CollectConstantLocals(child, locals, root, depth + 1);
        }

        private void ValidateConstNode(SafeCoreHirNode node, HashSet<SafeCoreSymbol> locals, bool allowReturn, int loopDepth, int root, int depth)
        {
            Step(node, depth);
            if (node.Kind == N.ConstBlockExpression && node.Id != root) { ValidateConstContext(node, depth + 1); return; }
            if (node.Kind == N.NameExpression && node.ReferencedSymbol is { Kind: SafeCoreSymbolKind.Local or SafeCoreSymbolKind.Parameter } symbol &&
                !locals.Contains(symbol))
                Fail(node, "RST2010", "A constant expression cannot capture runtime locals or parameters.");
            if (node.Kind is N.ReturnExpression or N.ReturnStatement && !allowReturn)
                Fail(node, "RST2010", "Return cannot exit a constant expression into an enclosing function.");
            if (node.Kind is N.BreakExpression or N.ContinueExpression && loopDepth == 0)
                Fail(node, "RST2010", "Loop control cannot cross a constant expression boundary.");
            if (node.Kind == N.CallExpression)
            {
                SafeCoreHirNode callee = Child(node, 0);
                if (callee.ReferencedSymbol is null ||
                    !(_constructors.ContainsKey(Key(callee.ReferencedSymbol)) ||
                    _declarations.TryGetValue(Key(callee.ReferencedSymbol), out var function) &&
                    function.Kind == N.Function && function.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ConstFunction)))
                    Fail(node, "RST2010", "Only direct const function calls are allowed in constant contexts.");
            }
            if (node.Kind == N.ClosureExpression || node.Kind == N.UnaryExpression && node.Value == "&mut")
                Fail(node, "RST2010", "This operation is outside the bounded scalar and aggregate const interpreter.");
            if (node.Kind == N.UnaryExpression && node.Value == "&" && !IsClosedPromotable(Child(node, 0), depth + 1))
                Fail(node, "RST2010", "A constant shared borrow requires a closed immutable value with no local storage dependency.");
            if (node.Kind is N.LoopExpression or N.WhileExpression) loopDepth++;
            foreach (SafeCoreHirNode child in Parts(node)) ValidateConstNode(child, locals, allowReturn, loopDepth, root, depth + 1);
        }

        private Constant ConstantItem(SafeCoreHirNode declaration, int depth)
        {
            Step(declaration, depth);
            string key = Key(declaration.DeclaredSymbol!);
            if (_constants.TryGetValue(key, out Constant? cached)) return cached;
            if (!_constantDependencies.Add(key)) Fail(declaration, "RST2008", "A constant has a recursive initialization dependency.");
            string savedModule = _module;
            _module = ModuleOf(declaration.DeclaredSymbol!);
            try
            {
                SafeCoreType type = Type(Parts(declaration).First(IsType), false, depth + 1);
                SafeCoreHirNode initializer = Parts(declaration).Last();
                Expr(initializer, type, depth + 1);
                ValidateConstContext(initializer, depth + 1);
                Constant result = EvaluateConstant(initializer, new(null), depth + 1);
                Require(result.Type, type, initializer);
                ValidateConstantRange(result with { Type = type }, initializer);
                _constants[key] = result with { Type = type };
                return _constants[key];
            }
            finally { _module = savedModule; _constantDependencies.Remove(key); }
        }

        private BigInteger? PatternConstantValue(SafeCoreSymbol symbol, SafeCoreHirNode use, int depth)
        {
            Step(use, depth);
            if (!_declarations.TryGetValue(Key(symbol), out var declaration) || declaration.Kind != N.Const) return null;
            Constant value = ConstantItem(declaration, depth + 1);
            return value.Value switch { BigInteger integer => integer, bool boolean => boolean ? BigInteger.One : BigInteger.Zero, _ => null };
        }

        private Constant EvaluateConstant(SafeCoreHirNode node, ConstantEnvironment environment, int depth)
        {
            Step(node, depth);
            Constant result;
            SafeCoreType known = _types.TryGetValue(node.Id, out SafeCoreType? evidence)
                ? _inference.Resolve(evidence, defaultNumerics: true) : Primitive(K.Unit);
            switch (node.Kind)
            {
                case N.LiteralExpression:
                    result = ConstantLiteral(node, known); break;
                case N.NameExpression:
                    if (node.ReferencedSymbol is not null && environment.Locals.TryGetValue(node.ReferencedSymbol, out var local)) result = local;
                    else if (node.ReferencedSymbol is not null && _declarations.TryGetValue(Key(node.ReferencedSymbol), out var named) && named.Kind == N.Const)
                        result = ConstantItem(named, depth + 1);
                    else if (node.ReferencedSymbol is not null && _constructors.TryGetValue(Key(node.ReferencedSymbol), out var unit) &&
                        unit.Node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.UnitStruct)) result = new(unit.Type, System.Array.Empty<Constant>(), Key(unit.Node.DeclaredSymbol!));
                    else { Fail(node, "RST2010", "A constant expression may reference only constants and its own locals."); return null!; }
                    break;
                case N.Block:
                    result = new(Primitive(K.Unit), null);
                    foreach (SafeCoreHirNode child in Parts(node)) result = EvaluateConstant(child, environment, depth + 1);
                    break;
                case N.BlockExpression:
                case N.ConstBlockExpression:
                    result = EvaluateConstant(Child(node, 0), environment, depth + 1); break;
                case N.Attribute: result = new(Primitive(K.Unit), null); break;
                case N.ExpressionStatement:
                    _ = EvaluateConstant(Child(node, 0), environment, depth + 1);
                    result = new(Primitive(K.Unit), null); break;
                case N.LetStatement:
                    int valueIndex = node.ChildIds.Count > 1 && IsType(Child(node, 1)) ? 2 : 1;
                    if (valueIndex >= node.ChildIds.Count) Fail(node, "RST2010", "Constant locals require an initializer.");
                    Constant initial = EvaluateConstant(Child(node, valueIndex), environment, depth + 1);
                    if (!MatchConstantPattern(Child(node, 0), initial, environment, depth + 1))
                    {
                        if (!node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasElse))
                            Fail(node, "RST2010", "The constant value does not match its binding pattern.");
                        _ = EvaluateConstant(Child(node, node.ChildIds.Count - 1), environment, depth + 1);
                    }
                    result = new(Primitive(K.Unit), null); break;
                case N.ReturnStatement:
                case N.ReturnExpression:
                    if (environment.ReturnType is null) Fail(node, "RST2010", "Return is not permitted outside a constant function.");
                    Constant returned = node.ChildIds.Count == 0 ? new(Primitive(K.Unit), null) : EvaluateConstant(Child(node, 0), environment, depth + 1);
                    Require(returned.Type, environment.ReturnType!, node);
                    throw new ConstantReturn(returned with { Type = environment.ReturnType! });
                case N.TupleExpression:
                    if (node.ChildIds.Count == 1 && !node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma))
                        result = EvaluateConstant(Child(node, 0), environment, depth + 1);
                    else
                    {
                        var tuple = new List<Constant>();
                        foreach (SafeCoreHirNode child in Parts(node)) tuple.Add(EvaluateConstant(child, environment, depth + 1));
                        result = new(known, tuple.AsReadOnly());
                    }
                    break;
                case N.UnaryExpression:
                    Constant operand = EvaluateConstant(Child(node, 0), environment, depth + 1);
                    if (node.Value == "&")
                    {
                        if (!IsClosedPromotable(Child(node, 0), depth + 1))
                            Fail(node, "RST2010", "A constant reference cannot borrow a local storage slot.");
                        result = new(known, operand); break;
                    }
                    if (node.Value == "*" && operand.Value is Constant referent)
                    { result = referent; break; }
                    object? unaryValue = node.Value switch
                    {
                        "-" when operand.Value is BigInteger number => -number,
                        "-" when operand.Value is double number => -number,
                        "!" when operand.Value is bool boolean => !boolean,
                        "!" when operand.Value is BigInteger number => BitwiseNot(number, operand.Type),
                        _ => null,
                    };
                    if (unaryValue is null) Fail(node, "RST2010", "This unary operation is not constant-evaluable in the scalar profile.");
                    result = new(known, unaryValue); break;
                case N.BinaryExpression:
                    result = ConstantBinary(node, environment, depth + 1); break;
                case N.IfExpression:
                    Constant condition = EvaluateConstant(Child(node, 0), environment, depth + 1);
                    if (condition.Value is not bool) Fail(node, "RST2010", "Constant conditions must be bool.");
                    result = (bool)condition.Value! ? EvaluateConstant(Child(node, 1), environment, depth + 1) :
                        node.ChildIds.Count == 3 ? EvaluateConstant(Child(node, 2), environment, depth + 1) : new(Primitive(K.Unit), null);
                    break;
                case N.MatchExpression:
                    result = EvaluateConstantMatch(node, environment, depth + 1); break;
                case N.CastExpression:
                    result = ConstantCast(node, EvaluateConstant(Child(node, 0), environment, depth + 1), known); break;
                case N.CallExpression:
                    result = ConstantCall(node, environment, depth + 1); break;
                case N.WhileExpression:
                case N.LoopExpression:
                    result = EvaluateConstantLoop(node, environment, depth + 1); break;
                case N.BreakExpression:
                case N.ContinueExpression:
                    throw new ConstantLoopControl(node.Kind == N.ContinueExpression,
                        node.ChildIds.Count == 0 ? new(Primitive(K.Unit), null) : EvaluateConstant(Child(node, 0), environment, depth + 1));
                case N.ArrayExpression:
                    var values = new List<Constant>();
                    if (node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.RepeatedArray))
                    {
                        Constant repeated = EvaluateConstant(Child(node, 0), environment, depth + 1);
                        long count = known.Length ?? 0;
                        if (count > 4096) Fail(node, "RST0002", "Materialized constant arrays are limited to 4096 elements.");
                        for (int i = 0; i < count; i++) { Step(node, depth); values.Add(repeated); }
                    }
                    else foreach (SafeCoreHirNode child in Parts(node)) values.Add(EvaluateConstant(child, environment, depth + 1));
                    result = new(known, values.AsReadOnly()); break;
                case N.StructExpression:
                    var namedFields = new Dictionary<string, Constant>(StringComparer.Ordinal);
                    foreach (SafeCoreHirNode field in Parts(node))
                    {
                        Step(field, depth);
                        if (field.Kind == N.StructExpressionField)
                            namedFields[Canonical(field.Name!)] = EvaluateConstant(Child(field, 0), environment, depth + 1);
                        else if (EvaluateConstant(field, environment, depth + 1).Value is IReadOnlyDictionary<string, Constant> inherited)
                            foreach (var item in inherited) { Step(field, depth); namedFields.TryAdd(item.Key, item.Value); }
                        else Fail(field, "RST2010", "The constant base requires a struct value.");
                    }
                    AdtShape shape = PatternShape(node, known, depth + 1);
                    result = new(known, new System.Collections.ObjectModel.ReadOnlyDictionary<string, Constant>(namedFields), Key(shape.Node.DeclaredSymbol!)); break;
                case N.IndexExpression:
                    Constant array = EvaluateConstant(Child(node, 0), environment, depth + 1);
                    Constant index = EvaluateConstant(Child(node, 1), environment, depth + 1);
                    if (array.Value is not IReadOnlyList<Constant> elements || index.Value is not BigInteger position || position < 0 || position >= elements.Count)
                        Fail(node, "RST2006", "A constant array index is out of range.");
                    result = ((IReadOnlyList<Constant>)array.Value!)[(int)(BigInteger)index.Value!]; break;
                case N.MemberExpression:
                    Constant aggregate = EvaluateConstant(Child(node, 0), environment, depth + 1);
                    if (aggregate.Value is IReadOnlyList<Constant> fields && int.TryParse(node.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int fieldIndex) && fieldIndex >= 0 && fieldIndex < fields.Count)
                        result = fields[fieldIndex];
                    else if (aggregate.Value is IReadOnlyDictionary<string, Constant> members && members.TryGetValue(Canonical(node.Name!), out var member)) result = member;
                    else { Fail(node, "RST2010", "This constant field access is not supported."); return null!; }
                    break;
                default: Fail(node, "RST2010", "This expression is not constant-evaluable in the type profile."); return null!;
            }
            // Negative minima are represented by a positive lexical operand; the enclosing
            // negation validates the signed result instead of rejecting that operand early.
            if (node.Kind != N.LiteralExpression && !(node.Kind == N.TupleExpression && node.ChildIds.Count == 1 &&
                !node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma))) ValidateConstantRange(result, node);
            if (_inference.Resolve(result.Type).Kind == K.F32 && result.Value is double floating) result = result with { Value = (double)(float)floating };
            return result;
        }

        private Constant EvaluateConstantLoop(SafeCoreHirNode node, ConstantEnvironment environment, int depth)
        {
            bool isWhile = node.Kind == N.WhileExpression;
            for (int iteration = 0; iteration < 4096; iteration++)
            {
                Step(node, depth);
                if (isWhile)
                {
                    Constant condition = EvaluateConstant(Child(node, 0), environment, depth + 1);
                    if (condition.Value is false) return new(Primitive(K.Unit), null);
                    if (condition.Value is not true) Fail(node, "RST2010", "Constant loop condition requires bool.");
                }
                try { _ = EvaluateConstant(Child(node, isWhile ? 1 : 0), environment, depth + 1); }
                catch (ConstantLoopControl control) when (!control.IsContinue) { return control.Value; }
                catch (ConstantLoopControl) { /* The next bounded iteration handles continue. */ }
            }
            Fail(node, "RST0002", "Constant loops exceed the 4096 iteration limit."); return null!;
        }

        private Constant ConstantLiteral(SafeCoreHirNode node, SafeCoreType known)
        {
            string raw = node.Value!;
            if (raw is "true" or "false") return new(Primitive(K.Bool), raw == "true");
            if (raw.StartsWith('"') || raw.StartsWith('r')) return new(known, raw);
            if (raw.StartsWith('\'') || raw.StartsWith("b'", StringComparison.Ordinal))
            {
                int start = raw[0] == 'b' ? 2 : 1;
                string value = raw[start..^1];
                int scalar;
                if (value.StartsWith("\\u{", StringComparison.Ordinal))
                    scalar = int.Parse(value[3..^1].Replace("_", "", StringComparison.Ordinal), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                else if (value.StartsWith("\\x", StringComparison.Ordinal)) scalar = int.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                else if (value.StartsWith('\\')) scalar = value[1] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => 0, _ => value[1] };
                else scalar = Rune.GetRuneAt(value, 0).Value;
                return new(known, new BigInteger(scalar));
            }
            string text = raw.Replace("_", "", StringComparison.Ordinal);
            if (known.Kind is K.F32 or K.F64 || node.Name == nameof(RustTokenKind.FloatLiteral))
            {
                if (text.EndsWith("f32", StringComparison.Ordinal) || text.EndsWith("f64", StringComparison.Ordinal)) text = text[..^3];
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double floating))
                    Fail(node, "RST2010", "Invalid floating-point constant.");
                return new(known, floating);
            }
            int radix = text.StartsWith("0x", StringComparison.Ordinal) ? 16 : text.StartsWith("0o", StringComparison.Ordinal) ? 8 : text.StartsWith("0b", StringComparison.Ordinal) ? 2 : 10;
            BigInteger number = 0;
            int offset = radix == 10 ? 0 : 2;
            for (int i = offset; i < text.Length; i++)
            {
                Step(node, 0);
                int digit = Digit(text[i]);
                if (digit < 0 || digit >= radix) break;
                number = number * radix + digit;
                if (number.GetBitLength() > 128) Fail(node, "RST2006", "Integer constant exceeds 128 bits.");
            }
            if (raw.Length == 0 || !char.IsAsciiDigit(raw[0])) Fail(node, "RST2010", "Only scalar and aggregate constants are evaluable in this profile.");
            return new(known, number);
        }

        private Constant ConstantBinary(SafeCoreHirNode node, ConstantEnvironment environment, int depth)
        {
            string op = node.Value!;
            bool compound = op is "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=";
            if (compound) op = op[..^1];
            SafeCoreHirNode leftNode = Child(node, 0);
            SafeCoreHirNode rightNode = Child(node, 1);
            if (op == "=")
            {
                if (leftNode.Kind != N.NameExpression || leftNode.ReferencedSymbol is null || !environment.Locals.ContainsKey(leftNode.ReferencedSymbol))
                    Fail(node, "RST2010", "Constant assignments require a local variable.");
                environment.Locals[leftNode.ReferencedSymbol!] = EvaluateConstant(rightNode, environment, depth + 1);
                return new(Primitive(K.Unit), null);
            }
            Constant left = EvaluateConstant(leftNode, environment, depth + 1);
            if (op == "&&" && left.Value is false) return new(Primitive(K.Bool), false);
            if (op == "||" && left.Value is true) return new(Primitive(K.Bool), true);
            Constant right = EvaluateConstant(rightNode, environment, depth + 1);
            object? value = null;
            if (left.Value is BigInteger a && right.Value is BigInteger b)
            {
                int width = IntegerWidth(_inference.Resolve(left.Type, defaultNumerics: true).Kind);
                if (op is "/" or "%" && b.IsZero) Fail(node, "RST2006", "Constant division or remainder by zero.");
                if (op is "/" or "%" && !IsUnsigned(_inference.Resolve(left.Type).Kind) && a == -(BigInteger.One << (width - 1)) && b == -1)
                    Fail(node, "RST2006", "Constant signed division or remainder overflows.");
                if (op is "<<" or ">>" && (b < 0 || b >= width)) Fail(node, "RST2006", "Constant shift count exceeds the integer width.");
                value = op switch
                {
                    "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, "%" => a % b,
                    "&" => a & b, "|" => a | b, "^" => a ^ b, "<<" => TruncateInteger(a << (int)b, left.Type), ">>" => a >> (int)b,
                    "==" => a == b, "!=" => a != b, "<" => a < b, ">" => a > b, "<=" => a <= b, ">=" => a >= b,
                    _ => null,
                };
            }
            else if (left.Value is bool aBool && right.Value is bool bBool)
                value = op switch { "&&" or "&" => aBool && bBool, "||" or "|" => aBool || bBool, "^" => aBool ^ bBool,
                    "==" => aBool == bBool, "!=" => aBool != bBool, "<" => !aBool && bBool, ">" => aBool && !bBool,
                    "<=" => !aBool || bBool, ">=" => aBool || !bBool, _ => null };
            else if (left.Value is double aFloat && right.Value is double bFloat)
                value = op switch { "+" => aFloat + bFloat, "-" => aFloat - bFloat, "*" => aFloat * bFloat, "/" => aFloat / bFloat, "%" => aFloat % bFloat, "==" => aFloat == bFloat, "!=" => aFloat != bFloat, "<" => aFloat < bFloat, ">" => aFloat > bFloat, "<=" => aFloat <= bFloat, ">=" => aFloat >= bFloat, _ => null };
            if (value is null) Fail(node, "RST2010", "The constant operator is not supported for these values.");
            if (compound)
            {
                if (leftNode.Kind != N.NameExpression || leftNode.ReferencedSymbol is null || !environment.Locals.ContainsKey(leftNode.ReferencedSymbol))
                    Fail(node, "RST2010", "Constant assignments require a local variable.");
                var assigned = new Constant(left.Type, value);
                ValidateConstantRange(assigned, node);
                environment.Locals[leftNode.ReferencedSymbol!] = assigned;
                return new(Primitive(K.Unit), null);
            }
            return new(_types[node.Id], value);
        }

        private Constant ConstantCall(SafeCoreHirNode node, ConstantEnvironment caller, int depth)
        {
            Step(node, depth);
            SafeCoreHirNode callee = Child(node, 0);
            if (callee.ReferencedSymbol is not null && _constructors.TryGetValue(Key(callee.ReferencedSymbol), out var constructor))
            {
                var fields = new List<Constant>();
                for (int i = 1; i < node.ChildIds.Count; i++) fields.Add(EvaluateConstant(Child(node, i), caller, depth + 1));
                return new(constructor.Type, fields.AsReadOnly(), Key(constructor.Node.DeclaredSymbol!));
            }
            if (callee.ReferencedSymbol is null || !_declarations.TryGetValue(Key(callee.ReferencedSymbol), out var declaration) ||
                declaration.Kind != N.Function || !declaration.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ConstFunction))
                Fail(node, "RST2010", "Only direct const function calls are allowed in constant expressions.");
            declaration = _declarations[Key(callee.ReferencedSymbol!)];
            DefineFunction(declaration);
            SafeCoreType signature = _values[Key(callee.ReferencedSymbol!)];
            CheckConstantFunctionBody(declaration, signature, depth + 1);
            var arguments = new List<Constant>();
            for (int i = 1; i < node.ChildIds.Count; i++) arguments.Add(EvaluateConstant(Child(node, i), caller, depth + 1));
            if (arguments.Count != signature.ParameterTypes.Count) Fail(node, "RST2004", "Incorrect constant call argument count.");
            var environment = new ConstantEnvironment(signature.ReturnType);
            int index = 0;
            foreach (SafeCoreHirNode parameter in Parts(declaration).Where(static p => p.Kind == N.Parameter))
            {
                Constant argument = arguments[index] with { Type = signature.ParameterTypes[index] };
                BindConstant(Child(parameter, 0), argument, environment, depth + 1);
                index++;
            }
            try { return EvaluateConstant(Parts(declaration).Last(), environment, depth + 1) with { Type = signature.ReturnType }; }
            catch (ConstantReturn returned) { return returned.Value; }
        }

        private void CheckConstantFunctionBody(SafeCoreHirNode node, SafeCoreType signature, int depth)
        {
            Step(node, depth);
            if (!_checkedConstBodies.Add(Key(node.DeclaredSymbol!))) return;
            Dictionary<SafeCoreSymbol, Binding> saved = new(_bindings);
            LoopContext[] loops = _loops.ToArray();
            SafeCoreType returnType = _returnType;
            string module = _module;
            try
            {
                _bindings.Clear(); _loops.Clear();
                _returnType = signature.ReturnType;
                _module = ModuleOf(node.DeclaredSymbol!);
                int index = 0;
                foreach (SafeCoreHirNode parameter in Parts(node).Where(static p => p.Kind == N.Parameter))
                    Bind(Child(parameter, 0), signature.ParameterTypes[index++], depth + 1);
                Expr(Parts(node).Last(), _returnType, depth + 1);
                ValidateConstContext(node, depth + 1);
            }
            finally
            {
                _bindings.Clear();
                foreach (var item in saved) _bindings.Add(item.Key, item.Value);
                _loops.Clear(); _loops.AddRange(loops);
                _returnType = returnType; _module = module;
            }
        }

        private void BindConstant(SafeCoreHirNode pattern, Constant value, ConstantEnvironment environment, int depth)
        {
            if (!MatchConstantPattern(pattern, value, environment, depth))
                Fail(pattern, "RST2010", "The constant value does not match its binding pattern.");
        }

        private Constant ConstantCast(SafeCoreHirNode node, Constant source, SafeCoreType target)
        {
            object? value;
            if (IsInteger(target.Kind))
            {
                BigInteger integer = source.Value switch { BigInteger n => n, bool b => b ? BigInteger.One : BigInteger.Zero, double f when double.IsFinite(f) => new BigInteger(f), _ => BigInteger.Zero };
                if (source.Value is double floating)
                {
                    int width = IntegerWidth(target.Kind);
                    BigInteger min = IsUnsigned(target.Kind) ? 0 : -(BigInteger.One << (width - 1));
                    BigInteger max = (BigInteger.One << (IsUnsigned(target.Kind) ? width : width - 1)) - 1;
                    integer = double.IsNaN(floating) ? 0 : double.IsPositiveInfinity(floating) ? max : double.IsNegativeInfinity(floating) ? min : BigInteger.Clamp(integer, min, max);
                }
                value = TruncateInteger(integer, target);
            }
            else if (target.Kind is K.F32 or K.F64) value = source.Value is BigInteger integer ? (double)integer : source.Value;
            else if (target.Kind == K.Char && source.Type.Kind == K.U8) value = source.Value;
            else { Fail(node, "RST2010", "This constant cast is not evaluable."); return null!; }
            if (target.Kind == K.F32 && value is double number) value = (double)(float)number;
            return new(target, value);
        }

        private void ValidateConstantRange(Constant value, SafeCoreHirNode node)
        {
            SafeCoreType type = _inference.Resolve(value.Type, defaultNumerics: true);
            if (value.Value is BigInteger integer && IsInteger(type.Kind) && !Fits(integer, type.Kind))
                Fail(node, "RST2006", "Constant arithmetic overflows its integer type.");
            // Rust permits IEEE infinities/NaNs produced by arithmetic; lexical literal
            // overflow is checked independently by the type pass.
        }

        private BigInteger BitwiseNot(BigInteger number, SafeCoreType type) => TruncateInteger(~number, type);
        private BigInteger TruncateInteger(BigInteger value, SafeCoreType type)
        {
            type = _inference.Resolve(type, defaultNumerics: true);
            int bits = IntegerWidth(type.Kind);
            BigInteger mask = (BigInteger.One << bits) - 1;
            BigInteger result = value & mask;
            return !IsUnsigned(type.Kind) && result >= BigInteger.One << (bits - 1) ? result - (BigInteger.One << bits) : result;
        }
        private static int IntegerWidth(K kind) => kind switch
        { K.I8 or K.U8 => 8, K.I16 or K.U16 => 16, K.I32 or K.U32 => 32, K.I128 or K.U128 => 128, _ => 64 };
    }
}
