using System.Diagnostics;
using System.Globalization;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.CodeGen.IL;

public sealed record SafeCoreClrResult(
    IReadOnlyList<ClrLirMethod> Methods,
    IReadOnlyList<TextSpan> MethodSpans,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsSuccessful => Methods.Count != 0 && Diagnostics.Count == 0;
}

/// <summary>Lowers checked primitive HIR to CLR LIR with empty stacks across statement boundaries.</summary>
public static class SafeCoreClrLowering
{
    public static SafeCoreClrResult Lower(SafeCoreTypedProgram program, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        var methods = new List<ClrLirMethod>();
        var spans = new List<TextSpan>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SafeCoreTypedFunction function in program.Functions)
        {
            names.Add(function.Declaration.DeclaredSymbol!.QualifiedName,
                function.Declaration.DeclaredSymbol!.Name == "main" &&
                function.Declaration.DeclaredSymbol.ScopePath == program.Hir.NameResolution!.RootScope!.Path
                    ? "Main" : "fn_" + function.Declaration.Id.ToString(CultureInfo.InvariantCulture));
        }

        var clock = Stopwatch.StartNew();
        foreach (SafeCoreTypedFunction function in program.Functions)
        {
            try
            {
                var lowerer = new Lowerer(program, function, names, clock, cancellationToken);
                ClrLirMethod method = lowerer.Run();
                ClrLirValidationResult validation = method.Validate();
                if (!validation.IsValid)
                    return new([], [], [new("RST2002", "Invalid lowered CLR LIR: " + validation.Diagnostics[0], function.Declaration.Span)]);
                methods.Add(method);
                spans.Add(function.Declaration.Span);
            }
            catch (LoweringLimitException)
            {
                return new([], [], [new("RST2001", "CLR lowering exceeded its local, block, work, nesting or time limit.", function.Declaration.Span)]);
            }
        }

        return new(methods.AsReadOnly(), spans.AsReadOnly(), []);
    }

    private sealed class LoweringLimitException : Exception;

    private sealed class Lowerer(
        SafeCoreTypedProgram program,
        SafeCoreTypedFunction function,
        Dictionary<string, string> names,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        private readonly List<ClrLirLocal> _locals = [];
        private readonly Dictionary<SafeCoreSymbol, int> _bindings = [];
        private readonly List<Block> _blocks = [];
        private Block? _current;
        private int _steps;
        private int _labels;

        public ClrLirMethod Run()
        {
            Start(NewBlock());
            for (int index = 0; index < function.Parameters.Count; index++)
            {
                SafeCoreTypedParameter parameter = function.Parameters[index];
                int local = Local(Map(parameter.Type));
                if (parameter.Pattern.DeclaredSymbol is { } symbol) _bindings.Add(symbol, local);
                Emit(new ClrLirLoadArgument(index));
                Emit(new ClrLirStoreLocal(local));
            }

            Expression(function.Body, 0);
            if (_current is not null) Terminate(new ClrLirReturn());
            return new(names[function.Declaration.DeclaredSymbol!.QualifiedName], Map(function.ReturnType),
                function.Parameters.Select(parameter => Map(parameter.Type)), _locals,
                _blocks.Select(block => new ClrLirBlock(block.Label, block.Instructions)));
        }

        private void Expression(SafeCoreHirNode node, int depth)
        {
            Step(depth);
            if (_current is null) return;
            switch (node.Kind)
            {
                case SafeCoreHirNodeKind.Block:
                    foreach (int child in node.ChildIds) Expression(program.Hir.GetNode(child), depth + 1);
                    break;
                case SafeCoreHirNodeKind.BlockExpression:
                    Expression(Child(node, 0), depth + 1);
                    break;
                case SafeCoreHirNodeKind.LetStatement:
                    Expression(Child(node, node.ChildIds.Count - 1), depth + 1);
                    if (_current is null) break;
                    SafeCoreHirNode pattern = Child(node, 0);
                    if (pattern.DeclaredSymbol is { } binding)
                    {
                        int local = Local(Type(pattern));
                        _bindings.Add(binding, local);
                        Emit(new ClrLirStoreLocal(local));
                    }
                    else Emit(new ClrLirDiscard(Type(pattern)));
                    break;
                case SafeCoreHirNodeKind.ExpressionStatement:
                    SafeCoreHirNode expression = Child(node, 0);
                    Expression(expression, depth + 1);
                    if (_current is not null && Type(expression) != ClrLirType.Void)
                        Emit(new ClrLirDiscard(Type(expression)));
                    break;
                case SafeCoreHirNodeKind.ReturnStatement:
                    if (node.ChildIds.Count != 0) Expression(Child(node, 0), depth + 1);
                    if (_current is not null) Terminate(new ClrLirReturn());
                    break;
                case SafeCoreHirNodeKind.LiteralExpression:
                    if (program.Integers.TryGetValue(node.Id, out int value)) Emit(new ClrLirLoadInt32(value));
                    else Emit(new ClrLirLoadBoolean(node.Value == "true"));
                    break;
                case SafeCoreHirNodeKind.NameExpression:
                    Emit(new ClrLirLoadLocal(_bindings[node.ReferencedSymbol!]));
                    break;
                case SafeCoreHirNodeKind.TupleExpression:
                    if (node.ChildIds.Count == 1) Expression(Child(node, 0), depth + 1);
                    break;
                case SafeCoreHirNodeKind.UnaryExpression:
                    if (program.Integers.TryGetValue(node.Id, out int negative)) Emit(new ClrLirLoadInt32(negative));
                    else
                    {
                        int operand = EvaluateToLocal(Child(node, 0), depth + 1);
                        if (_current is null) break;
                        if (node.Value == "-")
                        {
                            Emit(new ClrLirLoadInt32(0));
                            Emit(new ClrLirLoadLocal(operand));
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

                    break;
                case SafeCoreHirNodeKind.BinaryExpression:
                    Binary(node, depth);
                    break;
                case SafeCoreHirNodeKind.IfExpression:
                    Conditional(node, depth);
                    break;
                case SafeCoreHirNodeKind.CallExpression:
                    Call(node, depth);
                    break;
                case SafeCoreHirNodeKind.PrintExpression:
                    Print(node, depth);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected checked HIR node: " + node.Kind);
            }
        }

        private void Binary(SafeCoreHirNode node, int depth)
        {
            if (node.Value == "=")
            {
                Expression(Child(node, 1), depth + 1);
                if (_current is not null) Emit(new ClrLirStoreLocal(_bindings[Child(node, 0).ReferencedSymbol!]));
                return;
            }

            if (node.Value is "&&" or "||")
            {
                Expression(Child(node, 0), depth + 1);
                if (_current is null) return;
                Block evaluate = NewBlock();
                Block shortcut = NewBlock();
                Block join = NewBlock();
                Emit(new ClrLirBranchTrue(node.Value == "&&" ? evaluate.Label : shortcut.Label));
                Terminate(new ClrLirBranch(node.Value == "&&" ? shortcut.Label : evaluate.Label));
                Start(shortcut);
                Emit(new ClrLirLoadBoolean(node.Value == "||"));
                Terminate(new ClrLirBranch(join.Label));
                Start(evaluate);
                Expression(Child(node, 1), depth + 1);
                if (_current is not null) Terminate(new ClrLirBranch(join.Label));
                Start(join);
                return;
            }

            int lhs = EvaluateToLocal(Child(node, 0), depth + 1);
            int rhs = EvaluateToLocal(Child(node, 1), depth + 1);
            if (_current is null) return;
            Emit(new ClrLirLoadLocal(lhs));
            Emit(new ClrLirLoadLocal(rhs));
            Emit(new ClrLirBinary(node.Value switch
            {
                "+" => ClrLirBinaryOperator.AddChecked,
                "-" => ClrLirBinaryOperator.SubtractChecked,
                "*" => ClrLirBinaryOperator.MultiplyChecked,
                "==" or "!=" => ClrLirBinaryOperator.Equal,
                "<" or ">=" => ClrLirBinaryOperator.LessThan,
                ">" or "<=" => ClrLirBinaryOperator.GreaterThan,
                _ => throw new InvalidOperationException("Unsupported checked operator."),
            }, Type(Child(node, 0))));
            if (node.Value is "!=" or "<=" or ">=")
            {
                Emit(new ClrLirLoadBoolean(false));
                Emit(new ClrLirBinary(ClrLirBinaryOperator.Equal, ClrLirType.Bool));
            }
        }

        private void Conditional(SafeCoreHirNode node, int depth)
        {
            Expression(Child(node, 0), depth + 1);
            if (_current is null) return;
            Block thenBlock = NewBlock();
            Block elseBlock = NewBlock();
            Block join = NewBlock();
            Emit(new ClrLirBranchTrue(thenBlock.Label));
            Terminate(new ClrLirBranch(elseBlock.Label));
            Start(thenBlock);
            Expression(Child(node, 1), depth + 1);
            bool thenContinues = _current is not null;
            if (thenContinues) Terminate(new ClrLirBranch(join.Label));
            Start(elseBlock);
            if (node.ChildIds.Count == 3) Expression(Child(node, 2), depth + 1);
            bool elseContinues = _current is not null;
            if (elseContinues) Terminate(new ClrLirBranch(join.Label));
            if (thenContinues || elseContinues) Start(join);
        }

        private void Call(SafeCoreHirNode node, int depth)
        {
            SafeCoreSymbol target = Child(node, 0).ReferencedSymbol!;
            var arguments = new List<int>();
            var types = new List<ClrLirType>();
            // Spill earlier arguments before evaluating another expression, which may return.
            for (int index = 1; index < node.ChildIds.Count; index++)
            {
                SafeCoreHirNode argument = Child(node, index);
                arguments.Add(EvaluateToLocal(argument, depth + 1));
                types.Add(Type(argument));
            }

            if (_current is null) return;
            foreach (int argument in arguments) Emit(new ClrLirLoadLocal(argument));
            Emit(new ClrLirCall(new(names[target.ResolvedImportTargetQualifiedName ?? target.QualifiedName], Type(node), types)));
        }

        private void Print(SafeCoreHirNode node, int depth)
        {
            ClrLirType valueType = ClrLirType.Text;
            if (node.ChildIds.Count == 1) Emit(new ClrLirLoadString(program.PrintFormats[node.Id]));
            else
            {
                SafeCoreHirNode value = Child(node, 1);
                Expression(value, depth + 1);
                if (_current is null) return;
                valueType = Type(value);
                if (valueType == ClrLirType.I32)
                {
                    Emit(new ClrLirFormatInt32());
                    valueType = ClrLirType.Text;
                }
                if (valueType == ClrLirType.Bool)
                {
                    Block whenTrue = NewBlock();
                    Block whenFalse = NewBlock();
                    Block join = NewBlock();
                    Emit(new ClrLirBranchTrue(whenTrue.Label));
                    Terminate(new ClrLirBranch(whenFalse.Label));
                    Start(whenTrue);
                    Emit(new ClrLirLoadString("true"));
                    Terminate(new ClrLirBranch(join.Label));
                    Start(whenFalse);
                    Emit(new ClrLirLoadString("false"));
                    Terminate(new ClrLirBranch(join.Label));
                    Start(join);
                    valueType = ClrLirType.Text;
                }
            }

            Emit(new ClrLirCall(new("Console.WriteLine", ClrLirType.Void, [valueType])));
        }

        private int EvaluateToLocal(SafeCoreHirNode node, int depth)
        {
            Expression(node, depth);
            if (_current is null) return -1;
            int local = Local(Type(node));
            Emit(new ClrLirStoreLocal(local));
            return local;
        }

        private void Step(int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_steps > 100_000 || depth > 128 || clock.Elapsed > TimeSpan.FromSeconds(10))
                throw new LoweringLimitException();
        }

        private int Local(ClrLirType type)
        {
            if (_locals.Count >= ClrLirLimits.MaximumLocals) throw new LoweringLimitException();
            int index = _locals.Count;
            _locals.Add(new("local_" + index.ToString(CultureInfo.InvariantCulture), type));
            return index;
        }

        private Block NewBlock()
        {
            if (++_labels > ClrLirLimits.MaximumBlocks) throw new LoweringLimitException();
            return new("bb" + _labels.ToString(CultureInfo.InvariantCulture));
        }

        private void Start(Block block) { _blocks.Add(block); _current = block; }
        private void Emit(ClrLirInstruction instruction)
        {
            Step(0);
            if (_current!.Instructions.Count >= ClrLirLimits.MaximumInstructionsPerBlock) throw new LoweringLimitException();
            _current.Instructions.Add(instruction);
        }
        private void Terminate(ClrLirInstruction instruction) { Emit(instruction); _current = null; }
        private SafeCoreHirNode Child(SafeCoreHirNode node, int index) => program.Hir.GetNode(node.ChildIds[index]);
        private ClrLirType Type(SafeCoreHirNode node) => Map(program.Types[node.Id]);
        private sealed class Block(string label)
        {
            public string Label { get; } = label;
            public List<ClrLirInstruction> Instructions { get; } = [];
        }
    }

    private static ClrLirType Map(SafeCorePrimitiveType type) => type switch
    {
        SafeCorePrimitiveType.I32 => ClrLirType.I32,
        SafeCorePrimitiveType.Bool => ClrLirType.Bool,
        SafeCorePrimitiveType.Unit or SafeCorePrimitiveType.Never => ClrLirType.Void,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
