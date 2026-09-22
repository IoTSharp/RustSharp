using System.Diagnostics;
using System.Globalization;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirLoweringTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("MIR lowering produces deterministic typed three-address snapshots", SnapshotAsync),
        new("MIR lowering preserves scalar literal spelling and original HIR evidence", LiteralsAsync),
        new("MIR lowering preserves branches loops break continue and direct calls", ControlFlowAsync),
        new("MIR lowering snapshots reads before later side effects", EvaluationOrderAsync),
        new("MIR lowering emits typed tuple aggregates", TupleAsync),
        new("MIR lowering short circuits and drops unreachable tails", DivergenceAsync),
        new("MIR lowering rejects unsupported constructs at their original spans", UnsupportedAsync),
        new("MIR lowering bounds work size nesting time and cancellation", LimitsAsync),
    ];

    private static Task SnapshotAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SafeCoreTypeAnalysisProgram typed = Check("fn f() -> i32 { 1 + 2 }", cancellation.Token);
        SafeCoreMirProgram first = Lower(typed, cancellation.Token);
        SafeCoreMirProgram second = Lower(typed, cancellation.Token);
        AssertEx.Equal(SafeCoreMirFormatting.Format(first), SafeCoreMirFormatting.Format(second));
        SafeCoreMirFunction function = first.Functions.Single();
        AssertEx.Equal("crate::f#value", function.Name);
        AssertEx.Equal(1, function.Blocks.Count);
        AssertEx.Equal(1, function.Locals.Count);
        AssertEx.Equal("tmp0", function.Locals[0].Name);
        AssertEx.Equal("i32", function.Locals[0].Type.ToString());
        SafeCoreMirStatement statement = function.Blocks[0].Statements.Single();
        AssertEx.Equal(SafeCoreMirRvalueKind.Binary, statement.Value.Kind);
        AssertEx.Equal("+", statement.Value.Operator!);
        AssertEx.Equal("1,2", string.Join(',', statement.Value.Operands.Select(o => o.Value)));
        AssertEx.Equal(SafeCoreMirTerminatorKind.Return, function.Blocks[0].Terminator.Kind);
        AssertEx.Equal(statement.DestinationLocalId, function.Blocks[0].Terminator.Operand!.Id);
        return Task.CompletedTask;
    }

    private static Task LiteralsAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        const string source = "fn a() -> i8 { -((128i8)) } fn b() -> i128 { -170141183460469231731687303715884105728i128 } " +
            "fn c() -> u16 { 0xff_u16 } fn d() -> u16 { 0o17u16 } fn e() -> u16 { 0b1010u16 } " +
            "fn f() -> f32 { 0.1f32 } fn g() -> char { '\\u{1_f980}' } fn h() -> u8 { b'\\xFF' }";
        SafeCoreTypeAnalysisProgram typed = Check(source, cancellation.Token);
        SafeCoreMirProgram mir = Lower(typed, cancellation.Token);
        AssertEx.Equal("-128,-170141183460469231731687303715884105728,255,15,10,0.1,129408,255",
            string.Join(',', mir.Functions.Select(f => f.Blocks[0].Terminator.Operand!.Value)));
        var clock = Stopwatch.StartNew();
        AssertEx.True(mir.Functions.Count <= 10, "The source-evidence audit is capped at ten functions.");
        foreach (SafeCoreMirFunction function in mir.Functions)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(10), "Source audit timed out.");
            SafeCoreMirSource original = function.Blocks[0].Terminator.Operand!.Source;
            AssertEx.Equal("mir-lowering.rs", original.SourcePath);
            AssertEx.Equal(typed.Hir.GetNode(original.HirNodeId).Span, original.Span);
            AssertEx.True(original.Span.End <= original.SourceLength && original.SourceLength <= source.Length,
                "MIR source provenance must stay inside the HIR document extent.");
        }
        return Task.CompletedTask;
    }

    private static Task ControlFlowAsync()
    {
        Run("fn add(a: i32, b: i32) -> i32 { a + b } fn f() -> i32 { let mut x = 0; " +
            "while x < 5 { x += 1; if x == 2 { continue; } if x == 4 { break; } } add(x, 3) }", 7L);
        Run("fn f() -> i32 { let mut x = 0; loop { x += 1; if x == 3 { break x + 4; } } }", 7L);
        Run("fn f() -> i32 { loop { let inner = loop { break 9; }; break inner; } }", 9L);
        Run("fn f() -> i32 { while { return 6; true } {} 7 }", 6L);
        Run("fn f() -> i32 { let x = if true { return 3; } else { 4 }; x }", 3L);
        Run("mod m { pub fn add(x: i32) -> i32 { x + 2 } } use m::add as plus; fn f() -> i32 { plus(5) }", 7L);
        return Task.CompletedTask;
    }

    private static Task EvaluationOrderAsync()
    {
        Run("fn f() -> i32 { let mut x = 1; x + { x = 2; x } }", 3L);
        Run("fn pair(a: i32, b: i32) -> i32 { a * 10 + b } fn f() -> i32 { let mut x = 1; pair(x, { x = 2; x }) }", 12L);
        Run("fn f() -> i32 { let mut x = 1; x += { x = 2; 3 }; x }", 5L);
        Run("fn f() -> i32 { let mut x = 1; { let x = 9; x; } x }", 1L);
        return Task.CompletedTask;
    }

    private static Task TupleAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SafeCoreMirProgram mir = Lower(Check("fn f() -> (i32, bool) { (1, true) }", cancellation.Token), cancellation.Token);
        SafeCoreMirFunction function = mir.Functions.Single();
        AssertEx.Equal(SafeCoreSemanticTypeKind.Tuple, function.ReturnType.Kind);
        AssertEx.Equal(2, function.ReturnType.Elements.Count);
        SafeCoreMirStatement tuple = function.Blocks[0].Statements.Single();
        AssertEx.Equal(SafeCoreMirRvalueKind.Tuple, tuple.Value.Kind);
        AssertEx.Equal(2, tuple.Value.Operands.Count);
        AssertEx.Equal("1,true", string.Join(',', tuple.Value.Operands.Select(operand => operand.Value)));
        AssertEx.True(SafeCoreMirValidation.Validate(mir).IsSuccessful,
            "Tuple lowering must publish a valid typed MIR program.");
        return Task.CompletedTask;
    }

    private static Task DivergenceAsync()
    {
        Run("fn f() -> i32 { let mut x = 0; false && { x = 1; true }; true || { x = 2; false }; x }", 0L);
        Run("fn f() -> i32 { let mut x = 0; true && { x = 1; true }; false || { x = 2; false }; x }", 2L);
        Run("fn f() -> i32 { false && { return 9; }; 1 }", 1L);
        Run("fn f() -> i32 { loop { let x = { break 5; }; x; } }", 5L);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SafeCoreMirProgram mir = Lower(Check("fn unused() -> i32 { 2 } fn f() -> i32 { return 1; unused() }", cancellation.Token), cancellation.Token);
        SafeCoreMirFunction f = mir.Functions.Single(f => f.Name == "crate::f#value");
        AssertEx.Equal(1, f.Blocks.Count);
        AssertEx.Equal(0, f.Blocks[0].Statements.Count);
        AssertEx.Equal("1", f.Blocks[0].Terminator.Operand!.Value!);
        SafeCoreMirProgram never = Lower(Check("fn halt() -> ! { loop {} } fn f(flag: bool) -> i32 { if flag { halt() } else { 4 } }", cancellation.Token), cancellation.Token);
        AssertEx.True(never.Functions[1].Blocks.Any(b => b.Terminator.Kind == SafeCoreMirTerminatorKind.Call), "A never-returning call must remain an explicit call terminator.");
        AssertEx.True(never.Functions[1].Blocks.Any(b => b.Terminator.Kind == SafeCoreMirTerminatorKind.Unreachable), "The never continuation must terminate explicitly.");
        return Task.CompletedTask;
    }

    private static Task UnsupportedAsync()
    {
        (string Source, string Span)[] cases =
        [
            ("fn f() { let x = [1, 2]; }", "[1, 2]"),
            ("fn f() { let x = || 1; }", "|| 1"),
            ("fn f() -> i32 { match true { true => 1, false => 2 } }", "match true { true => 1, false => 2 }"),
            ("fn f() { let x = &1; }", "&1"),
            ("const X: i32 = 1; fn f() -> i32 { X }", "const X: i32 = 1;"),
            ("fn id() {} fn f() { let p = id; p(); }", "id"),
        ];
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        for (int index = 0; index < cases.Length; index++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var item = cases[index];
            SafeCoreMirLoweringResult result = SafeCoreMirLowering.Lower(Check(item.Source, cancellation.Token), cancellationToken: cancellation.Token);
            AssertEx.False(result.IsSuccessful, item.Source);
            AssertEx.True(result.Program is null, "Rejected lowering must never expose a partial program.");
            Diagnostic diagnostic = result.Diagnostics.Single();
            AssertEx.Equal(SafeCoreMirLowering.UnsupportedSyntax, diagnostic.Code);
            AssertEx.Equal("mir-lowering.rs", diagnostic.SourcePath!);
            AssertEx.Equal(item.Span, item.Source.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
        }
        return Task.CompletedTask;
    }

    private static Task LimitsAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SafeCoreTypeAnalysisProgram typed = Check("fn f(flag: bool) -> i32 { if flag { 1 + 2 } else { 3 } }", cancellation.Token);
        SafeCoreMirLoweringOptions[] limits =
        [
            new() { MaximumOperations = 1 }, new() { MaximumNestingDepth = 1 },
            new() { MaximumBlocksPerFunction = 1 }, new() { MaximumLocalsPerFunction = 1 },
            new() { Timeout = TimeSpan.FromTicks(1) },
        ];
        for (int index = 0; index < limits.Length; index++)
        {
            SafeCoreMirLoweringResult result = SafeCoreMirLowering.Lower(typed, limits[index], cancellation.Token);
            AssertEx.True(result.IsTruncated && result.Program is null, "Each configured bound must reject without a partial program.");
            AssertEx.Equal(SafeCoreMirLowering.LimitReached, result.Diagnostics.Single().Code);
        }
        var missing = typed with { Types = new Dictionary<int, SafeCoreType>() };
        AssertEx.Equal(SafeCoreMirLowering.InvalidEvidence,
            SafeCoreMirLowering.Lower(missing, cancellationToken: cancellation.Token).Diagnostics.Single().Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreMirLowering.Lower(typed, cancellationToken: cancelled.Token));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => SafeCoreMirLowering.Lower(typed, new() { Timeout = TimeSpan.Zero }));
        return Task.CompletedTask;
    }

    private static SafeCoreTypeAnalysisProgram Check(string source, CancellationToken cancellation)
    {
        SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source, "mir-lowering.rs", new() { Timeout = TimeSpan.FromSeconds(5) }, cancellation);
        AssertEx.True(syntax.IsSuccessful, $"Syntax: {source}\n{Format(syntax.Diagnostics)}");
        SafeCoreHirResult hir = SafeCoreHirLowering.Lower(syntax, new()
        {
            Timeout = TimeSpan.FromSeconds(5), CancellationToken = cancellation,
            NameResolution = new() { EnableTypeSystemExtensions = true, Timeout = TimeSpan.FromSeconds(5) },
        });
        AssertEx.True(hir.IsSuccessful, $"HIR: {source}\n{Format(hir.Diagnostics)}");
        SafeCoreTypeAnalysisResult typed = SafeCoreTypeAnalysis.Check(hir, new() { Timeout = TimeSpan.FromSeconds(5) }, cancellation);
        AssertEx.True(typed.IsSuccessful, $"{source}\n{Format(typed.Diagnostics)}");
        return typed.Program!;
    }

    private static SafeCoreMirProgram Lower(SafeCoreTypeAnalysisProgram typed, CancellationToken cancellation)
    {
        SafeCoreMirLoweringResult result = SafeCoreMirLowering.Lower(typed, new() { Timeout = TimeSpan.FromSeconds(5) }, cancellation);
        AssertEx.True(result.IsSuccessful, Format(result.Diagnostics));
        AssertEx.True(result.Validation is { IsSuccessful: true }, "Every successful lowering must contain explicit validator evidence.");
        return result.Program!;
    }

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) => string.Join("; ", diagnostics.Select(d => $"{d.Code}: {d.Message}"));

    private static void Run(string source, long expected)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SafeCoreMirProgram mir = Lower(Check(source, cancellation.Token), cancellation.Token);
        var interpreter = new ScalarInterpreter(mir, cancellation.Token);
        AssertEx.Equal(expected, (long)interpreter.Invoke(mir.Functions.Single(f => f.Name == "crate::f#value").Id, [], 0)!, source);
    }

    // Test-only executable semantics checks evaluation and branch behavior independently
    // of how many temporary slots or empty join blocks the lowerer chooses to emit.
    private sealed class ScalarInterpreter(SafeCoreMirProgram program, CancellationToken cancellation)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _steps;

        public object? Invoke(int functionId, object?[] arguments, int depth)
        {
            AssertEx.True(depth <= 32, "MIR test call nesting exceeded 32.");
            SafeCoreMirFunction function = program.Functions[functionId];
            var locals = new object?[function.Locals.Count];
            for (int index = 0; index < arguments.Length; index++) { Step(); locals[index] = arguments[index]; }
            int blockId = function.EntryBlockId;
            for (int iteration = 0; iteration < 4_096; iteration++)
            {
                Step();
                SafeCoreMirBlock block = function.Blocks[blockId];
                foreach (SafeCoreMirStatement statement in block.Statements)
                {
                    Step();
                    IReadOnlyList<SafeCoreMirOperand> operands = statement.Value.Operands;
                    object? first = Read(operands[0], locals);
                    object? value = statement.Value.Kind switch
                    {
                        SafeCoreMirRvalueKind.Use or SafeCoreMirRvalueKind.Coerce => first,
                        SafeCoreMirRvalueKind.Binary => Binary(statement.Value.Operator!, first!, Read(operands[1], locals)!),
                        SafeCoreMirRvalueKind.Unary => statement.Value.Operator == "!" ? !(bool)first! : -(long)first!,
                        _ => throw new InvalidOperationException("Unexpected rvalue in bounded scalar test interpreter."),
                    };
                    locals[statement.DestinationLocalId] = value;
                }
                SafeCoreMirTerminator terminator = block.Terminator;
                switch (terminator.Kind)
                {
                    case SafeCoreMirTerminatorKind.Return: return terminator.Operand is null ? null : Read(terminator.Operand, locals);
                    case SafeCoreMirTerminatorKind.Goto: blockId = terminator.TargetBlockId; break;
                    case SafeCoreMirTerminatorKind.Branch:
                        blockId = (bool)Read(terminator.Operand!, locals)! ? terminator.TargetBlockId : terminator.FalseTargetBlockId; break;
                    case SafeCoreMirTerminatorKind.Call:
                        object? result = Invoke(terminator.Operand!.Id, terminator.Arguments.Select(a => Read(a, locals)).ToArray(), depth + 1);
                        if (terminator.DestinationLocalId is int local) locals[local] = result;
                        blockId = terminator.TargetBlockId; break;
                    default: throw new InvalidOperationException("Test unexpectedly reached unreachable MIR.");
                }
            }
            throw new InvalidOperationException("MIR test exceeded 4096 CFG steps.");
        }

        private static object? Read(SafeCoreMirOperand operand, object?[] locals) => operand.Kind == SafeCoreMirOperandKind.Local
            ? locals[operand.Id] ?? throw new InvalidOperationException("Test read an uninitialized MIR slot.")
            : operand.Type.Kind switch
            {
                SafeCoreSemanticTypeKind.Unit => null,
                SafeCoreSemanticTypeKind.Bool => bool.Parse(operand.Value!),
                _ => long.Parse(operand.Value!, CultureInfo.InvariantCulture),
            };

        private static object Binary(string op, object left, object right) => op switch
        {
            "+" => (long)left + (long)right, "-" => (long)left - (long)right,
            "*" => (long)left * (long)right, "/" => (long)left / (long)right,
            "==" => left.Equals(right), "!=" => !left.Equals(right),
            "<" => (long)left < (long)right, ">" => (long)left > (long)right,
            "<=" => (long)left <= (long)right, ">=" => (long)left >= (long)right,
            _ => throw new InvalidOperationException("Unexpected binary operator in MIR test."),
        };

        private void Step()
        {
            cancellation.ThrowIfCancellationRequested();
            AssertEx.True(++_steps <= 4_096 && _clock.Elapsed < TimeSpan.FromSeconds(5), "Bounded MIR interpreter exceeded its work or time budget.");
        }
    }
}
