using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.CodeGen.IL;

/// <summary>
/// Lowers the validated scalar/aggregate portion of SafeCore MIR directly to
/// CLR LIR.  Keeping this pass MIR-shaped prevents the executable MIR profile
/// from silently re-checking and lowering a different HIR representation.
/// Named value layouts and storage places retain their CLR identity. Sized
/// references use managed pointers; byref-like aggregates remain a boundary.
/// </summary>
public static partial class SafeCoreMirClrLowering
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
            SafeCoreMirValidationResult validation = SafeCoreMirValidation.Validate(program,
                new() { CancellationToken = cancellationToken });
            if (!validation.IsSuccessful)
                return new([], [], validation.Diagnostics.Select(static item => new Diagnostic(item.Code,
                    item.Message, item.Source?.Span ?? new TextSpan(0, 0)) { SourcePath = item.Source?.SourcePath }).ToArray());
            SafeCoreMirReferenceProvenanceResult provenance = SafeCoreMirReferenceProvenance.Analyze(program,
                new() { CancellationToken = cancellationToken });
            if (!provenance.IsSuccessful) return new([], [], provenance.Diagnostics);
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
        private readonly Dictionary<ClrLirType, (string Name, SafeCoreMirSource Source)> _boundsFailures = [];
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

            foreach ((ClrLirType type, (string name, SafeCoreMirSource source)) in _boundsFailures)
            {
                Step();
                if (methods.Count >= 128) Limit("Bounds helpers exceed the CLR method budget.");
                methods.Add(new(name, type, [], [], [new("entry", [new ClrLirThrowIndexOutOfRange()])])
                    { IsPublic = false, IsCompilerGenerated = true });
                spans.Add(source.Span);
            }

            return new(methods.AsReadOnly(), spans.AsReadOnly(), [])
            {
                ValueTypes = [.. _layouts.Values.OrderBy(static value => value.Name, StringComparer.Ordinal)],
            };
        }

        internal ClrLirCallSite BoundsFailure(ClrLirType element, bool mutable, SafeCoreMirSource source)
        {
            Step();
            ClrLirType result = ClrLirType.ByReference(element, mutable);
            if (!_boundsFailures.TryGetValue(result, out var helper))
            {
                helper = ("bounds_failure_" + _boundsFailures.Count.ToString(CultureInfo.InvariantCulture), source);
                _boundsFailures.Add(result, helper);
            }
            return new(helper.Name, result, []);
        }

        private void ValidateLocals(SafeCoreMirFunction function)
        {
            if (function.Locals.Count > ClrLirLimits.MaximumLocals)
                Limit("A MIR function exceeds the CLR local limit.");
            if (function.ReturnType.Kind == SafeCoreSemanticTypeKind.Reference &&
                function.ReturnType.ElementType?.Kind == SafeCoreSemanticTypeKind.Slice)
                FailUnsupported(function.Source, "Reference-to-slice returns require a general slice ABI.");
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
                    if (local.Type.Kind == SafeCoreSemanticTypeKind.Reference && local.Type.ElementType?.Kind == SafeCoreSemanticTypeKind.Slice)
                        FailUnsupported(local.Source, "Reference-to-slice parameters require a general slice ABI.");
                    if (parametersEnded) Fail(function, "MIR parameters must precede user locals.");
                    parameters++;
                }
                else
                {
                    parametersEnded = true;
                }
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
                SafeCoreSemanticTypeKind.Reference when type.ElementType?.Kind == SafeCoreSemanticTypeKind.Slice =>
                    // BodyLowerer resolves local full-array slice layouts into
                    // managed pointers; this placeholder never reaches emitted ABI.
                    ClrLirType.Any,
                SafeCoreSemanticTypeKind.Reference when type.ElementType?.Kind == SafeCoreSemanticTypeKind.Reference =>
                    FailReferenceType(source),
                SafeCoreSemanticTypeKind.Reference => ClrLirType.ByReference(StorageType(type.ElementType!, source, depth + 1), type.IsMutable),
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
                            FieldStorageType(type.Elements[index], source, depth + 1)));
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
                            FieldStorageType(type.ElementType, source, depth + 1)));
                }
                else if (type.Kind == SafeCoreSemanticTypeKind.Adt)
                {
                    SafeCoreMirAdtLayout? declaration = program.AdtLayouts.FirstOrDefault(item => item.Type == type);
                    if (declaration is null && !HasUnitEvidence(type))
                        FailUnsupported(source, "A named ADT requires its closed MIR declaration layout.");
                    if (declaration?.Fields.Count > ClrLirLimits.MaximumFields)
                        Limit("A MIR ADT exceeds the CLR field limit.");
                    foreach (SafeCoreMirAdtField field in declaration?.Fields ?? [])
                    {
                        Step();
                        fields.Add(new(field.Name, FieldStorageType(field.Type, field.Source, depth + 1)));
                    }
                }
                else if (type.Kind is not (SafeCoreSemanticTypeKind.Unit or SafeCoreSemanticTypeKind.Adt))
                {
                    Fail(source, "Only tuple, array and unit layouts are supported by MIR CLR lowering.");
                }

                if (fields.Any(static field => field.Type.Kind == ClrLirTypeKind.ByReference))
                    FailUnsupported(source, "An aggregate containing references requires a CLR byref-like layout and is outside this executable profile.");
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

        private ClrLirType FieldStorageType(SafeCoreType type, SafeCoreMirSource source, int depth)
        {
            if (type.Kind == SafeCoreSemanticTypeKind.Reference)
                FailUnsupported(source, "An aggregate containing references requires a CLR byref-like layout and is outside this executable profile.");
            return StorageType(type, source, depth);
        }

        private bool HasUnitEvidence(SafeCoreType type)
        {
            bool found = false;
            foreach (SafeCoreMirFunction function in program.Functions)
            foreach (SafeCoreMirLocal local in function.Locals)
            {
                Step();
                if (local.Type != type) continue;
                if (!local.IsUnitAdt) return false;
                found = true;
            }
            return found;
        }

        internal SafeCoreType FieldType(SafeCoreType type, int index, SafeCoreMirSource source) => type.Kind switch
        {
            SafeCoreSemanticTypeKind.Tuple => type.Elements[index],
            SafeCoreSemanticTypeKind.Array => type.ElementType!,
            SafeCoreSemanticTypeKind.Adt => program.AdtLayouts.First(item => item.Type == type).Fields[index].Type,
            _ => throw new LoweringFailure(new(Invalid, "Invalid MIR aggregate projection.", source.Span)),
        };

        private static ClrLirType FailReferenceType(SafeCoreMirSource source)
        {
            FailUnsupported(source, "Nested references require a representation beyond CLR managed pointers.");
            return default;
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

    private sealed partial class BodyLowerer(Lowerer owner, SafeCoreMirFunction function)
    {
        private readonly List<ClrLirLocal> _locals = [];
        private readonly List<Block> _blocks = [];
        private readonly Dictionary<int, string> _labels = [];
        private readonly Dictionary<int, ClrLirType> _localTypes = [];
        private readonly Dictionary<int, SafeCoreType> _sliceArrays = [];
        private Block? _current;
        private int _extraLabel;

        private sealed class Block(string label)
        {
            public string Label { get; } = label;
            public List<ClrLirInstruction> Instructions { get; } = [];
        }

        public ClrLirMethod Run()
        {
            CollectSliceLayouts();
            foreach (SafeCoreMirBlock block in ReachableBlocks())
            {
                owner.Step();
                _labels[block.Id] = "bb" + block.Id.ToString(CultureInfo.InvariantCulture);
            }

            foreach (SafeCoreMirLocal local in function.Locals)
            {
                owner.Step();
                ClrLirType type = local.Type.Kind == SafeCoreSemanticTypeKind.Reference &&
                    local.Type.ElementType?.Kind == SafeCoreSemanticTypeKind.Slice
                    ? ClrLirType.ByReference(owner.StorageType(SliceArray(local.Id, local.Source), local.Source), local.Type.IsMutable)
                    : owner.StorageType(local.Type, local.Source);
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
                    EmitStatement(statement);
                }
                EmitTerminator(mirBlock.Terminator, mirBlock.Source);
            }

            ClrLirType returnType = owner.ReturnType(function.ReturnType, function.Source);
            ClrLirCallSite[] exceptionCleanup = BuildExceptionCleanup();
            int faultTryBlockCount = _blocks.Count;
            if (exceptionCleanup.Length != 0)
                RewriteReturnsForFault(returnType, out faultTryBlockCount);
            return new(owner.MethodName(function.Id, function.Source), returnType, parameters, _locals,
                _blocks.Select(static block => new ClrLirBlock(block.Label, block.Instructions)),
                exceptionCleanup, faultTryBlockCount)
            {
                SourceQualifiedName = function.Name,
                // The CLR entry point must remain externally discoverable even
                // when Rust's source declaration uses its default private visibility.
                IsPublic = function.IsPublic || IsEntryName(function.Name),
            };
        }

        private void RewriteReturnsForFault(ClrLirType returnType, out int protectedBlockCount)
        {
            protectedBlockCount = _blocks.Count;
            int returnLocal = -1;
            if (returnType != ClrLirType.Void)
            {
                returnLocal = _locals.Count;
                _locals.Add(new ClrLirLocal("__rustsharp_return", returnType));
            }

            string epilogueLabel = "fault_epilogue";
            foreach (Block block in _blocks)
            {
                for (int index = 0; index < block.Instructions.Count; index++)
                {
                    if (block.Instructions[index] is not ClrLirReturn)
                        continue;
                    if (returnLocal >= 0)
                    {
                        block.Instructions.Insert(index, new ClrLirStoreLocal(returnLocal));
                        index++;
                    }

                    block.Instructions[index] = new ClrLirLeave(epilogueLabel);
                }
            }

            var epilogue = new Block(epilogueLabel);
            if (returnLocal >= 0)
            {
                epilogue.Instructions.Add(new ClrLirLoadLocal(returnLocal));
            }

            epilogue.Instructions.Add(new ClrLirReturn());
            _blocks.Add(epilogue);
        }

        private ClrLirCallSite[] BuildExceptionCleanup()
        {
            // The MIR cleanup calls are emitted in lexical reverse-drop order.
            // Keep one call per local and order by local ID so a fault raised
            // before the normal return observes the same deterministic reverse
            // declaration order.  The generated fault handler is deliberately
            // bounded and only exists for functions that own a Drop value.
            var calls = new List<(int LocalId, ClrLirCallSite Site)>();
            foreach (SafeCoreMirBlock block in function.Blocks)
            {
                owner.Step();
                SafeCoreMirTerminator terminator = block.Terminator;
                if (terminator.DropLocalId is not int localId ||
                    terminator.Operand is null || terminator.Operand.Kind != SafeCoreMirOperandKind.Function)
                    continue;
                ClrLirType returnType = owner.ReturnType(terminator.Operand.Type.ReturnType, terminator.Source);
                ClrLirType[] parameters = terminator.Operand.Type.ParameterTypes
                    .Select(type => owner.StorageType(type, terminator.Source)).ToArray();
                string name = owner.MethodName(terminator.Operand.Id, terminator.Source);
                if (calls.Any(item => item.LocalId == localId)) continue;
                calls.Add((localId, new ClrLirCallSite(name, returnType, parameters)));
            }

            calls.Sort(static (left, right) => right.LocalId.CompareTo(left.LocalId));
            return calls.Select(static item => item.Site).ToArray();
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
            if (visited.Contains(function.EntryBlockId) && byId.TryGetValue(function.EntryBlockId, out SafeCoreMirBlock? entry))
                result.Add(entry);
            foreach (SafeCoreMirBlock block in function.Blocks)
            {
                owner.Step();
                if (block.Id != function.EntryBlockId && visited.Contains(block.Id)) result.Add(block);
            }
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
                    bool sliceCoercion = value.Kind == SafeCoreMirRvalueKind.Coerce &&
                        value.Type.Kind == SafeCoreSemanticTypeKind.Reference &&
                        value.Type.ElementType?.Kind == SafeCoreSemanticTypeKind.Slice;
                    bool sharedCoercion = sourceType.Kind == ClrLirTypeKind.ByReference &&
                        targetType.Kind == ClrLirTypeKind.ByReference && sourceType.Name == targetType.Name && !targetType.IsMutable;
                    if (!sliceCoercion && !sharedCoercion && sourceType != targetType)
                        Lowerer.FailUnsupported(value.Source,
                            "MIR CLR lowering supports casts and coercions only when source and target share a CLR representation.");
                    if (sliceCoercion && value.Operands[0].Type.Kind == SafeCoreSemanticTypeKind.Array)
                        EmitOperandAddress(value.Operands[0], value.Type.IsMutable);
                    else EmitOperand(value.Operands[0]);
                    if (sharedCoercion && targetType.TryGetByReferenceElement(out ClrLirType referent))
                        Emit(new ClrLirReadOnlyReference(referent));
                    return;
                case SafeCoreMirRvalueKind.Tuple:
                case SafeCoreMirRvalueKind.Array:
                case SafeCoreMirRvalueKind.Adt:
                    foreach (SafeCoreMirOperand operand in value.Operands) EmitOperand(operand);
                    Emit(new ClrLirConstructValue(owner.Layout(value.Type, value.Source)));
                    return;
                case SafeCoreMirRvalueKind.Index:
                    EmitIndex(value);
                    return;
                case SafeCoreMirRvalueKind.SliceLength:
                    EmitSliceLength(value);
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
                case SafeCoreMirOperandKind.Place:
                    if (operand.Place!.IsRoot)
                    {
                        Emit(new ClrLirLoadLocal(operand.Id));
                        return;
                    }
                    EmitPlaceAddress(operand.Place!, operand.Source, mutable: false);
                    Emit(new ClrLirLoadIndirect(owner.StorageType(operand.Type, operand.Source)));
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
                EmitOperand(reference);
                Emit(new ClrLirLoadIndirect(owner.StorageType(value.Type, value.Source)));
                return;
            }
            if (value.Operator is "&" or "&mut" or "reborrow" or "reborrow_mut")
            {
                if (value.Operator is "reborrow" or "reborrow_mut")
                {
                    EmitOperand(value.Operands[0]);
                    if (!value.Type.IsMutable)
                    {
                        SafeCoreType referent = value.Type.ElementType!;
                        if (referent.Kind == SafeCoreSemanticTypeKind.Slice)
                            referent = SliceArray(value.Operands[0].Id, value.Source);
                        Emit(new ClrLirReadOnlyReference(owner.StorageType(referent, value.Source)));
                    }
                }
                else EmitOperandAddress(value.Operands[0], value.Type.IsMutable);
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
            SafeCoreType indexed = array.Type;
            if (indexed.Kind == SafeCoreSemanticTypeKind.Reference)
            {
                indexed = indexed.ElementType!;
                if (indexed.Kind == SafeCoreSemanticTypeKind.Slice) indexed = SliceArray(array.Id, array.Source);
                EmitOperand(array);
                Emit(new ClrLirReadOnlyReference(owner.StorageType(indexed, array.Source)));
            }
            else EmitOperandAddress(array, mutable: false);
            if (indexed.Kind != SafeCoreSemanticTypeKind.Array)
                Lowerer.FailUnsupported(value.Source, "MIR index execution requires a fixed-array or full-array slice layout.");
            ClrLirValueType layout = owner.Layout(indexed, array.Source);
            if (index.Kind == SafeCoreMirOperandKind.Constant)
            {
                if (!int.TryParse(index.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int constant) ||
                    (uint)constant >= (uint)layout.Fields.Length)
                    Lowerer.Fail(value.Source, "MIR array/slice index is outside its validated bounds.");
                Emit(new ClrLirFieldAddress(layout, constant));
            }
            else
            {
                EmitOperand(index);
                EmitArrayElementAddress(layout, owner.StorageType(value.Type, value.Source), mutable: false, value.Source);
            }
            Emit(new ClrLirLoadIndirect(owner.StorageType(value.Type, value.Source)));
        }
        private void EmitSliceLength(SafeCoreMirRvalue value)
        {
            if (value.Operands.Count != 1 || value.Type.Kind != SafeCoreSemanticTypeKind.Usize)
                Lowerer.Fail(value.Source, "MIR slice length rvalue has an invalid signature.");
            SafeCoreType target = value.Operands[0].Type;
            if (target.Kind == SafeCoreSemanticTypeKind.Reference)
            {
                if (target.ElementType?.Kind is not (SafeCoreSemanticTypeKind.Array or SafeCoreSemanticTypeKind.Slice))
                    Lowerer.Fail(value.Source, "MIR slice length requires array/slice reference provenance.");
                target = target.ElementType!.Kind == SafeCoreSemanticTypeKind.Slice ? SliceArray(value.Operands[0].Id, value.Source) : target.ElementType!;
            }
            if (target.Kind != SafeCoreSemanticTypeKind.Array || target.Length is null)
                Lowerer.FailUnsupported(value.Source, "MIR slice length requires a bounded full-array owner.");
            long length = target.Length.Value;
            if (length < 0 || length > int.MaxValue)
                Lowerer.FailUnsupported(value.Source, "MIR slice length requires a bounded full-array owner.");
            Emit(new ClrLirLoadInt32((int)length));
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
            owner.Step();
            if (_blocks.Count >= ClrLirLimits.MaximumBlocks) Lowerer.Limit("MIR projection execution exceeds the CLR block budget.");
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
            if (_current.Instructions.Count >= ClrLirLimits.MaximumInstructionsPerBlock)
                Lowerer.Limit("MIR execution exceeds the CLR instruction block budget.");
            _current.Instructions.Add(instruction);
        }
    }
}
