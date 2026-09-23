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
        Dictionary<int, IReadOnlyList<int>> reachableBlocks, bool isTruncated, int operations = 0)
    {
        Diagnostics = diagnostics.AsReadOnly();
        ReachableBlocks = new ReadOnlyDictionary<int, IReadOnlyList<int>>(reachableBlocks);
        IsTruncated = isTruncated;
        Operations = Math.Max(0, operations);
    }
    public IReadOnlyList<SafeCoreMirDiagnostic> Diagnostics { get; }
    public IReadOnlyDictionary<int, IReadOnlyList<int>> ReachableBlocks { get; }
    public bool IsTruncated { get; }
    /// <summary>
    /// Number of bounded validator steps consumed before this result was
    /// published.  Callers that compose validation with another bounded pass
    /// can subtract this value from their shared operation budget.
    /// </summary>
    public int Operations { get; }

    /// <summary>Compatibility alias for callers that use an explicit name.</summary>
    public int OperationsUsed => Operations;
    public bool IsSuccessful => !IsTruncated && Diagnostics.Count == 0;
}

/// <summary>Checks structural, source and type invariants, including unreachable blocks.
/// CFG cycles are legal. This is not ownership or definite-initialization analysis.</summary>
public static class SafeCoreMirValidation
{
    private const long MaximumArrayElements = 100_000;

    public static SafeCoreMirValidationResult Validate(SafeCoreMirProgram program,
        SafeCoreMirValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        options ??= new();
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1) ||
            options.MaximumOperations is < 1 or > 4_000_000 ||
            options.MaximumFunctions is < 1 or > 4_096 ||
            options.MaximumLocals is < 1 or > 100_000 ||
            options.MaximumBlocks is < 1 or > 100_000 ||
            options.MaximumStatements is < 1 or > 100_000 ||
            options.MaximumDiagnostics is < 1 or > 4_096 ||
            options.MaximumTypeDepth is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(options));
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
                return new(_diagnostics, _reachable, false, _operations);
            }
            catch (SafeCoreMirLimitException exception)
            {
                // Keep the published diagnostic list inside the caller's bound even when
                // the bound is reached while reporting another diagnostic. Replacing the
                // final slot preserves a deterministic limit marker without leaking one
                // extra item past MaximumDiagnostics.
                int maximumDiagnostics = options.MaximumDiagnostics;
                var limit = new SafeCoreMirDiagnostic(SafeCoreMirDiagnosticCodes.LimitReached,
                    exception.Message, _block?.Source ?? _function?.Source,
                    _function?.Id ?? -1, _block?.Id ?? -1);
                if (_diagnostics.Count >= maximumDiagnostics)
                    _diagnostics[maximumDiagnostics - 1] = limit;
                else
                    _diagnostics.Add(limit);
                return new(_diagnostics, _reachable, true, _operations);
            }
        }

        private void Step()
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (++_operations > options.MaximumOperations || _clock.Elapsed >= options.Timeout)
                throw new SafeCoreMirLimitException("MIR validation work or time limit reached.");
        }

        private static void Limit(int count, int requested, int ceiling)
        {
            if (count > Math.Clamp(requested, 1, ceiling)) throw new SafeCoreMirLimitException("MIR validation size limit reached.");
        }

        private void Error(string code, string message, SafeCoreMirSource? source)
        {
            Step();
            if (_diagnostics.Count >= options.MaximumDiagnostics)
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
            if (depth > Math.Clamp(options.MaximumTypeDepth, 1, 128)) throw new SafeCoreMirLimitException("MIR type nesting limit reached.");
            if (type is null || type.Kind is SafeCoreSemanticTypeKind.Inference or SafeCoreSemanticTypeKind.Error)
            {
                Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "MIR requires a fully resolved non-error type.", _block?.Source ?? _function?.Source);
                return false;
            }
            bool valid = type.Kind != SafeCoreSemanticTypeKind.Array ||
                type.Length is long length && length >= 0 && length <= MaximumArrayElements;
            if (!valid)
                Error(SafeCoreMirDiagnosticCodes.InvalidInput,
                    "MIR fixed-array lengths must be nonnegative and bounded by the aggregate arena limit.",
                    _block?.Source ?? _function?.Source);
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
                if (local.IsUnitAdt && local.Type?.Kind != SafeCoreSemanticTypeKind.Adt)
                    Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "Unit ADT evidence requires a nominal ADT type.", local.Source);
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
                case SafeCoreMirOperandKind.Place:
                    if (operand.Value is not null || operand.Place is null || operand.Place.LocalId != operand.Id ||
                        operand.Place.LocalId < 0 || operand.Place.LocalId >= _function!.Locals.Count)
                        Error(SafeCoreMirDiagnosticCodes.InvalidOperand, "A MIR place operand must reference a local and a valid projection chain.", operand.Source);
                    else if (operand.Place.Projections.Count > 128)
                        Error(SafeCoreMirDiagnosticCodes.InvalidInput, "MIR place projection depth exceeds its bound.", operand.Source);
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
            // Unit structs retain their nominal ADT identity in MIR while
            // carrying the same zero-field runtime representation as `()`.
            if (type.Kind == SafeCoreSemanticTypeKind.Adt) return text == "()";
            if (type.Kind == SafeCoreSemanticTypeKind.Bool) return text is "true" or "false";
            if (type.IsFloat)
                return text == text.Trim() && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
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
                SafeCoreMirRvalueKind.Use or SafeCoreMirRvalueKind.Unary or SafeCoreMirRvalueKind.Coerce or SafeCoreMirRvalueKind.Cast or SafeCoreMirRvalueKind.Field => 1,
                SafeCoreMirRvalueKind.Binary or SafeCoreMirRvalueKind.Index => 2,
                SafeCoreMirRvalueKind.Write => 2,
                SafeCoreMirRvalueKind.Tuple => value.Type.Kind == SafeCoreSemanticTypeKind.Unit ? 0 : value.Type.Elements.Count,
                SafeCoreMirRvalueKind.Array => ArrayOperandCount(value.Type),
                SafeCoreMirRvalueKind.Print => value.Operator == "{}" ? 1 : 0,
                _ => -1,
            };
            if (expectedCount < 0 || value.Operands.Count != expectedCount)
            {
                Error(SafeCoreMirDiagnosticCodes.InvalidOperand, "Rvalue operand count or kind is invalid.", value.Source);
                return;
            }
            if (value.Kind is not (SafeCoreMirRvalueKind.Unary or SafeCoreMirRvalueKind.Binary or SafeCoreMirRvalueKind.Print or SafeCoreMirRvalueKind.Field) && value.Operator is not null)
                Error(SafeCoreMirDiagnosticCodes.InvalidOperand, "Only unary and binary computations carry an operator.", value.Source);
            if (value.Operator is { Length: > 128 })
                Error(SafeCoreMirDiagnosticCodes.InvalidOperand, "MIR operators must be bounded.", value.Source);
            SafeCoreType? first = value.Operands.Count > 0 ? value.Operands[0].Type : null;
            bool valid = value.Kind switch
            {
                SafeCoreMirRvalueKind.Use => first == value.Type,
                SafeCoreMirRvalueKind.Unary => Unary(value.Operator, first!, value.Type),
                SafeCoreMirRvalueKind.Binary => Binary(value.Operator, first!, value.Operands[1].Type, value.Type),
                SafeCoreMirRvalueKind.Coerce => Coercion(first!, value.Type),
                SafeCoreMirRvalueKind.Cast => Cast(first!, value.Type),
                SafeCoreMirRvalueKind.Tuple => Tuple(value),
                SafeCoreMirRvalueKind.Array => Array(value),
                SafeCoreMirRvalueKind.Index => Index(value),
                SafeCoreMirRvalueKind.Field => Field(value),
                SafeCoreMirRvalueKind.Write => Write(value),
                SafeCoreMirRvalueKind.Print => Print(value),
                _ => false,
            };
            if (!valid) Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "Rvalue operator and operand/result types are incompatible.", value.Source);
        }

        private static bool Field(SafeCoreMirRvalue value) =>
            value.Operands[0].Type.Kind == SafeCoreSemanticTypeKind.Tuple &&
            int.TryParse(value.Operator, NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
            value.Operator == index.ToString(CultureInfo.InvariantCulture) &&
            index >= 0 && index < value.Operands[0].Type.Elements.Count &&
            value.Type == value.Operands[0].Type.Elements[index];

        private static bool Unary(string? op, SafeCoreType operand, SafeCoreType result) => op switch
        {
            "-" => result == operand && (operand.IsFloat || operand.Kind is >= SafeCoreSemanticTypeKind.I8 and <= SafeCoreSemanticTypeKind.Isize),
            "!" => result == operand && (operand.IsInteger || operand.Kind == SafeCoreSemanticTypeKind.Bool),
            "&" or "&mut" => result.Kind == SafeCoreSemanticTypeKind.Reference && result.ElementType == operand &&
                result.IsMutable == (op == "&mut"),
            "reborrow" or "reborrow_mut" => result.Kind == SafeCoreSemanticTypeKind.Reference &&
                operand.Kind == SafeCoreSemanticTypeKind.Reference && result.ElementType == operand.ElementType &&
                result.IsMutable == (op == "reborrow_mut"),
            "*" => operand.Kind == SafeCoreSemanticTypeKind.Reference && result == operand.ElementType,
            _ => false,
        };

        private static bool Write(SafeCoreMirRvalue value) =>
            value.Operands.Count == 2 && value.Operands[0].Type.Kind == SafeCoreSemanticTypeKind.Reference &&
            value.Operands[1].Type == value.Type && value.Operands[0].Type.ElementType == value.Type;

        private static bool Binary(string? op, SafeCoreType left, SafeCoreType right, SafeCoreType result) => op switch
        {
            "+" or "-" or "*" or "/" or "%" => left == right && result == left && (left.IsInteger || left.IsFloat),
            "&" or "|" or "^" => left == right && result == left && (left.IsInteger || left.Kind == SafeCoreSemanticTypeKind.Bool),
            "<<" or ">>" => left.IsInteger && right.IsInteger && result == left,
            // Logical operators remain outside the v1 typed-MIR rvalue contract;
            // short-circuit lowering uses explicit CFG branches. Versioned
            // extensions must preserve that rejection boundary.
            "==" or "!=" or "<" or "<=" or ">" or ">=" => left == right && result.Kind == SafeCoreSemanticTypeKind.Bool
                && (left.IsInteger || left.IsFloat || left.Kind is SafeCoreSemanticTypeKind.Bool or SafeCoreSemanticTypeKind.Char),
            _ => false,
        };

        private bool Coercion(SafeCoreType from, SafeCoreType to)
        {
            Step();
            TimeSpan remaining = options.Timeout - _clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new SafeCoreMirLimitException("MIR validation work or time limit reached.");
            var inference = new SafeCoreTypeInference(new()
            {
                MaximumOperations = Math.Max(1, Math.Clamp(options.MaximumOperations, 1, 4_000_000) - _operations),
                MaximumNestingDepth = Math.Clamp(options.MaximumTypeDepth, 1, 128),
                // Coercion is a nested analysis. Give it only the time left in this
                // validation pass so a large aggregate cannot reset the wall-clock
                // budget for every element.
                Timeout = remaining,
                CancellationToken = options.CancellationToken,
            });
            bool result;
            try
            {
                result = inference.Coerce(from, to);
            }
            catch (SafeCoreTypeInferenceLimitException)
            {
                throw new SafeCoreMirLimitException("MIR coercion work limit reached.");
            }
            finally
            {
                // Nested inference has its own counter.  Charge the consumed
                // steps back to this validator before the next outer Step so
                // composed MIR/ownership budgets remain global.
                _operations = (int)Math.Min(int.MaxValue,
                    (long)_operations + inference.OperationsUsed);
            }

            return result;
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

        private static int ArrayOperandCount(SafeCoreType type) =>
            type.Kind == SafeCoreSemanticTypeKind.Array && type.Length is long length &&
            length is >= 0 and <= int.MaxValue ? (int)length : -1;

        private bool Array(SafeCoreMirRvalue value)
        {
            if (value.Type.Kind != SafeCoreSemanticTypeKind.Array || value.Type.Length is not long length ||
                length < 0 || length > MaximumArrayElements || value.Operands.Count != length)
                return false;
            for (int index = 0; index < value.Operands.Count; index++)
            {
                Step();
                if (value.Operands[index].Type != value.Type.ElementType) return false;
            }
            return true;
        }

        private static bool Index(SafeCoreMirRvalue value)
        {
            if (value.Type.Kind is SafeCoreSemanticTypeKind.Inference or SafeCoreSemanticTypeKind.Error ||
                value.Operands.Count != 2)
                return false;
            SafeCoreType array = value.Operands[0].Type;
            SafeCoreType index = value.Operands[1].Type;
            if (array.Kind != SafeCoreSemanticTypeKind.Array || index.Kind != SafeCoreSemanticTypeKind.Usize ||
                array.Length is not long length || value.Type != array.ElementType)
                return false;
            if (value.Operands[1].Kind == SafeCoreMirOperandKind.Constant &&
                (!BigInteger.TryParse(value.Operands[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out BigInteger constant) ||
                 constant < 0 || constant >= length))
                return false;
            return true;
        }

        private static bool Print(SafeCoreMirRvalue value)
        {
            if (value.Type.Kind != SafeCoreSemanticTypeKind.Unit || value.Operator is null)
                return false;
            if (value.Operator == "{}")
                return value.Operands.Count == 1 && IsDisplayable(value.Operands[0].Type);
            return value.Operands.Count == 0 &&
                !value.Operator.Contains('{', StringComparison.Ordinal) &&
                !value.Operator.Contains('}', StringComparison.Ordinal);

            static bool IsDisplayable(SafeCoreType type) => type.Kind is
                SafeCoreSemanticTypeKind.Unit or SafeCoreSemanticTypeKind.Bool or
                SafeCoreSemanticTypeKind.Char or SafeCoreSemanticTypeKind.I8 or
                SafeCoreSemanticTypeKind.I16 or SafeCoreSemanticTypeKind.I32 or
                SafeCoreSemanticTypeKind.I64 or SafeCoreSemanticTypeKind.I128 or
                SafeCoreSemanticTypeKind.Isize or SafeCoreSemanticTypeKind.U8 or
                SafeCoreSemanticTypeKind.U16 or SafeCoreSemanticTypeKind.U32 or
                SafeCoreSemanticTypeKind.U64 or SafeCoreSemanticTypeKind.U128 or
                SafeCoreSemanticTypeKind.Usize or SafeCoreSemanticTypeKind.F32 or
                SafeCoreSemanticTypeKind.F64;
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
            if (terminator.Kind != SafeCoreMirTerminatorKind.Call && terminator.DropLocalId is not null)
                Error(SafeCoreMirDiagnosticCodes.InvalidControlFlow, "Only call terminators may carry a Drop local.", terminator.Source);
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
            if (terminator.Operand?.Kind != SafeCoreMirOperandKind.Function)
            {
                Error(SafeCoreMirDiagnosticCodes.UnsupportedNode,
                    "The safe-core MIR profile supports direct calls only; indirect calls are outside this boundary.",
                    terminator.Source);
                return;
            }
            SafeCoreType? callee = terminator.Operand?.Type;
            if (callee?.Kind != SafeCoreSemanticTypeKind.Function)
            {
                Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "A call requires a typed function operand.", terminator.Source);
                return;
            }
            if (terminator.DropLocalId is int droppedLocal)
            {
                SafeCoreMirLocal? dropped = droppedLocal >= 0 && droppedLocal < _function!.Locals.Count
                    ? _function.Locals[droppedLocal] : null;
                if (terminator.Arguments.Count != 0 || terminator.DestinationLocalId is not null ||
                    dropped is null || !dropped.DestructorFunctionId.HasValue ||
                    terminator.Operand is null || terminator.Operand.Id != dropped.DestructorFunctionId.Value ||
                    callee.ReturnType.Kind != SafeCoreSemanticTypeKind.Unit)
                    Error(SafeCoreMirDiagnosticCodes.InvalidControlFlow,
                        "A destructor call must target the local's zero-argument unit Drop function.", terminator.Source);
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
                else if (callee.ReturnType.Kind == SafeCoreSemanticTypeKind.Unit)
                    Error(SafeCoreMirDiagnosticCodes.TypeMismatch, "A unit call cannot initialize a MIR destination.", terminator.Source);
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
            var queued = new bool[function.Blocks.Count];
            var pending = new Queue<int>();
            Enqueue(function.EntryBlockId);
            // Each block is enqueued at most once. The queue therefore remains bounded
            // by the validated block arena; Step() supplies the shared work/time bound.
            while (pending.Count > 0)
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
                if (id >= 0 && id < visited.Length && !visited[id] && !queued[id])
                {
                    queued[id] = true;
                    pending.Enqueue(id);
                }
            }
        }
    }
}
