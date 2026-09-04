using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace RustSharp.Syntax;

/// <summary>
/// Bounded symbol collection and path-resolution spike for the safe-core AST.
/// The compiler does not call this prototype; it exists to make the P1-03
/// shape and diagnostics reviewable before HIR is introduced.
/// </summary>
public static class SafeCoreNameResolution
{
    private const int AbsoluteMaximumSymbols = 500_000;
    private const int AbsoluteMaximumScopes = 250_000;
    private const int AbsoluteMaximumPathSegments = 512;
    private const int AbsoluteMaximumNameLength = 16_384;
    private const int AbsoluteMaximumPathLength = 65_536;
    private const int AbsoluteMaximumDiagnosticMessageLength = 4_096;
    private const int AbsoluteMaximumDiagnostics = 1024;
    private const int AbsoluteMaximumNestingDepth = 512;
    private const int AbsoluteMaximumOperations = 4_000_000;

    private static readonly HashSet<string> PrimitiveTypeNames = new(StringComparer.Ordinal)
    {
        "bool",
        "char",
        "f32",
        "f64",
        "i8",
        "i16",
        "i32",
        "i64",
        "i128",
        "isize",
        "str",
        "u8",
        "u16",
        "u32",
        "u64",
        "u128",
        "usize",
    };

    /// <summary>Resolves a successfully parsed safe-core syntax result.</summary>
    public static SafeCoreNameResolutionResult Resolve(
        SafeCoreSyntaxResult? syntax,
        SafeCoreNameResolutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        SafeCoreNameResolutionOptions normalized = NormalizeOptions(options);
        if (!syntax.IsSuccessful || syntax.Root is null)
        {
            string detail = syntax.Diagnostics.Count == 0
                ? "The syntax result has no root."
                : syntax.Diagnostics[0].Message;
            return InvalidResult(
                syntax.SourcePath ?? string.Empty,
                $"Cannot resolve an unsuccessful syntax result: {detail}.",
                normalized.MaximumDiagnosticMessageLength);
        }

        return Collect(syntax.Root, syntax.SourcePath, normalized);
    }

    /// <summary>
    /// Collects names directly from an already parsed AST. This overload is
    /// useful for focused prototype tests and deliberately does not enter the
    /// production compiler pipeline.
    /// </summary>
    public static SafeCoreNameResolutionResult Collect(
        SafeCoreCompilationUnitSyntax? root,
        string? sourcePath = null,
        SafeCoreNameResolutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new Resolver(NormalizeOptions(options), sourcePath ?? string.Empty).Run(root);
    }

    private static SafeCoreNameResolutionResult InvalidResult(
        string sourcePath,
        string message,
        int maximumMessageLength) =>
        new(
            LimitText(sourcePath ?? string.Empty, maximumMessageLength),
            null,
            Array.Empty<SafeCoreScope>(),
            Array.Empty<SafeCoreSymbol>(),
            Array.Empty<SafeCorePathResolution>(),
            [new Diagnostic(
                SafeCoreNameResolutionDiagnosticCodes.InvalidSyntax,
                LimitText(message, maximumMessageLength),
                new TextSpan(0, 0))],
            false);

    private static string LimitText(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        if (maximumLength <= 3)
        {
            return value[..maximumLength];
        }

        return value[..(maximumLength - 3)] + "...";
    }

    private static SafeCoreNameResolutionOptions NormalizeOptions(SafeCoreNameResolutionOptions? options)
    {
        options ??= new SafeCoreNameResolutionOptions();
        return new SafeCoreNameResolutionOptions
        {
            MaximumSymbols = Math.Clamp(options.MaximumSymbols, 1, AbsoluteMaximumSymbols),
            MaximumScopes = Math.Clamp(options.MaximumScopes, 1, AbsoluteMaximumScopes),
            MaximumPathSegments = Math.Clamp(options.MaximumPathSegments, 1, AbsoluteMaximumPathSegments),
            MaximumNameLength = Math.Clamp(options.MaximumNameLength, 1, AbsoluteMaximumNameLength),
            MaximumPathLength = Math.Clamp(options.MaximumPathLength, 1, AbsoluteMaximumPathLength),
            MaximumDiagnosticMessageLength = Math.Clamp(
                options.MaximumDiagnosticMessageLength,
                1,
                AbsoluteMaximumDiagnosticMessageLength),
            MaximumDiagnostics = Math.Clamp(options.MaximumDiagnostics, 1, AbsoluteMaximumDiagnostics),
            MaximumNestingDepth = Math.Clamp(options.MaximumNestingDepth, 1, AbsoluteMaximumNestingDepth),
            MaximumOperations = Math.Clamp(options.MaximumOperations, 1, AbsoluteMaximumOperations),
        };
    }

    private sealed class Resolver
    {
        private readonly SafeCoreNameResolutionOptions _options;
        private readonly string _sourcePath;
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly List<ScopeBuilder> _scopes = [];
        private readonly List<SymbolBuilder> _symbols = [];
        private readonly List<SymbolBuilder> _imports = [];
        private readonly List<PathRecord> _pathRecords = [];
        private readonly Dictionary<SafeCoreItemSyntax, ScopeBuilder> _itemScopes =
            new(ReferenceComparer<SafeCoreItemSyntax>.Instance);
        private readonly Dictionary<SafeCoreBlockSyntax, ScopeBuilder> _blockScopes =
            new(ReferenceComparer<SafeCoreBlockSyntax>.Instance);
        private readonly HashSet<SymbolBuilder> _activeImports =
            new(ReferenceComparer<SymbolBuilder>.Instance);
        private ScopeBuilder? _root;
        private int _operations;
        private int _depth;
        private bool _truncated;
        private bool _limitReported;

        public Resolver(SafeCoreNameResolutionOptions options, string sourcePath)
        {
            _options = options;
            _sourcePath = LimitText(sourcePath, options.MaximumPathLength);
        }

        public SafeCoreNameResolutionResult Run(SafeCoreCompilationUnitSyntax rootSyntax)
        {
            _root = CreateScope(parent: null, "crate", "crate");
            if (_root is null)
            {
                return BuildResult();
            }

            _root.Span = rootSyntax.Span;

            CollectItems(rootSyntax.Items, _root, depth: 0);
            ResolveImports();
            ResolveItems(rootSyntax.Items, _root, depth: 0);
            return BuildResult();
        }

        private SafeCoreNameResolutionResult BuildResult()
        {
            if (_root is null)
            {
                return new(
                    _sourcePath,
                    null,
                    Array.Empty<SafeCoreScope>(),
                    Array.Empty<SafeCoreSymbol>(),
                    Array.Empty<SafeCorePathResolution>(),
                    _diagnostics.AsReadOnly(),
                    _truncated);
            }

            var publicSymbols = new Dictionary<SymbolBuilder, SafeCoreSymbol>(ReferenceComparer<SymbolBuilder>.Instance);
            foreach (SymbolBuilder symbol in _symbols)
            {
                publicSymbols[symbol] = ToPublicSymbol(symbol);
            }

            var publicScopes = new List<SafeCoreScope>(_scopes.Count);
            foreach (ScopeBuilder scope in _scopes)
            {
                var scopeSymbols = new List<SafeCoreSymbol>(scope.Symbols.Count);
                foreach (SymbolBuilder symbol in scope.Symbols)
                {
                    scopeSymbols.Add(publicSymbols[symbol]);
                }

                publicScopes.Add(new(
                    scope.Path,
                    scope.Parent?.Path,
                    scope.ModulePath,
                    scopeSymbols.AsReadOnly()));
            }

            var publicRecords = new List<SafeCorePathResolution>(_pathRecords.Count);
            foreach (PathRecord record in _pathRecords)
            {
                var candidates = new List<SafeCoreSymbol>(record.Candidates.Count);
                foreach (SymbolBuilder candidate in record.Candidates)
                {
                    if (publicSymbols.TryGetValue(candidate, out SafeCoreSymbol? publicCandidate))
                    {
                        candidates.Add(publicCandidate);
                    }
                }

                SafeCoreSymbol? symbol = record.Symbol is not null &&
                    publicSymbols.TryGetValue(record.Symbol, out SafeCoreSymbol? publicSymbol)
                    ? publicSymbol
                    : null;
                publicRecords.Add(new(
                    record.Path,
                    record.ScopePath,
                    record.Status,
                    symbol,
                    candidates.AsReadOnly(),
                    record.Span));
            }

            return new(
                _sourcePath,
                publicScopes[0],
                publicScopes.AsReadOnly(),
                _symbols.Select(symbol => publicSymbols[symbol]).ToArray(),
                publicRecords.AsReadOnly(),
                _diagnostics.AsReadOnly(),
                _truncated);
        }

        private static SafeCoreSymbol ToPublicSymbol(SymbolBuilder symbol) => new(
            symbol.Name,
            symbol.QualifiedName,
            symbol.Kind,
            symbol.Namespace,
            symbol.IsPublic,
            symbol.IsImport,
            symbol.TargetPath,
            symbol.Span,
            symbol.DeclaringScope.Path);

        private void CollectItems(
            IReadOnlyList<SafeCoreItemSyntax> items,
            ScopeBuilder scope,
            int depth)
        {
            if (!Enter(depth, scope.Span))
            {
                return;
            }

            try
            {
                int count = items.Count;
                for (var index = 0; index < count; index++)
                {
                    if (!Step(scope.Span))
                    {
                        return;
                    }

                    CollectItem(items[index], scope, depth + 1);
                    if (_truncated)
                    {
                        return;
                    }
                }
            }
            finally
            {
                Exit();
            }
        }

        private void CollectItem(SafeCoreItemSyntax item, ScopeBuilder scope, int depth)
        {
            if (!Step(item.Span))
            {
                return;
            }

            switch (item)
            {
                case SafeCoreModuleSyntax module:
                {
                    SymbolBuilder? symbol = AddSymbol(
                        scope,
                        module.Name,
                        SafeCoreSymbolKind.Module,
                        SafeCoreSymbolNamespace.Type,
                        module.IsPublic,
                        module.Span,
                        isImport: false,
                        targetPath: null);
                    ScopeBuilder? moduleScope = symbol is null
                        ? null
                        : CreateScope(scope, symbol.QualifiedName, symbol.QualifiedName);
                    if (symbol is not null && moduleScope is not null)
                    {
                        moduleScope.Span = module.Span;
                        symbol.MemberScope = moduleScope;
                        _itemScopes[module] = moduleScope;
                        CollectItems(module.Items, moduleScope, depth + 1);
                    }

                    break;
                }
                case SafeCoreUseSyntax use:
                    CollectUse(use, scope);
                    break;
                case SafeCoreFunctionSyntax function:
                    CollectFunction(function, scope, depth + 1);
                    break;
                case SafeCoreStructSyntax structure:
                    CollectStruct(structure, scope);
                    break;
                case SafeCoreEnumSyntax enumeration:
                    CollectEnum(enumeration, scope);
                    break;
                case SafeCoreTypeAliasSyntax alias:
                    CollectTypeAlias(alias, scope);
                    break;
                case SafeCoreConstSyntax constant:
                    _ = AddSymbol(
                        scope,
                        constant.Name,
                        SafeCoreSymbolKind.Const,
                        SafeCoreSymbolNamespace.Value,
                        constant.IsPublic,
                        constant.Span,
                        isImport: false,
                        targetPath: null);
                    break;
                default:
                    AddDiagnostic(
                        SafeCoreNameResolutionDiagnosticCodes.InvalidSyntax,
                        "The syntax tree contains an unsupported safe-core item node.",
                        item.Span);
                    break;
            }
        }

        private void CollectUse(SafeCoreUseSyntax use, ScopeBuilder scope)
        {
            if (!TrySplitPath(use.Path, use.Span, out IReadOnlyList<string>? segments))
            {
                return;
            }

            string name = use.Alias ?? segments[^1];
            SymbolBuilder? symbol = AddSymbol(
                scope,
                name,
                SafeCoreSymbolKind.Import,
                SafeCoreSymbolNamespace.Both,
                use.IsPublic,
                use.Span,
                isImport: true,
                targetPath: use.Path);
            if (symbol is not null)
            {
                _imports.Add(symbol);
            }
        }

        private void CollectFunction(SafeCoreFunctionSyntax function, ScopeBuilder parent, int depth)
        {
            SymbolBuilder? symbol = AddSymbol(
                parent,
                function.Name,
                SafeCoreSymbolKind.Function,
                SafeCoreSymbolNamespace.Value,
                function.IsPublic,
                function.Span,
                isImport: false,
                targetPath: null);
            if (symbol is null)
            {
                return;
            }

            ScopeBuilder? functionScope = CreateScope(parent, symbol.QualifiedName, parent.ModulePath);
            if (functionScope is null)
            {
                return;
            }

            functionScope.Span = function.Span;
            _itemScopes[function] = functionScope;
            CollectGenericParameters(function.GenericParameters, functionScope);
            for (var index = 0; index < function.Parameters.Count; index++)
            {
                if (!Step(function.Parameters[index].Span))
                {
                    return;
                }

                CollectPatternBindings(
                    function.Parameters[index].Pattern,
                    functionScope,
                    SafeCoreSymbolKind.Parameter);
            }

            CollectBlock(function.Body, functionScope, depth + 1, useExistingScope: true);
        }

        private void CollectStruct(SafeCoreStructSyntax structure, ScopeBuilder parent)
        {
            SymbolBuilder? symbol = AddSymbol(
                parent,
                structure.Name,
                SafeCoreSymbolKind.Struct,
                SafeCoreSymbolNamespace.Both,
                structure.IsPublic,
                structure.Span,
                isImport: false,
                targetPath: null);
            if (symbol is null)
            {
                return;
            }

            ScopeBuilder? itemScope = CreateScope(parent, symbol.QualifiedName, parent.ModulePath);
            if (itemScope is null)
            {
                return;
            }

            itemScope.Span = structure.Span;
            _itemScopes[structure] = itemScope;
            CollectGenericParameters(structure.GenericParameters, itemScope);
            for (var index = 0; index < structure.Fields.Count; index++)
            {
                SafeCoreFieldSyntax field = structure.Fields[index];
                if (!Step(field.Span))
                {
                    return;
                }

                if (field.Name is not null)
                {
                    _ = AddSymbol(
                        itemScope,
                        field.Name,
                        SafeCoreSymbolKind.Field,
                        SafeCoreSymbolNamespace.Value,
                        field.IsPublic,
                        field.Span,
                        isImport: false,
                        targetPath: null);
                }
            }
        }

        private void CollectEnum(SafeCoreEnumSyntax enumeration, ScopeBuilder parent)
        {
            SymbolBuilder? symbol = AddSymbol(
                parent,
                enumeration.Name,
                SafeCoreSymbolKind.Enum,
                SafeCoreSymbolNamespace.Type,
                enumeration.IsPublic,
                enumeration.Span,
                isImport: false,
                targetPath: null);
            if (symbol is null)
            {
                return;
            }

            ScopeBuilder? itemScope = CreateScope(parent, symbol.QualifiedName, parent.ModulePath);
            if (itemScope is null)
            {
                return;
            }

            symbol.MemberScope = itemScope;
            itemScope.Span = enumeration.Span;
            _itemScopes[enumeration] = itemScope;
            CollectGenericParameters(enumeration.GenericParameters, itemScope);
            for (var index = 0; index < enumeration.Variants.Count; index++)
            {
                SafeCoreEnumVariantSyntax variant = enumeration.Variants[index];
                if (!Step(variant.Span))
                {
                    return;
                }

                SymbolBuilder? variantSymbol = AddSymbol(
                    itemScope,
                    variant.Name,
                    SafeCoreSymbolKind.EnumVariant,
                    SafeCoreSymbolNamespace.Value,
                    isPublic: true,
                    variant.Span,
                    isImport: false,
                    targetPath: null);
                if (variantSymbol is null)
                {
                    return;
                }
            }
        }

        private void CollectTypeAlias(SafeCoreTypeAliasSyntax alias, ScopeBuilder parent)
        {
            SymbolBuilder? symbol = AddSymbol(
                parent,
                alias.Name,
                SafeCoreSymbolKind.TypeAlias,
                SafeCoreSymbolNamespace.Type,
                alias.IsPublic,
                alias.Span,
                isImport: false,
                targetPath: null);
            if (symbol is null)
            {
                return;
            }

            ScopeBuilder? itemScope = CreateScope(parent, symbol.QualifiedName, parent.ModulePath);
            if (itemScope is null)
            {
                return;
            }

            itemScope.Span = alias.Span;
            _itemScopes[alias] = itemScope;
            CollectGenericParameters(alias.GenericParameters, itemScope);
        }

        private void CollectGenericParameters(
            IReadOnlyList<SafeCoreGenericParameterSyntax> parameters,
            ScopeBuilder scope)
        {
            for (var index = 0; index < parameters.Count; index++)
            {
                SafeCoreGenericParameterSyntax parameter = parameters[index];
                if (!Step(parameter.Span))
                {
                    return;
                }

                _ = AddSymbol(
                    scope,
                    parameter.Name,
                    SafeCoreSymbolKind.GenericParameter,
                    SafeCoreSymbolNamespace.Type,
                    isPublic: false,
                    parameter.Span,
                    isImport: false,
                    targetPath: null);
            }
        }

        private void CollectPatternBindings(
            SafeCorePatternSyntax pattern,
            ScopeBuilder scope,
            SafeCoreSymbolKind kind,
            bool allowShadowing = false,
            HashSet<string>? namesInPattern = null)
        {
            if (!Step(pattern.Span))
            {
                return;
            }

            namesInPattern ??= new HashSet<string>(StringComparer.Ordinal);
            switch (pattern)
            {
                case SafeCoreIdentifierPatternSyntax identifier:
                    string comparisonName = CanonicalizeIdentifier(identifier.Name);
                    if (!namesInPattern.Add(comparisonName))
                    {
                        AddDiagnostic(
                            SafeCoreNameResolutionDiagnosticCodes.DuplicateSymbol,
                            $"The name '{identifier.Name}' is bound more than once in the same pattern.",
                            identifier.Span);
                        break;
                    }

                    _ = AddSymbol(
                        scope,
                        identifier.Name,
                        kind,
                        SafeCoreSymbolNamespace.Value,
                        isPublic: false,
                        identifier.Span,
                        isImport: false,
                        targetPath: null,
                        allowShadowing);
                    break;
                case SafeCoreTuplePatternSyntax tuple:
                    for (var index = 0; index < tuple.Elements.Count; index++)
                    {
                        CollectPatternBindings(tuple.Elements[index], scope, kind, allowShadowing, namesInPattern);
                        if (_truncated)
                        {
                            return;
                        }
                    }

                    break;
                case SafeCorePathPatternSyntax path:
                    for (var index = 0; index < path.Arguments.Count; index++)
                    {
                        CollectPatternBindings(
                            path.Arguments[index],
                            scope,
                            kind,
                            allowShadowing,
                            namesInPattern);
                        if (_truncated)
                        {
                            return;
                        }
                    }

                    break;
            }
        }

        private void CollectBlock(
            SafeCoreBlockSyntax block,
            ScopeBuilder parent,
            int depth,
            bool useExistingScope)
        {
            if (!Enter(depth, block.Span))
            {
                return;
            }

            try
            {
                ScopeBuilder? scope = useExistingScope
                    ? parent
                    : CreateScope(parent, parent.Path + "::<block>", parent.ModulePath);
                if (scope is null)
                {
                    return;
                }

                _blockScopes[block] = scope;
                for (var index = 0; index < block.Statements.Count; index++)
                {
                    SafeCoreStatementSyntax statement = block.Statements[index];
                    if (!Step(statement.Span))
                    {
                        return;
                    }

                    switch (statement)
                    {
                        case SafeCoreLetStatementSyntax let:
                            if (let.Initializer is not null)
                            {
                                CollectNestedExpressionScopes(let.Initializer, scope, depth + 1);
                            }

                            break;
                        case SafeCoreReturnStatementSyntax @return when @return.Value is not null:
                            CollectNestedExpressionScopes(@return.Value, scope, depth + 1);
                            break;
                        case SafeCoreExpressionStatementSyntax expression:
                            CollectNestedExpressionScopes(expression.Expression, scope, depth + 1);
                            break;
                    }

                    if (_truncated)
                    {
                        return;
                    }
                }

                if (block.TailExpression is not null)
                {
                    CollectNestedExpressionScopes(block.TailExpression, scope, depth + 1);
                }
            }
            finally
            {
                Exit();
            }
        }

        private void CollectNestedExpressionScopes(
            SafeCoreExpressionSyntax expression,
            ScopeBuilder parent,
            int depth)
        {
            if (!Enter(depth, expression.Span))
            {
                return;
            }

            try
            {
                if (!Step(expression.Span))
                {
                    return;
                }

                switch (expression)
                {
                    case SafeCoreUnaryExpressionSyntax unary:
                        CollectNestedExpressionScopes(unary.Operand, parent, depth + 1);
                        break;
                    case SafeCoreBinaryExpressionSyntax binary:
                        CollectNestedExpressionScopes(binary.Left, parent, depth + 1);
                        CollectNestedExpressionScopes(binary.Right, parent, depth + 1);
                        break;
                    case SafeCoreCallExpressionSyntax call:
                        CollectNestedExpressionScopes(call.Callee, parent, depth + 1);
                        for (var index = 0; index < call.Arguments.Count; index++)
                        {
                            CollectNestedExpressionScopes(call.Arguments[index], parent, depth + 1);
                            if (_truncated)
                            {
                                return;
                            }
                        }

                        break;
                    case SafeCoreTupleExpressionSyntax tuple:
                        for (var index = 0; index < tuple.Elements.Count; index++)
                        {
                            CollectNestedExpressionScopes(tuple.Elements[index], parent, depth + 1);
                            if (_truncated)
                            {
                                return;
                            }
                        }

                        break;
                    case SafeCoreArrayExpressionSyntax array:
                        for (var index = 0; index < array.Elements.Count; index++)
                        {
                            CollectNestedExpressionScopes(array.Elements[index], parent, depth + 1);
                            if (_truncated)
                            {
                                return;
                            }
                        }

                        if (array.RepeatCount is not null)
                        {
                            CollectNestedExpressionScopes(array.RepeatCount, parent, depth + 1);
                        }

                        break;
                    case SafeCoreBlockExpressionSyntax blockExpression:
                        CollectBlock(blockExpression.Block, parent, depth + 1, useExistingScope: false);
                        break;
                    case SafeCoreIfExpressionSyntax conditional:
                        CollectNestedExpressionScopes(conditional.Condition, parent, depth + 1);
                        CollectBlock(conditional.Then, parent, depth + 1, useExistingScope: false);
                        if (conditional.Else is not null)
                        {
                            CollectNestedExpressionScopes(conditional.Else, parent, depth + 1);
                        }

                        break;
                    case SafeCoreIndexExpressionSyntax indexExpression:
                        CollectNestedExpressionScopes(indexExpression.Target, parent, depth + 1);
                        CollectNestedExpressionScopes(indexExpression.Index, parent, depth + 1);
                        break;
                }
            }
            finally
            {
                Exit();
            }
        }

        private void ResolveImports()
        {
            for (var index = 0; index < _imports.Count; index++)
            {
                if (!Step(_imports[index].Span))
                {
                    return;
                }

                _ = EnsureImportResolved(_imports[index]);
            }

            CheckImportDuplicates();
        }

        private void CheckImportDuplicates()
        {
            for (var scopeIndex = 0; scopeIndex < _scopes.Count; scopeIndex++)
            {
                ScopeBuilder scope = _scopes[scopeIndex];
                var visitedGroups = 0;
                foreach (List<SymbolBuilder> group in scope.ByName.Values)
                {
                    visitedGroups++;
                    if (visitedGroups > _options.MaximumSymbols || !Step(scope.Span))
                    {
                        return;
                    }

                    for (var leftIndex = 0; leftIndex < group.Count; leftIndex++)
                    {
                        for (var rightIndex = leftIndex + 1; rightIndex < group.Count; rightIndex++)
                        {
                            if (!Step(group[rightIndex].Span))
                            {
                                return;
                            }

                            SymbolBuilder left = group[leftIndex];
                            SymbolBuilder right = group[rightIndex];
                            if ((!left.IsImport && !right.IsImport) ||
                                !NamespacesConflict(left.Namespace, right.Namespace))
                            {
                                continue;
                            }

                            AddDiagnostic(
                                SafeCoreNameResolutionDiagnosticCodes.DuplicateSymbol,
                                $"The name '{left.Name}' is declared more than once in '{scope.Path}'.",
                                right.Span);
                        }
                    }
                }
            }
        }

        private ImportResolution EnsureImportResolved(SymbolBuilder import)
        {
            if (_activeImports.Contains(import))
            {
                AddDiagnostic(
                    SafeCoreNameResolutionDiagnosticCodes.ImportCycle,
                    $"Import cycle encountered while resolving '{import.TargetPath}'.",
                    import.Span);
                import.ImportResolutionAttempted = true;
                import.ImportResolution = new(SafeCoreNameResolutionStatus.Ambiguous, null);
                return import.ImportResolution;
            }

            if (import.ImportResolutionAttempted)
            {
                return import.ImportResolution;
            }

            if (_activeImports.Count >= _options.MaximumNestingDepth)
            {
                StopLimit(import.Span);
                import.ImportResolutionAttempted = true;
                import.ImportResolution = new(SafeCoreNameResolutionStatus.LimitExceeded, null);
                return import.ImportResolution;
            }

            import.ImportResolutionAttempted = true;
            if (import.TargetPath is null)
            {
                import.ImportResolution = new(SafeCoreNameResolutionStatus.Invalid, null);
                return import.ImportResolution;
            }

            _activeImports.Add(import);

            try
            {
                PathResult result = ResolvePathInternal(
                    import.TargetPath,
                    import.DeclaringScope,
                    expectedNamespace: null,
                    import.Span,
                    emitDiagnostic: true);
                if (result.Status == SafeCoreNameResolutionStatus.Resolved && result.Symbol is not null)
                {
                    SymbolBuilder resolvedTarget = GetResolvedImportTarget(result.Symbol);
                    import.ResolvedImportTarget = resolvedTarget;
                    import.Namespace = resolvedTarget.Namespace;
                    import.MemberScope = GetMemberScope(resolvedTarget);
                    import.ImportResolution = new(result.Status, resolvedTarget);
                    if (import.IsPublic && !result.Symbol.IsPublic)
                    {
                        AddDiagnostic(
                            SafeCoreNameResolutionDiagnosticCodes.PrivateName,
                            $"Public import '{import.Name}' re-exports a private symbol.",
                            import.Span);
                    }
                }
                else
                {
                    import.ImportResolution = new(result.Status, null);
                }

                return import.ImportResolution;
            }
            finally
            {
                _activeImports.Remove(import);
            }
        }

        private void ResolveItems(
            IReadOnlyList<SafeCoreItemSyntax> items,
            ScopeBuilder scope,
            int depth)
        {
            if (!Enter(depth, scope.Span))
            {
                return;
            }

            try
            {
                for (var index = 0; index < items.Count; index++)
                {
                    SafeCoreItemSyntax item = items[index];
                    if (!Step(item.Span))
                    {
                        return;
                    }

                    switch (item)
                    {
                        case SafeCoreModuleSyntax module:
                            if (_itemScopes.TryGetValue(module, out ScopeBuilder? moduleScope))
                            {
                                ResolveItems(module.Items, moduleScope, depth + 1);
                            }

                            break;
                        case SafeCoreUseSyntax:
                            break;
                        case SafeCoreFunctionSyntax function:
                            ResolveFunction(function, GetItemScope(function, scope), depth + 1);
                            break;
                        case SafeCoreStructSyntax structure:
                            ResolveStruct(structure, GetItemScope(structure, scope));
                            break;
                        case SafeCoreEnumSyntax enumeration:
                            ResolveEnum(enumeration, GetItemScope(enumeration, scope));
                            break;
                        case SafeCoreTypeAliasSyntax alias:
                            ResolveGenericBounds(alias.GenericParameters, GetItemScope(alias, scope));
                            ResolveType(alias.Type, GetItemScope(alias, scope), depth + 1);
                            break;
                        case SafeCoreConstSyntax constant:
                            ResolveType(constant.Type, scope, depth + 1);
                            ResolveExpression(constant.Value, scope, depth + 1);
                            break;
                    }

                    if (_truncated)
                    {
                        return;
                    }
                }
            }
            finally
            {
                Exit();
            }
        }

        private void ResolveFunction(SafeCoreFunctionSyntax function, ScopeBuilder scope, int depth)
        {
            ResolveGenericBounds(function.GenericParameters, scope);
            for (var index = 0; index < function.Parameters.Count; index++)
            {
                SafeCoreParameterSyntax parameter = function.Parameters[index];
                ResolvePattern(parameter.Pattern, scope, depth + 1);
                ResolveType(parameter.Type, scope, depth + 1);
                if (_truncated)
                {
                    return;
                }
            }

            if (function.ReturnType is not null)
            {
                ResolveType(function.ReturnType, scope, depth + 1);
            }

            ResolveBlock(function.Body, scope, depth + 1);
        }

        private void ResolveStruct(SafeCoreStructSyntax structure, ScopeBuilder scope)
        {
            ResolveGenericBounds(structure.GenericParameters, scope);
            for (var index = 0; index < structure.Fields.Count; index++)
            {
                ResolveType(structure.Fields[index].Type, scope, depth: 0);
                if (_truncated)
                {
                    return;
                }
            }
        }

        private void ResolveEnum(SafeCoreEnumSyntax enumeration, ScopeBuilder scope)
        {
            ResolveGenericBounds(enumeration.GenericParameters, scope);
            for (var variantIndex = 0; variantIndex < enumeration.Variants.Count; variantIndex++)
            {
                SafeCoreEnumVariantSyntax variant = enumeration.Variants[variantIndex];
                for (var fieldIndex = 0; fieldIndex < variant.Fields.Count; fieldIndex++)
                {
                    ResolveType(variant.Fields[fieldIndex].Type, scope, depth: 0);
                    if (_truncated)
                    {
                        return;
                    }
                }
            }
        }

        private void ResolveGenericBounds(
            IReadOnlyList<SafeCoreGenericParameterSyntax> parameters,
            ScopeBuilder scope)
        {
            // Trait declarations are outside the current safe-core AST. Keep
            // bounds in the syntax model and defer binding them until that
            // namespace is introduced, avoiding false unresolved diagnostics.
            if (parameters.Count > _options.MaximumPathSegments)
            {
                StopLimit(scope.Span);
            }
        }

        private void ResolveBlock(SafeCoreBlockSyntax block, ScopeBuilder fallbackScope, int depth)
        {
            if (!Enter(depth, block.Span))
            {
                return;
            }

            try
            {
                ScopeBuilder scope = _blockScopes.TryGetValue(block, out ScopeBuilder? mapped)
                    ? mapped
                    : fallbackScope;
                for (var index = 0; index < block.Statements.Count; index++)
                {
                    SafeCoreStatementSyntax statement = block.Statements[index];
                    if (!Step(statement.Span))
                    {
                        return;
                    }

                    switch (statement)
                    {
                        case SafeCoreLetStatementSyntax let:
                            ResolvePattern(let.Pattern, scope, depth + 1);
                            if (let.Type is not null)
                            {
                                ResolveType(let.Type, scope, depth + 1);
                            }

                            if (let.Initializer is not null)
                            {
                                ResolveExpression(let.Initializer, scope, depth + 1);
                            }

                            // A local binding enters scope after its initializer,
                            // which gives Rust-style declaration order and lets a
                            // later `let` shadow an earlier local.
                            CollectPatternBindings(
                                let.Pattern,
                                scope,
                                SafeCoreSymbolKind.Local,
                                allowShadowing: true,
                                namesInPattern: new HashSet<string>(StringComparer.Ordinal));

                            break;
                        case SafeCoreReturnStatementSyntax @return when @return.Value is not null:
                            ResolveExpression(@return.Value, scope, depth + 1);
                            break;
                        case SafeCoreExpressionStatementSyntax expression:
                            ResolveExpression(expression.Expression, scope, depth + 1);
                            break;
                    }

                    if (_truncated)
                    {
                        return;
                    }
                }

                if (block.TailExpression is not null)
                {
                    ResolveExpression(block.TailExpression, scope, depth + 1);
                }
            }
            finally
            {
                Exit();
            }
        }

        private void ResolvePattern(SafeCorePatternSyntax pattern, ScopeBuilder scope, int depth)
        {
            if (!Enter(depth, pattern.Span))
            {
                return;
            }

            try
            {
                switch (pattern)
                {
                    case SafeCorePathPatternSyntax path:
                        ResolveAndRecord(path.Path, scope, SafeCoreSymbolNamespace.Value, path.Span);
                        for (var index = 0; index < path.Arguments.Count; index++)
                        {
                            ResolvePattern(path.Arguments[index], scope, depth + 1);
                        }

                        break;
                    case SafeCoreTuplePatternSyntax tuple:
                        for (var index = 0; index < tuple.Elements.Count; index++)
                        {
                            ResolvePattern(tuple.Elements[index], scope, depth + 1);
                        }

                        break;
                }
            }
            finally
            {
                Exit();
            }
        }

        private void ResolveType(SafeCoreTypeSyntax type, ScopeBuilder scope, int depth)
        {
            if (!Enter(depth, type.Span))
            {
                return;
            }

            try
            {
                switch (type)
                {
                    case SafeCorePathTypeSyntax path:
                    {
                        string pathText = string.Join("::", path.Segments.Select(segment => segment.Name));
                        if (!(path.Segments.Count == 1 && PrimitiveTypeNames.Contains(pathText)))
                        {
                            ResolveAndRecord(pathText, scope, SafeCoreSymbolNamespace.Type, path.Span);
                        }

                        for (var segmentIndex = 0; segmentIndex < path.Segments.Count; segmentIndex++)
                        {
                            SafeCorePathSegmentSyntax segment = path.Segments[segmentIndex];
                            for (var argumentIndex = 0; argumentIndex < segment.GenericArguments.Count; argumentIndex++)
                            {
                                ResolveType(segment.GenericArguments[argumentIndex], scope, depth + 1);
                            }
                        }

                        break;
                    }
                    case SafeCoreReferenceTypeSyntax reference:
                        ResolveType(reference.Inner, scope, depth + 1);
                        break;
                    case SafeCoreTupleTypeSyntax tuple:
                        for (var index = 0; index < tuple.Elements.Count; index++)
                        {
                            ResolveType(tuple.Elements[index], scope, depth + 1);
                        }

                        break;
                    case SafeCoreArrayTypeSyntax array:
                        ResolveType(array.Element, scope, depth + 1);
                        ResolveExpression(array.Length, scope, depth + 1);
                        break;
                    case SafeCoreSliceTypeSyntax slice:
                        ResolveType(slice.Element, scope, depth + 1);
                        break;
                }
            }
            finally
            {
                Exit();
            }
        }

        private void ResolveExpression(SafeCoreExpressionSyntax expression, ScopeBuilder scope, int depth)
        {
            if (!Enter(depth, expression.Span))
            {
                return;
            }

            try
            {
                if (!Step(expression.Span))
                {
                    return;
                }

                switch (expression)
                {
                    case SafeCoreNameExpressionSyntax name:
                        ResolveAndRecord(name.Path, scope, SafeCoreSymbolNamespace.Value, name.Span);
                        break;
                    case SafeCoreUnaryExpressionSyntax unary:
                        ResolveExpression(unary.Operand, scope, depth + 1);
                        break;
                    case SafeCoreBinaryExpressionSyntax binary:
                        ResolveExpression(binary.Left, scope, depth + 1);
                        ResolveExpression(binary.Right, scope, depth + 1);
                        break;
                    case SafeCoreCallExpressionSyntax call:
                        ResolveExpression(call.Callee, scope, depth + 1);
                        for (var index = 0; index < call.Arguments.Count; index++)
                        {
                            ResolveExpression(call.Arguments[index], scope, depth + 1);
                        }

                        break;
                    case SafeCoreTupleExpressionSyntax tuple:
                        for (var index = 0; index < tuple.Elements.Count; index++)
                        {
                            ResolveExpression(tuple.Elements[index], scope, depth + 1);
                        }

                        break;
                    case SafeCoreArrayExpressionSyntax array:
                        for (var index = 0; index < array.Elements.Count; index++)
                        {
                            ResolveExpression(array.Elements[index], scope, depth + 1);
                        }

                        if (array.RepeatCount is not null)
                        {
                            ResolveExpression(array.RepeatCount, scope, depth + 1);
                        }

                        break;
                    case SafeCoreBlockExpressionSyntax block:
                        ResolveBlock(block.Block, scope, depth + 1);
                        break;
                    case SafeCoreIfExpressionSyntax conditional:
                        ResolveExpression(conditional.Condition, scope, depth + 1);
                        ResolveBlock(conditional.Then, scope, depth + 1);
                        if (conditional.Else is not null)
                        {
                            ResolveExpression(conditional.Else, scope, depth + 1);
                        }

                        break;
                    case SafeCoreIndexExpressionSyntax indexExpression:
                        ResolveExpression(indexExpression.Target, scope, depth + 1);
                        ResolveExpression(indexExpression.Index, scope, depth + 1);
                        break;
                }
            }
            finally
            {
                Exit();
            }
        }

        private void ResolveAndRecord(
            string path,
            ScopeBuilder scope,
            SafeCoreSymbolNamespace expectedNamespace,
            TextSpan span)
        {
            PathResult result = ResolvePathInternal(path, scope, expectedNamespace, span, emitDiagnostic: true);
            if (!Step(span))
            {
                return;
            }

            var candidates = result.Candidates
                .Distinct(ReferenceComparer<SymbolBuilder>.Instance)
                .ToArray();
            _pathRecords.Add(new(LimitText(path ?? string.Empty, _options.MaximumPathLength), scope.Path, result.Status, result.Symbol, candidates, span));
        }

        private PathResult ResolvePathInternal(
            string path,
            ScopeBuilder context,
            SafeCoreSymbolNamespace? expectedNamespace,
            TextSpan span,
            bool emitDiagnostic)
        {
            string safePath = path ?? string.Empty;
            if (!TrySplitPath(safePath, span, out IReadOnlyList<string>? segments))
            {
                return new(SafeCoreNameResolutionStatus.Invalid, null, []);
            }

            if (expectedNamespace == SafeCoreSymbolNamespace.Type &&
                segments.Count == 1 &&
                PrimitiveTypeNames.Contains(segments[0]))
            {
                return new(SafeCoreNameResolutionStatus.Resolved, null, []);
            }

            string trimmedPath = safePath.Trim();
            bool absoluteRoot = trimmedPath.StartsWith("::", StringComparison.Ordinal);
            ScopeBuilder? start;
            var position = 0;
            if (absoluteRoot && segments[0] != "crate")
            {
                start = _root;
            }
            else if (segments[0] == "crate")
            {
                start = _root;
                position = 1;
            }
            else if (segments[0] == "self")
            {
                start = FindModuleScope(context);
                position = 1;
            }
            else
            {
                start = null;
            }

            if (!absoluteRoot && segments[0] == "super")
            {
                start = FindModuleScope(context);
                while (position < segments.Count && segments[position] == "super")
                {
                    if (!Step(span))
                    {
                        return new(SafeCoreNameResolutionStatus.LimitExceeded, null, []);
                    }

                    start = start?.Parent;
                    position++;
                }
            }

            if (start is not null && position == segments.Count)
            {
                return ReportPathResult(
                    new(SafeCoreNameResolutionStatus.Resolved, null, []),
                    safePath,
                    span,
                    emitDiagnostic);
            }

            if (start is null)
            {
                start = FindScopeContainingFirstSegment(
                    context,
                    segments[0],
                    segments.Count == 1 ? expectedNamespace : null,
                    out PathResult? firstResult);
                if (firstResult is not null)
                {
                    if (firstResult.Status != SafeCoreNameResolutionStatus.Resolved || position + 1 >= segments.Count)
                    {
                        return ReportPathResult(firstResult, safePath, span, emitDiagnostic);
                    }

                    position = 1;
                    return ResolveRemaining(
                        firstResult.Symbol,
                        position,
                        segments,
                        context,
                        expectedNamespace,
                        safePath,
                        span,
                        emitDiagnostic);
                }
            }

            if (start is null || position >= segments.Count)
            {
                return ReportPathResult(
                    new(SafeCoreNameResolutionStatus.Unresolved, null, []),
                    safePath,
                    span,
                    emitDiagnostic);
            }

            IReadOnlyList<SymbolBuilder> candidates = LookupAccessible(
                start,
                segments[position],
                position + 1 == segments.Count ? expectedNamespace : null,
                context,
                out bool privateOnly);
            if (candidates.Count == 0)
            {
                PathResult missing = new(
                    privateOnly ? SafeCoreNameResolutionStatus.Private : SafeCoreNameResolutionStatus.Unresolved,
                    null,
                    []);
                return ReportPathResult(missing, safePath, span, emitDiagnostic);
            }

            PathResult first = SelectCandidates(candidates);
            if (first.Status != SafeCoreNameResolutionStatus.Resolved || position + 1 == segments.Count)
            {
                return ReportPathResult(first, safePath, span, emitDiagnostic);
            }

            return ResolveRemaining(first.Symbol, position + 1, segments, context, expectedNamespace, safePath, span, emitDiagnostic);
        }

        private PathResult ResolveRemaining(
            SymbolBuilder? first,
            int position,
            IReadOnlyList<string> segments,
            ScopeBuilder context,
            SafeCoreSymbolNamespace? expectedNamespace,
            string path,
            TextSpan span,
            bool emitDiagnostic)
        {
            if (first is null)
            {
                return ReportPathResult(
                    new(SafeCoreNameResolutionStatus.Unresolved, null, []),
                    path,
                    span,
                    emitDiagnostic);
            }

            SymbolBuilder current = first;
            while (position < segments.Count)
            {
                if (!Step(span))
                {
                    return new(SafeCoreNameResolutionStatus.LimitExceeded, null, [current]);
                }

                SymbolBuilder memberOwner = GetResolvedImportTarget(current);
                ScopeBuilder? memberScope = GetMemberScope(memberOwner);
                if (memberScope is null)
                {
                    return ReportPathResult(
                        new(SafeCoreNameResolutionStatus.Unresolved, null, [current]),
                        path,
                        span,
                        emitDiagnostic);
                }

                List<SymbolBuilder> next = LookupAccessible(
                    memberScope,
                    segments[position],
                    expectedNamespace: position + 1 == segments.Count ? expectedNamespace : null,
                    context,
                    out bool privateOnly,
                    requiredKind: memberOwner.Kind == SafeCoreSymbolKind.Enum
                        ? SafeCoreSymbolKind.EnumVariant
                        : null);

                if (next.Count == 0)
                {
                    return ReportPathResult(
                        new(
                            privateOnly ? SafeCoreNameResolutionStatus.Private : SafeCoreNameResolutionStatus.Unresolved,
                            null,
                            [current]),
                        path,
                        span,
                        emitDiagnostic);
                }

                PathResult selected = SelectCandidates(next);
                if (selected.Status != SafeCoreNameResolutionStatus.Resolved || selected.Symbol is null)
                {
                    return ReportPathResult(selected, path, span, emitDiagnostic);
                }

                current = selected.Symbol;
                position++;
            }

            return ReportPathResult(
                new(SafeCoreNameResolutionStatus.Resolved, current, [current]),
                path,
                span,
                emitDiagnostic);
        }

        private PathResult SelectCandidates(IReadOnlyList<SymbolBuilder> candidates)
        {
            if (candidates.Count > 1)
            {
                return new(SafeCoreNameResolutionStatus.Ambiguous, null, candidates);
            }

            SymbolBuilder candidate = candidates[0];
            if (candidate.IsImport)
            {
                ImportResolution import = EnsureImportResolved(candidate);
                if (import.Status != SafeCoreNameResolutionStatus.Resolved)
                {
                    return new(import.Status, null, candidates);
                }
            }

            return new(SafeCoreNameResolutionStatus.Resolved, candidate, candidates);
        }

        private PathResult ReportPathResult(
            PathResult result,
            string path,
            TextSpan span,
            bool emitDiagnostic)
        {
            if (emitDiagnostic)
            {
                string? message = result.Status switch
                {
                    SafeCoreNameResolutionStatus.Invalid => $"Invalid safe-core path '{LimitText(path ?? string.Empty, _options.MaximumPathLength)}'.",
                    SafeCoreNameResolutionStatus.Unresolved => $"Could not resolve '{LimitText(path ?? string.Empty, _options.MaximumPathLength)}'.",
                    SafeCoreNameResolutionStatus.Ambiguous => $"Path '{LimitText(path ?? string.Empty, _options.MaximumPathLength)}' resolves to multiple symbols.",
                    SafeCoreNameResolutionStatus.Private => $"Path '{LimitText(path ?? string.Empty, _options.MaximumPathLength)}' refers to a private symbol.",
                    SafeCoreNameResolutionStatus.LimitExceeded => "Name resolution stopped after reaching a configured safety limit.",
                    _ => null,
                };
                if (message is not null)
                {
                    string code = result.Status switch
                    {
                        SafeCoreNameResolutionStatus.Invalid => SafeCoreNameResolutionDiagnosticCodes.InvalidPath,
                        SafeCoreNameResolutionStatus.Unresolved => SafeCoreNameResolutionDiagnosticCodes.UnresolvedName,
                        SafeCoreNameResolutionStatus.Ambiguous => SafeCoreNameResolutionDiagnosticCodes.AmbiguousName,
                        SafeCoreNameResolutionStatus.Private => SafeCoreNameResolutionDiagnosticCodes.PrivateName,
                        _ => SafeCoreNameResolutionDiagnosticCodes.LimitReached,
                    };
                    AddDiagnostic(code, message, span);
                }
            }

            return result;
        }

        private ScopeBuilder? FindScopeContainingFirstSegment(
            ScopeBuilder context,
            string segment,
            SafeCoreSymbolNamespace? expectedNamespace,
            out PathResult? result)
        {
            for (ScopeBuilder? scope = context; scope is not null; scope = scope.Parent)
            {
                if (!Step(scope.Span))
                {
                    result = new PathResult(SafeCoreNameResolutionStatus.LimitExceeded, null, []);
                    return scope;
                }

                IReadOnlyList<SymbolBuilder> candidates = LookupAccessible(
                    scope,
                    segment,
                    expectedNamespace,
                    context,
                    out bool privateOnly);
                if (candidates.Count != 0 || privateOnly)
                {
                    PathResult selected = candidates.Count == 0
                        ? new(SafeCoreNameResolutionStatus.Private, null, [])
                        : SelectCandidates(candidates);
                    result = selected;
                    return scope;
                }
            }

            result = null;
            return null;
        }

        private List<SymbolBuilder> LookupAccessible(
            ScopeBuilder scope,
            string name,
            SafeCoreSymbolNamespace? expectedNamespace,
            ScopeBuilder requester,
            out bool privateOnly,
            SafeCoreSymbolKind? requiredKind = null)
        {
            privateOnly = false;
            string canonicalName = CanonicalizeIdentifier(name);
            if (!scope.ByName.TryGetValue(canonicalName, out List<SymbolBuilder>? declared))
            {
                return [];
            }

            var accessible = new List<SymbolBuilder>(declared.Count);
            SymbolBuilder? latestLocal = null;
            for (var index = 0; index < declared.Count; index++)
            {
                SymbolBuilder symbol = declared[index];
                if (!Step(symbol.Span))
                {
                    return accessible;
                }

                if (expectedNamespace is not null && !MatchesNamespace(symbol.Namespace, expectedNamespace.Value))
                {
                    continue;
                }

                if (requiredKind is not null && symbol.Kind != requiredKind.Value)
                {
                    continue;
                }

                if (!IsAccessible(symbol, requester))
                {
                    privateOnly = true;
                    continue;
                }

                accessible.Add(symbol);
                if (symbol.Kind == SafeCoreSymbolKind.Local)
                {
                    latestLocal = symbol;
                }
            }

            return latestLocal is null ? accessible : [latestLocal];
        }

        private static bool MatchesNamespace(
            SafeCoreSymbolNamespace declared,
            SafeCoreSymbolNamespace requested) =>
            declared == SafeCoreSymbolNamespace.Both || declared == requested;

        private static bool IsAccessible(SymbolBuilder symbol, ScopeBuilder requester)
        {
            if (symbol.IsPublic)
            {
                return true;
            }

            string owner = symbol.DeclaringScope.ModulePath;
            string requesterPath = requester.ModulePath;
            return string.Equals(owner, requesterPath, StringComparison.Ordinal) ||
                requesterPath.StartsWith(owner + "::", StringComparison.Ordinal);
        }

        private ScopeBuilder FindModuleScope(ScopeBuilder scope)
        {
            ScopeBuilder current = scope;
            while (current.Parent is not null &&
                !string.Equals(current.Path, current.ModulePath, StringComparison.Ordinal))
            {
                if (!Step(current.Span))
                {
                    break;
                }

                current = current.Parent;
            }

            return current;
        }

        private static SymbolBuilder GetResolvedImportTarget(SymbolBuilder symbol) =>
            symbol.IsImport && symbol.ResolvedImportTarget is not null
                ? symbol.ResolvedImportTarget
                : symbol;

        private static ScopeBuilder? GetMemberScope(SymbolBuilder symbol) =>
            symbol.Kind is SafeCoreSymbolKind.Module or SafeCoreSymbolKind.Enum
                ? symbol.MemberScope
                : null;

        private ScopeBuilder GetItemScope(SafeCoreItemSyntax item, ScopeBuilder fallback) =>
            _itemScopes.TryGetValue(item, out ScopeBuilder? scope) ? scope : fallback;

        private ScopeBuilder? CreateScope(ScopeBuilder? parent, string requestedPath, string modulePath)
        {
            if (!Step(parent?.Span ?? new TextSpan(0, 0)))
            {
                return null;
            }

            if (_scopes.Count >= _options.MaximumScopes)
            {
                StopLimit(parent?.Span ?? new TextSpan(0, 0));
                return null;
            }

            string path = requestedPath;
            if (parent is not null)
            {
                var suffix = 1;
                for (; suffix <= _options.MaximumScopes; suffix++)
                {
                    bool collision = false;
                    for (var childIndex = 0; childIndex < parent.Children.Count; childIndex++)
                    {
                        if (!Step(parent.Span))
                        {
                            return null;
                        }

                        if (string.Equals(parent.Children[childIndex].Path, path, StringComparison.Ordinal))
                        {
                            collision = true;
                            break;
                        }
                    }

                    if (!collision)
                    {
                        break;
                    }

                    path = requestedPath + "#" + suffix;
                }

                if (suffix > _options.MaximumScopes)
                {
                    StopLimit(parent.Span);
                    return null;
                }
            }

            var scope = new ScopeBuilder(path, modulePath, parent)
            {
                Span = parent?.Span ?? new TextSpan(0, 0),
            };
            _scopes.Add(scope);
            parent?.Children.Add(scope);
            return scope;
        }

        private SymbolBuilder? AddSymbol(
            ScopeBuilder scope,
            string name,
            SafeCoreSymbolKind kind,
            SafeCoreSymbolNamespace @namespace,
            bool isPublic,
            TextSpan span,
            bool isImport,
            string? targetPath,
            bool allowShadowing = false)
        {
            if (!Step(span))
            {
                return null;
            }

            if (name is null || name.Length == 0 || name.Length > _options.MaximumNameLength)
            {
                AddDiagnostic(
                    SafeCoreNameResolutionDiagnosticCodes.InvalidPath,
                    $"Symbol name '{LimitText(name ?? string.Empty, _options.MaximumNameLength)}' is empty or exceeds the configured name limit.",
                    span);
                return null;
            }

            if (IsForbiddenRawIdentifier(name))
            {
                AddDiagnostic(
                    SafeCoreNameResolutionDiagnosticCodes.InvalidPath,
                    $"Symbol name '{name}' uses a forbidden raw identifier.",
                    span);
                return null;
            }

            if (!IsValidIdentifier(name, span))
            {
                AddDiagnostic(
                    SafeCoreNameResolutionDiagnosticCodes.InvalidPath,
                    $"Symbol name '{LimitText(name, _options.MaximumNameLength)}' is not a valid Rust identifier.",
                    span);
                return null;
            }

            string canonicalName = CanonicalizeIdentifier(name);
            if (_symbols.Count >= _options.MaximumSymbols)
            {
                StopLimit(span);
                return null;
            }

            var symbol = new SymbolBuilder(
                canonicalName,
                scope.Path == "crate" ? "crate::" + canonicalName : scope.Path + "::" + canonicalName,
                kind,
                @namespace,
                isPublic,
                isImport,
                targetPath,
                span,
                scope);
            if (scope.ByName.TryGetValue(canonicalName, out List<SymbolBuilder>? existing))
            {
                if (!isImport && !allowShadowing)
                {
                    for (var index = 0; index < existing.Count; index++)
                    {
                        if (!Step(span))
                        {
                            return null;
                        }

                        if (!existing[index].IsImport && NamespacesConflict(existing[index].Namespace, @namespace))
                        {
                            AddDiagnostic(
                                SafeCoreNameResolutionDiagnosticCodes.DuplicateSymbol,
                                $"The name '{canonicalName}' is declared more than once in '{scope.Path}'.",
                                span);
                            break;
                        }
                    }
                }
            }
            else
            {
                existing = [];
                scope.ByName.Add(canonicalName, existing);
            }

            existing.Add(symbol);
            scope.Symbols.Add(symbol);
            _symbols.Add(symbol);
            return symbol;
        }

        private static bool NamespacesConflict(
            SafeCoreSymbolNamespace left,
            SafeCoreSymbolNamespace right) =>
            left == SafeCoreSymbolNamespace.Both ||
            right == SafeCoreSymbolNamespace.Both ||
            left == right;

        private static string CanonicalizeIdentifier(string name) =>
            RustIdentifierFacts.Canonicalize(name);

        private static bool IsForbiddenRawIdentifier(string name) =>
            RustIdentifierFacts.IsForbiddenRawIdentifier(name);

        private bool TrySplitPath(
            string path,
            TextSpan span,
            out IReadOnlyList<string> segments)
        {
            segments = Array.Empty<string>();
            if (!Step(span))
            {
                return false;
            }

            if (path is null)
            {
                AddDiagnostic(
                    SafeCoreNameResolutionDiagnosticCodes.InvalidPath,
                    "A safe-core path cannot be null.",
                    span);
                return false;
            }

            if (path.Length > _options.MaximumPathLength)
            {
                AddDiagnostic(
                    SafeCoreNameResolutionDiagnosticCodes.InvalidPath,
                    $"Path '{LimitText(path, _options.MaximumPathLength)}' exceeds the configured path length limit.",
                    span);
                return false;
            }

            string value = path.Trim();
            if (value.StartsWith("::", StringComparison.Ordinal))
            {
                value = value[2..];
            }

            string[] pieces = value.Split("::", StringSplitOptions.None);
            if (pieces.Length == 0 || pieces.Length > _options.MaximumPathSegments)
            {
                AddDiagnostic(
                    SafeCoreNameResolutionDiagnosticCodes.InvalidPath,
                    $"Path '{path}' exceeds the configured segment limit or is empty.",
                    span);
                return false;
            }

            var canonicalPieces = new string[pieces.Length];
            for (var index = 0; index < pieces.Length; index++)
            {
                if (!Step(span))
                {
                    return false;
                }

                string piece = pieces[index];
                if (!IsPathSegment(piece, span))
                {
                    AddDiagnostic(
                        SafeCoreNameResolutionDiagnosticCodes.InvalidPath,
                        $"Path '{path}' contains invalid segment '{piece}'.",
                        span);
                    return false;
                }

                canonicalPieces[index] = CanonicalizeIdentifier(piece);
            }

            segments = canonicalPieces;
            return true;
        }

        private bool IsPathSegment(string value, TextSpan span)
        {
            if (value.Length == 0)
            {
                return false;
            }

            if (IsForbiddenRawIdentifier(value))
            {
                return false;
            }

            return IsValidIdentifier(value, span);
        }

        private bool IsValidIdentifier(string value, TextSpan span)
        {
            int start = value.StartsWith("r#", StringComparison.Ordinal) ? 2 : 0;
            if (start == value.Length)
            {
                return false;
            }

            ReadOnlySpan<char> identifier = value.AsSpan(start);
            OperationStatus firstStatus = Rune.DecodeFromUtf16(
                identifier,
                out Rune first,
                out int firstWidth);
            if (firstStatus != OperationStatus.Done || !RustIdentifierFacts.IsIdentifierStart(first))
            {
                return false;
            }

            int index = firstWidth;
            for (int scalarCount = 1;
                 index < identifier.Length && scalarCount < identifier.Length;
                 scalarCount++)
            {
                if (!Step(span))
                {
                    return false;
                }

                OperationStatus status = Rune.DecodeFromUtf16(
                    identifier[index..],
                    out Rune current,
                    out int width);
                if (status != OperationStatus.Done || !RustIdentifierFacts.IsIdentifierContinue(current))
                {
                    return false;
                }

                index += width;
            }

            return index == identifier.Length;
        }

        private bool Enter(int depth, TextSpan span)
        {
            if (_truncated || depth > _options.MaximumNestingDepth)
            {
                StopLimit(span);
                return false;
            }

            _depth++;
            return true;
        }

        private void Exit()
        {
            if (_depth > 0)
            {
                _depth--;
            }
        }

        private bool Step(TextSpan span)
        {
            if (_truncated)
            {
                return false;
            }

            _operations++;
            if (_operations <= _options.MaximumOperations)
            {
                return true;
            }

            StopLimit(span);
            return false;
        }

        private void StopLimit(TextSpan span)
        {
            _truncated = true;
            if (_limitReported)
            {
                return;
            }

            _limitReported = true;
            AddDiagnosticCore(
                SafeCoreNameResolutionDiagnosticCodes.LimitReached,
                "Safe-core name resolution stopped after reaching a configured safety limit.",
                span);
        }

        private void AddDiagnostic(string code, string message, TextSpan span)
        {
            if (_diagnostics.Count >= _options.MaximumDiagnostics - 1)
            {
                StopLimit(span);
                return;
            }

            AddDiagnosticCore(code, message, span);
        }

        private void AddDiagnosticCore(string code, string message, TextSpan span)
        {
            if (_diagnostics.Count >= _options.MaximumDiagnostics)
            {
                return;
            }

            _diagnostics.Add(new Diagnostic(
                code,
                LimitText(message ?? string.Empty, _options.MaximumDiagnosticMessageLength),
                span));
        }

        private sealed class ScopeBuilder
        {
            public ScopeBuilder(string path, string modulePath, ScopeBuilder? parent)
            {
                Path = path;
                ModulePath = modulePath;
                Parent = parent;
            }

            public string Path { get; }
            public string ModulePath { get; }
            public ScopeBuilder? Parent { get; }
            public TextSpan Span { get; set; }
            public List<ScopeBuilder> Children { get; } = [];
            public List<SymbolBuilder> Symbols { get; } = [];
            public Dictionary<string, List<SymbolBuilder>> ByName { get; } = new(StringComparer.Ordinal);
        }

        private sealed class SymbolBuilder
        {
            public SymbolBuilder(
                string name,
                string qualifiedName,
                SafeCoreSymbolKind kind,
                SafeCoreSymbolNamespace @namespace,
                bool isPublic,
                bool isImport,
                string? targetPath,
                TextSpan span,
                ScopeBuilder declaringScope)
            {
                Name = name;
                QualifiedName = qualifiedName;
                Kind = kind;
                Namespace = @namespace;
                IsPublic = isPublic;
                IsImport = isImport;
                TargetPath = targetPath;
                Span = span;
                DeclaringScope = declaringScope;
            }

            public string Name { get; }
            public string QualifiedName { get; }
            public SafeCoreSymbolKind Kind { get; }
            public SafeCoreSymbolNamespace Namespace { get; set; }
            public bool IsPublic { get; }
            public bool IsImport { get; }
            public string? TargetPath { get; }
            public TextSpan Span { get; }
            public ScopeBuilder DeclaringScope { get; }
            public ScopeBuilder? MemberScope { get; set; }
            public bool ImportResolutionAttempted { get; set; }
            public ImportResolution ImportResolution { get; set; } =
                new(SafeCoreNameResolutionStatus.Invalid, null);
            public SymbolBuilder? ResolvedImportTarget { get; set; }
        }

        private sealed record ImportResolution(SafeCoreNameResolutionStatus Status, SymbolBuilder? Target);

        private sealed record PathResult(
            SafeCoreNameResolutionStatus Status,
            SymbolBuilder? Symbol,
            IReadOnlyList<SymbolBuilder> Candidates);

        private sealed record PathRecord(
            string Path,
            string ScopePath,
            SafeCoreNameResolutionStatus Status,
            SymbolBuilder? Symbol,
            IReadOnlyList<SymbolBuilder> Candidates,
            TextSpan Span);
    }

    private sealed class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class
    {
        public static ReferenceComparer<T> Instance { get; } = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
