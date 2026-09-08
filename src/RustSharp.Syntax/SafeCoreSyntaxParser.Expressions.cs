using System.Collections.ObjectModel;

namespace RustSharp.Syntax;

public static partial class SafeCoreSyntax
{
    private sealed partial class Parser
    {
        private SafeCoreBlockSyntax? ParseBlock()
        {
            if (!EnterDepth()) return null;
            try
            {
                RustToken? start = ConsumeExpected("{");
                if (start is null) return null;
                IReadOnlyList<SafeCoreAttributeSyntax> leading = ParseAttributes(allowInner: true);
                var inner = leading.Where(static a => a.IsInner).ToArray();
                SafeCoreAttributeSyntax[] pending = leading.Where(static a => !a.IsInner).ToArray();
                var statements = new List<SafeCoreStatementSyntax>();
                SafeCoreExpressionSyntax? tail = null;
                while (!AtEnd && !At("}") && !_terminated && Step())
                {
                    int before = CurrentStart;
                    IReadOnlyList<SafeCoreAttributeSyntax> attributes = pending.Length > 0 ? pending : ParseAttributes();
                    pending = Array.Empty<SafeCoreAttributeSyntax>();
                    SafeCoreStatementSyntax? statement = null;
                    if (At(";")) statement = NewNode(new SafeCoreEmptyStatementSyntax(Consume()!.Span));
                    else if (At("let")) statement = ParseLetStatement();
                    else if (IsBlockItemStart())
                    {
                        SafeCoreItemSyntax? item = ParseItem(attributes);
                        if (item is not null) statement = NewNode(new SafeCoreItemStatementSyntax(item, item.Span));
                    }
                    else if (At("return")) statement = ParseReturnStatement();
                    else
                    {
                        SafeCoreExpressionSyntax? expression = ParseExpression(statementContext: true);
                        if (expression is not null)
                        {
                            if (At(";"))
                            {
                                RustToken semicolon = Consume()!;
                                statement = NewNode(new SafeCoreExpressionStatementSyntax(expression, true, SpanFrom(expression.Span.Start, semicolon.Span.End)));
                            }
                            else if (At("}")) tail = expression with { Attributes = attributes };
                            else if (IsExpressionWithBlock(expression)) statement = NewNode(new SafeCoreExpressionStatementSyntax(expression, false, expression.Span));
                            else ReportExpected("';' or '}' after an expression");
                        }
                    }
                    if (statement is not null) statements.Add(statement with { Attributes = attributes });
                    else if (tail is null)
                    {
                        RecoverUntil(";", "}");
                        if (At(";")) Consume();
                    }
                    if (CurrentStart == before && !AtEnd) Consume();
                }
                if (pending.Length > 0) ReportExpected("an item or statement after an outer attribute");
                RustToken? end = ConsumeExpected("}");
                if (end is null) { ReportUnterminated("block"); return null; }
                return NewNode(new SafeCoreBlockSyntax(Array.AsReadOnly(statements.ToArray()), tail, SpanFrom(start, end)) { Attributes = Array.AsReadOnly(inner) });
            }
            finally { ExitDepth(); }
        }

        private bool IsBlockItemStart() => CurrentText is "fn" or "struct" or "enum" or "type" or "use" or "mod" or
            "trait" or "impl" or "pub" or "static" or "extern" or "unsafe" or "async" || At("const") && !NextTextIs("{");

        private bool NextTextIs(string text) => _pendingToken is null && _index + 1 < _tokens.Count && _tokens[_index + 1].Text == text;

        private static bool IsExpressionWithBlock(SafeCoreExpressionSyntax expression) => expression is
            SafeCoreBlockExpressionSyntax or SafeCoreIfExpressionSyntax or SafeCoreMatchExpressionSyntax or
            SafeCoreLoopExpressionSyntax or SafeCoreWhileExpressionSyntax or SafeCoreForExpressionSyntax or
            SafeCoreLabeledBlockExpressionSyntax or SafeCoreConstBlockExpressionSyntax;

        private SafeCoreLetStatementSyntax? ParseLetStatement()
        {
            RustToken start = Consume()!;
            SafeCorePatternSyntax? pattern = ParsePattern();
            if (pattern is null) return null;
            SafeCoreTypeSyntax? type = null;
            if (At(":")) { Consume(); type = ParseType(); if (type is null) return null; }
            SafeCoreExpressionSyntax? initializer = null;
            if (At("=")) { Consume(); initializer = ParseExpression(); if (initializer is null) return null; }
            SafeCoreBlockSyntax? elseBlock = null;
            if (At("else"))
            {
                if (initializer is null) { ReportExpected("an initializer before 'else'"); return null; }
                if (_source[initializer.Span.End - 1] == '}' ||
                    initializer is SafeCoreBinaryExpressionSyntax { Operator: "&&" or "||" })
                    ReportUnexpected("A let-else initializer ending in '}' or using a lazy boolean operator must be parenthesized.");
                Consume(); elseBlock = ParseBlock(); if (elseBlock is null) return null;
            }
            RustToken? end = ConsumeExpected(";");
            return end is null ? null : NewNode(new SafeCoreLetStatementSyntax(pattern, type, initializer, SpanFrom(start, end)) { ElseBlock = elseBlock });
        }

        private SafeCoreReturnStatementSyntax? ParseReturnStatement()
        {
            RustToken start = Consume()!;
            bool hasValue = !AtExpressionBoundary();
            SafeCoreExpressionSyntax? value = hasValue ? ParseExpression() : null;
            if (hasValue && value is null) return null;
            RustToken? end = At("}") ? TokenAtEnd(PreviousEnd) : ConsumeExpected(";");
            return end is null ? null : NewNode(new SafeCoreReturnStatementSyntax(value, SpanFrom(start, end)));
        }

        private static int GetExpressionPrecedence(string? text)
        {
            if (text is ".." or "..=") return 2;
            int original = GetBinaryPrecedence(text);
            return original >= 2 ? original + 1 : original;
        }

        // Delimiters reset the struct-literal restriction; operators propagate it through control-flow heads.
        private SafeCoreExpressionSyntax? ParseExpression(int minimumPrecedence = 0, bool allowStructLiteral = true, bool allowLet = false, bool statementContext = false)
        {
            if (!EnterDepth()) return null;
            try
            {
                SafeCoreExpressionSyntax? left = ParsePrefixExpression(allowStructLiteral, allowLet);
                if (left is null) return null;
                while (!AtEnd && !_terminated && Step())
                {
                    // A following tuple/array/unary expression starts a new statement. Dot and
                    // question-mark postfixes are unambiguous continuations of a block expression.
                    if (statementContext && IsExpressionWithBlock(left) && !At(".") && !At("?")) break;
                    if (At("(")) { left = ParseCallExpression(left); if (left is null) return null; continue; }
                    if (At("["))
                    {
                        Consume(); SafeCoreExpressionSyntax? index = ParseExpression(); RustToken? end = ConsumeExpected("]");
                        if (index is null || end is null) return null;
                        left = NewNode(new SafeCoreIndexExpressionSyntax(left, index, SpanFrom(left.Span.Start, end.Span.End))); continue;
                    }
                    if (At(".")) { left = ParseMemberExpression(left); if (left is null) return null; continue; }
                    if (At("?"))
                    {
                        RustToken end = Consume()!;
                        left = NewNode(new SafeCoreTryExpressionSyntax(left, SpanFrom(left.Span.Start, end.Span.End))); continue;
                    }
                    if (At("as"))
                    {
                        if (13 < minimumPrecedence) break;
                        Consume(); SafeCoreTypeSyntax? type = ParseType(allowBounds: false); if (type is null) return null;
                        left = NewNode(new SafeCoreCastExpressionSyntax(left, type, SpanFrom(left.Span.Start, type.Span.End))); continue;
                    }
                    string? op = CurrentText;
                    int precedence = GetExpressionPrecedence(op);
                    if (precedence < minimumPrecedence)
                    {
                        if (op is "!" or "::") { ReportUnsupported($"expression continuation '{op}'"); return null; }
                        break;
                    }
                    if (op is ".." or "..=")
                    {
                        if (left is SafeCoreRangeExpressionSyntax) ReportUnexpected("Range operators cannot be chained.");
                        RustToken range = Consume()!;
                        bool hasEndpoint = !AtExpressionBoundary() && (allowStructLiteral || !At("{"));
                        SafeCoreExpressionSyntax? endpoint = hasEndpoint ? ParseExpression(3, allowStructLiteral) : null;
                        if (hasEndpoint && endpoint is null || endpoint is null && op == "..=") { ReportExpected("a range endpoint"); return null; }
                        left = NewNode(new SafeCoreRangeExpressionSyntax(left, endpoint, op == "..=", SpanFrom(left.Span.Start, endpoint?.Span.End ?? range.Span.End))); continue;
                    }
                    if (op is "==" or "!=" or "<" or "<=" or ">" or ">=" &&
                        left is SafeCoreBinaryExpressionSyntax { Operator: "==" or "!=" or "<" or "<=" or ">" or ">=" })
                        ReportUnexpected("Comparison operators cannot be chained; parenthesize the comparison result explicitly.");
                    if (op == "||" && ContainsConditionLet(left)) ReportUnexpected("Let expressions are not allowed in a logical-or condition.");
                    Consume();
                    SafeCoreExpressionSyntax? right = ParseExpression(IsAssignmentOperator(op!) ? precedence : precedence + 1, allowStructLiteral, allowLet && op == "&&");
                    if (right is null) return null;
                    left = NewNode(new SafeCoreBinaryExpressionSyntax(op!, left, right, SpanFrom(left.Span.Start, right.Span.End)));
                }
                return left;
            }
            finally { ExitDepth(); }
        }

        private bool ContainsConditionLet(SafeCoreExpressionSyntax expression)
        {
            var pending = new Stack<SafeCoreExpressionSyntax>(); pending.Push(expression);
            while (pending.Count > 0 && !_terminated && Step())
            {
                SafeCoreExpressionSyntax current = pending.Pop();
                if (current is SafeCoreLetExpressionSyntax) return true;
                if (current is SafeCoreBinaryExpressionSyntax { Operator: "&&" } binary)
                { pending.Push(binary.Left); pending.Push(binary.Right); }
            }
            return false;
        }
        private bool AtExpressionBoundary() => AtEnd || CurrentText is ";" or "," or ")" or "]" or "}" or "=>" or "else";

        private SafeCoreExpressionSyntax? ParsePrefixExpression(bool allowStructLiteral = true, bool allowLet = false)
        {
            if (At("println") && NextTextIs("!"))
            {
                RustToken start = Consume()!; Consume();
                SafeCoreCallExpressionSyntax? arguments = ParseCallExpression(NewNode(new SafeCoreNameExpressionSyntax(start.Text, start.Span)));
                return arguments is null ? null : NewNode(new SafeCorePrintExpressionSyntax(arguments.Arguments, arguments.Span));
            }
            if (At("if")) return ParseIfExpression();
            if (At("match")) return ParseMatchExpression();
            if (CurrentText is "loop" or "while" or "for") return ParseLoopExpression(null, CurrentStart);
            if (Current is { Kind: RustTokenKind.Lifetime or RustTokenKind.RawLifetime } && NextTextIs(":"))
            {
                RustToken label = Consume()!; Consume();
                if (At("{"))
                {
                    SafeCoreBlockSyntax? block = ParseBlock();
                    return block is null ? null : NewNode(new SafeCoreLabeledBlockExpressionSyntax(label.Text, block, SpanFrom(label, block.Span.End)));
                }
                return ParseLoopExpression(label.Text, label.Span.Start);
            }
            if (CurrentText is "return" or "break" or "continue") return ParseControlExpression(allowStructLiteral);
            if (CurrentText is "|" or "||" or "move") return ParseClosureExpression(allowStructLiteral);
            if (At("let"))
            {
                if (!allowLet) { ReportUnexpected("A let expression is only allowed in an if or while condition."); return null; }
                RustToken start = Consume()!; SafeCorePatternSyntax? pattern = ParsePattern(allowOr: true);
                if (pattern is null || !Expect("=")) return null;
                SafeCoreExpressionSyntax? value = ParseExpression(5, allowStructLiteral);
                return value is null ? null : NewNode(new SafeCoreLetExpressionSyntax(pattern, value, SpanFrom(start, value.Span.End)));
            }
            if (At("const") && NextTextIs("{"))
            {
                RustToken start = Consume()!; SafeCoreBlockSyntax? block = ParseBlock();
                return block is null ? null : NewNode(new SafeCoreConstBlockExpressionSyntax(block, SpanFrom(start, block.Span.End)));
            }
            if (At("{"))
            {
                SafeCoreBlockSyntax? block = ParseBlock(); return block is null ? null : NewNode(new SafeCoreBlockExpressionSyntax(block, block.Span));
            }
            if (At("(")) return ParseTupleExpression();
            if (At("[")) return ParseArrayExpression();
            if (CurrentText is ".." or "..=")
            {
                RustToken range = Consume()!;
                bool hasEndpoint = !AtExpressionBoundary() && (allowStructLiteral || !At("{"));
                SafeCoreExpressionSyntax? end = hasEndpoint ? ParseExpression(3, allowStructLiteral) : null;
                if (hasEndpoint && end is null || range.Text == "..=" && end is null) { ReportExpected("a range endpoint"); return null; }
                return NewNode(new SafeCoreRangeExpressionSyntax(null, end, range.Text == "..=", SpanFrom(range, end?.Span.End ?? range.Span.End)));
            }
            if (Current is { } token && IsUnaryOperator(token.Text))
            {
                RustToken start = At("&&") ? ConsumeLeadingPunctuation() : Consume()!;
                string op = start.Text;
                if (op == "&" && At("mut")) { Consume(); op = "&mut"; }
                SafeCoreExpressionSyntax? operand = ParseExpression(14, allowStructLiteral);
                return operand is null ? null : NewNode(new SafeCoreUnaryExpressionSyntax(op, operand, SpanFrom(start, operand.Span.End)));
            }
            if (Current is { } literal && IsLiteral(literal))
            {
                ValidateLiteralSuffix(literal); Consume(); return NewNode(new SafeCoreLiteralExpressionSyntax(literal.Kind, literal.Text, literal.Span));
            }
            if (IsNameToken(Current) || At("::"))
            {
                SafeCoreNameExpressionSyntax? name = ParseNameExpression();
                return name is not null && allowStructLiteral && At("{") ? ParseStructExpression(name) : name;
            }
            if (AtLess)
            {
                SafeCoreQualifiedNameExpressionSyntax? qualified = ParseQualifiedNameExpression();
                if (qualified is null) return null;
                if (!allowStructLiteral || !At("{")) return qualified;
                SafeCoreStructExpressionSyntax? structure = ParseStructExpression(qualified.Path);
                return structure is null ? null : structure with { Qualifier = qualified, Span = SpanFrom(qualified.Span.Start, structure.Span.End) };
            }
            if (Current is { } unsupported)
            {
                if (unsupported.Text is "async" or "unsafe" or "const" or "#") ReportUnsupported($"expression '{unsupported.Text}'");
                else ReportUnexpected($"Unexpected token '{unsupported.Text}' in an expression.");
                Consume();
            }
            else ReportExpected("a safe-core expression");
            return null;
        }

        private SafeCoreNameExpressionSyntax? ParseNameExpression()
        {
            int start = CurrentStart;
            bool absolute = At("::"); if (absolute) Consume();
            var segments = new List<SafeCoreExpressionPathSegmentSyntax>();
            while (!AtEnd && !_terminated && Step())
            {
                (string? name, RustToken? token) = ConsumePathName("expression path segment");
                if (name is null || token is null) return null;
                IReadOnlyList<SafeCoreGenericArgumentSyntax> arguments = Array.Empty<SafeCoreGenericArgumentSyntax>();
                bool hasArguments = At("::") && (NextTextIs("<") || NextTextIs("<<"));
                if (hasArguments) { Consume(); arguments = ParseGenericArguments(); }
                segments.Add(NewNode(new SafeCoreExpressionPathSegmentSyntax(name, arguments, SpanFrom(token, PreviousEnd)) { HasGenericArguments = hasArguments }));
                if (!At("::")) break;
                Consume();
            }
            if (segments.Count == 0) { ReportExpected("an expression path"); return null; }
            return NewNode(new SafeCoreNameExpressionSyntax((absolute ? "::" : "") + string.Join("::", segments.Select(static s => s.Name)), SpanFrom(start, PreviousEnd))
                { Segments = Array.AsReadOnly(segments.ToArray()) });
        }

        private SafeCoreQualifiedNameExpressionSyntax? ParseQualifiedNameExpression()
        {
            RustToken start = ConsumeLeadingPunctuation();
            SafeCoreTypeSyntax? selfType = ParseType(); if (selfType is null) return null;
            SafeCoreTypeSyntax? traitType = null;
            if (At("as")) { Consume(); traitType = ParsePathType(); if (traitType is null) return null; }
            if (ConsumeGreater() is null || !Expect("::")) return null;
            SafeCoreNameExpressionSyntax? path = ParseNameExpression();
            return path is null ? null : NewNode(new SafeCoreQualifiedNameExpressionSyntax(selfType, traitType, path, SpanFrom(start, path.Span.End)));
        }

        private SafeCoreMemberExpressionSyntax? ParseMemberExpression(SafeCoreExpressionSyntax target)
        {
            Consume();
            RustToken? member = Current;
            if (At("await")) { ReportUnsupported("await expressions"); return null; }
            if (member is { Kind: RustTokenKind.FloatLiteral, LiteralSuffix: null })
            {
                int dot = member.Text.IndexOf('.');
                if (dot > 0 && member.Text.LastIndexOf('.') == dot &&
                    member.Text.All(static c => c == '.' || c is >= '0' and <= '9'))
                {
                    Consume();
                    SafeCoreMemberExpressionSyntax first = NewNode(new SafeCoreMemberExpressionSyntax(target, member.Text[..dot],
                        Array.Empty<SafeCoreGenericArgumentSyntax>(), SpanFrom(target.Span.Start, member.Span.Start + dot)));
                    if (dot + 1 < member.Text.Length)
                        return NewNode(new SafeCoreMemberExpressionSyntax(first, member.Text[(dot + 1)..],
                            Array.Empty<SafeCoreGenericArgumentSyntax>(), SpanFrom(target.Span.Start, member.Span.End)));
                    _pendingToken = new RustToken(RustTokenKind.Punctuation, new TextSpan(member.Span.End - 1, 1), ".");
                    _previousEnd = member.Span.End - 1;
                    return first;
                }
            }
            if (!IsIdentifierToken(member) && member is not { Kind: RustTokenKind.IntegerLiteral })
            { ReportExpected("a field name, tuple index, or method name after '.'"); return null; }
            Consume();
            if (member!.Kind == RustTokenKind.IntegerLiteral && (member.LiteralSuffix is not null || !member.Text.All(static c => c is >= '0' and <= '9')))
                ReportUnexpected("A tuple field index must be an unsuffixed decimal integer.");
            IReadOnlyList<SafeCoreGenericArgumentSyntax> arguments = Array.Empty<SafeCoreGenericArgumentSyntax>();
            bool hasArguments = At("::");
            if (hasArguments)
            {
                Consume(); if (!AtLess) { ReportExpected("'<' after a method turbofish separator"); return null; }
                arguments = ParseGenericArguments();
                if (!At("(")) { ReportExpected("a method call after generic arguments"); return null; }
            }
            return NewNode(new SafeCoreMemberExpressionSyntax(target, member.Text, arguments, SpanFrom(target.Span.Start, PreviousEnd)) { HasGenericArguments = hasArguments });
        }

        private SafeCoreStructExpressionSyntax? ParseStructExpression(SafeCoreNameExpressionSyntax path)
        {
            Consume(); var fields = new List<SafeCoreStructExpressionFieldSyntax>(); SafeCoreExpressionSyntax? @base = null;
            while (!AtEnd && !At("}") && !_terminated && Step())
            {
                if (At(".."))
                {
                    Consume(); @base = ParseExpression(); if (@base is null) return null;
                    if (!At("}")) { ReportExpected("'}' after a struct update expression"); return null; }
                    break;
                }
                IReadOnlyList<SafeCoreAttributeSyntax> attributes = ParseAttributes();
                RustToken? field = ConsumeExpressionFieldName(); if (field is null) return null;
                bool shorthand = !At(":"); SafeCoreExpressionSyntax? value;
                if (!shorthand) { Consume(); value = ParseExpression(); }
                else if (IsIdentifierToken(field)) value = NewNode(new SafeCoreNameExpressionSyntax(field.Text, field.Span));
                else { ReportExpected("':' after a tuple field index"); return null; }
                if (value is null) return null;
                fields.Add(NewNode(new SafeCoreStructExpressionFieldSyntax(field.Text, value, shorthand, attributes, SpanFrom(field, value.Span.End))));
                if (!At(",")) break;
                Consume();
            }
            RustToken? end = ConsumeExpected("}");
            return end is null ? null : NewNode(new SafeCoreStructExpressionSyntax(path, Array.AsReadOnly(fields.ToArray()), @base, SpanFrom(path.Span.Start, end.Span.End)));
        }

        private RustToken? ConsumeExpressionFieldName()
        {
            if (IsIdentifierToken(Current)) return Consume();
            if (Current is { Kind: RustTokenKind.IntegerLiteral, LiteralSuffix: null } number && number.Text.All(static c => c is >= '0' and <= '9')) return Consume();
            ReportExpected("a field name or unsuffixed tuple field index"); return null;
        }

        private SafeCoreCallExpressionSyntax? ParseCallExpression(SafeCoreExpressionSyntax callee)
        {
            if (!Expect("(")) return null;
            var arguments = new List<SafeCoreExpressionSyntax>();
            while (!AtEnd && !At(")") && !_terminated && Step())
            {
                SafeCoreExpressionSyntax? argument = ParseExpression(); if (argument is null) return null; arguments.Add(argument);
                if (!At(",")) break;
                Consume();
            }
            RustToken? end = ConsumeExpected(")");
            return end is null ? null : NewNode(new SafeCoreCallExpressionSyntax(callee, Array.AsReadOnly(arguments.ToArray()), SpanFrom(callee.Span.Start, end.Span.End)));
        }

        private SafeCoreTupleExpressionSyntax? ParseTupleExpression()
        {
            RustToken start = Consume()!; var elements = new List<SafeCoreExpressionSyntax>(); bool trailing = false;
            while (!AtEnd && !At(")") && !_terminated && Step())
            {
                SafeCoreExpressionSyntax? element = ParseExpression(); if (element is null) return null; elements.Add(element);
                if (!At(",")) { trailing = false; break; }
                Consume(); trailing = At(")");
            }
            RustToken? end = ConsumeExpected(")");
            return end is null ? null : NewNode(new SafeCoreTupleExpressionSyntax(Array.AsReadOnly(elements.ToArray()), trailing, SpanFrom(start, end)));
        }

        private SafeCoreArrayExpressionSyntax? ParseArrayExpression()
        {
            RustToken start = Consume()!; var elements = new List<SafeCoreExpressionSyntax>(); SafeCoreExpressionSyntax? repeat = null;
            while (!AtEnd && !At("]") && !_terminated && Step())
            {
                SafeCoreExpressionSyntax? element = ParseExpression(); if (element is null) return null; elements.Add(element);
                if (At(";"))
                {
                    if (elements.Count != 1) ReportUnexpected("An array repeat expression requires exactly one element before ';'.");
                    Consume(); repeat = ParseExpression(); if (repeat is null) return null; break;
                }
                if (!At(",")) break;
                Consume();
            }
            RustToken? end = ConsumeExpected("]");
            return end is null ? null : NewNode(new SafeCoreArrayExpressionSyntax(Array.AsReadOnly(elements.ToArray()), repeat, SpanFrom(start, end)));
        }

        private SafeCoreMatchExpressionSyntax? ParseMatchExpression()
        {
            RustToken start = Consume()!;
            SafeCoreExpressionSyntax? scrutinee = ParseExpression(allowStructLiteral: false);
            if (scrutinee is null || !Expect("{")) return null;
            var arms = new List<SafeCoreMatchArmSyntax>();
            while (!AtEnd && !At("}") && !_terminated && Step())
            {
                IReadOnlyList<SafeCoreAttributeSyntax> attributes = ParseAttributes(); int armStart = CurrentStart;
                SafeCorePatternSyntax? pattern = ParsePattern(allowOr: true); if (pattern is null) return null;
                SafeCoreExpressionSyntax? guard = null;
                if (At("if")) { Consume(); guard = ParseExpression(); if (guard is null) return null; }
                if (!Expect("=>")) return null;
                SafeCoreExpressionSyntax? body = ParseExpression(statementContext: true); if (body is null) return null;
                arms.Add(NewNode(new SafeCoreMatchArmSyntax(pattern, guard, body, attributes, SpanFrom(armStart, body.Span.End))));
                if (At(",")) Consume();
                else if (!At("}") && !IsExpressionWithBlock(body)) { ReportExpected("',' after a match arm without a block"); return null; }
            }
            RustToken? end = ConsumeExpected("}");
            return end is null ? null : NewNode(new SafeCoreMatchExpressionSyntax(scrutinee, Array.AsReadOnly(arms.ToArray()), SpanFrom(start, end)));
        }

        private SafeCoreExpressionSyntax? ParseLoopExpression(string? label, int start)
        {
            string? kind = CurrentText;
            if (kind is not ("loop" or "while" or "for")) { ReportExpected("a loop or block after a label"); return null; }
            Consume(); SafeCoreExpressionSyntax? condition = null; SafeCorePatternSyntax? pattern = null;
            if (kind == "while") { condition = ParseExpression(allowStructLiteral: false, allowLet: true); if (condition is null) return null; }
            if (kind == "for")
            {
                pattern = ParsePattern(); if (pattern is null || !Expect("in")) return null;
                condition = ParseExpression(allowStructLiteral: false); if (condition is null) return null;
            }
            SafeCoreBlockSyntax? body = ParseBlock(); if (body is null) return null;
            TextSpan span = SpanFrom(start, body.Span.End);
            return kind switch
            {
                "loop" => NewNode(new SafeCoreLoopExpressionSyntax(label, body, span)),
                "while" => NewNode(new SafeCoreWhileExpressionSyntax(label, condition!, body, span)),
                _ => NewNode(new SafeCoreForExpressionSyntax(label, pattern!, condition!, body, span)),
            };
        }

        private SafeCoreExpressionSyntax? ParseControlExpression(bool allowStructLiteral)
        {
            RustToken keyword = Consume()!; string? label = null;
            if (keyword.Text != "return" && Current is { Kind: RustTokenKind.Lifetime or RustTokenKind.RawLifetime }) label = Consume()!.Text;
            if (keyword.Text == "continue") return NewNode(new SafeCoreContinueExpressionSyntax(label, SpanFrom(keyword, PreviousEnd)));
            bool hasValue = !AtExpressionBoundary() && (allowStructLiteral || !At("{"));
            SafeCoreExpressionSyntax? value = hasValue ? ParseExpression(allowStructLiteral: allowStructLiteral) : null;
            if (hasValue && value is null) return null;
            TextSpan span = SpanFrom(keyword, value?.Span.End ?? PreviousEnd);
            return keyword.Text == "return" ? NewNode(new SafeCoreReturnExpressionSyntax(value, span)) : NewNode(new SafeCoreBreakExpressionSyntax(label, value, span));
        }

        private SafeCoreClosureExpressionSyntax? ParseClosureExpression(bool allowStructLiteral)
        {
            int start = CurrentStart; bool move = At("move"); if (move) Consume();
            var parameters = new List<SafeCoreClosureParameterSyntax>();
            if (At("||")) Consume();
            else
            {
                if (!Expect("|")) return null;
                while (!AtEnd && !At("|") && !_terminated && Step())
                {
                    IReadOnlyList<SafeCoreAttributeSyntax> attributes = ParseAttributes(); int parameterStart = CurrentStart;
                    SafeCorePatternSyntax? pattern = ParsePattern(allowOr: false); if (pattern is null) return null;
                    SafeCoreTypeSyntax? type = null;
                    if (At(":")) { Consume(); type = ParseType(); if (type is null) return null; }
                    parameters.Add(NewNode(new SafeCoreClosureParameterSyntax(pattern, type, attributes, SpanFrom(parameterStart, PreviousEnd))));
                    if (!At(",")) break;
                    Consume();
                }
                if (!Expect("|")) return null;
            }
            SafeCoreTypeSyntax? returnType = null;
            if (At("->")) { Consume(); returnType = ParseType(); if (returnType is null) return null; }
            SafeCoreExpressionSyntax? body;
            if (returnType is not null)
            {
                if (!At("{")) { ReportExpected("a block after a closure return type"); return null; }
                SafeCoreBlockSyntax? block = ParseBlock(); body = block is null ? null : NewNode(new SafeCoreBlockExpressionSyntax(block, block.Span));
            }
            else body = ParseExpression(allowStructLiteral: allowStructLiteral);
            return body is null ? null : NewNode(new SafeCoreClosureExpressionSyntax(move, Array.AsReadOnly(parameters.ToArray()), returnType, body, SpanFrom(start, body.Span.End)));
        }

        private SafeCorePatternSyntax? ParsePattern(bool allowOr = false, bool allowRest = false, bool allowRange = true)
        {
            if (!EnterDepth()) return null;
            try
            {
                int start = CurrentStart; bool leadingOr = allowOr && At("|"); if (leadingOr) Consume();
                SafeCorePatternSyntax? first = ParsePatternWithoutAlternative(allowRest, allowRange); if (first is null) return null;
                if (!allowOr || !At("|") && !leadingOr) return first;
                var alternatives = new List<SafeCorePatternSyntax> { first };
                while (At("|") && !_terminated && Step())
                {
                    Consume(); SafeCorePatternSyntax? next = ParsePatternWithoutAlternative(allowRest, allowRange); if (next is null) return null; alternatives.Add(next);
                }
                return NewNode(new SafeCoreOrPatternSyntax(Array.AsReadOnly(alternatives.ToArray()), SpanFrom(start, PreviousEnd)));
            }
            finally { ExitDepth(); }
        }

        private SafeCorePatternSyntax? ParsePatternWithoutAlternative(bool allowRest, bool allowRange)
        {
            SafeCorePatternSyntax? pattern = ParsePatternAtom(allowRest); if (pattern is null) return null;
            if (!allowRange && pattern is SafeCoreRangePatternSyntax)
            { ReportUnexpected("A range pattern inside a reference pattern must be parenthesized."); return null; }
            if (At("@"))
            {
                if (pattern is not SafeCoreIdentifierPatternSyntax binding) { ReportUnexpected("The left side of '@' must be an identifier binding."); return null; }
                Consume(); SafeCorePatternSyntax? nested = ParsePattern(allowRest: allowRest); if (nested is null) return null;
                return NewNode(new SafeCoreAtPatternSyntax(binding, nested, SpanFrom(binding.Span.Start, nested.Span.End)));
            }
            if (allowRange && CurrentText is ".." or "..=" or "...")
            {
                if (!IsRangePatternBound(pattern)) { ReportUnexpected("A range bound must be a literal or path."); return null; }
                RustToken op = Consume()!;
                if (op.Text == "...") ReportUnexpected("Use '..=' for an inclusive range pattern in Edition 2024.");
                bool hasEnd = !AtPatternBoundary(); SafeCorePatternSyntax? end = hasEnd ? ParsePatternAtom(false) : null;
                if (hasEnd && end is null) return null;
                if (end is not null && !IsRangePatternBound(end)) { ReportUnexpected("A range bound must be a literal or path."); return null; }
                if (end is null && op.Text != "..") { ReportExpected("an inclusive range pattern endpoint"); return null; }
                return NewNode(new SafeCoreRangePatternSyntax(pattern, end, op.Text != "..", SpanFrom(pattern.Span.Start, end?.Span.End ?? op.Span.End)));
            }
            return pattern;
        }

        private static bool IsRangePatternBound(SafeCorePatternSyntax pattern) => pattern is SafeCoreLiteralPatternSyntax or SafeCoreIdentifierPatternSyntax { IsMutable: false, IsByReference: false } or
            SafeCorePathPatternSyntax { Arguments.Count: 0 };

        private bool AtPatternBoundary() => AtEnd || CurrentText is "," or ";" or ":" or "=" or ")" or "]" or "}" or "|" or "=>" or "if" or "in";

        private SafeCorePatternSyntax? ParsePatternAtom(bool allowRest)
        {
            if (!EnterDepth()) return null;
            try { return ParsePatternAtomCore(allowRest); }
            finally { ExitDepth(); }
        }

        private SafeCorePatternSyntax? ParsePatternAtomCore(bool allowRest)
        {
            if (At("&") || At("&&"))
            {
                RustToken start = At("&&") ? ConsumeLeadingPunctuation() : Consume()!;
                bool mutable = At("mut"); if (mutable) Consume();
                SafeCorePatternSyntax? nested = ParsePattern(allowRange: false);
                return nested is null ? null : NewNode(new SafeCoreReferencePatternSyntax(mutable, nested, SpanFrom(start, nested.Span.End)));
            }
            if (At("mut") || At("ref"))
            {
                RustToken start = Consume()!; bool byReference = start.Text == "ref"; bool mutable = start.Text == "mut";
                if (byReference && At("mut")) { mutable = true; Consume(); }
                (string? name, RustToken? token) = ConsumeName("binding name");
                return name is null || token is null ? null : NewNode(new SafeCoreIdentifierPatternSyntax(name, mutable, SpanFrom(start, token)) { IsByReference = byReference });
            }
            if (At("_")) return NewNode(new SafeCoreWildcardPatternSyntax(Consume()!.Span));
            if (At("-") || Current is { } literal && IsLiteral(literal))
            {
                RustToken start = Consume()!; RustToken number = start;
                if (start.Text == "-")
                {
                    if (Current is not { Kind: RustTokenKind.IntegerLiteral or RustTokenKind.FloatLiteral } signed) { ReportExpected("a numeric literal after '-' in a pattern"); return null; }
                    number = signed; Consume();
                }
                ValidateLiteralSuffix(number); TextSpan span = SpanFrom(start, number);
                return NewNode(new SafeCoreLiteralPatternSyntax(number.Kind, _source.Substring(span.Start, span.Length), span));
            }
            if (At("(") || At("[")) return ParseSequencePattern();
            if (CurrentText is ".." or "..=")
            {
                RustToken op = Consume()!;
                if (op.Text == ".." && AtPatternBoundary())
                {
                    if (!allowRest) { ReportUnexpected("A rest pattern is only allowed inside a tuple, slice, or tuple-struct pattern."); return null; }
                    return NewNode(new SafeCoreRestPatternSyntax(op.Span));
                }
                SafeCorePatternSyntax? end = ParsePatternAtom(false);
                if (end is null || !IsRangePatternBound(end)) { ReportExpected("a literal or path range endpoint"); return null; }
                return NewNode(new SafeCoreRangePatternSyntax(null, end, op.Text == "..=", SpanFrom(op, end.Span.End)));
            }
            if (!IsNameToken(Current) && !At("::") && !AtLess) { ReportExpected("a safe-core pattern"); return null; }
            int startPosition = CurrentStart;
            bool isQualified = AtLess;
            SafeCoreQualifiedNameExpressionSyntax? qualifier = isQualified ? ParseQualifiedNameExpression() : null;
            if (isQualified && qualifier is null) return null;
            SafeCoreNameExpressionSyntax? namePath = qualifier?.Path ?? ParseNameExpression();
            if (namePath is null) return null;
            if (At("!")) { ReportUnsupported("macro invocations in patterns"); return null; }
            string path = namePath.Path;
            if (At("{"))
            {
                SafeCoreStructPatternSyntax? structure = ParseStructPattern(path, startPosition);
                return structure is null ? null : structure with { Segments = namePath.Segments, Qualifier = qualifier };
            }
            if (At("("))
            {
                Consume(); IReadOnlyList<SafeCorePatternSyntax>? arguments = ParsePatternElements(")", out _);
                RustToken? end = ConsumeExpected(")");
                return arguments is null || end is null ? null : NewNode(new SafeCorePathPatternSyntax(path, arguments, SpanFrom(startPosition, end.Span.End))
                    { HasArguments = true, Segments = namePath.Segments, Qualifier = qualifier });
            }
            return qualifier is null && namePath.Segments.Count == 1 && !namePath.Segments[0].HasGenericArguments && !path.StartsWith("::", StringComparison.Ordinal) && path is not ("Self" or "self" or "super" or "crate")
                ? NewNode(new SafeCoreIdentifierPatternSyntax(path, false, SpanFrom(startPosition, PreviousEnd)))
                : NewNode(new SafeCorePathPatternSyntax(path, Array.Empty<SafeCorePatternSyntax>(), SpanFrom(startPosition, PreviousEnd))
                    { Segments = namePath.Segments, Qualifier = qualifier });
        }

        private SafeCorePatternSyntax? ParseSequencePattern()
        {
            RustToken start = Consume()!; string close = start.Text == "(" ? ")" : "]";
            ReadOnlyCollection<SafeCorePatternSyntax>? elements = ParsePatternElements(close, out bool trailing);
            RustToken? end = ConsumeExpected(close); if (elements is null || end is null) return null;
            return start.Text == "(" ? NewNode(new SafeCoreTuplePatternSyntax(elements, trailing, SpanFrom(start, end)))
                : NewNode(new SafeCoreSlicePatternSyntax(elements, SpanFrom(start, end)));
        }

        private ReadOnlyCollection<SafeCorePatternSyntax>? ParsePatternElements(string close, out bool trailing)
        {
            var elements = new List<SafeCorePatternSyntax>(); trailing = false; int rests = 0;
            while (!AtEnd && !At(close) && !_terminated && Step())
            {
                SafeCorePatternSyntax? element = ParsePattern(allowOr: true, allowRest: true); if (element is null) return null;
                if (element is SafeCoreRestPatternSyntax or SafeCoreAtPatternSyntax { Pattern: SafeCoreRestPatternSyntax }) rests++;
                if (rests > 1) ReportUnexpected("A sequence pattern may contain only one rest pattern.");
                elements.Add(element);
                if (!At(",")) { trailing = false; break; }
                Consume(); trailing = At(close);
            }
            return Array.AsReadOnly(elements.ToArray());
        }

        private SafeCoreStructPatternSyntax? ParseStructPattern(string path, int start)
        {
            Consume(); var fields = new List<SafeCoreStructPatternFieldSyntax>(); bool rest = false;
            while (!AtEnd && !At("}") && !_terminated && Step())
            {
                if (At(".."))
                {
                    Consume(); rest = true; if (At(",")) Consume();
                    if (!At("}")) { ReportExpected("'}' after a struct rest pattern"); return null; }
                    break;
                }
                IReadOnlyList<SafeCoreAttributeSyntax> attributes = ParseAttributes(); int fieldStart = CurrentStart;
                SafeCorePatternSyntax? pattern; string name; bool shorthand;
                if (At("ref") || At("mut"))
                {
                    pattern = ParsePatternAtom(false);
                    if (pattern is not SafeCoreIdentifierPatternSyntax binding) return null;
                    name = binding.Name; shorthand = true;
                }
                else
                {
                    RustToken? field = ConsumeExpressionFieldName(); if (field is null) return null; name = field.Text; shorthand = !At(":");
                    if (!shorthand) { Consume(); pattern = ParsePattern(allowOr: true); }
                    else if (IsIdentifierToken(field)) pattern = NewNode(new SafeCoreIdentifierPatternSyntax(name, false, field.Span));
                    else { ReportExpected("':' after a tuple field index"); return null; }
                }
                if (pattern is null) return null;
                fields.Add(NewNode(new SafeCoreStructPatternFieldSyntax(name, pattern, shorthand, attributes, SpanFrom(fieldStart, pattern.Span.End))));
                if (!At(",")) break;
                Consume();
            }
            RustToken? end = ConsumeExpected("}");
            return end is null ? null : NewNode(new SafeCoreStructPatternSyntax(path, Array.AsReadOnly(fields.ToArray()), rest, SpanFrom(start, end.Span.End)));
        }

        private SafeCoreIfExpressionSyntax? ParseIfExpression()
        {
            RustToken start = Consume()!;
            SafeCoreExpressionSyntax? condition = ParseExpression(allowStructLiteral: false, allowLet: true);
            SafeCoreBlockSyntax? then = ParseBlock();
            if (condition is null || then is null) return null;
            SafeCoreExpressionSyntax? otherwise = null;
            if (At("else"))
            {
                Consume();
                if (!At("if") && !At("{")) { ReportExpected("'if' or '{' after 'else'"); return null; }
                if (!EnterDepth()) return null;
                try { otherwise = ParsePrefixExpression(); }
                finally { ExitDepth(); }
                if (otherwise is null) return null;
            }
            return NewNode(new SafeCoreIfExpressionSyntax(condition, then, otherwise, SpanFrom(start, otherwise?.Span.End ?? then.Span.End)));
        }
    }
}
