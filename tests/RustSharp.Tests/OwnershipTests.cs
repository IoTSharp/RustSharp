using RustSharp.CodeGen.IL;

namespace RustSharp.Tests;

/// <summary>
/// P0-13 ownership spike tests. These cases exercise the linear MIR checker
/// directly so ownership failures are rejected before CLR LIR/PE emission.
/// </summary>
internal static class OwnershipTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Ownership move consumes owned local", MoveConsumesOwnedLocalAsync),
        new("Ownership copy remains usable after move", CopyRemainsUsableAsync),
        new("Ownership permits multiple shared borrows", AllowsSharedBorrowsAsync),
        new("Ownership rejects overlapping mutable borrows", RejectsOverlappingMutableBorrowsAsync),
        new("Ownership NLL ends borrow before owner use", NonLexicalLifetimeAsync),
        new("Ownership rejects escaping references", RejectsEscapingReferenceAsync),
        new("Ownership rejects move while borrowed", RejectsMoveWhileBorrowedAsync),
        new("Ownership drop order is deterministic", DeterministicDropOrderAsync),
    ];

    private static Task MoveConsumesOwnedLocalAsync()
    {
        var program = new OwnershipMirProgram(
            [
                new OwnershipMirLocal("source", hasDrop: true),
                new OwnershipMirLocal("destination", initiallyInitialized: false),
            ],
            [
                OwnershipMirInstruction.Move("source", "destination"),
                OwnershipMirInstruction.Use("destination"),
            ]);

        OwnershipMirAnalysisResult result = program.Analyze();
        AssertEx.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.True(result.Trace.Contains("move source -> destination"), "The move must be visible in the MIR trace.");
        AssertEx.Equal("destination", result.DropOrder.Single(), "Drop responsibility must follow the moved value.");
        return Task.CompletedTask;
    }

    private static Task CopyRemainsUsableAsync()
    {
        var program = new OwnershipMirProgram(
            [
                new OwnershipMirLocal("value", OwnershipMirLocalKind.Copy),
                new OwnershipMirLocal("copy", OwnershipMirLocalKind.Copy, initiallyInitialized: false),
            ],
            [
                OwnershipMirInstruction.Move("value", "copy"),
                OwnershipMirInstruction.Use("value"),
                OwnershipMirInstruction.Use("copy"),
            ]);

        OwnershipMirAnalysisResult result = program.Analyze();
        AssertEx.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task AllowsSharedBorrowsAsync()
    {
        var program = new OwnershipMirProgram(
            [new OwnershipMirLocal("owner", hasDrop: true)],
            [
                OwnershipMirInstruction.BorrowShared("owner", "left"),
                OwnershipMirInstruction.BorrowShared("owner", "right"),
                OwnershipMirInstruction.Use("left"),
                OwnershipMirInstruction.Use("right"),
                OwnershipMirInstruction.EndBorrow("left"),
                OwnershipMirInstruction.EndBorrow("right"),
            ]);

        OwnershipMirAnalysisResult result = program.Analyze();
        AssertEx.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.Equal("owner", result.DropOrder.Single());
        return Task.CompletedTask;
    }

    private static Task RejectsOverlappingMutableBorrowsAsync()
    {
        var program = new OwnershipMirProgram(
            [new OwnershipMirLocal("owner")],
            [
                OwnershipMirInstruction.BorrowMutable("owner", "first"),
                OwnershipMirInstruction.BorrowMutable("owner", "second"),
                OwnershipMirInstruction.Use("first"),
            ]);

        OwnershipMirAnalysisResult result = program.Analyze();
        AssertEx.False(result.IsValid, "Two active mutable borrows must be rejected.");
        AssertEx.Equal("OWN002", result.Diagnostics[0].Code);
        return Task.CompletedTask;
    }

    private static Task NonLexicalLifetimeAsync()
    {
        var program = new OwnershipMirProgram(
            [
                new OwnershipMirLocal("owner"),
                new OwnershipMirLocal("moved", initiallyInitialized: false),
            ],
            [
                OwnershipMirInstruction.BorrowShared("owner", "view"),
                OwnershipMirInstruction.Use("view"),
                OwnershipMirInstruction.Move("owner", "moved"),
                OwnershipMirInstruction.Use("moved"),
            ]);

        OwnershipMirAnalysisResult result = program.Analyze();
        AssertEx.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics));
        return Task.CompletedTask;
    }

    private static Task RejectsEscapingReferenceAsync()
    {
        var program = new OwnershipMirProgram(
            [new OwnershipMirLocal("owner")],
            [
                OwnershipMirInstruction.BorrowShared("owner", "view"),
                OwnershipMirInstruction.Return("view"),
            ]);

        OwnershipMirAnalysisResult result = program.Analyze();
        AssertEx.False(result.IsValid, "Returning a live reference must be rejected as an escape.");
        AssertEx.Equal("OWN005", result.Diagnostics[0].Code);
        return Task.CompletedTask;
    }

    private static Task RejectsMoveWhileBorrowedAsync()
    {
        var program = new OwnershipMirProgram(
            [
                new OwnershipMirLocal("owner"),
                new OwnershipMirLocal("target", initiallyInitialized: false),
            ],
            [
                OwnershipMirInstruction.BorrowShared("owner", "view"),
                OwnershipMirInstruction.Move("owner", "target"),
                OwnershipMirInstruction.Use("view"),
            ]);

        OwnershipMirAnalysisResult result = program.Analyze();
        AssertEx.False(result.IsValid, "An owner cannot move while a borrow is active.");
        AssertEx.Equal("OWN009", result.Diagnostics[0].Code);
        return Task.CompletedTask;
    }

    private static Task DeterministicDropOrderAsync()
    {
        var program = new OwnershipMirProgram(
            [
                new OwnershipMirLocal("first", hasDrop: true),
                new OwnershipMirLocal("second", hasDrop: true),
                new OwnershipMirLocal("third", hasDrop: true),
            ],
            [OwnershipMirInstruction.ScopeExit()]);

        OwnershipMirAnalysisResult result = program.Analyze();
        AssertEx.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics));
        AssertEx.True(
            result.DropOrder.SequenceEqual(["third", "second", "first"]),
            $"Drop order must be reverse declaration order, got [{string.Join(", ", result.DropOrder)}].");
        return Task.CompletedTask;
    }
}
