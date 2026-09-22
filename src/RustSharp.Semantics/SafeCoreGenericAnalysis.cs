using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using RustSharp.Syntax;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

public sealed record SafeCoreGenericAnalysisOptions
{
    public GenericAnalysisLimits Limits { get; init; } = new();
    public ImmutableArray<SafeCoreCrate> Crates { get; init; } = [];
}

public static class SafeCoreGenericDiagnosticCodes
{
    public const string InvalidInput = "RSG1001";
    public const string Unsupported = "RSG1002";
    public const string Unresolved = "RSG1003";
    public const string InvalidCall = "RSG1004";
    public const string BodyMismatch = "RSG1005";
    public const string Trait = "RSG1006";
    public const string LimitReached = "RSG0002";
}

/// <summary>Rigid type evidence for one generic declaration, keyed by name-bound HIR IDs.</summary>
public sealed record SafeCoreGenericFunctionDefinition(
    string Id,
    SafeCoreHirNode Declaration,
    SafeCoreHirNode Body,
    GenericFunctionDefinition PlanDefinition,
    ImmutableDictionary<int, RustType> Types,
    ImmutableDictionary<int, GenericFunctionInstance> Calls);

/// <summary>Closed type and call evidence over the original HIR body. This is not generated IL.</summary>
public sealed record SafeCoreGenericSpecialization(
    GenericFunctionInstance Instance,
    SafeCoreHirNode Declaration,
    ImmutableDictionary<string, RustType> Bindings,
    GenericMonomorphizedFunction PlannedSignature,
    ImmutableDictionary<int, RustType> Types,
    ImmutableDictionary<int, GenericFunctionInstance> Calls);

public sealed record SafeCoreGenericAnalysisProgram(
    SafeCoreHirResult Hir,
    ImmutableArray<SafeCoreGenericFunctionDefinition> Functions,
    GenericMonomorphizationResult Plan,
    ImmutableArray<SafeCoreGenericSpecialization> Specializations)
{
    public ImmutableArray<SafeCoreGenericNominalDefinition> NominalTypes { get; init; } = [];
}

public sealed record SafeCoreGenericFieldDefinition(string Name, RustType Type, SafeCoreHirNode Declaration);

public sealed record SafeCoreGenericNominalDefinition(string Id, SafeCoreHirNode Declaration,
    ImmutableArray<string> Parameters, ImmutableArray<SafeCoreGenericFieldDefinition> Fields);

public sealed record SafeCoreGenericAnalysisResult(
    SafeCoreGenericAnalysisProgram? Program, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsSuccessful => Program is not null && Diagnostics.Count == 0;
}

/// <summary>Checks the bounded generic subset on name-bound HIR, then specializes reachable type evidence.</summary>
public static partial class SafeCoreGenericAnalysis
{
    public const string Profile = "safe-core-generics-v1";

    public static SafeCoreGenericAnalysisResult Check(SafeCoreSyntaxResult syntax,
        SafeCoreGenericAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        cancellationToken.ThrowIfCancellationRequested();
        var budget = CreateBudget(options, cancellationToken);
        if (!syntax.IsSuccessful) return new(null, syntax.Diagnostics);
        try
        {
            SafeCoreHirResult hir = SafeCoreHirLowering.Lower(syntax, new()
            {
                CancellationToken = cancellationToken,
                Timeout = budget.RemainingTime,
                MaximumNodes = budget.Limits.MaximumItems,
                MaximumOperations = budget.Limits.MaximumWork,
                MaximumNestingDepth = budget.Limits.MaximumDepth,
                NameResolution = new()
                {
                    EnableGenericExtensions = true,
                    Crates = options?.Crates ?? [],
                    CancellationToken = cancellationToken,
                    Timeout = budget.RemainingTime,
                    MaximumSymbols = budget.Limits.MaximumItems,
                    MaximumScopes = budget.Limits.MaximumItems,
                    MaximumOperations = budget.Limits.MaximumWork,
                    MaximumNestingDepth = budget.Limits.MaximumDepth,
                },
            });
            return Analyze(hir, budget);
        }
        catch (GenericFailure failure)
        {
            return Failure(failure, syntax.SourcePath, syntax.Root!.Span);
        }
    }

    public static SafeCoreGenericAnalysisResult Check(SafeCoreHirResult hir,
        SafeCoreGenericAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hir);
        cancellationToken.ThrowIfCancellationRequested();
        return Analyze(hir, CreateBudget(options, cancellationToken));
    }

    private static GenericBudget CreateBudget(SafeCoreGenericAnalysisOptions? options, CancellationToken cancellation)
    {
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.Limits);
        return new(options.Limits, cancellation);
    }

    private static SafeCoreGenericAnalysisResult Analyze(SafeCoreHirResult hir, GenericBudget budget)
    {
        if (!hir.IsSuccessful) return new(null, hir.Diagnostics.Select(diagnostic => diagnostic with
        {
            SourcePath = hir.SourcePath,
            Code = diagnostic.Code is SafeCoreHirDiagnosticCodes.LimitReached or SafeCoreNameResolutionDiagnosticCodes.LimitReached
                ? SafeCoreGenericDiagnosticCodes.LimitReached : diagnostic.Code,
        }).ToImmutableArray());
        var checker = new Checker(hir, budget);
        try { return new(checker.Run(), []); }
        catch (SourceFailure failure)
        {
            return new(null, [failure.Diagnostic with { SourcePath = hir.SourcePath }]);
        }
        catch (GenericFailure failure) { return Failure(failure, hir.SourcePath, checker.Current.Span); }
    }

    private static SafeCoreGenericAnalysisResult Failure(GenericFailure failure, string path, TextSpan span) =>
        new(null, [new Diagnostic(failure.Status switch
        {
            GenericAnalysisStatus.LimitExceeded => SafeCoreGenericDiagnosticCodes.LimitReached,
            GenericAnalysisStatus.MissingImplementation or GenericAnalysisStatus.OverlappingImplementations or
                GenericAnalysisStatus.CyclicObligation => SafeCoreGenericDiagnosticCodes.Trait,
            _ => SafeCoreGenericDiagnosticCodes.InvalidInput,
        }, failure.Message, span) { SourcePath = path }]);

    private sealed class SourceFailure(Diagnostic diagnostic) : Exception(diagnostic.Message)
    {
        public Diagnostic Diagnostic { get; } = diagnostic;
    }

    private sealed partial class Checker
    {
        private readonly SafeCoreHirResult hir;
        private readonly GenericBudget budget;
        public Checker(SafeCoreHirResult hir, GenericBudget budget)
        {
            this.hir = hir;
            this.budget = budget;
            Current = hir.Root!;
        }
        private static readonly RustType Never = RustType.Named("$never");
        private readonly Dictionary<string, SafeCoreHirNode> declarations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Declaration> definitions = new(StringComparer.Ordinal);
        private readonly Dictionary<int, RustType> signatureTypes = [];
        private readonly List<(SafeCoreHirNode Node, RustType Type, Declaration Owner)> wellFormed = [];
        private readonly List<GenericTraitImplementation> implementations = [];
        private readonly List<SafeCoreHirNode> implNodes = [];
        private readonly Dictionary<int, string> itemModules = [];
        private GenericTraitSession traits = null!;
        public SafeCoreHirNode Current { get; private set; }

        private sealed class Declaration(SafeCoreHirNode node, ImmutableArray<string> parameters)
        {
            public SafeCoreHirNode Node { get; } = node;
            public ImmutableArray<string> Parameters { get; } = parameters;
            public ImmutableArray<GenericTraitObligation> Bounds { get; set; } = [];
            public ImmutableArray<RustType> ParameterTypes { get; set; } = [];
            public RustType ReturnType { get; set; } = RustType.Unit;
            public SafeCoreHirNode Body { get; set; } = node;
            public List<RustType> Fields { get; } = [];
            public List<SafeCoreGenericFieldDefinition> FieldDefinitions { get; } = [];
        }

        public SafeCoreGenericAnalysisProgram Run()
        {
            budget.Count(hir.Nodes.Count);
            Collect(hir.Root!, 0, "crate");
            foreach (var entry in declarations.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                Step(entry.Value);
                definitions.Add(entry.Key, new(entry.Value, Parameters(entry.Value)));
            }
            // Every declaration is known before resolving any signature or implementation.
            foreach (Declaration definition in definitions.Values) Define(definition);
            foreach (SafeCoreHirNode implementation in implNodes) DefineImplementation(implementation);
            Step(implNodes.Count > 0 ? implNodes[0] : hir.Root!);
            traits = new GenericTraitSolver([.. implementations]).CreateSession(budget);
            foreach (GenericTraitImplementation implementation in implementations)
            {
                foreach (GenericTraitObligation bound in implementation.Bounds)
                    if (!HasParameter(bound.Target, 0)) traits.Prove(bound, []);
            }
            foreach (Declaration definition in definitions.Values)
            {
                foreach (GenericTraitObligation bound in definition.Bounds)
                {
                    Step(definition.Node);
                    if (!HasParameter(bound.Target, 0)) traits.Prove(bound, []);
                }
            }
            foreach (var site in wellFormed)
            {
                Step(site.Node);
                WellFormed(site.Type, site.Owner.Bounds, 0);
            }
            foreach (Declaration definition in definitions.Values.Where(static d => d.Node.Kind == N.Struct))
            {
                CheckLayout(definition, new HashSet<string>(StringComparer.Ordinal), 0);
                foreach (string parameter in definition.Parameters)
                    if (!definition.Fields.Any(type => ContainsParameter(type, parameter, 0)))
                        Fail(definition.Node, SafeCoreGenericDiagnosticCodes.InvalidInput,
                            "Each struct type parameter must occur in a field type.");
            }
            var functions = ImmutableArray.CreateBuilder<SafeCoreGenericFunctionDefinition>();
            foreach (var entry in definitions.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                Step(entry.Value.Node);
                if (entry.Value.Node.Kind == N.Function)
                    functions.Add(new BodyChecker(this, entry.Value).Run());
            }
            var roots = ImmutableArray.CreateBuilder<GenericFunctionInstance>();
            foreach (var function in functions)
            {
                Step(function.Declaration);
                if (function.Declaration.DeclaredSymbol!.Name == "main" &&
                    function.Declaration.DeclaredSymbol.ScopePath == hir.NameResolution!.RootScope!.Path)
                {
                    if (function.PlanDefinition.Parameters.Length != 0 || function.PlanDefinition.ParameterTypes.Length != 0 ||
                        !function.PlanDefinition.ReturnType.Equals(RustType.Unit))
                        Fail(function.Declaration, SafeCoreGenericDiagnosticCodes.InvalidInput,
                            "The root main function must have no generic or value parameters and return unit.");
                    roots.Add(new(function.Id, []));
                }
            }
            Step(hir.Root!);
            GenericMonomorphizationResult plan = GenericMonomorphization.Plan(
                [.. functions.Select(static f => f.PlanDefinition)], roots.ToImmutable(), traits, budget);
            if (!plan.IsSuccess)
            {
                throw new GenericFailure(
                    plan.Status,
                    plan.Diagnostic ?? "Generic monomorphization did not produce a complete closed plan.");
            }
            var byId = functions.ToDictionary(static f => f.Id, StringComparer.Ordinal);
            var specializations = ImmutableArray.CreateBuilder<SafeCoreGenericSpecialization>();
            foreach (GenericMonomorphizedFunction planned in plan.Instances)
            {
                SafeCoreGenericFunctionDefinition definition = byId[planned.Instance.FunctionId];
                Step(definition.Declaration);
                ImmutableDictionary<string, RustType> bindings = BindArguments(definition.PlanDefinition.Parameters, planned.Instance.Arguments);
                var types = ImmutableDictionary.CreateBuilder<int, RustType>();
                foreach (var entry in definition.Types.OrderBy(static pair => pair.Key))
                {
                    Step(hir.GetNode(entry.Key));
                    RustType closed = Substitute(entry.Value, bindings);
                    GenericTypes.Key(closed, budget, requireClosed: true);
                    types.Add(entry.Key, closed);
                }
                var calls = ImmutableDictionary.CreateBuilder<int, GenericFunctionInstance>();
                foreach (var entry in definition.Calls.OrderBy(static pair => pair.Key))
                {
                    Step(hir.GetNode(entry.Key));
                    calls.Add(entry.Key, new(entry.Value.FunctionId,
                        [.. entry.Value.Arguments.Select(type => Substitute(type, bindings))]));
                }
                specializations.Add(new(planned.Instance, definition.Declaration, bindings, planned,
                    types.ToImmutable(), calls.ToImmutable()));
            }
            return new(hir, functions.ToImmutable(), plan, specializations.ToImmutable())
            {
                NominalTypes = [.. definitions.OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                    .Where(static entry => entry.Value.Node.Kind == N.Struct)
                    .Select(static entry => new SafeCoreGenericNominalDefinition(entry.Key, entry.Value.Node,
                        entry.Value.Parameters, [.. entry.Value.FieldDefinitions]))],
            };
        }

        private void Collect(SafeCoreHirNode node, int depth, string module)
        {
            Step(node, depth);
            if (node.Kind == N.Module) module = Key(node.DeclaredSymbol!);
            switch (node.Kind)
            {
                case N.CompilationUnit:
                case N.Module:
                case N.ImportGroup:
                case N.Import:
                    foreach (SafeCoreHirNode child in Parts(node)) Collect(child, depth + 1, module);
                    break;
                case N.Attribute:
                    Documentation(node);
                    break;
                case N.Function:
                case N.Struct:
                case N.Trait:
                    budget.Count(declarations.Count + 1);
                    if (!declarations.TryAdd(Key(node.DeclaredSymbol!), node))
                        Fail(node, SafeCoreGenericDiagnosticCodes.InvalidInput, "Duplicate generic declaration identity.");
                    break;
                case N.Implementation:
                    budget.Count(implNodes.Count + 1);
                    implNodes.Add(node);
                    itemModules.Add(node.Id, module);
                    break;
                default: Unsupported(node); break;
            }
        }

        private ImmutableArray<string> Parameters(SafeCoreHirNode node)
        {
            var names = ImmutableArray.CreateBuilder<string>();
            foreach (SafeCoreHirNode parameter in Parts(node).Where(static p => p.Kind == N.GenericParameter))
            {
                Step(parameter);
                names.Add(Key(parameter.DeclaredSymbol!));
            }
            ImmutableArray<string> result = names.ToImmutable();
            budget.Parameters(result);
            return result;
        }

        private void Define(Declaration definition)
        {
            Step(definition.Node);
            definition.Bounds = Bounds(definition.Node, definition);
            var parameterTypes = ImmutableArray.CreateBuilder<RustType>();
            foreach (SafeCoreHirNode part in Parts(definition.Node))
            {
                Step(part);
                switch (part.Kind)
                {
                    case N.Attribute: Documentation(part); break;
                    case N.GenericParameter:
                    case N.TraitBound: break;
                    case N.Parameter when definition.Node.Kind == N.Function:
                        CheckPattern(Child(part, 0));
                        parameterTypes.Add(Type(Child(part, 1), definition, 0));
                        break;
                    case N.Block when definition.Node.Kind == N.Function:
                        definition.Body = part; break;
                    case N.Field when definition.Node.Kind == N.Struct:
                        foreach (SafeCoreHirNode attribute in Parts(part).Where(static p => p.Kind == N.Attribute)) Documentation(attribute);
                        RustType fieldType = Type(Parts(part).First(IsType), definition, 0);
                        definition.FieldDefinitions.Add(new(part.Name ?? definition.Fields.Count.ToString(CultureInfo.InvariantCulture), fieldType, part));
                        definition.Fields.Add(fieldType);
                        budget.Count(definition.Fields.Count);
                        break;
                    default:
                        if (definition.Node.Kind == N.Function && IsType(part)) definition.ReturnType = Type(part, definition, 0);
                        else Unsupported(part);
                        break;
                }
            }
            if (definition.Node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ConstFunction)) Unsupported(definition.Node);
            definition.ParameterTypes = parameterTypes.ToImmutable();
        }

        private void DefineImplementation(SafeCoreHirNode node)
        {
            Step(node);
            var definition = new Declaration(node, Parameters(node));
            definition.Bounds = Bounds(node, definition);
            SafeCoreHirNode traitNode = Parts(node).Single(p => p.Kind == N.TraitBound && p.ChildIds.Count == 1);
            string trait = Trait(Child(traitNode, 0));
            RustType target = Type(Parts(node).First(IsType), definition, 0);
            string crate = CrateOf(itemModules[node.Id]);
            if (CrateOf(trait) != crate && (target.Kind != RustTypeKind.Named ||
                !definitions.TryGetValue(target.Name, out Declaration? self) || self.Node.Kind != N.Struct || CrateOf(target.Name) != crate))
                Fail(node, SafeCoreGenericDiagnosticCodes.Trait, "A trait implementation requires a trait or nominal self type owned by its crate.");
            string id = "impl@" + node.Span.Start.ToString(CultureInfo.InvariantCulture);
            implementations.Add(new(id, trait, target, definition.Parameters, definition.Bounds));
        }

        private ImmutableArray<GenericTraitObligation> Bounds(SafeCoreHirNode node, Declaration owner)
        {
            var result = ImmutableArray.CreateBuilder<GenericTraitObligation>();
            foreach (SafeCoreHirNode part in Parts(node))
            {
                Step(part);
                if (part.Kind == N.GenericParameter)
                {
                    foreach (SafeCoreHirNode bound in Parts(part))
                    {
                        Step(bound);
                        if (bound.Kind != N.TraitBound || bound.ChildIds.Count != 2) Unsupported(bound);
                        result.Add(new(Trait(Child(bound, 1)), Type(Child(bound, 0), owner, 0)));
                    }
                }
                else if (part.Kind == N.TraitBound && part.ChildIds.Count == 2)
                    result.Add(new(Trait(Child(part, 1)), Type(Child(part, 0), owner, 0)));
            }
            budget.Count(result.Count);
            return result.ToImmutable();
        }

        private string Trait(SafeCoreHirNode node)
        {
            Step(node);
            if (node.Kind != N.PathType || node.ReferencedSymbol is null ||
                !declarations.TryGetValue(Key(node.ReferencedSymbol), out SafeCoreHirNode? declaration) || declaration.Kind != N.Trait)
                Fail(node, SafeCoreGenericDiagnosticCodes.Trait, "A bound or implementation must name a declared marker trait.");
            foreach (SafeCoreHirNode segment in Parts(node))
                if (segment.ChildIds.Count != 0) Unsupported(segment);
            return Key(node.ReferencedSymbol!);
        }

        private RustType Type(SafeCoreHirNode node, Declaration owner, int depth)
        {
            Step(node, depth);
            RustType type;
            switch (node.Kind)
            {
                case N.UnitType: type = RustType.Unit; break;
                case N.TupleType:
                    budget.Count(node.ChildIds.Count);
                    if (node.ChildIds.Count > 16) Unsupported(node);
                    type = node.ChildIds.Count == 1 && !node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma)
                        ? Type(Child(node, 0), owner, depth + 1)
                        : Tuple([.. Parts(node).Select(part => Type(part, owner, depth + 1))]);
                    break;
                case N.PathType:
                    var arguments = ImmutableArray.CreateBuilder<RustType>();
                    foreach (SafeCoreHirNode segment in Parts(node))
                        foreach (SafeCoreHirNode argument in Parts(segment))
                            arguments.Add(Type(argument, owner, depth + 1));
                    if (node.ReferencedSymbol is not null)
                    {
                        string id = Key(node.ReferencedSymbol);
                        if (node.ReferencedSymbol.Kind == SafeCoreSymbolKind.GenericParameter)
                        {
                            if (!owner.Parameters.Contains(id) || arguments.Count != 0)
                                Fail(node, SafeCoreGenericDiagnosticCodes.InvalidInput, "Invalid type parameter reference or arguments.");
                            type = RustType.Parameter(id);
                        }
                        else if (definitions.TryGetValue(id, out Declaration? declaration) && declaration.Node.Kind == N.Struct)
                        {
                            if (declaration.Parameters.Length != arguments.Count)
                                Fail(node, SafeCoreGenericDiagnosticCodes.InvalidCall, "The nominal type has incorrect generic arity.");
                            type = RustType.Named(id, arguments.ToArray());
                            budget.Count(wellFormed.Count + 1);
                            wellFormed.Add((node, type, owner));
                        }
                        else { Unsupported(node); return null!; }
                    }
                    else
                    {
                        if (arguments.Count != 0) Unsupported(node);
                        type = node.Name switch
                        {
                            "i32" => RustType.I32,
                            "bool" => RustType.Bool,
                            _ => UnsupportedType(node),
                        };
                    }
                    break;
                default: Unsupported(node); return null!;
            }
            GenericTypes.Key(type, budget);
            signatureTypes[node.Id] = type;
            return type;
        }

        private void WellFormed(RustType type, ImmutableArray<GenericTraitObligation> assumptions, int depth)
        {
            budget.Step(depth);
            if (definitions.TryGetValue(type.Name, out Declaration? definition) && definition.Node.Kind == N.Struct)
            {
                ImmutableDictionary<string, RustType> bindings = BindArguments(definition.Parameters, type.Arguments);
                foreach (GenericTraitObligation bound in definition.Bounds)
                    traits.Prove(new(bound.Trait, Substitute(bound.Target, bindings)), assumptions);
            }
            foreach (RustType child in type.Arguments) WellFormed(child, assumptions, depth + 1);
        }

        private void CheckLayout(Declaration definition, HashSet<string> active, int depth)
        {
            Step(definition.Node, depth);
            string id = Key(definition.Node.DeclaredSymbol!);
            if (!active.Add(id)) Fail(definition.Node, SafeCoreGenericDiagnosticCodes.InvalidInput, "A struct has infinite recursive layout.");
            foreach (RustType field in definition.Fields) Visit(field, depth + 1);
            active.Remove(id);

            void Visit(RustType type, int nesting)
            {
                budget.Step(nesting);
                if (definitions.TryGetValue(type.Name, out Declaration? target) && target.Node.Kind == N.Struct)
                    CheckLayout(target, active, nesting + 1);
                foreach (RustType argument in type.Arguments) Visit(argument, nesting + 1);
            }
        }

        private bool ContainsParameter(RustType type, string name, int depth)
        {
            budget.Step(depth);
            return type.Kind == RustTypeKind.Parameter && type.Name == name ||
                type.Arguments.Any(argument => ContainsParameter(argument, name, depth + 1));
        }

        private bool HasParameter(RustType type, int depth)
        {
            budget.Step(depth);
            return type.Kind == RustTypeKind.Parameter || type.Arguments.Any(argument => HasParameter(argument, depth + 1));
        }

        private ImmutableDictionary<string, RustType> BindArguments(ImmutableArray<string> names, ImmutableArray<RustType> arguments)
        {
            var bindings = ImmutableDictionary.CreateBuilder<string, RustType>(StringComparer.Ordinal);
            for (int index = 0; index < names.Length; index++)
            {
                budget.Step();
                bindings.Add(names[index], arguments[index]);
            }
            return bindings.ToImmutable();
        }

        private RustType Substitute(RustType type, IReadOnlyDictionary<string, RustType> bindings)
        {
            RustType result = GenericTypes.Substitute(type, bindings, budget, 0);
            GenericTypes.Key(result, budget);
            return result;
        }

        private void Step(SafeCoreHirNode node, int depth = 0) { Current = node; budget.Step(depth); }
        private string CrateOf(string path) => hir.NameResolution!.Crates
            .Where(crate => path == crate.ScopePath || path.StartsWith(crate.ScopePath + "::", StringComparison.Ordinal))
            .OrderByDescending(static crate => crate.ScopePath.Length).FirstOrDefault()?.ScopePath ?? "crate";
        private IEnumerable<SafeCoreHirNode> Parts(SafeCoreHirNode node) => node.ChildIds.Select(hir.GetNode);
        private SafeCoreHirNode Child(SafeCoreHirNode node, int index) => hir.GetNode(node.ChildIds[index]);
        private static string Key(SafeCoreSymbol symbol) => symbol.ResolvedImportTargetQualifiedName ?? symbol.QualifiedName;
        private static bool IsType(SafeCoreHirNode node) => node.Kind is >= N.PathType and <= N.NeverType or N.FunctionType or N.InferredType;
        private static RustType Tuple(RustType[] parts) => parts.Length == 0 ? RustType.Unit : RustType.Named("$tuple", parts);
        private static void Documentation(SafeCoreHirNode node)
        {
            if (node.Name != "doc" || !node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.DocumentationAttribute)) Unsupported(node);
        }
        private static void CheckPattern(SafeCoreHirNode node)
        {
            if (node.Kind is not (N.IdentifierPattern or N.WildcardPattern) ||
                node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ByReference)) Unsupported(node);
        }
        private static RustType UnsupportedType(SafeCoreHirNode node) { Unsupported(node); return null!; }
        [DoesNotReturn]
        private static void Unsupported(SafeCoreHirNode node) => Fail(node, SafeCoreGenericDiagnosticCodes.Unsupported,
            $"{node.Kind} is outside {Profile}.");
        [DoesNotReturn]
        private static void Fail(SafeCoreHirNode node, string code, string message) => throw new SourceFailure(new(code, message, node.Span));
    }
}
