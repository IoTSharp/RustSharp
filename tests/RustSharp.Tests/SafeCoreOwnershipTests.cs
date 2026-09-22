using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreOwnershipTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("ownership CFG drops live values in reverse declaration order", NormalDropAsync),
        new("ownership CFG checks borrow conflicts and explicit NLL", BorrowAsync),
        new("ownership CFG reports move and reference escape diagnostics", MoveAndEscapeAsync),
        new("ownership CFG records unwind and abort panic paths", PanicStrategiesAsync),
        new("ownership CFG bounds looping paths", LoopLimitAsync),
        new("ownership CFG rejects invalid local and terminator IDs", InvalidArenaIdsAsync),
        new("ownership CFG cleans only entered branch scopes", BranchScopeCleanupAsync),
        new("ownership CFG rejects owner assignment during mutable borrow", MutableOwnerWriteAsync),
        new("ownership CFG validates nested reborrows and scope escapes", ReborrowAndScopeEscapeAsync),
    ];

    private static Task NormalDropAsync()
    {
        SafeCoreOwnershipFunction function = Function(
            [
                Local(0, "first", hasDrop: true),
                Local(1, "second", hasDrop: true),
                Local(2, "condition", Type(SafeCoreSemanticTypeKind.Bool), SafeCoreOwnershipKind.Copy),
            ],
            [
                Block(0, [
                    SafeCoreOwnershipInstruction.Use(2, Source(3)),
                    SafeCoreOwnershipInstruction.Assign(2, Source(4)),
                ], SafeCoreOwnershipTerminator.ReturnUnit(Source(5))),
            ]);

        SafeCoreOwnershipAnalysisResult result = SafeCoreOwnershipAnalysis.Analyze(new([function]));
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.Equal("second,first", string.Join(',', result.Paths.Single().DropOrder));
        AssertEx.Equal(SafeCoreOwnershipOutcome.Returned, result.Paths.Single().Outcome);

        SafeCoreOwnershipFunction returned = Function(
            [Local(0, "returned", hasDrop: true)],
            [Block(0, [], SafeCoreOwnershipTerminator.Return(0, Source(6)))]);
        SafeCoreOwnershipAnalysisResult returnedResult = SafeCoreOwnershipAnalysis.Analyze(new([returned]));
        AssertEx.True(returnedResult.IsSuccessful, string.Join(Environment.NewLine, returnedResult.Diagnostics));
        AssertEx.True(returnedResult.Paths.Single().DropOrder.IsEmpty,
            "A moved return value belongs to the caller and must not be dropped by the callee.");
        AssertEx.True(returnedResult.Paths.Single().Trace.Contains("return_move returned"),
            "Ownership transfer on return must be visible in the path trace.");
        return Task.CompletedTask;
    }

    private static Task BorrowAsync()
    {
        SafeCoreOwnershipFunction function = Function(
            [
                Local(0, "owner", hasDrop: true),
                Local(1, "shared", reference: true, initiallyInitialized: false),
                Local(2, "mutable", reference: true, initiallyInitialized: false),
            ],
            [
                Block(0, [
                    SafeCoreOwnershipInstruction.Borrow(0, 1, mutable: false, Source(1)),
                    SafeCoreOwnershipInstruction.EndBorrow(1, Source(2)),
                    SafeCoreOwnershipInstruction.Borrow(0, 2, mutable: true, Source(3)),
                    SafeCoreOwnershipInstruction.Write(2, Source(4)),
                    SafeCoreOwnershipInstruction.EndBorrow(2, Source(5)),
                ], SafeCoreOwnershipTerminator.ReturnUnit(Source(6))),
            ]);

        SafeCoreOwnershipAnalysisResult result = SafeCoreOwnershipAnalysis.Analyze(new([function]));
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.True(result.Paths.Single().Trace.Any(item => item.StartsWith("borrow_mut", StringComparison.Ordinal)),
            "The mutable reborrow must be visible in the trace.");

        SafeCoreOwnershipFunction conflict = Function(
            [Local(0, "owner"), Local(1, "left", reference: true, initiallyInitialized: false), Local(2, "right", reference: true, initiallyInitialized: false)],
            [Block(0, [
                SafeCoreOwnershipInstruction.Borrow(0, 1, mutable: true, Source(7)),
                SafeCoreOwnershipInstruction.Borrow(0, 2, mutable: false, Source(8)),
            ], SafeCoreOwnershipTerminator.ReturnUnit(Source(9)))]);
        SafeCoreOwnershipAnalysisResult rejected = SafeCoreOwnershipAnalysis.Analyze(new([conflict]));
        AssertEx.False(rejected.IsSuccessful, "An overlapping mutable/shared borrow must fail.");
        AssertEx.Equal(SafeCoreOwnershipDiagnosticCodes.BorrowConflict, rejected.Diagnostics.Single().Code);
        return Task.CompletedTask;
    }

    private static Task MoveAndEscapeAsync()
    {
        SafeCoreOwnershipFunction move = Function(
            [Local(0, "source"), Local(1, "target", initiallyInitialized: false)],
            [Block(0, [SafeCoreOwnershipInstruction.Move(0, 1, Source(2)), SafeCoreOwnershipInstruction.Use(0, Source(3))],
                SafeCoreOwnershipTerminator.ReturnUnit(Source(4)))]);
        SafeCoreOwnershipAnalysisResult moved = SafeCoreOwnershipAnalysis.Analyze(new([move]));
        AssertEx.False(moved.IsSuccessful, "Use after move must be rejected.");
        AssertEx.Equal(SafeCoreOwnershipDiagnosticCodes.UseAfterMove, moved.Diagnostics.Single().Code);
        AssertEx.Equal(3, moved.Diagnostics.Single().Source.Span.Start);

        SafeCoreOwnershipFunction escape = Function(
            [Local(0, "owner"), Local(1, "view", reference: true, initiallyInitialized: false)],
            [Block(0, [SafeCoreOwnershipInstruction.Borrow(0, 1, mutable: false, Source(5))],
                SafeCoreOwnershipTerminator.Return(1, Source(6)))]);
        SafeCoreOwnershipAnalysisResult escaped = SafeCoreOwnershipAnalysis.Analyze(new([escape]));
        AssertEx.False(escaped.IsSuccessful, "A live reference must not escape through return.");
        AssertEx.True(escaped.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.Escape),
            "The escape diagnostic must be stable.");
        return Task.CompletedTask;
    }

    private static Task PanicStrategiesAsync()
    {
        SafeCoreOwnershipFunction unwind = Function(
            [Local(0, "resource", hasDrop: true)],
            [Block(0, [], SafeCoreOwnershipTerminator.Panic(Source(1)))], SafeCorePanicStrategy.Unwind);
        SafeCoreOwnershipAnalysisResult unwound = SafeCoreOwnershipAnalysis.Analyze(new([unwind]));
        AssertEx.True(unwound.IsSuccessful, string.Join(Environment.NewLine, unwound.Diagnostics));
        AssertEx.Equal(SafeCoreOwnershipOutcome.Unwound, unwound.Paths.Single().Outcome);
        AssertEx.Equal("resource", unwound.Paths.Single().DropOrder.Single());

        SafeCoreOwnershipFunction abort = Function(
            [Local(0, "resource", hasDrop: true)],
            [Block(0, [], SafeCoreOwnershipTerminator.Panic(Source(2)))], SafeCorePanicStrategy.Abort);
        SafeCoreOwnershipAnalysisResult aborted = SafeCoreOwnershipAnalysis.Analyze(new([abort]));
        AssertEx.True(aborted.IsSuccessful, string.Join(Environment.NewLine, aborted.Diagnostics));
        AssertEx.Equal(SafeCoreOwnershipOutcome.Aborted, aborted.Paths.Single().Outcome);
        AssertEx.True(aborted.Paths.Single().DropOrder.IsEmpty, "Abort must not run unwind cleanup.");
        return Task.CompletedTask;
    }

    private static Task LoopLimitAsync()
    {
        SafeCoreOwnershipFunction loop = Function(
            [Local(0, "condition", Type(SafeCoreSemanticTypeKind.Bool), SafeCoreOwnershipKind.Copy)],
            [Block(0, [SafeCoreOwnershipInstruction.Assign(0, Source(1))], SafeCoreOwnershipTerminator.Goto(0, Source(2)))]);
        SafeCoreOwnershipAnalysisResult result = SafeCoreOwnershipAnalysis.Analyze(new([loop]), new() { MaximumBlockVisits = 3 });
        AssertEx.True(result.IsTruncated, "An unbounded CFG loop must stop at the configured visit limit.");
        AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.LimitReached),
            "The loop limit diagnostic must be machine readable.");
        return Task.CompletedTask;
    }

    private static Task InvalidArenaIdsAsync()
    {
        SafeCoreOwnershipFunction function = new(
            "crate::invalid-ids",
            [Local(0, "value")],
            [new SafeCoreOwnershipScope(0, -1, Source(0))],
            [
                new SafeCoreOwnershipBlock(
                    0,
                    0,
                    [new SafeCoreOwnershipInstruction(SafeCoreOwnershipInstructionKind.Use, -2, -1, false, -1, Source(1))],
                    SafeCoreOwnershipTerminator.Branch(-2, -1, 0, Source(2)),
                    Source(3)),
            ],
            0,
            SafeCorePanicStrategy.Unwind,
            Source(0));

        SafeCoreOwnershipAnalysisResult result = SafeCoreOwnershipAnalysis.Analyze(new([function]));
        AssertEx.False(result.IsSuccessful, "Malformed arena IDs must be rejected before CFG execution.");
        AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.InvalidInput),
            "Negative local and condition IDs must produce InvalidInput diagnostics.");
        AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.InvalidControlFlow),
            "A branch sentinel target must not be accepted as a valid CFG edge.");

        SafeCoreOwnershipFunction unreachableUnknown = new(
            "crate::unknown-instruction",
            [Local(0, "value")],
            [new SafeCoreOwnershipScope(0, -1, Source(10))],
            [
                new SafeCoreOwnershipBlock(0, 0, [], SafeCoreOwnershipTerminator.ReturnUnit(Source(11)), Source(12)),
                new SafeCoreOwnershipBlock(
                    1,
                    0,
                    [new SafeCoreOwnershipInstruction((SafeCoreOwnershipInstructionKind)999, -1, -1, false, -1, Source(13))],
                    SafeCoreOwnershipTerminator.ReturnUnit(Source(14)),
                    Source(15)),
            ],
            0,
            SafeCorePanicStrategy.Unwind,
            Source(10));
        SafeCoreOwnershipAnalysisResult unknown = SafeCoreOwnershipAnalysis.Analyze(new([unreachableUnknown]));
        AssertEx.False(unknown.IsSuccessful, "Unknown instruction kinds must be rejected even in unreachable blocks.");
        AssertEx.True(unknown.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.InvalidInput),
            "Unknown instruction kinds must produce an InvalidInput diagnostic.");

        SafeCoreOwnershipFunction unknownEnums = new(
            "crate::unknown-enums",
            [new SafeCoreOwnershipLocal(0, "value", Type(SafeCoreSemanticTypeKind.Adt),
                (SafeCoreOwnershipKind)999, false, 0, false, true, Source(16))],
            [new SafeCoreOwnershipScope(0, -1, Source(17))],
            [new SafeCoreOwnershipBlock(0, 0, [], SafeCoreOwnershipTerminator.ReturnUnit(Source(18)), Source(19))],
            0,
            (SafeCorePanicStrategy)999,
            Source(17));
        SafeCoreOwnershipAnalysisResult enumResult = SafeCoreOwnershipAnalysis.Analyze(new([unknownEnums]));
        AssertEx.False(enumResult.IsSuccessful, "Unknown ownership enum values must be rejected before execution.");
        AssertEx.True(enumResult.Diagnostics.Count(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.InvalidInput) >= 2,
            "Unknown local kind and panic strategy must both produce InvalidInput diagnostics.");

        SafeCoreMirSource invalidSource = new(string.Empty, new TextSpan(-1, 2), -1, -1);
        SafeCoreOwnershipFunction invalidEvidence = new(
            "crate::invalid-source",
            [new SafeCoreOwnershipLocal(0, "value", Type(SafeCoreSemanticTypeKind.Adt),
                SafeCoreOwnershipKind.Move, false, 0, false, true, invalidSource)],
            [new SafeCoreOwnershipScope(0, -1, invalidSource)],
            [new SafeCoreOwnershipBlock(0, 0, [], SafeCoreOwnershipTerminator.ReturnUnit(invalidSource), invalidSource)],
            0,
            SafeCorePanicStrategy.Unwind,
            invalidSource);
        SafeCoreOwnershipAnalysisResult invalidEvidenceResult = SafeCoreOwnershipAnalysis.Analyze(new([invalidEvidence]));
        AssertEx.False(invalidEvidenceResult.IsSuccessful, "Invalid source evidence must be rejected before ownership execution.");
        AssertEx.True(invalidEvidenceResult.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.InvalidInput),
            "Invalid source evidence must produce an InvalidInput diagnostic.");
        return Task.CompletedTask;
    }

    private static Task BranchScopeCleanupAsync()
    {
        SafeCoreOwnershipFunction function = new(
            "crate::branch-scopes",
            [
                Local(0, "root", hasDrop: true),
                Local(1, "condition", Type(SafeCoreSemanticTypeKind.Bool), SafeCoreOwnershipKind.Copy),
                Local(2, "then_value", Type(SafeCoreSemanticTypeKind.Adt), SafeCoreOwnershipKind.Move, hasDrop: true, scopeId: 1),
                Local(3, "else_value", Type(SafeCoreSemanticTypeKind.Adt), SafeCoreOwnershipKind.Move, hasDrop: true, scopeId: 2),
            ],
            [
                new SafeCoreOwnershipScope(0, -1, Source(0)),
                new SafeCoreOwnershipScope(1, 0, Source(1)),
                new SafeCoreOwnershipScope(2, 0, Source(2)),
            ],
            [
                new SafeCoreOwnershipBlock(0, 0, [SafeCoreOwnershipInstruction.Use(1, Source(3))],
                    SafeCoreOwnershipTerminator.Branch(1, 1, 2, Source(4)), Source(5)),
                new SafeCoreOwnershipBlock(1, 1, [], SafeCoreOwnershipTerminator.ReturnUnit(Source(6)), Source(7)),
                new SafeCoreOwnershipBlock(2, 2, [], SafeCoreOwnershipTerminator.ReturnUnit(Source(8)), Source(9)),
            ],
            0,
            SafeCorePanicStrategy.Unwind,
            Source(0));

        SafeCoreOwnershipAnalysisResult result = SafeCoreOwnershipAnalysis.Analyze(new([function]));
        AssertEx.True(result.IsSuccessful, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.Equal(2, result.Paths.Length);
        SafeCoreOwnershipPath thenPath = result.Paths.Single(path => path.DropOrder.Contains("then_value"));
        SafeCoreOwnershipPath elsePath = result.Paths.Single(path => path.DropOrder.Contains("else_value"));
        AssertEx.False(thenPath.DropOrder.Contains("else_value"), "An unentered sibling scope must not be dropped.");
        AssertEx.False(elsePath.DropOrder.Contains("then_value"), "A branch must not retain the other branch's scope.");
        return Task.CompletedTask;
    }

    private static Task MutableOwnerWriteAsync()
    {
        SafeCoreOwnershipFunction function = Function(
            [Local(0, "owner"), Local(1, "view", reference: true, initiallyInitialized: false)],
            [Block(0, [
                SafeCoreOwnershipInstruction.Borrow(0, 1, mutable: true, Source(1)),
                SafeCoreOwnershipInstruction.Assign(0, Source(2)),
            ], SafeCoreOwnershipTerminator.ReturnUnit(Source(3)))]);

        SafeCoreOwnershipAnalysisResult result = SafeCoreOwnershipAnalysis.Analyze(new([function]));
        AssertEx.False(result.IsSuccessful, "Owner assignment must be rejected while a mutable borrow is active.");
        AssertEx.Equal(SafeCoreOwnershipDiagnosticCodes.BorrowConflict, result.Diagnostics.Single().Code);
        return Task.CompletedTask;
    }

    private static Task ReborrowAndScopeEscapeAsync()
    {
        SafeCoreType mutableReference = SafeCoreType.Reference(SafeCoreType.Adt("Resource"), mutable: true);
        SafeCoreOwnershipFunction valid = Function(
            [
                Local(0, "owner"),
                Local(1, "parent", mutableReference, SafeCoreOwnershipKind.Move, reference: true, initiallyInitialized: false),
                Local(2, "child", mutableReference, SafeCoreOwnershipKind.Move, reference: true, initiallyInitialized: false),
            ],
            [Block(0, [
                SafeCoreOwnershipInstruction.Borrow(0, 1, mutable: true, Source(1)),
                SafeCoreOwnershipInstruction.Borrow(1, 2, mutable: true, Source(2)),
                SafeCoreOwnershipInstruction.EndBorrow(2, Source(3)),
                SafeCoreOwnershipInstruction.Write(1, Source(4)),
                SafeCoreOwnershipInstruction.EndBorrow(1, Source(5)),
            ], SafeCoreOwnershipTerminator.ReturnUnit(Source(6)))]);
        SafeCoreOwnershipAnalysisResult validResult = SafeCoreOwnershipAnalysis.Analyze(new([valid]));
        AssertEx.True(validResult.IsSuccessful, string.Join(Environment.NewLine, validResult.Diagnostics));

        SafeCoreOwnershipFunction sharedParent = Function(
            [
                Local(0, "owner"),
                Local(1, "parent", Type(SafeCoreSemanticTypeKind.Reference), SafeCoreOwnershipKind.Move, reference: true, initiallyInitialized: false),
                Local(2, "child", mutableReference, SafeCoreOwnershipKind.Move, reference: true, initiallyInitialized: false),
            ],
            [Block(0, [
                SafeCoreOwnershipInstruction.Borrow(0, 1, mutable: false, Source(7)),
                SafeCoreOwnershipInstruction.Borrow(1, 2, mutable: true, Source(8)),
            ], SafeCoreOwnershipTerminator.ReturnUnit(Source(9)))]);
        SafeCoreOwnershipAnalysisResult sharedResult = SafeCoreOwnershipAnalysis.Analyze(new([sharedParent]));
        AssertEx.False(sharedResult.IsSuccessful, "A shared reference must not be mutably reborrowed.");
        AssertEx.Equal(SafeCoreOwnershipDiagnosticCodes.BorrowConflict, sharedResult.Diagnostics.Single().Code);

        SafeCoreOwnershipFunction escaped = new(
            "crate::reborrow-escape",
            [
                Local(0, "inner_owner", Type(SafeCoreSemanticTypeKind.Adt), SafeCoreOwnershipKind.Move, hasDrop: true, scopeId: 1),
                Local(1, "outer_view", Type(SafeCoreSemanticTypeKind.Reference), SafeCoreOwnershipKind.Move, reference: true, initiallyInitialized: false),
            ],
            [new SafeCoreOwnershipScope(0, -1, Source(10)), new SafeCoreOwnershipScope(1, 0, Source(11))],
            [
                new SafeCoreOwnershipBlock(0, 1, [SafeCoreOwnershipInstruction.Borrow(0, 1, mutable: false, Source(12))],
                    SafeCoreOwnershipTerminator.Goto(1, Source(13)), Source(14)),
                new SafeCoreOwnershipBlock(1, 0, [], SafeCoreOwnershipTerminator.ReturnUnit(Source(15)), Source(16)),
            ],
            0,
            SafeCorePanicStrategy.Unwind,
            Source(10));
        SafeCoreOwnershipAnalysisResult escapedResult = SafeCoreOwnershipAnalysis.Analyze(new([escaped]));
        AssertEx.False(escapedResult.IsSuccessful, "A reference in an outer scope must not outlive an inner owner.");
        AssertEx.True(escapedResult.Diagnostics.Any(diagnostic => diagnostic.Code == SafeCoreOwnershipDiagnosticCodes.Escape),
            "Scope transition must preserve owner-to-reference escape evidence.");
        return Task.CompletedTask;
    }

    private static SafeCoreOwnershipFunction Function(
        IReadOnlyList<SafeCoreOwnershipLocal> locals,
        IReadOnlyList<SafeCoreOwnershipBlock> blocks,
        SafeCorePanicStrategy strategy = SafeCorePanicStrategy.Unwind) =>
        new("crate::ownership", locals, [new SafeCoreOwnershipScope(0, -1, Source(0))], blocks, 0, strategy, Source(0));

    private static SafeCoreOwnershipBlock Block(int id, IReadOnlyList<SafeCoreOwnershipInstruction> instructions,
        SafeCoreOwnershipTerminator terminator) => new(id, 0, instructions, terminator, Source(id));

    private static SafeCoreOwnershipLocal Local(int id, string name, bool hasDrop = false,
        bool reference = false, bool initiallyInitialized = true) =>
        Local(id, name, Type(reference ? SafeCoreSemanticTypeKind.Reference : SafeCoreSemanticTypeKind.Adt),
            SafeCoreOwnershipKind.Move, hasDrop, 0, reference, initiallyInitialized);

    private static SafeCoreOwnershipLocal Local(int id, string name, SafeCoreType type,
        SafeCoreOwnershipKind kind, bool hasDrop = false, int scopeId = 0,
        bool reference = false, bool initiallyInitialized = true) =>
        new(id, name, type, kind, hasDrop, scopeId, reference, initiallyInitialized, Source(id));

    private static SafeCoreType Type(SafeCoreSemanticTypeKind kind) => kind switch
    {
        SafeCoreSemanticTypeKind.Bool => SafeCoreType.Primitive(kind),
        SafeCoreSemanticTypeKind.Reference => SafeCoreType.Reference(SafeCoreType.Adt("Resource"), mutable: false),
        _ => SafeCoreType.Adt("Resource"),
    };

    private static SafeCoreMirSource Source(int start) => new("ownership.rs", new TextSpan(start, 1), start, 64);
}
