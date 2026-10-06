using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>Bounds for source/MIR drop-flag projection.</summary>
public sealed record SafeCoreMirDropFlagLoweringOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumOperations { get; init; } = 1_000_000;
    public int MaximumPaths { get; init; } = 65_536;
    public int MaximumEventsPerPath { get; init; } = 65_536;
    public int MaximumCharacters { get; init; } = 4_000_000;
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>One deterministic per-place drop flag observation.</summary>
public sealed record SafeCoreMirDropFlag(
    int LocalId,
    string LocalName,
    SafeCoreDropPlaceState State,
    bool Eligible,
    SafeCoreMirSource Source)
{
    /// <summary>An active enum payload condition required before this potential obligation is eligible.</summary>
    public string? GuardCondition { get; init; }
}

/// <summary>Flag observations for one ownership path.</summary>
public sealed record SafeCoreMirDropFlagPath(
    int PathId,
    string FunctionName,
    SafeCoreOwnershipOutcome Outcome,
    IReadOnlyList<SafeCoreMirDropFlag> FinalFlags,
    IReadOnlyList<IReadOnlyList<SafeCoreMirDropFlag>> Events,
    SafeCoreMirSource Source);

/// <summary>Result and canonical snapshot for the drop-flag projection.</summary>
public sealed record SafeCoreMirDropFlagResult(
    IReadOnlyList<SafeCoreMirDropFlagPath> Paths,
    string? Snapshot,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool IsTruncated)
{
    public bool IsSuccessful => !IsTruncated && Diagnostics.Count == 0 &&
        Paths.Count > 0 && Snapshot is not null;
}

/// <summary>
/// Materializes the initialization/move/drop flags already implied by
/// ownership evidence. It never invents a drop obligation and never executes
/// a destructor, so generated cleanup can consume the same path facts.
/// </summary>
public static partial class SafeCoreMirDropFlagLowering
{
    public const string Profile = "safe-core-mir-drop-flags-p1-v1";
    public const string InvalidEvidence = "RSM4201";
    public const string LimitReached = "RSM4202";

    public static SafeCoreMirDropFlagResult Lower(
        SafeCoreMirOwnershipResult evidence,
        SafeCoreMirDropFlagLoweringOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        options ??= new();
        ValidateOptions(options);
        options.CancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        int operations = 0;
        var diagnostics = new List<Diagnostic>();
        try
        {
            if (!evidence.IsSuccessful || evidence.Program is null || evidence.Ownership is null)
            {
                AddDiagnostic(diagnostics, InvalidEvidence,
                    "Drop-flag lowering requires successful ownership evidence.", InvalidSource, options);
                return Failure(diagnostics, false);
            }

            // The ownership analysis is a versioned producer/consumer
            // boundary.  Do not silently discard a path for an unknown
            // function or collapse duplicate local identities while building
            // the flag map; either would publish an incomplete cleanup plan.
            ValidateEvidenceShape(evidence.Program, evidence.Ownership, options, clock, ref operations);
            if (evidence.MirProgram is { } typedMir)
                return LowerTyped(typedMir, evidence.Program, evidence.Ownership, options, clock, ref operations);

            var lowered = new List<SafeCoreMirDropFlagPath>();
            foreach (SafeCoreOwnershipFunction function in evidence.Program.Functions)
            {
                Step(options, clock, ref operations);
                SafeCoreOwnershipPath[] paths = [.. evidence.Ownership.Paths
                    .Where(path => string.Equals(path.FunctionName, function.Name, StringComparison.Ordinal))
                    .OrderBy(path => path.PathId)];
                foreach (SafeCoreOwnershipPath path in paths)
                {
                    Step(options, clock, ref operations);
                    if (lowered.Count >= options.MaximumPaths) throw new DropFlagLimitException();
                    lowered.Add(LowerPath(function, path, options, clock, ref operations));
                }
            }

            if (diagnostics.Count != 0) return Failure(diagnostics, false);
            string snapshot = Format(lowered, options, clock, ref operations);
            return new(lowered.AsReadOnly(), snapshot, diagnostics.AsReadOnly(), false);
        }
        catch (DropFlagLimitException)
        {
            AddLimitDiagnostic(diagnostics, "Drop-flag lowering exceeded its bounded work or output limit.");
            return Failure(diagnostics, true);
        }
        catch (SafeCoreMirLimitException)
        {
            AddLimitDiagnostic(diagnostics, "Typed drop-flag place resolution exceeded its bounded work or time limit.");
            return Failure(diagnostics, true);
        }
        catch (DropFlagEvidenceException exception)
        {
            AddDiagnostic(diagnostics, InvalidEvidence, exception.Message, InvalidSource, options);
            return Failure(diagnostics, false);
        }
    }

    private static void ValidateEvidenceShape(
        SafeCoreOwnershipProgram program,
        SafeCoreOwnershipAnalysisResult ownership,
        SafeCoreMirDropFlagLoweringOptions options, Stopwatch clock, ref int operations)
    {
        if (ownership.Paths.Length > options.MaximumPaths)
            throw new DropFlagLimitException();
        var functions = new HashSet<string>(StringComparer.Ordinal);
        var pathFunctions = new HashSet<string>(
            ownership.Paths.Select(path => path.FunctionName), StringComparer.Ordinal);
        foreach (SafeCoreOwnershipFunction function in program.Functions)
        {
            Step(options, clock, ref operations);
            if (function.Locals.Count > 4096 || function.Blocks.Count > 16384) throw new DropFlagLimitException();
            if (!functions.Add(function.Name))
                throw new DropFlagEvidenceException($"Ownership function '{function.Name}' is duplicated.");

            var localIds = new HashSet<int>();
            var localNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (SafeCoreOwnershipLocal local in function.Locals)
            {
                Step(options, clock, ref operations);
                if (local.Id < 0 || !localIds.Add(local.Id))
                    throw new DropFlagEvidenceException(
                        $"Ownership function '{function.Name}' has duplicate or negative local ID {local.Id.ToString(CultureInfo.InvariantCulture)}.");
                if (string.IsNullOrWhiteSpace(local.Name) || !localNames.Add(local.Name))
                    throw new DropFlagEvidenceException(
                        $"Ownership function '{function.Name}' has duplicate or empty local name '{local.Name}'.");
            }

            if (!pathFunctions.Contains(function.Name))
                throw new DropFlagEvidenceException(
                    $"Ownership function '{function.Name}' has no path evidence.");
        }

        var pathIds = new HashSet<int>();
        foreach (SafeCoreOwnershipPath path in ownership.Paths)
        {
            Step(options, clock, ref operations);
            if (path.Trace.Length > options.MaximumEventsPerPath || path.DropOrder.Length > options.MaximumEventsPerPath)
                throw new DropFlagLimitException();
            if (path.PathId < 0 || !pathIds.Add(path.PathId))
                throw new DropFlagEvidenceException(
                    $"Drop evidence path ID {path.PathId.ToString(CultureInfo.InvariantCulture)} is duplicated or negative.");
            if (!functions.Contains(path.FunctionName))
                throw new DropFlagEvidenceException(
                    $"Drop evidence references unknown function '{path.FunctionName}'.");
            if (path.Outcome == SafeCoreOwnershipOutcome.LimitExceeded)
                throw new DropFlagEvidenceException("Drop evidence contains an incomplete limit-exceeded path.");
        }
    }

    private static SafeCoreMirDropFlagPath LowerPath(
        SafeCoreOwnershipFunction function,
        SafeCoreOwnershipPath path,
        SafeCoreMirDropFlagLoweringOptions options,
        Stopwatch clock,
        ref int operations)
    {
        var locals = function.Locals.ToDictionary(local => local.Id);
        var states = new Dictionary<int, SafeCoreDropPlaceState>();
        foreach (SafeCoreOwnershipLocal local in function.Locals)
            states[local.Id] = local.InitiallyInitialized
                ? SafeCoreDropPlaceState.Live
                : SafeCoreDropPlaceState.Uninitialized;
        var events = new List<IReadOnlyList<SafeCoreMirDropFlag>>();
        var observedDrops = new Dictionary<int, int>();
        for (int index = 0; index < path.Trace.Length; index++)
        {
            Step(options, clock, ref operations);
            string trace = path.Trace[index] ?? string.Empty;
            ApplyTrace(trace, states, locals, function, observedDrops,
                options, clock, ref operations);
            if (events.Count >= options.MaximumEventsPerPath) throw new DropFlagLimitException();
            events.Add(SnapshotFlags(states, locals, function.Source));
        }

        // DropOrder is authoritative when an older producer omitted a trace
        // event. Mark all listed places consumed before publishing the final
        // flags; unknown names are rejected rather than silently discarded.
        var dropOrderSeen = new Dictionary<int, int>();
        foreach (string name in path.DropOrder)
        {
            Step(options, clock, ref operations);
            SafeCoreOwnershipLocal local = Resolve(name, locals) ??
                throw new DropFlagEvidenceException($"Drop evidence references unknown local '{name}'.");
            if (!local.HasDrop)
                throw new DropFlagEvidenceException($"Drop evidence references non-droppable local '{local.Name}'.");
            int occurrence = dropOrderSeen.GetValueOrDefault(local.Id) + 1;
            dropOrderSeen[local.Id] = occurrence;
            if (occurrence <= observedDrops.GetValueOrDefault(local.Id))
                continue;
            // An assignment starts another live generation of the same
            // place. Repeated DropOrder entries are valid only when matching
            // explicit trace drops prove those generations were consumed.
            // A legacy missing trace may materialize one final obligation.
            if (occurrence > 1)
                throw new DropFlagEvidenceException($"Drop evidence repeats local '{local.Name}' without a matching live generation.");
            if (states[local.Id] is not (SafeCoreDropPlaceState.Live or SafeCoreDropPlaceState.PartiallyMoved))
                throw new DropFlagEvidenceException($"Drop evidence references non-live local '{local.Name}'.");
            states[local.Id] = SafeCoreDropPlaceState.Dropped;
        }

        foreach ((int localId, int count) in observedDrops)
        {
            Step(options, clock, ref operations);
            if (count != dropOrderSeen.GetValueOrDefault(localId))
                throw new DropFlagEvidenceException($"Drop trace for local '{locals[localId].Name}' is absent from DropOrder.");
        }

        if (path.Outcome == SafeCoreOwnershipOutcome.Aborted)
        {
            // Abort has no unwind Drop. Keep live flags visible to the host.
            foreach (SafeCoreOwnershipLocal local in function.Locals)
                if (states[local.Id] == SafeCoreDropPlaceState.Dropped && !path.DropOrder.Contains(local.Name, StringComparer.Ordinal))
                    states[local.Id] = SafeCoreDropPlaceState.Live;
        }

        return new(path.PathId, function.Name, path.Outcome,
            SnapshotFlags(states, locals, function.Source), events.AsReadOnly(), function.Source);
    }

    private static void ApplyTrace(
        string trace,
        Dictionary<int, SafeCoreDropPlaceState> states,
        Dictionary<int, SafeCoreOwnershipLocal> locals,
        SafeCoreOwnershipFunction function,
        Dictionary<int, int> observedDrops,
        SafeCoreMirDropFlagLoweringOptions options,
        Stopwatch clock,
        ref int operations)
    {
        if (trace.Length == 0) return;
        if (trace.StartsWith("scope_exit ", StringComparison.Ordinal))
        {
            if (!int.TryParse(trace["scope_exit ".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int scope) || scope < 0)
                throw new DropFlagEvidenceException("A scope_exit trace has an invalid scope ID.");
            foreach (SafeCoreOwnershipLocal local in function.Locals.Where(item => item.ScopeId == scope))
            {
                Step(options, clock, ref operations);
                states[local.Id] = states[local.Id] switch
                {
                    SafeCoreDropPlaceState.Live => SafeCoreDropPlaceState.Dropped,
                    SafeCoreDropPlaceState.PartiallyMoved => SafeCoreDropPlaceState.Dropped,
                    SafeCoreDropPlaceState.Uninitialized or SafeCoreDropPlaceState.Moved or SafeCoreDropPlaceState.Dropped => SafeCoreDropPlaceState.Dropped,
                    _ => states[local.Id],
                };
            }
            return;
        }

        string? name = EventName(trace, "drop ") ?? EventName(trace, "return_move ") ??
            EventName(trace, "consume ");
        if (name is not null)
        {
            SafeCoreOwnershipLocal local = Resolve(name, locals) ??
                throw new DropFlagEvidenceException($"Drop trace references unknown local '{name}'.");
            if (trace.StartsWith("drop ", StringComparison.Ordinal))
            {
                if (states[local.Id] is not (SafeCoreDropPlaceState.Live or SafeCoreDropPlaceState.PartiallyMoved))
                    throw new DropFlagEvidenceException(
                        $"Drop trace repeats or consumes non-live local '{local.Name}'.");
                states[local.Id] = SafeCoreDropPlaceState.Dropped;
                observedDrops[local.Id] = observedDrops.GetValueOrDefault(local.Id) + 1;
            }
            else
            {
                if (states[local.Id] != SafeCoreDropPlaceState.Live)
                    throw new DropFlagEvidenceException(
                        $"Move/consume trace references non-live local '{local.Name}'.");
                states[local.Id] = SafeCoreDropPlaceState.Moved;
            }
            return;
        }

        if (trace.StartsWith("move ", StringComparison.Ordinal))
        {
            string body = trace["move ".Length..];
            int separator = body.IndexOf(" -> ", StringComparison.Ordinal);
            if (separator <= 0) throw new DropFlagEvidenceException("A move trace has no source place.");
            SafeCoreOwnershipLocal local = Resolve(body[..separator], locals) ??
                throw new DropFlagEvidenceException($"Move trace references unknown local '{body[..separator]}'.");
            string destinationName = body[(separator + " -> ".Length)..];
            SafeCoreOwnershipLocal destination = Resolve(destinationName, locals) ??
                throw new DropFlagEvidenceException(
                    $"Move trace references unknown destination '{destinationName}'.");
            if (destination.Id == local.Id)
                throw new DropFlagEvidenceException("A move trace cannot use the same source and destination place.");
            if (states[local.Id] != SafeCoreDropPlaceState.Live)
                throw new DropFlagEvidenceException(
                    $"Move trace references non-live local '{local.Name}'.");
            states[local.Id] = SafeCoreDropPlaceState.Moved;
            states[destination.Id] = SafeCoreDropPlaceState.Live;
            return;
        }

        if (trace.StartsWith("copy_ref ", StringComparison.Ordinal))
        {
            string body = trace["copy_ref ".Length..];
            int separator = body.IndexOf(" -> ", StringComparison.Ordinal);
            if (separator <= 0 || separator + " -> ".Length >= body.Length)
                throw new DropFlagEvidenceException("A copy_ref trace has no complete source and destination.");
            SafeCoreOwnershipLocal source = Resolve(body[..separator], locals) ??
                throw new DropFlagEvidenceException($"Copy trace references unknown local '{body[..separator]}'.");
            string destinationName = body[(separator + " -> ".Length)..];
            SafeCoreOwnershipLocal destination = Resolve(destinationName, locals) ??
                throw new DropFlagEvidenceException($"Copy trace references unknown destination '{destinationName}'.");
            if (source.Id == destination.Id)
                throw new DropFlagEvidenceException("A copy_ref trace cannot use the same source and destination place.");
            if (states[source.Id] is SafeCoreDropPlaceState.Uninitialized or SafeCoreDropPlaceState.Moved)
                throw new DropFlagEvidenceException($"Copy trace references non-live local '{source.Name}'.");
            states[destination.Id] = SafeCoreDropPlaceState.Live;
            return;
        }

        if (trace.StartsWith("assign ", StringComparison.Ordinal) || trace.StartsWith("write ", StringComparison.Ordinal))
        {
            string body = trace[(trace.IndexOf(' ') + 1)..];
            int scopeSuffix = body.IndexOf(" (scope)", StringComparison.Ordinal);
            if (scopeSuffix >= 0) body = body[..scopeSuffix];
            SafeCoreOwnershipLocal local = Resolve(body, locals) ??
                throw new DropFlagEvidenceException($"Assignment trace references unknown local '{body}'.");
            states[local.Id] = SafeCoreDropPlaceState.Live;
            return;
        }

        if (trace is "panic_abort" or "panic_unwind" or "return" or "unreachable" ||
            trace.StartsWith("branch ", StringComparison.Ordinal) ||
            trace.StartsWith("use ", StringComparison.Ordinal) ||
            trace.StartsWith("use_borrow ", StringComparison.Ordinal) ||
            trace.StartsWith("borrow ", StringComparison.Ordinal) ||
            trace.StartsWith("borrow_mut ", StringComparison.Ordinal) ||
            trace.StartsWith("end_borrow ", StringComparison.Ordinal) ||
            trace.StartsWith("nll_end ", StringComparison.Ordinal) ||
            trace.StartsWith("copy_ref ", StringComparison.Ordinal) ||
            trace.StartsWith("return_move ", StringComparison.Ordinal)) return;

        // A panic-abort path is terminal, so it should not carry producer
        // events after the boundary. Unknown events are evidence corruption
        // for every outcome because they could hide a destructor transition.
        throw new DropFlagEvidenceException($"Unsupported drop-flag trace event '{trace}'.");
    }

    private static string? EventName(string trace, string prefix) =>
        trace.StartsWith(prefix, StringComparison.Ordinal) && trace.Length > prefix.Length
            ? trace[prefix.Length..].Replace(" (scope)", string.Empty, StringComparison.Ordinal)
            : null;

    private static SafeCoreOwnershipLocal? Resolve(string name, Dictionary<int, SafeCoreOwnershipLocal> locals)
    {
        int scopeSuffix = name.IndexOf(" (scope)", StringComparison.Ordinal);
        if (scopeSuffix >= 0) name = name[..scopeSuffix];
        if (locals.Values.FirstOrDefault(local => string.Equals(local.Name, name, StringComparison.Ordinal)) is { } local)
            return local;
        int projection = name.IndexOfAny(['.', '[']);
        if (projection > 0) name = name[..projection];
        return locals.Values.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
    }

    private static ReadOnlyCollection<SafeCoreMirDropFlag> SnapshotFlags(
        Dictionary<int, SafeCoreDropPlaceState> states,
        Dictionary<int, SafeCoreOwnershipLocal> locals,
        SafeCoreMirSource fallback)
    {
        return Array.AsReadOnly<SafeCoreMirDropFlag>([.. locals.Values.OrderBy(local => local.Id).Select(local =>
            new SafeCoreMirDropFlag(local.Id, local.Name, states[local.Id],
                local.HasDrop && states[local.Id] is
                    (SafeCoreDropPlaceState.Live or SafeCoreDropPlaceState.PartiallyMoved),
                local.Source))]);
    }

    private static string Format(
        IReadOnlyList<SafeCoreMirDropFlagPath> paths,
        SafeCoreMirDropFlagLoweringOptions options,
        Stopwatch clock,
        ref int operations)
    {
        var text = new StringBuilder(Profile).Append('\n');
        foreach (SafeCoreMirDropFlagPath path in paths.OrderBy(path => path.PathId))
        {
            Step(options, clock, ref operations);
            Append(text, $"path #{path.PathId} {path.Outcome.ToString().ToLowerInvariant()} fn={Escape(path.FunctionName)}\n",
                options, clock, ref operations);
            foreach (SafeCoreMirDropFlag flag in path.FinalFlags)
            {
                Step(options, clock, ref operations);
                Append(text, FormattableString.Invariant($"  %{flag.LocalId} {Escape(flag.LocalName)} state={flag.State.ToString().ToLowerInvariant()} eligible={(flag.Eligible ? "true" : "false")}{(flag.GuardCondition is null ? string.Empty : " guard=" + Escape(flag.GuardCondition))}\n"),
                    options, clock, ref operations);
            }
        }

        return text.ToString();

        static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal);
    }

    private static void Append(StringBuilder text, string value,
        SafeCoreMirDropFlagLoweringOptions options, Stopwatch clock, ref int operations)
    {
        Step(options, clock, ref operations);
        if ((long)text.Length + value.Length > options.MaximumCharacters) throw new DropFlagLimitException();
        text.Append(value);
    }

    private static void ValidateOptions(SafeCoreMirDropFlagLoweringOptions options)
    {
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1) ||
            options.MaximumOperations is < 1 or > 4_000_000 || options.MaximumPaths is < 1 or > 65_536 ||
            options.MaximumEventsPerPath is < 1 or > 65_536 || options.MaximumCharacters is < 1 or > 4_000_000)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static void Step(SafeCoreMirDropFlagLoweringOptions options, Stopwatch clock, ref int operations)
    {
        options.CancellationToken.ThrowIfCancellationRequested();
        if (++operations > options.MaximumOperations || clock.Elapsed >= options.Timeout) throw new DropFlagLimitException();
    }

    private static SafeCoreMirDropFlagResult Failure(List<Diagnostic> diagnostics, bool truncated) =>
        new(Array.Empty<SafeCoreMirDropFlagPath>(), null, diagnostics.AsReadOnly(), truncated);

    private static void AddDiagnostic(List<Diagnostic> diagnostics, string code, string message,
        SafeCoreMirSource source, SafeCoreMirDropFlagLoweringOptions options)
    {
        if (diagnostics.Count < 256) diagnostics.Add(new Diagnostic(code, message, source.Span) { SourcePath = source.SourcePath });
    }

    private static void AddLimitDiagnostic(List<Diagnostic> diagnostics, string message)
    {
        if (diagnostics.Count < 256) diagnostics.Add(new Diagnostic(LimitReached, message, new TextSpan(0, 0)));
    }

    private static readonly SafeCoreMirSource InvalidSource = new("<invalid>", new TextSpan(0, 0), 0, 0);
    private sealed class DropFlagLimitException : Exception;
    private sealed class DropFlagEvidenceException(string message) : Exception(message);
}
