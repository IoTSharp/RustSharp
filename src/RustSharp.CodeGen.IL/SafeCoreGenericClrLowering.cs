using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RustSharp.Semantics;
using RustSharp.Syntax;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.CodeGen.IL;

/// <summary>Lowers closed generic HIR evidence to static methods and concrete CLR value layouts.</summary>
public static class SafeCoreGenericClrLowering
{
    public const string MissingEntryPoint = "RSG2001";
    public const string InvalidLowering = "RSG2002";

    public static SafeCoreClrResult Lower(SafeCoreGenericAnalysisProgram program, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        cancellationToken.ThrowIfCancellationRequested();
        if (!program.Hir.IsSuccessful || !program.Plan.IsSuccess || program.Functions.IsDefault || program.Specializations.IsDefault)
            return new([], [], [new(InvalidLowering, "Generic emission requires complete closed HIR evidence.", program.Hir.Root?.Span ?? default)]);
        try { return new Lowerer(program, cancellationToken).Run(); }
        catch (LoweringFailure failure)
        {
            return new([], [], [failure.Diagnostic with { SourcePath = program.Hir.SourcePath }]);
        }
        catch (TimeoutException exception)
        {
            return new([], [], [new Diagnostic(SafeCoreGenericDiagnosticCodes.LimitReached,
                exception.Message, program.Hir.Root!.Span) { SourcePath = program.Hir.SourcePath }]);
        }
    }

    private sealed class LoweringFailure(Diagnostic diagnostic) : Exception(diagnostic.Message)
    {
        public Diagnostic Diagnostic { get; } = diagnostic;
    }

    private sealed class Lowerer(SafeCoreGenericAnalysisProgram program, CancellationToken cancellationToken)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Dictionary<string, Method> methods = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ClrLirType> mappedTypes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ClrLirValueType> layouts = new(StringComparer.Ordinal);
        private readonly HashSet<string> activeLayouts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SafeCoreGenericNominalDefinition> nominalTypes =
            program.NominalTypes.ToDictionary(static value => value.Id, StringComparer.Ordinal);
        private int steps;
        private SafeCoreHirResult Hir => program.Hir;

        private sealed record Method(SafeCoreGenericSpecialization Specialization, SafeCoreGenericFunctionDefinition Definition,
            string Name, bool IsEntry);

        public SafeCoreClrResult Run()
        {
            SafeCoreHirNode root = program.Hir.Root!;
            Step(root);
            if (program.Specializations.Length > 128) Limit(root, "Generic emission supports at most 128 reachable closed methods.");
            var definitions = program.Functions.ToDictionary(static value => value.Id, StringComparer.Ordinal);
            var ordered = program.Specializations.Select(value => (Key: InstanceKey(value.Instance, root), Value: value))
                .OrderBy(static value => value.Key, StringComparer.Ordinal).ToArray();
            int entryCount = 0;
            for (int index = 0; index < ordered.Length; index++)
            {
                var item = ordered[index];
                Step(item.Value.Declaration);
                if (!definitions.TryGetValue(item.Value.Instance.FunctionId, out SafeCoreGenericFunctionDefinition? definition))
                    Fail(item.Value.Declaration, "A closed instance is missing its checked definition.");
                if (item.Value.PlannedSignature.ParameterTypes.Length > ClrLirLimits.MaximumParameters)
                    Limit(item.Value.Declaration, "A closed method exceeds its parameter limit.");
                SafeCoreSymbol? symbol = item.Value.Declaration.DeclaredSymbol;
                bool isEntry = symbol?.Name == "main" && symbol.ScopePath == program.Hir.NameResolution!.RootScope!.Path;
                if (isEntry) entryCount++;
                var method = new Method(item.Value, definition!, isEntry ? "Main" : "generic_" + index.ToString(CultureInfo.InvariantCulture), isEntry);
                if (!methods.TryAdd(item.Key, method)) Fail(item.Value.Declaration, "A closed method instance occurs more than once.");
                if (isEntry && (item.Value.Instance.Arguments.Length != 0 || item.Value.PlannedSignature.ParameterTypes.Length != 0 ||
                    !item.Value.PlannedSignature.ReturnType.Equals(RustType.Unit)))
                    Fail(item.Value.Declaration, "The executable root must be a nongeneric main returning unit with no parameters.");
            }
            if (entryCount != 1)
                throw new LoweringFailure(new(MissingEntryPoint, "Generic executable emission requires a root fn main() returning unit.", root.Span));

            var lowered = new List<ClrLirMethod>(ordered.Length);
            var spans = new List<TextSpan>(ordered.Length);
            foreach (var item in ordered)
            {
                Method method = methods[item.Key];
                Step(method.Specialization.Declaration);
                ClrLirMethod body = new BodyLowerer(this, method).Run();
                ClrLirValidationResult validation = body.Validate(cancellationToken);
                if (!validation.IsValid)
                    Fail(method.Specialization.Declaration, "Invalid closed CLR LIR: " + validation.Diagnostics[0]);
                lowered.Add(body);
                spans.Add(method.Specialization.Declaration.Span);
            }
            byte[] genericMetadata = SafeCoreGenericMetadata.Encode(program, cancellationToken);
            Step(root);
            return new(lowered.AsReadOnly(), spans.AsReadOnly(), [])
            {
                ValueTypes = [.. layouts.Values.OrderBy(static value => value.Name, StringComparer.Ordinal)],
                GenericMetadata = genericMetadata,
                GenericInstances = [.. ordered.Select(static item => new RustSharpMetadataGenericInstance(
                    item.Value.Instance.FunctionId,
                    item.Value.Instance.Arguments.Length == 0
                        ? "()"
                        : string.Join(",", item.Value.Instance.Arguments.Select(static argument => argument.ToString()))))
                    .OrderBy(static value => value.FunctionId, StringComparer.Ordinal)
                    .ThenBy(static value => value.Arguments, StringComparer.Ordinal)],
                TraitImplementations = [.. program.Plan.SelectedImplementations
                    .Order(StringComparer.Ordinal)],
            };
        }

        private string InstanceKey(GenericFunctionInstance instance, SafeCoreHirNode node)
        {
            Step(node);
            var result = new StringBuilder(instance.FunctionId.Length.ToString(CultureInfo.InvariantCulture) + ":" + instance.FunctionId);
            foreach (RustType argument in instance.Arguments) result.Append(TypeKey(argument, node));
            if (result.Length > 65_536) Limit(node, "A closed instance identity exceeds its size limit.");
            return result.ToString();
        }

        private string TypeKey(RustType type, SafeCoreHirNode node)
        {
            var result = new StringBuilder();
            Append(type, 0);
            return result.ToString();
            void Append(RustType current, int depth)
            {
                Step(node, depth);
                if (current.Kind == RustTypeKind.Parameter) Fail(node, "CLR emission cannot contain open type parameters.");
                if (result.Length + current.Name.Length + 32 > 65_536) Limit(node, "A closed type identity exceeds its size limit.");
                result.Append((int)current.Kind).Append(':').Append(current.Name.Length.ToString(CultureInfo.InvariantCulture))
                    .Append(':').Append(current.Name).Append('[');
                foreach (RustType argument in current.Arguments) Append(argument, depth + 1);
                result.Append(']');
            }
        }

        private ClrLirType Map(RustType type, SafeCoreHirNode node, int depth = 0)
        {
            Step(node, depth);
            if (type.Equals(RustType.I32)) return ClrLirType.I32;
            if (type.Equals(RustType.Bool)) return ClrLirType.Bool;
            if (type.Equals(RustType.Text)) return ClrLirType.Text;
            if (type.Name == "$never") return ClrLirType.Void;
            string key = TypeKey(type, node);
            if (mappedTypes.TryGetValue(key, out ClrLirType previous)) return previous;
            if (layouts.Count >= ClrLirLimits.MaximumValueTypes) Limit(node, "Too many closed generic value layouts.");
            if (!activeLayouts.Add(key)) Fail(node, "A closed value layout is recursively embedded in itself.");
            var fields = new List<ClrLirField>();
            if (type.Equals(RustType.Unit)) { }
            else if (type.Name == "$tuple")
            {
                if (type.Arguments.Length > ClrLirLimits.MaximumFields)
                    Limit(node, "A closed tuple exceeds its field limit.");
                for (int index = 0; index < type.Arguments.Length; index++)
                    fields.Add(new(index.ToString(CultureInfo.InvariantCulture), Map(type.Arguments[index], node, depth + 1)));
            }
            else if (nominalTypes.TryGetValue(type.Name, out SafeCoreGenericNominalDefinition? nominal))
            {
                if (nominal.Parameters.Length != type.Arguments.Length) Fail(node, "A closed nominal type has invalid generic arity.");
                if (nominal.Fields.Length > ClrLirLimits.MaximumFields)
                    Limit(node, "A closed nominal type exceeds its field limit.");
                var bindings = new Dictionary<string, RustType>(StringComparer.Ordinal);
                for (int index = 0; index < nominal.Parameters.Length; index++) bindings.Add(nominal.Parameters[index], type.Arguments[index]);
                foreach (SafeCoreGenericFieldDefinition field in nominal.Fields)
                    fields.Add(new(field.Name, Map(Substitute(field.Type, bindings, field.Declaration, 0), field.Declaration, depth + 1)));
            }
            else Fail(node, "A closed nominal type has no checked field definition.");
            string name = "value_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
            var layout = new ClrLirValueType(name, fields);
            if (!layouts.TryAdd(name, layout)) Fail(node, "Conflicting closed value layout identities.");
            mappedTypes.Add(key, layout.Type);
            activeLayouts.Remove(key);
            return layout.Type;
        }

        private RustType Substitute(RustType type, IReadOnlyDictionary<string, RustType> bindings, SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (type.Kind == RustTypeKind.Parameter)
            {
                if (!bindings.TryGetValue(type.Name, out RustType? replacement)) Fail(node, "A field type contains an unbound generic parameter.");
                return replacement!;
            }
            if (type.Arguments.Length == 0) return type;
            return RustType.Named(type.Name, [.. type.Arguments.Select(argument => Substitute(argument, bindings, node, depth + 1))]);
        }

        private ClrLirValueType Layout(RustType type, SafeCoreHirNode node)
        {
            ClrLirType mapped = Map(type, node);
            if (mapped.Kind != ClrLirTypeKind.Value || !layouts.TryGetValue(mapped.Name!, out ClrLirValueType? layout))
                Fail(node, "A field operation requires a concrete value layout.");
            return layouts[mapped.Name!];
        }

        private void Step(SafeCoreHirNode node, int depth = 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++steps > 1_000_000 || depth > 128 || clock.Elapsed > TimeSpan.FromSeconds(10))
                Limit(node, "Generic CLR lowering exceeded its work, depth or time limit.");
        }

        [DoesNotReturn]
        private static void Limit(SafeCoreHirNode node, string message) =>
            throw new LoweringFailure(new(SafeCoreGenericDiagnosticCodes.LimitReached, message, node.Span));
        [DoesNotReturn]
        private static void Fail(SafeCoreHirNode node, string message) =>
            throw new LoweringFailure(new(InvalidLowering, message, node.Span));

        private sealed class BodyLowerer(Lowerer owner, Method method)
        {
            private readonly List<ClrLirLocal> locals = [];
            private readonly Dictionary<SafeCoreSymbol, int> bindings = [];
            private readonly List<Block> blocks = [];
            private Block? current;
            private int labels;
            private SafeCoreHirNode Declaration => method.Specialization.Declaration;
            private SafeCoreHirResult Hir => owner.Hir;

            public ClrLirMethod Run()
            {
                Start(NewBlock());
                int index = 0;
                foreach (SafeCoreHirNode parameter in Parts(Declaration).Where(static value => value.Kind == N.Parameter))
                {
                    SafeCoreHirNode pattern = Child(parameter, 0);
                    int local = Local(owner.Map(method.Specialization.PlannedSignature.ParameterTypes[index], parameter));
                    Emit(new ClrLirLoadArgument(index++));
                    Emit(new ClrLirStoreLocal(local));
                    if (pattern.DeclaredSymbol is { } symbol) bindings.Add(symbol, local);
                }
                Expression(method.Definition.Body, 0);
                if (current is not null) Return();
                return new(method.Name, ReturnType(method),
                    method.Specialization.PlannedSignature.ParameterTypes.Select(type => owner.Map(type, Declaration)), locals,
                    blocks.Select(static block => new ClrLirBlock(block.Label, block.Instructions)))
                {
                    SourceQualifiedName = Declaration.DeclaredSymbol?.QualifiedName,
                    IsPublic = method.IsEntry || Declaration.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Public),
                };
            }

            private ClrLirType ReturnType(Method target) => target.IsEntry ? ClrLirType.Void :
                owner.Map(target.Specialization.PlannedSignature.ReturnType, target.Specialization.Declaration);

            private void Expression(SafeCoreHirNode node, int depth)
            {
                owner.Step(node, depth);
                if (current is null) return;
                switch (node.Kind)
                {
                    case N.Block:
                        for (int index = 0; index < node.ChildIds.Count; index++)
                        {
                            SafeCoreHirNode child = Child(node, index);
                            Expression(child, depth + 1);
                            if (current is null) break;
                            if (index < node.ChildIds.Count - 1 || Statement(child)) Discard(child);
                        }
                        if (current is not null && (node.ChildIds.Count == 0 || Statement(Child(node, node.ChildIds.Count - 1)))) Unit();
                        break;
                    case N.Attribute:
                        Unit(); break;
                    case N.BlockExpression:
                        Expression(Child(node, 0), depth + 1); break;
                    case N.LetStatement:
                        SafeCoreHirNode pattern = Child(node, 0);
                        Expression(Child(node, node.ChildIds.Count - 1), depth + 1);
                        if (current is null) break;
                        int local = Local(Type(pattern));
                        Emit(new ClrLirStoreLocal(local));
                        if (pattern.DeclaredSymbol is { } binding) bindings.Add(binding, local);
                        Unit(); break;
                    case N.ExpressionStatement:
                        Expression(Child(node, 0), depth + 1);
                        if (current is not null) { Discard(Child(node, 0)); Unit(); }
                        break;
                    case N.ReturnStatement:
                    case N.ReturnExpression:
                        if (node.ChildIds.Count == 0) Unit();
                        else Expression(Child(node, 0), depth + 1);
                        if (current is not null) Return();
                        break;
                    case N.NameExpression:
                        if (node.ReferencedSymbol is { } referenced && bindings.TryGetValue(referenced, out int bound))
                            Emit(new ClrLirLoadLocal(bound));
                        else Emit(new ClrLirConstructValue(owner.Layout(ValueType(node), node)));
                        break;
                    case N.LiteralExpression:
                        if (node.Value is "true" or "false") Emit(new ClrLirLoadBoolean(node.Value == "true"));
                        else Emit(new ClrLirLoadInt32(Integer(node, false)));
                        break;
                    case N.TupleExpression:
                        if (node.ChildIds.Count == 1 && !node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma))
                            Expression(Child(node, 0), depth + 1);
                        else Construct(node, Parts(node), depth);
                        break;
                    case N.StructExpression:
                        Structure(node, depth); break;
                    case N.MemberExpression:
                        Member(node, depth); break;
                    case N.UnaryExpression:
                        Unary(node, depth); break;
                    case N.BinaryExpression:
                        Binary(node, depth); break;
                    case N.IfExpression:
                        Conditional(node, depth); break;
                    case N.CallExpression:
                        Call(node, depth); break;
                    case N.PrintExpression:
                        Print(node, depth); break;
                    default: Fail(node, "This checked HIR operation has no closed CLR lowering."); break;
                }
            }

            private void Construct(SafeCoreHirNode node, IEnumerable<SafeCoreHirNode> arguments, int depth)
            {
                var values = new List<int>();
                foreach (SafeCoreHirNode argument in arguments)
                {
                    values.Add(Evaluate(argument, depth + 1));
                    if (current is null) return;
                }
                ClrLirValueType layout = owner.Layout(ValueType(node), node);
                if (values.Count != layout.Fields.Length) Fail(node, "A constructor does not match its closed value layout.");
                foreach (int value in values) Emit(new ClrLirLoadLocal(value));
                Emit(new ClrLirConstructValue(layout));
            }

            private void Structure(SafeCoreHirNode node, int depth)
            {
                var values = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (SafeCoreHirNode field in Parts(node).Where(static value => value.Kind == N.StructExpressionField))
                {
                    int value = Evaluate(Child(field, 0), depth + 1);
                    if (current is null) return;
                    values.Add(field.Name!, value);
                }
                ClrLirValueType layout = owner.Layout(ValueType(node), node);
                if (values.Count != layout.Fields.Length) Fail(node, "Record construction is missing a closed field value.");
                foreach (ClrLirField field in layout.Fields)
                {
                    if (!values.TryGetValue(field.Name, out int value)) Fail(node, "Record field does not match its declaration.");
                    Emit(new ClrLirLoadLocal(value));
                }
                Emit(new ClrLirConstructValue(layout));
            }

            private void Member(SafeCoreHirNode node, int depth)
            {
                SafeCoreHirNode receiver = Child(node, 0);
                Expression(receiver, depth + 1);
                if (current is null) return;
                ClrLirValueType layout = owner.Layout(ValueType(receiver), receiver);
                int index = -1;
                for (int field = 0; field < layout.Fields.Length; field++)
                    if (layout.Fields[field].Name == node.Name) { index = field; break; }
                if (index < 0 || layout.Fields[index].Type != Type(node)) Fail(node, "Field projection does not match its closed layout.");
                Emit(new ClrLirReadField(layout, index));
            }

            private void Unary(SafeCoreHirNode node, int depth)
            {
                SafeCoreHirNode child = Child(node, 0);
                if (node.Value == "-" && child.Kind == N.LiteralExpression)
                { Emit(new ClrLirLoadInt32(Integer(child, true))); return; }
                int operand = Evaluate(child, depth + 1);
                if (current is null) return;
                if (node.Value == "-")
                {
                    Emit(new ClrLirLoadInt32(0)); Emit(new ClrLirLoadLocal(operand));
                    Emit(new ClrLirBinary(ClrLirBinaryOperator.SubtractChecked, ClrLirType.I32));
                }
                else
                {
                    Emit(new ClrLirLoadLocal(operand));
                    if (Type(node) == ClrLirType.Bool) Emit(new ClrLirLoadBoolean(true));
                    else Emit(new ClrLirLoadInt32(-1));
                    Emit(new ClrLirBinary(ClrLirBinaryOperator.ExclusiveOr, Type(node)));
                }
            }

            private void Binary(SafeCoreHirNode node, int depth)
            {
                if (node.Value == "=")
                {
                    Expression(Child(node, 1), depth + 1);
                    if (current is not null)
                    { Emit(new ClrLirStoreLocal(bindings[Child(node, 0).ReferencedSymbol!])); Unit(); }
                    return;
                }
                if (node.Value is "&&" or "||")
                {
                    Expression(Child(node, 0), depth + 1);
                    if (current is null) return;
                    Block evaluate = NewBlock(); Block shortcut = NewBlock(); Block join = NewBlock();
                    Emit(new ClrLirBranchTrue(node.Value == "&&" ? evaluate.Label : shortcut.Label));
                    Terminate(new ClrLirBranch(node.Value == "&&" ? shortcut.Label : evaluate.Label));
                    Start(shortcut); Emit(new ClrLirLoadBoolean(node.Value == "||")); Terminate(new ClrLirBranch(join.Label));
                    Start(evaluate); Expression(Child(node, 1), depth + 1);
                    if (current is not null) Terminate(new ClrLirBranch(join.Label));
                    Start(join); return;
                }
                int left = Evaluate(Child(node, 0), depth + 1);
                int right = Evaluate(Child(node, 1), depth + 1);
                if (current is null) return;
                Emit(new ClrLirLoadLocal(left)); Emit(new ClrLirLoadLocal(right));
                Emit(new ClrLirBinary(node.Value switch
                {
                    "+" => ClrLirBinaryOperator.AddChecked, "-" => ClrLirBinaryOperator.SubtractChecked,
                    "*" => ClrLirBinaryOperator.MultiplyChecked, "==" or "!=" => ClrLirBinaryOperator.Equal,
                    "<" or ">=" => ClrLirBinaryOperator.LessThan, ">" or "<=" => ClrLirBinaryOperator.GreaterThan,
                    _ => throw new LoweringFailure(new(InvalidLowering, "Unsupported checked binary operator.", node.Span)),
                }, Type(Child(node, 0))));
                if (node.Value is "!=" or "<=" or ">=")
                { Emit(new ClrLirLoadBoolean(false)); Emit(new ClrLirBinary(ClrLirBinaryOperator.Equal, ClrLirType.Bool)); }
            }

            private void Conditional(SafeCoreHirNode node, int depth)
            {
                Expression(Child(node, 0), depth + 1);
                if (current is null) return;
                Block thenBlock = NewBlock(); Block elseBlock = NewBlock(); Block join = NewBlock();
                Emit(new ClrLirBranchTrue(thenBlock.Label)); Terminate(new ClrLirBranch(elseBlock.Label));
                Start(thenBlock); Expression(Child(node, 1), depth + 1);
                bool thenContinues = current is not null;
                if (thenContinues) Terminate(new ClrLirBranch(join.Label));
                Start(elseBlock);
                if (node.ChildIds.Count == 3) Expression(Child(node, 2), depth + 1); else Unit();
                bool elseContinues = current is not null;
                if (elseContinues) Terminate(new ClrLirBranch(join.Label));
                if (thenContinues || elseContinues) Start(join);
            }

            private void Call(SafeCoreHirNode node, int depth)
            {
                if (!method.Specialization.Calls.TryGetValue(node.Id, out GenericFunctionInstance? instance))
                { Construct(node, Parts(node).Skip(1), depth); return; }
                if (!owner.methods.TryGetValue(owner.InstanceKey(instance, node), out Method? target))
                    Fail(node, "A static call has no reachable closed method instance.");
                var arguments = new List<int>();
                foreach (SafeCoreHirNode argument in Parts(node).Skip(1))
                {
                    arguments.Add(Evaluate(argument, depth + 1));
                    if (current is null) return;
                }
                foreach (int argument in arguments) Emit(new ClrLirLoadLocal(argument));
                Emit(new ClrLirCall(new(target!.Name, ReturnType(target),
                    target.Specialization.PlannedSignature.ParameterTypes.Select(type => owner.Map(type, node)))));
                if (target.IsEntry) Unit();
                else if (target.Specialization.PlannedSignature.ReturnType.Name == "$never")
                {
                    Block unreachable = NewBlock();
                    Terminate(new ClrLirBranch(unreachable.Label)); Start(unreachable); Terminate(new ClrLirBranch(unreachable.Label));
                }
            }

            private void Print(SafeCoreHirNode node, int depth)
            {
                if (node.ChildIds.Count == 1)
                {
                    if (!SyntaxTree.TryDecodeStringLiteral(Child(node, 0).Value!, out string text)) Fail(node, "Invalid checked print literal.");
                    Emit(new ClrLirLoadString(text));
                }
                else
                {
                    SafeCoreHirNode value = Child(node, 1);
                    Expression(value, depth + 1);
                    if (current is null) return;
                    if (Type(value) == ClrLirType.I32) Emit(new ClrLirFormatInt32());
                    else if (Type(value) == ClrLirType.Bool)
                    {
                        Block whenTrue = NewBlock(); Block whenFalse = NewBlock(); Block join = NewBlock();
                        Emit(new ClrLirBranchTrue(whenTrue.Label)); Terminate(new ClrLirBranch(whenFalse.Label));
                        Start(whenTrue); Emit(new ClrLirLoadString("true")); Terminate(new ClrLirBranch(join.Label));
                        Start(whenFalse); Emit(new ClrLirLoadString("false")); Terminate(new ClrLirBranch(join.Label)); Start(join);
                    }
                    else Fail(value, "A checked print value has unsupported CLR formatting.");
                }
                Emit(new ClrLirCall(new("Console.WriteLine", ClrLirType.Void, [ClrLirType.Text])));
                Unit();
            }

            private int Integer(SafeCoreHirNode node, bool negative)
            {
                string text = node.Value!;
                if (text.EndsWith("i32", StringComparison.Ordinal)) text = text[..^3];
                text = text.Replace("_", string.Empty, StringComparison.Ordinal);
                int radix = 10;
                if (text.StartsWith("0x", StringComparison.Ordinal)) { radix = 16; text = text[2..]; }
                else if (text.StartsWith("0o", StringComparison.Ordinal)) { radix = 8; text = text[2..]; }
                else if (text.StartsWith("0b", StringComparison.Ordinal)) { radix = 2; text = text[2..]; }
                long value = 0;
                foreach (char character in text)
                {
                    owner.Step(node);
                    int digit = character is >= '0' and <= '9' ? character - '0' : character is >= 'a' and <= 'f' ? character - 'a' + 10 : character - 'A' + 10;
                    if (digit < 0 || digit >= radix) Fail(node, "Invalid checked integer literal.");
                    value = value * radix + digit;
                    if (value > (negative ? 2147483648L : int.MaxValue)) Fail(node, "Checked integer is outside i32 range.");
                }
                return (int)(negative ? -value : value);
            }

            private int Evaluate(SafeCoreHirNode node, int depth)
            {
                Expression(node, depth);
                if (current is null) return -1;
                int local = Local(Type(node)); Emit(new ClrLirStoreLocal(local)); return local;
            }
            private void Unit() => Emit(new ClrLirConstructValue(owner.Layout(RustType.Unit, Declaration)));
            private void Discard(SafeCoreHirNode node)
            { ClrLirType type = Type(node); if (type != ClrLirType.Void) Emit(new ClrLirDiscard(type)); }
            private void Return()
            {
                if (method.IsEntry) Emit(new ClrLirDiscard(owner.Map(RustType.Unit, Declaration)));
                Terminate(new ClrLirReturn());
            }
            private int Local(ClrLirType type)
            {
                if (locals.Count >= ClrLirLimits.MaximumLocals) Limit(Declaration, "Closed method exceeds its local variable limit.");
                int index = locals.Count; locals.Add(new("local_" + index.ToString(CultureInfo.InvariantCulture), type)); return index;
            }
            private Block NewBlock()
            {
                if (++labels > ClrLirLimits.MaximumBlocks) Limit(Declaration, "Closed method exceeds its block limit.");
                return new("bb" + labels.ToString(CultureInfo.InvariantCulture));
            }
            private void Start(Block block) { blocks.Add(block); current = block; }
            private void Emit(ClrLirInstruction instruction)
            {
                owner.Step(Declaration);
                if (current!.Instructions.Count >= ClrLirLimits.MaximumInstructionsPerBlock) Limit(Declaration, "Closed block exceeds its instruction limit.");
                current.Instructions.Add(instruction);
            }
            private void Terminate(ClrLirInstruction instruction) { Emit(instruction); current = null; }
            private SafeCoreHirNode Child(SafeCoreHirNode node, int index) => Hir.GetNode(node.ChildIds[index]);
            private IEnumerable<SafeCoreHirNode> Parts(SafeCoreHirNode node) => node.ChildIds.Select(Hir.GetNode);
            private RustType ValueType(SafeCoreHirNode node)
            {
                if (!method.Specialization.Types.TryGetValue(node.Id, out RustType? type)) Fail(node, "A HIR value is missing closed type evidence.");
                return type!;
            }
            private ClrLirType Type(SafeCoreHirNode node) => owner.Map(ValueType(node), node);
            private static bool Statement(SafeCoreHirNode node) => node.Kind is N.LetStatement or N.ReturnStatement or N.ExpressionStatement or N.Attribute;
            private sealed class Block(string label)
            {
                public string Label { get; } = label;
                public List<ClrLirInstruction> Instructions { get; } = [];
            }
        }
    }
}
