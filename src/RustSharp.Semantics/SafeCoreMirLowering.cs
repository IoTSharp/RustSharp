using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using RustSharp.Syntax;
using K = RustSharp.Semantics.SafeCoreSemanticTypeKind;
using N = RustSharp.Semantics.SafeCoreHirNodeKind;

namespace RustSharp.Semantics;

/// <summary>Independent bounds for the opt-in bounded-value MIR lowering pass.</summary>
public sealed record SafeCoreMirLoweringOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumOperations { get; init; } = 1_000_000;
    public int MaximumNestingDepth { get; init; } = 128;
    public int MaximumFunctions { get; init; } = 1_024;
    public int MaximumBlocksPerFunction { get; init; } = 16_384;
    public int MaximumLocalsPerFunction { get; init; } = 65_536;
    /// <summary>
    /// Enables the versioned repeated-array extension. The v1 profile keeps
    /// its original rejection contract; callers opting into the next profile
    /// may lower only structural-Copy repetitions.
    /// </summary>
    public bool EnableRepeatedArrays { get; init; }
    /// <summary>Enables the versioned P1 pattern and closure execution extensions.</summary>
    public bool EnableP1Extensions { get; init; }
    /// <summary>Bounds the expansion of nested or-patterns, including guarded alternatives.</summary>
    public int MaximumPatternAlternatives { get; init; } = 256;
}

/// <summary>No partial program is published when lowering or validation fails.</summary>
public sealed record SafeCoreMirLoweringResult(
    SafeCoreMirProgram? Program,
    IReadOnlyList<Diagnostic> Diagnostics,
    SafeCoreMirValidationResult? Validation,
    bool IsTruncated)
{
    public bool IsSuccessful => Program is not null && Diagnostics.Count == 0 &&
        !IsTruncated && Validation is { IsSuccessful: true };
}

/// <summary>
/// Lowers resolved P1-04 evidence to the experimental bounded-value MIR profile.
/// The compiler's opt-in safe-core MIR profile consumes the resulting program
/// through the dedicated MIR-to-CLR-LIR backend; existing primitive and generic
/// profiles retain their own lowering paths.
/// </summary>
public static partial class SafeCoreMirLowering
{
    public const string Profile = "safe-core-mir-v1";
    public const string InvalidEvidence = "RSM2001";
    public const string UnsupportedSyntax = "RSM2002";
    public const string LimitReached = "RSM2003";

    public static SafeCoreMirLoweringResult Lower(SafeCoreTypeAnalysisProgram program,
        SafeCoreMirLoweringOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(program.Hir);
        ArgumentNullException.ThrowIfNull(program.Types);
        ArgumentNullException.ThrowIfNull(program.Coercions);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1) ||
            options.MaximumOperations is < 1 or > 4_000_000 ||
            options.MaximumNestingDepth is < 1 or > 128 ||
            options.MaximumFunctions is < 1 or > 4_096 ||
            options.MaximumBlocksPerFunction is < 1 or > 65_536 ||
            options.MaximumLocalsPerFunction is < 1 or > 262_144 ||
            options.MaximumPatternAlternatives is < 1 or > 4_096)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!program.Hir.IsSuccessful || program.Hir.Root is null)
            return new(null, [new Diagnostic(InvalidEvidence, "MIR requires successful, resolved HIR type evidence.",
                program.Hir.Root?.Span ?? new TextSpan(0, 0)) { SourcePath = program.Hir.SourcePath }], null, false);
        try
        {
            var lowerer = new Lowerer(program, options, cancellationToken);
            SafeCoreMirProgram mir = lowerer.Run();
            SafeCoreMirValidationResult validation = SafeCoreMirValidation.Validate(mir,
                new() { CancellationToken = cancellationToken, Timeout = lowerer.Remaining,
                    MaximumOperations = lowerer.RemainingOperations,
                    MaximumTypeDepth = options.MaximumNestingDepth });
            return validation.IsSuccessful
                ? new(mir, [], validation, false)
                : new(null, validation.Diagnostics.Select(d => new Diagnostic(d.Code, d.Message,
                    d.Source?.Span ?? program.Hir.Root!.Span) { SourcePath = d.Source?.SourcePath ?? program.Hir.SourcePath }).ToArray(),
                    validation, validation.IsTruncated);
        }
        catch (LoweringException exception)
        {
            return new(null, [exception.Diagnostic with { SourcePath = program.Hir.SourcePath }], null,
                exception.Diagnostic.Code == LimitReached);
        }
        catch (SafeCoreMirLimitException)
        {
            return new(null, [new Diagnostic(LimitReached, "MIR construction exceeded its bounded collection limits.",
                program.Hir.Root!.Span) { SourcePath = program.Hir.SourcePath }], null, true);
        }
    }

    private sealed class LoweringException(Diagnostic diagnostic) : Exception(diagnostic.Message)
    {
        public Diagnostic Diagnostic { get; } = diagnostic;
    }

    private sealed partial class Lowerer(SafeCoreTypeAnalysisProgram input, SafeCoreMirLoweringOptions options,
        CancellationToken cancellation)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<SafeCoreHirNode> _functionNodes = [];
        private readonly Dictionary<string, int> _functions = new(StringComparer.Ordinal);
        private readonly List<SafeCoreMirLocal> _locals = [];
        private readonly Dictionary<SafeCoreSymbol, int> _bindings = [];
        private readonly Dictionary<int, int> _referenceOwners = [];
        private readonly Dictionary<string, int> _dropFunctions = new(StringComparer.Ordinal);
        private readonly HashSet<int> _destructorNodes = [];
        private readonly List<List<int>> _dropScopes = [];
        private readonly List<BlockBuilder> _blocks = [];
        private readonly List<LoopContext> _loops = [];
        private BlockBuilder? _current;
        private int _operations;

        private sealed class BlockBuilder(int id, SafeCoreMirSource source)
        {
            public int Id { get; } = id;
            public SafeCoreMirSource Source { get; } = source;
            public List<SafeCoreMirStatement> Statements { get; } = [];
            public SafeCoreMirTerminator? Terminator { get; set; }
        }

        private sealed class LoopContext(string? label, int header, int exit, int? result)
        {
            public string? Label { get; } = label;
            public int Header { get; } = header;
            public int Exit { get; } = exit;
            public int? Result { get; } = result;
            public bool HasBreak { get; set; }
        }

        public TimeSpan Remaining
        {
            get
            {
                Step(input.Hir.Root!, 0);
                TimeSpan remaining = options.Timeout - _clock.Elapsed;
                if (remaining <= TimeSpan.Zero) Limit(input.Hir.Root!);
                return remaining;
            }
        }

        public int RemainingOperations
        {
            get
            {
                if (_operations >= options.MaximumOperations) Limit(input.Hir.Root!);
                return options.MaximumOperations - _operations;
            }
        }

        public SafeCoreMirProgram Run()
        {
            Collect(input.Hir.Root!, 0);
            var functions = new List<SafeCoreMirFunction>(_functionNodes.Count);
            for (int index = 0; index < _functionNodes.Count; index++)
            {
                Step(_functionNodes[index], 0);
                functions.Add(Function(_functionNodes[index], index));
            }
            return new(functions, cancellation);
        }

        private void Collect(SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (node.Kind is N.CompilationUnit or N.Module)
            {
                for (int index = 0; index < node.ChildIds.Count; index++) Collect(Child(node, index), depth + 1);
                return;
            }
            if (node.Kind is N.Attribute or N.Import or N.ImportGroup or N.TypeAlias) return;
            // Nominal unit ADTs and bounded Drop implementations contribute
            // type/cleanup metadata to the HIR but are not standalone MIR
            // entry functions. Their associated Drop body is consumed by the
            // owning function's cleanup projection.
            if (options.EnableP1Extensions && node.Kind == N.Struct &&
                node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.UnitStruct)) return;
            if (options.EnableP1Extensions && node.Kind == N.Implementation)
            {
                ValidateDropImplementation(node, depth);
                return;
            }
            if (node.Kind != N.Function) Unsupported(node);
            if (_functionNodes.Count >= options.MaximumFunctions) Limit(node);
            SafeCoreType signature = Type(node);
            // Function-item evidence must carry a named signature that the call
            // resolver can use. An anonymous function type here is incomplete
            // evidence, rather than an unsupported source construct.
            if (signature.Kind != K.Function || string.IsNullOrEmpty(signature.Name) || node.DeclaredSymbol is null)
                Invalid(node);
            SafeCoreHirNode returnTypeNode = node;
            int parameterIndex = 0;
            for (int childIndex = 0; childIndex < node.ChildIds.Count; childIndex++)
            {
                Step(node, depth);
                SafeCoreHirNode child = Child(node, childIndex);
                if (child.Kind == N.Parameter)
                {
                    if (_destructorNodes.Contains(node.Id)) continue;
                    if (parameterIndex >= signature.ParameterTypes.Count)
                        Invalid(child);
                    // The second parameter child is the type syntax node. Keeping it
                    // as the evidence anchor gives unsupported signature types their
                    // exact source span instead of the enclosing function span.
                    SafeCoreHirNode typeNode = Child(child, 1);
                    if (!IsStructuralCopy(signature.ParameterTypes[parameterIndex])) Unsupported(typeNode);
                    ValueType(signature.ParameterTypes[parameterIndex++], typeNode);
                }
                else if (IsTypeNode(child.Kind))
                {
                    returnTypeNode = child;
                }
            }
            if (!_destructorNodes.Contains(node.Id) && parameterIndex != signature.ParameterTypes.Count)
                Invalid(node);
            if (!IsStructuralCopy(signature.ReturnType) && signature.ReturnType.Kind != K.Never)
                Unsupported(returnTypeNode);
            ValueType(signature.ReturnType, returnTypeNode, allowNever: true);
            if (!_functions.TryAdd(SymbolKey(node.DeclaredSymbol!), _functionNodes.Count)) Invalid(node);
            _functionNodes.Add(node);
        }

        private SafeCoreMirFunction Function(SafeCoreHirNode node, int id)
        {
            _locals.Clear(); _bindings.Clear(); _referenceOwners.Clear(); _blocks.Clear(); _loops.Clear();
            _closures.Clear(); _closureReturns.Clear(); _dropScopes.Clear();
            SafeCoreType signature = Type(node);
            bool isDestructor = _destructorNodes.Contains(node.Id);
            var parameterPatterns = new List<(SafeCoreHirNode Pattern, int Local)>();
            int parameterIndex = 0;
            for (int index = 0; index < node.ChildIds.Count; index++)
            {
                SafeCoreHirNode child = Child(node, index);
                Step(child, 0);
                if (child.Kind != N.Parameter) continue;
                if (isDestructor) continue;
                SafeCoreHirNode pattern = UnwrapPattern(Child(child, 0));
                if (!options.EnableP1Extensions && pattern.Kind is not (N.IdentifierPattern or N.WildcardPattern) ||
                    pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ByReference)) Unsupported(pattern);
                int local = Local(pattern.Name ?? $"arg{parameterIndex.ToString(CultureInfo.InvariantCulture)}",
                    signature.ParameterTypes[parameterIndex++], SafeCoreMirLocalKind.Parameter,
                    pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable), pattern);
                if (pattern.DeclaredSymbol is not null && !_bindings.TryAdd(pattern.DeclaredSymbol, local))
                    Invalid(pattern);
                if (pattern.Kind is not (N.IdentifierPattern or N.WildcardPattern))
                    parameterPatterns.Add((pattern, local));
            }
            if (!isDestructor && parameterIndex != signature.ParameterTypes.Count) Invalid(node);
            _current = Block(node);
            for (int index = 0; index < parameterPatterns.Count; index++)
            {
                Step(node, 0);
                (SafeCoreHirNode pattern, int local) = parameterPatterns[index];
                BindIrrefutablePattern(pattern, SafeCoreMirOperand.Local(local, _locals[local].Type, Source(pattern)), 1);
            }
            SafeCoreMirOperand? body = Expr(Child(node, node.ChildIds.Count - 1), 0);
            if (_current is not null)
            {
                EmitDropBodies(node);
                End(SafeCoreMirTerminator.Return(signature.ReturnType.Kind == K.Unit ? null : body, Source(node)));
            }
            var blocks = new List<SafeCoreMirBlock>(_blocks.Count);
            for (int index = 0; index < _blocks.Count; index++)
            {
                Step(node, 0);
                BlockBuilder block = _blocks[index];
                blocks.Add(new(block.Id, block.Statements,
                    block.Terminator ?? SafeCoreMirTerminator.Unreachable(block.Source), block.Source, cancellation));
            }
            return new(id, signature.Name!, signature.ReturnType, _locals.ToArray(), blocks, 0, Source(node),
                node.DeclaredSymbol!.IsPublic, cancellation);
        }

        private SafeCoreMirOperand? Expr(SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (_current is null) return null;
            SafeCoreMirOperand? value = ExprCore(node, depth);
            if (value is not null && _current is not null && input.Coercions.TryGetValue(node.Id, out SafeCoreType? target) &&
                !value.Type.Equals(target))
            {
                ValueType(target, node);
                value = Emit(SafeCoreMirRvalue.Coerce(value, target, Source(node)), target, node);
            }
            return value;
        }

        private SafeCoreMirOperand? ExprCore(SafeCoreHirNode node, int depth)
        {
            switch (node.Kind)
            {
                case N.Attribute: return Unit(node);
                case N.Block:
                    int firstLocal = _locals.Count;
                    _dropScopes.Add([]);
                    try
                    {
                        SafeCoreMirOperand? result = Unit(node);
                        for (int index = 0; index < node.ChildIds.Count && _current is not null; index++)
                            result = Expr(Child(node, index), depth + 1);
                        if (result is { Kind: SafeCoreMirOperandKind.Local, Type.Kind: K.Reference } &&
                            _referenceOwners.TryGetValue(result.Id, out int escapedOwner) && escapedOwner >= firstLocal)
                            throw new LoweringException(new(SafeCoreOwnershipDiagnosticCodes.Escape,
                                "A reference cannot escape the lexical scope of its owner.", node.Span));
                        // A reference local from an outer scope may be
                        // reassigned while this block is active. If its new
                        // provenance points at a block local, the borrow
                        // would outlive the owner even though the block's
                        // value is unit, so report the stable ownership escape
                        // diagnostic at the block boundary.
                        foreach ((int reference, int owner) in _referenceOwners)
                            if (reference < firstLocal && owner >= firstLocal)
                                throw new LoweringException(new(SafeCoreOwnershipDiagnosticCodes.Escape,
                                    "A reference cannot escape the lexical scope of its owner.", node.Span));
                        if (_current is not null) EmitScopeDrops(_dropScopes.Count - 1);
                        return result;
                    }
                    finally { _dropScopes.RemoveAt(_dropScopes.Count - 1); }
                case N.BlockExpression: return Expr(Child(node, 0), depth + 1);
                case N.TupleExpression when node.ChildIds.Count == 0: return Unit(node);
                case N.TupleExpression when node.ChildIds.Count == 1 &&
                    !node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma):
                    return Expr(Child(node, 0), depth + 1);
                case N.TupleExpression: return Tuple(node, depth);
                case N.ArrayExpression: return Array(node, depth);
                case N.ExpressionStatement:
                    _ = Expr(Child(node, 0), depth + 1);
                    return _current is null ? null : Unit(node);
                case N.LetStatement: return Let(node, depth);
                case N.LiteralExpression: return Literal(node, Type(node));
                case N.NameExpression:
                    int binding = -1;
                    if (node.ReferencedSymbol is null || !_bindings.TryGetValue(node.ReferencedSymbol, out binding))
                    {
                        SafeCoreType constructorType = Type(node);
                        if (node.ReferencedSymbol?.Kind == SafeCoreSymbolKind.Struct && IsUnitAdt(constructorType))
                            return SafeCoreMirOperand.Constant(constructorType, "()", Source(node));
                        Unsupported(node);
                    }
                    SafeCoreMirLocal local = _locals[binding];
                    if (local.Type.Kind == K.Adt) Unsupported(node);
                    if (local.Type.Kind == K.Reference)
                        return SafeCoreMirOperand.Local(binding, local.Type, Source(node));
                    // Snapshot the read before a later operand can mutate the user local.
                    return Emit(SafeCoreMirRvalue.Use(SafeCoreMirOperand.Local(binding, local.Type, Source(node)), Source(node)), local.Type, node);
                case N.UnaryExpression:
                    if (node.Value is "&" or "&mut")
                    {
                        if (!options.EnableP1Extensions) Unsupported(node);
                        SafeCoreType referenceType = Type(node);
                        int referenceId = Temp(referenceType, node);
                        LowerBorrow(node, referenceId, depth);
                        return SafeCoreMirOperand.Local(referenceId, referenceType, Source(node));
                    }
                    if (node.Value == "*")
                    {
                        if (!options.EnableP1Extensions) Unsupported(node);
                        SafeCoreMirOperand? reference = Expr(Child(node, 0), depth + 1);
                        SafeCoreType resultType = Type(node);
                        if (reference is null || reference.Type.Kind != K.Reference ||
                            reference.Type.ElementType != resultType)
                            Unsupported(node);
                        return Emit(SafeCoreMirRvalue.Unary("*", reference, resultType, Source(node)), resultType, node);
                    }
                    if (node.Value is not ("!" or "-")) Unsupported(node);
                    SafeCoreHirNode inner = UnwrapExpression(Child(node, 0), depth + 1);
                    if (node.Value == "-" && inner.Kind == N.LiteralExpression && Type(node).IsInteger)
                        return Literal(inner, Type(node), negate: true, origin: node);
                    SafeCoreMirOperand? operand = Expr(Child(node, 0), depth + 1);
                    return operand is null ? null : Emit(SafeCoreMirRvalue.Unary(node.Value!, operand, Type(node), Source(node)), Type(node), node);
                case N.BinaryExpression: return Binary(node, depth);
                case N.CastExpression:
                    SafeCoreMirOperand? cast = Expr(Child(node, 0), depth + 1);
                    Scalar(Type(node), node);
                    return cast is null ? null : Emit(SafeCoreMirRvalue.Cast(cast, Type(node), Source(node)), Type(node), node);
                case N.CallExpression: return Call(node, depth);
                case N.ClosureExpression when options.EnableP1Extensions: return ClosureValue(node, depth);
                case N.PrintExpression: return Print(node, depth);
                case N.IfExpression: return If(node, depth);
                case N.IndexExpression: return Index(node, depth);
                case N.MemberExpression when options.EnableP1Extensions: return Member(node, depth);
                case N.MatchExpression when options.EnableP1Extensions: return Match(node, depth);
                case N.LoopExpression:
                case N.WhileExpression: return Loop(node, depth);
                case N.BreakExpression:
                case N.ContinueExpression: return LoopControl(node, depth);
                case N.ReturnExpression:
                case N.ReturnStatement:
                    SafeCoreMirOperand? returned = node.ChildIds.Count == 0 ? null : Expr(Child(node, 0), depth + 1);
                    if (_current is not null)
                    {
                        if (_closureReturns.Count != 0)
                        {
                            ClosureReturn context = _closureReturns[^1];
                            context.HasReturn |= Join(context.Destination, returned, context.Join, node);
                    }
                        else
                        {
                            EmitDropBodies(node);
                            End(SafeCoreMirTerminator.Return(returned?.Type.Kind == K.Unit ? null : returned, Source(node)));
                        }
                    }
                    return null;
                default: Unsupported(node); return null;
            }
        }

        private SafeCoreMirOperand? Let(SafeCoreHirNode node, int depth)
        {
            if (options.EnableP1Extensions) return ExtendedLet(node, depth);
            if (node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasElse)) Unsupported(node);
            SafeCoreHirNode pattern = UnwrapPattern(Child(node, 0));
            if (pattern.Kind is not (N.IdentifierPattern or N.WildcardPattern) ||
                pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ByReference)) Unsupported(pattern);
            SafeCoreMirOperand? value = Expr(Child(node, node.ChildIds.Count - 1), depth + 1);
            if (_current is null || value is null) return null;
            if (pattern.Kind == N.WildcardPattern) return Unit(node);
            if (pattern.DeclaredSymbol is null) Invalid(pattern);
            int local = Local(pattern.Name!, Type(pattern), SafeCoreMirLocalKind.User,
                pattern.Modifiers.HasFlag(SafeCoreHirNodeModifiers.Mutable), pattern);
            if (!_bindings.TryAdd(pattern.DeclaredSymbol!, local))
                Invalid(pattern);
            Assign(local, value, node);
            return Unit(node);
        }

        private SafeCoreMirOperand? Binary(SafeCoreHirNode node, int depth)
        {
            string op = node.Value!;
            if (op is "&&" or "||") return ShortCircuit(node, depth);
            if (op is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=")
            {
                SafeCoreHirNode place = UnwrapExpression(Child(node, 0), depth + 1);
                int destination = -1;
                int? throughReference = null;
                if (place.Kind == N.NameExpression && place.ReferencedSymbol is not null)
                {
                    if (!_bindings.TryGetValue(place.ReferencedSymbol, out destination)) Unsupported(place);
                }
                else if (place.Kind == N.UnaryExpression && place.Value == "*" && place.ChildIds.Count == 1)
                {
                    SafeCoreHirNode referenceNode = UnwrapExpression(Child(place, 0), depth + 1);
                    int referenceId = -1;
                    int ownerId = -1;
                    if (referenceNode.Kind != N.NameExpression || referenceNode.ReferencedSymbol is null ||
                        !_bindings.TryGetValue(referenceNode.ReferencedSymbol, out referenceId) ||
                        _locals[referenceId].Type.Kind != K.Reference ||
                        !_referenceOwners.TryGetValue(referenceId, out ownerId))
                        Unsupported(place);
                    throughReference = referenceId;
                    destination = ownerId;
                }
                else Unsupported(place);
                SafeCoreMirOperand? right = Expr(Child(node, 1), depth + 1);
                if (right is null || _current is null) return null;
                if (throughReference is int referenceLocal)
                {
                    SafeCoreMirOperand reference = SafeCoreMirOperand.Local(referenceLocal,
                        _locals[referenceLocal].Type, Source(place));
                    if (op != "=")
                    {
                        SafeCoreMirOperand owner = Emit(SafeCoreMirRvalue.Unary("*", reference,
                            _locals[destination].Type, Source(place)), _locals[destination].Type, place);
                        right = Emit(SafeCoreMirRvalue.Binary(op[..^1], owner, right,
                            _locals[destination].Type, Source(node)), _locals[destination].Type, node);
                    }
                    _current.Statements.Add(new(destination,
                        SafeCoreMirRvalue.Write(reference, right, _locals[destination].Type, Source(node)), Source(node)));
                    return Unit(node);
                }
                if (_locals[destination].Type.Kind == K.Reference)
                {
                    int rightOwner = -1;
                    if (right.Kind != SafeCoreMirOperandKind.Local || right.Type != _locals[destination].Type ||
                        !_referenceOwners.TryGetValue(right.Id, out rightOwner))
                        Unsupported(place);
                    // Replacing a reference keeps its provenance explicit. A
                    // later ownership pass distinguishes shared Copy from
                    // mutable Move using the source reference type.
                    _referenceOwners[destination] = rightOwner;
                    _current.Statements.Add(new(destination,
                        SafeCoreMirRvalue.Use(right, Source(node)), Source(node)));
                    return Unit(node);
                }
                if (_locals[destination].Type.Kind is K.Reference or K.Adt) Unsupported(place);
                if (op != "=")
                    right = Emit(SafeCoreMirRvalue.Binary(op[..^1],
                        SafeCoreMirOperand.Local(destination, _locals[destination].Type, Source(place)),
                        right, _locals[destination].Type, Source(node)), _locals[destination].Type, node);
                Assign(destination, right, node);
                return Unit(node);
            }
            SafeCoreMirOperand? leftValue = Expr(Child(node, 0), depth + 1);
            SafeCoreMirOperand? rightValue = Expr(Child(node, 1), depth + 1);
            if (leftValue is null || rightValue is null || _current is null) return null;
            return Emit(SafeCoreMirRvalue.Binary(op, leftValue, rightValue, Type(node), Source(node)), Type(node), node);
        }

        private SafeCoreMirOperand? Tuple(SafeCoreHirNode node, int depth)
        {
            SafeCoreType type = EffectiveType(node);
            ValueType(type, node);
            if (type.Kind != K.Tuple || type.Elements.Count != node.ChildIds.Count)
                Unsupported(node);

            var operands = new List<SafeCoreMirOperand>(node.ChildIds.Count);
            for (int index = 0; index < node.ChildIds.Count && _current is not null; index++)
            {
                Step(node, depth + 1);
                SafeCoreMirOperand? element = Expr(Child(node, index), depth + 1);
                if (element is null) return null;
                operands.Add(element);
            }

            return _current is null
                ? null
                : Emit(SafeCoreMirRvalue.Tuple(operands, type, Source(node), cancellation), type, node);
        }

        private SafeCoreMirOperand? Array(SafeCoreHirNode node, int depth)
        {
            SafeCoreType type = EffectiveType(node);
            ValueType(type, node);
            if (type.Kind != K.Array)
                Unsupported(node);
            long? arrayLength = type.Length;
            if (arrayLength is null || arrayLength.Value < 0 || arrayLength.Value > options.MaximumOperations)
                Unsupported(node);
            long length = arrayLength.Value;

            bool repeatedArray = node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.RepeatedArray);
            if (repeatedArray && !options.EnableRepeatedArrays)
                Unsupported(node);
            if (repeatedArray ? node.ChildIds.Count != 2 : length != node.ChildIds.Count)
                Unsupported(node);

            if (repeatedArray)
            {
                // Rust evaluates the repeat operand once and copies that value
                // into every element. MIR keeps one immutable operand for each
                // slot, so side effects in the operand cannot be duplicated by
                // this lowering. The typed-MIR profile only accepts structural
                // Copy values here; ownership-bearing values remain a stable
                // unsupported boundary until their provenance is represented.
                SafeCoreMirOperand? repeated = Expr(Child(node, 0), depth + 1);
                if (_current is null || repeated is null) return null;
                if (repeated.Type != type.ElementType || !IsStructuralCopy(type.ElementType))
                    Unsupported(node);

                var repeatedOperands = new List<SafeCoreMirOperand>(checked((int)length));
                for (long index = 0; index < length && _current is not null; index++)
                {
                    Step(node, depth + 1);
                    repeatedOperands.Add(repeated);
                }

                return _current is null
                    ? null
                    : Emit(SafeCoreMirRvalue.Array(repeatedOperands, type, Source(node), cancellation), type, node);
            }

            var operands = new List<SafeCoreMirOperand>(node.ChildIds.Count);
            for (int index = 0; index < node.ChildIds.Count && _current is not null; index++)
            {
                Step(node, depth + 1);
                SafeCoreMirOperand? element = Expr(Child(node, index), depth + 1);
                if (element is null) return null;
                if (element.Type != type.ElementType) Invalid(Child(node, index));
                operands.Add(element);
            }

            return _current is null
                ? null
                : Emit(SafeCoreMirRvalue.Array(operands, type, Source(node), cancellation), type, node);
        }

        private SafeCoreMirOperand? Index(SafeCoreHirNode node, int depth)
        {
            SafeCoreMirOperand? array = Expr(Child(node, 0), depth + 1);
            SafeCoreMirOperand? index = Expr(Child(node, 1), depth + 1);
            if (_current is null || array is null || index is null) return null;
            SafeCoreType resultType = EffectiveType(node);
            SafeCoreType indexed = array.Type.Kind == K.Reference ? array.Type.ElementType! : array.Type;
            if (indexed is null || indexed.Kind is not (K.Array or K.Slice) || index.Type.Kind != K.Usize ||
                resultType != indexed.ElementType)
                Unsupported(node);
            if (index.Kind == SafeCoreMirOperandKind.Constant &&
                (!BigInteger.TryParse(index.Value, NumberStyles.None, CultureInfo.InvariantCulture, out BigInteger constant) ||
                 indexed.Kind == K.Array && (indexed.Length is not long length || constant < 0 || constant >= length) ||
                 constant < 0))
                Invalid(node);
            return Emit(SafeCoreMirRvalue.Index(array, index, resultType, Source(node)), resultType, node);
        }

        private SafeCoreMirOperand? Call(SafeCoreHirNode node, int depth)
        {
            SafeCoreHirNode calleeNode = UnwrapExpression(Child(node, 0), depth + 1);
            // A full array-to-slice view has a stable owner length. Keep
            // `.len()` explicit in MIR so the backend cannot silently treat a
            // slice as a fixed array value or drop its provenance.
            if (calleeNode.Kind == N.MemberExpression &&
                string.Equals(calleeNode.Name, "len", StringComparison.Ordinal))
            {
                if (node.ChildIds.Count != 1) Unsupported(node);
                SafeCoreMirOperand? target = Expr(Child(calleeNode, 0), depth + 1);
                if (_current is null || target is null) return null;
                SafeCoreType targetType = target.Type.Kind == K.Reference ? target.Type.ElementType! : target.Type;
                if (targetType.Kind is not (K.Array or K.Slice)) Unsupported(calleeNode);
                SafeCoreType resultType = SafeCoreType.Primitive(K.Usize);
                return Emit(SafeCoreMirRvalue.SliceLength(target, Source(node)), resultType, node);
            }
            if (options.EnableP1Extensions && Type(calleeNode).Kind == K.Closure)
                return ClosureCall(node, calleeNode, depth);
            int function = -1;
            if (calleeNode.Kind != N.NameExpression || calleeNode.ReferencedSymbol is null ||
                !_functions.TryGetValue(SymbolKey(calleeNode.ReferencedSymbol), out function)) Unsupported(calleeNode);
            SafeCoreType signature = Type(_functionNodes[function]);
            var arguments = new List<SafeCoreMirOperand>();
            for (int index = 1; index < node.ChildIds.Count && _current is not null; index++)
            {
                SafeCoreMirOperand? argument = Expr(Child(node, index), depth + 1);
                if (argument is not null) arguments.Add(argument);
            }
            if (_current is null) return null;
            int? destination = signature.ReturnType.Kind is K.Unit or K.Never ? null : Temp(signature.ReturnType, node);
            BlockBuilder continuation = Block(node);
            End(SafeCoreMirTerminator.Call(SafeCoreMirOperand.Function(function, signature, Source(calleeNode)),
                arguments, destination, continuation.Id, Source(node), cancellation));
            _current = continuation;
            if (signature.ReturnType.Kind == K.Never)
            {
                End(SafeCoreMirTerminator.Unreachable(Source(node)));
                return null;
            }
            return destination is int local ? SafeCoreMirOperand.Local(local, signature.ReturnType, Source(node)) : Unit(node);
        }

        private SafeCoreMirOperand Print(SafeCoreHirNode node, int depth)
        {
            if (node.ChildIds.Count is < 1 or > 2)
                Unsupported(node);
            SafeCoreHirNode format = Child(node, 0);
            string text = string.Empty;
            if (format.Kind != N.LiteralExpression || format.Value is null ||
                !SyntaxTree.TryDecodeStringLiteral(format.Value, out text))
                Invalid(format);
            SafeCoreMirOperand? value = null;
            if (node.ChildIds.Count == 2)
            {
                if (!string.Equals(text, "{}", StringComparison.Ordinal))
                    Unsupported(format);
                value = Expr(Child(node, 1), depth + 1);
            }
            else if (text.Contains('{', StringComparison.Ordinal) || text.Contains('}', StringComparison.Ordinal))
            {
                Unsupported(format);
            }

            if (_current is null)
                return Unit(node);
            return Emit(SafeCoreMirRvalue.Print(text, value, Source(node), cancellation),
                SafeCoreType.Primitive(K.Unit), node);
        }

        private SafeCoreMirOperand? If(SafeCoreHirNode node, int depth)
        {
            SafeCoreMirOperand? condition = Expr(Child(node, 0), depth + 1);
            if (condition is null || _current is null) return null;
            SafeCoreType type = EffectiveType(node);
            int? destination = type.Kind is K.Unit or K.Never ? null : Temp(type, node);
            BlockBuilder thenBlock = Block(Child(node, 1));
            BlockBuilder elseBlock = Block(node.ChildIds.Count == 3 ? Child(node, 2) : node);
            BlockBuilder join = Block(node);
            End(SafeCoreMirTerminator.Branch(condition, thenBlock.Id, elseBlock.Id, Source(node)));
            _current = thenBlock;
            SafeCoreMirOperand? then = Expr(Child(node, 1), depth + 1);
            bool thenReturns = Join(destination, then, join, node);
            _current = elseBlock;
            SafeCoreMirOperand? other = node.ChildIds.Count == 3 ? Expr(Child(node, 2), depth + 1) : Unit(node);
            bool elseReturns = Join(destination, other, join, node);
            _current = thenReturns || elseReturns ? join : null;
            return _current is null ? null : destination is int local ? SafeCoreMirOperand.Local(local, type, Source(node)) : Unit(node);
        }

        private SafeCoreMirOperand? ShortCircuit(SafeCoreHirNode node, int depth)
        {
            SafeCoreMirOperand? left = Expr(Child(node, 0), depth + 1);
            if (left is null || _current is null) return null;
            int result = Temp(SafeCoreType.Primitive(K.Bool), node);
            BlockBuilder rhs = Block(Child(node, 1)), shortcut = Block(node), join = Block(node);
            bool and = node.Value == "&&";
            End(SafeCoreMirTerminator.Branch(left, and ? rhs.Id : shortcut.Id, and ? shortcut.Id : rhs.Id, Source(node)));
            _current = shortcut;
            Assign(result, SafeCoreMirOperand.Constant(SafeCoreType.Primitive(K.Bool), and ? "false" : "true", Source(node)), node);
            End(SafeCoreMirTerminator.Goto(join.Id, Source(node)));
            _current = rhs;
            _ = Join(result, Expr(Child(node, 1), depth + 1), join, node);
            _current = join;
            return SafeCoreMirOperand.Local(result, SafeCoreType.Primitive(K.Bool), Source(node));
        }

        private SafeCoreMirOperand? Loop(SafeCoreHirNode node, int depth)
        {
            bool isWhile = node.Kind == N.WhileExpression;
            SafeCoreType type = EffectiveType(node);
            int? result = type.Kind is K.Unit or K.Never ? null : Temp(type, node);
            BlockBuilder header = Block(node), body = Block(node), exit = Block(node);
            End(SafeCoreMirTerminator.Goto(header.Id, Source(node)));
            var context = new LoopContext(node.Name, header.Id, exit.Id, result);
            _loops.Add(context);
            _current = header;
            if (isWhile)
            {
                SafeCoreMirOperand? condition = Expr(Child(node, 0), depth + 1);
                if (_current is not null && condition is not null)
                    End(SafeCoreMirTerminator.Branch(condition, body.Id, exit.Id, Source(node)));
                else
                {
                    _loops.RemoveAt(_loops.Count - 1);
                    _current = context.HasBreak ? exit : null;
                    return _current is null ? null : Unit(node);
                }
            }
            else End(SafeCoreMirTerminator.Goto(body.Id, Source(node)));
            _current = body;
            _ = Expr(Child(node, isWhile ? 1 : 0), depth + 1);
            if (_current is not null) End(SafeCoreMirTerminator.Goto(header.Id, Source(node)));
            _loops.RemoveAt(_loops.Count - 1);
            _current = isWhile || context.HasBreak ? exit : null;
            return _current is null ? null : result is int local ? SafeCoreMirOperand.Local(local, type, Source(node)) : Unit(node);
        }

        private SafeCoreMirOperand? LoopControl(SafeCoreHirNode node, int depth)
        {
            LoopContext? context = null;
            for (int index = _loops.Count - 1; index >= 0; index--)
            {
                Step(node, depth);
                if (node.Name is null || node.Name == _loops[index].Label) { context = _loops[index]; break; }
            }
            if (context is null) Invalid(node);
            if (node.Kind == N.ContinueExpression)
            {
                End(SafeCoreMirTerminator.Goto(context!.Header, Source(node)));
                return null;
            }
            SafeCoreMirOperand? value = node.ChildIds.Count == 0 ? Unit(node) : Expr(Child(node, 0), depth + 1);
            if (_current is null || value is null) return null;
            if (context!.Result is int result) Assign(result, value, node);
            context.HasBreak = true;
            End(SafeCoreMirTerminator.Goto(context.Exit, Source(node)));
            return null;
        }

        private bool Join(int? destination, SafeCoreMirOperand? value, BlockBuilder join, SafeCoreHirNode node)
        {
            if (_current is null) return false;
            if (destination is int local)
            {
                if (value is null) Invalid(node);
                Assign(local, value!, node);
            }
            End(SafeCoreMirTerminator.Goto(join.Id, Source(node)));
            return true;
        }

        private SafeCoreMirOperand Literal(SafeCoreHirNode node, SafeCoreType type, bool negate = false,
            SafeCoreHirNode? origin = null)
        {
            Scalar(type, node);
            string text = node.Value ?? string.Empty;
            if (text.Length is 0 or > 4_096) Unsupported(node);
            try
            {
                string value;
                if (type.Kind == K.Bool) value = text;
                else if (type.Kind == K.Char || text.StartsWith("b'", StringComparison.Ordinal))
                {
                    string character = text[(type.Kind == K.Char ? 1 : 2)..^1];
                    int scalar = character.StartsWith("\\u{", StringComparison.Ordinal)
                        ? int.Parse(character[3..^1].Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                        : character.StartsWith("\\x", StringComparison.Ordinal)
                            ? int.Parse(character[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                            : character switch
                            {
                                "\\n" => '\n', "\\r" => '\r', "\\t" => '\t', "\\0" => 0,
                                "\\\\" => '\\', "\\'" => '\'', "\\\"" => '"',
                                _ => Rune.GetRuneAt(character, 0).Value,
                            };
                    value = scalar.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    string suffix = type.ToString();
                    text = text.Replace("_", string.Empty, StringComparison.Ordinal);
                    if (text.EndsWith(suffix, StringComparison.Ordinal)) text = text[..^suffix.Length];
                    if (type.IsFloat)
                    {
                        double number = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                        value = type.Kind == K.F32 ? ((float)number).ToString("R", CultureInfo.InvariantCulture)
                            : number.ToString("R", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        int radix = text.StartsWith("0x", StringComparison.Ordinal) ? 16 :
                            text.StartsWith("0o", StringComparison.Ordinal) ? 8 : text.StartsWith("0b", StringComparison.Ordinal) ? 2 : 10;
                        int start = radix == 10 ? 0 : 2;
                        BigInteger number = BigInteger.Zero;
                        for (int index = start; index < text.Length; index++)
                        {
                            Step(node, 0);
                            char c = char.ToLowerInvariant(text[index]);
                            int digit = c is >= 'a' and <= 'f' ? c - 'a' + 10 : c - '0';
                            if (digit < 0 || digit >= radix) Invalid(node);
                            number = number * radix + digit;
                        }
                        value = (negate ? -number : number).ToString(CultureInfo.InvariantCulture);
                    }
                }
                return SafeCoreMirOperand.Constant(type, value, Source(origin ?? node));
            }
            catch (FormatException) { Invalid(node); }
            catch (OverflowException) { Invalid(node); }
            catch (ArgumentException) { Invalid(node); }
            catch (IndexOutOfRangeException) { Invalid(node); }
            return null!;
        }

        private SafeCoreHirNode UnwrapExpression(SafeCoreHirNode node, int depth)
        {
            for (int index = 0; index <= options.MaximumNestingDepth; index++)
            {
                Step(node, depth + index);
                if (node.Kind != N.TupleExpression || node.ChildIds.Count != 1 ||
                    node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma)) return node;
                node = Child(node, 0);
            }
            Limit(node); return null!;
        }

        private SafeCoreHirNode UnwrapPattern(SafeCoreHirNode node)
        {
            for (int depth = 0; depth <= options.MaximumNestingDepth; depth++)
            {
                Step(node, depth);
                if (node.Kind != N.TuplePattern || node.ChildIds.Count != 1 ||
                    node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.HasTrailingComma)) return node;
                node = Child(node, 0);
            }
            Limit(node); return null!;
        }

        private SafeCoreMirOperand Emit(SafeCoreMirRvalue value, SafeCoreType type, SafeCoreHirNode node)
        {
            int local = Temp(type, node);
            _current!.Statements.Add(new(local, value, Source(node)));
            return SafeCoreMirOperand.Local(local, type, Source(node));
        }

        private void Assign(int local, SafeCoreMirOperand value, SafeCoreHirNode node) =>
            _current!.Statements.Add(new(local, SafeCoreMirRvalue.Use(value, Source(node)), Source(node)));

        private int Temp(SafeCoreType type, SafeCoreHirNode node) => Local(
            $"tmp{_locals.Count.ToString(CultureInfo.InvariantCulture)}", type, SafeCoreMirLocalKind.Temporary, false, node);

        private int Local(string name, SafeCoreType type, SafeCoreMirLocalKind kind, bool mutable, SafeCoreHirNode node)
        {
            Step(node, 0);
            ValueType(type, node);
            if (_locals.Count >= options.MaximumLocalsPerFunction) Limit(node);
            int id = _locals.Count;
            _locals.Add(new(id, name, type, kind, mutable, Source(node))
            {
                IsUnitAdt = options.EnableP1Extensions && type.Kind == K.Adt && IsUnitAdt(type),
                DestructorFunctionId = type.Name is { } identity && _dropFunctions.TryGetValue(identity, out int destructor)
                    ? destructor : null,
            });
            return id;
        }

        private BlockBuilder Block(SafeCoreHirNode node)
        {
            Step(node, 0);
            if (_blocks.Count >= options.MaximumBlocksPerFunction) Limit(node);
            var block = new BlockBuilder(_blocks.Count, Source(node));
            _blocks.Add(block);
            return block;
        }

        private void End(SafeCoreMirTerminator terminator)
        {
            _current!.Terminator = terminator;
            _current = null;
        }

        private SafeCoreType EffectiveType(SafeCoreHirNode node) =>
            input.Coercions.TryGetValue(node.Id, out SafeCoreType? target) ? target : Type(node);

        private SafeCoreType Type(SafeCoreHirNode node)
        {
            if (!input.Types.TryGetValue(node.Id, out SafeCoreType? type) || type is null
                || type.Kind is K.Inference or K.Error)
                Invalid(node);
            return type!;
        }

        private void EmitDropBodies(SafeCoreHirNode returnSite)
        {
            Step(returnSite, 0);
            for (int scope = _dropScopes.Count - 1; scope >= 0 && _current is not null; scope--)
                EmitScopeDrops(scope);
        }

        private void EmitScopeDrops(int scope)
        {
            List<int> drops = _dropScopes[scope];
            for (int index = drops.Count - 1; index >= 0; index--)
            {
                SafeCoreMirLocal local = _locals[drops[index]];
                SafeCoreHirNode method = _functionNodes[local.DestructorFunctionId!.Value];
                Step(method, 0);
                SafeCoreType signature = SafeCoreType.Function([], SafeCoreType.Primitive(K.Unit), Type(method).Name);
                BlockBuilder continuation = Block(method);
                End(new SafeCoreMirTerminator(SafeCoreMirTerminatorKind.Call,
                    SafeCoreMirOperand.Function(local.DestructorFunctionId.Value, signature, Source(method)),
                    [], null, continuation.Id, -1, local.Source, cancellation) { DropLocalId = local.Id });
                _current = continuation;
            }
        }

        private void ValidateDropImplementation(SafeCoreHirNode implementation, int depth)
        {
            SafeCoreHirNode? method = null;
            for (int index = 0; index < implementation.ChildIds.Count; index++)
            {
                SafeCoreHirNode child = Child(implementation, index);
                Step(child, depth);
                if (child.Kind is N.Attribute or N.PathType) continue;
                if (child.Kind != N.Function || child.Name != "drop" || method is not null) Unsupported(child);
                method = child;
            }
            if (method is null || method.ChildIds.Count == 0) Unsupported(implementation);
            SafeCoreType signature = Type(method);
            if (signature.Kind != K.Function || signature.ParameterTypes.Count != 1 ||
                signature.ParameterTypes[0].Kind != K.Reference || !signature.ParameterTypes[0].IsMutable ||
                !IsUnitAdt(signature.ParameterTypes[0].ElementType) || signature.ReturnType.Kind != K.Unit ||
                method.Modifiers.HasFlag(SafeCoreHirNodeModifiers.ConstFunction)) Unsupported(method);
            SafeCoreHirNode body = Child(method, method.ChildIds.Count - 1);
            ValidateDropBody(body, depth + 1);
            if (!_dropFunctions.TryAdd(signature.ParameterTypes[0].ElementType.Name!, _functionNodes.Count)) Unsupported(implementation);
            _destructorNodes.Add(method.Id);
            Collect(method, depth + 1);
        }

        private void ValidateDropBody(SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (node.Kind == N.PrintExpression)
            {
                if (node.ChildIds.Count != 1 || Child(node, 0).Kind != N.LiteralExpression) Unsupported(node);
                return;
            }
            if (node.Kind is not (N.Block or N.BlockExpression or N.ExpressionStatement)) Unsupported(node);
            for (int index = 0; index < node.ChildIds.Count; index++) ValidateDropBody(Child(node, index), depth + 1);
        }

        private static void Scalar(SafeCoreType type, SafeCoreHirNode node, bool allowNever = false)
        {
            if (!(type.IsInteger || type.IsFloat || type.Kind is K.Unit or K.Bool or K.Char || allowNever && type.Kind == K.Never))
                Unsupported(node);
        }

        private static bool IsStructuralCopy(SafeCoreType type) => type.Kind switch
        {
            K.Unit or K.Bool or K.Char or
            K.I8 or K.I16 or K.I32 or K.I64 or K.I128 or K.Isize or
            K.U8 or K.U16 or K.U32 or K.U64 or K.U128 or K.Usize or
            K.F32 or K.F64 => true,
            K.Tuple or K.Array => type.Elements.All(IsStructuralCopy),
            K.Slice => IsStructuralCopy(type.ElementType!),
            _ => false,
        };

        private static bool IsTypeNode(N node) => node is N.PathType or N.ReferenceType or N.TupleType
            or N.ArrayType or N.SliceType or N.UnitType or N.NeverType or N.FunctionType or N.InferredType;

        private void ValueType(SafeCoreType type, SafeCoreHirNode node, bool allowNever = false, int depth = 0)
        {
            Step(node, depth);
            if (type is null || type.Kind is K.Inference or K.Error)
                Invalid(node);
            if (depth > options.MaximumNestingDepth)
                Limit(node);

            // References are represented as bounded aliases in the executable
            // profile. Their element still has to satisfy the same value
            // layout contract so no opaque or unsized value can cross MIR.
            if (type.Kind == K.Reference)
            {
                if (!options.EnableP1Extensions) Unsupported(node);
                SafeCoreType element = type.ElementType!;
                if (element.Kind == K.Slice)
                {
                    // Slices are unsized and cannot occupy a standalone MIR
                    // slot. A reference to a slice is admitted only when the
                    // element has a bounded structural Copy representation;
                    // the executable backend retains the full-array owner.
                    if (!IsStructuralCopy(element.ElementType!)) Unsupported(node);
                    ValueType(element.ElementType!, node, allowNever: false, depth + 1);
                    return;
                }
                if (!IsStructuralCopy(element)) Unsupported(node);
                ValueType(element, node, allowNever: false, depth + 1);
                return;
            }

            // Unit structs carry no runtime fields. Keep their nominal type in
            // MIR for Drop/provenance metadata while allowing the CLR backend
            // to use the zero-field value representation.
            if (options.EnableP1Extensions && type.Kind == K.Adt && IsUnitAdt(type)) return;

            if (type.Kind is K.Tuple or K.Array)
            {
                if (type.Elements.Count > 1024 || type.Kind == K.Array &&
                    (type.Length is not long length || length < 0 || length > options.MaximumOperations))
                    Unsupported(node);
                ValueType(type.ElementType, node, allowNever: false, depth + 1);
                if (type.Kind == K.Tuple)
                    for (int index = 1; index < type.Elements.Count; index++)
                        ValueType(type.Elements[index], node, allowNever: false, depth + 1);
                return;
            }

            Scalar(type, node, allowNever);
        }

        private bool IsUnitAdt(SafeCoreType type) => type.Kind == K.Adt &&
            input.Hir.Nodes.Any(node => node.Kind == N.Struct &&
                node.Modifiers.HasFlag(SafeCoreHirNodeModifiers.UnitStruct) &&
                node.DeclaredSymbol is not null && SymbolKey(node.DeclaredSymbol) == type.Name);

        private SafeCoreHirNode Child(SafeCoreHirNode node, int index)
        {
            if ((uint)index >= (uint)node.ChildIds.Count) Invalid(node);
            int id = node.ChildIds[index];
            if ((uint)id >= (uint)input.Hir.Nodes.Count) Invalid(node);
            return input.Hir.GetNode(id);
        }

        private SafeCoreMirSource Source(SafeCoreHirNode node) =>
            new(input.Hir.SourcePath, node.Span, node.Id, input.Hir.Root!.Span.End);

        private SafeCoreMirOperand Unit(SafeCoreHirNode node) =>
            SafeCoreMirOperand.Constant(SafeCoreType.Primitive(K.Unit), "()", Source(node));

        private static string SymbolKey(SafeCoreSymbol symbol) =>
            symbol.ResolvedImportTargetQualifiedName ?? symbol.QualifiedName;

        private void Step(SafeCoreHirNode node, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (++_operations > options.MaximumOperations || depth > options.MaximumNestingDepth || _clock.Elapsed >= options.Timeout)
                Limit(node);
        }

        [DoesNotReturn]
        private static void Invalid(SafeCoreHirNode node) => throw new LoweringException(new(InvalidEvidence,
            "MIR lowering requires complete and consistent resolved type evidence.", node.Span));

        [DoesNotReturn]
        private static void Unsupported(SafeCoreHirNode node) => throw new LoweringException(new(UnsupportedSyntax,
            "This construct is outside the selected bounded-value MIR execution profile.", node.Span));

        [DoesNotReturn]
        private static void Limit(SafeCoreHirNode node) => throw new LoweringException(new(LimitReached,
            "MIR lowering exceeded its configured work, size, depth or time limit.", node.Span));
    }
}
