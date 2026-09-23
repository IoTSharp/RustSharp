using System.Collections;
using System.Globalization;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirValidationTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("typed MIR preserves frozen collection ownership", FrozenInputsAsync),
        new("typed MIR accepts cyclic CFG and reports deterministic reachability", CyclicFlowAsync),
        new("typed MIR rejects negative and duplicate arena IDs", InvalidIdsAsync),
        new("typed MIR validates dead blocks and explicit terminator shapes", DeadBlocksAsync),
        new("typed MIR validates source evidence without integer overflow", SourceEvidenceAsync),
        new("typed MIR enforces assignments branches and return types", ValueTypesAsync),
        new("typed MIR checks scalar constant ranges and Unicode scalars", ConstantsAsync),
        new("typed MIR checks nominal call signatures arguments and results", CallsAsync),
        new("typed MIR keeps diverging calls terminal", DivergingCallsAsync),
        new("typed MIR validates explicit coercions casts and tuple construction", ComputationsAsync),
        new("typed MIR validates fixed-array construction and indexing", ArraysAsync),
        new("typed MIR validation obeys cancellation work size diagnostic depth and time budgets", ValidationBudgetsAsync),
        new("typed MIR formatter is invariant deterministic and bounded", FormattingAsync),
    ];

    private static readonly SafeCoreType Integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
    private static readonly SafeCoreType Boolean = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Bool);
    private static readonly SafeCoreType Unit = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Unit);
    private static readonly SafeCoreType Never = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Never);
    private static readonly SafeCoreMirSource Source = new("sample.rs", new TextSpan(0, 12), 0, 12);

    private static SafeCoreMirOperand Number(string value = "1") => SafeCoreMirOperand.Constant(Integer, value, Source);
    private static SafeCoreMirBlock ReturnBlock(int id = 0) => new(id, [], SafeCoreMirTerminator.Return(Number(), Source), Source);
    private static SafeCoreMirFunction Function(IReadOnlyList<SafeCoreMirBlock> blocks,
        IReadOnlyList<SafeCoreMirLocal>? locals = null, SafeCoreType? result = null, int entry = 0) =>
        new(0, "crate::main", result ?? Integer, locals ?? [], blocks, entry, Source);
    private static SafeCoreMirProgram Program(params SafeCoreMirBlock[] blocks) => new([Function(blocks)]);
    private static void Invalid(SafeCoreMirProgram program, string code)
    {
        SafeCoreMirValidationResult validation = SafeCoreMirValidation.Validate(program);
        AssertEx.False(validation.IsSuccessful, "Malformed MIR must not pass validation.");
        AssertEx.True(validation.Diagnostics.Any(diagnostic => diagnostic.Code == code), $"Expected MIR diagnostic {code}.");
    }

    private static Task FrozenInputsAsync()
    {
        SafeCoreMirBlock[] blocks = [ReturnBlock()];
        var function = Function(blocks);
        blocks[0] = new(7, [], SafeCoreMirTerminator.Goto(7, Source), Source);
        SafeCoreMirFunction[] functions = [function];
        var program = new SafeCoreMirProgram(functions);
        functions[0] = Function(blocks);
        AssertEx.True(SafeCoreMirValidation.Validate(program).IsSuccessful, "Caller mutations cannot change a MIR snapshot.");
        var indexed = new IndexOnlyList<SafeCoreMirFunction>(function);
        AssertEx.Equal(1, new SafeCoreMirProgram(indexed).Functions.Count);
        AssertEx.Throws<SafeCoreMirLimitException>(() => _ = new SafeCoreMirProgram(new OversizedList<SafeCoreMirFunction>()));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => _ = new SafeCoreMirProgram(indexed, cancelled.Token));
        return Task.CompletedTask;
    }

    private static Task CyclicFlowAsync()
    {
        SafeCoreMirProgram program = Program(
            new(0, [], SafeCoreMirTerminator.Branch(SafeCoreMirOperand.Constant(Boolean, "true", Source), 1, 2, Source), Source),
            new(1, [], SafeCoreMirTerminator.Goto(0, Source), Source), ReturnBlock(2), ReturnBlock(3));
        SafeCoreMirValidationResult result = SafeCoreMirValidation.Validate(program);
        AssertEx.True(result.IsSuccessful, "CFG back edges are valid.");
        AssertEx.Equal("0,1,2", string.Join(',', result.ReachableBlocks[0]));
        return Task.CompletedTask;
    }

    private static Task InvalidIdsAsync()
    {
        Invalid(Program(new SafeCoreMirBlock(0, [], SafeCoreMirTerminator.Goto(-2, Source), Source)), SafeCoreMirDiagnosticCodes.InvalidControlFlow);
        Invalid(Program(ReturnBlock(1)), SafeCoreMirDiagnosticCodes.InvalidControlFlow);
        Invalid(new([Function([ReturnBlock()], entry: -1)]), SafeCoreMirDiagnosticCodes.InvalidControlFlow);
        Invalid(Program(new SafeCoreMirBlock(0, [], SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(-1, Integer, Source), Source), Source)),
            SafeCoreMirDiagnosticCodes.InvalidOperand);
        Invalid(new([Function([ReturnBlock()], [new(-1, "x", Integer, SafeCoreMirLocalKind.Parameter, false, Source)])]),
            SafeCoreMirDiagnosticCodes.InvalidInput);
        SafeCoreMirFunction duplicate = Function([ReturnBlock()]);
        Invalid(new([duplicate, duplicate]), SafeCoreMirDiagnosticCodes.InvalidInput);
        return Task.CompletedTask;
    }

    private static Task DeadBlocksAsync()
    {
        Invalid(Program(ReturnBlock(), new(1, [], SafeCoreMirTerminator.Goto(99, Source), Source)),
            SafeCoreMirDiagnosticCodes.InvalidControlFlow);
        Invalid(Program(new SafeCoreMirBlock(0, [], null!, Source)), SafeCoreMirDiagnosticCodes.InvalidControlFlow);
        Invalid(Program(new SafeCoreMirBlock(0, [], new(SafeCoreMirTerminatorKind.Return, Number(), [], null, 0, -1, Source), Source)),
            SafeCoreMirDiagnosticCodes.InvalidControlFlow);
        return Task.CompletedTask;
    }

    private static Task SourceEvidenceAsync()
    {
        var overflow = new SafeCoreMirSource("bad.rs", new(int.MaxValue, int.MaxValue), 0, int.MaxValue);
        Invalid(Program(new SafeCoreMirBlock(0, [], SafeCoreMirTerminator.Return(Number(), overflow), Source)), SafeCoreMirDiagnosticCodes.InvalidSource);
        var negative = Source with { HirNodeId = -1 };
        Invalid(Program(new SafeCoreMirBlock(0, [], SafeCoreMirTerminator.Return(Number(), negative), Source)), SafeCoreMirDiagnosticCodes.InvalidSource);
        Invalid(Program(new SafeCoreMirBlock(0, [], SafeCoreMirTerminator.Return(Number(), Source with { SourcePath = "" }), Source)),
            SafeCoreMirDiagnosticCodes.InvalidSource);
        return Task.CompletedTask;
    }

    private static Task ValueTypesAsync()
    {
        Invalid(Program(new SafeCoreMirBlock(0, [], SafeCoreMirTerminator.Branch(Number(), 0, 0, Source), Source)), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Program(new SafeCoreMirBlock(0, [], SafeCoreMirTerminator.Return(null, Source), Source)), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(new([Function([new(0, [], SafeCoreMirTerminator.Return(null, Source), Source)], result: Never)]), SafeCoreMirDiagnosticCodes.TypeMismatch);
        SafeCoreMirStatement statement = new(0, SafeCoreMirRvalue.Use(Number(), Source), Source);
        Invalid(new([Function([new(0, [statement], SafeCoreMirTerminator.Return(Number(), Source), Source)],
            [new(0, "x", Boolean, SafeCoreMirLocalKind.User, false, Source)])]), SafeCoreMirDiagnosticCodes.TypeMismatch);
        SafeCoreType unresolved = new SafeCoreTypeInference().Fresh();
        Invalid(new([Function([ReturnBlock()], [new(0, "x", unresolved, SafeCoreMirLocalKind.Parameter, false, Source)])]), SafeCoreMirDiagnosticCodes.TypeMismatch);
        SafeCoreMirOperand invalidOperand = new(SafeCoreMirOperandKind.Constant, null!, -1, "1", Source);
        Invalid(Computation(SafeCoreMirRvalue.Unary("-", invalidOperand, Integer, Source)), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(Program(new SafeCoreMirBlock(0, [], SafeCoreMirTerminator.Branch(invalidOperand, 0, 0, Source), Source)), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(new([new(0, "crate::invalid", null!, [], [ReturnBlock()], 0, Source)]), SafeCoreMirDiagnosticCodes.TypeMismatch);
        return Task.CompletedTask;
    }

    private static Task ConstantsAsync()
    {
        Invalid(Program(new SafeCoreMirBlock(0, [], SafeCoreMirTerminator.Return(Number("2147483648"), Source), Source)), SafeCoreMirDiagnosticCodes.InvalidOperand);
        Invalid(Program(new SafeCoreMirBlock(0, [], SafeCoreMirTerminator.Return(Number("1i32"), Source), Source)), SafeCoreMirDiagnosticCodes.InvalidOperand);
        SafeCoreType character = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Char);
        var surrogate = SafeCoreMirOperand.Constant(character, "55296", Source);
        Invalid(new([Function([new(0, [], SafeCoreMirTerminator.Return(surrogate, Source), Source)], result: character)]), SafeCoreMirDiagnosticCodes.InvalidOperand);
        AssertEx.True(SafeCoreMirValidation.Validate(Program(new SafeCoreMirBlock(0, [], SafeCoreMirTerminator.Return(Number("-2147483648"), Source), Source))).IsSuccessful,
            "Signed minimum constant must be representable.");
        return Task.CompletedTask;
    }

    private static Task CallsAsync()
    {
        SafeCoreMirFunction callee = new(1, "crate::callee", Integer,
            [new(0, "arg", Integer, SafeCoreMirLocalKind.Parameter, false, Source)],
            [new(0, [], SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(0, Integer, Source), Source), Source)], 0, Source);
        SafeCoreMirOperand target = SafeCoreMirOperand.Function(1, SafeCoreType.Function([Integer], Integer, "crate::callee"), Source);
        SafeCoreMirLocal[] locals = [new(0, "result", Integer, SafeCoreMirLocalKind.Temporary, false, Source)];
        SafeCoreMirFunction caller = Function([
            new(0, [], SafeCoreMirTerminator.Call(target, [Number()], 0, 1, Source), Source),
            new(1, [], SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(0, Integer, Source), Source), Source)], locals);
        AssertEx.True(SafeCoreMirValidation.Validate(new([caller, callee])).IsSuccessful, "Typed calls should pass.");
        Invalid(new([Function([new(0, [], SafeCoreMirTerminator.Call(target, [], 0, 0, Source), Source)], locals), callee]), SafeCoreMirDiagnosticCodes.TypeMismatch);
        Invalid(new([Function([new(0, [], SafeCoreMirTerminator.Call(target, [Number()], null, 0, Source), Source)], locals), callee]), SafeCoreMirDiagnosticCodes.TypeMismatch);
        SafeCoreMirOperand wrongNominal = target with { Type = SafeCoreType.Function([Integer], Integer, "crate::other") };
        Invalid(new([Function([new(0, [], SafeCoreMirTerminator.Call(wrongNominal, [Number()], 0, 0, Source), Source)], locals), callee]), SafeCoreMirDiagnosticCodes.TypeMismatch);
        SafeCoreMirOperand negative = target with { Id = -1 };
        Invalid(new([Function([new(0, [], SafeCoreMirTerminator.Call(negative, [Number()], 0, 0, Source), Source)], locals), callee]), SafeCoreMirDiagnosticCodes.InvalidOperand);
        SafeCoreType functionType = target.Type;
        SafeCoreMirLocal[] indirectLocals =
        [
            new(0, "callee", functionType, SafeCoreMirLocalKind.Temporary, false, Source),
            new(1, "result", Integer, SafeCoreMirLocalKind.Temporary, false, Source),
        ];
        SafeCoreMirOperand indirect = SafeCoreMirOperand.Local(0, functionType, Source);
        Invalid(new([Function([
            new(0, [], SafeCoreMirTerminator.Call(indirect, [Number()], 1, 1, Source), Source),
            new(1, [], SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(1, Integer, Source), Source), Source)],
            indirectLocals), callee]), SafeCoreMirDiagnosticCodes.UnsupportedNode);
        return Task.CompletedTask;
    }

    private static Task DivergingCallsAsync()
    {
        SafeCoreMirFunction diverge = new(1, "crate::diverge", Never, [], [new(0, [], SafeCoreMirTerminator.Goto(0, Source), Source)], 0, Source);
        SafeCoreMirOperand target = SafeCoreMirOperand.Function(1, SafeCoreType.Function([], Never, "crate::diverge"), Source);
        SafeCoreMirFunction caller = Function([new(0, [], SafeCoreMirTerminator.Call(target, [], null, -1, Source), Source)]);
        AssertEx.True(SafeCoreMirValidation.Validate(new([caller, diverge])).IsSuccessful, "A diverging callee requires no return path.");
        Invalid(new([Function([new(0, [], SafeCoreMirTerminator.Call(target, [], null, 1, Source), Source), ReturnBlock(1)]), diverge]),
            SafeCoreMirDiagnosticCodes.InvalidControlFlow);
        return Task.CompletedTask;
    }

    private static Task ComputationsAsync()
    {
        SafeCoreType wider = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I64);
        SafeCoreMirRvalue invalid = SafeCoreMirRvalue.Coerce(Number(), wider, Source);
        Invalid(Computation(invalid), SafeCoreMirDiagnosticCodes.TypeMismatch);
        AssertEx.True(SafeCoreMirValidation.Validate(Computation(SafeCoreMirRvalue.Cast(Number(), wider, Source))).IsSuccessful,
            "Explicit numeric casts are distinct from implicit coercions.");
        SafeCoreType tuple = SafeCoreType.Tuple([Integer, Boolean]);
        AssertEx.True(SafeCoreMirValidation.Validate(Computation(SafeCoreMirRvalue.Tuple(
            [Number(), SafeCoreMirOperand.Constant(Boolean, "false", Source)], tuple, Source))).IsSuccessful, "Tuple members retain their types.");
        Invalid(Computation(SafeCoreMirRvalue.Binary("&&", SafeCoreMirOperand.Constant(Boolean, "true", Source),
            SafeCoreMirOperand.Constant(Boolean, "false", Source), Boolean, Source)), SafeCoreMirDiagnosticCodes.TypeMismatch);
        return Task.CompletedTask;
    }

    private static Task ArraysAsync()
    {
        SafeCoreType array = SafeCoreType.Array(Integer, 2);
        SafeCoreType index = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Usize);
        SafeCoreMirRvalue construction = SafeCoreMirRvalue.Array(
            [Number("1"), Number("2")], array, Source);
        AssertEx.True(SafeCoreMirValidation.Validate(Computation(construction)).IsSuccessful,
            "Fixed-array construction must preserve element types and length.");

        SafeCoreMirLocal[] locals =
        [
            new(0, "values", array, SafeCoreMirLocalKind.Parameter, false, Source),
            new(1, "result", Integer, SafeCoreMirLocalKind.Temporary, false, Source),
        ];
        SafeCoreMirOperand values = SafeCoreMirOperand.Local(0, array, Source);
        SafeCoreMirOperand constantIndex = SafeCoreMirOperand.Constant(index, "1", Source);
        SafeCoreMirRvalue read = SafeCoreMirRvalue.Index(values, constantIndex, Integer, Source);
        SafeCoreMirFunction function = Function(
            [new(0, [new(1, read, Source)], SafeCoreMirTerminator.Return(
                SafeCoreMirOperand.Local(1, Integer, Source), Source), Source)], locals);
        AssertEx.True(SafeCoreMirValidation.Validate(new([function])).IsSuccessful,
            "Fixed-array indexing must accept an array local and a usize operand.");

        SafeCoreMirRvalue wrongElement = SafeCoreMirRvalue.Array(
            [Number("1"), SafeCoreMirOperand.Constant(Boolean, "true", Source)], array, Source);
        Invalid(Computation(wrongElement), SafeCoreMirDiagnosticCodes.TypeMismatch);
        SafeCoreMirRvalue outOfBounds = SafeCoreMirRvalue.Index(values,
            SafeCoreMirOperand.Constant(index, "2", Source), Integer, Source);
        SafeCoreMirFunction invalid = Function(
            [new(0, [new(1, outOfBounds, Source)], SafeCoreMirTerminator.Return(
                SafeCoreMirOperand.Local(1, Integer, Source), Source), Source)], locals);
        Invalid(new([invalid]), SafeCoreMirDiagnosticCodes.TypeMismatch);
        return Task.CompletedTask;
    }

    private static SafeCoreMirProgram Computation(SafeCoreMirRvalue value) => new([Function([
        new(0, [new(0, value, Source)], SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(0, value.Type, Source), Source), Source)],
        [new(0, "result", value.Type, SafeCoreMirLocalKind.Temporary, false, Source)], value.Type)]);

    private static Task ValidationBudgetsAsync()
    {
        SafeCoreMirProgram program = Program(ReturnBlock());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreMirValidation.Validate(program, new() { CancellationToken = cancellation.Token }));
        AssertEx.True(SafeCoreMirValidation.Validate(program, new() { MaximumOperations = 1 }).IsTruncated, "Operation limits are explicit.");
        AssertEx.True(SafeCoreMirValidation.Validate(program, new() { Timeout = TimeSpan.FromTicks(1) }).IsTruncated, "Wall time must be checked.");
        AssertEx.True(SafeCoreMirValidation.Validate(Program(ReturnBlock(), ReturnBlock(1)), new() { MaximumBlocks = 1 }).IsTruncated, "Block limits are enforced.");
        SafeCoreMirProgram malformed = Program(new SafeCoreMirBlock(1, [], SafeCoreMirTerminator.Goto(-1, Source), Source));
        AssertEx.True(SafeCoreMirValidation.Validate(malformed, new() { MaximumDiagnostics = 1 }).IsTruncated, "Diagnostic overflow cannot report successful validation.");
        SafeCoreMirValidationResult boundedDiagnostics = SafeCoreMirValidation.Validate(
            Program(new SafeCoreMirBlock(1, [], SafeCoreMirTerminator.Goto(-1, Source), Source)),
            new() { MaximumDiagnostics = 1, MaximumOperations = 10_000 });
        AssertEx.True(boundedDiagnostics.Diagnostics.Count <= 1,
            "Validation must never publish more diagnostics than its configured arena.");
        AssertEx.Equal(SafeCoreMirDiagnosticCodes.LimitReached, boundedDiagnostics.Diagnostics.Single().Code);
        SafeCoreType nested = SafeCoreType.Tuple([SafeCoreType.Tuple([Integer])]);
        SafeCoreMirProgram deep = new([Function([ReturnBlock()], [new(0, "arg", nested, SafeCoreMirLocalKind.Parameter, false, Source)])]);
        AssertEx.True(SafeCoreMirValidation.Validate(deep, new() { MaximumTypeDepth = 1 }).IsTruncated, "Nested types consume depth budget.");
        SafeCoreType oneLevel = SafeCoreType.Tuple([Integer]);
        SafeCoreMirProgram shallow = new([Function([ReturnBlock()], [new(0, "arg", oneLevel, SafeCoreMirLocalKind.Parameter, false, Source)])]);
        AssertEx.True(SafeCoreMirValidation.Validate(shallow, new() { MaximumTypeDepth = 1 }).IsSuccessful,
            "The configured type depth includes the root level and one permitted child.");
        AssertEx.Throws<ArgumentOutOfRangeException>(() => SafeCoreMirValidation.Validate(program, new() { MaximumOperations = 0 }));
        return Task.CompletedTask;
    }

    private static Task FormattingAsync()
    {
        SafeCoreMirProgram program = Program(ReturnBlock());
        const string expected = "safe-core-mir-v1\nfn @0 crate::main -> i32 entry bb0 [sample.rs:0+12/12 hir#0] {\n  bb0 [sample.rs:0+12/12 hir#0]:\n    return const 1:i32 [sample.rs:0+12/12 hir#0]\n}\n";
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG");
            AssertEx.Equal(expected, SafeCoreMirFormatting.Format(program));
        }
        finally { CultureInfo.CurrentCulture = previous; }
        AssertEx.Equal(expected, SafeCoreMirFormatting.Format(program));
        AssertEx.Throws<SafeCoreMirLimitException>(() => SafeCoreMirFormatting.Format(program, new() { MaximumCharacters = 1 }));
        AssertEx.Throws<SafeCoreMirLimitException>(() => SafeCoreMirFormatting.Format(program, new() { MaximumOperations = 1 }));
        AssertEx.Throws<SafeCoreMirLimitException>(() => SafeCoreMirFormatting.Format(program, new() { Timeout = TimeSpan.FromTicks(1) }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreMirFormatting.Format(program, new() { CancellationToken = cancellation.Token }));
        return Task.CompletedTask;
    }

    private sealed class IndexOnlyList<T>(T item) : IReadOnlyList<T>
    {
        public int Count => 1;
        public T this[int index] => index == 0 ? item : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Unbounded enumeration is forbidden.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class OversizedList<T> : IReadOnlyList<T>
    {
        public int Count => 100_001;
        public T this[int index] => throw new InvalidOperationException("Oversized collections must be rejected before access.");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Unbounded enumeration is forbidden.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
