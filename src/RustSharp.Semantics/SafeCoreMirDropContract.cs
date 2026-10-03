using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace RustSharp.Semantics;

/// <summary>Finite state of one source place that owns a destructor.</summary>
public enum SafeCoreDropPlaceState
{
    Uninitialized,
    Live,
    PartiallyMoved,
    Moved,
    Dropped,
}

/// <summary>Events that can change a P1-08 place/drop flag.</summary>
public enum SafeCoreDropEvent
{
    Initialize,
    Move,
    PartialMove,
    AssignReplace,
    ScopeExit,
    ReturnMove,
    PanicUnwind,
    PanicAbort,
}

/// <summary>Result of applying one bounded destructor transition.</summary>
public sealed record SafeCoreDropTransition(
    SafeCoreDropPlaceState State,
    bool ShouldDrop,
    bool IsTerminal,
    bool IsValid,
    string? Diagnostic)
{
    public static SafeCoreDropTransition Invalid(string diagnostic) =>
        new(SafeCoreDropPlaceState.Dropped, false, true, false, diagnostic);
}

/// <summary>
/// Frozen P1-08.01 transition contract.  This model is deliberately small:
/// ownership analysis supplies the places, while this contract decides when a
/// place remains eligible for one destructor call.
/// </summary>
public static class SafeCoreMirDropContract
{
    public const string Profile = "safe-core-drop-contract-p1-v1";
    public const string InvalidTransition = "RSM4101";
    public const string DoublePanic = "RSM4102";

    private static readonly ReadOnlyCollection<SafeCoreDropContractRow> Rows =
        Array.AsReadOnly<SafeCoreDropContractRow>(
        [
            new("DROP-INIT", SafeCoreDropPlaceState.Uninitialized, SafeCoreDropEvent.Initialize,
                SafeCoreDropPlaceState.Live, false, false),
            new("DROP-MOVE", SafeCoreDropPlaceState.Live, SafeCoreDropEvent.Move,
                SafeCoreDropPlaceState.Moved, false, true),
            new("DROP-PARTIAL", SafeCoreDropPlaceState.Live, SafeCoreDropEvent.PartialMove,
                SafeCoreDropPlaceState.PartiallyMoved, false, false),
            new("DROP-REPLACE", SafeCoreDropPlaceState.Live, SafeCoreDropEvent.AssignReplace,
                SafeCoreDropPlaceState.Live, true, false),
            new("DROP-SCOPE", SafeCoreDropPlaceState.Live, SafeCoreDropEvent.ScopeExit,
                SafeCoreDropPlaceState.Dropped, true, true),
            new("DROP-RETURN", SafeCoreDropPlaceState.Live, SafeCoreDropEvent.ReturnMove,
                SafeCoreDropPlaceState.Moved, false, true),
            new("DROP-UNWIND", SafeCoreDropPlaceState.Live, SafeCoreDropEvent.PanicUnwind,
                SafeCoreDropPlaceState.Dropped, true, true),
            new("DROP-ABORT", SafeCoreDropPlaceState.Live, SafeCoreDropEvent.PanicAbort,
                SafeCoreDropPlaceState.Live, false, false),
            new("DROP-UNINIT-UNWIND", SafeCoreDropPlaceState.Uninitialized, SafeCoreDropEvent.PanicUnwind,
                SafeCoreDropPlaceState.Dropped, false, true),
            new("DROP-MOVED-UNWIND", SafeCoreDropPlaceState.Moved, SafeCoreDropEvent.PanicUnwind,
                SafeCoreDropPlaceState.Dropped, false, true),
            new("DROP-DROPPED-UNWIND", SafeCoreDropPlaceState.Dropped, SafeCoreDropEvent.PanicUnwind,
                SafeCoreDropPlaceState.Dropped, false, true),
            new("DROP-UNINIT-EXIT", SafeCoreDropPlaceState.Uninitialized, SafeCoreDropEvent.ScopeExit,
                SafeCoreDropPlaceState.Dropped, false, true),
            new("DROP-MOVED-EXIT", SafeCoreDropPlaceState.Moved, SafeCoreDropEvent.ScopeExit,
                SafeCoreDropPlaceState.Dropped, false, true),
            new("DROP-PARTIAL-EXIT", SafeCoreDropPlaceState.PartiallyMoved, SafeCoreDropEvent.ScopeExit,
                SafeCoreDropPlaceState.Dropped, true, true),
            new("DROP-DROPPED-EXIT", SafeCoreDropPlaceState.Dropped, SafeCoreDropEvent.ScopeExit,
                SafeCoreDropPlaceState.Dropped, false, true),
            new("DROP-DROPPED-ABORT", SafeCoreDropPlaceState.Dropped, SafeCoreDropEvent.PanicAbort,
                SafeCoreDropPlaceState.Dropped, false, true),
        ]);

    public static IReadOnlyList<SafeCoreDropContractRow> TransitionTable => Rows;

    /// <summary>Applies one table row, rejecting repeated or invalid cleanup.</summary>
    public static SafeCoreDropTransition Apply(SafeCoreDropPlaceState state, SafeCoreDropEvent @event)
    {
        if (!Enum.IsDefined(state) || !Enum.IsDefined(@event))
            return SafeCoreDropTransition.Invalid("The drop state or event is outside the P1-08 contract.");

        SafeCoreDropContractRow? row = Rows.FirstOrDefault(item =>
            item.From == state && item.Event == @event);
        if (row is null)
        {
            // Uninitialized and moved values may be abandoned on every panic
            // path, but only an abort is allowed to leave a live value intact.
            if (@event == SafeCoreDropEvent.PanicAbort)
                return new(state, false, state == SafeCoreDropPlaceState.Dropped, true, null);
            return SafeCoreDropTransition.Invalid(
                string.Format(CultureInfo.InvariantCulture,
                    "The transition {0}->{1} is not permitted.", state, @event));
        }

        return new(row.To, row.ShouldDrop, row.Terminal, true, null);
    }

    /// <summary>
    /// Records a destructor failure without losing the original panic.  A
    /// failure during unwind is a double panic and therefore aborts the
    /// remaining transition; normal cleanup may continue with later fields.
    /// </summary>
    public static SafeCoreDropFailureOutcome DestructorFailure(bool duringUnwind,
        Exception firstFailure, Exception destructorFailure)
    {
        ArgumentNullException.ThrowIfNull(firstFailure);
        ArgumentNullException.ThrowIfNull(destructorFailure);
        return duringUnwind
            ? new(SafeCoreDropFailureAction.Abort, firstFailure, destructorFailure)
            : new(SafeCoreDropFailureAction.Continue, firstFailure, destructorFailure);
    }

    /// <summary>Produces the canonical, hashable transition-table snapshot.</summary>
    public static string Snapshot()
    {
        var builder = new StringBuilder(Profile).Append('\n');
        foreach (SafeCoreDropContractRow row in Rows)
        {
            builder.Append(row.CaseId).Append(' ')
                .Append(row.From).Append(" + ").Append(row.Event).Append(" -> ")
                .Append(row.To).Append(" drop=").Append(row.ShouldDrop ? "true" : "false")
                .Append(" terminal=").Append(row.Terminal ? "true" : "false").Append('\n');
        }

        builder.Append("failure normal=continue unwind=abort\n");
        return builder.ToString();
    }
}

public sealed record SafeCoreDropContractRow(
    string CaseId,
    SafeCoreDropPlaceState From,
    SafeCoreDropEvent Event,
    SafeCoreDropPlaceState To,
    bool ShouldDrop,
    bool Terminal);

public enum SafeCoreDropFailureAction
{
    Continue,
    Abort,
}

public sealed record SafeCoreDropFailureOutcome(
    SafeCoreDropFailureAction Action,
    Exception FirstFailure,
    Exception DestructorFailure)
{
    public bool IsDoublePanic => Action == SafeCoreDropFailureAction.Abort;
}
