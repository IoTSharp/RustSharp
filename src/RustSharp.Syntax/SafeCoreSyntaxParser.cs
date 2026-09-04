using System.Collections.ObjectModel;

namespace RustSharp.Syntax;

/// <summary>
/// Parses the bounded safe-core syntax profile on top of the lossless Rust
/// lexer. This parser is intentionally separate from the first vertical-slice
/// parser used by the IL emitter.
/// </summary>
public static class SafeCoreSyntax
{
    private const int AbsoluteMaximumSourceLength = 4_000_000;
    private const int AbsoluteMaximumTokens = 1_000_000;
    private const int AbsoluteMaximumNodes = 500_000;
    private const int AbsoluteMaximumDiagnostics = 1024;
    private const int AbsoluteMaximumNestingDepth = 512;
    private const int AbsoluteMaximumOperations = 4_000_000;

    /// <summary>Parses source using the safe-core defaults and explicit bounds.</summary>
    public static SafeCoreSyntaxResult Parse(
        string? source,
        string? sourcePath = null,
        SafeCoreSyntaxOptions? options = null)
    {
        source ??= string.Empty;
        sourcePath ??= string.Empty;
        SafeCoreSyntaxOptions normalized = NormalizeOptions(options);
        var lexOptions = new RustLexerOptions
        {
            MaximumSourceLength = normalized.MaximumSourceLength,
            MaximumTokens = normalized.MaximumTokens,
            MaximumTrivia = Math.Min(normalized.MaximumTokens * 2, 1_000_000),
            MaximumDiagnostics = normalized.MaximumDiagnostics,
            MaximumDelimiterDepth = normalized.MaximumNestingDepth,
        };
        RustLexResult lexResult = RustLexer.Lex(source, sourcePath, lexOptions);
        var parser = new Parser(source, sourcePath, lexResult, normalized);
        return parser.Run();
    }

    private static SafeCoreSyntaxOptions NormalizeOptions(SafeCoreSyntaxOptions? options)
    {
        options ??= new SafeCoreSyntaxOptions();
        return options with
        {
            MaximumSourceLength = Math.Clamp(options.MaximumSourceLength, 1, AbsoluteMaximumSourceLength),
            MaximumTokens = Math.Clamp(options.MaximumTokens, 1, AbsoluteMaximumTokens),
            MaximumNodes = Math.Clamp(options.MaximumNodes, 1, AbsoluteMaximumNodes),
            MaximumDiagnostics = Math.Clamp(options.MaximumDiagnostics, 1, AbsoluteMaximumDiagnostics),
            MaximumNestingDepth = Math.Clamp(options.MaximumNestingDepth, 1, AbsoluteMaximumNestingDepth),
            MaximumOperations = Math.Clamp(options.MaximumOperations, 1, AbsoluteMaximumOperations),
        };
    }

    private sealed class Parser
    {
        private static readonly HashSet<string> UnsupportedItemKeywords =
        [
            "impl", "trait", "unsafe", "extern", "union", "macro_rules", "macro", "async", "const",
        ];

        private readonly string _source;
        private readonly string _sourcePath;
        private readonly RustLexResult _lexResult;
        private readonly SafeCoreSyntaxOptions _options;
        private readonly IReadOnlyList<RustToken> _tokens;
        private readonly List<Diagnostic> _diagnostics;
        private int _index;
        private int _nodeCount;
        private int _operationCount;
        private int _depth;
        private int _pendingGreaterClosers;
        private RustToken? _pendingGreaterToken;
        private bool _terminated;
        private bool _limitReported;

        internal Parser(
            string source,
            string sourcePath,
            RustLexResult lexResult,
            SafeCoreSyntaxOptions options)
        {
            _source = source;
            _sourcePath = sourcePath;
            _lexResult = lexResult;
            _options = options;
            _tokens = lexResult.Tokens;
            _diagnostics = new List<Diagnostic>(Math.Min(options.MaximumDiagnostics, lexResult.Diagnostics.Count + 8));
            foreach (Diagnostic diagnostic in lexResult.Diagnostics)
            {
                if (_diagnostics.Count == options.MaximumDiagnostics)
                {
                    break;
                }

                _diagnostics.Add(diagnostic);
            }
        }

        internal SafeCoreSyntaxResult Run()
        {
            SafeCoreCompilationUnitSyntax? root = null;
            if (_lexResult.IsTruncated)
            {
                AddDiagnostic(
                    SafeCoreSyntaxDiagnosticCodes.LexicalTruncation,
                    "The lexical pass was truncated before safe-core parsing could finish.",
                    _source.Length,
                    0);
            }

            if (_lexResult.Diagnostics.Count == 0 && !_lexResult.IsTruncated)
            {
                root = ParseCompilationUnit();
            }

            if (_diagnostics.Count != 0 || _terminated)
            {
                root = null;
            }

            return new SafeCoreSyntaxResult(
                _source,
                _sourcePath,
                root,
                Array.AsReadOnly(_diagnostics.ToArray()),
                _lexResult,
                _lexResult.IsTruncated || _terminated);
        }

        private SafeCoreCompilationUnitSyntax? ParseCompilationUnit()
        {
            if (!EnterDepth())
            {
                return null;
            }

            try
            {
                int start = _tokens.Count == 0 ? 0 : _tokens[0].Span.Start;
                IReadOnlyList<SafeCoreAttributeSyntax> leadingAttributes = ParseAttributes();
                SafeCoreAttributeSyntax[] rootAttributes = leadingAttributes
                    .Where(static attribute => attribute.IsInner)
                    .ToArray();
                SafeCoreAttributeSyntax[] pendingItemAttributes = leadingAttributes
                    .Where(static attribute => !attribute.IsInner)
                    .ToArray();
                var items = new List<SafeCoreItemSyntax>();

                while (!AtEnd && !_terminated)
                {
                    int before = _index;
                    SafeCoreItemSyntax? item = ParseItem(pendingItemAttributes);
                    pendingItemAttributes = Array.Empty<SafeCoreAttributeSyntax>();
                    if (item is not null)
                    {
                        items.Add(item);
                    }
                    else
                    {
                        RecoverItem();
                    }

                    if (_index == before && !AtEnd)
                    {
                        Consume();
                    }

                    if (At("}"))
                    {
                        ReportUnexpected("'}' at the module root.");
                        Consume();
                    }
                }

                if (_terminated)
                {
                    return null;
                }

                if (items.Count == 0 && pendingItemAttributes.Length != 0)
                {
                    ReportExpected("an item after the attribute");
                }

                return NewNode(
                    new SafeCoreCompilationUnitSyntax(
                        rootAttributes,
                        Array.AsReadOnly(items.ToArray()),
                        new TextSpan(start, Math.Max(0, _source.Length - start))));
            }
            finally
            {
                ExitDepth();
            }
        }

        private SafeCoreItemSyntax? ParseItem(IReadOnlyList<SafeCoreAttributeSyntax> inheritedAttributes)
        {
            if (!EnterDepth())
            {
                return null;
            }

            try
            {
                IReadOnlyList<SafeCoreAttributeSyntax> attributes = inheritedAttributes.Count == 0
                    ? ParseAttributes()
                    : inheritedAttributes;
                bool isPublic = ParseVisibility();
                if (AtEnd)
                {
                    if (attributes.Count != 0 || isPublic)
                    {
                        ReportExpected("an item after the attribute or visibility modifier");
                    }

                    return null;
                }

                return CurrentText switch
                {
                    "mod" => ParseModule(attributes, isPublic),
                    "use" => ParseUse(attributes, isPublic),
                    "fn" => ParseFunction(attributes, isPublic),
                    "struct" => ParseStruct(attributes, isPublic),
                    "enum" => ParseEnum(attributes, isPublic),
                    "type" => ParseTypeAlias(attributes, isPublic),
                    "const" => ParseConst(attributes, isPublic),
                    _ => RejectUnsupportedItem(),
                };
            }
            finally
            {
                ExitDepth();
            }
        }

        private SafeCoreItemSyntax? RejectUnsupportedItem()
        {
            string text = CurrentText ?? "<end of file>";
            if (UnsupportedItemKeywords.Contains(text) || IsName(text))
            {
                ReportUnsupported($"item '{text}'");
            }
            else
            {
                ReportUnexpected($"Unexpected token '{text}' where an item was expected.");
            }

            return null;
        }

        private SafeCoreModuleSyntax? ParseModule(
            IReadOnlyList<SafeCoreAttributeSyntax> attributes,
            bool isPublic)
        {
            RustToken? startToken = ConsumeExpected("mod");
            if (startToken is null)
            {
                return null;
            }

            (string? name, _) = ConsumeName("module name");
            if (name is null)
            {
                return null;
            }

            if (At(";"))
            {
                ReportUnsupported("external module declarations");
                return null;
            }

            if (!Expect("{"))
            {
                return null;
            }

            var items = new List<SafeCoreItemSyntax>();
            while (!AtEnd && !At("}") && !_terminated)
            {
                int before = _index;
                SafeCoreItemSyntax? item = ParseItem(ParseAttributes());
                if (item is not null)
                {
                    items.Add(item);
                }
                else
                {
                    RecoverItem();
                }

                if (_index == before && !AtEnd)
                {
                    Consume();
                }
            }

            RustToken? endToken = ConsumeExpected("}");
            if (endToken is null)
            {
                ReportUnterminated("module body");
                return null;
            }

            return NewNode(new SafeCoreModuleSyntax(
                name,
                Array.AsReadOnly(items.ToArray()),
                isPublic,
                attributes,
                SpanFrom(startToken, endToken)));
        }

        private SafeCoreUseSyntax? ParseUse(
            IReadOnlyList<SafeCoreAttributeSyntax> attributes,
            bool isPublic)
        {
            RustToken? startToken = ConsumeExpected("use");
            if (startToken is null)
            {
                return null;
            }

            var pieces = new List<string>();
            string? alias = null;
            var nestedDepth = 0;
            while (!AtEnd && !_terminated)
            {
                if (At(";") && nestedDepth == 0)
                {
                    break;
                }

                if (At("{") || At("(") || At("["))
                {
                    nestedDepth++;
                }
                else if (At("}") || At(")") || At("]"))
                {
                    nestedDepth = Math.Max(0, nestedDepth - 1);
                }

                if (At("as") && nestedDepth == 0)
                {
                    Consume();
                    (alias, _) = ConsumeName("import alias");
                    if (alias is null)
                    {
                        return null;
                    }
                }
                else
                {
                    RustToken? token = Consume();
                    if (token is not null)
                    {
                        pieces.Add(token.Text);
                    }
                }
            }

            RustToken? endToken = ConsumeExpected(";");
            if (endToken is null)
            {
                return null;
            }

            if (pieces.Count == 0)
            {
                ReportExpected("an import path");
                return null;
            }

            return NewNode(new SafeCoreUseSyntax(
                string.Concat(pieces),
                alias,
                isPublic,
                attributes,
                SpanFrom(startToken, endToken)));
        }

        private SafeCoreFunctionSyntax? ParseFunction(
            IReadOnlyList<SafeCoreAttributeSyntax> attributes,
            bool isPublic)
        {
            RustToken? startToken = ConsumeExpected("fn");
            if (startToken is null)
            {
                return null;
            }

            (string? name, _) = ConsumeName("function name");
            if (name is null)
            {
                return null;
            }

            IReadOnlyList<SafeCoreGenericParameterSyntax> generics = ParseGenericParameters();
            if (!Expect("("))
            {
                return null;
            }

            var parameters = new List<SafeCoreParameterSyntax>();
            while (!AtEnd && !At(")") && !_terminated)
            {
                int parameterStart = CurrentStart;
                SafeCorePatternSyntax? pattern = ParsePattern();
                if (pattern is null || !Expect(":"))
                {
                    RecoverUntil(",", ")");
                    if (At(","))
                    {
                        Consume();
                    }

                    continue;
                }

                SafeCoreTypeSyntax? type = ParseType();
                if (type is null)
                {
                    RecoverUntil(",", ")");
                    if (At(","))
                    {
                        Consume();
                    }

                    continue;
                }

                parameters.Add(NewNode(new SafeCoreParameterSyntax(
                    pattern!,
                    type,
                    SpanFrom(parameterStart, type.Span.End))));
                if (!At(")") && !Expect(","))
                {
                    RecoverUntil(",", ")");
                    if (At(","))
                    {
                        Consume();
                    }
                }
            }

            if (ConsumeExpected(")") is null)
            {
                return null;
            }

            SafeCoreTypeSyntax? returnType = null;
            if (At("->"))
            {
                Consume();
                returnType = ParseType();
                if (returnType is null)
                {
                    return null;
                }
            }

            if (At("where"))
            {
                ReportUnsupported("where clauses are not yet in the bounded safe-core profile");
                RecoverUntil("{", ";");
            }

            SafeCoreBlockSyntax? body = ParseBlock();
            if (body is null)
            {
                if (At(";"))
                {
                    ReportUnsupported("function declarations without a body");
                    Consume();
                }

                return null;
            }

            return NewNode(new SafeCoreFunctionSyntax(
                name,
                generics,
                Array.AsReadOnly(parameters.ToArray()),
                returnType,
                body,
                isPublic,
                attributes,
                SpanFrom(startToken, body.Span.End)));
        }

        private SafeCoreStructSyntax? ParseStruct(
            IReadOnlyList<SafeCoreAttributeSyntax> attributes,
            bool isPublic)
        {
            RustToken? startToken = ConsumeExpected("struct");
            if (startToken is null)
            {
                return null;
            }

            (string? name, _) = ConsumeName("struct name");
            if (name is null)
            {
                return null;
            }

            IReadOnlyList<SafeCoreGenericParameterSyntax> generics = ParseGenericParameters();
            var fields = new List<SafeCoreFieldSyntax>();
            bool tupleStruct;
            RustToken? endToken;
            if (At("{"))
            {
                tupleStruct = false;
                Consume();
                while (!AtEnd && !At("}") && !_terminated)
                {
                    bool fieldPublic = ParseVisibility();
                    int fieldStart = CurrentStart;
                    (string? fieldName, RustToken? fieldToken) = ConsumeName("field name");
                    if (fieldName is null || !Expect(":"))
                    {
                        RecoverUntil(",", "}");
                        if (At(","))
                        {
                            Consume();
                        }

                        continue;
                    }

                    SafeCoreTypeSyntax? fieldType = ParseType();
                    if (fieldType is null)
                    {
                        RecoverUntil(",", "}");
                        if (At(","))
                        {
                            Consume();
                        }

                        continue;
                    }

                    fields.Add(NewNode(new SafeCoreFieldSyntax(
                        fieldName,
                        fieldType,
                        fieldPublic,
                        SpanFrom(fieldStart, fieldType.Span.End))));
                    if (!At("}") && !Expect(","))
                    {
                        RecoverUntil(",", "}");
                        if (At(","))
                        {
                            Consume();
                        }
                    }
                }

                endToken = ConsumeExpected("}");
            }
            else if (At("("))
            {
                tupleStruct = true;
                Consume();
                while (!AtEnd && !At(")") && !_terminated)
                {
                    int fieldStart = CurrentStart;
                    SafeCoreTypeSyntax? fieldType = ParseType();
                    if (fieldType is null)
                    {
                        RecoverUntil(",", ")");
                        if (At(","))
                        {
                            Consume();
                        }

                        continue;
                    }

                    fields.Add(NewNode(new SafeCoreFieldSyntax(
                        null,
                        fieldType,
                        false,
                        SpanFrom(fieldStart, fieldType.Span.End))));
                    if (!At(")") && !Expect(","))
                    {
                        RecoverUntil(",", ")");
                        if (At(","))
                        {
                            Consume();
                        }
                    }
                }

                if (ConsumeExpected(")") is null)
                {
                    return null;
                }

                endToken = ConsumeExpected(";");
            }
            else
            {
                ReportExpected("'{' or '(' after a struct name");
                return null;
            }

            if (endToken is null)
            {
                return null;
            }

            return NewNode(new SafeCoreStructSyntax(
                name,
                generics,
                Array.AsReadOnly(fields.ToArray()),
                tupleStruct,
                isPublic,
                attributes,
                SpanFrom(startToken, endToken)));
        }

        private SafeCoreEnumSyntax? ParseEnum(
            IReadOnlyList<SafeCoreAttributeSyntax> attributes,
            bool isPublic)
        {
            RustToken? startToken = ConsumeExpected("enum");
            if (startToken is null)
            {
                return null;
            }

            (string? name, _) = ConsumeName("enum name");
            if (name is null)
            {
                return null;
            }

            IReadOnlyList<SafeCoreGenericParameterSyntax> generics = ParseGenericParameters();
            if (!Expect("{"))
            {
                return null;
            }

            var variants = new List<SafeCoreEnumVariantSyntax>();
            while (!AtEnd && !At("}") && !_terminated)
            {
                int variantStart = CurrentStart;
                (string? variantName, RustToken? variantToken) = ConsumeName("enum variant name");
                if (variantName is null)
                {
                    RecoverUntil(",", "}");
                    if (At(","))
                    {
                        Consume();
                    }

                    continue;
                }

                var fields = new List<SafeCoreFieldSyntax>();
                if (At("("))
                {
                    Consume();
                    while (!AtEnd && !At(")") && !_terminated)
                    {
                        int fieldStart = CurrentStart;
                        SafeCoreTypeSyntax? type = ParseType();
                        if (type is null)
                        {
                            RecoverUntil(",", ")");
                            if (At(","))
                            {
                                Consume();
                            }

                            continue;
                        }

                        fields.Add(NewNode(new SafeCoreFieldSyntax(null, type, false, SpanFrom(fieldStart, type.Span.End))));
                        if (!At(")") && !Expect(","))
                        {
                            RecoverUntil(",", ")");
                            if (At(","))
                            {
                                Consume();
                            }
                        }
                    }

                    ConsumeExpected(")");
                }
                else if (At("{"))
                {
                    ReportUnsupported("struct-like enum variant fields");
                    SkipBalancedGroup();
                }

                RustToken? variantEnd = fields.Count == 0
                    ? variantToken
                    : TokenAtEnd(fields[^1].Span.End);
                variants.Add(NewNode(new SafeCoreEnumVariantSyntax(
                    variantName,
                    Array.AsReadOnly(fields.ToArray()),
                    SpanFrom(variantStart, variantEnd?.Span.End ?? CurrentStart))));

                if (At("="))
                {
                    ReportUnsupported("explicit enum discriminants");
                    Consume();
                    _ = ParseExpression();
                }

                if (At(","))
                {
                    Consume();
                }
                else if (!At("}"))
                {
                    ReportExpected("',' or '}' after an enum variant");
                    RecoverUntil(",", "}");
                    if (At(","))
                    {
                        Consume();
                    }
                }
            }

            RustToken? endToken = ConsumeExpected("}");
            if (endToken is null)
            {
                ReportUnterminated("enum body");
                return null;
            }

            return NewNode(new SafeCoreEnumSyntax(
                name,
                generics,
                Array.AsReadOnly(variants.ToArray()),
                isPublic,
                attributes,
                SpanFrom(startToken, endToken)));
        }

        private SafeCoreTypeAliasSyntax? ParseTypeAlias(
            IReadOnlyList<SafeCoreAttributeSyntax> attributes,
            bool isPublic)
        {
            RustToken? startToken = ConsumeExpected("type");
            if (startToken is null)
            {
                return null;
            }

            (string? name, _) = ConsumeName("type alias name");
            if (name is null)
            {
                return null;
            }

            IReadOnlyList<SafeCoreGenericParameterSyntax> generics = ParseGenericParameters();
            if (!Expect("="))
            {
                return null;
            }

            SafeCoreTypeSyntax? type = ParseType();
            RustToken? endToken = ConsumeExpected(";");
            if (type is null || endToken is null)
            {
                return null;
            }

            return NewNode(new SafeCoreTypeAliasSyntax(
                name,
                generics,
                type,
                isPublic,
                attributes,
                SpanFrom(startToken, endToken)));
        }

        private SafeCoreConstSyntax? ParseConst(
            IReadOnlyList<SafeCoreAttributeSyntax> attributes,
            bool isPublic)
        {
            RustToken? startToken = ConsumeExpected("const");
            if (startToken is null)
            {
                return null;
            }

            (string? name, _) = ConsumeName("constant name");
            if (name is null || !Expect(":"))
            {
                return null;
            }

            SafeCoreTypeSyntax? type = ParseType();
            if (type is null || !Expect("="))
            {
                return null;
            }

            SafeCoreExpressionSyntax? value = ParseExpression();
            RustToken? endToken = ConsumeExpected(";");
            if (value is null || endToken is null)
            {
                return null;
            }

            return NewNode(new SafeCoreConstSyntax(
                name!,
                type!,
                value,
                isPublic,
                attributes,
                SpanFrom(startToken, endToken)));
        }

        private IReadOnlyList<SafeCoreGenericParameterSyntax> ParseGenericParameters()
        {
            if (!At("<"))
            {
                return Array.Empty<SafeCoreGenericParameterSyntax>();
            }

            Consume();
            var parameters = new List<SafeCoreGenericParameterSyntax>();
            while (!AtEnd && !At(">") && !_terminated)
            {
                int start = CurrentStart;
                if (At("'"))
                {
                    ReportUnsupported("lifetime generic parameters");
                    Consume();
                    continue;
                }

                if (At("const"))
                {
                    ReportUnsupported("const generic parameters");
                    Consume();
                }

                (string? name, _) = ConsumeName("generic parameter");
                if (name is null)
                {
                    RecoverUntil(",", ">");
                    if (At(","))
                    {
                        Consume();
                    }

                    continue;
                }

                var bounds = new List<SafeCoreTypeSyntax>();
                if (At(":"))
                {
                    Consume();
                    while (!AtEnd && !At(",") && !At(">") && !_terminated)
                    {
                        SafeCoreTypeSyntax? bound = ParseType();
                        if (bound is not null)
                        {
                            bounds.Add(bound);
                        }

                        if (!At("+"))
                        {
                            break;
                        }

                        Consume();
                    }
                }

                int end = bounds.Count == 0 ? PreviousEnd : bounds[^1].Span.End;
                parameters.Add(NewNode(new SafeCoreGenericParameterSyntax(
                    name,
                    Array.AsReadOnly(bounds.ToArray()),
                    SpanFrom(start, end))));
                if (At(","))
                {
                    Consume();
                }
                else if (!At(">"))
                {
                    ReportExpected("',' or '>' after a generic parameter");
                    RecoverUntil(",", ">");
                    if (At(","))
                    {
                        Consume();
                    }
                }
            }

            if (ConsumeGreater() is null)
            {
                ReportUnterminated("generic parameter list");
            }

            return Array.AsReadOnly(parameters.ToArray());
        }

        private IReadOnlyList<SafeCoreAttributeSyntax> ParseAttributes()
        {
            if (!At("#"))
            {
                return Array.Empty<SafeCoreAttributeSyntax>();
            }

            var attributes = new List<SafeCoreAttributeSyntax>();
            while (At("#") && !_terminated)
            {
                RustToken? startToken = Consume();
                bool inner = false;
                if (At("!"))
                {
                    inner = true;
                    Consume();
                }

                if (!Expect("["))
                {
                    break;
                }

                int contentStart = CurrentStart;
                int nesting = 1;
                RustToken? endToken = null;
                while (!AtEnd && nesting != 0 && !_terminated)
                {
                    if (At("["))
                    {
                        nesting++;
                    }
                    else if (At("]"))
                    {
                        nesting--;
                        if (nesting == 0)
                        {
                            endToken = Consume();
                            break;
                        }
                    }

                    Consume();
                }

                if (endToken is null)
                {
                    ReportUnterminated("attribute");
                    break;
                }

                int contentEnd = Math.Clamp(endToken.Span.Start, contentStart, _source.Length);
                string content = _source.Substring(contentStart, contentEnd - contentStart).Trim();
                string path = ExtractAttributePath(content);
                string arguments = content.Length <= path.Length
                    ? string.Empty
                    : content[path.Length..].TrimStart();
                attributes.Add(NewNode(new SafeCoreAttributeSyntax(
                    inner,
                    path,
                    arguments,
                    SpanFrom(startToken!, endToken))));
            }

            return Array.AsReadOnly(attributes.ToArray());
        }

        private bool ParseVisibility()
        {
            if (!At("pub"))
            {
                return false;
            }

            Consume();
            if (At("("))
            {
                ReportUnsupported("restricted visibility modifiers");
                SkipBalancedGroup();
                return false;
            }

            return true;
        }

        private SafeCoreBlockSyntax? ParseBlock()
        {
            if (!EnterDepth())
            {
                return null;
            }

            try
            {
                RustToken? startToken = ConsumeExpected("{");
                if (startToken is null)
                {
                    return null;
                }

                var statements = new List<SafeCoreStatementSyntax>();
                SafeCoreExpressionSyntax? tail = null;
                while (!AtEnd && !At("}") && !_terminated)
                {
                    int before = _index;
                    if (At("let"))
                    {
                        SafeCoreLetStatementSyntax? let = ParseLetStatement();
                        if (let is not null)
                        {
                            statements.Add(let);
                        }
                    }
                    else if (At("return"))
                    {
                        SafeCoreReturnStatementSyntax? result = ParseReturnStatement();
                        if (result is not null)
                        {
                            statements.Add(result);
                        }
                    }
                    else
                    {
                        int expressionStart = CurrentStart;
                        SafeCoreExpressionSyntax? expression = ParseExpression();
                        if (expression is null)
                        {
                            RecoverUntil(";", "}");
                            if (At(";"))
                            {
                                Consume();
                            }
                        }
                        else if (At(";"))
                        {
                            RustToken semicolonToken = Consume()!;
                            statements.Add(NewNode(new SafeCoreExpressionStatementSyntax(
                                expression,
                                true,
                                SpanFrom(expressionStart, semicolonToken.Span.End))));
                        }
                        else if (At("}"))
                        {
                            tail = expression;
                        }
                        else
                        {
                            ReportExpected("';' or '}' after an expression");
                            RecoverUntil(";", "}");
                            if (At(";"))
                            {
                                Consume();
                            }
                        }
                    }

                    if (_index == before && !AtEnd)
                    {
                        Consume();
                    }
                }

                RustToken? endToken = ConsumeExpected("}");
                if (endToken is null)
                {
                    ReportUnterminated("block");
                    return null;
                }

                return NewNode(new SafeCoreBlockSyntax(
                    Array.AsReadOnly(statements.ToArray()),
                    tail,
                    SpanFrom(startToken, endToken)));
            }
            finally
            {
                ExitDepth();
            }
        }

        private SafeCoreLetStatementSyntax? ParseLetStatement()
        {
            RustToken? startToken = ConsumeExpected("let");
            if (startToken is null)
            {
                return null;
            }

            SafeCorePatternSyntax? pattern = ParsePattern();
            SafeCoreTypeSyntax? type = null;
            if (At(":"))
            {
                Consume();
                type = ParseType();
            }

            SafeCoreExpressionSyntax? initializer = null;
            if (At("="))
            {
                Consume();
                initializer = ParseExpression();
            }

            RustToken? endToken = ConsumeExpected(";");
            if (pattern is null || endToken is null)
            {
                return null;
            }

            return NewNode(new SafeCoreLetStatementSyntax(
                pattern,
                type,
                initializer,
                SpanFrom(startToken, endToken)));
        }

        private SafeCoreReturnStatementSyntax? ParseReturnStatement()
        {
            RustToken? startToken = ConsumeExpected("return");
            if (startToken is null)
            {
                return null;
            }

            SafeCoreExpressionSyntax? value = At(";") ? null : ParseExpression();
            RustToken? endToken = ConsumeExpected(";");
            if (endToken is null)
            {
                return null;
            }

            return NewNode(new SafeCoreReturnStatementSyntax(value, SpanFrom(startToken, endToken)));
        }

        private SafeCoreExpressionSyntax? ParseExpression(int minimumPrecedence = 0)
        {
            if (!EnterDepth())
            {
                return null;
            }

            try
            {
                SafeCoreExpressionSyntax? left = ParsePrefixExpression();
                if (left is null)
                {
                    return null;
                }

                while (!AtEnd && !_terminated)
                {
                    if (At("("))
                    {
                        SafeCoreCallExpressionSyntax? call = ParseCallExpression(left);
                        if (call is null)
                        {
                            return null;
                        }

                        left = call;
                        continue;
                    }

                    if (At("["))
                    {
                        Consume();
                        SafeCoreExpressionSyntax? index = ParseExpression();
                        RustToken? endToken = ConsumeExpected("]");
                        if (index is null || endToken is null)
                        {
                            return null;
                        }

                        left = NewNode(new SafeCoreIndexExpressionSyntax(
                            left,
                            index,
                            SpanFrom(left.Span.Start, endToken.Span.End)));
                        continue;
                    }

                    string? op = CurrentText;
                    int precedence = GetBinaryPrecedence(op);
                    if (precedence < minimumPrecedence)
                    {
                        break;
                    }

                    Consume();
                    int rightMinimumPrecedence = IsAssignmentOperator(op!)
                        ? precedence
                        : precedence + 1;
                    SafeCoreExpressionSyntax? right = ParseExpression(rightMinimumPrecedence);
                    if (right is null)
                    {
                        return null;
                    }

                    SafeCoreExpressionSyntax leftExpression = left;
                    left = NewNode(new SafeCoreBinaryExpressionSyntax(
                        op!,
                        leftExpression,
                        right,
                        SpanFrom(leftExpression.Span.Start, right.Span.End)));
                }

                return left;
            }
            finally
            {
                ExitDepth();
            }
        }

        private SafeCoreExpressionSyntax? ParsePrefixExpression()
        {
            if (At("if"))
            {
                return ParseIfExpression();
            }

            if (At("{"))
            {
                SafeCoreBlockSyntax? block = ParseBlock();
                return block is null
                    ? null
                    : NewNode(new SafeCoreBlockExpressionSyntax(block, block.Span));
            }

            if (At("(") )
            {
                return ParseTupleExpression();
            }

            if (At("["))
            {
                return ParseArrayExpression();
            }

            if (Current is { } token && IsUnaryOperator(token.Text))
            {
                RustToken start = Consume()!;
                string op = start.Text;
                if (op == "&" && At("mut"))
                {
                    Consume();
                    op = "&mut";
                }

                SafeCoreExpressionSyntax? operand = ParseExpression(12);
                if (operand is null)
                {
                    return null;
                }

                return NewNode(new SafeCoreUnaryExpressionSyntax(
                    op,
                    operand,
                    SpanFrom(start, operand.Span.End)));
            }

            if (Current is { } literal && IsLiteral(literal))
            {
                ValidateLiteralSuffix(literal);
                Consume();
                return NewNode(new SafeCoreLiteralExpressionSyntax(
                    literal.Kind,
                    literal.Text,
                    literal.Span));
            }

            if (Current is { } nameToken && IsNameToken(nameToken))
            {
                return ParseNameExpression();
            }

            if (Current is { } unsupported)
            {
                if (unsupported.Text is "match" or "loop" or "while" or "for" or "break" or "continue" or
                    "move" or "async" or "|" or "unsafe")
                {
                    ReportUnsupported($"expression '{unsupported.Text}'");
                }
                else
                {
                    ReportUnexpected($"Unexpected token '{unsupported.Text}' in an expression.");
                }

                Consume();
            }

            return null;
        }

        private SafeCoreIfExpressionSyntax? ParseIfExpression()
        {
            RustToken? startToken = ConsumeExpected("if");
            if (startToken is null)
            {
                return null;
            }

            SafeCoreExpressionSyntax? condition = ParseExpression();
            SafeCoreBlockSyntax? then = ParseBlock();
            if (condition is null || then is null)
            {
                return null;
            }

            SafeCoreExpressionSyntax? @else = null;
            if (At("else"))
            {
                Consume();
                @else = At("if")
                    ? ParseIfExpression()
                    : At("{")
                        ? ParsePrefixExpression()
                        : null;
                if (@else is null)
                {
                    ReportExpected("'if' or '{' after 'else'");
                }
            }

            int end = @else?.Span.End ?? then.Span.End;
            return NewNode(new SafeCoreIfExpressionSyntax(
                condition,
                then,
                @else,
                SpanFrom(startToken, end)));
        }

        private SafeCoreNameExpressionSyntax? ParseNameExpression()
        {
            int start = CurrentStart;
            var segments = new List<string>();
            while (!AtEnd && IsNameToken(Current!))
            {
                segments.Add(Consume()!.Text);
                if (!At("::"))
                {
                    break;
                }

                Consume();
                if (!IsNameToken(Current))
                {
                    ReportExpected("a path segment after '::'");
                    return null;
                }
            }

            return NewNode(new SafeCoreNameExpressionSyntax(
                string.Join("::", segments),
                SpanFrom(start, PreviousEnd)));
        }

        private SafeCoreCallExpressionSyntax? ParseCallExpression(SafeCoreExpressionSyntax callee)
        {
            ConsumeExpected("(");
            var arguments = new List<SafeCoreExpressionSyntax>();
            while (!AtEnd && !At(")") && !_terminated)
            {
                SafeCoreExpressionSyntax? argument = ParseExpression();
                if (argument is not null)
                {
                    arguments.Add(argument);
                }

                if (At(","))
                {
                    Consume();
                }
                else if (!At(")"))
                {
                    ReportExpected("',' or ')' after a call argument");
                    RecoverUntil(",", ")");
                    if (At(","))
                    {
                        Consume();
                    }
                }
            }

            RustToken? endToken = ConsumeExpected(")");
            if (endToken is null)
            {
                return null;
            }

            return NewNode(new SafeCoreCallExpressionSyntax(
                callee,
                Array.AsReadOnly(arguments.ToArray()),
                SpanFrom(callee.Span.Start, endToken.Span.End)));
        }

        private SafeCoreTupleExpressionSyntax? ParseTupleExpression()
        {
            RustToken? startToken = ConsumeExpected("(");
            if (startToken is null)
            {
                return null;
            }

            var elements = new List<SafeCoreExpressionSyntax>();
            bool trailingComma = false;
            while (!AtEnd && !At(")") && !_terminated)
            {
                SafeCoreExpressionSyntax? element = ParseExpression();
                if (element is not null)
                {
                    elements.Add(element);
                }

                if (!At(","))
                {
                    break;
                }

                trailingComma = true;
                Consume();
                if (!At(")"))
                {
                    trailingComma = false;
                }
            }

            RustToken? endToken = ConsumeExpected(")");
            if (endToken is null)
            {
                return null;
            }

            return NewNode(new SafeCoreTupleExpressionSyntax(
                Array.AsReadOnly(elements.ToArray()),
                trailingComma,
                SpanFrom(startToken, endToken)));
        }

        private SafeCoreArrayExpressionSyntax? ParseArrayExpression()
        {
            RustToken? startToken = ConsumeExpected("[");
            if (startToken is null)
            {
                return null;
            }

            var elements = new List<SafeCoreExpressionSyntax>();
            SafeCoreExpressionSyntax? repeatCount = null;
            while (!AtEnd && !At("]") && !_terminated)
            {
                SafeCoreExpressionSyntax? element = ParseExpression();
                if (element is not null)
                {
                    elements.Add(element);
                }

                if (At(";"))
                {
                    Consume();
                    repeatCount = ParseExpression();
                    break;
                }

                if (At(","))
                {
                    Consume();
                    continue;
                }

                break;
            }

            RustToken? endToken = ConsumeExpected("]");
            if (endToken is null)
            {
                return null;
            }

            return NewNode(new SafeCoreArrayExpressionSyntax(
                Array.AsReadOnly(elements.ToArray()),
                repeatCount,
                SpanFrom(startToken, endToken)));
        }

        private SafeCorePatternSyntax? ParsePattern()
        {
            if (!EnterDepth())
            {
                return null;
            }

            try
            {
                if (At("mut"))
                {
                    RustToken start = Consume()!;
                    (string? name, RustToken? nameToken) = ConsumeName("mutable binding name");
                    if (name is null || nameToken is null)
                    {
                        return null;
                    }

                    return NewNode(new SafeCoreIdentifierPatternSyntax(
                        name,
                        true,
                        SpanFrom(start, nameToken)));
                }

                if (At("_"))
                {
                    RustToken token = Consume()!;
                    return NewNode(new SafeCoreWildcardPatternSyntax(token.Span));
                }

                if (At("("))
                {
                    RustToken start = Consume()!;
                    var elements = new List<SafeCorePatternSyntax>();
                    bool trailingComma = false;
                    while (!AtEnd && !At(")") && !_terminated)
                    {
                        SafeCorePatternSyntax? element = ParsePattern();
                        if (element is not null)
                        {
                            elements.Add(element);
                        }

                        if (!At(","))
                        {
                            break;
                        }

                        trailingComma = true;
                        Consume();
                        if (!At(")"))
                        {
                            trailingComma = false;
                        }
                    }

                    RustToken? end = ConsumeExpected(")");
                    return end is null
                        ? null
                        : NewNode(new SafeCoreTuplePatternSyntax(
                            Array.AsReadOnly(elements.ToArray()),
                            trailingComma,
                            SpanFrom(start, end)));
                }

                if (Current is { } literal && IsLiteral(literal))
                {
                    ValidateLiteralSuffix(literal);
                    Consume();
                    return NewNode(new SafeCoreLiteralPatternSyntax(literal.Kind, literal.Text, literal.Span));
                }

                if (!IsNameToken(Current))
                {
                    ReportExpected("a safe-core pattern");
                    return null;
                }

                int startPosition = CurrentStart;
                var segments = new List<string>();
                RustToken? lastToken = null;
                while (IsNameToken(Current))
                {
                    lastToken = Consume();
                    segments.Add(lastToken!.Text);
                    if (!At("::"))
                    {
                        break;
                    }

                    Consume();
                    if (!IsNameToken(Current))
                    {
                        ReportExpected("a path segment after '::'");
                        return null;
                    }
                }

                if (At("("))
                {
                    Consume();
                    var arguments = new List<SafeCorePatternSyntax>();
                    while (!AtEnd && !At(")") && !_terminated)
                    {
                        SafeCorePatternSyntax? argument = ParsePattern();
                        if (argument is not null)
                        {
                            arguments.Add(argument);
                        }

                        if (At(","))
                        {
                            Consume();
                        }
                        else if (!At(")"))
                        {
                            ReportExpected("',' or ')' in a pattern");
                            RecoverUntil(",", ")");
                            if (At(","))
                            {
                                Consume();
                            }
                        }
                    }

                    RustToken? end = ConsumeExpected(")");
                    if (end is null)
                    {
                        return null;
                    }

                    return NewNode(new SafeCorePathPatternSyntax(
                        string.Join("::", segments),
                        Array.AsReadOnly(arguments.ToArray()),
                        SpanFrom(startPosition, end.Span.End)));
                }

                if (segments.Count == 1)
                {
                    return NewNode(new SafeCoreIdentifierPatternSyntax(
                        segments[0],
                        false,
                        SpanFrom(startPosition, lastToken!.Span.End)));
                }

                return NewNode(new SafeCorePathPatternSyntax(
                    string.Join("::", segments),
                    Array.Empty<SafeCorePatternSyntax>(),
                    SpanFrom(startPosition, lastToken!.Span.End)));
            }
            finally
            {
                ExitDepth();
            }
        }

        private SafeCoreTypeSyntax? ParseType()
        {
            if (!EnterDepth())
            {
                return null;
            }

            try
            {
                if (At("&"))
                {
                    RustToken start = Consume()!;
                    string? lifetime = null;
                    if (Current is { Kind: RustTokenKind.Lifetime or RustTokenKind.RawLifetime } lifetimeToken)
                    {
                        lifetime = lifetimeToken.Text;
                        Consume();
                    }

                    bool mutable = false;
                    if (At("mut"))
                    {
                        mutable = true;
                        Consume();
                    }

                    SafeCoreTypeSyntax? inner = ParseType();
                    return inner is null
                        ? null
                        : NewNode(new SafeCoreReferenceTypeSyntax(
                            lifetime,
                            mutable,
                            inner,
                            SpanFrom(start, inner.Span.End)));
                }

                if (At("!"))
                {
                    RustToken token = Consume()!;
                    return NewNode(new SafeCoreNeverTypeSyntax(token.Span));
                }

                if (At("("))
                {
                    RustToken start = Consume()!;
                    var elements = new List<SafeCoreTypeSyntax>();
                    bool trailingComma = false;
                    while (!AtEnd && !At(")") && !_terminated)
                    {
                        SafeCoreTypeSyntax? element = ParseType();
                        if (element is not null)
                        {
                            elements.Add(element);
                        }

                        if (!At(","))
                        {
                            break;
                        }

                        trailingComma = true;
                        Consume();
                        if (!At(")"))
                        {
                            trailingComma = false;
                        }
                    }

                    RustToken? end = ConsumeExpected(")");
                    if (end is null)
                    {
                        return null;
                    }

                    return elements.Count == 0
                        ? NewNode(new SafeCoreUnitTypeSyntax(SpanFrom(start, end)))
                        : NewNode(new SafeCoreTupleTypeSyntax(
                            Array.AsReadOnly(elements.ToArray()),
                            trailingComma,
                            SpanFrom(start, end)));
                }

                if (At("["))
                {
                    RustToken start = Consume()!;
                    SafeCoreTypeSyntax? element = ParseType();
                    if (element is null)
                    {
                        return null;
                    }

                    if (At(";"))
                    {
                        Consume();
                        SafeCoreExpressionSyntax? length = ParseExpression();
                        RustToken? end = ConsumeExpected("]");
                        return length is null || end is null
                            ? null
                            : NewNode(new SafeCoreArrayTypeSyntax(
                                element,
                                length,
                                SpanFrom(start, end)));
                    }

                    RustToken? sliceEnd = ConsumeExpected("]");
                    return sliceEnd is null
                        ? null
                        : NewNode(new SafeCoreSliceTypeSyntax(element, SpanFrom(start, sliceEnd)));
                }

                if (CurrentText is "dyn" or "impl" or "fn" or "unsafe")
                {
                    ReportUnsupported($"type form '{CurrentText}'");
                    Consume();
                    return null;
                }

                if (!IsNameToken(Current))
                {
                    ReportExpected("a safe-core type");
                    return null;
                }

                int startPosition = CurrentStart;
                var segments = new List<SafeCorePathSegmentSyntax>();
                while (IsNameToken(Current))
                {
                    int segmentStart = CurrentStart;
                    (string? name, RustToken? nameToken) = ConsumePathName("type path segment");
                    if (name is null || nameToken is null)
                    {
                        return null;
                    }

                    var arguments = new List<SafeCoreTypeSyntax>();
                    if (At("<"))
                    {
                        Consume();
                        while (!AtEnd && !At(">") && !_terminated)
                        {
                            SafeCoreTypeSyntax? argument = ParseType();
                            if (argument is not null)
                            {
                                arguments.Add(argument);
                            }

                            if (At(","))
                            {
                                Consume();
                            }
                            else if (!At(">"))
                            {
                                ReportExpected("',' or '>' in generic type arguments");
                                RecoverUntil(",", ">");
                                if (At(","))
                                {
                                    Consume();
                                }
                            }
                        }

                        if (ConsumeGreater() is null)
                        {
                            ReportUnterminated("generic type arguments");
                            return null;
                        }
                    }

                    int segmentEnd = arguments.Count == 0 ? nameToken.Span.End : arguments[^1].Span.End;
                    segments.Add(NewNode(new SafeCorePathSegmentSyntax(
                        name,
                        Array.AsReadOnly(arguments.ToArray()),
                        SpanFrom(segmentStart, segmentEnd))));
                    if (!At("::"))
                    {
                        break;
                    }

                    Consume();
                    if (!IsNameToken(Current))
                    {
                        ReportExpected("a type path segment after '::'");
                        return null;
                    }
                }

                return NewNode(new SafeCorePathTypeSyntax(
                    Array.AsReadOnly(segments.ToArray()),
                    SpanFrom(startPosition, PreviousEnd)));
            }
            finally
            {
                ExitDepth();
            }
        }

        private void RecoverItem()
        {
            int braceDepth = 0;
            while (!AtEnd && !_terminated)
            {
                if (At("{"))
                {
                    braceDepth++;
                }
                else if (At("}"))
                {
                    if (braceDepth == 0)
                    {
                        return;
                    }

                    braceDepth--;
                }

                RustToken? token = Consume();
                if (token?.Text == ";" && braceDepth == 0)
                {
                    return;
                }
            }
        }

        private void RecoverUntil(params string[] stopTokens)
        {
            var stops = new HashSet<string>(stopTokens, StringComparer.Ordinal);
            int localDepth = 0;
            while (!AtEnd && !_terminated)
            {
                if (localDepth == 0 && CurrentText is not null && stops.Contains(CurrentText))
                {
                    return;
                }

                if (At("(") || At("[") || At("{"))
                {
                    localDepth++;
                }
                else if (At(")") || At("]") || At("}"))
                {
                    if (localDepth == 0)
                    {
                        return;
                    }

                    localDepth--;
                }

                Consume();
            }
        }

        private void SkipBalancedGroup()
        {
            if (!(At("(") || At("[") || At("{")))
            {
                return;
            }

            string opening = CurrentText!;
            string closing = opening switch
            {
                "(" => ")",
                "[" => "]",
                _ => "}",
            };
            int depth = 0;
            while (!AtEnd && !_terminated)
            {
                if (At(opening))
                {
                    depth++;
                }
                else if (At(closing))
                {
                    depth--;
                }

                Consume();
                if (depth == 0)
                {
                    return;
                }
            }

            ReportUnterminated("delimited construct");
        }

        private RustToken? ConsumeExpected(string text)
        {
            if (At(text))
            {
                return Consume();
            }

            ReportExpected($"'{text}'");
            return null;
        }

        private bool Expect(string text) => ConsumeExpected(text) is not null;

        private RustToken? ConsumeGreater()
        {
            if (At(">"))
            {
                return Consume();
            }

            if (_pendingGreaterClosers > 0)
            {
                return Consume();
            }

            if (CurrentText == ">>")
            {
                RustToken token = Consume()!;
                _pendingGreaterClosers = 1;
                _pendingGreaterToken = token;
                return token;
            }

            ReportExpected("'>'");
            return null;
        }

        private (string? Name, RustToken? Token) ConsumeName(string description)
        {
            if (!IsIdentifierToken(Current))
            {
                ReportExpected(description);
                return (null, null);
            }

            RustToken token = Consume()!;
            return (token.Text, token);
        }

        private (string? Name, RustToken? Token) ConsumePathName(string description)
        {
            if (!IsNameToken(Current))
            {
                ReportExpected(description);
                return (null, null);
            }

            RustToken token = Consume()!;
            return (token.Text, token);
        }

        private RustToken? Consume()
        {
            if (!Step())
            {
                return null;
            }

            if (_pendingGreaterClosers > 0 && _pendingGreaterToken is not null)
            {
                RustToken sourceToken = _pendingGreaterToken;
                int offset = sourceToken.Span.Start + sourceToken.Span.Length - _pendingGreaterClosers;
                _pendingGreaterClosers--;
                if (_pendingGreaterClosers == 0)
                {
                    _pendingGreaterToken = null;
                }

                return new RustToken(
                    RustTokenKind.Punctuation,
                    new TextSpan(Math.Max(0, offset), 1),
                    ">",
                    false,
                    null,
                    Array.Empty<RustTrivia>());
            }

            if (_index >= _tokens.Count)
            {
                return null;
            }

            return _tokens[_index++];
        }

        private bool Step()
        {
            if (_terminated)
            {
                return false;
            }

            _operationCount++;
            if (_operationCount <= _options.MaximumOperations)
            {
                return true;
            }

            AddLimitDiagnostic(CurrentStart);
            return false;
        }

        private bool EnterDepth()
        {
            if (_depth >= _options.MaximumNestingDepth)
            {
                AddLimitDiagnostic(CurrentStart);
                return false;
            }

            _depth++;
            return true;
        }

        private void ExitDepth()
        {
            if (_depth > 0)
            {
                _depth--;
            }
        }

        private bool At(string text) => string.Equals(CurrentText, text, StringComparison.Ordinal);

        private bool AtEnd => _index >= _tokens.Count && _pendingGreaterClosers == 0;

        private string? CurrentText => _pendingGreaterClosers > 0 ? ">" : Current?.Text;

        private RustToken? Current => _index < _tokens.Count ? _tokens[_index] : null;

        private int CurrentStart => _pendingGreaterClosers > 0 && _pendingGreaterToken is not null
            ? _pendingGreaterToken.Span.End - _pendingGreaterClosers
            : Current?.Span.Start ?? _source.Length;

        private int PreviousEnd => _index > 0 ? _tokens[_index - 1].Span.End : 0;

        private static bool IsNameToken(RustToken? token) =>
            IsIdentifierToken(token) ||
            token is { Kind: RustTokenKind.Keyword, Text: "self" or "Self" or "crate" or "super" };

        private static bool IsIdentifierToken(RustToken? token) =>
            token is { Kind: RustTokenKind.Identifier or RustTokenKind.RawIdentifier };

        private static bool IsName(string? text) => text is not null &&
            (text.Length != 0 && (char.IsLetter(text[0]) || text[0] == '_' || text[0] == 'r' ||
                text is "self" or "Self" or "crate" or "super"));

        private static bool IsLiteral(RustToken token) => token.Kind is
            RustTokenKind.IntegerLiteral or RustTokenKind.FloatLiteral or RustTokenKind.StringLiteral or
            RustTokenKind.RawStringLiteral or RustTokenKind.ByteStringLiteral or RustTokenKind.RawByteStringLiteral or
            RustTokenKind.CStringLiteral or RustTokenKind.RawCStringLiteral or RustTokenKind.CharacterLiteral or
            RustTokenKind.ByteCharacterLiteral || token.Text is "true" or "false";

        private void ValidateLiteralSuffix(RustToken literal)
        {
            if (literal.LiteralSuffix is not { } suffix)
            {
                return;
            }

            bool valid = literal.Kind switch
            {
                RustTokenKind.IntegerLiteral =>
                    IsIntegerLiteralSuffix(suffix) ||
                    (IsFloatLiteralSuffix(suffix) && IsDecimalIntegerLiteral(literal)),
                RustTokenKind.FloatLiteral => IsFloatLiteralSuffix(suffix),
                _ => false,
            };
            if (valid)
            {
                return;
            }

            TextSpan suffixSpan = literal.LiteralSuffixSpan!.Value;
            AddDiagnostic(
                SafeCoreSyntaxDiagnosticCodes.InvalidLiteralSuffix,
                $"Literal suffix '{suffix}' is not valid for this literal.",
                suffixSpan.Start,
                suffixSpan.Length);
        }

        private static bool IsDecimalIntegerLiteral(RustToken literal)
        {
            int primaryLength = literal.Text.Length - literal.LiteralSuffix!.Length;
            ReadOnlySpan<char> primary = literal.Text.AsSpan(0, primaryLength);
            return !primary.StartsWith("0b", StringComparison.Ordinal) &&
                !primary.StartsWith("0o", StringComparison.Ordinal) &&
                !primary.StartsWith("0x", StringComparison.Ordinal);
        }

        private static bool IsIntegerLiteralSuffix(string suffix) => suffix is
            "u8" or "u16" or "u32" or "u64" or "u128" or "usize" or
            "i8" or "i16" or "i32" or "i64" or "i128" or "isize";

        private static bool IsFloatLiteralSuffix(string suffix) => suffix is "f32" or "f64";

        private static bool IsUnaryOperator(string text) => text is "!" or "-" or "+" or "&" or "*";

        private static int GetBinaryPrecedence(string? text) => text switch
        {
            "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "^=" or "&=" or "|=" or
                "<<=" or ">>=" => 1,
            "||" => 2,
            "&&" => 3,
            "|" => 4,
            "^" => 5,
            "&" => 6,
            "==" or "!=" => 7,
            "<" or ">" or "<=" or ">=" => 8,
            "<<" or ">>" => 9,
            "+" or "-" => 10,
            "*" or "/" or "%" => 11,
            _ => -1,
        };

        private static bool IsAssignmentOperator(string text) => text is
            "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "^=" or "&=" or "|=" or
            "<<=" or ">>=";

        private static string ExtractAttributePath(string content)
        {
            int cut = content.Length;
            foreach (char marker in new[] { '(', '=', ',', ' ' })
            {
                int position = content.IndexOf(marker);
                if (position >= 0)
                {
                    cut = Math.Min(cut, position);
                }
            }

            return content[..cut].Trim();
        }

        private SafeCoreCompilationUnitSyntax NewNode(SafeCoreCompilationUnitSyntax node) =>
            RegisterNode() ? node : node;

        private T NewNode<T>(T node)
            where T : notnull
        {
            RegisterNode();
            return node;
        }

        private bool RegisterNode()
        {
            if (_terminated)
            {
                return false;
            }

            _nodeCount++;
            if (_nodeCount <= _options.MaximumNodes)
            {
                return true;
            }

            AddLimitDiagnostic(CurrentStart);
            return false;
        }

        private void AddLimitDiagnostic(int start)
        {
            if (_limitReported)
            {
                _terminated = true;
                return;
            }

            _limitReported = true;
            _terminated = true;
            AddDiagnosticCore(
                SafeCoreSyntaxDiagnosticCodes.LimitReached,
                "Safe-core parsing stopped after reaching a configured safety limit.",
                start,
                0);
        }

        private void ReportExpected(string expected) =>
            AddDiagnostic(SafeCoreSyntaxDiagnosticCodes.ExpectedToken, $"Expected {expected}.", CurrentStart, CurrentLength);

        private void ReportUnexpected(string message) =>
            AddDiagnostic(SafeCoreSyntaxDiagnosticCodes.UnexpectedToken, message, CurrentStart, CurrentLength);

        private void ReportUnsupported(string construct) =>
            AddDiagnostic(
                SafeCoreSyntaxDiagnosticCodes.UnsupportedSyntax,
                $"Unsupported safe-core syntax: {construct}.",
                CurrentStart,
                CurrentLength);

        private void ReportUnterminated(string construct) =>
            AddDiagnostic(
                SafeCoreSyntaxDiagnosticCodes.UnterminatedConstruct,
                $"The {construct} is not terminated.",
                CurrentStart,
                Math.Max(0, _source.Length - CurrentStart));

        private void AddDiagnostic(string code, string message, int start, int length)
        {
            if (_terminated && !_limitReported)
            {
                return;
            }

            if (_diagnostics.Count >= _options.MaximumDiagnostics - 1)
            {
                AddLimitDiagnostic(start);
                return;
            }

            AddDiagnosticCore(code, message, start, length);
        }

        private void AddDiagnosticCore(string code, string message, int start, int length)
        {
            int safeStart = Math.Clamp(start, 0, _source.Length);
            int safeLength = Math.Clamp(length, 0, _source.Length - safeStart);
            if (_diagnostics.Count < _options.MaximumDiagnostics)
            {
                _diagnostics.Add(new Diagnostic(code, message, new TextSpan(safeStart, safeLength)));
            }
        }

        private int CurrentLength => _pendingGreaterClosers > 0 ? 1 : Current?.Span.Length ?? 0;

        private static RustToken TokenAtEnd(int end) => new(
            RustTokenKind.Punctuation,
            new TextSpan(Math.Max(0, end - 1), 1),
            string.Empty,
            false,
            null,
            Array.Empty<RustTrivia>());

        private TextSpan SpanFrom(RustToken start, RustToken end) =>
            SpanFrom(start.Span.Start, end.Span.End);

        private TextSpan SpanFrom(RustToken start, int end) =>
            SpanFrom(start.Span.Start, end);

        private TextSpan SpanFrom(int start, int end) =>
            new(Math.Clamp(start, 0, _source.Length), Math.Clamp(end - start, 0, _source.Length - Math.Clamp(start, 0, _source.Length)));
    }
}
