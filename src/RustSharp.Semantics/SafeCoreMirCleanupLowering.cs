using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>
/// The finite cleanup events that are emitted by the P1-08 projection.  This
/// is an evidence format, not a replacement for CLR exception handling or a
/// claim that every Rust destructor can already be lowered.
/// </summary>
public enum SafeCoreMirCleanupActionKind
{
    ScopeExit,
    EndBorrow,
    Drop,
    ReturnBoundary,
    PanicBoundary,
    UnreachableBoundary,
}

public enum SafeCoreMirCleanupExitKind
{
    Returned,
    Unwound,
    Aborted,
    Unreachable,
}

/// <summary>One source-correlated cleanup event in a lowered ownership path.</summary>
public sealed record SafeCoreMirCleanupAction(
    SafeCoreMirCleanupActionKind Kind,
    int ScopeId,
    int LocalId,
    string? LocalName,
    SafeCoreMirSource Source)
{
    public static SafeCoreMirCleanupAction ScopeExit(int scopeId, SafeCoreMirSource source) =>
        new(SafeCoreMirCleanupActionKind.ScopeExit, scopeId, -1, null, source);

    public static SafeCoreMirCleanupAction EndBorrow(int localId, string localName, SafeCoreMirSource source) =>
        new(SafeCoreMirCleanupActionKind.EndBorrow, -1, localId, localName, source);

    public static SafeCoreMirCleanupAction Drop(int localId, string localName, SafeCoreMirSource source) =>
        new(SafeCoreMirCleanupActionKind.Drop, -1, localId, localName, source);

    public static SafeCoreMirCleanupAction Boundary(
        SafeCoreMirCleanupActionKind kind, SafeCoreMirSource source) =>
        kind is SafeCoreMirCleanupActionKind.ReturnBoundary or
            SafeCoreMirCleanupActionKind.PanicBoundary or
            SafeCoreMirCleanupActionKind.UnreachableBoundary
            ? new(kind, -1, -1, null, source)
            : throw new ArgumentOutOfRangeException(nameof(kind));
}

/// <summary>Cleanup evidence for one bounded ownership-analysis path.</summary>
public sealed record SafeCoreMirCleanupPath
{
    public SafeCoreMirCleanupPath(
        int pathId,
        string functionName,
        SafeCoreMirCleanupExitKind exitKind,
        SafeCorePanicStrategy panicStrategy,
        IReadOnlyList<SafeCoreMirCleanupAction> actions,
        SafeCoreMirSource source)
    {
        PathId = pathId;
        FunctionName = functionName;
        ExitKind = exitKind;
        PanicStrategy = panicStrategy;
        Actions = Freeze(actions, nameof(actions));
        Source = source;
    }

    public int PathId { get; }
    public string FunctionName { get; }
    public SafeCoreMirCleanupExitKind ExitKind { get; }
    public SafeCorePanicStrategy PanicStrategy { get; }
    public IReadOnlyList<SafeCoreMirCleanupAction> Actions { get; }
    public SafeCoreMirSource Source { get; }

    private static ReadOnlyCollection<SafeCoreMirCleanupAction> Freeze(
        IReadOnlyList<SafeCoreMirCleanupAction> actions, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(actions, parameterName);
        if (actions.Count > 65_536)
            throw new ArgumentException("Cleanup action count exceeds its bound.", parameterName);
        var copy = new SafeCoreMirCleanupAction[actions.Count];
        for (int index = 0; index < copy.Length; index++)
            copy[index] = actions[index] ?? throw new ArgumentException(
                "Cleanup actions cannot contain null values.", parameterName);
        return Array.AsReadOnly(copy);
    }
}

/// <summary>Cleanup paths and panic policy for one emitted function.</summary>
public sealed record SafeCoreMirCleanupFunction
{
    public SafeCoreMirCleanupFunction(
        string name,
        SafeCorePanicStrategy panicStrategy,
        IReadOnlyList<SafeCoreMirCleanupPath> paths,
        SafeCoreMirSource source)
    {
        Name = name;
        PanicStrategy = panicStrategy;
        Paths = Freeze(paths, nameof(paths));
        Source = source;
    }

    public string Name { get; }
    public SafeCorePanicStrategy PanicStrategy { get; }
    public IReadOnlyList<SafeCoreMirCleanupPath> Paths { get; }
    public SafeCoreMirSource Source { get; }

    private static ReadOnlyCollection<SafeCoreMirCleanupPath> Freeze(
        IReadOnlyList<SafeCoreMirCleanupPath> paths, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(paths, parameterName);
        if (paths.Count is < 1 or > 65_536)
            throw new ArgumentException("Cleanup path count is out of bounds.", parameterName);
        var copy = new SafeCoreMirCleanupPath[paths.Count];
        for (int index = 0; index < copy.Length; index++)
            copy[index] = paths[index] ?? throw new ArgumentException(
                "Cleanup paths cannot contain null values.", parameterName);
        return Array.AsReadOnly(copy);
    }
}

/// <summary>Bounded options for compiler-integrated cleanup projection.</summary>
public sealed record SafeCoreMirCleanupLoweringOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumOperations { get; init; } = 1_000_000;
    public int MaximumFunctions { get; init; } = 4_096;
    public int MaximumPaths { get; init; } = 65_536;
    public int MaximumActionsPerPath { get; init; } = 65_536;
    public int MaximumCharacters { get; init; } = 4_000_000;
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Structured P1-08 cleanup evidence and its deterministic snapshot.</summary>
public sealed record SafeCoreMirCleanupResult(
    IReadOnlyList<SafeCoreMirCleanupFunction> Functions,
    string? Snapshot,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool IsTruncated)
{
    public bool IsSuccessful => !IsTruncated && Diagnostics.Count == 0 &&
        Functions.Count > 0 && Snapshot is not null;
}

/// <summary>
/// Projects already-validated ownership paths into explicit cleanup events.
/// The projection is deliberately separate from typed-MIR lowering: it never
/// invents ownership facts and never executes a destructor.  It is suitable
/// for compiler metadata, debugger snapshots, and a later IL/AOT emitter.
/// </summary>
public static class SafeCoreMirCleanupLowering
{
    public const string Profile = "safe-core-mir-cleanup-p1-v1";
    public const string InvalidEvidence = "RSM4001";
    public const string Unsupported = "RSM4002";
    public const string LimitReached = "RSM4003";

    public static SafeCoreMirCleanupResult Lower(
        SafeCoreMirProgram mir,
        SafeCoreMirOwnershipResult ownership,
        SafeCoreMirCleanupLoweringOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mir);
        ArgumentNullException.ThrowIfNull(ownership);
        options ??= new();
        ValidateOptions(options);
        options.CancellationToken.ThrowIfCancellationRequested();

        var clock = Stopwatch.StartNew();
        int operations = 0;
        var diagnostics = new List<Diagnostic>();
        try
        {
            if (!ownership.IsSuccessful || ownership.Program is null || ownership.Ownership is null)
            {
                AddDiagnostic(diagnostics, InvalidEvidence,
                    "Cleanup lowering requires successful typed-MIR ownership evidence.",
                    mir.Functions.Count > 0 ? mir.Functions[0].Source : InvalidSource,
                    options);
                return Failure(diagnostics, false);
            }

            SafeCoreMirValidationResult mirValidation = SafeCoreMirValidation.Validate(mir,
                new SafeCoreMirValidationOptions
                {
                    Timeout = options.Timeout,
                    MaximumOperations = options.MaximumOperations,
                    MaximumFunctions = options.MaximumFunctions,
                    MaximumDiagnostics = Math.Min(256, options.MaximumFunctions),
                    CancellationToken = options.CancellationToken,
                });
            if (!mirValidation.IsSuccessful)
            {
                foreach (SafeCoreMirDiagnostic diagnostic in mirValidation.Diagnostics)
                    AddDiagnostic(diagnostics, diagnostic.Code, diagnostic.Message,
                        diagnostic.Source ?? InvalidSource, options);
                return Failure(diagnostics, mirValidation.IsTruncated);
            }

            // Validation is part of this projection's bounded work. Charge its
            // actual steps before cleanup traversal so a single call cannot
            // spend the full operation budget once in validation and again in
            // projection.
            operations = mirValidation.OperationsUsed;

            if (mir.Functions.Count != ownership.Program.Functions.Count)
            {
                AddDiagnostic(diagnostics, InvalidEvidence,
                    "Cleanup ownership and typed-MIR function counts do not match.",
                    mir.Functions.Count > 0 ? mir.Functions[0].Source : InvalidSource,
                    options);
                return Failure(diagnostics, false);
            }

            var functions = new List<SafeCoreMirCleanupFunction>(mir.Functions.Count);
            var seenPaths = new HashSet<int>();
            for (int functionIndex = 0; functionIndex < mir.Functions.Count; functionIndex++)
            {
                Step(options, clock, ref operations);
                if (functionIndex >= options.MaximumFunctions)
                    throw new CleanupLimitException();

                SafeCoreMirFunction mirFunction = mir.Functions[functionIndex];
                SafeCoreOwnershipFunction ownershipFunction = ownership.Program.Functions[functionIndex];
                if (!string.Equals(mirFunction.Name, ownershipFunction.Name, StringComparison.Ordinal) ||
                    !SameSource(mirFunction.Source, ownershipFunction.Source))
                {
                    AddDiagnostic(diagnostics, InvalidEvidence,
                        $"Cleanup ownership function '{ownershipFunction.Name}' does not match typed MIR function '{mirFunction.Name}'.",
                        ownershipFunction.Source, options);
                    continue;
                }

                var localsByName = BuildLocalMap(ownershipFunction.Locals);
                SafeCoreOwnershipPath[] paths = [.. ownership.Ownership.Paths
                    .Where(path => string.Equals(path.FunctionName, ownershipFunction.Name, StringComparison.Ordinal))
                    .OrderBy(path => path.PathId)];
                if (paths.Length == 0)
                {
                    AddDiagnostic(diagnostics, InvalidEvidence,
                        $"Ownership function '{ownershipFunction.Name}' has no cleanup path evidence.",
                        ownershipFunction.Source, options);
                    continue;
                }

                var loweredPaths = new List<SafeCoreMirCleanupPath>(paths.Length);
                foreach (SafeCoreOwnershipPath path in paths)
                {
                    Step(options, clock, ref operations);
                    if (!seenPaths.Add(path.PathId))
                    {
                        AddDiagnostic(diagnostics, InvalidEvidence,
                            $"Ownership path ID {path.PathId.ToString(CultureInfo.InvariantCulture)} is duplicated.",
                            ownershipFunction.Source, options);
                        continue;
                    }
                    if (loweredPaths.Count >= options.MaximumPaths)
                        throw new CleanupLimitException();
                    SafeCoreMirCleanupPath? lowered = LowerPath(
                        path, ownershipFunction, localsByName, options, clock, ref operations, diagnostics);
                    if (lowered is not null) loweredPaths.Add(lowered);
                }

                if (loweredPaths.Count > 0)
                    functions.Add(new SafeCoreMirCleanupFunction(
                        ownershipFunction.Name,
                        ownershipFunction.PanicStrategy,
                        loweredPaths,
                        ownershipFunction.Source));
            }

            if (diagnostics.Count != 0)
                return Failure(diagnostics, false);

            string snapshot = Format(functions, options, clock, ref operations);
            return new(functions.AsReadOnly(), snapshot, diagnostics.AsReadOnly(), false);
        }
        catch (CleanupLimitException)
        {
            AddLimitDiagnostic(diagnostics,
                "Cleanup lowering exceeded its bounded work, path, action, character or time limit.",
                mir.Functions.Count > 0 ? mir.Functions[0].Source : InvalidSource);
            return Failure(diagnostics, true);
        }
        catch (CleanupEvidenceException exception)
        {
            AddLimitSafeDiagnostic(diagnostics, InvalidEvidence, exception.Message,
                exception.EvidenceSource, options);
            return Failure(diagnostics, false);
        }
    }

    private static SafeCoreMirCleanupPath? LowerPath(
        SafeCoreOwnershipPath path,
        SafeCoreOwnershipFunction function,
        Dictionary<string, SafeCoreOwnershipLocal> localsByName,
        SafeCoreMirCleanupLoweringOptions options,
        Stopwatch clock,
        ref int operations,
        List<Diagnostic> diagnostics)
    {
        SafeCoreMirCleanupExitKind exitKind = path.Outcome switch
        {
            SafeCoreOwnershipOutcome.Returned => SafeCoreMirCleanupExitKind.Returned,
            SafeCoreOwnershipOutcome.Unwound => SafeCoreMirCleanupExitKind.Unwound,
            SafeCoreOwnershipOutcome.Aborted => SafeCoreMirCleanupExitKind.Aborted,
            SafeCoreOwnershipOutcome.Unreachable => SafeCoreMirCleanupExitKind.Unreachable,
            _ => throw new CleanupEvidenceException(
                "Ownership analysis produced a non-terminal cleanup outcome.", function.Source),
        };

        if (exitKind == SafeCoreMirCleanupExitKind.Unwound &&
            function.PanicStrategy != SafeCorePanicStrategy.Unwind)
        {
            AddDiagnostic(diagnostics, InvalidEvidence,
                "An unwound path requires the function's unwind panic strategy.", function.Source, options);
            return null;
        }
        if (exitKind == SafeCoreMirCleanupExitKind.Aborted &&
            function.PanicStrategy != SafeCorePanicStrategy.Abort)
        {
            AddDiagnostic(diagnostics, InvalidEvidence,
                "An aborted path requires the function's abort panic strategy.", function.Source, options);
            return null;
        }

        var actions = new List<SafeCoreMirCleanupAction>(Math.Min(path.Trace.Length, options.MaximumActionsPerPath));
        var observedDrops = new HashSet<string>(StringComparer.Ordinal);
        bool hasScopeExit = false;
        bool hasBoundary = false;
        for (int traceIndex = 0; traceIndex < path.Trace.Length; traceIndex++)
        {
            Step(options, clock, ref operations);
            string trace = path.Trace[traceIndex] ?? string.Empty;
            if (trace.Length == 0) continue;
            if (actions.Count >= options.MaximumActionsPerPath)
                throw new CleanupLimitException();

            if (TryParseScopeExit(trace, out int scopeId))
            {
                SafeCoreOwnershipScope? scope = function.Scopes.FirstOrDefault(item => item.Id == scopeId);
                actions.Add(SafeCoreMirCleanupAction.ScopeExit(scopeId, scope?.Source ?? function.Source));
                hasScopeExit = true;
                continue;
            }

            if (TryParseLocalEvent(trace, "drop ", localsByName, out SafeCoreOwnershipLocal? drop) && drop is not null)
            {
                actions.Add(SafeCoreMirCleanupAction.Drop(drop.Id, drop.Name, drop.Source));
                observedDrops.Add(drop.Name);
                continue;
            }

            if ((TryParseLocalEvent(trace, "end_borrow ", localsByName, out SafeCoreOwnershipLocal? endBorrow) ||
                TryParseLocalEvent(trace, "nll_end ", localsByName, out endBorrow)) && endBorrow is not null)
            {
                actions.Add(SafeCoreMirCleanupAction.EndBorrow(endBorrow.Id, endBorrow.Name, endBorrow.Source));
                continue;
            }

            if (trace.Equals("panic_unwind", StringComparison.Ordinal))
            {
                actions.Add(SafeCoreMirCleanupAction.Boundary(
                    SafeCoreMirCleanupActionKind.PanicBoundary, function.Source));
                hasBoundary = true;
                continue;
            }
            if (trace.Equals("panic_abort", StringComparison.Ordinal))
            {
                actions.Add(SafeCoreMirCleanupAction.Boundary(
                    SafeCoreMirCleanupActionKind.PanicBoundary, function.Source));
                hasBoundary = true;
                continue;
            }
            if (trace.Equals("return", StringComparison.Ordinal))
            {
                actions.Add(SafeCoreMirCleanupAction.Boundary(
                    SafeCoreMirCleanupActionKind.ReturnBoundary, function.Source));
                hasBoundary = true;
                continue;
            }
            if (trace.Equals("unreachable", StringComparison.Ordinal))
            {
                actions.Add(SafeCoreMirCleanupAction.Boundary(
                    SafeCoreMirCleanupActionKind.UnreachableBoundary, function.Source));
                hasBoundary = true;
                continue;
            }

            // Ownership also records semantic events that are consumed by the
            // borrow/move analysis, but which do not create cleanup actions.
            // Keep this allow-list explicit and validate the bounded shape so
            // a future producer event cannot disappear silently.
            if (IsKnownNonCleanupTrace(trace))
                continue;

            // Ownership traces are a versioned producer/consumer contract.
            // Never ignore an event that this cleanup profile cannot lower:
            // silently dropping it would turn incomplete destructor evidence
            // into an apparently successful cleanup path.
            AddDiagnostic(diagnostics, Unsupported,
                $"Cleanup ownership trace event '{trace}' is outside the supported P1-08 lowering contract.",
                function.Source, options);
            return null;
        }

        // DropOrder is a compact, stable ownership fact. Older ownership
        // producers may omit individual `drop` trace entries, so materialize
        // any missing drops immediately before the terminal boundary.
        foreach (string dropName in path.DropOrder)
        {
            Step(options, clock, ref operations);
            if (observedDrops.Contains(dropName)) continue;
            if (!localsByName.TryGetValue(dropName, out SafeCoreOwnershipLocal? local))
            {
                AddDiagnostic(diagnostics, Unsupported,
                    $"Cleanup drop evidence references unknown local '{dropName}'.", function.Source, options);
                return null;
            }
            if (actions.Count >= options.MaximumActionsPerPath)
                throw new CleanupLimitException();
            int boundaryIndex = actions.FindIndex(action =>
                action.Kind is SafeCoreMirCleanupActionKind.ReturnBoundary or SafeCoreMirCleanupActionKind.PanicBoundary or
                SafeCoreMirCleanupActionKind.UnreachableBoundary);
            SafeCoreMirCleanupAction drop = SafeCoreMirCleanupAction.Drop(local.Id, local.Name, local.Source);
            if (boundaryIndex < 0) actions.Add(drop);
            else actions.Insert(boundaryIndex, drop);
            observedDrops.Add(dropName);
        }

        if (exitKind is SafeCoreMirCleanupExitKind.Returned or SafeCoreMirCleanupExitKind.Unwound)
        {
            if (!hasScopeExit)
            {
                AddDiagnostic(diagnostics, Unsupported,
                    "A returning or unwinding cleanup path must retain at least one scope_exit event.",
                    function.Source, options);
                return null;
            }
        }

        if (!hasBoundary)
        {
            SafeCoreMirCleanupActionKind boundaryKind = exitKind switch
            {
                SafeCoreMirCleanupExitKind.Returned => SafeCoreMirCleanupActionKind.ReturnBoundary,
                SafeCoreMirCleanupExitKind.Unwound or SafeCoreMirCleanupExitKind.Aborted => SafeCoreMirCleanupActionKind.PanicBoundary,
                _ => SafeCoreMirCleanupActionKind.UnreachableBoundary,
            };
            if (actions.Count >= options.MaximumActionsPerPath)
                throw new CleanupLimitException();
            actions.Add(SafeCoreMirCleanupAction.Boundary(boundaryKind, function.Source));
        }

        return new SafeCoreMirCleanupPath(path.PathId, function.Name, exitKind,
            function.PanicStrategy, actions, function.Source);
    }

    private static Dictionary<string, SafeCoreOwnershipLocal> BuildLocalMap(
        IReadOnlyList<SafeCoreOwnershipLocal> locals)
    {
        var result = new Dictionary<string, SafeCoreOwnershipLocal>(StringComparer.Ordinal);
        foreach (SafeCoreOwnershipLocal local in locals)
        {
            // Ownership validation already requires dense IDs, but names are
            // source-level identities. Keep the first deterministic binding if
            // an older producer contains a shadowing name.
            result.TryAdd(local.Name, local);
            result.TryAdd(local.Id.ToString(CultureInfo.InvariantCulture), local);
        }
        return result;
    }

    private static bool TryParseScopeExit(string trace, out int scopeId)
    {
        const string prefix = "scope_exit ";
        if (!trace.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(trace[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out scopeId) ||
            scopeId < 0)
        {
            scopeId = -1;
            return false;
        }

        return true;
    }

    private static bool TryParseLocalEvent(
        string trace,
        string prefix,
        Dictionary<string, SafeCoreOwnershipLocal> locals,
        out SafeCoreOwnershipLocal? local)
    {
        local = null;
        if (!trace.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string name = trace[prefix.Length..];
        int suffix = name.IndexOf(" (scope)", StringComparison.Ordinal);
        if (suffix >= 0) name = name[..suffix];
        // Projected places retain a source display such as `owner.field`; the
        // cleanup projection records the owning root while preserving source
        // correlation through the local fact.
        int projection = name.IndexOfAny(['.', '[']);
        if (projection > 0) name = name[..projection];
        return locals.TryGetValue(name, out local);
    }

    private static bool IsKnownNonCleanupTrace(string trace)
    {
        const StringComparison comparison = StringComparison.Ordinal;

        if (trace.StartsWith("branch ", comparison))
        {
            return int.TryParse(
                trace["branch ".Length..], NumberStyles.None, CultureInfo.InvariantCulture,
                out int target) && target >= 0;
        }

        if (trace.StartsWith("return_move ", comparison))
            return trace["return_move ".Length..].Length > 0;

        if (trace.StartsWith("use_borrow ", comparison) ||
            trace.StartsWith("use ", comparison) ||
            trace.StartsWith("assign ", comparison) ||
            trace.StartsWith("write ", comparison))
        {
            int separator = trace.IndexOf(' ');
            return separator >= 0 && trace[(separator + 1)..].Length > 0;
        }

        if (trace.StartsWith("move ", comparison))
        {
            string body = trace["move ".Length..];
            int separator = body.IndexOf(" -> ", comparison);
            return separator > 0 && separator + " -> ".Length < body.Length;
        }

        if (trace.StartsWith("copy_ref ", comparison))
        {
            string body = trace["copy_ref ".Length..];
            int separator = body.IndexOf(" -> ", comparison);
            return separator > 0 && separator + " -> ".Length < body.Length;
        }

        if (trace.StartsWith("borrow_mut ", comparison) ||
            trace.StartsWith("borrow ", comparison))
        {
            int separator = trace.IndexOf(" as ", comparison);
            return separator > 0 && separator + " as ".Length < trace.Length;
        }

        return false;
    }

    private static string Format(
        IReadOnlyList<SafeCoreMirCleanupFunction> functions,
        SafeCoreMirCleanupLoweringOptions options,
        Stopwatch clock,
        ref int operations)
    {
        var text = new StringBuilder();
        int operationCount = operations;
        Add("safe-core-mir-cleanup-p1-v1\n");
        foreach (SafeCoreMirCleanupFunction function in functions)
        {
            Step(options, clock, ref operationCount);
            Add(FormattableString.Invariant($"fn {Escape(function.Name)} panic={function.PanicStrategy.ToString().ToLowerInvariant()} "));
            Source(function.Source);
            Add(" {\n");
            foreach (SafeCoreMirCleanupPath path in function.Paths.OrderBy(item => item.PathId))
            {
                Step(options, clock, ref operationCount);
                Add(FormattableString.Invariant($"  path #{path.PathId} {path.ExitKind.ToString().ToLowerInvariant()} "));
                Source(path.Source);
                Add("\n");
                foreach (SafeCoreMirCleanupAction action in path.Actions)
                {
                    Step(options, clock, ref operationCount);
                    Add("    ");
                    Add(ActionName(action.Kind));
                    if (action.ScopeId >= 0) Add(FormattableString.Invariant($" scope={action.ScopeId}"));
                    if (action.LocalId >= 0)
                        Add(FormattableString.Invariant($" %{action.LocalId} {Escape(action.LocalName ?? string.Empty)}"));
                    Add(" ");
                    Source(action.Source);
                    Add("\n");
                }
            }
            Add("}\n");
        }

        operations = operationCount;
        return text.ToString();

        void Add(string value)
        {
            Step(options, clock, ref operationCount);
            if ((long)text.Length + value.Length > options.MaximumCharacters)
                throw new CleanupLimitException();
            text.Append(value);
        }

        void Source(SafeCoreMirSource source) => Add(FormattableString.Invariant(
            $"[{Escape(source.SourcePath)}:{source.Span.Start}+{source.Span.Length}/{source.SourceLength} hir#{source.HirNodeId}]")
        );

        string Escape(string value)
        {
            Step(options, clock, ref operationCount);
            return value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal)
                .Replace("[", "\\[", StringComparison.Ordinal)
                .Replace("]", "\\]", StringComparison.Ordinal);
        }
    }

    private static bool SameSource(SafeCoreMirSource expected, SafeCoreMirSource actual) =>
        string.Equals(expected.SourcePath, actual.SourcePath, StringComparison.Ordinal) &&
        expected.Span == actual.Span && expected.HirNodeId == actual.HirNodeId &&
        expected.SourceLength == actual.SourceLength;

    private static string ActionName(SafeCoreMirCleanupActionKind kind) => kind switch
    {
        SafeCoreMirCleanupActionKind.ScopeExit => "scope_exit",
        SafeCoreMirCleanupActionKind.EndBorrow => "end_borrow",
        SafeCoreMirCleanupActionKind.Drop => "drop",
        SafeCoreMirCleanupActionKind.ReturnBoundary => "return_boundary",
        SafeCoreMirCleanupActionKind.PanicBoundary => "panic_boundary",
        SafeCoreMirCleanupActionKind.UnreachableBoundary => "unreachable_boundary",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static SafeCoreMirCleanupResult Failure(List<Diagnostic> diagnostics, bool truncated) =>
        new(Array.Empty<SafeCoreMirCleanupFunction>(), null, diagnostics.AsReadOnly(), truncated);

    private static readonly SafeCoreMirSource InvalidSource =
        new("<invalid>", new TextSpan(0, 0), 0, 0);

    private static void AddDiagnostic(
        List<Diagnostic> diagnostics,
        string code,
        string message,
        SafeCoreMirSource source,
        SafeCoreMirCleanupLoweringOptions options)
    {
        if (diagnostics.Count >= Math.Min(256, options.MaximumFunctions))
            throw new CleanupLimitException();
        diagnostics.Add(new Diagnostic(code, message, source.Span) { SourcePath = source.SourcePath });
    }

    private static void AddLimitDiagnostic(
        List<Diagnostic> diagnostics,
        string message,
        SafeCoreMirSource source)
    {
        if (diagnostics.Count < 256)
            diagnostics.Add(new Diagnostic(LimitReached, message, source.Span)
            {
                SourcePath = source.SourcePath,
            });
    }

    private static void AddLimitSafeDiagnostic(
        List<Diagnostic> diagnostics,
        string code,
        string message,
        SafeCoreMirSource source,
        SafeCoreMirCleanupLoweringOptions options)
    {
        if (diagnostics.Count < Math.Min(256, options.MaximumFunctions))
            diagnostics.Add(new Diagnostic(code, message, source.Span)
            {
                SourcePath = source.SourcePath,
            });
    }

    private static void ValidateOptions(SafeCoreMirCleanupLoweringOptions options)
    {
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1) ||
            options.MaximumOperations is < 1 or > 4_000_000 ||
            options.MaximumFunctions is < 1 or > 4_096 ||
            options.MaximumPaths is < 1 or > 65_536 ||
            options.MaximumActionsPerPath is < 1 or > 65_536 ||
            options.MaximumCharacters is < 1 or > 4_000_000)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static void Step(
        SafeCoreMirCleanupLoweringOptions options,
        Stopwatch clock,
        ref int operations)
    {
        options.CancellationToken.ThrowIfCancellationRequested();
        if (++operations > options.MaximumOperations || clock.Elapsed >= options.Timeout)
            throw new CleanupLimitException();
    }

    private sealed class CleanupLimitException : Exception;

    private sealed class CleanupEvidenceException(string message, SafeCoreMirSource source)
        : Exception(message)
    {
        public SafeCoreMirSource EvidenceSource { get; } = source;
    }
}
