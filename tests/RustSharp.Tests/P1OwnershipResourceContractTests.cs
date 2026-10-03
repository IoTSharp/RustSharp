using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class P1OwnershipResourceContractTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P1 ownership resource rejects invalid statement and instruction options", InvalidOptionsAsync),
        new("P1 ownership resource enforces per-function MIR locals", LocalLimitAsync),
        new("P1 ownership resource enforces per-function MIR blocks", BlockLimitAsync),
        new("P1 ownership resource enforces per-function MIR statements", StatementLimitAsync),
        new("P1 ownership resource bounds independently supplied instruction effects", InstructionLimitAsync),
        new("P1 ownership resource bounds independently supplied arenas", EvidenceArenaLimitAsync),
        new("P1 ownership resource accepts exact per-function boundaries", ExactBoundariesAsync),
        new("P1 ownership resource preserves semantic rejection diagnostics", SemanticDiagnosticsAsync),
        new("P1 ownership resource honors cancellation before analysis", CancellationAsync),
        new("P1 ownership resource preserves the shared validation budget", SharedBudgetAsync),
        new("P1 ownership resource honors the caller wall-clock budget", TimeoutAsync),
    ];

    private static readonly SafeCoreType Integer = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.I32);
    private static readonly SafeCoreType Unit = SafeCoreType.Primitive(SafeCoreSemanticTypeKind.Unit);
    private static readonly SafeCoreMirSource Source = new("p1-ownership-resource.rs", new TextSpan(7, 4), 0, 32);

    private static Task InvalidOptionsAsync()
    {
        var (mir, evidence) = Fixture();
        AssertEx.Throws<ArgumentOutOfRangeException>(() => SafeCoreMirOwnershipAdapter.Analyze(mir, evidence,
            new SafeCoreMirOwnershipOptions { MaximumStatementsPerFunction = 0 }));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => SafeCoreMirOwnershipAdapter.Analyze(mir, evidence,
            new SafeCoreMirOwnershipOptions { MaximumStatementsPerFunction = 65_537 }));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => SafeCoreMirOwnershipAdapter.Analyze(mir, evidence,
            new SafeCoreMirOwnershipOptions { MaximumInstructionsPerBlock = 0 }));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => SafeCoreMirOwnershipAdapter.Analyze(mir, evidence,
            new SafeCoreMirOwnershipOptions { MaximumInstructionsPerBlock = 65_537 }));
        return Task.CompletedTask;
    }

    private static Task LocalLimitAsync()
    {
        var (mir, evidence) = Fixture();
        AssertResourceRejection(SafeCoreMirOwnershipAdapter.Analyze(mir, evidence,
            new SafeCoreMirOwnershipOptions { MaximumLocalsPerFunction = 1 }));
        return Task.CompletedTask;
    }

    private static Task BlockLimitAsync()
    {
        var (mir, evidence) = Fixture();
        AssertResourceRejection(SafeCoreMirOwnershipAdapter.Analyze(mir, evidence,
            new SafeCoreMirOwnershipOptions { MaximumBlocksPerFunction = 2 }));
        return Task.CompletedTask;
    }

    private static Task StatementLimitAsync()
    {
        var (mir, evidence) = Fixture();
        AssertResourceRejection(SafeCoreMirOwnershipAdapter.Analyze(mir, evidence,
            new SafeCoreMirOwnershipOptions { MaximumStatementsPerFunction = 1 }));
        return Task.CompletedTask;
    }

    private static Task InstructionLimitAsync()
    {
        var (mir, evidence) = Fixture();
        AssertResourceRejection(SafeCoreMirOwnershipAdapter.Analyze(mir, evidence,
            new SafeCoreMirOwnershipOptions { MaximumInstructionsPerBlock = 1 }));
        return Task.CompletedTask;
    }

    private static Task EvidenceArenaLimitAsync()
    {
        var (mir, evidence) = Fixture();
        SafeCoreOwnershipFunction first = evidence.Functions[0];
        var extraLocal = new SafeCoreOwnershipLocal(2, "invented", Integer, SafeCoreOwnershipKind.Copy,
            false, 0, false, false, Source);
        var excessLocals = new SafeCoreOwnershipFunction(first.Name, [.. first.Locals, extraLocal],
            first.Scopes, first.Blocks, first.EntryBlockId, first.PanicStrategy, first.Source);
        AssertResourceRejection(SafeCoreMirOwnershipAdapter.Analyze(mir,
            new SafeCoreOwnershipProgram([excessLocals, evidence.Functions[1]]),
            new SafeCoreMirOwnershipOptions { MaximumLocalsPerFunction = 2 }));

        var extraBlock = new SafeCoreOwnershipBlock(3, 0, [], SafeCoreOwnershipTerminator.Unreachable(Source), Source);
        var excessBlocks = new SafeCoreOwnershipFunction(first.Name, first.Locals, first.Scopes,
            [.. first.Blocks, extraBlock], first.EntryBlockId, first.PanicStrategy, first.Source);
        AssertResourceRejection(SafeCoreMirOwnershipAdapter.Analyze(mir,
            new SafeCoreOwnershipProgram([excessBlocks, evidence.Functions[1]]),
            new SafeCoreMirOwnershipOptions { MaximumBlocksPerFunction = 3 }));
        return Task.CompletedTask;
    }

    private static Task ExactBoundariesAsync()
    {
        var first = ValueFunction(0);
        var second = ValueFunction(1);
        var mir = new SafeCoreMirProgram([first.Mir, second.Mir]);
        var evidence = new SafeCoreOwnershipProgram([first.Ownership, second.Ownership]);
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(mir, evidence,
            new SafeCoreMirOwnershipOptions
            {
                MaximumLocalsPerFunction = 2,
                MaximumBlocksPerFunction = 3,
                MaximumStatementsPerFunction = 2,
                MaximumInstructionsPerBlock = 4,
            });
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.Equal(2, result.Ownership!.Paths.Length);
        AssertEx.True(result.Ownership.Paths.All(path => path.Outcome == SafeCoreOwnershipOutcome.Returned),
            "Both functions must retain complete ownership paths at their exact independent arena boundaries.");
        return Task.CompletedTask;
    }

    private static Task SemanticDiagnosticsAsync()
    {
        var mir = new SafeCoreMirProgram([
            new SafeCoreMirFunction(0, "crate::uninitialized", Integer,
                [new SafeCoreMirLocal(0, "value", Integer, SafeCoreMirLocalKind.Temporary, false, Source)],
                [new SafeCoreMirBlock(0, [], SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(0, Integer, Source), Source), Source)],
                0, Source),
        ]);
        var evidence = new SafeCoreOwnershipProgram([
            new SafeCoreOwnershipFunction("crate::uninitialized",
                [new SafeCoreOwnershipLocal(0, "value", Integer, SafeCoreOwnershipKind.Copy, false, 0, false, false, Source)],
                [new SafeCoreOwnershipScope(0, -1, Source)],
                [new SafeCoreOwnershipBlock(0, 0, [SafeCoreOwnershipInstruction.Use(0, Source)],
                    SafeCoreOwnershipTerminator.Return(0, Source), Source)],
                0, SafeCorePanicStrategy.Unwind, Source),
        ]);
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(mir, evidence);
        AssertEx.True(result.Validation is { IsSuccessful: true }, "The small MIR must pass structural validation.");
        AssertEx.False(result.IsSuccessful, "Uninitialized ownership evidence must cause semantic rejection.");
        SafeCoreOwnershipDiagnostic original = result.Ownership!.Diagnostics.First(diagnostic =>
            diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.UseAfterMove);
        Diagnostic surfaced = result.Diagnostics.First(diagnostic => diagnostic.Code == original.Code);
        AssertEx.Equal(original.Message, surfaced.Message);
        AssertEx.Equal(original.Source.Span, surfaced.Span);
        AssertEx.Equal(original.Source.SourcePath, surfaced.SourcePath!);
        AssertEx.Equal(result.Ownership.Diagnostics.Length, result.Diagnostics.Count,
            "Every ownership diagnostic must be observable through the public adapter result.");
        return Task.CompletedTask;
    }

    private static Task CancellationAsync()
    {
        var (mir, evidence) = Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var options = new SafeCoreMirOwnershipOptions { CancellationToken = cancellation.Token };
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreMirOwnershipAdapter.Analyze(mir, evidence, options));
        AssertEx.Throws<OperationCanceledException>(() => SafeCoreMirOwnershipAdapter.Analyze(mir, options));
        return Task.CompletedTask;
    }

    private static Task SharedBudgetAsync()
    {
        var (mir, evidence) = Fixture();
        SafeCoreMirValidationResult baseline = SafeCoreMirValidation.Validate(mir);
        AssertEx.True(baseline.IsSuccessful, "The fixture must pass standalone MIR validation.");
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(mir, evidence,
            new SafeCoreMirOwnershipOptions { MaximumOperations = checked(baseline.OperationsUsed + 4) });
        AssertEx.False(result.IsSuccessful, "Later phases cannot reset the operations consumed by MIR validation.");
        AssertEx.True(result.IsTruncated, "Shared budget exhaustion must be visible to the caller.");
        AssertEx.True(result.Ownership is null || result.Ownership.IsTruncated,
            "Exhausted evidence cannot publish a complete ownership analysis.");
        AssertEx.True(result.Diagnostics.Count > 0, "Budget exhaustion must provide a stable diagnostic.");
        return Task.CompletedTask;
    }

    private static Task TimeoutAsync()
    {
        var (mir, evidence) = Fixture();
        SafeCoreMirOwnershipResult result = SafeCoreMirOwnershipAdapter.Analyze(mir, evidence,
            new SafeCoreMirOwnershipOptions { Timeout = TimeSpan.FromTicks(1) });
        AssertEx.True(result.IsTruncated, "The caller's exhausted wall-clock budget must terminate analysis.");
        AssertEx.False(result.IsSuccessful, "A timed-out analysis cannot claim valid evidence.");
        return Task.CompletedTask;
    }

    private static void AssertResourceRejection(SafeCoreMirOwnershipResult result)
    {
        AssertEx.True(result.Validation is { IsSuccessful: true },
            "The aggregate MIR arena must fit, so the per-function evidence limit is what rejects this fixture.");
        AssertEx.False(result.IsSuccessful, "Evidence that exceeds a configured arena limit cannot succeed.");
        AssertEx.True(result.IsTruncated, "Resource exhaustion must be reported as a truncated result.");
        AssertEx.True(result.Program is null && result.Ownership is null,
            "Resource rejection must not publish a partial ownership program or analysis.");
        AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreMirOwnershipAdapter.LimitReached),
            "Resource rejection must retain the stable RSM3003 diagnostic.");
    }

    private static (SafeCoreMirProgram Mir, SafeCoreOwnershipProgram Ownership) Fixture()
    {
        var first = ValueFunction(0);
        // The second function leaves unused aggregate arena room. The first
        // still must obey each caller limit independently of that room.
        var smallMir = new SafeCoreMirFunction(1, "crate::empty", Unit, [],
            [new SafeCoreMirBlock(0, [], SafeCoreMirTerminator.Return(null, Source), Source)], 0, Source);
        var smallOwnership = new SafeCoreOwnershipFunction("crate::empty", [],
            [new SafeCoreOwnershipScope(0, -1, Source)],
            [new SafeCoreOwnershipBlock(0, 0, [], SafeCoreOwnershipTerminator.ReturnUnit(Source), Source)],
            0, SafeCorePanicStrategy.Unwind, Source);
        return (new SafeCoreMirProgram([first.Mir, smallMir]),
            new SafeCoreOwnershipProgram([first.Ownership, smallOwnership]));
    }

    private static (SafeCoreMirFunction Mir, SafeCoreOwnershipFunction Ownership) ValueFunction(int id)
    {
        string name = "crate::value" + id;
        SafeCoreMirStatement assignment = new(1,
            SafeCoreMirRvalue.Use(SafeCoreMirOperand.Local(0, Integer, Source), Source), Source);
        var mir = new SafeCoreMirFunction(id, name, Integer,
            [
                new SafeCoreMirLocal(0, "input", Integer, SafeCoreMirLocalKind.Parameter, false, Source),
                new SafeCoreMirLocal(1, "copy", Integer, SafeCoreMirLocalKind.Temporary, false, Source),
            ],
            [
                new SafeCoreMirBlock(0, [assignment, assignment],
                    SafeCoreMirTerminator.Return(SafeCoreMirOperand.Local(1, Integer, Source), Source), Source),
                new SafeCoreMirBlock(1, [], SafeCoreMirTerminator.Unreachable(Source), Source),
                new SafeCoreMirBlock(2, [], SafeCoreMirTerminator.Unreachable(Source), Source),
            ], 0, Source);
        var ownership = new SafeCoreOwnershipFunction(name,
            [
                new SafeCoreOwnershipLocal(0, "input", Integer, SafeCoreOwnershipKind.Copy, false, 0, false, true, Source),
                new SafeCoreOwnershipLocal(1, "copy", Integer, SafeCoreOwnershipKind.Copy, false, 0, false, false, Source),
            ],
            [new SafeCoreOwnershipScope(0, -1, Source)],
            [
                new SafeCoreOwnershipBlock(0, 0,
                    [SafeCoreOwnershipInstruction.Use(0, Source with { }), SafeCoreOwnershipInstruction.Assign(1, Source),
                        SafeCoreOwnershipInstruction.Use(0, Source), SafeCoreOwnershipInstruction.Assign(1, Source)],
                    SafeCoreOwnershipTerminator.Return(1, Source), Source),
                new SafeCoreOwnershipBlock(1, 0, [], SafeCoreOwnershipTerminator.Unreachable(Source), Source),
                new SafeCoreOwnershipBlock(2, 0, [], SafeCoreOwnershipTerminator.Unreachable(Source), Source),
            ], 0, SafeCorePanicStrategy.Unwind, Source);
        return (mir, ownership);
    }
}
