using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreMirOwnershipAdapterTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("typed MIR ownership adapter materializes scalar constants", ConstantReturnAsync),
        new("typed MIR ownership adapter applies local limits per function", PerFunctionLimitAsync),
        new("typed MIR ownership adapter bounds materialized constants", MaterializedConstantLimitAsync),
        new("typed MIR ownership adapter reports use before initialization", UseBeforeInitializationAsync),
        new("typed MIR ownership adapter rejects reference evidence it cannot prove", UnsupportedReferenceAsync),
    ];

    private static readonly SafeCoreType Integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
    private static readonly SafeCoreType Reference = SafeCoreType.Reference(Integer, mutable: false);
    private static readonly SafeCoreMirSource Source = new("ownership-adapter.rs", new TextSpan(0, 16), 0, 16);

    private static Task ConstantReturnAsync()
    {
        SafeCoreMirProgram program = new([
            new SafeCoreMirFunction(
                0,
                "crate::constant",
                Integer,
                [],
                [new SafeCoreMirBlock(
                    0,
                    [],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Constant(Integer, "7", Source), Source),
                    Source)],
                0,
                Source),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(program);
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.True(result.Program!.Functions[0].Locals.Count == 1,
            "A scalar constant return must be represented by one explicit adapter local.");
        AssertEx.Equal(SafeCoreOwnershipOutcome.Returned, result.Ownership!.Paths.Single().Outcome);
        return Task.CompletedTask;
    }

    private static Task UseBeforeInitializationAsync()
    {
        SafeCoreMirProgram program = new([
            new SafeCoreMirFunction(
                0,
                "crate::uninitialized",
                Integer,
                [new SafeCoreMirLocal(0, "value", Integer, SafeCoreMirLocalKind.Temporary, false, Source)],
                [new SafeCoreMirBlock(
                    0,
                    [],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(0, Integer, Source), Source),
                    Source)],
                0,
                Source),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(program);
        AssertEx.False(result.IsSuccessful, "Typed MIR structural validity must not imply definite initialization.");
        AssertEx.True(result.Ownership!.Diagnostics.Any(diagnostic =>
            diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.UseAfterMove),
            "The ownership phase must report a use of an uninitialized typed-MIR local.");
        return Task.CompletedTask;
    }

    private static Task MaterializedConstantLimitAsync()
    {
        SafeCoreMirProgram program = new([
            new SafeCoreMirFunction(
                0,
                "crate::bounded_constant",
                Integer,
                [new SafeCoreMirLocal(0, "slot", Integer, SafeCoreMirLocalKind.User, false, Source)],
                [new SafeCoreMirBlock(
                    0,
                    [],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Constant(Integer, "7", Source), Source),
                    Source)],
                0,
                Source),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(
            program,
            new SafeCoreMirOwnershipOptions { MaximumLocalsPerFunction = 1 });
        AssertEx.False(result.IsSuccessful, "Synthetic locals must obey the configured arena limit.");
        AssertEx.True(result.Program is null, "A bounded adaptation failure must not publish a partial program.");
        AssertEx.True(result.Diagnostics.Any(diagnostic =>
                diagnostic.Code == SafeCoreMirOwnershipDiagnosticCodes.LimitReached),
            "The adapter must report the bounded local limit.");
        return Task.CompletedTask;
    }

    private static Task PerFunctionLimitAsync()
    {
        SafeCoreMirProgram program = new([
            ConstantFunction(0, "crate::first"),
            ConstantFunction(1, "crate::second"),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(
            program,
            new SafeCoreMirOwnershipOptions { MaximumLocalsPerFunction = 1 });
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.Equal(2, result.Program!.Functions.Count);
        AssertEx.True(result.Program.Functions.All(function => function.Locals.Count == 1),
            "The local arena limit must apply independently to each function.");
        return Task.CompletedTask;
    }

    private static SafeCoreMirFunction ConstantFunction(int id, string name) =>
        new(
            id,
            name,
            Integer,
            [],
            [new SafeCoreMirBlock(
                0,
                [],
                SafeCoreMirTerminator.Return(SafeCoreMirOperand.Constant(Integer, "7", Source), Source),
                Source)],
            0,
            Source);

    private static Task UnsupportedReferenceAsync()
    {
        SafeCoreMirProgram program = new([
            new SafeCoreMirFunction(
                0,
                "crate::reference",
                Reference,
                [new SafeCoreMirLocal(0, "view", Reference, SafeCoreMirLocalKind.Parameter, false, Source)],
                [new SafeCoreMirBlock(
                    0,
                    [],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(0, Reference, Source), Source),
                    Source)],
                0,
                Source),
        ]);

        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(program);
        AssertEx.False(result.IsSuccessful, "Reference ownership cannot be inferred from typed MIR alone.");
        AssertEx.Equal(SafeCoreMirOwnershipDiagnosticCodes.Unsupported, result.Diagnostics.Single().Code);
        return Task.CompletedTask;
    }
}
