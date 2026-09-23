using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.CodeGen.IL;

/// <summary>
/// Lowers the validated scalar/aggregate portion of SafeCore MIR directly to
/// CLR LIR.  Keeping this pass MIR-shaped prevents the executable MIR profile
/// from silently re-checking and lowering a different HIR representation.
/// References, closures, dynamic indexing and other ownership-bearing values
/// remain explicit profile boundaries.
/// </summary>
public static class SafeCoreMirClrLowering
{
    public const string Unsupported = "RSM2101";
    public const string Invalid = "RSM2102";
    public const string LimitReached = "RSM2103";

    public static SafeCoreClrResult Lower(
        SafeCoreMirProgram program,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return new Lowerer(program, cancellationToken).Run();
        }
        catch (LoweringFailure failure)
        {
            return new([], [], [failure.Diagnostic]);
        }
        catch (TimeoutException exception)
        {
            return new([], [], [new Diagnostic(LimitReached, exception.Message, new TextSpan(0, 0))]);
        }
    }

    private sealed class LoweringFailure(Diagnostic diagnostic) : Exception(diagnostic.Message)
    {
        public Diagnostic Diagnostic { get; } = diagnostic;
    }

    private sealed class Lowerer(SafeCoreMirProgram program, CancellationToken cancellationToken)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<string, ClrLirValueType> _layouts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _activeLayouts = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _methodNames = [];
        private int _steps;

        public SafeCoreClrResult Run()
        {
            Step();
            if (program.Functions.Count is < 1 or > 128)
                Limit("Safe-core MIR CLR lowering requires 1 to 128 functions.");

            int entryCount = 0;
            foreach (SafeCoreMirFunction function in program.Functions)
            {
                Step();
                if (!_methodNames.TryAdd(function.Id,
                        IsEntryName(function.Name) ? "Main" : "fn_" + function.Id.ToString(CultureInfo.InvariantCulture)))
                    Fail(function, "MIR function IDs must be unique.");
                if (IsEntryName(function.Name)) entryCount++;
                ValidateLocals(function);
            }

            if (entryCount != 1)
                Fail(program.Functions[0], "Safe-core MIR executable lowering requires one fn main.");

            var methods = new List<ClrLirMethod>(program.Functions.Count);
            var spans = new List<TextSpan>(program.Functions.Count);
            foreach (SafeCoreMirFunction function in program.Functions)
            {
                Step();
                ClrLirMethod method = new BodyLowerer(this, function).Run();
                ClrLirValidationResult validation = method.Validate(cancellationToken);
                if (!validation.IsValid)
                    Fail(function, "Invalid CLR LIR: " + validation.Diagnostics[0]);
                methods.Add(method);
                spans.Add(function.Source.Span);
            }

            return new(methods.AsReadOnly(), spans.AsReadOnly(), [])
            {
                ValueTypes = [.. _layouts.Values.OrderBy(static value => value.Name, StringComparer.Ordinal)],
            };
        }

        private void ValidateLocals(SafeCoreMirFunction function)
        {
            bool parametersEnded = false;
            int parameters = 0;
            int expectedId = 0;
            foreach (SafeCoreMirLocal local in function.Locals)
            {
                Step();
                if (local.Id != expectedId++)
                    Fail(function, "MIR local IDs must form a dense zero-based arena.");
                if (local.Kind == SafeCoreMirLocalKind.Parameter)
                {
                    if (parametersEnded) Fail(function, "MIR parameters must precede user locals.");
                    parameters++;
                }
                else
                {
                    parametersEnded = true;
                }
                if (local.Type.Kind == SafeCoreSemanticTypeKind.Adt && !local.IsUnitAdt)
                    FailUnsupported(local.Source, "ADT locals require explicit zero-field declaration evidence.");
                StorageType(local.Type, local.Source);
            }
            if (parameters > ClrLirLimits.MaximumParameters)
                Limit("A MIR function exceeds the CLR parameter limit.");
        }

        internal string MethodName(int functionId, SafeCoreMirSource source)
        {
            if (!_methodNames.TryGetValue(functionId, out string? name))
                Fail(source, "A MIR call targets an unknown function.");
            return name;
        }

        internal ClrLirType StorageType(SafeCoreType type, SafeCoreMirSource source, int depth = 0)
        {
            Step();
            if (depth > 128) Limit("MIR value type nesting exceeded the CLR lowering limit.");
            return type.Kind switch
            {
                SafeCoreSemanticTypeKind.Unit => Layout(type, source, depth).Type,
                SafeCoreSemanticTypeKind.Bool => ClrLirType.Bool,
                SafeCoreSemanticTypeKind.I32 or SafeCoreSemanticTypeKind.Usize => ClrLirType.I32,
                // A Rust `char` is a Unicode scalar value, not an i32 ABI
                // value.  Until the CLR LIR has an explicit scalar-char
                // representation and formatter, reject it at every value
                // boundary instead of silently changing its meaning.
                SafeCoreSemanticTypeKind.Char => FailType(type, source),
                SafeCoreSemanticTypeKind.Tuple or SafeCoreSemanticTypeKind.Array => Layout(type, source, depth).Type,
                // The bounded reference profile represents a direct local
                // borrow as an alias to the owner's CLR value slot. MIR
                // ownership evidence preserves the provenance; executable LIR
                // keeps the representation scalar and performs dereference
                // reads/writes against the owner destination.
                SafeCoreSemanticTypeKind.Reference => StorageType(type.ElementType, source, depth + 1),
                // A unit ADT has no fields and is represented by the same
                // zero-field CLR value as `()`. Non-unit ADTs remain outside
                // this bounded executable representation until their layout
                // metadata is available.
                SafeCoreSemanticTypeKind.Adt => Layout(type, source, depth).Type,
                _ => FailType(type, source),
            };
        }

        internal ClrLirType ReturnType(SafeCoreType type, SafeCoreMirSource source) =>
            type.Kind is SafeCoreSemanticTypeKind.Unit or SafeCoreSemanticTypeKind.Never
                ? ClrLirType.Void
                : StorageType(type, source);

        internal ClrLirValueType Layout(SafeCoreType type, SafeCoreMirSource source, int depth = 0)
        {
            Step();
            string key = TypeKey(type);
            if (_layouts.TryGetValue(key, out ClrLirValueType? existing)) return existing;
            if (!_activeLayouts.Add(key)) Fail(source, "A recursive MIR value layout is not supported.");
            try
            {
                var fields = new List<ClrLirField>();
                if (type.Kind == SafeCoreSemanticTypeKind.Tuple)
                {
                    if (type.Elements.Count > ClrLirLimits.MaximumFields)
                        Limit("A MIR tuple exceeds the CLR field limit.");
                    for (int index = 0; index < type.Elements.Count; index++)
                        fields.Add(new(index.ToString(CultureInfo.InvariantCulture),
                            StorageType(type.Elements[index], source, depth + 1)));
                }
                else if (type.Kind == SafeCoreSemanticTypeKind.Array)
                {
                    long? declaredLength = type.Length;
                    if (declaredLength is null)
                        Limit("A MIR fixed array requires a known length.");
                    long length = declaredLength.Value;
                    if (length < 0 || length > ClrLirLimits.MaximumFields)
                        Limit("A MIR fixed array exceeds the CLR field limit.");
                    for (int index = 0; index < length; index++)
                        fields.Add(new(index.ToString(CultureInfo.InvariantCulture),
                            StorageType(type.ElementType, source, depth + 1)));
                }
                else if (type.Kind is not (SafeCoreSemanticTypeKind.Unit or SafeCoreSemanticTypeKind.Adt))
                {
                    Fail(source, "Only tuple, array and unit layouts are supported by MIR CLR lowering.");
                }

                string name = "mir_value_" + Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..24];
                var layout = new ClrLirValueType(name, fields);
                _layouts.Add(key, layout);
                return layout;
            }
            finally
            {
                _activeLayouts.Remove(key);
            }
        }

        internal void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_steps > 1_000_000 || _clock.Elapsed > TimeSpan.FromSeconds(10))
                Limit("Safe-core MIR CLR lowering exceeded its work or time limit.");
        }

        internal static string TypeKey(SafeCoreType type) =>
            type.Kind + "|" + type.ToString();

        private static bool IsEntryName(string name) =>
            string.Equals(name, "main", StringComparison.Ordinal) ||
            name.EndsWith("::main", StringComparison.Ordinal) ||
            name.Contains("::main#", StringComparison.Ordinal);

        private static ClrLirType FailType(SafeCoreType type, SafeCoreMirSource source) =>
            throw new LoweringFailure(new(Unsupported,
                "MIR CLR lowering does not support value type '" + type + "'.", source.Span)
            { SourcePath = source.SourcePath });

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        internal static void FailUnsupported(SafeCoreMirSource source, string message) =>
            throw new LoweringFailure(new(Unsupported, message, source.Span)
            { SourcePath = source.SourcePath });

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        internal static void Fail(SafeCoreMirFunction function, string message) =>
            throw new LoweringFailure(new(Invalid, message, function.Source.Span)
            { SourcePath = function.Source.SourcePath });

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        internal static void Fail(SafeCoreMirSource source, string message) =>
            throw new LoweringFailure(new(Invalid, message, source.Span)
            { SourcePath = source.SourcePath });

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        internal static void Limit(string message) =>
            throw new LoweringFailure(new(LimitReached, message, new TextSpan(0, 0)));
    }

    private sealed class BodyLowerer(Lowerer owner, SafeCoreMirFunction function)
    {
        private readonly List<ClrLirLocal> _locals = [];
        private readonly List<Block> _blocks = [];
        private readonly Dictionary<int, string> _labels = [];
        private readonly Dictionary<int, ClrLirType> _localTypes = [];
        private readonly Dictionary<int, int> _referenceOwners = [];
        private Block? _current;
        private int _extraLabel;

        private sealed class Block(string label)
        {
            public string Label { get; } = label;
            public List<ClrLirInstruction> Instructions { get; } = [];
        }

        public ClrLirMethod Run()
        {
            CollectReferenceProvenance();
            foreach (SafeCoreMirBlock block in ReachableBlocks())
            {
                owner.Step();
                _labels[block.Id] = "bb" + block.Id.ToString(CultureInfo.InvariantCulture);
            }

            foreach (SafeCoreMirLocal local in function.Locals)
            {
                owner.Step();
                ClrLirType type = owner.StorageType(local.Type, local.Source);
                _localTypes.Add(local.Id, type);
                _locals.Add(new("local_" + local.Id.ToString(CultureInfo.InvariantCulture), type));
            }

            int parameterCount = function.Locals.TakeWhile(static local => local.Kind == SafeCoreMirLocalKind.Parameter).Count();
            var parameters = function.Locals.Take(parameterCount)
                .Select(local => _localTypes[local.Id]).ToArray();

            foreach (SafeCoreMirBlock mirBlock in ReachableBlocks())
            {
                Start(new Block(_labels[mirBlock.Id]));
                if (mirBlock.Id == function.EntryBlockId)
                {
                    int entryParameterCount = function.Locals.TakeWhile(
                        static local => local.Kind == SafeCoreMirLocalKind.Parameter).Count();
                    for (int parameter = 0; parameter < entryParameterCount; parameter++)
                    {
                        Emit(new ClrLirLoadArgument(parameter));
                        Store(parameter, function.Source);
                    }
                }
                foreach (SafeCoreMirStatement statement in mirBlock.Statements)
                {
                    owner.Step();
                    if (statement.Value.Kind == SafeCoreMirRvalueKind.Write &&
                        RootOwner(statement.Value.Operands[0].Id, statement.Source) != statement.DestinationLocalId)
                        Lowerer.Fail(statement.Source, "A MIR write destination differs from its reference provenance.");
                    EmitRvalue(statement.Value);
                    Store(statement.DestinationLocalId, statement.Source);
                }
                EmitTerminator(mirBlock.Terminator, mirBlock.Source);
            }

            ClrLirType returnType = owner.ReturnType(function.ReturnType, function.Source);
            return new(owner.MethodName(function.Id, function.Source), returnType, parameters, _locals,
                _blocks.Select(static block => new ClrLirBlock(block.Label, block.Instructions)))
            {
                SourceQualifiedName = function.Name,
                // The CLR entry point must remain externally discoverable even
                // when Rust's source declaration uses its default private visibility.
                IsPublic = function.IsPublic || IsEntryName(function.Name),
            };
        }

        private void CollectReferenceProvenance()
        {
            foreach (SafeCoreMirLocal local in function.Locals)
            {
                owner.Step();
                if (local.Type.Kind == SafeCoreSemanticTypeKind.Reference && local.Kind == SafeCoreMirLocalKind.Parameter)
                    Lowerer.FailUnsupported(local.Source, "Reference parameters require an explicit interprocedural ownership contract.");
            }
            foreach (SafeCoreMirBlock block in function.Blocks)
            foreach (SafeCoreMirStatement statement in block.Statements)
            {
                owner.Step();
                if (statement.Value.Type.Kind != SafeCoreSemanticTypeKind.Reference) continue;
                SafeCoreMirRvalue value = statement.Value;
                bool directBorrow = value.Kind == SafeCoreMirRvalueKind.Unary &&
                    value.Operator is ("&" or "&mut" or "reborrow" or "reborrow_mut") &&
                    value.Operands.Count == 1 && value.Operands[0].Kind == SafeCoreMirOperandKind.Local;
                bool referenceCopy = value.Kind == SafeCoreMirRvalueKind.Use &&
                    value.Operands.Count == 1 && value.Operands[0].Kind == SafeCoreMirOperandKind.Local &&
                    value.Operands[0].Type.Kind == SafeCoreSemanticTypeKind.Reference;
                if ((!directBorrow && !referenceCopy) ||
                    !_referenceOwners.TryAdd(statement.DestinationLocalId, value.Operands[0].Id))
                    Lowerer.FailUnsupported(value.Source, "Reference locals require one explicit, immutable MIR borrow origin.");
            }
            foreach (SafeCoreMirLocal local in function.Locals)
            {
                owner.Step();
                if (local.Type.Kind == SafeCoreSemanticTypeKind.Reference) _ = RootOwner(local.Id, local.Source);
            }
        }

        private int RootOwner(int reference, SafeCoreMirSource source)
        {
            for (int depth = 0; depth <= function.Locals.Count; depth++)
            {
                owner.Step();
                if (reference < 0 || reference >= function.Locals.Count) Lowerer.Fail(source, "Reference origin is outside its local arena.");
                if (function.Locals[reference].Type.Kind != SafeCoreSemanticTypeKind.Reference) return reference;
                if (!_referenceOwners.TryGetValue(reference, out reference))
                    Lowerer.FailUnsupported(source, "Reference provenance is missing from MIR.");
            }
            Lowerer.Fail(source, "Reference provenance contains a cycle.");
            return -1;
        }

        private static bool IsEntryName(string name) =>
            string.Equals(name, "main", StringComparison.Ordinal) ||
            name.EndsWith("::main", StringComparison.Ordinal) ||
            name.Contains("::main#", StringComparison.Ordinal);

        private List<SafeCoreMirBlock> ReachableBlocks()
        {
            var byId = function.Blocks.ToDictionary(static block => block.Id);
            var visited = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(function.EntryBlockId);
            while (queue.Count != 0)
            {
                owner.Step();
                int id = queue.Dequeue();
                if (!visited.Add(id) || !byId.TryGetValue(id, out SafeCoreMirBlock? block)) continue;
                SafeCoreMirTerminator terminator = block.Terminator;
                if (terminator.Kind is SafeCoreMirTerminatorKind.Goto or SafeCoreMirTerminatorKind.Branch)
                    queue.Enqueue(terminator.TargetBlockId);
                else if (terminator.Kind == SafeCoreMirTerminatorKind.Call)
                    queue.Enqueue(terminator.TargetBlockId);
                if (terminator.Kind == SafeCoreMirTerminatorKind.Branch)
                    queue.Enqueue(terminator.FalseTargetBlockId);
            }

            var result = new List<SafeCoreMirBlock>(visited.Count);
            foreach (SafeCoreMirBlock block in function.Blocks)
                if (visited.Contains(block.Id)) result.Add(block);
            if (result.Count == 0) Lowerer.Fail(function, "MIR entry block is not reachable.");
            return result;
        }

        private void EmitRvalue(SafeCoreMirRvalue value)
        {
            owner.Step();
            switch (value.Kind)
            {
                case SafeCoreMirRvalueKind.Use:
                    EmitOperand(value.Operands[0]);
                    return;
                case SafeCoreMirRvalueKind.Unary:
                    EmitUnary(value);
                    return;
                case SafeCoreMirRvalueKind.Binary:
                    EmitBinary(value);
                    return;
                case SafeCoreMirRvalueKind.Coerce:
                case SafeCoreMirRvalueKind.Cast:
                    // The current CLR value model represents usize as i32, but
                    // it has no conversion instruction for changing CLR stack
                    // types. Keep identity-representation casts explicit and
                    // reject the rest before emitting malformed stack code
                    // (for example bool -> i32).
                    ClrLirType sourceType = owner.StorageType(value.Operands[0].Type, value.Source);
                    ClrLirType targetType = owner.StorageType(value.Type, value.Source);
                    if (sourceType != targetType)
                        Lowerer.FailUnsupported(value.Source,
                            "MIR CLR lowering supports casts and coercions only when source and target share a CLR representation.");
                    EmitOperand(value.Operands[0]);
                    return;
                case SafeCoreMirRvalueKind.Tuple:
                case SafeCoreMirRvalueKind.Array:
                    foreach (SafeCoreMirOperand operand in value.Operands) EmitOperand(operand);
                    Emit(new ClrLirConstructValue(owner.Layout(value.Type, value.Source)));
                    return;
                case SafeCoreMirRvalueKind.Index:
                    EmitIndex(value);
                    return;
                case SafeCoreMirRvalueKind.Field:
                    EmitField(value);
                    return;
                case SafeCoreMirRvalueKind.Write:
                    // The enclosing MIR statement stores the computed value
                    // into the owner local identified during lowering.
                    EmitOperand(value.Operands[1]);
                    return;
                case SafeCoreMirRvalueKind.Print:
                    EmitPrint(value);
                    return;
                default:
                    Lowerer.Fail(value.Source, "Unknown MIR rvalue kind.");
                    return;
            }
        }

        private void EmitOperand(SafeCoreMirOperand operand)
        {
            owner.Step();
            switch (operand.Kind)
            {
                case SafeCoreMirOperandKind.Local:
                    if (!_localTypes.ContainsKey(operand.Id)) Lowerer.Fail(operand.Source, "MIR local operand is outside its local arena.");
                    Emit(new ClrLirLoadLocal(operand.Id));
                    return;
                case SafeCoreMirOperandKind.Constant:
                    EmitConstant(operand);
                    return;
                default:
                    Lowerer.Fail(operand.Source, "Function operands are valid only on MIR call terminators.");
                    return;
            }
        }

        private void EmitConstant(SafeCoreMirOperand operand)
        {
            SafeCoreSemanticTypeKind kind = operand.Type.Kind;
            if (kind == SafeCoreSemanticTypeKind.Unit)
            {
                if (operand.Value != "()") Lowerer.Fail(operand.Source, "Invalid unit MIR constant.");
                Emit(new ClrLirConstructValue(owner.Layout(operand.Type, operand.Source)));
                return;
            }
            if (kind == SafeCoreSemanticTypeKind.Adt)
            {
                if (operand.Value != "()") Lowerer.Fail(operand.Source, "Invalid unit ADT MIR constant.");
                Emit(new ClrLirConstructValue(owner.Layout(operand.Type, operand.Source)));
                return;
            }
            if (kind == SafeCoreSemanticTypeKind.Bool)
            {
                if (operand.Value is not ("true" or "false")) Lowerer.Fail(operand.Source, "Invalid bool MIR constant.");
                Emit(new ClrLirLoadBoolean(operand.Value == "true"));
                return;
            }
            if (kind == SafeCoreSemanticTypeKind.Char)
                Lowerer.FailUnsupported(operand.Source,
                    "MIR CLR lowering does not support char values until a Unicode scalar ABI is available.");
            if (kind is not (SafeCoreSemanticTypeKind.I32 or SafeCoreSemanticTypeKind.Usize))
                Lowerer.Fail(operand.Source, "MIR constant is outside the scalar CLR profile.");
            if (!int.TryParse(operand.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int integer))
                Lowerer.Fail(operand.Source, "MIR constant is outside the i32 CLR range.");
            Emit(new ClrLirLoadInt32(integer));
        }

        private void EmitUnary(SafeCoreMirRvalue value)
        {
            if (value.Operator == "-")
            {
                Emit(new ClrLirLoadInt32(0));
                EmitOperand(value.Operands[0]);
                Emit(new ClrLirBinary(ClrLirBinaryOperator.SubtractChecked, ClrLirType.I32));
                return;
            }
            if (value.Operator == "!")
            {
                EmitOperand(value.Operands[0]);
                if (value.Type.Kind == SafeCoreSemanticTypeKind.Bool)
                {
                    Emit(new ClrLirLoadBoolean(true));
                    Emit(new ClrLirBinary(ClrLirBinaryOperator.ExclusiveOr, ClrLirType.Bool));
                }
                else
                {
                    Emit(new ClrLirLoadInt32(-1));
                    Emit(new ClrLirBinary(ClrLirBinaryOperator.ExclusiveOr, ClrLirType.I32));
                }
                return;
            }
            if (value.Operator == "*")
            {
                SafeCoreMirOperand reference = value.Operands[0];
                if (reference.Kind != SafeCoreMirOperandKind.Local)
                    Lowerer.FailUnsupported(value.Source, "Dereference requires an explicit MIR reference local.");
                Emit(new ClrLirLoadLocal(RootOwner(reference.Id, value.Source)));
                return;
            }
            if (value.Operator is "&" or "&mut" or "reborrow" or "reborrow_mut")
            {
                // The reference slot itself is an inert bounded token. Reads
                // dereference the proven owner slot above, preserving alias
                // identity after writes rather than loading this snapshot.
                EmitOperand(value.Operands[0]);
                return;
            }
            Lowerer.Fail(value.Source, "Unsupported MIR unary operator.");
        }

        private void EmitBinary(SafeCoreMirRvalue value)
        {
            if (value.Operator is not ("+" or "-" or "*" or "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||"))
                Lowerer.FailUnsupported(value.Source, "The CLR MIR profile supports only bounded scalar binary operators.");
            EmitOperand(value.Operands[0]);
            EmitOperand(value.Operands[1]);
            ClrLirType operandType = value.Operands[0].Type.Kind == SafeCoreSemanticTypeKind.Bool
                ? ClrLirType.Bool : ClrLirType.I32;
            Emit(new ClrLirBinary(value.Operator switch
            {
                "+" => ClrLirBinaryOperator.AddChecked,
                "-" => ClrLirBinaryOperator.SubtractChecked,
                "*" => ClrLirBinaryOperator.MultiplyChecked,
                "==" or "!=" => ClrLirBinaryOperator.Equal,
                // Inclusive comparisons are lowered as the negation of the
                // opposite strict comparison below: <= is !(>), and >= is
                // !(<).  Mapping them to their same-direction operator would
                // silently swap the two inclusive relations.
                "<" or ">=" => ClrLirBinaryOperator.LessThan,
                ">" or "<=" => ClrLirBinaryOperator.GreaterThan,
                "&&" => ClrLirBinaryOperator.And,
                "||" => ClrLirBinaryOperator.Or,
                _ => throw new InvalidOperationException("Unsupported MIR binary operator."),
            }, operandType));
            if (value.Operator is "!=" or "<=" or ">=")
            {
                Emit(new ClrLirLoadBoolean(false));
                Emit(new ClrLirBinary(ClrLirBinaryOperator.Equal, ClrLirType.Bool));
            }
        }

        private void EmitField(SafeCoreMirRvalue value)
        {
            if (value.Operands.Count != 1 || value.Operands[0].Type.Kind != SafeCoreSemanticTypeKind.Tuple ||
                !int.TryParse(value.Operator, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                index < 0 || index >= value.Operands[0].Type.Elements.Count ||
                value.Type != value.Operands[0].Type.Elements[index])
                Lowerer.Fail(value.Source, "MIR tuple projection does not match its aggregate layout.");
            EmitOperand(value.Operands[0]);
            Emit(new ClrLirReadField(owner.Layout(value.Operands[0].Type, value.Source),
                int.Parse(value.Operator!, CultureInfo.InvariantCulture)));
        }

        private void EmitIndex(SafeCoreMirRvalue value)
        {
            SafeCoreMirOperand array = value.Operands[0];
            SafeCoreMirOperand index = value.Operands[1];
            BigInteger constant = BigInteger.Zero;
            if (array.Type.Kind != SafeCoreSemanticTypeKind.Array || index.Kind != SafeCoreMirOperandKind.Constant ||
                !BigInteger.TryParse(index.Value, NumberStyles.None, CultureInfo.InvariantCulture, out constant) ||
                constant < 0 || constant > int.MaxValue)
                Lowerer.FailUnsupported(value.Source, "MIR CLR lowering requires a statically known fixed-array index.");
            long? declaredLength = array.Type.Length;
            if (declaredLength is null)
                Lowerer.Fail(value.Source, "MIR fixed-array length evidence is missing.");
            long length = declaredLength.Value;
            if (constant >= length)
                Lowerer.Fail(value.Source, "MIR fixed-array index is outside its validated bounds.");
            EmitOperand(array);
            Emit(new ClrLirReadField(owner.Layout(array.Type, array.Source), (int)constant));
        }

        private void EmitPrint(SafeCoreMirRvalue value)
        {
            if (value.Operator == "{}")
            {
                SafeCoreMirOperand operand = value.Operands[0];
                if (operand.Type.Kind == SafeCoreSemanticTypeKind.Bool)
                {
                    EmitBoolText(operand);
                }
                else if (operand.Type.Kind is SafeCoreSemanticTypeKind.I32 or SafeCoreSemanticTypeKind.Usize)
                {
                    EmitOperand(operand);
                    Emit(new ClrLirFormatInt32());
                }
                else if (operand.Type.Kind == SafeCoreSemanticTypeKind.Char)
                    Lowerer.FailUnsupported(value.Source,
                        "MIR CLR lowering does not support char values until a Unicode scalar ABI is available.");
                else if (operand.Type.Kind == SafeCoreSemanticTypeKind.Unit)
                {
                    Emit(new ClrLirLoadString("()"));
                }
                else
                {
                    Lowerer.FailUnsupported(value.Source, "MIR print formatting supports only scalar and unit values.");
                }
            }
            else
            {
                Emit(new ClrLirLoadString(value.Operator ?? string.Empty));
            }
            Emit(new ClrLirCall(new("Console.WriteLine", ClrLirType.Void, [ClrLirType.Text])));
            Emit(new ClrLirConstructValue(owner.Layout(SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Unit), value.Source)));
        }

        private void EmitBoolText(SafeCoreMirOperand operand)
        {
            Block whenTrue = NewBlock();
            Block whenFalse = NewBlock();
            Block join = NewBlock();
            EmitOperand(operand);
            Terminate(new ClrLirBranchTrue(whenTrue.Label));
            Start(whenFalse);
            Emit(new ClrLirLoadString("false"));
            Terminate(new ClrLirBranch(join.Label));
            Start(whenTrue);
            Emit(new ClrLirLoadString("true"));
            Terminate(new ClrLirBranch(join.Label));
            Start(join);
        }

        private void EmitTerminator(SafeCoreMirTerminator terminator, SafeCoreMirSource source)
        {
            owner.Step();
            switch (terminator.Kind)
            {
                case SafeCoreMirTerminatorKind.Return:
                    if (terminator.Operand is not null)
                    {
                        EmitOperand(terminator.Operand);
                        if (function.ReturnType.Kind == SafeCoreSemanticTypeKind.Unit)
                            Emit(new ClrLirDiscard(owner.StorageType(terminator.Operand.Type, source)));
                    }
                    Terminate(new ClrLirReturn());
                    break;
                case SafeCoreMirTerminatorKind.Goto:
                    Terminate(new ClrLirBranch(Label(terminator.TargetBlockId, source)));
                    break;
                case SafeCoreMirTerminatorKind.Branch:
                    EmitOperand(terminator.Operand ?? throw new InvalidOperationException());
                    Terminate(new ClrLirBranchTrue(Label(terminator.TargetBlockId, source)));
                    // The false edge is a separate block instruction sequence;
                    // append it to the just-terminated block by starting a tiny
                    // bridge block so both successors have empty stacks.
                    Block bridge = NewBlock();
                    Start(bridge);
                    Terminate(new ClrLirBranch(Label(terminator.FalseTargetBlockId, source)));
                    // Replace the true terminator's fall-through edge with the
                    // bridge only when the LIR encoder can reach it; BranchTrue
                    // naturally falls through to the next block, which is bridge.
                    break;
                case SafeCoreMirTerminatorKind.Call:
                    EmitCall(terminator, source);
                    break;
                case SafeCoreMirTerminatorKind.Unreachable:
                    Terminate(new ClrLirBranch(_current?.Label ?? "bb0"));
                    break;
                default:
                    Lowerer.Fail(source, "Unknown MIR terminator kind.");
                    break;
            }
        }

        private void EmitCall(SafeCoreMirTerminator terminator, SafeCoreMirSource source)
        {
            SafeCoreMirOperand? callee = terminator.Operand;
            if (callee is null || callee.Kind != SafeCoreMirOperandKind.Function)
                Lowerer.FailUnsupported(source, "MIR CLR lowering supports direct function calls only.");
            if (callee.Type.Kind != SafeCoreSemanticTypeKind.Function)
                Lowerer.Fail(source, "MIR call operand does not carry a function signature.");
            foreach (SafeCoreMirOperand argument in terminator.Arguments) EmitOperand(argument);
            var parameterTypes = callee.Type.ParameterTypes.Select(type => owner.StorageType(type, source)).ToArray();
            ClrLirType returnType = owner.ReturnType(callee.Type.ReturnType, source);
            Emit(new ClrLirCall(new(owner.MethodName(callee.Id, source), returnType, parameterTypes)));
            if (terminator.DestinationLocalId is int destination)
                Store(destination, source);
            if (terminator.TargetBlockId >= 0)
                Terminate(new ClrLirBranch(Label(terminator.TargetBlockId, source)));
            else if (_current is not null)
                Terminate(new ClrLirBranch(_current.Label));
        }

        private string Label(int id, SafeCoreMirSource source) =>
            _labels.TryGetValue(id, out string? label)
                ? label
                : throw new LoweringFailure(new(SafeCoreMirClrLowering.Invalid, "MIR terminator targets an unreachable block.", source.Span)
                { SourcePath = source.SourcePath });

        private void Store(int id, SafeCoreMirSource source)
        {
            if (!_localTypes.ContainsKey(id)) Lowerer.Fail(source, "MIR destination local is outside its local arena.");
            Emit(new ClrLirStoreLocal(id));
        }

        private Block NewBlock() => new("extra_" + (++_extraLabel).ToString(CultureInfo.InvariantCulture));

        private void Start(Block block)
        {
            _blocks.Add(block);
            _current = block;
        }

        private void Terminate(ClrLirInstruction instruction)
        {
            Emit(instruction);
            _current = null;
        }

        private void Emit(ClrLirInstruction instruction)
        {
            if (_current is null) Lowerer.Fail(function, "MIR CLR lowering emitted into a terminated block.");
            owner.Step();
            _current.Instructions.Add(instruction);
        }
    }
}
