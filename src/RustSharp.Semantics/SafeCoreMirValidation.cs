using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace RustSharp.Semantics;

public sealed record SafeCoreMirValidationOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public CancellationToken CancellationToken { get; init; }
    public int MaximumOperations { get; init; } = 1_000_000;
    public int MaximumFunctions { get; init; } = 4_096;
    public int MaximumLocals { get; init; } = 100_000;
    public int MaximumBlocks { get; init; } = 100_000;
    public int MaximumStatements { get; init; } = 100_000;
    public int MaximumDiagnostics { get; init; } = 128;
    public int MaximumTypeDepth { get; init; } = 128;
}

public static class SafeCoreMirDiagnosticCodes
{
    public const string InvalidInput = "RSM0001";
    public const string LimitReached = "RSM0002";
    public const string InvalidControlFlow = "RSM1001";
    public const string TypeMismatch = "RSM1002";
    public const string InvalidSource = "RSM1003";
    public const string InvalidOperand = "RSM1004";
    public const string UnsupportedNode = "RSM1005";
}

public sealed record SafeCoreMirDiagnostic(string Code, string Message, SafeCoreMirSource? Source,
    int FunctionId = -1, int BlockId = -1);

public sealed class SafeCoreMirValidationResult
{
    internal SafeCoreMirValidationResult(List<SafeCoreMirDiagnostic> diagnostics,
        Dictionary<int, IReadOnlyList<int>> reachableBlocks, bool isTruncated)
    {
        Diagnostics = diagnostics.AsReadOnly();
        ReachableBlocks = new ReadOnlyDictionary<int, IReadOnlyList<int>>(reachableBlocks);
        IsTruncated = isTruncated;
    }
    public IReadOnlyList<SafeCoreMirDiagnostic> Diagnostics { get; }
    public IReadOnlyDictionary<int, IReadOnlyList<int>> ReachableBlocks { get; }
    public bool IsTruncated { get; }
    public bool IsSuccessful => !IsTruncated && Diagnostics.Count == 0;
}

/// <summary>Checks structural, source and type invariants, including unreachable blocks.
/// CFG cycles are legal. This is not ownership or definite-initialization analysis.</summary>
public static class SafeCoreMirValidation
{
    public static SafeCoreMirValidationResult Validate(SafeCoreMirProgram program,
        SafeCoreMirValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumOperations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumFunctions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumLocals);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumBlocks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumStatements);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumDiagnostics);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumTypeDepth);
        return new Validator(program, options).Run();
    }

    private sealed class Validator(SafeCoreMirProgram program, SafeCoreMirValidationOptions options)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<SafeCoreMirDiagnostic> _diagnostics = [];
        private readonly Dictionary<int, IReadOnlyList<int>> _reachable = [];
        private SafeCoreMirFunction? _function;
        private SafeCoreMirBlock? _block;
        private int _operations;
        private int _locals;
        private int _blocks;
        private int _statements;

        public SafeCoreMirValidationResult Run()
        {
            try
            {
                Step();
                Limit(program.Functions.Count, options.MaximumFunctions, 4_096);
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (int index = 0; index < program.Functions.Count; index++)
                {
                    Step();
                    _function = program.Functions[index];
                    _block = null;
                    Source(_function.Source);
                    if (_function.Id != index || string.IsNullOrEmpty(_function.Name)
                        || _function.Name.Length > 4_096 || !names.Add(_function.Name))
                        Error(SafeCoreMirDiagnosticCodes.InvalidInput, "Function IDs must index the arena and names must be unique and bounded.", _function.Source);
                    if (Type(_function.ReturnType)) Function();
                }
                return new(_diagnostics, _reachable, false);
            }
            catch (SafeCoreMirLimitException exception)
            {
                _diagnostics.Add(new(SafeCoreMirDiagnosticCodes.LimitReached, exception.Message,
                    _block?.Source ?? _function?.Source, _function?.Id ?? -1, _block?.Id ?? -1));
                return new(_diagnostics, _reachable, true);
            }
        }

        private void Step()
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (++_operations > Math.Clamp(options.MaximumOperations, 1, 1_000_000)
                || _clock.Elapsed >= (options.Timeout > TimeSpan.Zero && options.Timeout <= TimeSpan.FromMinutes(1)
                    ? options.Timeout : TimeSpan.FromSeconds(10)))
                throw new SafeCoreMirLimitException("MIR validation work or time limit reached.");
        }

        private static void Limit(int count, int requested, int ceiling)
        {
            if (count > Math.Clamp(requested, 1, ceiling)) throw new SafeCoreMirLimitException("MIR validation size limit reached.");
        }

        private void Error(string code, string message, SafeCoreMirSource? source)
        {
            Step();
            if (_diagnostics.Count >= Math.Clamp(options.MaximumDiagnostics, 1, 1_024))
                throw new SafeCoreMirLimitException("MIR diagnostic limit reached.");
            _diagnostics.Add(new(code, message, source, _function?.Id ?? -1, _block?.Id ?? -1));
        }

        private void Source(SafeCoreMirSource? source)
        {
            Step();
            if (source is null || string.IsNullOrWhiteSpace(source.SourcePath) || source.SourcePath.Length > 4_096
                || source.HirNodeId < 0 || source.SourceLength < 0 || source.Span.Start < 0 || source.Span.Length < 0
                || (long)source.Span.Start + source.Span.Length > source.SourceLength)
                Error(SafeCoreMirDiagnosticCodes.InvalidSource, "MIR source evidence requires a path, HIR ID and a span within its source.", source);
        }

        private bool Type(SafeCoreType? type, int depth = 0)
        {
            Step();
            if (depth >= Math.Clamp(options.MaximumTypeDepth, 1, 128)) throw new SafeCoreMirLimitException("MIR type nesting limit reached.");
            if (type is null || type.Kind is SafeCoreSemanticTypeKind.Inference or SafeCoreSemanticTypeKind.Error)
            {
                Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "MIR requires a fully resolved non-error type.", _block?.Source ?? _function?.Source);
                return false;
            }
            bool valid = true;
            for (int index = 0; index < type.Elements.Count; index++) valid &= Type(type.Elements[index], depth + 1);
            return valid;
        }

        private void Function()
        {
            SafeCoreMirFunction function = _function!;
            _locals += function.Locals.Count;
            _blocks += function.Blocks.Count;
            Limit(_locals, options.MaximumLocals, 100_000);
            Limit(_blocks, options.MaximumBlocks, 100_000);
            bool pastParameters = false;
            for (int index = 0; index < function.Locals.Count; index++)
            {
                Step();
                SafeCoreMirLocal local = function.Locals[index];
                Source(local.Source);
                Type(local.Type);
                if (local.Id != index || string.IsNullOrEmpty(local.Name) || local.Name.Length > 4_096
                    || !Enum.IsDefined(local.Kind))
                    Error(SafeCoreMirDiagnosticCodes.InvalidInput, "Local IDs must index the arena and local metadata must be valid.", local.Source);
                if (local.Type?.Kind == SafeCoreSemanticTypeKind.Never)
                    Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "The never type cannot occupy a local slot.", local.Source);
                if (local.Kind == SafeCoreMirLocalKind.Parameter && pastParameters)
                    Error(SafeCoreMirDiagnosticCodes.InvalidInput, "Parameter slots must precede other locals.", local.Source);
                pastParameters |= local.Kind != SafeCoreMirLocalKind.Parameter;
            }
            Target(function.EntryBlockId, function.Source);
            for (int index = 0; index < function.Blocks.Count; index++)
            {
                Step();
                _block = function.Blocks[index];
                Source(_block.Source);
                if (_block.Id != index) Error(SafeCoreMirDiagnosticCodes.InvalidControlFlow, "Block IDs must index the function's block arena.", _block.Source);
                _statements += _block.Statements.Count;
                Limit(_statements, options.MaximumStatements, 100_000);
                for (int statementIndex = 0; statementIndex < _block.Statements.Count; statementIndex++)
                {
                    Step();
                    SafeCoreMirStatement statement = _block.Statements[statementIndex];
                    Source(statement.Source);
                    SafeCoreType? destination = LocalType(statement.DestinationLocalId, statement.Source);
                    if (statement.Value is null)
                        Error(SafeCoreMirDiagnosticCodes.InvalidInput, "An assignment requires a value.", statement.Source);
                    else
                    {
                        Rvalue(statement.Value);
                        Equal(destination, statement.Value.Type, statement.Source, "Assignment type differs from its destination.");
                    }
                }
                Terminator(_block.Terminator);
            }
            Reachability();
        }

        private SafeCoreType? LocalType(int id, SafeCoreMirSource? source)
        {
            Step();
            if (id < 0 || id >= _function!.Locals.Count)
            {
                Error(SafeCoreMirDiagnosticCodes.InvalidOperand, "Local ID is outside the function's local arena.", source);
                return null;
            }
            return _function.Locals[id].Type;
        }

        private void Equal(SafeCoreType? expected, SafeCoreType? actual, SafeCoreMirSource? source, string message)
        {
            Step();
            if (expected is not null && actual is not null && expected != actual)
                Error(SafeCoreMirDiagnosticCodes.TypeMismatch, message, source);
        }

        private bool Operand(SafeCoreMirOperand operand)
        {
            Step();
            Source(operand.Source);
            if (!Type(operand.Type)) return false;
            switch (operand.Kind)
            {
                case SafeCoreMirOperandKind.Local:
                    Equal(LocalType(operand.Id, operand.Source), operand.Type, operand.Source, "Local operand type differs from its slot.");
                    if (operand.Value is not null) Error(SafeCoreMirDiagnosticCodes.InvalidOperand, "A local operand cannot carry a constant payload.", operand.Source);
                    break;
                case SafeCoreMirOperandKind.Function:
                    if (operand.Id < 0 || operand.Id >= program.Functions.Count)
                        Error(SafeCoreMirDiagnosticCodes.InvalidOperand, "Function operand ID is outside the function arena.", operand.Source);
                    else
                    {
                        SafeCoreMirFunction target = program.Functions[operand.Id];
                        int parameterCount = 0;
                        for (int index = 0; index < target.Locals.Count; index++)
                        {
                            Step();
                            if (target.Locals[index].Kind != SafeCoreMirLocalKind.Parameter) break;
                            parameterCount++;
                        }
                        if (operand.Type.Kind != SafeCoreSemanticTypeKind.Function || operand.Type.Name != target.Name
                            || operand.Type.ParameterTypes.Count != parameterCount || operand.Type.ReturnType != target.ReturnType)
                            Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "Function operand does not match its declaration's nominal signature.", operand.Source);
                        else
                            for (int index = 0; index < parameterCount; index++)
                                Equal(target.Locals[index].Type, operand.Type.ParameterTypes[index], operand.Source, "Function parameter signature mismatch.");
                    }
                    if (operand.Value is not null) Error(SafeCoreMirDiagnosticCodes.InvalidOperand, "A function operand cannot carry a constant payload.", operand.Source);
                    break;
                case SafeCoreMirOperandKind.Constant:
                    if (operand.Id != -1 || !Constant(operand.Type, operand.Value))
                        Error(SafeCoreMirDiagnosticCodes.InvalidOperand, "Constant payload is invalid or out of range for its type.", operand.Source);
                    break;
                default:
                    Error(SafeCoreMirDiagnosticCodes.InvalidOperand, "Unknown operand kind.", operand.Source);
                    break;
            }
            return true;
        }

        private static bool Constant(SafeCoreType type, string? text)
        {
            if (text is null || text.Length > 4_096) return false;
            if (type.Kind == SafeCoreSemanticTypeKind.Unit) return text == "()";
            if (type.Kind == SafeCoreSemanticTypeKind.Bool) return text is "true" or "false";
            if (type.IsFloat)
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    && double.IsFinite(value) && (type.Kind != SafeCoreSemanticTypeKind.F32 || float.IsFinite((float)value));
            if ((!type.IsInteger && type.Kind != SafeCoreSemanticTypeKind.Char)
                || !BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger number)) return false;
            if (type.Kind == SafeCoreSemanticTypeKind.Char) return number >= 0 && number <= 0x10ffff && (number < 0xd800 || number > 0xdfff);
            int bits = type.Kind switch
            {
                SafeCoreSemanticTypeKind.I8 or SafeCoreSemanticTypeKind.U8 => 8,
                SafeCoreSemanticTypeKind.I16 or SafeCoreSemanticTypeKind.U16 => 16,
                SafeCoreSemanticTypeKind.I32 or SafeCoreSemanticTypeKind.U32 => 32,
                SafeCoreSemanticTypeKind.I128 or SafeCoreSemanticTypeKind.U128 => 128,
                _ => 64,
            };
            bool signed = type.Kind is >= SafeCoreSemanticTypeKind.I8 and <= SafeCoreSemanticTypeKind.Isize;
            BigInteger magnitude = BigInteger.One << (signed ? bits - 1 : bits);
            return number >= (signed ? -magnitude : BigInteger.Zero) && number < magnitude;
        }

        private void Rvalue(SafeCoreMirRvalue value)
        {
            Step();
            Source(value.Source);
            if (!Type(value.Type)) return;
            bool validOperands = true;
            for (int index = 0; index < value.Operands.Count; index++) validOperands &= Operand(value.Operands[index]);
            if (!validOperands) return;
            int expectedCount = value.Kind switch
            {
                SafeCoreMirRvalueKind.Use or SafeCoreMirRvalueKind.Unary or SafeCoreMirRvalueKind.Coerce or SafeCoreMirRvalueKind.Cast => 1,
                SafeCoreMirRvalueKind.Binary => 2,
                SafeCoreMirRvalueKind.Tuple => value.Type.Kind == SafeCoreSemanticTypeKind.Unit ? 0 : value.Type.Elements.Count,
                _ => -1,
            };
            if (expectedCount < 0 || value.Operands.Count != expectedCount)
            {
                Error(SafeCoreMirDiagnosticCodes.InvalidOperand, "Rvalue operand count or kind is invalid.", value.Source);
                return;
            }
            if (value.Kind is not (SafeCoreMirRvalueKind.Unary or SafeCoreMirRvalueKind.Binary) && value.Operator is not null)
                Error(SafeCoreMirDiagnosticCodes.InvalidOperand, "Only unary and binary computations carry an operator.", value.Source);
            SafeCoreType? first = value.Operands.Count > 0 ? value.Operands[0].Type : null;
            bool valid = value.Kind switch
            {
                SafeCoreMirRvalueKind.Use => first == value.Type,
                SafeCoreMirRvalueKind.Unary => Unary(value.Operator, first!, value.Type),
                SafeCoreMirRvalueKind.Binary => Binary(value.Operator, first!, value.Operands[1].Type, value.Type),
                SafeCoreMirRvalueKind.Coerce => Coercion(first!, value.Type),
                SafeCoreMirRvalueKind.Cast => Cast(first!, value.Type),
                SafeCoreMirRvalueKind.Tuple => Tuple(value),
                _ => false,
            };
            if (!valid) Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "Rvalue operator and operand/result types are incompatible.", value.Source);
        }

        private static bool Unary(string? op, SafeCoreType operand, SafeCoreType result) => result == operand && op switch
        {
            "-" => operand.IsFloat || operand.Kind is >= SafeCoreSemanticTypeKind.I8 and <= SafeCoreSemanticTypeKind.Isize,
            "!" => operand.IsInteger || operand.Kind == SafeCoreSemanticTypeKind.Bool,
            _ => false,
        };

        private static bool Binary(string? op, SafeCoreType left, SafeCoreType right, SafeCoreType result) => op switch
        {
            "+" or "-" or "*" or "/" or "%" => left == right && result == left && (left.IsInteger || left.IsFloat),
            "&" or "|" or "^" => left == right && result == left && (left.IsInteger || left.Kind == SafeCoreSemanticTypeKind.Bool),
            "<<" or ">>" => left.IsInteger && right.IsInteger && result == left,
            "==" or "!=" or "<" or "<=" or ">" or ">=" => left == right && result.Kind == SafeCoreSemanticTypeKind.Bool
                && (left.IsInteger || left.IsFloat || left.Kind is SafeCoreSemanticTypeKind.Bool or SafeCoreSemanticTypeKind.Char),
            _ => false,
        };

        private bool Coercion(SafeCoreType from, SafeCoreType to)
        {
            Step();
            var inference = new SafeCoreTypeInference(new()
            {
                MaximumOperations = Math.Max(1, Math.Clamp(options.MaximumOperations, 1, 1_000_000) - _operations),
                MaximumNestingDepth = Math.Clamp(options.MaximumTypeDepth, 1, 128),
                Timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : TimeSpan.FromSeconds(10),
                CancellationToken = options.CancellationToken,
            });
            try { return inference.Coerce(from, to); }
            catch (SafeCoreTypeInferenceLimitException) { throw new SafeCoreMirLimitException("MIR coercion work limit reached."); }
        }

        private static bool Cast(SafeCoreType from, SafeCoreType to) =>
            (from.IsInteger || from.IsFloat || from.Kind is SafeCoreSemanticTypeKind.Bool or SafeCoreSemanticTypeKind.Char)
            && (to.IsInteger || to.IsFloat && (from.IsInteger || from.IsFloat)
                || to.Kind == SafeCoreSemanticTypeKind.Char && from.Kind == SafeCoreSemanticTypeKind.U8);

        private bool Tuple(SafeCoreMirRvalue value)
        {
            if (value.Type.Kind == SafeCoreSemanticTypeKind.Unit) return value.Operands.Count == 0;
            if (value.Type.Kind != SafeCoreSemanticTypeKind.Tuple) return false;
            for (int index = 0; index < value.Operands.Count; index++)
            {
                Step();
                if (value.Operands[index].Type != value.Type.Elements[index]) return false;
            }
            return true;
        }

        private bool Target(int id, SafeCoreMirSource source)
        {
            Step();
            if (id >= 0 && id < _function!.Blocks.Count) return true;
            Error(SafeCoreMirDiagnosticCodes.InvalidControlFlow, "Branch target is outside the function's block arena.", source);
            return false;
        }

        private void Terminator(SafeCoreMirTerminator? terminator)
        {
            Step();
            if (terminator is null)
            {
                Error(SafeCoreMirDiagnosticCodes.InvalidControlFlow, "Every block requires an explicit terminator.", _block!.Source);
                return;
            }
            Source(terminator.Source);
            if (terminator.Operand is not null) Operand(terminator.Operand);
            for (int index = 0; index < terminator.Arguments.Count; index++) Operand(terminator.Arguments[index]);
            if (terminator.Kind != SafeCoreMirTerminatorKind.Call && (terminator.Arguments.Count != 0 || terminator.DestinationLocalId is not null))
                Error(SafeCoreMirDiagnosticCodes.InvalidControlFlow, "Only call terminators may carry arguments or a destination.", terminator.Source);
            if (terminator.Kind != SafeCoreMirTerminatorKind.Branch && terminator.FalseTargetBlockId != -1)
                Error(SafeCoreMirDiagnosticCodes.InvalidControlFlow, "Only branch terminators may carry a false target.", terminator.Source);
            switch (terminator.Kind)
            {
                case SafeCoreMirTerminatorKind.Return:
                    if (terminator.TargetBlockId != -1) Error(SafeCoreMirDiagnosticCodes.InvalidControlFlow, "Return cannot have a successor.", terminator.Source);
                    if (_function!.ReturnType.Kind == SafeCoreSemanticTypeKind.Never)
                        Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "A diverging function cannot return.", terminator.Source);
                    else if (terminator.Operand is null)
                    {
                        if (_function.ReturnType.Kind != SafeCoreSemanticTypeKind.Unit)
                            Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "A non-unit return requires a value.", terminator.Source);
                    }
                    else Equal(_function.ReturnType, terminator.Operand.Type, terminator.Source, "Return type differs from the function result.");
                    break;
                case SafeCoreMirTerminatorKind.Goto:
                    if (terminator.Operand is not null) Error(SafeCoreMirDiagnosticCodes.InvalidControlFlow, "Goto cannot carry an operand.", terminator.Source);
                    Target(terminator.TargetBlockId, terminator.Source);
                    break;
                case SafeCoreMirTerminatorKind.Branch:
                    if (terminator.Operand?.Type?.Kind != SafeCoreSemanticTypeKind.Bool)
                        Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "A branch condition must be bool.", terminator.Source);
                    Target(terminator.TargetBlockId, terminator.Source);
                    Target(terminator.FalseTargetBlockId, terminator.Source);
                    break;
                case SafeCoreMirTerminatorKind.Call:
                    Call(terminator);
                    break;
                case SafeCoreMirTerminatorKind.Unreachable:
                    if (terminator.Operand is not null || terminator.TargetBlockId != -1)
                        Error(SafeCoreMirDiagnosticCodes.InvalidControlFlow, "Unreachable cannot carry operands or successors.", terminator.Source);
                    break;
                default:
                    Error(SafeCoreMirDiagnosticCodes.InvalidControlFlow, "Unknown terminator kind.", terminator.Source);
                    break;
            }
        }

        private void Call(SafeCoreMirTerminator terminator)
        {
            SafeCoreType? callee = terminator.Operand?.Type;
            if (callee?.Kind != SafeCoreSemanticTypeKind.Function)
            {
                Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "A call requires a typed function operand.", terminator.Source);
                return;
            }
            if (callee.ParameterTypes.Count != terminator.Arguments.Count)
                Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "Call argument count differs from the function signature.", terminator.Source);
            else
                for (int index = 0; index < terminator.Arguments.Count; index++)
                    Equal(callee.ParameterTypes[index], terminator.Arguments[index].Type, terminator.Arguments[index].Source,
                        "Call argument type differs from the function parameter; coercions must be explicit.");
            if (terminator.DestinationLocalId is int destination)
            {
                Equal(LocalType(destination, terminator.Source), callee.ReturnType, terminator.Source, "Call destination type differs from the function result.");
                if (callee.ReturnType.Kind == SafeCoreSemanticTypeKind.Never)
                    Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "A diverging call cannot initialize a destination.", terminator.Source);
            }
            else if (callee.ReturnType.Kind is not (SafeCoreSemanticTypeKind.Unit or SafeCoreSemanticTypeKind.Never))
                Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "A value-returning call requires a destination, including discarded results.", terminator.Source);
            if (callee.ReturnType.Kind == SafeCoreSemanticTypeKind.Never)
            {
                if (terminator.TargetBlockId != -1 && Target(terminator.TargetBlockId, terminator.Source)
                    && _function!.Blocks[terminator.TargetBlockId].Terminator?.Kind != SafeCoreMirTerminatorKind.Unreachable)
                    Error(SafeCoreMirDiagnosticCodes.InvalidControlFlow, "A diverging call cannot continue to executable control flow.", terminator.Source);
            }
            else Target(terminator.TargetBlockId, terminator.Source);
        }

        private void Reachability()
        {
            SafeCoreMirFunction function = _function!;
            var visited = new bool[function.Blocks.Count];
            var pending = new Queue<int>();
            if (function.EntryBlockId >= 0 && function.EntryBlockId < visited.Length) pending.Enqueue(function.EntryBlockId);
            for (int iterations = 0; pending.Count > 0 && iterations <= function.Blocks.Count * 2; iterations++)
            {
                Step();
                int id = pending.Dequeue();
                if (visited[id]) continue;
                visited[id] = true;
                SafeCoreMirTerminator? terminator = function.Blocks[id].Terminator;
                if (terminator?.Kind is SafeCoreMirTerminatorKind.Goto or SafeCoreMirTerminatorKind.Branch
                    || terminator?.Kind == SafeCoreMirTerminatorKind.Call && terminator.Operand?.Type?.Kind == SafeCoreSemanticTypeKind.Function
                        && terminator.Operand.Type.ReturnType.Kind != SafeCoreSemanticTypeKind.Never)
                {
                    Enqueue(terminator.TargetBlockId);
                    if (terminator.Kind == SafeCoreMirTerminatorKind.Branch) Enqueue(terminator.FalseTargetBlockId);
                }
            }
            var reachable = new List<int>();
            for (int index = 0; index < visited.Length; index++)
            {
                Step();
                if (visited[index]) reachable.Add(index);
            }
            _reachable[function.Id] = reachable.AsReadOnly();
            void Enqueue(int id)
            {
                if (id >= 0 && id < visited.Length && !visited[id]) pending.Enqueue(id);
            }
        }
    }
}
