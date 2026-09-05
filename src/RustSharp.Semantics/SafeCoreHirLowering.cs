using System.Diagnostics.CodeAnalysis;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>
/// Lowers the bounded safe-core syntax model into a flat, name-bound HIR arena.
/// Used by the opt-in safe-core compilation profile.
/// </summary>
public static class SafeCoreHirLowering
{
    private const int AbsoluteMaximumNodes = 500_000;
    private const int AbsoluteMaximumNestingDepth = 512;
    private const int AbsoluteMaximumOperations = 4_000_000;
    private const int AbsoluteMaximumDiagnostics = 1024;
    private const int AbsoluteMaximumDiagnosticMessageLength = 4096;

    public static SafeCoreHirResult Lower(
        SafeCoreSyntaxResult syntax,
        SafeCoreHirLoweringOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        SafeCoreHirLoweringOptions normalized = NormalizeOptions(options);
        if (!syntax.IsSuccessful || syntax.Root is null)
        {
            return RejectInvalidSyntax(syntax, normalized);
        }

        SafeCoreNameResolutionResult resolution = SafeCoreNameResolution.Resolve(
            syntax,
            normalized.NameResolution);
        if (!resolution.IsSuccessful)
        {
            return RejectInvalidResolution(syntax, resolution, normalized);
        }

        return new Lowerer(syntax.SourcePath, resolution, normalized).Run(syntax.Root);
    }

    private static SafeCoreHirLoweringOptions NormalizeOptions(SafeCoreHirLoweringOptions? options)
    {
        options ??= new SafeCoreHirLoweringOptions();
        return options with
        {
            MaximumNodes = Math.Clamp(options.MaximumNodes, 1, AbsoluteMaximumNodes),
            MaximumNestingDepth = Math.Clamp(
                options.MaximumNestingDepth,
                1,
                AbsoluteMaximumNestingDepth),
            MaximumOperations = Math.Clamp(
                options.MaximumOperations,
                1,
                AbsoluteMaximumOperations),
            MaximumDiagnostics = Math.Clamp(
                options.MaximumDiagnostics,
                1,
                AbsoluteMaximumDiagnostics),
            MaximumDiagnosticMessageLength = Math.Clamp(
                options.MaximumDiagnosticMessageLength,
                1,
                AbsoluteMaximumDiagnosticMessageLength),
            NameResolution = options.NameResolution ?? new SafeCoreNameResolutionOptions(),
        };
    }

    private static SafeCoreHirResult RejectInvalidSyntax(
        SafeCoreSyntaxResult syntax,
        SafeCoreHirLoweringOptions options)
    {
        IReadOnlyList<Diagnostic> diagnostics = syntax.Diagnostics.Count == 0
            ? [new Diagnostic(
                SafeCoreHirDiagnosticCodes.InvalidInput,
                "HIR lowering requires a successful safe-core syntax result.",
                new TextSpan(0, 0))]
            : syntax.Diagnostics;
        return RejectedResult(
            syntax.SourcePath,
            nameResolution: null,
            diagnostics,
            syntax.IsTruncated,
            options);
    }

    private static SafeCoreHirResult RejectInvalidResolution(
        SafeCoreSyntaxResult syntax,
        SafeCoreNameResolutionResult resolution,
        SafeCoreHirLoweringOptions options)
    {
        IReadOnlyList<Diagnostic> diagnostics = resolution.Diagnostics.Count == 0
            ? [new Diagnostic(
                SafeCoreHirDiagnosticCodes.InvalidInput,
                "HIR lowering requires successful safe-core name resolution.",
                syntax.Root?.Span ?? new TextSpan(0, 0))]
            : resolution.Diagnostics;
        return RejectedResult(
            syntax.SourcePath,
            resolution,
            diagnostics,
            resolution.IsTruncated,
            options);
    }

    private static SafeCoreHirResult RejectedResult(
        string sourcePath,
        SafeCoreNameResolutionResult? nameResolution,
        IReadOnlyList<Diagnostic> inputDiagnostics,
        bool isTruncated,
        SafeCoreHirLoweringOptions options)
    {
        var diagnostics = new List<Diagnostic>(
            Math.Min(inputDiagnostics.Count, options.MaximumDiagnostics));
        int retained = Math.Min(inputDiagnostics.Count, options.MaximumDiagnostics);
        for (var index = 0; index < retained; index++)
        {
            Diagnostic diagnostic = inputDiagnostics[index];
            diagnostics.Add(diagnostic with
            {
                Message = LimitText(diagnostic.Message, options.MaximumDiagnosticMessageLength),
            });
        }

        bool diagnosticsTruncated = inputDiagnostics.Count > retained;
        if (diagnosticsTruncated && diagnostics.Count != 0)
        {
            diagnostics[^1] = new Diagnostic(
                SafeCoreHirDiagnosticCodes.LimitReached,
                LimitText(
                    "HIR lowering stopped after reaching the configured diagnostic limit.",
                    options.MaximumDiagnosticMessageLength),
                diagnostics[^1].Span);
        }

        return new SafeCoreHirResult(
            sourcePath ?? string.Empty,
            null,
            Array.Empty<SafeCoreHirNode>(),
            nameResolution,
            diagnostics,
            isTruncated || diagnosticsTruncated);
    }

    private static string LimitText(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private sealed class Lowerer
    {
        private static readonly HashSet<string> PrimitiveTypeNames = new(StringComparer.Ordinal)
        {
            "bool", "char", "str",
            "i8", "i16", "i32", "i64", "i128", "isize",
            "u8", "u16", "u32", "u64", "u128", "usize",
            "f32", "f64",
        };

        private readonly string _sourcePath;
        private readonly SafeCoreNameResolutionResult _resolution;
        private readonly SafeCoreHirLoweringOptions _options;
        private readonly List<NodeBuilder> _nodes = [];
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly Dictionary<DeclarationKey, List<SafeCoreSymbol>> _declarations = [];
        private readonly Dictionary<ReferenceKey, List<SafeCorePathResolution>> _references = [];
        private int _operations;
        private int _depth;
        private bool _truncated;
        private bool _limitReported;

        public Lowerer(
            string sourcePath,
            SafeCoreNameResolutionResult resolution,
            SafeCoreHirLoweringOptions options)
        {
            _sourcePath = sourcePath ?? string.Empty;
            _resolution = resolution;
            _options = options;
        }

        public SafeCoreHirResult Run(SafeCoreCompilationUnitSyntax root)
        {
            BuildIndexes(root.Span);
            int rootId = _truncated ? -1 : LowerCompilationUnit(root);
            var nodes = new List<SafeCoreHirNode>(_nodes.Count);
            for (var index = 0; index < _nodes.Count; index++)
            {
                NodeBuilder node = _nodes[index];
                nodes.Add(new SafeCoreHirNode(
                    node.Id,
                    node.Kind,
                    node.Span,
                    node.Name,
                    node.Value,
                    node.Flags,
                    node.DeclaredSymbol,
                    node.ReferencedSymbol,
                    node.ChildIds));
            }

            SafeCoreHirNode? publicRoot = rootId >= 0 && rootId < nodes.Count
                ? nodes[rootId]
                : null;
            return new SafeCoreHirResult(
                _sourcePath,
                publicRoot,
                nodes,
                _resolution,
                _diagnostics,
                _truncated);
        }

        private void BuildIndexes(TextSpan span)
        {
            for (var index = 0; index < _resolution.Symbols.Count; index++)
            {
                SafeCoreSymbol symbol = _resolution.Symbols[index];
                if (!Step(span))
                {
                    return;
                }

                var key = new DeclarationKey(symbol.Span, symbol.Kind);
                if (!_declarations.TryGetValue(key, out List<SafeCoreSymbol>? candidates))
                {
                    candidates = [];
                    _declarations.Add(key, candidates);
                }

                candidates.Add(symbol);
            }

            for (var index = 0; index < _resolution.Resolutions.Count; index++)
            {
                SafeCorePathResolution resolution = _resolution.Resolutions[index];
                if (!Step(span))
                {
                    return;
                }

                var key = new ReferenceKey(resolution.Span, resolution.Path);
                if (!_references.TryGetValue(key, out List<SafeCorePathResolution>? candidates))
                {
                    candidates = [];
                    _references.Add(key, candidates);
                }

                candidates.Add(resolution);
            }
        }

        private int LowerCompilationUnit(SafeCoreCompilationUnitSyntax syntax)
        {
            if (!TryCreateNode(SafeCoreHirNodeKind.CompilationUnit, syntax.Span, out NodeBuilder? node))
            {
                return -1;
            }

            try
            {
                LowerAttributes(syntax.Attributes, node);
                for (var index = 0; index < syntax.Items.Count && !_truncated; index++)
                {
                    AddChild(node, LowerItem(syntax.Items[index]));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerItem(SafeCoreItemSyntax syntax) => syntax switch
        {
            SafeCoreModuleSyntax module => LowerModule(module),
            SafeCoreUseSyntax use => LowerUse(use),
            SafeCoreFunctionSyntax function => LowerFunction(function),
            SafeCoreStructSyntax structure => LowerStruct(structure),
            SafeCoreEnumSyntax enumeration => LowerEnum(enumeration),
            SafeCoreTypeAliasSyntax alias => LowerTypeAlias(alias),
            SafeCoreConstSyntax constant => LowerConst(constant),
            _ => Unsupported(syntax.Span, "item"),
        };

        private int LowerModule(SafeCoreModuleSyntax syntax)
        {
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.Module,
                    syntax.Span,
                    out NodeBuilder? node,
                    syntax.Name,
                    flags: VisibilityFlag(syntax.IsPublic),
                    declaredSymbol: FindDeclaration(
                        syntax.Span,
                        SafeCoreSymbolKind.Module,
                        syntax.Name)))
            {
                return -1;
            }

            try
            {
                LowerAttributes(syntax.Attributes, node);
                for (var index = 0; index < syntax.Items.Count && !_truncated; index++)
                {
                    AddChild(node, LowerItem(syntax.Items[index]));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerUse(SafeCoreUseSyntax syntax)
        {
            string declaredName = syntax.Alias ?? GetLastPathSegment(syntax.Path);
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.Import,
                    syntax.Span,
                    out NodeBuilder? node,
                    declaredName,
                    syntax.Path,
                    VisibilityFlag(syntax.IsPublic),
                    FindDeclaration(syntax.Span, SafeCoreSymbolKind.Import, declaredName)))
            {
                return -1;
            }

            try
            {
                LowerAttributes(syntax.Attributes, node);
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerFunction(SafeCoreFunctionSyntax syntax)
        {
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.Function,
                    syntax.Span,
                    out NodeBuilder? node,
                    syntax.Name,
                    flags: VisibilityFlag(syntax.IsPublic),
                    declaredSymbol: FindDeclaration(
                        syntax.Span,
                        SafeCoreSymbolKind.Function,
                        syntax.Name)))
            {
                return -1;
            }

            try
            {
                LowerAttributes(syntax.Attributes, node);
                LowerGenericParameters(syntax.GenericParameters, node);
                for (var index = 0; index < syntax.Parameters.Count && !_truncated; index++)
                {
                    AddChild(node, LowerParameter(syntax.Parameters[index]));
                }

                if (syntax.ReturnType is not null)
                {
                    AddChild(node, LowerType(syntax.ReturnType, requireBinding: true));
                }

                AddChild(node, LowerBlock(syntax.Body));
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerStruct(SafeCoreStructSyntax syntax)
        {
            SafeCoreHirNodeModifiers flags = VisibilityFlag(syntax.IsPublic);
            if (syntax.IsTupleStruct)
            {
                flags |= SafeCoreHirNodeModifiers.TupleStruct;
            }

            if (!TryCreateNode(
                    SafeCoreHirNodeKind.Struct,
                    syntax.Span,
                    out NodeBuilder? node,
                    syntax.Name,
                    flags: flags,
                    declaredSymbol: FindDeclaration(
                        syntax.Span,
                        SafeCoreSymbolKind.Struct,
                        syntax.Name)))
            {
                return -1;
            }

            try
            {
                LowerAttributes(syntax.Attributes, node);
                LowerGenericParameters(syntax.GenericParameters, node);
                for (var index = 0; index < syntax.Fields.Count && !_truncated; index++)
                {
                    AddChild(node, LowerField(syntax.Fields[index]));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerEnum(SafeCoreEnumSyntax syntax)
        {
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.Enum,
                    syntax.Span,
                    out NodeBuilder? node,
                    syntax.Name,
                    flags: VisibilityFlag(syntax.IsPublic),
                    declaredSymbol: FindDeclaration(
                        syntax.Span,
                        SafeCoreSymbolKind.Enum,
                        syntax.Name)))
            {
                return -1;
            }

            try
            {
                LowerAttributes(syntax.Attributes, node);
                LowerGenericParameters(syntax.GenericParameters, node);
                for (var index = 0; index < syntax.Variants.Count && !_truncated; index++)
                {
                    AddChild(node, LowerEnumVariant(syntax.Variants[index]));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerEnumVariant(SafeCoreEnumVariantSyntax syntax)
        {
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.EnumVariant,
                    syntax.Span,
                    out NodeBuilder? node,
                    syntax.Name,
                    declaredSymbol: FindDeclaration(
                        syntax.Span,
                        SafeCoreSymbolKind.EnumVariant,
                        syntax.Name)))
            {
                return -1;
            }

            try
            {
                for (var index = 0; index < syntax.Fields.Count && !_truncated; index++)
                {
                    AddChild(node, LowerField(syntax.Fields[index]));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerTypeAlias(SafeCoreTypeAliasSyntax syntax)
        {
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.TypeAlias,
                    syntax.Span,
                    out NodeBuilder? node,
                    syntax.Name,
                    flags: VisibilityFlag(syntax.IsPublic),
                    declaredSymbol: FindDeclaration(
                        syntax.Span,
                        SafeCoreSymbolKind.TypeAlias,
                        syntax.Name)))
            {
                return -1;
            }

            try
            {
                LowerAttributes(syntax.Attributes, node);
                LowerGenericParameters(syntax.GenericParameters, node);
                AddChild(node, LowerType(syntax.Type, requireBinding: true));
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerConst(SafeCoreConstSyntax syntax)
        {
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.Const,
                    syntax.Span,
                    out NodeBuilder? node,
                    syntax.Name,
                    flags: VisibilityFlag(syntax.IsPublic),
                    declaredSymbol: FindDeclaration(
                        syntax.Span,
                        SafeCoreSymbolKind.Const,
                        syntax.Name)))
            {
                return -1;
            }

            try
            {
                LowerAttributes(syntax.Attributes, node);
                AddChild(node, LowerType(syntax.Type, requireBinding: true));
                AddChild(node, LowerExpression(syntax.Value));
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerGenericParameter(SafeCoreGenericParameterSyntax syntax)
        {
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.GenericParameter,
                    syntax.Span,
                    out NodeBuilder? node,
                    syntax.Name,
                    declaredSymbol: FindDeclaration(
                        syntax.Span,
                        SafeCoreSymbolKind.GenericParameter,
                        syntax.Name)))
            {
                return -1;
            }

            try
            {
                // Trait declarations are not in the current AST, so bounds
                // remain deliberately unbound until that namespace exists.
                for (var index = 0; index < syntax.Bounds.Count && !_truncated; index++)
                {
                    AddChild(node, LowerType(syntax.Bounds[index], requireBinding: false));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerParameter(SafeCoreParameterSyntax syntax)
        {
            if (!TryCreateNode(SafeCoreHirNodeKind.Parameter, syntax.Span, out NodeBuilder? node))
            {
                return -1;
            }

            try
            {
                AddChild(node, LowerPattern(syntax.Pattern, SafeCoreSymbolKind.Parameter));
                AddChild(node, LowerType(syntax.Type, requireBinding: true));
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerField(SafeCoreFieldSyntax syntax)
        {
            SafeCoreSymbol? declaration = syntax.Name is null
                ? null
                : FindDeclaration(syntax.Span, SafeCoreSymbolKind.Field, syntax.Name);
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.Field,
                    syntax.Span,
                    out NodeBuilder? node,
                    syntax.Name,
                    flags: VisibilityFlag(syntax.IsPublic),
                    declaredSymbol: declaration))
            {
                return -1;
            }

            try
            {
                AddChild(node, LowerType(syntax.Type, requireBinding: true));
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerBlock(SafeCoreBlockSyntax syntax)
        {
            if (!TryCreateNode(SafeCoreHirNodeKind.Block, syntax.Span, out NodeBuilder? node))
            {
                return -1;
            }

            try
            {
                for (var index = 0; index < syntax.Statements.Count && !_truncated; index++)
                {
                    AddChild(node, LowerStatement(syntax.Statements[index]));
                }

                if (syntax.TailExpression is not null)
                {
                    AddChild(node, LowerExpression(syntax.TailExpression));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerStatement(SafeCoreStatementSyntax syntax) => syntax switch
        {
            SafeCoreLetStatementSyntax let => LowerLet(let),
            SafeCoreReturnStatementSyntax @return => LowerReturn(@return),
            SafeCoreExpressionStatementSyntax expression => LowerExpressionStatement(expression),
            _ => Unsupported(syntax.Span, "statement"),
        };

        private int LowerLet(SafeCoreLetStatementSyntax syntax)
        {
            if (!TryCreateNode(SafeCoreHirNodeKind.LetStatement, syntax.Span, out NodeBuilder? node))
            {
                return -1;
            }

            try
            {
                AddChild(node, LowerPattern(syntax.Pattern, SafeCoreSymbolKind.Local));
                if (syntax.Type is not null)
                {
                    AddChild(node, LowerType(syntax.Type, requireBinding: true));
                }

                if (syntax.Initializer is not null)
                {
                    AddChild(node, LowerExpression(syntax.Initializer));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerReturn(SafeCoreReturnStatementSyntax syntax)
        {
            if (!TryCreateNode(SafeCoreHirNodeKind.ReturnStatement, syntax.Span, out NodeBuilder? node))
            {
                return -1;
            }

            try
            {
                if (syntax.Value is not null)
                {
                    AddChild(node, LowerExpression(syntax.Value));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerExpressionStatement(SafeCoreExpressionStatementSyntax syntax)
        {
            SafeCoreHirNodeModifiers flags = syntax.HasSemicolon
                ? SafeCoreHirNodeModifiers.HasSemicolon
                : SafeCoreHirNodeModifiers.None;
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.ExpressionStatement,
                    syntax.Span,
                    out NodeBuilder? node,
                    flags: flags))
            {
                return -1;
            }

            try
            {
                AddChild(node, LowerExpression(syntax.Expression));
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerPattern(SafeCorePatternSyntax syntax, SafeCoreSymbolKind bindingKind) => syntax switch
        {
            SafeCoreIdentifierPatternSyntax identifier => LowerIdentifierPattern(identifier, bindingKind),
            SafeCoreWildcardPatternSyntax wildcard => LowerLeaf(SafeCoreHirNodeKind.WildcardPattern, wildcard.Span),
            SafeCoreLiteralPatternSyntax literal => LowerLeaf(
                SafeCoreHirNodeKind.LiteralPattern,
                literal.Span,
                value: literal.RawText),
            SafeCoreTuplePatternSyntax tuple => LowerTuplePattern(tuple, bindingKind),
            SafeCorePathPatternSyntax path => LowerPathPattern(path, bindingKind),
            _ => Unsupported(syntax.Span, "pattern"),
        };

        private int LowerIdentifierPattern(
            SafeCoreIdentifierPatternSyntax syntax,
            SafeCoreSymbolKind bindingKind)
        {
            SafeCoreHirNodeModifiers flags = syntax.IsMutable
                ? SafeCoreHirNodeModifiers.Mutable
                : SafeCoreHirNodeModifiers.None;
            return LowerLeaf(
                SafeCoreHirNodeKind.IdentifierPattern,
                syntax.Span,
                syntax.Name,
                flags: flags,
                declaredSymbol: FindDeclaration(syntax.Span, bindingKind, syntax.Name));
        }

        private int LowerTuplePattern(
            SafeCoreTuplePatternSyntax syntax,
            SafeCoreSymbolKind bindingKind)
        {
            SafeCoreHirNodeModifiers flags = syntax.HasTrailingComma
                ? SafeCoreHirNodeModifiers.HasTrailingComma
                : SafeCoreHirNodeModifiers.None;
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.TuplePattern,
                    syntax.Span,
                    out NodeBuilder? node,
                    flags: flags))
            {
                return -1;
            }

            try
            {
                for (var index = 0; index < syntax.Elements.Count && !_truncated; index++)
                {
                    AddChild(node, LowerPattern(syntax.Elements[index], bindingKind));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerPathPattern(
            SafeCorePathPatternSyntax syntax,
            SafeCoreSymbolKind bindingKind)
        {
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.PathPattern,
                    syntax.Span,
                    out NodeBuilder? node,
                    syntax.Path,
                    referencedSymbol: FindReference(syntax.Path, syntax.Span)))
            {
                return -1;
            }

            try
            {
                for (var index = 0; index < syntax.Arguments.Count && !_truncated; index++)
                {
                    AddChild(node, LowerPattern(syntax.Arguments[index], bindingKind));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerExpression(SafeCoreExpressionSyntax syntax) => syntax switch
        {
            SafeCoreNameExpressionSyntax name => LowerLeaf(
                SafeCoreHirNodeKind.NameExpression,
                name.Span,
                name.Path,
                referencedSymbol: FindReference(name.Path, name.Span)),
            SafeCoreLiteralExpressionSyntax literal => LowerLeaf(
                SafeCoreHirNodeKind.LiteralExpression,
                literal.Span,
                value: literal.RawText),
            SafeCoreUnaryExpressionSyntax unary => LowerUnary(unary),
            SafeCoreBinaryExpressionSyntax binary => LowerBinary(binary),
            SafeCoreCallExpressionSyntax call => LowerCall(call),
            SafeCorePrintExpressionSyntax print => LowerPrint(print),
            SafeCoreTupleExpressionSyntax tuple => LowerTupleExpression(tuple),
            SafeCoreArrayExpressionSyntax array => LowerArrayExpression(array),
            SafeCoreBlockExpressionSyntax block => LowerBlockExpression(block),
            SafeCoreIfExpressionSyntax conditional => LowerIf(conditional),
            SafeCoreIndexExpressionSyntax index => LowerIndex(index),
            _ => Unsupported(syntax.Span, "expression"),
        };

        private int LowerUnary(SafeCoreUnaryExpressionSyntax syntax)
        {
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.UnaryExpression,
                    syntax.Span,
                    out NodeBuilder? node,
                    value: syntax.Operator))
            {
                return -1;
            }

            try
            {
                AddChild(node, LowerExpression(syntax.Operand));
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerBinary(SafeCoreBinaryExpressionSyntax syntax)
        {
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.BinaryExpression,
                    syntax.Span,
                    out NodeBuilder? node,
                    value: syntax.Operator))
            {
                return -1;
            }

            try
            {
                AddChild(node, LowerExpression(syntax.Left));
                AddChild(node, LowerExpression(syntax.Right));
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerCall(SafeCoreCallExpressionSyntax syntax)
        {
            if (!TryCreateNode(SafeCoreHirNodeKind.CallExpression, syntax.Span, out NodeBuilder? node))
            {
                return -1;
            }

            try
            {
                AddChild(node, LowerExpression(syntax.Callee));
                for (var index = 0; index < syntax.Arguments.Count && !_truncated; index++)
                {
                    AddChild(node, LowerExpression(syntax.Arguments[index]));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerPrint(SafeCorePrintExpressionSyntax syntax)
        {
            if (!TryCreateNode(SafeCoreHirNodeKind.PrintExpression, syntax.Span, out NodeBuilder? node))
            {
                return -1;
            }

            try
            {
                for (var index = 0; index < syntax.Arguments.Count && !_truncated; index++)
                {
                    AddChild(node, LowerExpression(syntax.Arguments[index]));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerTupleExpression(SafeCoreTupleExpressionSyntax syntax)
        {
            SafeCoreHirNodeModifiers flags = syntax.HasTrailingComma
                ? SafeCoreHirNodeModifiers.HasTrailingComma
                : SafeCoreHirNodeModifiers.None;
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.TupleExpression,
                    syntax.Span,
                    out NodeBuilder? node,
                    flags: flags))
            {
                return -1;
            }

            try
            {
                for (var index = 0; index < syntax.Elements.Count && !_truncated; index++)
                {
                    AddChild(node, LowerExpression(syntax.Elements[index]));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerArrayExpression(SafeCoreArrayExpressionSyntax syntax)
        {
            SafeCoreHirNodeModifiers flags = syntax.RepeatCount is null
                ? SafeCoreHirNodeModifiers.None
                : SafeCoreHirNodeModifiers.RepeatedArray;
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.ArrayExpression,
                    syntax.Span,
                    out NodeBuilder? node,
                    flags: flags))
            {
                return -1;
            }

            try
            {
                for (var index = 0; index < syntax.Elements.Count && !_truncated; index++)
                {
                    AddChild(node, LowerExpression(syntax.Elements[index]));
                }

                if (syntax.RepeatCount is not null)
                {
                    AddChild(node, LowerExpression(syntax.RepeatCount));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerBlockExpression(SafeCoreBlockExpressionSyntax syntax)
        {
            if (!TryCreateNode(SafeCoreHirNodeKind.BlockExpression, syntax.Span, out NodeBuilder? node))
            {
                return -1;
            }

            try
            {
                AddChild(node, LowerBlock(syntax.Block));
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerIf(SafeCoreIfExpressionSyntax syntax)
        {
            if (!TryCreateNode(SafeCoreHirNodeKind.IfExpression, syntax.Span, out NodeBuilder? node))
            {
                return -1;
            }

            try
            {
                AddChild(node, LowerExpression(syntax.Condition));
                AddChild(node, LowerBlock(syntax.Then));
                if (syntax.Else is not null)
                {
                    AddChild(node, LowerExpression(syntax.Else));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerIndex(SafeCoreIndexExpressionSyntax syntax)
        {
            if (!TryCreateNode(SafeCoreHirNodeKind.IndexExpression, syntax.Span, out NodeBuilder? node))
            {
                return -1;
            }

            try
            {
                AddChild(node, LowerExpression(syntax.Target));
                AddChild(node, LowerExpression(syntax.Index));
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerType(SafeCoreTypeSyntax syntax, bool requireBinding) => syntax switch
        {
            SafeCorePathTypeSyntax path => LowerPathType(path, requireBinding),
            SafeCoreReferenceTypeSyntax reference => LowerReferenceType(reference, requireBinding),
            SafeCoreTupleTypeSyntax tuple => LowerTupleType(tuple, requireBinding),
            SafeCoreArrayTypeSyntax array => LowerArrayType(array, requireBinding),
            SafeCoreSliceTypeSyntax slice => LowerSliceType(slice, requireBinding),
            SafeCoreUnitTypeSyntax unit => LowerLeaf(SafeCoreHirNodeKind.UnitType, unit.Span),
            SafeCoreNeverTypeSyntax never => LowerLeaf(SafeCoreHirNodeKind.NeverType, never.Span),
            _ => Unsupported(syntax.Span, "type"),
        };

        private int LowerPathType(SafeCorePathTypeSyntax syntax, bool requireBinding)
        {
            string path = string.Join("::", syntax.Segments.Select(static segment => segment.Name));
            bool isPrimitive = syntax.Segments.Count == 1 && PrimitiveTypeNames.Contains(path);
            SafeCoreSymbol? reference = requireBinding && !isPrimitive
                ? FindReference(path, syntax.Span)
                : null;
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.PathType,
                    syntax.Span,
                    out NodeBuilder? node,
                    path,
                    referencedSymbol: reference))
            {
                return -1;
            }

            try
            {
                for (var index = 0; index < syntax.Segments.Count && !_truncated; index++)
                {
                    AddChild(node, LowerPathSegment(syntax.Segments[index], requireBinding));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerPathSegment(SafeCorePathSegmentSyntax syntax, bool requireBinding)
        {
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.PathSegment,
                    syntax.Span,
                    out NodeBuilder? node,
                    syntax.Name))
            {
                return -1;
            }

            try
            {
                for (var index = 0; index < syntax.GenericArguments.Count && !_truncated; index++)
                {
                    AddChild(node, LowerType(syntax.GenericArguments[index], requireBinding));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerReferenceType(SafeCoreReferenceTypeSyntax syntax, bool requireBinding)
        {
            SafeCoreHirNodeModifiers flags = syntax.IsMutable
                ? SafeCoreHirNodeModifiers.MutableReference
                : SafeCoreHirNodeModifiers.None;
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.ReferenceType,
                    syntax.Span,
                    out NodeBuilder? node,
                    value: syntax.Lifetime,
                    flags: flags))
            {
                return -1;
            }

            try
            {
                AddChild(node, LowerType(syntax.Inner, requireBinding));
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerTupleType(SafeCoreTupleTypeSyntax syntax, bool requireBinding)
        {
            SafeCoreHirNodeModifiers flags = syntax.HasTrailingComma
                ? SafeCoreHirNodeModifiers.HasTrailingComma
                : SafeCoreHirNodeModifiers.None;
            if (!TryCreateNode(
                    SafeCoreHirNodeKind.TupleType,
                    syntax.Span,
                    out NodeBuilder? node,
                    flags: flags))
            {
                return -1;
            }

            try
            {
                for (var index = 0; index < syntax.Elements.Count && !_truncated; index++)
                {
                    AddChild(node, LowerType(syntax.Elements[index], requireBinding));
                }

                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerArrayType(SafeCoreArrayTypeSyntax syntax, bool requireBinding)
        {
            if (!TryCreateNode(SafeCoreHirNodeKind.ArrayType, syntax.Span, out NodeBuilder? node))
            {
                return -1;
            }

            try
            {
                AddChild(node, LowerType(syntax.Element, requireBinding));
                AddChild(node, LowerExpression(syntax.Length));
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private int LowerSliceType(SafeCoreSliceTypeSyntax syntax, bool requireBinding)
        {
            if (!TryCreateNode(SafeCoreHirNodeKind.SliceType, syntax.Span, out NodeBuilder? node))
            {
                return -1;
            }

            try
            {
                AddChild(node, LowerType(syntax.Element, requireBinding));
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private void LowerAttributes(
            IReadOnlyList<SafeCoreAttributeSyntax> attributes,
            NodeBuilder parent)
        {
            for (var index = 0; index < attributes.Count && !_truncated; index++)
            {
                SafeCoreAttributeSyntax attribute = attributes[index];
                SafeCoreHirNodeModifiers flags = attribute.IsInner
                    ? SafeCoreHirNodeModifiers.InnerAttribute
                    : SafeCoreHirNodeModifiers.None;
                AddChild(parent, LowerLeaf(
                    SafeCoreHirNodeKind.Attribute,
                    attribute.Span,
                    attribute.Path,
                    attribute.ArgumentsText,
                    flags));
            }
        }

        private void LowerGenericParameters(
            IReadOnlyList<SafeCoreGenericParameterSyntax> parameters,
            NodeBuilder parent)
        {
            for (var index = 0; index < parameters.Count && !_truncated; index++)
            {
                AddChild(parent, LowerGenericParameter(parameters[index]));
            }
        }

        private int LowerLeaf(
            SafeCoreHirNodeKind kind,
            TextSpan span,
            string? name = null,
            string? value = null,
            SafeCoreHirNodeModifiers flags = SafeCoreHirNodeModifiers.None,
            SafeCoreSymbol? declaredSymbol = null,
            SafeCoreSymbol? referencedSymbol = null)
        {
            if (!TryCreateNode(
                    kind,
                    span,
                    out NodeBuilder? node,
                    name,
                    value,
                    flags,
                    declaredSymbol,
                    referencedSymbol))
            {
                return -1;
            }

            try
            {
                return node.Id;
            }
            finally
            {
                Exit();
            }
        }

        private bool TryCreateNode(
            SafeCoreHirNodeKind kind,
            TextSpan span,
            [NotNullWhen(true)]
            out NodeBuilder? node,
            string? name = null,
            string? value = null,
            SafeCoreHirNodeModifiers flags = SafeCoreHirNodeModifiers.None,
            SafeCoreSymbol? declaredSymbol = null,
            SafeCoreSymbol? referencedSymbol = null)
        {
            node = null;
            if (!Enter(span))
            {
                return false;
            }

            if (_nodes.Count >= _options.MaximumNodes)
            {
                StopLimit(span);
                Exit();
                return false;
            }

            node = new NodeBuilder(
                _nodes.Count,
                kind,
                span,
                name,
                value,
                flags,
                declaredSymbol,
                referencedSymbol);
            _nodes.Add(node);
            return true;
        }

        private SafeCoreSymbol? FindDeclaration(
            TextSpan span,
            SafeCoreSymbolKind kind,
            string expectedName)
        {
            if (!Step(span))
            {
                return null;
            }

            var key = new DeclarationKey(span, kind);
            string semanticName = NormalizeIdentifier(expectedName);
            if (_declarations.TryGetValue(key, out List<SafeCoreSymbol>? candidates))
            {
                SafeCoreSymbol? match = null;
                for (var index = 0; index < candidates.Count; index++)
                {
                    SafeCoreSymbol candidate = candidates[index];
                    if (!Step(span))
                    {
                        return null;
                    }

                    if (!string.Equals(candidate.Name, semanticName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (match is not null)
                    {
                        match = null;
                        break;
                    }

                    match = candidate;
                }

                if (match is not null)
                {
                    return match;
                }
            }

            AddDiagnostic(
                SafeCoreHirDiagnosticCodes.MissingDeclaration,
                $"Could not bind the {kind} declaration '{expectedName}' while lowering HIR.",
                span);
            return null;
        }

        private SafeCoreSymbol? FindReference(string path, TextSpan span)
        {
            if (!Step(span))
            {
                return null;
            }

            var key = new ReferenceKey(span, path);
            if (_references.TryGetValue(key, out List<SafeCorePathResolution>? candidates) &&
                candidates.Count == 1 &&
                candidates[0].Status == SafeCoreNameResolutionStatus.Resolved &&
                candidates[0].Symbol is not null)
            {
                return candidates[0].Symbol;
            }

            AddDiagnostic(
                SafeCoreHirDiagnosticCodes.MissingReference,
                $"Could not bind the path '{path}' while lowering HIR.",
                span);
            return null;
        }

        private int Unsupported(TextSpan span, string category)
        {
            AddDiagnostic(
                SafeCoreHirDiagnosticCodes.UnsupportedNode,
                $"The safe-core HIR prototype cannot lower this {category} node.",
                span);
            return -1;
        }

        private bool Enter(TextSpan span)
        {
            if (_truncated || _depth >= _options.MaximumNestingDepth || !Step(span))
            {
                if (!_truncated)
                {
                    StopLimit(span);
                }

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
                SafeCoreHirDiagnosticCodes.LimitReached,
                "Safe-core HIR lowering stopped after reaching a configured safety limit.",
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

        private static void AddChild(NodeBuilder parent, int childId)
        {
            if (childId >= 0)
            {
                parent.ChildIds.Add(childId);
            }
        }

        private static SafeCoreHirNodeModifiers VisibilityFlag(bool isPublic) => isPublic
            ? SafeCoreHirNodeModifiers.Public
            : SafeCoreHirNodeModifiers.None;

        private static string GetLastPathSegment(string path)
        {
            int separator = path.LastIndexOf("::", StringComparison.Ordinal);
            return separator < 0 ? path : path[(separator + 2)..];
        }

        private static string NormalizeIdentifier(string name)
        {
            string value = name.StartsWith("r#", StringComparison.Ordinal) ? name[2..] : name;
            return value.IsNormalized(NormalizationForm.FormC)
                ? value
                : value.Normalize(NormalizationForm.FormC);
        }

        private sealed class NodeBuilder
        {
            public NodeBuilder(
                int id,
                SafeCoreHirNodeKind kind,
                TextSpan span,
                string? name,
                string? value,
                SafeCoreHirNodeModifiers flags,
                SafeCoreSymbol? declaredSymbol,
                SafeCoreSymbol? referencedSymbol)
            {
                Id = id;
                Kind = kind;
                Span = span;
                Name = name;
                Value = value;
                Flags = flags;
                DeclaredSymbol = declaredSymbol;
                ReferencedSymbol = referencedSymbol;
            }

            public int Id { get; }
            public SafeCoreHirNodeKind Kind { get; }
            public TextSpan Span { get; }
            public string? Name { get; }
            public string? Value { get; }
            public SafeCoreHirNodeModifiers Flags { get; }
            public SafeCoreSymbol? DeclaredSymbol { get; }
            public SafeCoreSymbol? ReferencedSymbol { get; }
            public List<int> ChildIds { get; } = [];
        }

        private readonly record struct DeclarationKey(TextSpan Span, SafeCoreSymbolKind Kind);

        private readonly record struct ReferenceKey(TextSpan Span, string Path);
    }
}
