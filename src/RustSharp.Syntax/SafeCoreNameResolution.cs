using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace RustSharp.Syntax;

/// <summary>
/// Bounded symbol collection and path resolution for the safe-core AST.
/// Feeds HIR lowering and the opt-in primitive compilation profile.
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
            EnableTypeSystemExtensions = options.EnableTypeSystemExtensions,
            Timeout = options.Timeout > TimeSpan.Zero && options.Timeout <= TimeSpan.FromMinutes(1)
                ? options.Timeout : TimeSpan.FromSeconds(10),
            CancellationToken = options.CancellationToken,
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
        private readonly Dictionary<TextSpan, SymbolBuilder> _patternBindings = [];
        private readonly HashSet<TextSpan> _patternReferenceSpans = [];
        private readonly Dictionary<SafeCoreExpressionSyntax, ScopeBuilder> _expressionScopes = new(ReferenceComparer<SafeCoreExpressionSyntax>.Instance);
        private readonly Dictionary<SafeCoreMatchArmSyntax, ScopeBuilder> _armScopes = new(ReferenceComparer<SafeCoreMatchArmSyntax>.Instance);
        private readonly Dictionary<SymbolBuilder, SafeCoreTypeAliasSyntax> _aliasDefinitions = new(ReferenceComparer<SymbolBuilder>.Instance);
        private readonly HashSet<SymbolBuilder> _aliasMemberStack = new(ReferenceComparer<SymbolBuilder>.Instance);
        private readonly List<ScopeBuilder> _scopes = [];
        private readonly List<SymbolBuilder> _symbols = [];
        private readonly List<SymbolBuilder> _imports = [];
        private readonly List<GlobImport> _globImports = [];
        private readonly List<(string Path, ScopeBuilder Scope, TextSpan Span)> _importGroupPrefixes = [];
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
        private bool _probingImports;
        private readonly long _started = Stopwatch.GetTimestamp();

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

            // Documentation comments are retained as inert `doc` attributes in
            // the HIR. Every other attribute would require evaluation (for
            // example `cfg`, `path`, or a tool attribute), which is outside
            // this profile and must fail with the semantic boundary code.
            RejectUnsupportedRootAttributes(rootSyntax.Attributes, rootSyntax.Span);
            if (_diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax))
            {
                return BuildResult();
            }

            CollectItems(rootSyntax.Items, _root, depth: 0);
            if (_diagnostics.Any(static diagnostic => diagnostic.Code == SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax))
            {
                return BuildResult();
            }
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
                if (symbol.IsGlobImport && !symbol.IsActiveGlobImport) continue;
                if (symbol.ImportPeer is { } peer)
                {
                    if (symbol.ImportResolution.Status != SafeCoreNameResolutionStatus.Resolved &&
                        peer.ImportResolution.Status == SafeCoreNameResolutionStatus.Resolved) continue;
                    if (symbol.ImportResolution.Status == peer.ImportResolution.Status &&
                        ReferenceEquals(symbol.ResolvedImportTarget, peer.ResolvedImportTarget) &&
                        symbol.VisibilityScopePath == peer.VisibilityScopePath)
                    {
                        if (publicSymbols.TryGetValue(peer, out SafeCoreSymbol? shared)) publicSymbols[symbol] = shared;
                        else publicSymbols[symbol] = ToPublicSymbol(symbol) with { Namespace = SafeCoreSymbolNamespace.Both };
                        continue;
                    }
                }
                publicSymbols[symbol] = ToPublicSymbol(symbol);
            }

            var publicScopes = new List<SafeCoreScope>(_scopes.Count);
            foreach (ScopeBuilder scope in _scopes)
            {
                var scopeSymbols = new List<SafeCoreSymbol>(scope.Symbols.Count);
                var seenSymbols = new HashSet<SafeCoreSymbol>();
                foreach (SymbolBuilder symbol in scope.Symbols)
                {
                    if (publicSymbols.TryGetValue(symbol, out SafeCoreSymbol? publicSymbol) && seenSymbols.Add(publicSymbol))
                        scopeSymbols.Add(publicSymbol);
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
                var seenCandidates = new HashSet<SafeCoreSymbol>();
                foreach (SymbolBuilder candidate in record.Candidates)
                {
                    if (publicSymbols.TryGetValue(candidate, out SafeCoreSymbol? publicCandidate))
                    {
                        if (seenCandidates.Add(publicCandidate)) candidates.Add(publicCandidate);
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

            var publicPatternBindings = new Dictionary<TextSpan, SafeCoreSymbol>();
            foreach (var binding in _patternBindings)
            {
                if (!Step(binding.Key)) break;
                if (publicSymbols.TryGetValue(binding.Value, out SafeCoreSymbol? symbol))
                    publicPatternBindings[binding.Key] = symbol;
            }
            return new(
                _sourcePath,
                publicScopes[0],
                publicScopes.AsReadOnly(),
                publicSymbols.Values.Distinct().ToArray(),
                publicRecords.AsReadOnly(),
                _diagnostics.AsReadOnly(),
                _truncated)
            {
                PatternBindings = new System.Collections.ObjectModel.ReadOnlyDictionary<TextSpan, SafeCoreSymbol>(
                    publicPatternBindings),
            };
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
            symbol.DeclaringScope.Path)
        {
            ResolvedImportTargetQualifiedName = symbol.ResolvedImportTarget?.QualifiedName,
            VisibilityScopePath = symbol.VisibilityScopePath,
            IsAnonymousImport = symbol.IsAnonymousImport,
        };

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

            RejectUnsupportedAttributes(item.Attributes, item.Span);
            if (_truncated)
            {
                return;
            }

            if (item is SafeCoreModuleSyntax { IsExternal: true } or
                    SafeCoreFunctionSyntax { WhereClause: not null } or
                    SafeCoreStructSyntax { WhereClause: not null } or SafeCoreEnumSyntax { WhereClause: not null } or
                    SafeCoreTypeAliasSyntax { WhereClause: not null } or SafeCoreConstSyntax { Name: "_" })
            {
                RejectNewSyntax(item.Span);
                return;
            }

            if (item is SafeCoreModuleSyntax moduleItem &&
                HasUnsupportedAttributes(moduleItem.InnerAttributes))
            {
                RejectUnsupportedAttributes(moduleItem.InnerAttributes, moduleItem.Span);
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
                        targetPath: null,
                        visibility: module.Visibility);
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
                        targetPath: null,
                        visibility: constant.Visibility);
                    if (_options.EnableTypeSystemExtensions) CollectNestedExpressionScopes(constant.Value, scope, depth + 1);
                    break;
                default:
                    RejectNewSyntax(item.Span);
                    break;
            }
        }

        private void CollectUse(SafeCoreUseSyntax use, ScopeBuilder scope)
        {
            if (!TryResolveVisibility(scope, use.Visibility, use.IsPublic, use.Span, out _)) return;
            if (use.Tree is null)
            {
                CollectImportLeaf(use.Path, use.Alias, use.Visibility, use.Span, scope);
                return;
            }
            CollectUseTree(use.Tree, string.Empty, use.Visibility, scope, use.Tree.Kind == SafeCoreUseTreeKind.Path ? use.Span : null, 0);
        }

        private void CollectUseTree(SafeCoreUseTreeSyntax tree, string prefix, SafeCoreVisibilitySyntax visibility,
            ScopeBuilder scope, TextSpan? simpleSpan, int depth)
        {
            if (!Enter(depth, tree.Span)) return;
            try
            {
                if (!Step(tree.Span)) return;
                if (tree.IsAbsolute)
                {
                    RejectNewSyntax(tree.Span);
                    return;
                }
                if (tree.Prefix.Count > _options.MaximumPathSegments)
                {
                    StopLimit(tree.Span);
                    return;
                }
                var path = new StringBuilder(prefix);
                if (path.Length > _options.MaximumPathLength)
                {
                    StopLimit(tree.Span);
                    return;
                }
                bool isSelfImport = false;
                for (var index = 0; index < tree.Prefix.Count; index++)
                {
                    if (!Step(tree.Span)) return;
                    string segment = tree.Prefix[index];
                    // In a group, `self` imports the accumulated prefix itself.
                    if (segment == "self" && index == tree.Prefix.Count - 1 && path.Length > 0 && tree.Kind == SafeCoreUseTreeKind.Path)
                    {
                        isSelfImport = true;
                        continue;
                    }
                    if (path.Length > 0) path.Append("::");
                    path.Append(segment);
                    if (path.Length > _options.MaximumPathLength) { StopLimit(tree.Span); return; }
                }
                string targetPath = path.ToString();
                if (tree.Kind == SafeCoreUseTreeKind.Glob)
                {
                    if (tree.Alias is not null || tree.Children.Count != 0 || targetPath.Length == 0)
                    {
                        RejectNewSyntax(tree.Span);
                        return;
                    }

                    if (_globImports.Count >= _options.MaximumSymbols)
                    {
                        StopLimit(tree.Span);
                        return;
                    }

                    _globImports.Add(new GlobImport(targetPath, scope, visibility, tree.Span));
                    return;
                }
                if (tree.Kind == SafeCoreUseTreeKind.Path)
                {
                    if (tree.Children.Count > 0) { RejectNewSyntax(tree.Span); return; }
                    CollectImportLeaf(targetPath, tree.Alias, visibility, simpleSpan ?? tree.Span, scope, isSelfImport);
                    return;
                }
                if (tree.Kind != SafeCoreUseTreeKind.Group || tree.Alias is not null)
                {
                    RejectNewSyntax(tree.Span);
                    return;
                }
                if (targetPath.Length > 0)
                {
                    if (_importGroupPrefixes.Count >= _options.MaximumSymbols) { StopLimit(tree.Span); return; }
                    _importGroupPrefixes.Add((targetPath, scope, tree.Span));
                }
                for (var index = 0; index < tree.Children.Count && !_truncated; index++)
                {
                    if (!Step(tree.Children[index].Span)) return;
                    CollectUseTree(tree.Children[index], targetPath, visibility, scope, null, depth + 1);
                }
            }
            finally { Exit(); }
        }

        private void CollectImportLeaf(string path, string? alias, SafeCoreVisibilitySyntax visibility, TextSpan span,
            ScopeBuilder scope, bool isSelfImport = false)
        {
            if (path.StartsWith("::", StringComparison.Ordinal)) { RejectNewSyntax(span); return; }
            if (!TrySplitPath(path, span, out IReadOnlyList<string>? segments)) return;
            // A crate-root alias requires a first-class crate symbol, outside this in-memory profile.
            if (segments.Count == 1 && segments[0] is "crate" or "self" or "super")
            {
                RejectNewSyntax(span);
                return;
            }
            string name = alias ?? segments[^1];
            SymbolBuilder? symbol = AddSymbol(
                scope,
                name,
                SafeCoreSymbolKind.Import,
                SafeCoreSymbolNamespace.Type,
                visibility.Kind == SafeCoreVisibilityKind.Public,
                span,
                isImport: true,
                targetPath: path,
                visibility: visibility,
                isAnonymousImport: name == "_");
            if (symbol is not null)
            {
                symbol.IsSelfImport = isSelfImport;
                _imports.Add(symbol);
                if (!isSelfImport)
                {
                    SymbolBuilder? valueBinding = AddSymbol(scope, name, SafeCoreSymbolKind.Import,
                        SafeCoreSymbolNamespace.Value, visibility.Kind == SafeCoreVisibilityKind.Public,
                        span, isImport: true, targetPath: path, visibility: visibility, isAnonymousImport: name == "_");
                    if (valueBinding is null) return;
                    symbol.ImportPeer = valueBinding;
                    valueBinding.ImportPeer = symbol;
                    valueBinding.IsSecondaryImportBinding = true;
                    _imports.Add(valueBinding);
                }
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
                targetPath: null,
                visibility: function.Visibility);
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

                if (!_options.EnableTypeSystemExtensions) CollectPatternBindings(
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
                _options.EnableTypeSystemExtensions && !structure.IsTupleStruct && !structure.IsUnitStruct
                    ? SafeCoreSymbolNamespace.Type : SafeCoreSymbolNamespace.Both,
                structure.IsPublic,
                structure.Span,
                isImport: false,
                targetPath: null,
                visibility: structure.Visibility);
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

                if (field.Name is not null || _options.EnableTypeSystemExtensions)
                {
                    _ = AddSymbol(
                        itemScope,
                        field.Name ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        SafeCoreSymbolKind.Field,
                        SafeCoreSymbolNamespace.Value,
                        field.IsPublic,
                        field.Span,
                        isImport: false,
                        targetPath: null,
                        visibility: field.Visibility);
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
                targetPath: null,
                visibility: enumeration.Visibility);
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
                variantSymbol.VisibilityScopePath = symbol.VisibilityScopePath;
                if (_options.EnableTypeSystemExtensions && variant.Kind != SafeCoreEnumVariantKind.Unit)
                {
                    ScopeBuilder? variantScope = CreateScope(itemScope, variantSymbol.QualifiedName, parent.ModulePath);
                    if (variantScope is null) return;
                    variantScope.Span = variant.Span;
                    for (var fieldIndex = 0; fieldIndex < variant.Fields.Count && !_truncated; fieldIndex++)
                    {
                        SafeCoreFieldSyntax field = variant.Fields[fieldIndex];
                        if (!Step(field.Span)) return;
                        string fieldName = field.Name ?? fieldIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        SymbolBuilder? fieldSymbol = AddSymbol(variantScope, fieldName, SafeCoreSymbolKind.Field,
                            SafeCoreSymbolNamespace.Value, isPublic: true, field.Span, isImport: false, targetPath: null);
                        if (fieldSymbol is not null) fieldSymbol.VisibilityScopePath = symbol.VisibilityScopePath;
                    }
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
                targetPath: null,
                visibility: alias.Visibility);
            if (symbol is null)
            {
                return;
            }
            if (_options.EnableTypeSystemExtensions) _aliasDefinitions[symbol] = alias;

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

                if (parameter.Attributes.Count > 0 || parameter.Kind != SafeCoreGenericParameterKind.Type || parameter.DefaultType is not null ||
                    parameter.DefaultValue is not null || parameter.ConstType is not null || parameter.Constraints.Any(static bound =>
                        bound is not SafeCoreTraitBoundSyntax { IsOptional: false, GenericParameters.Count: 0 }))
                {
                    RejectNewSyntax(parameter.Span);
                    continue;
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
            HashSet<string>? namesInPattern = null,
            Dictionary<string, SymbolBuilder>? sharedBindings = null,
            int depth = 0)
        {
            if (depth > _options.MaximumNestingDepth) { StopLimit(pattern.Span); return; }
            if (!Step(pattern.Span))
            {
                return;
            }

            namesInPattern ??= new HashSet<string>(StringComparer.Ordinal);
            switch (pattern)
            {
                case SafeCoreIdentifierPatternSyntax identifier:
                    if (_patternReferenceSpans.Contains(identifier.Span)) break;
                    string comparisonName = CanonicalizeIdentifier(identifier.Name);
                    if (!namesInPattern.Add(comparisonName))
                    {
                        AddDiagnostic(
                            SafeCoreNameResolutionDiagnosticCodes.DuplicateSymbol,
                            $"The name '{identifier.Name}' is bound more than once in the same pattern.",
                            identifier.Span);
                        break;
                    }

                    if (sharedBindings is not null && sharedBindings.TryGetValue(comparisonName, out SymbolBuilder? shared))
                    {
                        _patternBindings[identifier.Span] = shared;
                        break;
                    }
                    SymbolBuilder? declared = AddSymbol(
                        scope,
                        identifier.Name,
                        kind,
                        SafeCoreSymbolNamespace.Value,
                        isPublic: false,
                        identifier.Span,
                        isImport: false,
                        targetPath: null,
                        allowShadowing);
                    if (declared is not null && _options.EnableTypeSystemExtensions)
                    {
                        _patternBindings[identifier.Span] = declared;
                        if (sharedBindings is not null) sharedBindings[comparisonName] = declared;
                    }
                    break;
                case SafeCoreTuplePatternSyntax tuple:
                    for (var index = 0; index < tuple.Elements.Count; index++)
                    {
                        CollectPatternBindings(tuple.Elements[index], scope, kind, allowShadowing, namesInPattern, sharedBindings, depth + 1);
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
                            namesInPattern, sharedBindings, depth + 1);
                        if (_truncated)
                        {
                            return;
                        }
                    }

                    break;
                case SafeCoreReferencePatternSyntax reference when _options.EnableTypeSystemExtensions:
                    CollectPatternBindings(reference.Pattern, scope, kind, allowShadowing, namesInPattern, sharedBindings, depth + 1);
                    break;
                case SafeCoreSlicePatternSyntax slice when _options.EnableTypeSystemExtensions:
                    for (var index = 0; index < slice.Elements.Count && !_truncated; index++)
                        CollectPatternBindings(slice.Elements[index], scope, kind, allowShadowing, namesInPattern, sharedBindings, depth + 1);
                    break;
                case SafeCoreAtPatternSyntax at when _options.EnableTypeSystemExtensions:
                    CollectPatternBindings(at.Binding, scope, kind, allowShadowing, namesInPattern, sharedBindings, depth + 1);
                    CollectPatternBindings(at.Pattern, scope, kind, allowShadowing, namesInPattern, sharedBindings, depth + 1);
                    break;
                case SafeCoreStructPatternSyntax structure when _options.EnableTypeSystemExtensions:
                    for (var index = 0; index < structure.Fields.Count && !_truncated; index++)
                        CollectPatternBindings(structure.Fields[index].Pattern, scope, kind, allowShadowing, namesInPattern, sharedBindings, depth + 1);
                    break;
                case SafeCoreOrPatternSyntax alternatives when _options.EnableTypeSystemExtensions:
                    sharedBindings ??= new(StringComparer.Ordinal);
                    string[] inheritedNames = namesInPattern.ToArray();
                    for (var index = 0; index < alternatives.Alternatives.Count && !_truncated; index++)
                    {
                        var alternativeNames = new HashSet<string>(inheritedNames, StringComparer.Ordinal);
                        CollectPatternBindings(alternatives.Alternatives[index], scope, kind, allowShadowing,
                            alternativeNames, sharedBindings, depth + 1);
                        foreach (string name in alternativeNames)
                        {
                            if (!Step(pattern.Span)) return;
                            namesInPattern.Add(name);
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
                            if (_options.EnableTypeSystemExtensions && let.ElseBlock is not null)
                                CollectBlock(let.ElseBlock, scope, depth + 1, useExistingScope: false);

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
                    case SafeCorePrintExpressionSyntax print:
                        for (var index = 0; index < print.Arguments.Count && !_truncated; index++)
                        {
                            CollectNestedExpressionScopes(print.Arguments[index], parent, depth + 1);
                        }

                        break;
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
                    case SafeCoreConstBlockExpressionSyntax blockExpression when _options.EnableTypeSystemExtensions:
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
                    case SafeCoreStructExpressionSyntax structure when _options.EnableTypeSystemExtensions:
                        for (var index = 0; index < structure.Fields.Count && !_truncated; index++)
                            CollectNestedExpressionScopes(structure.Fields[index].Value, parent, depth + 1);
                        if (structure.Base is not null) CollectNestedExpressionScopes(structure.Base, parent, depth + 1);
                        break;
                    case SafeCoreMemberExpressionSyntax member when _options.EnableTypeSystemExtensions:
                        CollectNestedExpressionScopes(member.Target, parent, depth + 1);
                        break;
                    case SafeCoreCastExpressionSyntax cast when _options.EnableTypeSystemExtensions:
                        CollectNestedExpressionScopes(cast.Expression, parent, depth + 1);
                        break;
                    case SafeCoreLoopExpressionSyntax loop when _options.EnableTypeSystemExtensions:
                        CollectBlock(loop.Body, parent, depth + 1, useExistingScope: false);
                        break;
                    case SafeCoreWhileExpressionSyntax loop when _options.EnableTypeSystemExtensions:
                        CollectNestedExpressionScopes(loop.Condition, parent, depth + 1);
                        CollectBlock(loop.Body, parent, depth + 1, useExistingScope: false);
                        break;
                    case SafeCoreBreakExpressionSyntax { Value: not null } result when _options.EnableTypeSystemExtensions:
                        CollectNestedExpressionScopes(result.Value, parent, depth + 1);
                        break;
                    case SafeCoreReturnExpressionSyntax { Value: not null } result when _options.EnableTypeSystemExtensions:
                        CollectNestedExpressionScopes(result.Value, parent, depth + 1);
                        break;
                    case SafeCoreClosureExpressionSyntax closure when _options.EnableTypeSystemExtensions:
                        ScopeBuilder? closureScope = CreateScope(parent, parent.Path + "::<closure>", parent.ModulePath);
                        if (closureScope is null) return;
                        closureScope.Span = closure.Span;
                        _expressionScopes[closure] = closureScope;
                        CollectNestedExpressionScopes(closure.Body, closureScope, depth + 1);
                        break;
                    case SafeCoreMatchExpressionSyntax match when _options.EnableTypeSystemExtensions:
                        CollectNestedExpressionScopes(match.Scrutinee, parent, depth + 1);
                        for (var index = 0; index < match.Arms.Count && !_truncated; index++)
                        {
                            SafeCoreMatchArmSyntax arm = match.Arms[index];
                            if (!Step(arm.Span)) return;
                            ScopeBuilder? armScope = CreateScope(parent, parent.Path + "::<match-arm>", parent.ModulePath);
                            if (armScope is null) return;
                            armScope.Span = arm.Span;
                            _armScopes[arm] = armScope;
                            if (arm.Guard is not null) CollectNestedExpressionScopes(arm.Guard, armScope, depth + 1);
                            CollectNestedExpressionScopes(arm.Body, armScope, depth + 1);
                        }
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
            if (_imports.Count > 0 || _globImports.Count > 0)
            {
                ResolveImportFixedPoint();
                if (_truncated) return;
            }

            for (var index = 0; index < _importGroupPrefixes.Count; index++)
            {
                (string path, ScopeBuilder scope, TextSpan span) = _importGroupPrefixes[index];
                if (!Step(span)) return;
                PathResult result = ResolvePathInternal(path, scope, SafeCoreSymbolNamespace.Type, span, emitDiagnostic: true);
                _pathRecords.Add(new(path, scope.Path, result.Status, result.Symbol, result.Candidates, span));
                if (result.Status == SafeCoreNameResolutionStatus.Resolved && result.Symbol is not null &&
                    GetResolvedImportTarget(result.Symbol).Kind is not (SafeCoreSymbolKind.Module or SafeCoreSymbolKind.Enum))
                    AddDiagnostic(SafeCoreNameResolutionDiagnosticCodes.InvalidPath, "An import group prefix must name a module or enum.", span);
            }
            for (var index = 0; index < _globImports.Count; index++)
            {
                if (!Step(_globImports[index].Span)) return;
                _ = ResolveGlobScope(_globImports[index], emitDiagnostic: true);
                if (_truncated) return;
            }
            for (var index = 0; index < _imports.Count; index++)
            {
                if (!Step(_imports[index].Span))
                {
                    return;
                }

                _ = EnsureImportResolved(_imports[index]);
                if (!_imports[index].IsSecondaryImportBinding) ReportImportDiagnostics(_imports[index]);
            }

            CheckImportDuplicates();
        }

        private void ResolveImportFixedPoint()
        {
            _probingImports = true;
            try
            {
                // Recompute until imports and visible glob bindings stabilize.
                // Both the round bound and Step's deadline/cancellation budget
                // apply, including cycles whose candidates do not stabilize.
                for (var round = 0; round < _options.MaximumSymbols; round++)
                {
                    if (!Step(_root!.Span)) return;
                    var previous = new (ImportResolution Resolution, string? Visibility)[_imports.Count];
                    for (var index = 0; index < _imports.Count; index++)
                    {
                        if (!Step(_imports[index].Span)) return;
                        previous[index] = (_imports[index].ImportResolution, _imports[index].VisibilityScopePath);
                        ResetImport(_imports[index]);
                    }

                    for (var index = 0; index < _imports.Count; index++)
                    {
                        if (!Step(_imports[index].Span)) return;
                        _ = EnsureImportResolved(_imports[index]);
                    }

                    bool changed = false;
                    for (var index = 0; index < _globImports.Count; index++)
                    {
                        if (!Step(_globImports[index].Span)) return;
                        changed |= ExpandGlobImport(_globImports[index]);
                        if (_truncated) return;
                    }

                    for (var index = 0; index < _imports.Count; index++)
                    {
                        if (!Step(_imports[index].Span)) return;
                        changed |= previous[index] != (_imports[index].ImportResolution, _imports[index].VisibilityScopePath);
                    }

                    if (!changed) return;
                }

                StopLimit(_root!.Span);
            }
            finally
            {
                _probingImports = false;
            }
        }

        private static void ResetImport(SymbolBuilder import)
        {
            import.ImportResolutionAttempted = false;
            import.ResolvedImportTarget = null;
            import.MemberScope = null;
            import.ImportCycleReported = false;
            import.ImportVisibilityError = false;
            import.ImportResolution = new(SafeCoreNameResolutionStatus.Invalid, null);
            import.VisibilityScopePath = import.DeclaredImportVisibilityScopePath;
            import.IsPublic = import.VisibilityScopePath is null;
        }

        private ScopeBuilder? ResolveGlobScope(GlobImport glob, bool emitDiagnostic)
        {
            PathResult result = ResolvePathInternal(glob.TargetPath, glob.Scope,
                SafeCoreSymbolNamespace.Type, glob.Span, emitDiagnostic);
            if (emitDiagnostic)
                _pathRecords.Add(new(glob.TargetPath, glob.Scope.Path, result.Status, result.Symbol, result.Candidates, glob.Span));
            if (result.Status != SafeCoreNameResolutionStatus.Resolved) return null;

            ScopeBuilder? members;
            if (result.Symbol is not null)
            {
                members = GetMemberScope(GetResolvedImportTarget(result.Symbol));
            }
            else
            {
                // Root module paths have no first-class declaration symbol.
                members = null;
                if (TrySplitPath(glob.TargetPath, glob.Span, out IReadOnlyList<string> segments))
                {
                    members = segments[0] == "crate" ? _root : FindModuleScope(glob.Scope);
                    for (var index = 0; index < segments.Count; index++)
                    {
                        if (!Step(glob.Span)) return null;
                        if (segments[index] == "super") members = members?.Parent;
                        else if (index != 0 || segments[index] is not ("crate" or "self")) { members = null; break; }
                    }
                }
            }

            if (members is null || ReferenceEquals(members, glob.Scope))
            {
                if (emitDiagnostic)
                    AddDiagnostic(SafeCoreNameResolutionDiagnosticCodes.InvalidPath,
                        members is null ? "A glob import prefix must name a module or enum." : "A module cannot glob-import itself.", glob.Span);
                return null;
            }

            return members;
        }

        private bool ExpandGlobImport(GlobImport glob)
        {
            ScopeBuilder? members = ResolveGlobScope(glob, emitDiagnostic: false);
            if (!TryResolveVisibility(glob.Scope, glob.Visibility, false, glob.Span, out string? visibility)) return false;
            var desired = new Dictionary<(string Name, SymbolBuilder Target), (SafeCoreSymbolNamespace Namespace, string? Visibility)>();
            bool changed = false;
            int visited = 0;
            if (members is not null)
            foreach (string name in members.ByName.Keys)
            {
                if (++visited > _options.MaximumSymbols || !Step(glob.Span)) return changed;
                for (var namespaceIndex = 0; namespaceIndex < 2; namespaceIndex++)
                {
                    SafeCoreSymbolNamespace @namespace = namespaceIndex == 0 ? SafeCoreSymbolNamespace.Type : SafeCoreSymbolNamespace.Value;
                    List<SymbolBuilder> candidates = LookupAccessible(members, name, @namespace, glob.Scope, out _);
                    for (var index = 0; index < candidates.Count; index++)
                    {
                        if (!Step(glob.Span)) return changed;
                        SymbolBuilder member = candidates[index];
                        if (member.IsImport && member.ImportResolution.Status != SafeCoreNameResolutionStatus.Resolved) continue;
                        if (members.Path != members.ModulePath && member.Kind != SafeCoreSymbolKind.EnumVariant) continue;
                        SymbolBuilder target = GetResolvedImportTarget(member);
                        if (target.IsImport) continue;
                        string? allowed = VisibilityContains(visibility, member.VisibilityScopePath) ? member.VisibilityScopePath : visibility;
                        var key = (member.Name, target);
                        if (desired.TryGetValue(key, out var previous))
                        {
                            desired[key] = (previous.Namespace == @namespace ? @namespace : SafeCoreSymbolNamespace.Both,
                                VisibilityContains(previous.Visibility, allowed) ? previous.Visibility : allowed);
                        }
                        else desired.Add(key, (@namespace, allowed));
                    }
                }
            }

            visited = 0;
            foreach (var binding in glob.Bindings)
            {
                if (++visited > _options.MaximumSymbols || !Step(glob.Span)) return changed;
                bool active = desired.ContainsKey(binding.Key);
                changed |= binding.Value.IsActiveGlobImport != active;
                binding.Value.IsActiveGlobImport = active;
            }

            visited = 0;
            foreach (var binding in desired)
            {
                if (++visited > _options.MaximumSymbols || !Step(glob.Span)) return changed;
                (string name, SymbolBuilder target) = binding.Key;
                (SafeCoreSymbolNamespace @namespace, string? allowed) = binding.Value;
                if (glob.Bindings.TryGetValue(binding.Key, out SymbolBuilder? imported))
                {
                    changed |= imported.Namespace != @namespace || imported.VisibilityScopePath != allowed;
                    imported.Namespace = @namespace;
                    imported.VisibilityScopePath = allowed;
                    imported.IsPublic = allowed is null;
                    continue;
                }

                imported = AddSymbol(glob.Scope, name, SafeCoreSymbolKind.Import, @namespace,
                    allowed is null, glob.Span, isImport: true, targetPath: target.QualifiedName);
                if (imported is null) return changed;
                imported.IsGlobImport = true;
                imported.IsActiveGlobImport = true;
                imported.VisibilityScopePath = allowed;
                imported.ResolvedImportTarget = target;
                imported.MemberScope = GetMemberScope(target);
                imported.ImportResolutionAttempted = true;
                imported.ImportResolution = new(SafeCoreNameResolutionStatus.Resolved, target);
                glob.Bindings.Add(binding.Key, imported);
                changed = true;
            }

            return changed;
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
                            if (left.IsImport && left.ImportResolution.Status != SafeCoreNameResolutionStatus.Resolved ||
                                right.IsImport && right.ImportResolution.Status != SafeCoreNameResolutionStatus.Resolved) continue;
                            if (left.IsGlobImport || right.IsGlobImport || (!left.IsImport && !right.IsImport) ||
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
                import.ImportCycleReported = true;
                // This is a transient result while the same binding is being
                // probed as a path prefix. Do not cache it: a concrete module
                // or value candidate may resolve the outer path immediately
                // after this placeholder is skipped by LookupAccessible.
                return new(SafeCoreNameResolutionStatus.Ambiguous, null);
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
                    expectedNamespace: import.Namespace,
                    import.Span,
                    emitDiagnostic: false);
                if (result.Status == SafeCoreNameResolutionStatus.Resolved && result.Symbol is not null)
                {
                    SymbolBuilder resolvedTarget = GetResolvedImportTarget(result.Symbol);
                    if (import.IsSelfImport && !MatchesNamespace(result.Symbol.Namespace, SafeCoreSymbolNamespace.Type))
                    {
                        AddDiagnostic(SafeCoreNameResolutionDiagnosticCodes.UnresolvedName,
                            "A self import requires a declaration in the type namespace.", import.Span);
                        import.ImportResolution = new(SafeCoreNameResolutionStatus.Unresolved, null);
                        return import.ImportResolution;
                    }
                    import.ResolvedImportTarget = resolvedTarget;
                    import.MemberScope = GetMemberScope(resolvedTarget);
                    import.ImportResolution = new(result.Status, resolvedTarget);
                    if (!VisibilityContains(result.Symbol.VisibilityScopePath, import.VisibilityScopePath) ||
                        !VisibilityContains(resolvedTarget.VisibilityScopePath, import.VisibilityScopePath))
                    {
                        import.ImportVisibilityError = true;
                        if (VisibilityContains(import.VisibilityScopePath, result.Symbol.VisibilityScopePath))
                            import.VisibilityScopePath = result.Symbol.VisibilityScopePath;
                        if (VisibilityContains(import.VisibilityScopePath, resolvedTarget.VisibilityScopePath))
                            import.VisibilityScopePath = resolvedTarget.VisibilityScopePath;
                        import.IsPublic = import.VisibilityScopePath is null;
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

        private void ReportImportDiagnostics(SymbolBuilder import)
        {
            SymbolBuilder? peer = import.ImportPeer;
            bool hasTarget = import.ImportResolution.Status == SafeCoreNameResolutionStatus.Resolved ||
                peer?.ImportResolution.Status == SafeCoreNameResolutionStatus.Resolved;
            bool hasExportableTarget = import.ImportResolution.Status == SafeCoreNameResolutionStatus.Resolved && !import.ImportVisibilityError ||
                peer?.ImportResolution.Status == SafeCoreNameResolutionStatus.Resolved && !peer.ImportVisibilityError;
            if (!hasExportableTarget && (import.ImportVisibilityError || peer?.ImportVisibilityError == true))
            {
                AddDiagnostic(SafeCoreNameResolutionDiagnosticCodes.PrivateName,
                    $"Import '{import.Name}' cannot re-export its target with broader visibility.", import.Span);
                return;
            }

            if (!hasTarget && (import.ImportCycleReported || peer?.ImportCycleReported == true))
                AddDiagnostic(SafeCoreNameResolutionDiagnosticCodes.ImportCycle,
                    $"Import cycle encountered while resolving '{import.TargetPath}'.", import.Span);

            // A use binds every available namespace. An absent or inaccessible
            // counterpart is legal, but ambiguity in either namespace is not.
            SafeCoreNameResolutionStatus status = import.ImportResolution.Status;
            SafeCoreNameResolutionStatus other = peer?.ImportResolution.Status ?? status;
            if (status == SafeCoreNameResolutionStatus.Ambiguous || other == SafeCoreNameResolutionStatus.Ambiguous)
                status = SafeCoreNameResolutionStatus.Ambiguous;
            else if (hasTarget) return;
            else if (status == SafeCoreNameResolutionStatus.Private || other == SafeCoreNameResolutionStatus.Private)
                status = SafeCoreNameResolutionStatus.Private;
            ReportPathResult(new(status, null, []), import.TargetPath!, import.Span, emitDiagnostic: true);
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
                if (parameter.Attributes.Count > 0 || parameter.Receiver is not null) RejectNewSyntax(parameter.Span);
                ResolvePattern(parameter.Pattern, scope, depth + 1);
                if (_options.EnableTypeSystemExtensions) CollectPatternBindings(parameter.Pattern, scope, SafeCoreSymbolKind.Parameter);
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
                if (HasUnsupportedAttributes(structure.Fields[index].Attributes))
                {
                    RejectNewSyntax(structure.Fields[index].Span);
                }
                if (structure.Fields[index].Name is null)
                    _ = TryResolveVisibility(scope, structure.Fields[index].Visibility, structure.Fields[index].IsPublic,
                        structure.Fields[index].Span, out _);
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
                if (HasUnsupportedAttributes(variant.Attributes) ||
                    variant.Kind == SafeCoreEnumVariantKind.Struct && !_options.EnableTypeSystemExtensions ||
                    variant.Discriminant is not null)
                {
                    RejectNewSyntax(variant.Span);
                }
                for (var fieldIndex = 0; fieldIndex < variant.Fields.Count; fieldIndex++)
                {
                    if (HasUnsupportedAttributes(variant.Fields[fieldIndex].Attributes)) RejectNewSyntax(variant.Fields[fieldIndex].Span);
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
            // Trait resolution is outside the current semantic profile. Keep
            // legacy path bounds in the syntax model and defer binding until that
            // namespace is introduced, avoiding false unresolved diagnostics.
            if (parameters.Count > _options.MaximumPathSegments)
            {
                StopLimit(scope.Span);
                return;
            }

            for (var index = 0; index < parameters.Count && !_truncated; index++)
            {
                SafeCoreGenericParameterSyntax parameter = parameters[index];
                if (!Step(parameter.Span)) return;
                for (var boundIndex = 0; boundIndex < parameter.Bounds.Count && !_truncated; boundIndex++)
                {
                    ValidateDeferredBoundSyntax(parameter.Bounds[boundIndex]);
                }
                // Also inspect the structured view so an externally supplied AST cannot hide a
                // new type by omitting it from the backwards-compatible Bounds collection.
                for (var constraintIndex = 0; constraintIndex < parameter.Constraints.Count && !_truncated; constraintIndex++)
                {
                    SafeCoreTypeBoundSyntax constraint = parameter.Constraints[constraintIndex];
                    if (!Step(constraint.Span)) return;
                    if (constraint is SafeCoreTraitBoundSyntax { IsOptional: false, GenericParameters.Count: 0 } trait)
                        ValidateDeferredBoundSyntax(trait.Type);
                    else RejectNewSyntax(constraint.Span);
                }
            }
        }

        // Bounds deliberately defer name binding, but must still retain only syntax that HIR
        // understands. The explicit work stack observes the same depth and operation limits as
        // resolution, including types nested inside generic arguments and array-length expressions.
        private void ValidateDeferredBoundSyntax(SafeCoreTypeSyntax root)
        {
            var pending = new Stack<(object Syntax, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count > 0 && !_truncated)
            {
                (object syntax, int depth) = pending.Pop();
                TextSpan span = syntax switch
                {
                    SafeCoreTypeSyntax type => type.Span,
                    SafeCorePathSegmentSyntax segment => segment.Span,
                    SafeCoreExpressionSyntax expression => expression.Span,
                    SafeCorePatternSyntax pattern => pattern.Span,
                    SafeCoreStatementSyntax statement => statement.Span,
                    SafeCoreBlockSyntax block => block.Span,
                    _ => root.Span,
                };
                if (!Enter(depth, span)) return;
                try
                {
                    if (!Step(span)) return;
                    switch (syntax)
                    {
                        case SafeCorePathTypeSyntax path:
                            if (path.IsAbsolute) RejectNewSyntax(path.Span);
                            if (path.Segments.Count > _options.MaximumPathSegments) { StopLimit(path.Span); return; }
                            for (var index = 0; index < path.Segments.Count && Step(span); index++)
                                pending.Push((path.Segments[index], depth + 1));
                            break;
                        case SafeCorePathSegmentSyntax segment:
                            if (segment.HasFunctionArguments || segment.FunctionParameters.Count > 0 || segment.FunctionReturnType is not null)
                                RejectNewSyntax(segment.Span);
                            for (var index = 0; index < segment.GenericArguments.Count && Step(span); index++)
                                pending.Push((segment.GenericArguments[index], depth + 1));
                            for (var index = 0; index < segment.Arguments.Count && Step(span); index++)
                            {
                                if (segment.Arguments[index] is not SafeCoreTypeArgumentSyntax argument)
                                    RejectNewSyntax(segment.Arguments[index].Span);
                                else if (index >= segment.GenericArguments.Count || !ReferenceEquals(argument.Type, segment.GenericArguments[index]))
                                    pending.Push((argument.Type, depth + 1));
                            }
                            break;
                        case SafeCoreReferenceTypeSyntax reference:
                            pending.Push((reference.Inner, depth + 1));
                            break;
                        case SafeCoreTupleTypeSyntax tuple:
                            for (var index = 0; index < tuple.Elements.Count && Step(span); index++) pending.Push((tuple.Elements[index], depth + 1));
                            break;
                        case SafeCoreArrayTypeSyntax array:
                            pending.Push((array.Element, depth + 1)); pending.Push((array.Length, depth + 1));
                            break;
                        case SafeCoreSliceTypeSyntax slice:
                            pending.Push((slice.Element, depth + 1));
                            break;
                        case SafeCoreUnitTypeSyntax:
                        case SafeCoreNeverTypeSyntax:
                            break;
                        case SafeCoreExpressionSyntax expression when expression.Attributes.Count > 0:
                            RejectNewSyntax(span);
                            break;
                        case SafeCoreNameExpressionSyntax name:
                            if (name.Path.StartsWith("::", StringComparison.Ordinal) ||
                                name.Segments.Any(static segment => segment.HasGenericArguments || segment.GenericArguments.Count > 0)) RejectNewSyntax(span);
                            break;
                        case SafeCoreLiteralExpressionSyntax:
                            break;
                        case SafeCoreUnaryExpressionSyntax unary:
                            pending.Push((unary.Operand, depth + 1));
                            break;
                        case SafeCoreBinaryExpressionSyntax binary:
                            pending.Push((binary.Left, depth + 1)); pending.Push((binary.Right, depth + 1));
                            break;
                        case SafeCoreCallExpressionSyntax call:
                            pending.Push((call.Callee, depth + 1));
                            for (var index = 0; index < call.Arguments.Count && Step(span); index++) pending.Push((call.Arguments[index], depth + 1));
                            break;
                        case SafeCorePrintExpressionSyntax print:
                            for (var index = 0; index < print.Arguments.Count && Step(span); index++) pending.Push((print.Arguments[index], depth + 1));
                            break;
                        case SafeCoreTupleExpressionSyntax tuple:
                            for (var index = 0; index < tuple.Elements.Count && Step(span); index++) pending.Push((tuple.Elements[index], depth + 1));
                            break;
                        case SafeCoreArrayExpressionSyntax array:
                            for (var index = 0; index < array.Elements.Count && Step(span); index++) pending.Push((array.Elements[index], depth + 1));
                            if (array.RepeatCount is not null) pending.Push((array.RepeatCount, depth + 1));
                            break;
                        case SafeCoreIndexExpressionSyntax indexExpression:
                            pending.Push((indexExpression.Target, depth + 1)); pending.Push((indexExpression.Index, depth + 1));
                            break;
                        case SafeCoreIfExpressionSyntax conditional:
                            pending.Push((conditional.Condition, depth + 1)); pending.Push((conditional.Then, depth + 1));
                            if (conditional.Else is not null) pending.Push((conditional.Else, depth + 1));
                            break;
                        case SafeCoreBlockExpressionSyntax block:
                            pending.Push((block.Block, depth + 1));
                            break;
                        case SafeCoreBlockSyntax block:
                            RejectUnsupportedAttributes(block.Attributes, span);
                            for (var index = 0; index < block.Statements.Count && Step(span); index++) pending.Push((block.Statements[index], depth + 1));
                            if (block.TailExpression is not null) pending.Push((block.TailExpression, depth + 1));
                            break;
                        case SafeCoreStatementSyntax statement when statement.Attributes.Count > 0:
                            RejectNewSyntax(span);
                            break;
                        case SafeCoreLetStatementSyntax let:
                            if (let.ElseBlock is not null) RejectNewSyntax(span);
                            pending.Push((let.Pattern, depth + 1));
                            if (let.Type is not null) pending.Push((let.Type, depth + 1));
                            if (let.Initializer is not null) pending.Push((let.Initializer, depth + 1));
                            break;
                        case SafeCoreReturnStatementSyntax result:
                            if (result.Value is not null) pending.Push((result.Value, depth + 1));
                            break;
                        case SafeCoreExpressionStatementSyntax statement:
                            pending.Push((statement.Expression, depth + 1));
                            break;
                        case SafeCoreIdentifierPatternSyntax { IsByReference: false }:
                        case SafeCoreWildcardPatternSyntax:
                        case SafeCoreLiteralPatternSyntax:
                            break;
                        case SafeCoreTuplePatternSyntax tuple:
                            for (var index = 0; index < tuple.Elements.Count && Step(span); index++) pending.Push((tuple.Elements[index], depth + 1));
                            break;
                        case SafeCorePathPatternSyntax path:
                            if (path.Qualifier is not null || path.Path.StartsWith("::", StringComparison.Ordinal) ||
                                path.Segments.Any(static segment => segment.HasGenericArguments || segment.GenericArguments.Count > 0)) RejectNewSyntax(span);
                            for (var index = 0; index < path.Arguments.Count && Step(span); index++) pending.Push((path.Arguments[index], depth + 1));
                            break;
                        default:
                            RejectNewSyntax(span);
                            break;
                    }
                }
                finally { Exit(); }
            }
        }

        private void ResolveBlock(SafeCoreBlockSyntax block, ScopeBuilder fallbackScope, int depth)
        {
            // Blocks in type-level constant expressions are discovered during type
            // resolution, after the ordinary function-body scope collection pass.
            if (_options.EnableTypeSystemExtensions && !_blockScopes.ContainsKey(block))
                CollectBlock(block, fallbackScope, depth, useExistingScope: false);
            RejectUnsupportedAttributes(block.Attributes, block.Span);
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
                    if (statement.Attributes.Count > 0) RejectNewSyntax(statement.Span);
                    if (!Step(statement.Span))
                    {
                        return;
                    }

                    switch (statement)
                    {
                        case SafeCoreLetStatementSyntax let:
                            if (let.ElseBlock is not null && !_options.EnableTypeSystemExtensions) RejectNewSyntax(let.ElseBlock.Span);
                            ResolvePattern(let.Pattern, scope, depth + 1);
                            if (let.Type is not null)
                            {
                                ResolveType(let.Type, scope, depth + 1);
                            }

                            if (let.Initializer is not null)
                            {
                                ResolveExpression(let.Initializer, scope, depth + 1);
                            }
                            if (_options.EnableTypeSystemExtensions && let.ElseBlock is not null)
                                ResolveBlock(let.ElseBlock, scope, depth + 1);

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
                        case SafeCoreReturnStatementSyntax:
                            break;
                        default:
                            RejectNewSyntax(statement.Span);
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
                        if (path.Qualifier is not null || path.Path.StartsWith("::", StringComparison.Ordinal) ||
                            path.Segments.Any(static segment => segment.HasGenericArguments || segment.GenericArguments.Count > 0))
                        {
                            RejectNewSyntax(path.Span);
                        }
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
                    case SafeCoreIdentifierPatternSyntax identifier when !identifier.IsByReference || _options.EnableTypeSystemExtensions:
                        if (_options.EnableTypeSystemExtensions)
                        {
                            PathResult candidate = ResolvePathInternal(identifier.Name, scope, SafeCoreSymbolNamespace.Value, identifier.Span, emitDiagnostic: false);
                            if (candidate.Status == SafeCoreNameResolutionStatus.Resolved && candidate.Symbol is not null &&
                                GetResolvedImportTarget(candidate.Symbol).Kind is SafeCoreSymbolKind.Const or SafeCoreSymbolKind.Struct or SafeCoreSymbolKind.EnumVariant)
                            {
                                if (identifier.IsMutable || identifier.IsByReference)
                                    AddDiagnostic(SafeCoreNameResolutionDiagnosticCodes.DuplicateSymbol,
                                        $"Binding pattern '{identifier.Name}' cannot shadow a constant or constructor.", identifier.Span);
                                else
                                {
                                    _patternReferenceSpans.Add(identifier.Span);
                                    _pathRecords.Add(new(identifier.Name, scope.Path, candidate.Status, candidate.Symbol, candidate.Candidates, identifier.Span));
                                }
                            }
                        }
                        break;
                    case SafeCoreLiteralPatternSyntax:
                    case SafeCoreWildcardPatternSyntax:
                        break;
                    case SafeCoreReferencePatternSyntax reference when _options.EnableTypeSystemExtensions:
                        ResolvePattern(reference.Pattern, scope, depth + 1);
                        break;
                    case SafeCoreSlicePatternSyntax slice when _options.EnableTypeSystemExtensions:
                        for (var index = 0; index < slice.Elements.Count && !_truncated; index++) ResolvePattern(slice.Elements[index], scope, depth + 1);
                        break;
                    case SafeCoreRestPatternSyntax when _options.EnableTypeSystemExtensions:
                        break;
                    case SafeCoreAtPatternSyntax at when _options.EnableTypeSystemExtensions:
                        ResolvePattern(at.Binding, scope, depth + 1);
                        ResolvePattern(at.Pattern, scope, depth + 1);
                        break;
                    case SafeCoreOrPatternSyntax alternatives when _options.EnableTypeSystemExtensions:
                        for (var index = 0; index < alternatives.Alternatives.Count && !_truncated; index++) ResolvePattern(alternatives.Alternatives[index], scope, depth + 1);
                        break;
                    case SafeCoreRangePatternSyntax range when _options.EnableTypeSystemExtensions:
                        if (range.Start is not null) ResolveRangeBound(range.Start, scope, depth + 1);
                        if (range.End is not null) ResolveRangeBound(range.End, scope, depth + 1);
                        break;
                    case SafeCoreStructPatternSyntax structure when _options.EnableTypeSystemExtensions:
                        if (structure.Qualifier is not null || structure.Segments.Any(static segment => segment.HasGenericArguments || segment.GenericArguments.Count > 0))
                            RejectNewSyntax(structure.Span);
                        ResolveConstructorAndRecord(structure.Path, scope, structure.Span);
                        for (var index = 0; index < structure.Fields.Count && !_truncated; index++)
                        {
                            if (structure.Fields[index].Attributes.Count > 0) RejectNewSyntax(structure.Fields[index].Span);
                            ResolvePattern(structure.Fields[index].Pattern, scope, depth + 1);
                        }
                        break;
                    default:
                        RejectNewSyntax(pattern.Span);
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
                        if (path.IsAbsolute) RejectNewSyntax(path.Span);
                        string pathText = string.Join("::", path.Segments.Select(segment => segment.Name));
                        if (!(path.Segments.Count == 1 && PrimitiveTypeNames.Contains(pathText)))
                        {
                            ResolveAndRecord(pathText, scope, SafeCoreSymbolNamespace.Type, path.Span);
                        }

                        for (var segmentIndex = 0; segmentIndex < path.Segments.Count; segmentIndex++)
                        {
                            SafeCorePathSegmentSyntax segment = path.Segments[segmentIndex];
                            if (segment.HasFunctionArguments || segment.FunctionParameters.Count > 0 || segment.FunctionReturnType is not null)
                            {
                                RejectNewSyntax(segment.Span);
                            }
                            for (var argumentIndex = 0; argumentIndex < segment.GenericArguments.Count; argumentIndex++)
                            {
                                ResolveType(segment.GenericArguments[argumentIndex], scope, depth + 1);
                            }
                            for (var argumentIndex = 0; argumentIndex < segment.Arguments.Count && !_truncated; argumentIndex++)
                            {
                                SafeCoreGenericArgumentSyntax argument = segment.Arguments[argumentIndex];
                                if (!Step(argument.Span)) return;
                                if (argument is not SafeCoreTypeArgumentSyntax typeArgument)
                                    RejectNewSyntax(argument.Span);
                                else if (argumentIndex >= segment.GenericArguments.Count ||
                                    !ReferenceEquals(typeArgument.Type, segment.GenericArguments[argumentIndex]))
                                    ResolveType(typeArgument.Type, scope, depth + 1);
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
                    case SafeCoreUnitTypeSyntax:
                    case SafeCoreNeverTypeSyntax:
                        break;
                    case SafeCoreFunctionTypeSyntax function when _options.EnableTypeSystemExtensions:
                        if (function.GenericParameters.Count > 0) RejectNewSyntax(function.Span);
                        for (var index = 0; index < function.Parameters.Count && !_truncated; index++)
                        {
                            SafeCoreFunctionTypeParameterSyntax parameter = function.Parameters[index];
                            if (parameter.Attributes.Count > 0) RejectNewSyntax(parameter.Span);
                            ResolveType(parameter.Type, scope, depth + 1);
                        }
                        if (function.ReturnType is not null) ResolveType(function.ReturnType, scope, depth + 1);
                        break;
                    case SafeCoreInferredTypeSyntax when _options.EnableTypeSystemExtensions:
                        break;
                    default:
                        RejectNewSyntax(type.Span);
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
            if (_options.EnableTypeSystemExtensions &&
                (expression is SafeCoreClosureExpressionSyntax && !_expressionScopes.ContainsKey(expression) ||
                 expression is SafeCoreMatchExpressionSyntax matchScope && matchScope.Arms.Count > 0 && !_armScopes.ContainsKey(matchScope.Arms[0])))
                CollectNestedExpressionScopes(expression, scope, depth);
            if (expression.Attributes.Count > 0) RejectNewSyntax(expression.Span);
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
                        if (name.Path.StartsWith("::", StringComparison.Ordinal) ||
                            name.Segments.Any(static segment => segment.HasGenericArguments || segment.GenericArguments.Count != 0)) RejectNewSyntax(name.Span);
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
                    case SafeCorePrintExpressionSyntax print:
                        for (var index = 0; index < print.Arguments.Count && !_truncated; index++)
                        {
                            ResolveExpression(print.Arguments[index], scope, depth + 1);
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
                    case SafeCoreConstBlockExpressionSyntax block when _options.EnableTypeSystemExtensions:
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
                    case SafeCoreLiteralExpressionSyntax:
                        break;
                    case SafeCoreStructExpressionSyntax structure when _options.EnableTypeSystemExtensions:
                        if (structure.Qualifier is not null) RejectNewSyntax(structure.Span);
                        if (structure.Path.Path.StartsWith("::", StringComparison.Ordinal) ||
                            structure.Path.Segments.Any(static segment => segment.HasGenericArguments || segment.GenericArguments.Count != 0)) RejectNewSyntax(structure.Path.Span);
                        ResolveConstructorAndRecord(structure.Path.Path, scope, structure.Path.Span);
                        for (var index = 0; index < structure.Fields.Count && !_truncated; index++)
                        {
                            SafeCoreStructExpressionFieldSyntax field = structure.Fields[index];
                            if (field.Attributes.Count > 0) RejectNewSyntax(field.Span);
                            ResolveExpression(field.Value, scope, depth + 1);
                        }
                        if (structure.Base is not null) ResolveExpression(structure.Base, scope, depth + 1);
                        break;
                    case SafeCoreMemberExpressionSyntax member when _options.EnableTypeSystemExtensions:
                        if (member.HasGenericArguments || member.GenericArguments.Count > 0) RejectNewSyntax(member.Span);
                        ResolveExpression(member.Target, scope, depth + 1);
                        break;
                    case SafeCoreCastExpressionSyntax cast when _options.EnableTypeSystemExtensions:
                        ResolveExpression(cast.Expression, scope, depth + 1);
                        ResolveType(cast.Type, scope, depth + 1);
                        break;
                    case SafeCoreLoopExpressionSyntax loop when _options.EnableTypeSystemExtensions:
                        if (loop.Label is not null) RejectNewSyntax(loop.Span);
                        ResolveBlock(loop.Body, scope, depth + 1);
                        break;
                    case SafeCoreWhileExpressionSyntax loop when _options.EnableTypeSystemExtensions:
                        if (loop.Label is not null) RejectNewSyntax(loop.Span);
                        ResolveExpression(loop.Condition, scope, depth + 1);
                        ResolveBlock(loop.Body, scope, depth + 1);
                        break;
                    case SafeCoreBreakExpressionSyntax result when _options.EnableTypeSystemExtensions:
                        if (result.Label is not null) RejectNewSyntax(result.Span);
                        if (result.Value is not null) ResolveExpression(result.Value, scope, depth + 1);
                        break;
                    case SafeCoreContinueExpressionSyntax result when _options.EnableTypeSystemExtensions:
                        if (result.Label is not null) RejectNewSyntax(result.Span);
                        break;
                    case SafeCoreReturnExpressionSyntax result when _options.EnableTypeSystemExtensions:
                        if (result.Value is not null) ResolveExpression(result.Value, scope, depth + 1);
                        break;
                    case SafeCoreClosureExpressionSyntax closure when _options.EnableTypeSystemExtensions:
                        ScopeBuilder closureScope = _expressionScopes.TryGetValue(closure, out ScopeBuilder? collectedClosure) ? collectedClosure : scope;
                        for (var index = 0; index < closure.Parameters.Count && !_truncated; index++)
                        {
                            SafeCoreClosureParameterSyntax parameter = closure.Parameters[index];
                            if (parameter.Attributes.Count > 0) RejectNewSyntax(parameter.Span);
                            ResolvePattern(parameter.Pattern, closureScope, depth + 1);
                            CollectPatternBindings(parameter.Pattern, closureScope, SafeCoreSymbolKind.Parameter);
                            if (parameter.Type is not null) ResolveType(parameter.Type, closureScope, depth + 1);
                        }
                        if (closure.ReturnType is not null) ResolveType(closure.ReturnType, closureScope, depth + 1);
                        ResolveExpression(closure.Body, closureScope, depth + 1);
                        break;
                    case SafeCoreMatchExpressionSyntax match when _options.EnableTypeSystemExtensions:
                        ResolveExpression(match.Scrutinee, scope, depth + 1);
                        for (var index = 0; index < match.Arms.Count && !_truncated; index++)
                        {
                            SafeCoreMatchArmSyntax arm = match.Arms[index];
                            if (arm.Attributes.Count > 0) RejectNewSyntax(arm.Span);
                            ScopeBuilder armScope = _armScopes.TryGetValue(arm, out ScopeBuilder? collectedArm) ? collectedArm : scope;
                            ResolvePattern(arm.Pattern, armScope, depth + 1);
                            CollectPatternBindings(arm.Pattern, armScope, SafeCoreSymbolKind.Local);
                            if (arm.Guard is not null) ResolveExpression(arm.Guard, armScope, depth + 1);
                            ResolveExpression(arm.Body, armScope, depth + 1);
                        }
                        break;
                    default:
                        RejectNewSyntax(expression.Span);
                        break;
                }
            }
            finally
            {
                Exit();
            }
        }

        private void ResolveRangeBound(SafeCorePatternSyntax pattern, ScopeBuilder scope, int depth)
        {
            if (pattern is SafeCoreIdentifierPatternSyntax identifier)
            {
                _patternReferenceSpans.Add(identifier.Span);
                ResolveAndRecord(identifier.Name, scope, SafeCoreSymbolNamespace.Value, identifier.Span);
            }
            else ResolvePattern(pattern, scope, depth);
        }

        private void ResolveConstructorAndRecord(string path, ScopeBuilder scope, TextSpan span)
        {
            PathResult result = ResolvePathInternal(path, scope, SafeCoreSymbolNamespace.Type, span, emitDiagnostic: false);
            if (result.Status != SafeCoreNameResolutionStatus.Resolved || result.Symbol is null ||
                GetResolvedImportTarget(result.Symbol).Kind is not (SafeCoreSymbolKind.Struct or SafeCoreSymbolKind.TypeAlias))
                result = ResolvePathInternal(path, scope, SafeCoreSymbolNamespace.Value, span, emitDiagnostic: true);
            if (!Step(span)) return;
            _pathRecords.Add(new(path, scope.Path, result.Status, result.Symbol, result.Candidates, span));
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
                    segments.Count == 1 ? expectedNamespace : SafeCoreSymbolNamespace.Type,
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
                position + 1 == segments.Count ? expectedNamespace : SafeCoreSymbolNamespace.Type,
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
                    expectedNamespace: position + 1 == segments.Count ? expectedNamespace : SafeCoreSymbolNamespace.Type,
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
            bool hasExplicitType = false;
            bool hasExplicitValue = false;
            bool hasResolvedType = false;
            bool hasResolvedValue = false;
            for (var index = 0; index < declared.Count; index++)
            {
                SymbolBuilder symbol = declared[index];
                if (!Step(symbol.Span))
                {
                    return accessible;
                }

                if (symbol.IsGlobImport && !symbol.IsActiveGlobImport) continue;

                // Resolve only the requested namespace, so a type dependency
                // does not create a spurious cycle in the value dependency.
                if (expectedNamespace is not null && !MatchesNamespace(symbol.Namespace, expectedNamespace.Value)) continue;

                if (symbol.IsImport && !symbol.IsGlobImport)
                {
                    // Keep failed explicit bindings as candidates so the path
                    // retains its actual failure (including import cycles).
                    // They also reserve their namespaces against globs.
                    _ = EnsureImportResolved(symbol);
                    if (symbol.ImportResolution.Status is SafeCoreNameResolutionStatus.Unresolved or SafeCoreNameResolutionStatus.Private)
                        continue;
                }

                if (!symbol.IsGlobImport && (!symbol.IsImport || symbol.ImportResolution.Status == SafeCoreNameResolutionStatus.Resolved))
                {
                    hasExplicitType |= MatchesNamespace(symbol.Namespace, SafeCoreSymbolNamespace.Type);
                    hasExplicitValue |= MatchesNamespace(symbol.Namespace, SafeCoreSymbolNamespace.Value);
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
                if (!symbol.IsImport || symbol.ImportResolution.Status == SafeCoreNameResolutionStatus.Resolved)
                {
                    hasResolvedType |= MatchesNamespace(symbol.Namespace, SafeCoreSymbolNamespace.Type);
                    hasResolvedValue |= MatchesNamespace(symbol.Namespace, SafeCoreSymbolNamespace.Value);
                }
                if (symbol.Kind == SafeCoreSymbolKind.Local)
                {
                    latestLocal = symbol;
                }
            }

            if (latestLocal is not null) return [latestLocal];
            var selected = new List<SymbolBuilder>(accessible.Count);
            for (var index = 0; index < accessible.Count; index++)
            {
                SymbolBuilder candidate = accessible[index];
                if (!Step(candidate.Span)) return selected;
                if (candidate.IsImport && candidate.ImportResolution.Status != SafeCoreNameResolutionStatus.Resolved &&
                    (candidate.Namespace == SafeCoreSymbolNamespace.Type && hasResolvedType ||
                     candidate.Namespace == SafeCoreSymbolNamespace.Value && hasResolvedValue ||
                     candidate.Namespace == SafeCoreSymbolNamespace.Both && hasResolvedType && hasResolvedValue)) continue;
                if (candidate.IsGlobImport &&
                    ((expectedNamespace is SafeCoreSymbolNamespace.Type && hasExplicitType) ||
                     (expectedNamespace is SafeCoreSymbolNamespace.Value && hasExplicitValue) ||
                     (expectedNamespace is null &&
                      (candidate.Namespace == SafeCoreSymbolNamespace.Type && hasExplicitType ||
                       candidate.Namespace == SafeCoreSymbolNamespace.Value && hasExplicitValue ||
                       candidate.Namespace == SafeCoreSymbolNamespace.Both && hasExplicitType && hasExplicitValue)))) continue;

                bool duplicate = false;
                for (var selectedIndex = 0; selectedIndex < selected.Count; selectedIndex++)
                {
                    if (!Step(candidate.Span)) return selected;
                    SymbolBuilder existing = selected[selectedIndex];
                    if (candidate.IsGlobImport && existing.IsGlobImport &&
                        ReferenceEquals(GetResolvedImportTarget(existing), GetResolvedImportTarget(candidate)))
                    {
                        // Duplicate routes to one declaration retain the
                        // broadest visibility available to this requester.
                        if (VisibilityContains(candidate.VisibilityScopePath, existing.VisibilityScopePath))
                            selected[selectedIndex] = candidate;
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate) selected.Add(candidate);
            }

            return selected;
        }

        private static bool MatchesNamespace(
            SafeCoreSymbolNamespace declared,
            SafeCoreSymbolNamespace requested) =>
            declared == SafeCoreSymbolNamespace.Both || declared == requested;

        private static bool IsAccessible(SymbolBuilder symbol, ScopeBuilder requester)
        {
            return VisibilityContains(symbol.VisibilityScopePath, requester.ModulePath);
        }

        private static bool VisibilityContains(string? outer, string? inner) =>
            outer is null || inner is not null && (string.Equals(outer, inner, StringComparison.Ordinal) ||
                inner.StartsWith(outer + "::", StringComparison.Ordinal));

        private bool TryResolveVisibility(ScopeBuilder scope, SafeCoreVisibilitySyntax? visibility,
            bool isPublic, TextSpan span, out string? visibilityScope)
        {
            ScopeBuilder module = FindModuleScope(scope);
            visibilityScope = isPublic ? null : module.Path;
            if (visibility is null) return true;
            switch (visibility.Kind)
            {
                case SafeCoreVisibilityKind.Public: visibilityScope = null; return true;
                case SafeCoreVisibilityKind.Private:
                case SafeCoreVisibilityKind.Self: visibilityScope = module.Path; return true;
                case SafeCoreVisibilityKind.Crate: visibilityScope = "crate"; return true;
                case SafeCoreVisibilityKind.Super:
                    if (module.Parent is not null) { visibilityScope = FindModuleScope(module.Parent).Path; return true; }
                    break;
                case SafeCoreVisibilityKind.Restricted:
                    if (visibility.Path is null) break;
                    if (!TrySplitPath(visibility.Path, span, out IReadOnlyList<string> segments)) return false;
                    ScopeBuilder? target = segments[0] switch { "crate" => _root, "self" or "super" => module, _ => null };
                    if (target is null) break;
                    var position = segments[0] == "super" ? 0 : 1;
                    for (; position < segments.Count && segments[position] == "super"; position++)
                    {
                        if (!Step(span)) return false;
                        target = target?.Parent;
                    }
                    if (target is null) break;
                    string path = target.Path;
                    for (; position < segments.Count; position++)
                    {
                        if (!Step(span)) return false;
                        path += "::" + CanonicalizeIdentifier(segments[position]);
                        if (path.Length > _options.MaximumPathLength) { StopLimit(span); return false; }
                    }
                    // An ancestor path denotes a real module, and cannot resolve through an import alias.
                    for (ScopeBuilder? ancestor = module; ancestor is not null; ancestor = ancestor.Parent)
                    {
                        if (!Step(span)) return false;
                        if (ancestor.Path == path && ancestor.Path == ancestor.ModulePath)
                        {
                            visibilityScope = path;
                            return true;
                        }
                    }
                    break;
            }
            AddDiagnostic(SafeCoreNameResolutionDiagnosticCodes.InvalidPath,
                "A visibility restriction must name the current module or one of its ancestors.", visibility.Span);
            return false;
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

        private ScopeBuilder? GetMemberScope(SymbolBuilder symbol)
        {
            if (symbol.Kind is SafeCoreSymbolKind.Module or SafeCoreSymbolKind.Enum) return symbol.MemberScope;
            if (!_options.EnableTypeSystemExtensions || !_aliasDefinitions.TryGetValue(symbol, out SafeCoreTypeAliasSyntax? alias) ||
                alias.GenericParameters.Count != 0 || alias.Type is not SafeCorePathTypeSyntax path ||
                path.Segments.Any(static segment => segment.GenericArguments.Count != 0 || segment.Arguments.Count != 0)) return null;
            if (!Step(alias.Span) || _aliasMemberStack.Count >= _options.MaximumNestingDepth) { StopLimit(alias.Span); return null; }
            if (!_aliasMemberStack.Add(symbol)) return null;
            try
            {
                string targetPath = string.Join("::", path.Segments.Select(static segment => segment.Name));
                PathResult target = ResolvePathInternal(targetPath, symbol.DeclaringScope, SafeCoreSymbolNamespace.Type, alias.Span, emitDiagnostic: false);
                return target.Status == SafeCoreNameResolutionStatus.Resolved && target.Symbol is not null
                    ? GetMemberScope(GetResolvedImportTarget(target.Symbol)) : null;
            }
            finally { _aliasMemberStack.Remove(symbol); }
        }

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
            bool allowShadowing = false,
            SafeCoreVisibilitySyntax? visibility = null,
            bool isAnonymousImport = false)
        {
            if (!Step(span))
            {
                return null;
            }

            if (!TryResolveVisibility(scope, visibility, isPublic, span, out string? visibilityScope)) return null;
            if (visibility is not null) isPublic = visibility.Kind == SafeCoreVisibilityKind.Public;

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

            // Tuple-field declarations use their positional index as their semantic name.
            // This exception is confined to fields in the opt-in type profile; user
            // declarations and path segments must still be valid Rust identifiers.
            bool isTupleFieldIndex = _options.EnableTypeSystemExtensions && kind == SafeCoreSymbolKind.Field &&
                int.TryParse(name, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int fieldIndex) && fieldIndex >= 0 &&
                fieldIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) == name;
            if (!isTupleFieldIndex && !IsValidIdentifier(name, span))
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
                scope.Path + "::" + canonicalName + (isAnonymousImport ? "@" + span.Start : string.Empty),
                kind,
                @namespace,
                isPublic,
                isImport,
                targetPath,
                span,
                scope)
            {
                VisibilityScopePath = visibilityScope,
                DeclaredImportVisibilityScopePath = visibilityScope,
                IsAnonymousImport = isAnonymousImport,
            };
            if (isAnonymousImport)
            {
                scope.Symbols.Add(symbol);
                _symbols.Add(symbol);
                return symbol;
            }
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
            _options.CancellationToken.ThrowIfCancellationRequested();
            if (_operations <= _options.MaximumOperations && Stopwatch.GetElapsedTime(_started) <= _options.Timeout)
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

        private bool HasUnsupportedAttributes(IReadOnlyList<SafeCoreAttributeSyntax> attributes)
        {
            for (var index = 0; index < attributes.Count; index++)
            {
                SafeCoreAttributeSyntax attribute = attributes[index];
                if (!Step(attribute.Span) || !IsDocumentationAttribute(attribute))
                {
                    return true;
                }
            }

            return false;
        }

        private void RejectUnsupportedRootAttributes(
            IReadOnlyList<SafeCoreAttributeSyntax> attributes,
            TextSpan fallbackSpan)
        {
            for (var index = 0; index < attributes.Count; index++)
            {
                SafeCoreAttributeSyntax attribute = attributes[index];
                if (!Step(attribute.Span))
                {
                    return;
                }
                // Preserve the legacy HIR-only root marker; executable type
                // checking still rejects no_std rather than ignoring it.
                if (IsDocumentationAttribute(attribute) ||
                    (attribute.IsInner && string.Equals(attribute.Path, "no_std", StringComparison.Ordinal) &&
                     string.IsNullOrEmpty(attribute.ArgumentsText)))
                {
                    continue;
                }

                RejectNewSyntax(attribute.Span.Length == 0 ? fallbackSpan : attribute.Span);
                return;
            }
        }

        private void RejectUnsupportedAttributes(
            IReadOnlyList<SafeCoreAttributeSyntax> attributes,
            TextSpan fallbackSpan)
        {
            for (var index = 0; index < attributes.Count; index++)
            {
                SafeCoreAttributeSyntax attribute = attributes[index];
                if (!Step(attribute.Span))
                {
                    return;
                }
                if (!IsDocumentationAttribute(attribute))
                {
                    RejectNewSyntax(attribute.Span.Length == 0 ? fallbackSpan : attribute.Span);
                    return;
                }
            }
        }

        private static bool IsDocumentationAttribute(SafeCoreAttributeSyntax attribute) =>
            attribute.IsDocumentation && attribute.Path == "doc";

        private void RejectNewSyntax(TextSpan span) => AddDiagnostic(
            SafeCoreNameResolutionDiagnosticCodes.UnsupportedSyntax,
            "This syntax is parsed, but its semantics are not implemented by the current safe-core name-resolution/HIR profile.",
            span);

        private void AddDiagnostic(string code, string message, TextSpan span)
        {
            if (_probingImports) return;
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
            public bool IsPublic { get; set; }
            public string? VisibilityScopePath { get; set; }
            public string? DeclaredImportVisibilityScopePath { get; init; }
            public bool IsAnonymousImport { get; init; }
            public bool IsSelfImport { get; set; }
            public SymbolBuilder? ImportPeer { get; set; }
            public bool IsSecondaryImportBinding { get; set; }
            public bool ImportVisibilityError { get; set; }
            public bool IsGlobImport { get; set; }
            public bool IsActiveGlobImport { get; set; }
            public bool IsImport { get; }
            public string? TargetPath { get; }
            public TextSpan Span { get; }
            public ScopeBuilder DeclaringScope { get; }
            public ScopeBuilder? MemberScope { get; set; }
            public bool ImportResolutionAttempted { get; set; }
            public bool ImportCycleReported { get; set; }
            public ImportResolution ImportResolution { get; set; } =
                new(SafeCoreNameResolutionStatus.Invalid, null);
            public SymbolBuilder? ResolvedImportTarget { get; set; }
        }

        private sealed record ImportResolution(SafeCoreNameResolutionStatus Status, SymbolBuilder? Target);

        private sealed record GlobImport(
            string TargetPath,
            ScopeBuilder Scope,
            SafeCoreVisibilitySyntax Visibility,
            TextSpan Span)
        {
            public Dictionary<(string Name, SymbolBuilder Target), SymbolBuilder> Bindings { get; } = [];
        }

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
