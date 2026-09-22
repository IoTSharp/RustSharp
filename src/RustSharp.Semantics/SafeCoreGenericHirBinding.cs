using System.Collections.Immutable;
using System.Diagnostics;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>Bounds for the source-to-HIR generic binding bridge.</summary>
public sealed record SafeCoreGenericHirBindingOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
    public CancellationToken CancellationToken { get; init; }
    public int MaximumFunctions { get; init; } = 256;
    public int MaximumCalls { get; init; } = 4096;
    public int MaximumNestingDepth { get; init; } = 128;
    public int MaximumOperations { get; init; } = 100_000;
    public int MaximumDiagnostics { get; init; } = 128;
    public GenericAnalysisLimits GenericLimits { get; init; } = new();
    public IReadOnlyList<GenericTraitImplementation> TraitImplementations { get; init; } = [];
}

/// <summary>One closed generic instance linked back to its declaration and call sites in HIR.</summary>
public sealed record SafeCoreGenericHirInstanceBinding(
    string FunctionId,
    int HirNodeId,
    ImmutableArray<RustType> Arguments,
    ImmutableArray<int> CallNodeIds);

/// <summary>Result of binding source generic functions through the name-bound HIR arena.</summary>
public sealed record SafeCoreGenericHirBindingResult(
    SafeCoreHirResult Hir,
    GenericMonomorphizationResult Plan,
    IReadOnlyList<SafeCoreGenericHirInstanceBinding> Bindings,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool IsTruncated)
{
    public bool IsSuccessful => !IsTruncated && Diagnostics.Count == 0 && Plan.IsSuccess;
}

/// <summary>
/// Connects the parsed safe-core source and bound HIR to the bounded generic
/// substitution/monomorphization foundation. This profile deliberately handles
/// type parameters and inferred/explicit type arguments only; lifetime, const,
/// associated and higher-ranked arguments remain explicit diagnostics.
/// </summary>
public static class SafeCoreGenericHirBinding
{
    public const string Profile = "safe-core-generic-hir-v1";

    public const string InvalidInput = "RSG0001";
    public const string LimitReached = "RSG0002";
    public const string Unsupported = "RSG1001";
    public const string MissingEvidence = "RSG1002";
    public const string PlanFailed = "RSG1003";

    public static SafeCoreGenericHirBindingResult Bind(
        SafeCoreSyntaxResult syntax,
        SafeCoreGenericHirBindingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        NormalizedOptions normalized = Normalize(options);
        normalized.CancellationToken.ThrowIfCancellationRequested();
        SafeCoreHirResult hir = SafeCoreHirLowering.Lower(
            syntax,
            new SafeCoreHirLoweringOptions
            {
                Timeout = normalized.Timeout,
                CancellationToken = normalized.CancellationToken,
                MaximumNestingDepth = normalized.MaximumNestingDepth,
                MaximumOperations = normalized.MaximumOperations,
                MaximumDiagnostics = normalized.MaximumDiagnostics,
                NameResolution = new SafeCoreNameResolutionOptions
                {
                    EnableTypeSystemExtensions = true,
                    Timeout = normalized.Timeout,
                    CancellationToken = normalized.CancellationToken,
                    MaximumNestingDepth = normalized.MaximumNestingDepth,
                    MaximumOperations = normalized.MaximumOperations,
                    MaximumDiagnostics = normalized.MaximumDiagnostics,
                },
            });
        return BindCore(syntax, hir, normalized);
    }

    public static SafeCoreGenericHirBindingResult Bind(
        SafeCoreSyntaxResult syntax,
        SafeCoreHirResult hir,
        SafeCoreGenericHirBindingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(hir);
        NormalizedOptions normalized = Normalize(options);
        normalized.CancellationToken.ThrowIfCancellationRequested();

        return BindCore(syntax, hir, normalized);
    }

    private static SafeCoreGenericHirBindingResult BindCore(
        SafeCoreSyntaxResult syntax,
        SafeCoreHirResult hir,
        NormalizedOptions normalized)
    {

        if (!syntax.IsSuccessful)
        {
            return Rejected(hir, EmptyPlan(GenericAnalysisStatus.InvalidInput,
                "Generic binding requires successful source syntax."), syntax.Diagnostics);
        }

        if (!hir.IsSuccessful)
        {
            return Rejected(hir, EmptyPlan(GenericAnalysisStatus.InvalidInput,
                "Generic binding requires successful name-bound HIR."), hir.Diagnostics);
        }

        return new Binder(syntax, hir, normalized).Run();
    }

    private static NormalizedOptions Normalize(SafeCoreGenericHirBindingOptions? options)
    {
        options ??= new SafeCoreGenericHirBindingOptions();
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1) ||
            options.MaximumFunctions is < 1 or > 4096 ||
            options.MaximumCalls is < 1 or > 65_536 ||
            options.MaximumNestingDepth is < 1 or > 256 ||
            options.MaximumOperations is < 1 or > 4_000_000 ||
            options.MaximumDiagnostics is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Generic HIR binding requires finite positive function, call, depth, operation and diagnostic limits.");
        }

        return new NormalizedOptions(
            options.Timeout,
            options.MaximumFunctions,
            options.MaximumCalls,
            options.MaximumNestingDepth,
            options.MaximumOperations,
            options.MaximumDiagnostics,
            options.GenericLimits ?? new GenericAnalysisLimits(),
            options.TraitImplementations ?? [],
            options.CancellationToken);
    }

    private static SafeCoreGenericHirBindingResult Rejected(
        SafeCoreHirResult hir,
        GenericMonomorphizationResult plan,
        IReadOnlyList<Diagnostic> diagnostics) =>
        new(hir, plan, [], diagnostics.ToArray(), IsTruncated: hir.IsTruncated);

    private static GenericMonomorphizationResult EmptyPlan(
        GenericAnalysisStatus status,
        string diagnostic) =>
        new(status, [], [], diagnostic);

    private sealed record NormalizedOptions(
        TimeSpan Timeout,
        int MaximumFunctions,
        int MaximumCalls,
        int MaximumNestingDepth,
        int MaximumOperations,
        int MaximumDiagnostics,
        GenericAnalysisLimits GenericLimits,
        IReadOnlyList<GenericTraitImplementation> TraitImplementations,
        CancellationToken CancellationToken);

    private sealed class Binder
    {
        private readonly SafeCoreSyntaxResult syntax;
        private readonly SafeCoreHirResult hir;
        private readonly NormalizedOptions options;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<Diagnostic> diagnostics = [];
        private readonly Dictionary<string, FunctionInfo> functions = new(StringComparer.Ordinal);
        private readonly Dictionary<(SafeCoreHirNodeKind Kind, TextSpan Span), List<SafeCoreHirNode>> hirIndex = [];
        private int operations;
        private bool truncated;

        public Binder(
            SafeCoreSyntaxResult syntax,
            SafeCoreHirResult hir,
            NormalizedOptions options)
        {
            this.syntax = syntax;
            this.hir = hir;
            this.options = options;
            BuildHirIndex();
        }

        public SafeCoreGenericHirBindingResult Run()
        {
            CollectFunctions(syntax.Root!.Items, depth: 0, modulePath: "crate");
            if (truncated || diagnostics.Count != 0)
            {
                return Finish(EmptyPlan(GenericAnalysisStatus.InvalidInput,
                    "Generic source/HIR binding did not produce a complete function set."));
            }

            foreach (FunctionInfo function in functions.Values.OrderBy(static value => value.Id, StringComparer.Ordinal))
            {
                CollectCalls(function, function.Syntax.Body, depth: 0);
                if (truncated)
                {
                    return Finish(EmptyPlan(GenericAnalysisStatus.LimitExceeded,
                        "Generic source/HIR binding exceeded its configured budget."));
                }

                if (diagnostics.Count != 0)
                {
                    return Finish(EmptyPlan(GenericAnalysisStatus.InvalidInput,
                        "Generic source/HIR binding did not produce complete call-site evidence."));
                }
            }

            ImmutableArray<GenericFunctionDefinition> definitions = functions.Values
                .OrderBy(static value => value.Id, StringComparer.Ordinal)
                .Select(static value => value.ToDefinition())
                .ToImmutableArray();
            ImmutableArray<GenericFunctionInstance> roots = functions.Values
                .Where(IsRoot)
                .OrderBy(static value => value.Id, StringComparer.Ordinal)
                .Select(static value => new GenericFunctionInstance(value.Id, []))
                .ToImmutableArray();

            GenericMonomorphizationResult plan = GenericMonomorphization.Plan(
                definitions,
                roots,
                new GenericTraitSolver(options.TraitImplementations.ToImmutableArray()),
                options.GenericLimits,
                options.CancellationToken);
            if (!plan.IsSuccess)
            {
                AddDiagnostic(
                    PlanFailed,
                    plan.Diagnostic ?? "Generic monomorphization did not produce a complete closed plan.",
                    syntax.Root!.Span);
            }

            return Finish(plan);
        }

        private SafeCoreGenericHirBindingResult Finish(GenericMonomorphizationResult plan)
        {
            var bindings = new List<SafeCoreGenericHirInstanceBinding>(plan.Instances.Length);
            foreach (GenericMonomorphizedFunction instance in plan.Instances)
            {
                if (!functions.TryGetValue(instance.Instance.FunctionId, out FunctionInfo? function))
                {
                    AddDiagnostic(MissingEvidence,
                        "A planned generic instance has no source/HIR declaration evidence.", syntax.Root!.Span);
                    continue;
                }

                bindings.Add(new(
                    instance.Instance.FunctionId,
                    function.HirNode.Id,
                    instance.Instance.Arguments,
                    function.Calls.Select(static call => call.HirNodeId).ToImmutableArray()));
            }

            return new(hir, plan, bindings, diagnostics.ToArray(), truncated);
        }

        private void BuildHirIndex()
        {
            foreach (SafeCoreHirNode node in hir.Nodes)
            {
                if (!Step(node.Span, depth: 0)) return;
                var key = (node.Kind, node.Span);
                if (!hirIndex.TryGetValue(key, out List<SafeCoreHirNode>? values))
                {
                    values = [];
                    hirIndex.Add(key, values);
                }

                values.Add(node);
            }
        }

        private void CollectFunctions(
            IReadOnlyList<SafeCoreItemSyntax> items,
            int depth,
            string modulePath)
        {
            if (!Step(new TextSpan(0, 0), depth)) return;
            foreach (SafeCoreItemSyntax item in items)
            {
                if (!Step(item.Span, depth)) return;
                switch (item)
                {
                    case SafeCoreModuleSyntax module when !module.IsExternal:
                        CollectFunctions(module.Items, depth + 1, modulePath + "::" + module.Name);
                        break;
                    case SafeCoreFunctionSyntax function:
                        AddFunction(function, modulePath, depth + 1);
                        break;
                }

                if (truncated) return;
            }
        }

        private void AddFunction(SafeCoreFunctionSyntax source, string modulePath, int depth)
        {
            if (functions.Count >= options.MaximumFunctions)
            {
                AddLimit(source.Span, "Generic function count exceeded its configured limit.");
                return;
            }

            SafeCoreHirNode? declaration = FindHirNode(SafeCoreHirNodeKind.Function, source.Span, source.Name);
            if (declaration?.DeclaredSymbol is not SafeCoreSymbol symbol)
            {
                AddDiagnostic(MissingEvidence,
                    $"Generic function '{source.Name}' has no bound HIR declaration.", source.Span);
                return;
            }

            var genericNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (SafeCoreGenericParameterSyntax parameter in source.GenericParameters)
            {
                if (!Step(parameter.Span, depth) || parameter.Kind != SafeCoreGenericParameterKind.Type ||
                    !genericNames.Add(parameter.Name) || parameter.DefaultType is not null ||
                    parameter.DefaultValue is not null || parameter.ConstType is not null ||
                    parameter.Constraints.Any(static constraint => constraint is not SafeCoreTraitBoundSyntax))
                {
                    AddDiagnostic(Unsupported,
                        "Only distinct type generic parameters without defaults or non-trait constraints are supported.",
                        parameter.Span);
                }
            }

            var parameterTypes = ImmutableArray.CreateBuilder<RustType>(source.Parameters.Count);
            var parameterNames = new Dictionary<string, RustType>(StringComparer.Ordinal);
            foreach (SafeCoreParameterSyntax parameter in source.Parameters)
            {
                if (!TryConvertType(parameter.Type, genericNames, depth + 1, out RustType? type) || type is null)
                    continue;
                parameterTypes.Add(type);
                if (parameter.Pattern is SafeCoreIdentifierPatternSyntax identifier)
                {
                    parameterNames[identifier.Name] = type;
                }
            }

            RustType returnType = RustType.Unit;
            if (source.ReturnType is not null)
            {
                if (!TryConvertType(source.ReturnType, genericNames, depth + 1, out RustType? convertedReturn) ||
                    convertedReturn is null)
                {
                    returnType = RustType.Unit;
                }
                else
                {
                    returnType = convertedReturn;
                }
            }

            if (parameterTypes.Count != source.Parameters.Count || diagnostics.Count != 0) return;
            var bounds = ImmutableArray.CreateBuilder<GenericTraitObligation>();
            foreach (SafeCoreGenericParameterSyntax parameter in source.GenericParameters)
            {
                foreach (SafeCoreTypeSyntax bound in parameter.Bounds)
                {
                    if (bound is not SafeCorePathTypeSyntax path || path.Segments.Count == 0 ||
                        path.Segments.Any(static segment => segment.GenericArguments.Count != 0 || segment.Arguments.Count != 0))
                    {
                        AddDiagnostic(Unsupported, "Generic trait bounds must be simple paths in this profile.", bound.Span);
                        continue;
                    }

                    bounds.Add(new(path.Segments[^1].Name, RustType.Parameter(parameter.Name)));
                }
            }

            string id = symbol.QualifiedName;
            if (functions.ContainsKey(id))
            {
                AddDiagnostic(InvalidInput, $"Generic function identity '{id}' is duplicated.", source.Span);
                return;
            }

            functions.Add(id, new FunctionInfo(
                id,
                source,
                declaration,
                genericNames.ToImmutableArray(),
                parameterTypes.ToImmutable(),
                returnType,
                parameterNames,
                bounds.ToImmutable()));
        }

        private void CollectCalls(FunctionInfo function, SafeCoreBlockSyntax block, int depth)
        {
            if (!Step(block.Span, depth)) return;
            foreach (SafeCoreStatementSyntax statement in block.Statements)
            {
                if (!Step(statement.Span, depth)) return;
                switch (statement)
                {
                    case SafeCoreLetStatementSyntax let when let.Initializer is not null:
                        CollectExpression(function, let.Initializer, depth + 1);
                        if (let.Pattern is SafeCoreIdentifierPatternSyntax identifier &&
                            TryExpressionType(function, let.Initializer, depth + 1, out RustType? localType) &&
                            localType is not null)
                        {
                            function.LocalTypes[identifier.Name] = localType;
                        }

                        if (let.ElseBlock is not null) CollectCalls(function, let.ElseBlock, depth + 1);
                        break;
                    case SafeCoreReturnStatementSyntax result when result.Value is not null:
                        CollectExpression(function, result.Value, depth + 1);
                        break;
                    case SafeCoreExpressionStatementSyntax expression:
                        CollectExpression(function, expression.Expression, depth + 1);
                        break;
                    case SafeCoreItemStatementSyntax:
                        AddDiagnostic(Unsupported, "Nested generic item bodies are outside this profile.", statement.Span);
                        break;
                }

                if (truncated) return;
            }

            if (block.TailExpression is not null) CollectExpression(function, block.TailExpression, depth + 1);
        }

        private void CollectExpression(FunctionInfo function, SafeCoreExpressionSyntax expression, int depth)
        {
            if (!Step(expression.Span, depth)) return;
            switch (expression)
            {
                case SafeCoreCallExpressionSyntax call:
                    AddCall(function, call, depth + 1);
                    CollectExpression(function, call.Callee, depth + 1);
                    foreach (SafeCoreExpressionSyntax argument in call.Arguments)
                        CollectExpression(function, argument, depth + 1);
                    break;
                case SafeCoreBinaryExpressionSyntax binary:
                    CollectExpression(function, binary.Left, depth + 1);
                    CollectExpression(function, binary.Right, depth + 1);
                    break;
                case SafeCoreUnaryExpressionSyntax unary:
                    CollectExpression(function, unary.Operand, depth + 1);
                    break;
                case SafeCoreTupleExpressionSyntax tuple:
                    foreach (SafeCoreExpressionSyntax element in tuple.Elements)
                        CollectExpression(function, element, depth + 1);
                    break;
                case SafeCoreArrayExpressionSyntax array:
                    foreach (SafeCoreExpressionSyntax element in array.Elements)
                        CollectExpression(function, element, depth + 1);
                    if (array.RepeatCount is not null) CollectExpression(function, array.RepeatCount, depth + 1);
                    break;
                case SafeCoreBlockExpressionSyntax nested:
                    CollectCalls(function, nested.Block, depth + 1);
                    break;
                case SafeCoreIfExpressionSyntax conditional:
                    CollectExpression(function, conditional.Condition, depth + 1);
                    CollectCalls(function, conditional.Then, depth + 1);
                    if (conditional.Else is not null) CollectExpression(function, conditional.Else, depth + 1);
                    break;
                case SafeCoreIndexExpressionSyntax index:
                    CollectExpression(function, index.Target, depth + 1);
                    CollectExpression(function, index.Index, depth + 1);
                    break;
                case SafeCoreStructExpressionSyntax structure:
                    foreach (SafeCoreStructExpressionFieldSyntax field in structure.Fields)
                        CollectExpression(function, field.Value, depth + 1);
                    if (structure.Base is not null) CollectExpression(function, structure.Base, depth + 1);
                    break;
                case SafeCoreMemberExpressionSyntax member:
                    CollectExpression(function, member.Target, depth + 1);
                    break;
                case SafeCoreCastExpressionSyntax cast:
                    CollectExpression(function, cast.Expression, depth + 1);
                    break;
                case SafeCoreLoopExpressionSyntax loop:
                    CollectCalls(function, loop.Body, depth + 1);
                    break;
                case SafeCoreWhileExpressionSyntax loop:
                    CollectExpression(function, loop.Condition, depth + 1);
                    CollectCalls(function, loop.Body, depth + 1);
                    break;
                case SafeCoreMatchExpressionSyntax match:
                    CollectExpression(function, match.Scrutinee, depth + 1);
                    foreach (SafeCoreMatchArmSyntax arm in match.Arms)
                    {
                        if (arm.Guard is not null) CollectExpression(function, arm.Guard, depth + 1);
                        CollectExpression(function, arm.Body, depth + 1);
                    }

                    break;
                case SafeCoreClosureExpressionSyntax closure:
                    CollectExpression(function, closure.Body, depth + 1);
                    break;
                case SafeCorePrintExpressionSyntax print:
                    foreach (SafeCoreExpressionSyntax argument in print.Arguments)
                        CollectExpression(function, argument, depth + 1);
                    break;
                case SafeCoreReturnExpressionSyntax result when result.Value is not null:
                    CollectExpression(function, result.Value, depth + 1);
                    break;
            }
        }

        private void AddCall(FunctionInfo function, SafeCoreCallExpressionSyntax call, int depth)
        {
            if (function.Calls.Count >= options.MaximumCalls)
            {
                AddLimit(call.Span, "Generic call-site count exceeded its configured limit.");
                return;
            }

            SafeCoreHirNode? hirCall = FindHirNode(SafeCoreHirNodeKind.CallExpression, call.Span);
            SafeCoreHirNode? callee = null;
            if (hirCall is not null && hirCall.ChildIds.Count > 0)
            {
                int calleeId = hirCall.ChildIds[0];
                if ((uint)calleeId < (uint)hir.Nodes.Count)
                {
                    callee = hir.GetNode(calleeId);
                }
            }
            SafeCoreSymbol? targetSymbol = callee?.ReferencedSymbol;
            if (hirCall is null || callee is null || targetSymbol is null)
            {
                AddDiagnostic(MissingEvidence,
                    "A generic call has no bound HIR callee evidence.", call.Span);
                return;
            }

            string targetId = CanonicalSymbolId(targetSymbol);
            if (!functions.TryGetValue(targetId, out FunctionInfo? target))
            {
                AddDiagnostic(Unsupported,
                    $"Generic call target '{targetId}' has no local source definition.", call.Span);
                return;
            }

            if (!TryInferCallArguments(function, target, call, depth, out ImmutableArray<RustType> arguments))
            {
                return;
            }

            function.Calls.Add(new PendingCall(
                target.Id,
                arguments,
                hirCall.Id,
                call.Span));
        }

        private bool TryInferCallArguments(
            FunctionInfo caller,
            FunctionInfo target,
            SafeCoreCallExpressionSyntax call,
            int depth,
            out ImmutableArray<RustType> arguments)
        {
            arguments = [];
            if (!Step(call.Span, depth)) return false;
            if (target.ParameterTypes.Length != call.Arguments.Count)
            {
                AddDiagnostic(InvalidInput,
                    $"Call to '{target.Id}' supplies {call.Arguments.Count} value arguments; the declaration expects {target.ParameterTypes.Length}.",
                    call.Span);
                return false;
            }

            if (!TryGetExplicitTypeArguments(
                    call.Callee,
                    caller,
                    depth + 1,
                    out bool hasExplicitArguments,
                    out List<RustType> explicitArguments))
            {
                return false;
            }

            var actual = ImmutableArray.CreateBuilder<RustType>(call.Arguments.Count);
            if (hasExplicitArguments || !target.GenericParameters.IsEmpty)
            {
                foreach (SafeCoreExpressionSyntax expression in call.Arguments)
                {
                    if (!TryExpressionType(caller, expression, depth + 1, out RustType? type))
                    {
                        AddDiagnostic(MissingEvidence,
                            "A generic call argument has no closed or caller-parameter type evidence.", expression.Span);
                        return false;
                    }

                    if (type is null) return false;
                    actual.Add(type);
                }
            }

            if (hasExplicitArguments)
            {
                if (explicitArguments.Count != target.GenericParameters.Length)
                {
                    AddDiagnostic(InvalidInput,
                        $"Call to '{target.Id}' supplies the wrong number of generic arguments.", call.Span);
                    return false;
                }

                var explicitBindings = new Dictionary<string, RustType>(StringComparer.Ordinal);
                for (int index = 0; index < target.ParameterTypes.Length; index++)
                {
                    if (!Unify(target.ParameterTypes[index], actual[index], target.GenericParameters,
                            explicitBindings, depth + 1))
                    {
                        AddDiagnostic(MissingEvidence,
                            $"Explicit generic arguments for '{target.Id}' do not match its value arguments.", call.Span);
                        return false;
                    }
                }

                for (int index = 0; index < target.GenericParameters.Length; index++)
                {
                    string parameter = target.GenericParameters[index];
                    if (!explicitBindings.TryGetValue(parameter, out RustType? inferred) ||
                        inferred is null || !inferred.Equals(explicitArguments[index]))
                    {
                        AddDiagnostic(
                            MissingEvidence,
                            $"Explicit generic argument '{explicitArguments[index]}' for '{target.Id}' does not match value-argument inference.",
                            call.Span);
                        return false;
                    }
                }

                arguments = explicitArguments.ToImmutableArray();
                return true;
            }

            if (target.GenericParameters.IsEmpty)
            {
                arguments = [];
                return true;
            }

            var bindings = new Dictionary<string, RustType>(StringComparer.Ordinal);
            for (int index = 0; index < target.ParameterTypes.Length; index++)
            {
                if (!Unify(target.ParameterTypes[index], actual[index], target.GenericParameters,
                        bindings, depth + 1))
                {
                    AddDiagnostic(MissingEvidence,
                        $"Could not infer generic arguments for '{target.Id}' from its value arguments.", call.Span);
                    return false;
                }
            }

            var closed = ImmutableArray.CreateBuilder<RustType>(target.GenericParameters.Length);
            foreach (string parameter in target.GenericParameters)
            {
                if (!bindings.TryGetValue(parameter, out RustType? value))
                {
                    AddDiagnostic(MissingEvidence,
                        $"Generic argument '{parameter}' for '{target.Id}' has no binding evidence.", call.Span);
                    return false;
                }

                closed.Add(value);
            }

            arguments = closed.ToImmutable();
            return true;
        }

        private bool TryGetExplicitTypeArguments(
            SafeCoreExpressionSyntax callee,
            FunctionInfo caller,
            int depth,
            out bool hasExplicitArguments,
            out List<RustType> arguments)
        {
            hasExplicitArguments = false;
            arguments = [];
            if (callee is not SafeCoreNameExpressionSyntax name || name.Segments.Count == 0) return true;
            SafeCoreExpressionPathSegmentSyntax segment = name.Segments[^1];
            if (!segment.HasGenericArguments) return true;
            hasExplicitArguments = true;

            var genericNames = new HashSet<string>(caller.GenericParameters, StringComparer.Ordinal);
            foreach (SafeCoreGenericArgumentSyntax argument in segment.GenericArguments)
            {
                if (argument is not SafeCoreTypeArgumentSyntax typeArgument)
                {
                    AddDiagnostic(
                        Unsupported,
                        "Lifetime, const and associated generic arguments are outside this profile.",
                        argument.Span);
                    return false;
                }

                if (!TryConvertType(typeArgument.Type, genericNames, depth + 1, out RustType? converted) ||
                    converted is null)
                {
                    return false;
                }

                arguments.Add(converted);
            }

            return true;
        }

        private bool TryExpressionType(
            FunctionInfo function,
            SafeCoreExpressionSyntax expression,
            int depth,
            out RustType? type)
        {
            type = null;
            if (!Step(expression.Span, depth)) return false;
            switch (expression)
            {
                case SafeCoreLiteralExpressionSyntax literal:
                    type = LiteralType(literal.RawText);
                    return true;
                case SafeCoreNameExpressionSyntax name:
                {
                    string simple = LastSegment(name.Path);
                    if (function.LocalTypes.TryGetValue(simple, out RustType? local) ||
                        function.ParameterTypesByName.TryGetValue(simple, out local))
                    {
                        type = local;
                        return true;
                    }

                    return false;
                }
                case SafeCoreCallExpressionSyntax call:
                {
                    SafeCoreHirNode? hirCall = FindHirNode(SafeCoreHirNodeKind.CallExpression, call.Span);
                    SafeCoreSymbol? targetSymbol = hirCall is null || hirCall.ChildIds.Count == 0
                        ? null
                        : hir.GetNode(hirCall.ChildIds[0]).ReferencedSymbol;
                    if (targetSymbol is null || !functions.TryGetValue(CanonicalSymbolId(targetSymbol), out FunctionInfo? target) ||
                        !TryInferCallArguments(function, target, call, depth + 1, out ImmutableArray<RustType> arguments))
                    {
                        return false;
                    }

                    var bindings = new Dictionary<string, RustType>(StringComparer.Ordinal);
                    for (int index = 0; index < target.GenericParameters.Length; index++)
                    {
                        bindings[target.GenericParameters[index]] = arguments[index];
                    }

                    GenericSubstitutionResult substituted = GenericSubstitution.Apply(
                        target.ReturnType,
                        bindings.ToImmutableDictionary(StringComparer.Ordinal),
                        options.GenericLimits,
                        options.CancellationToken);
                    if (!substituted.IsSuccess || substituted.Type is null) return false;
                    type = substituted.Type;
                    return true;
                }
                case SafeCoreUnaryExpressionSyntax unary:
                    if (unary.Operator == "!") { type = RustType.Bool; return true; }
                    if (unary.Operator == "-" && TryExpressionType(function, unary.Operand, depth + 1, out _))
                    {
                        type = RustType.I32;
                        return true;
                    }

                    return false;
                case SafeCoreBinaryExpressionSyntax binary:
                    if (binary.Operator is "&&" or "||" or "==" or "!=" or "<" or ">" or "<=" or ">=")
                    {
                        type = RustType.Bool;
                        return true;
                    }

                    if (TryExpressionType(function, binary.Left, depth + 1, out RustType? left) &&
                        left is not null && TryExpressionType(function, binary.Right, depth + 1, out _))
                    {
                        type = left;
                        return true;
                    }

                    return false;
                case SafeCoreTupleExpressionSyntax tuple:
                {
                    var elements = ImmutableArray.CreateBuilder<RustType>(tuple.Elements.Count);
                    foreach (SafeCoreExpressionSyntax element in tuple.Elements)
                    {
                        if (!TryExpressionType(function, element, depth + 1, out RustType? elementType) || elementType is null)
                            return false;
                        elements.Add(elementType);
                    }

                    type = RustType.Named("Tuple", [.. elements]);
                    return true;
                }
                case SafeCoreBlockExpressionSyntax block when block.Block.TailExpression is not null:
                    return TryExpressionType(function, block.Block.TailExpression, depth + 1, out type);
                case SafeCoreIfExpressionSyntax conditional when conditional.Else is not null &&
                    conditional.Then.TailExpression is not null:
                    if (TryExpressionType(function, conditional.Then.TailExpression, depth + 1, out RustType? thenType) &&
                        TryExpressionType(function, conditional.Else, depth + 1, out RustType? elseType) &&
                        thenType is not null && elseType is not null && thenType.Equals(elseType))
                    {
                        type = thenType;
                        return true;
                    }

                    return false;
                case SafeCoreCastExpressionSyntax cast:
                    return TryConvertType(cast.Type, new HashSet<string>(function.GenericParameters, StringComparer.Ordinal),
                        depth + 1, out type);
            }

            return false;
        }

        private bool TryConvertType(
            SafeCoreTypeSyntax syntaxType,
            HashSet<string> genericNames,
            int depth,
            out RustType? type)
        {
            type = null;
            if (!Step(syntaxType.Span, depth)) return false;
            switch (syntaxType)
            {
                case SafeCoreUnitTypeSyntax:
                    type = RustType.Unit;
                    return true;
                case SafeCoreNeverTypeSyntax:
                    type = RustType.Named("!" );
                    return true;
                case SafeCorePathTypeSyntax path when path.Segments.Count > 0:
                {
                    if (path.IsAbsolute)
                    {
                        AddDiagnostic(Unsupported, "Absolute generic type paths are outside this profile.", path.Span);
                        return false;
                    }

                    string name = string.Join("::", path.Segments.Select(static segment => segment.Name));
                    if (path.Segments.Count == 1 && genericNames.Contains(name) &&
                        path.Segments[0].GenericArguments.Count == 0)
                    {
                        type = RustType.Parameter(name);
                        return true;
                    }

                    var arguments = ImmutableArray.CreateBuilder<RustType>();
                    foreach (SafeCorePathSegmentSyntax segment in path.Segments)
                    {
                        if (segment.Arguments.Count != segment.GenericArguments.Count)
                        {
                            AddDiagnostic(Unsupported,
                                "Lifetime, const and associated generic arguments are outside this profile.", segment.Span);
                            return false;
                        }

                        foreach (SafeCoreTypeSyntax argument in segment.GenericArguments)
                        {
                            if (!TryConvertType(argument, genericNames, depth + 1, out RustType? converted) ||
                                converted is null)
                            {
                                return false;
                            }
                            arguments.Add(converted);
                        }
                    }

                    type = name switch
                    {
                        "bool" when arguments.Count == 0 => RustType.Bool,
                        "i32" when arguments.Count == 0 => RustType.I32,
                        "str" when arguments.Count == 0 => RustType.Text,
                        _ => RustType.Named(name, [.. arguments]),
                    };
                    return true;
                }
                case SafeCoreReferenceTypeSyntax reference:
                    if (!TryConvertType(reference.Inner, genericNames, depth + 1, out RustType? inner) ||
                        inner is null)
                    {
                        return false;
                    }

                    type = RustType.Named(reference.IsMutable ? "&mut" : "&", inner);
                    return true;
                case SafeCoreTupleTypeSyntax tuple:
                {
                    var elements = ImmutableArray.CreateBuilder<RustType>(tuple.Elements.Count);
                    foreach (SafeCoreTypeSyntax element in tuple.Elements)
                    {
                        if (!TryConvertType(element, genericNames, depth + 1, out RustType? converted) ||
                            converted is null)
                        {
                            return false;
                        }
                        elements.Add(converted);
                    }

                    type = RustType.Named("Tuple", [.. elements]);
                    return true;
                }
                case SafeCoreSliceTypeSyntax slice:
                    if (!TryConvertType(slice.Element, genericNames, depth + 1, out RustType? sliceElement) ||
                        sliceElement is null)
                    {
                        return false;
                    }

                    type = RustType.Named("Slice", sliceElement);
                    return true;
                case SafeCoreArrayTypeSyntax array:
                    if (!TryConvertType(array.Element, genericNames, depth + 1, out RustType? arrayElement) ||
                        arrayElement is null)
                    {
                        return false;
                    }

                    type = RustType.Named("Array", arrayElement);
                    return true;
                case SafeCoreFunctionTypeSyntax function:
                {
                    var arguments = ImmutableArray.CreateBuilder<RustType>(function.Parameters.Count + 1);
                    foreach (SafeCoreFunctionTypeParameterSyntax parameter in function.Parameters)
                    {
                        if (!TryConvertType(parameter.Type, genericNames, depth + 1, out RustType? converted) ||
                            converted is null)
                        {
                            return false;
                        }
                        arguments.Add(converted);
                    }

                    RustType returnType = RustType.Unit;
                    if (function.ReturnType is not null)
                    {
                        if (!TryConvertType(function.ReturnType, genericNames, depth + 1, out RustType? convertedReturn) ||
                            convertedReturn is null)
                        {
                            return false;
                        }

                        returnType = convertedReturn;
                    }

                    arguments.Add(returnType);
                    type = RustType.Named("Fn", [.. arguments]);
                    return true;
                }
                default:
                    AddDiagnostic(Unsupported, "This generic type form is outside the safe-core generic profile.", syntaxType.Span);
                    return false;
            }
        }

        private bool Unify(
            RustType template,
            RustType actual,
            ImmutableArray<string> targetParameters,
            Dictionary<string, RustType> bindings,
            int depth)
        {
            if (!Step(new TextSpan(0, 0), depth)) return false;
            if (template.Kind == RustTypeKind.Parameter && targetParameters.Contains(template.Name, StringComparer.Ordinal))
            {
                if (bindings.TryGetValue(template.Name, out RustType? existing)) return existing.Equals(actual);
                bindings.Add(template.Name, actual);
                return true;
            }

            if (template.Kind != actual.Kind || !string.Equals(template.Name, actual.Name, StringComparison.Ordinal) ||
                template.Arguments.Length != actual.Arguments.Length)
            {
                return false;
            }

            for (int index = 0; index < template.Arguments.Length; index++)
            {
                if (!Unify(template.Arguments[index], actual.Arguments[index], targetParameters, bindings, depth + 1))
                    return false;
            }

            return true;
        }

        private bool IsRoot(FunctionInfo function) =>
            string.Equals(function.Syntax.Name, "main", StringComparison.Ordinal) &&
            function.GenericParameters.IsEmpty &&
            function.HirNode.DeclaredSymbol is { } symbol &&
            string.Equals(symbol.ScopePath, hir.NameResolution?.RootScope?.Path, StringComparison.Ordinal);

        private SafeCoreHirNode? FindHirNode(SafeCoreHirNodeKind kind, TextSpan span, string? name = null)
        {
            if (!hirIndex.TryGetValue((kind, span), out List<SafeCoreHirNode>? nodes)) return null;
            return nodes.FirstOrDefault(node => name is null || string.Equals(node.Name, name, StringComparison.Ordinal));
        }

        private static string CanonicalSymbolId(SafeCoreSymbol symbol) =>
            symbol.ResolvedImportTargetQualifiedName ?? symbol.QualifiedName;

        private bool Step(TextSpan span, int depth)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (truncated) return false;
            if (++operations > options.MaximumOperations || depth > options.MaximumNestingDepth ||
                clock.Elapsed >= options.Timeout)
            {
                AddLimit(span, "Generic source/HIR binding exceeded its work, depth or time limit.");
                return false;
            }

            return true;
        }

        private void AddLimit(TextSpan span, string message)
        {
            truncated = true;
            AddDiagnostic(LimitReached, message, span);
        }

        private void AddDiagnostic(string code, string message, TextSpan span)
        {
            if (diagnostics.Count >= options.MaximumDiagnostics) return;
            diagnostics.Add(new Diagnostic(code, message.Length <= 512 ? message : message[..512], span)
            {
                SourcePath = syntax.SourcePath,
            });
        }

        private static RustType LiteralType(string rawText) =>
            rawText is "true" or "false" ? RustType.Bool :
            rawText.StartsWith('"') || rawText.StartsWith("r\"", StringComparison.Ordinal) || rawText.StartsWith('\'')
                ? RustType.Text : RustType.I32;

        private static string LastSegment(string path)
        {
            int separator = path.LastIndexOf("::", StringComparison.Ordinal);
            return separator < 0 ? path : path[(separator + 2)..];
        }

        private sealed class FunctionInfo(
            string id,
            SafeCoreFunctionSyntax source,
            SafeCoreHirNode hirNode,
            ImmutableArray<string> genericParameters,
            ImmutableArray<RustType> parameterTypes,
            RustType returnType,
            Dictionary<string, RustType> parameterTypesByName,
            ImmutableArray<GenericTraitObligation> bounds)
        {
            public string Id { get; } = id;
            public SafeCoreFunctionSyntax Syntax { get; } = source;
            public SafeCoreHirNode HirNode { get; } = hirNode;
            public ImmutableArray<string> GenericParameters { get; } = genericParameters;
            public ImmutableArray<RustType> ParameterTypes { get; } = parameterTypes;
            public RustType ReturnType { get; } = returnType;
            public Dictionary<string, RustType> ParameterTypesByName { get; } = parameterTypesByName;
            public Dictionary<string, RustType> LocalTypes { get; } = new(StringComparer.Ordinal);
            public ImmutableArray<GenericTraitObligation> Bounds { get; } = bounds;
            public List<PendingCall> Calls { get; } = [];

            public GenericFunctionDefinition ToDefinition() =>
                new(Id, GenericParameters, ParameterTypes, ReturnType,
                    Calls.Select(static call => new GenericFunctionInstance(call.TargetId, call.Arguments)).ToImmutableArray(),
                    Bounds);
        }

        private sealed record PendingCall(
            string TargetId,
            ImmutableArray<RustType> Arguments,
            int HirNodeId,
            TextSpan Span);
    }
}
