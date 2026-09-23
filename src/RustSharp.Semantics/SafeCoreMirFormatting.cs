using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RustSharp.Semantics;

public sealed record SafeCoreMirFormattingOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public CancellationToken CancellationToken { get; init; }
    public int MaximumOperations { get; init; } = 1_000_000;
    public int MaximumCharacters { get; init; } = 4_000_000;
}

/// <summary>Versioned deterministic text with explicit IDs, types and original source evidence.
/// Inputs must first pass <see cref="SafeCoreMirValidation.Validate"/>; formatting does not
/// repeat structural validation or certify validity of manually constructed programs.</summary>
public static class SafeCoreMirFormatting
{
    public static string Format(SafeCoreMirProgram program, SafeCoreMirFormattingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        options ??= new();
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1) ||
            options.MaximumOperations is < 1 or > 1_000_000 ||
            options.MaximumCharacters is < 1 or > 4_000_000)
            throw new ArgumentOutOfRangeException(nameof(options));
        return new Formatter(options).Format(program);
    }

    private sealed class Formatter(SafeCoreMirFormattingOptions options)
    {
        private readonly StringBuilder _text = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _operations;

        public string Format(SafeCoreMirProgram program)
        {
            Add("safe-core-mir-v1\n");
            for (int index = 0; index < program.Functions.Count; index++)
            {
                Step();
                SafeCoreMirFunction function = program.Functions[index];
                Add(FormattableString.Invariant($"fn @{function.Id} {Escape(function.Name)} -> {function.ReturnType} entry bb{function.EntryBlockId} "));
                Source(function.Source);
                Add(" {\n");
                for (int localIndex = 0; localIndex < function.Locals.Count; localIndex++)
                {
                    Step();
                    SafeCoreMirLocal local = function.Locals[localIndex];
                    Add(FormattableString.Invariant($"  let %{local.Id} {local.Kind.ToString().ToLowerInvariant()}{(local.IsMutable ? " mut" : "")} {Escape(local.Name)}: {local.Type} "));
                    if (local.IsUnitAdt) Add("unit_adt ");
                    if (local.DestructorFunctionId is int destructor)
                        Add(FormattableString.Invariant($"drop=@{destructor} "));
                    Source(local.Source);
                    Add("\n");
                }
                for (int blockIndex = 0; blockIndex < function.Blocks.Count; blockIndex++)
                {
                    Step();
                    SafeCoreMirBlock block = function.Blocks[blockIndex];
                    Add(FormattableString.Invariant($"  bb{block.Id} "));
                    Source(block.Source);
                    Add(":\n");
                    for (int statementIndex = 0; statementIndex < block.Statements.Count; statementIndex++)
                    {
                        Step();
                        SafeCoreMirStatement statement = block.Statements[statementIndex];
                        SafeCoreMirRvalue value = statement.Value;
                        Add(FormattableString.Invariant($"    %{statement.DestinationLocalId} = {value.Kind.ToString().ToLowerInvariant()}"));
                        if (value.Operator is not null) Add($" {Escape(value.Operator)}");
                        Add("(");
                        Operands(value.Operands);
                        Add($"): {value.Type} ");
                        Source(statement.Source);
                        Add("\n");
                    }
                    Terminator(block.Terminator);
                }
                Add("}\n");
            }
            return _text.ToString();
        }

        private void Terminator(SafeCoreMirTerminator terminator)
        {
            Step();
            Add($"    {terminator.Kind.ToString().ToLowerInvariant()}");
            if (terminator.Operand is not null)
            {
                Add(" ");
                Operand(terminator.Operand);
            }
            if (terminator.Kind == SafeCoreMirTerminatorKind.Call)
            {
                Add("(");
                Operands(terminator.Arguments);
                Add(")");
                if (terminator.DestinationLocalId is int destination) Add(FormattableString.Invariant($" -> %{destination}"));
            }
            if (terminator.DropLocalId is int dropped)
                Add(FormattableString.Invariant($" drop=%{dropped}"));
            if (terminator.TargetBlockId >= 0) Add(FormattableString.Invariant($" bb{terminator.TargetBlockId}"));
            if (terminator.FalseTargetBlockId >= 0) Add(FormattableString.Invariant($" else bb{terminator.FalseTargetBlockId}"));
            Add(" ");
            Source(terminator.Source);
            Add("\n");
        }

        private void Operands(IReadOnlyList<SafeCoreMirOperand> operands)
        {
            for (int index = 0; index < operands.Count; index++)
            {
                Step();
                if (index != 0) Add(", ");
                Operand(operands[index]);
            }
        }

        private void Operand(SafeCoreMirOperand operand)
        {
            Step();
            Add(operand.Kind switch
            {
                SafeCoreMirOperandKind.Local => "%" + operand.Id.ToString(CultureInfo.InvariantCulture),
                SafeCoreMirOperandKind.Function => "@" + operand.Id.ToString(CultureInfo.InvariantCulture),
                SafeCoreMirOperandKind.Place => "place " + (operand.Place?.ToString() ?? "<invalid>"),
                _ => "const " + Escape(operand.Value ?? ""),
            });
            Add($":{operand.Type}");
        }

        private void Source(SafeCoreMirSource source) => Add(FormattableString.Invariant(
            $"[{Escape(source.SourcePath)}:{source.Span.Start}+{source.Span.Length}/{source.SourceLength} hir#{source.HirNodeId}]"));

        private string Escape(string value)
        {
            Step();
            if (value.Length > 262_144) throw new SafeCoreMirLimitException("MIR formatting input text limit reached.");
            return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal)
                .Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
        }

        private void Add(string value)
        {
            Step();
            if ((long)_text.Length + value.Length > options.MaximumCharacters)
                throw new SafeCoreMirLimitException("MIR formatting character limit reached.");
            _text.Append(value);
        }

        private void Step()
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (++_operations > options.MaximumOperations || _clock.Elapsed >= options.Timeout)
                throw new SafeCoreMirLimitException("MIR formatting work or time limit reached.");
        }
    }
}
