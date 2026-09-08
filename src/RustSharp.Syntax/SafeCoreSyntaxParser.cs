using System.Collections.ObjectModel;
using System.Diagnostics;

namespace RustSharp.Syntax;

/// <summary>
/// Parses the bounded safe-core syntax profile on top of the lossless Rust
/// lexer. This parser is intentionally separate from the first vertical-slice
/// parser used by the IL emitter.
/// </summary>
public static partial class SafeCoreSyntax
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
        SafeCoreSyntaxOptions? options = null) => Parse(source, sourcePath, options, CancellationToken.None);

    /// <summary>Parses with cooperative cancellation and a shared lexer/parser deadline. Cancellation and timeout throw.</summary>
    public static SafeCoreSyntaxResult Parse(
        string? source,
        string? sourcePath,
        SafeCoreSyntaxOptions? options,
        CancellationToken cancellationToken)
    {
        source ??= string.Empty;
        sourcePath ??= string.Empty;
        SafeCoreSyntaxOptions normalized = NormalizeOptions(options);
        long startedAt = Stopwatch.GetTimestamp();
        var lexOptions = new RustLexerOptions
        {
            Timeout = normalized.Timeout,
            MaximumSourceLength = normalized.MaximumSourceLength,
            MaximumTokens = normalized.MaximumTokens,
            MaximumTrivia = Math.Min(normalized.MaximumTokens * 2, 1_000_000),
            MaximumDiagnostics = normalized.MaximumDiagnostics,
            MaximumDelimiterDepth = normalized.MaximumNestingDepth,
        };
        RustLexResult lexResult = RustLexer.Lex(source, sourcePath, lexOptions, cancellationToken);
        var parser = new Parser(source, sourcePath, lexResult, normalized, startedAt, cancellationToken);
        return parser.Run();
    }

    private static SafeCoreSyntaxOptions NormalizeOptions(SafeCoreSyntaxOptions? options)
    {
        options ??= new SafeCoreSyntaxOptions();
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Parser timeout must be positive and at most one minute.");
        }

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

    private sealed partial class Parser
    {
        private static readonly HashSet<string> UnsupportedItemKeywords =
        [
            "unsafe", "extern", "union", "macro_rules", "macro", "async", "static",
        ];

        private readonly string _source;
        private readonly string _sourcePath;
        private readonly RustLexResult _lexResult;
        private readonly SafeCoreSyntaxOptions _options;
        private readonly IReadOnlyList<RustToken> _tokens;
        private readonly List<Diagnostic> _diagnostics;
        private readonly CancellationToken _cancellationToken;
        private readonly long _startedAt;
        private int _index;
        private int _nodeCount;
        private int _operationCount;
        private int _depth;
        private RustToken? _pendingToken;
        private int _previousEnd;
        private bool _terminated;
        private bool _limitReported;
        private readonly HashSet<int> _consumedDocumentation = [];

        internal Parser(
            string source,
            string sourcePath,
            RustLexResult lexResult,
            SafeCoreSyntaxOptions options,
            long startedAt,
            CancellationToken cancellationToken)
        {
            _source = source;
            _sourcePath = sourcePath;
            _lexResult = lexResult;
            _options = options;
            _cancellationToken = cancellationToken;
            _startedAt = startedAt;
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
            CheckDeadline();
            SafeCoreCompilationUnitSyntax? root = null;
            if (_lexResult.IsTruncated)
            {
                AddDiagnosticCore(
                    SafeCoreSyntaxDiagnosticCodes.LexicalTruncation,
                    "The lexical pass was truncated before safe-core parsing could finish.",
                    _source.Length,
                    0);
            }

            if (_lexResult.Diagnostics.Count == 0 && !_lexResult.IsTruncated)
            {
                try
                {
                    root = ParseCompilationUnit();
                    foreach (RustTrivia trivia in _lexResult.Trivia)
                    {
                        Step();
                        if (trivia.IsDocumentation && !_consumedDocumentation.Contains(trivia.Span.Start))
                        {
                            AddDiagnostic(SafeCoreSyntaxDiagnosticCodes.UnexpectedToken,
                                "A documentation comment must annotate an item or an enclosing module/block.",
                                trivia.Span.Start, trivia.Span.Length);
                        }
                    }
                }
                catch (ParseLimitException)
                {
                    root = null;
                }
            }

            CheckDeadline();

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
                int start = 0;
                IReadOnlyList<SafeCoreAttributeSyntax> leadingAttributes = ParseAttributes(allowInner: true);
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

                if ((items.Count == 0 && pendingItemAttributes.Length != 0) || ParseAttributes().Count != 0)
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
                SafeCoreVisibilitySyntax visibility = ParseVisibilitySyntax();
                bool isPublic = visibility.Kind == SafeCoreVisibilityKind.Public;
                if (AtEnd)
                {
                    if (attributes.Count != 0 || visibility.Kind != SafeCoreVisibilityKind.Private)
                    {
                        ReportExpected("an item after the attribute or visibility modifier");
                    }

                    return null;
                }

                SafeCoreItemSyntax? item = CurrentText switch
                {
                    "mod" => ParseModule(attributes, isPublic),
                    "use" => ParseUse(attributes, isPublic),
                    "fn" => ParseFunction(attributes, isPublic),
                    "struct" => ParseStruct(attributes, isPublic),
                    "enum" => ParseEnum(attributes, isPublic),
                    "type" => ParseTypeAlias(attributes, isPublic),
                    "const" when PeekItemText(1) == "fn" => ParseFunction(attributes, isPublic),
                    "const" => ParseConst(attributes, isPublic),
                    "trait" => ParseTrait(attributes, isPublic),
                    "impl" => ParseImpl(attributes, isPublic),
                    _ => RejectUnsupportedItem(),
                };
                return item is null ? null : item with { Visibility = visibility };
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
            RustToken start = ConsumeExpected("mod")!;
            (string? name, _) = ConsumeName("module name");
            if (name is null) return null;
            if (At(";"))
            {
                RustToken end = Consume()!;
                return NewNode(new SafeCoreModuleSyntax(name, Array.Empty<SafeCoreItemSyntax>(), isPublic,
                    attributes, SpanFrom(start, end)) { IsExternal = true });
            }

            if (!Expect("{")) return null;
            IReadOnlyList<SafeCoreAttributeSyntax> preamble = ParseAttributes(allowInner: true);
            SafeCoreAttributeSyntax[] inner = preamble.Where(static attribute => attribute.IsInner).ToArray();
            SafeCoreAttributeSyntax[] pending = preamble.Where(static attribute => !attribute.IsInner).ToArray();
            var items = new List<SafeCoreItemSyntax>();
            while (!AtEnd && !At("}") && !_terminated)
            {
                Step();
                int before = CurrentStart;
                SafeCoreItemSyntax? item = ParseItem(pending);
                pending = Array.Empty<SafeCoreAttributeSyntax>();
                if (item is not null) items.Add(item); else RecoverItem();
                if (CurrentStart == before && !AtEnd) Consume();
            }

            if (pending.Length != 0 || ParseAttributes().Count != 0)
            {
                ReportExpected("an item after the attribute");
            }

            RustToken? close = ConsumeExpected("}");
            if (close is null) return null;
            return NewNode(new SafeCoreModuleSyntax(name, Array.AsReadOnly(items.ToArray()), isPublic,
                attributes, SpanFrom(start, close)) { InnerAttributes = inner });
        }

        private SafeCoreUseSyntax? ParseUse(IReadOnlyList<SafeCoreAttributeSyntax> attributes, bool isPublic)
        {
            RustToken start = ConsumeExpected("use")!;
            SafeCoreUseTreeSyntax? tree = ParseUseTree();
            if (tree is null) return null;
            RustToken? end = ConsumeExpected(";");
            if (end is null) return null;
            string path = (tree.IsAbsolute ? "::" : string.Empty) + string.Join("::", tree.Prefix);
            return NewNode(new SafeCoreUseSyntax(path, tree.Alias, isPublic, attributes, SpanFrom(start, end))
            {
                Tree = tree,
            });
        }

        private SafeCoreUseTreeSyntax? ParseUseTree()
        {
            EnterDepth();
            try
            {
                int start = CurrentStart;
                bool absolute = At("::");
                if (absolute) Consume();
                var prefix = new List<string>();
                var children = new List<SafeCoreUseTreeSyntax>();
                bool hasSeparator = false;
                while (IsNameToken(Current))
                {
                    prefix.Add(Consume()!.Text);
                    hasSeparator = false;
                    if (!At("::")) break;
                    Consume();
                    hasSeparator = true;
                    if (!IsNameToken(Current))
                    {
                        if (!At("{") && !At("*"))
                        {
                            ReportExpected("an import path segment, group or glob after '::'");
                            return null;
                        }
                        break;
                    }
                }

                SafeCoreUseTreeKind kind = SafeCoreUseTreeKind.Path;
                string? alias = null;
                if (At("{") && (prefix.Count == 0 || hasSeparator))
                {
                    kind = SafeCoreUseTreeKind.Group;
                    Consume();
                    while (!AtEnd && !At("}") && !_terminated)
                    {
                        Step();
                        SafeCoreUseTreeSyntax? child = ParseUseTree();
                        if (child is null) return null;
                        children.Add(child);
                        if (At(",")) Consume();
                        else if (!At("}"))
                        {
                            ReportExpected("',' or '}' in an import group");
                            return null;
                        }
                    }
                    if (!Expect("}")) return null;
                }
                else if (At("*") && (prefix.Count == 0 || hasSeparator))
                {
                    kind = SafeCoreUseTreeKind.Glob;
                    Consume();
                }
                else if (prefix.Count == 0)
                {
                    ReportExpected("an import path, group or glob");
                    return null;
                }
                else if (At("as"))
                {
                    Consume();
                    if (At("_")) alias = Consume()!.Text;
                    else
                    {
                        (alias, _) = ConsumeName("import alias");
                        if (alias is null) return null;
                    }
                }

                return NewNode(new SafeCoreUseTreeSyntax(kind, absolute, Array.AsReadOnly(prefix.ToArray()),
                    alias, Array.AsReadOnly(children.ToArray()), SpanFrom(start, PreviousEnd)));
            }
            finally
            {
                ExitDepth();
            }
        }
        private ReadOnlyCollection<SafeCoreAttributeSyntax> ParseAttributes(bool allowInner = false)
        {
            var attributes = new List<SafeCoreAttributeSyntax>();
            AppendDocumentationAttributes(attributes, ref allowInner);
            while (At("#") && !_terminated)
            {
                RustToken? startToken = Consume();
                bool inner = false;
                if (At("!"))
                {
                    inner = true;
                    if (!allowInner)
                    {
                        ReportUnsupported("inner attributes outside the compilation-unit preamble");
                    }

                    Consume();
                }
                else
                {
                    allowInner = false;
                }

                if (!Expect("["))
                {
                    break;
                }

                var segments = new List<string>();
                do
                {
                    (string? segment, _) = ConsumePathName("attribute path segment");
                    if (segment is null)
                    {
                        return Array.AsReadOnly(attributes.ToArray());
                    }

                    segments.Add(segment);
                    if (!At("::"))
                    {
                        break;
                    }

                    Consume();
                }
                while (!AtEnd && !_terminated);

                int argumentsStart = CurrentStart;
                if (At("(") || At("[") || At("{"))
                {
                    SkipBalancedGroup();
                }
                else if (At("="))
                {
                    Consume();
                    if (Current is not { } value || !IsLiteral(value))
                    {
                        ReportUnsupported("non-literal attribute values");
                        return Array.AsReadOnly(attributes.ToArray());
                    }

                    ValidateLiteralSuffix(value);
                    Consume();
                }

                int argumentsEnd = PreviousEnd;
                RustToken? endToken = ConsumeExpected("]");
                if (endToken is null)
                {
                    ReportUnterminated("attribute");
                    break;
                }

                string arguments = argumentsEnd <= argumentsStart
                    ? string.Empty
                    : _source.Substring(argumentsStart, argumentsEnd - argumentsStart);
                attributes.Add(NewNode(new SafeCoreAttributeSyntax(
                    inner,
                    string.Join("::", segments),
                    arguments,
                    SpanFrom(startToken!, endToken))));
                AppendDocumentationAttributes(attributes, ref allowInner);
            }

            return Array.AsReadOnly(attributes.ToArray());
        }

        private void AppendDocumentationAttributes(List<SafeCoreAttributeSyntax> attributes, ref bool allowInner)
        {
            IReadOnlyList<RustTrivia> triviaList = Current?.LeadingTrivia ?? _lexResult.TrailingTrivia;
            foreach (RustTrivia trivia in triviaList)
            {
                Step();
                if (!trivia.IsDocumentation || !_consumedDocumentation.Add(trivia.Span.Start)) continue;
                bool inner = trivia.Text[2] == '!';
                if (inner && !allowInner)
                {
                    AddDiagnostic(SafeCoreSyntaxDiagnosticCodes.UnexpectedToken,
                        "An inner documentation comment belongs in the enclosing preamble.", trivia.Span.Start, trivia.Span.Length);
                }
                if (!inner) allowInner = false;
                string content = trivia.Kind == RustTriviaKind.BlockComment ? trivia.Text[3..^2] : trivia.Text[3..];
                var escaped = new System.Text.StringBuilder(content.Length + 4);
                escaped.Append("= \"");
                foreach (char character in content)
                {
                    Step();
                    escaped.Append(character switch
                    {
                        '\\' => "\\\\",
                        '"' => "\\\"",
                        '\n' => "\\n",
                        '\r' => "\\r",
                        '\t' => "\\t",
                        '\0' => "\\0",
                        _ => character.ToString(),
                    });
                }
                escaped.Append('"');
                attributes.Add(NewNode(new SafeCoreAttributeSyntax(inner, "doc", escaped.ToString(), trivia.Span)
                {
                    IsDocumentation = true,
                    DocumentationText = content,
                }));
            }
        }

        private bool ParseVisibility() => ParseVisibilitySyntax().Kind == SafeCoreVisibilityKind.Public;

        private SafeCoreVisibilitySyntax ParseVisibilitySyntax()
        {
            if (!At("pub"))
            {
                return NewNode(new SafeCoreVisibilitySyntax(SafeCoreVisibilityKind.Private, null, new TextSpan(CurrentStart, 0)));
            }

            RustToken start = Consume()!;
            SafeCoreVisibilityKind kind = SafeCoreVisibilityKind.Public;
            string? path = null;
            if (At("("))
            {
                Consume();
                bool restricted = At("in");
                if (restricted) Consume();
                if (CurrentText is not ("crate" or "self" or "super"))
                {
                    ReportExpected("'crate', 'self', 'super' or an 'in' path in visibility");
                    RecoverUntil(")");
                }
                else
                {
                    string root = Consume()!.Text;
                    var segments = new List<string> { root };
                    kind = restricted ? SafeCoreVisibilityKind.Restricted : root switch
                    {
                        "crate" => SafeCoreVisibilityKind.Crate,
                        "self" => SafeCoreVisibilityKind.Self,
                        _ => SafeCoreVisibilityKind.Super,
                    };
                    while (restricted && At("::"))
                    {
                        Consume();
                        (string? segment, _) = ConsumePathName("visibility path segment");
                        if (segment is null) break;
                        segments.Add(segment);
                    }
                    path = string.Join("::", segments);
                }
                ConsumeExpected(")");
            }
            return NewNode(new SafeCoreVisibilitySyntax(kind, path, SpanFrom(start, PreviousEnd)));
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
                if (localDepth == 0 && CurrentText is not null &&
                    (stops.Contains(CurrentText) || (AtGreater && stops.Contains(">"))))
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
                // Attribute token-tree contents remain opaque, including documentation tokens.
                if (depth > 0 && Current is { } token)
                {
                    foreach (RustTrivia trivia in token.LeadingTrivia)
                    {
                        Step();
                        if (trivia.IsDocumentation) _consumedDocumentation.Add(trivia.Span.Start);
                    }
                }
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
            if (AtGreater)
            {
                return ConsumeLeadingPunctuation();
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

            if (_pendingToken is { } pending)
            {
                _pendingToken = null;
                _previousEnd = pending.Span.End;
                return pending;
            }

            if (_index >= _tokens.Count)
            {
                return null;
            }

            RustToken token = _tokens[_index++];
            _previousEnd = token.Span.End;
            return token;
        }

        // Rust's grammar may consume one punctuation character from a joint lexical token.
        private RustToken ConsumeLeadingPunctuation()
        {
            RustToken token = Consume()!;
            if (token.Text.Length == 1)
            {
                return token;
            }

            _pendingToken = new RustToken(RustTokenKind.Punctuation,
                new TextSpan(token.Span.Start + 1, token.Span.Length - 1), token.Text[1..],
                false, null, Array.Empty<RustTrivia>());
            _previousEnd = token.Span.Start + 1;
            return new RustToken(RustTokenKind.Punctuation, new TextSpan(token.Span.Start, 1),
                token.Text[..1], false, null, token.LeadingTrivia);
        }

        private void CheckDeadline()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(_startedAt) >= _options.Timeout)
            {
                throw new TimeoutException("Safe-core parsing exceeded its wall-clock budget.");
            }
        }

        private bool Step()
        {
            CheckDeadline();
            if (_terminated)
            {
                throw new ParseLimitException();
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
            Step();
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

        private bool AtEnd => _index >= _tokens.Count && _pendingToken is null;

        private string? CurrentText => Current?.Text;

        private RustToken? Current => _pendingToken ?? (_index < _tokens.Count ? _tokens[_index] : null);

        private int CurrentStart => Current?.Span.Start ?? _source.Length;

        private int PreviousEnd => _previousEnd;

        private bool AtLess => CurrentText is "<" or "<<";
        private bool AtGreater => CurrentText is ">" or ">>" or ">=" or ">>=";

        private static bool IsNameToken(RustToken? token) =>
            IsIdentifierToken(token) ||
            token is { Kind: RustTokenKind.Keyword, Text: "self" or "Self" or "crate" or "super" };

        private static bool IsIdentifierToken(RustToken? token) =>
            token is { Kind: RustTokenKind.Identifier or RustTokenKind.RawIdentifier, Text: not "_" };

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

        private static bool IsUnaryOperator(string text) => text is "!" or "-" or "&" or "&&" or "*";

        private static int GetBinaryPrecedence(string? text) => text switch
        {
            "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "^=" or "&=" or "|=" or
                "<<=" or ">>=" => 1,
            "||" => 2,
            "&&" => 3,
            "==" or "!=" or "<" or ">" or "<=" or ">=" => 4,
            "|" => 5,
            "^" => 6,
            "&" => 7,
            "<<" or ">>" => 9,
            "+" or "-" => 10,
            "*" or "/" or "%" => 11,
            _ => -1,
        };

        private static bool IsAssignmentOperator(string text) => text is
            "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "^=" or "&=" or "|=" or
            "<<=" or ">>=";

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
            Step();
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
            throw new ParseLimitException();
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

        private int CurrentLength => Current?.Span.Length ?? 0;

        private sealed class ParseLimitException : Exception;

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
