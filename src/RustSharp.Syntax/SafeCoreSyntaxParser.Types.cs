using System.Collections.ObjectModel;

namespace RustSharp.Syntax;

public static partial class SafeCoreSyntax
{
    private sealed partial class Parser
    {
        private SafeCoreTypeSyntax? ParseType(bool allowBounds = true)
        {
            if (!EnterDepth()) { return null; }
            try
            {
                if (At("&") || At("&&"))
                {
                    RustToken start = ConsumeLeadingPunctuation();
                    string? lifetime = IsLifetimeToken(Current) ? Consume()!.Text : null;
                    bool mutable = TakeItemToken("mut");
                    SafeCoreTypeSyntax? inner = ParseType(allowBounds: false);
                    return inner is null ? null : NewNode(new SafeCoreReferenceTypeSyntax(
                        lifetime, mutable, inner, SpanFrom(start, inner.Span.End)));
                }

                if (At("!")) { return NewNode(new SafeCoreNeverTypeSyntax(Consume()!.Span)); }
                if (At("_")) { return NewNode(new SafeCoreInferredTypeSyntax(Consume()!.Span)); }
                if (At("fn") || At("for")) { return ParseFunctionType(); }
                if (At("dyn") || At("impl"))
                {
                    RustToken start = Consume()!;
                    ReadOnlyCollection<SafeCoreTypeBoundSyntax> bounds = ParseTypeBounds(allowMultiple: allowBounds);
                    if (bounds.Count == 0) { ReportExpected("at least one trait bound"); return null; }
                    return NewNode(new SafeCoreBoundedTypeSyntax(start.Text == "dyn", bounds, SpanFrom(start, PreviousEnd)));
                }

                if (At("("))
                {
                    RustToken start = Consume()!;
                    var elements = new List<SafeCoreTypeSyntax>();
                    bool trailingComma = false;
                    while (!AtEnd && !At(")") && !_terminated && Step())
                    {
                        SafeCoreTypeSyntax? element = ParseType();
                        if (element is null) { return null; }
                        elements.Add(element);
                        trailingComma = TakeItemToken(",");
                        if (!trailingComma) { break; }
                    }
                    RustToken? end = ConsumeExpected(")");
                    if (end is null) { return null; }
                    return elements.Count == 0
                        ? NewNode(new SafeCoreUnitTypeSyntax(SpanFrom(start, end)))
                        : NewNode(new SafeCoreTupleTypeSyntax(elements.AsReadOnly(), trailingComma, SpanFrom(start, end)));
                }

                if (At("["))
                {
                    RustToken start = Consume()!;
                    SafeCoreTypeSyntax? element = ParseType();
                    if (element is null) { return null; }
                    if (TakeItemToken(";"))
                    {
                        SafeCoreExpressionSyntax? length = ParseExpression();
                        RustToken? end = ConsumeExpected("]");
                        return length is null || end is null ? null : NewNode(new SafeCoreArrayTypeSyntax(element, length, SpanFrom(start, end)));
                    }
                    RustToken? sliceEnd = ConsumeExpected("]");
                    return sliceEnd is null ? null : NewNode(new SafeCoreSliceTypeSyntax(element, SpanFrom(start, sliceEnd)));
                }

                if (CurrentText is "unsafe" or "extern" or "async" or "*")
                {
                    ReportUnsupported($"type form '{CurrentText}'");
                    Consume();
                    return null;
                }
                if (AtLess) { return ParseQualifiedPathType(); }
                return ParsePathType();
            }
            finally { ExitDepth(); }
        }

        private SafeCorePathTypeSyntax? ParsePathType()
        {
            int start = CurrentStart;
            bool absolute = TakeItemToken("::");
            if (!IsNameToken(Current)) { ReportExpected("a type path segment"); return null; }
            var segments = new List<SafeCorePathSegmentSyntax>();
            while (!AtEnd && !_terminated && Step())
            {
                int segmentStart = CurrentStart;
                (string? name, _) = ConsumePathName("a type path segment");
                if (name is null) { return null; }
                if (At("::") && PeekItemText(1) is "<" or "<<" or "(") { Consume(); }
                bool hasGenericArguments = AtLess;
                IReadOnlyList<SafeCoreGenericArgumentSyntax> arguments = hasGenericArguments ? ParseGenericArguments() : [];
                bool functionArguments = false;
                var functionParameters = new List<SafeCoreTypeSyntax>();
                SafeCoreTypeSyntax? functionReturn = null;
                if (At("("))
                {
                    if (hasGenericArguments) { ReportUnexpected("A type path segment cannot have both angle-bracketed and function arguments."); return null; }
                    functionArguments = true;
                    Consume();
                    while (!AtEnd && !At(")") && !_terminated && Step())
                    {
                        SafeCoreTypeSyntax? parameter = ParseType();
                        if (parameter is null) { return null; }
                        functionParameters.Add(parameter);
                        if (!TakeItemToken(",")) { break; }
                    }
                    if (!Expect(")")) { return null; }
                    if (TakeItemToken("->"))
                    {
                        functionReturn = ParseType(allowBounds: false);
                        if (functionReturn is null) { return null; }
                    }
                }
                segments.Add(NewNode(new SafeCorePathSegmentSyntax(name,
                    arguments.OfType<SafeCoreTypeArgumentSyntax>().Select(static argument => argument.Type).ToArray(),
                    SpanFrom(segmentStart, PreviousEnd))
                {
                    Arguments = arguments,
                    HasGenericArguments = hasGenericArguments,
                    HasFunctionArguments = functionArguments,
                    FunctionParameters = functionParameters.AsReadOnly(),
                    FunctionReturnType = functionReturn,
                }));
                if (!TakeItemToken("::")) { break; }
                if (!IsNameToken(Current)) { ReportExpected("a type path segment after '::'"); return null; }
            }
            return segments.Count == 0 ? null : NewNode(new SafeCorePathTypeSyntax(segments.AsReadOnly(), SpanFrom(start, PreviousEnd)) { IsAbsolute = absolute });
        }

        private SafeCoreQualifiedPathTypeSyntax? ParseQualifiedPathType()
        {
            RustToken start = ConsumeLeadingPunctuation();
            SafeCoreTypeSyntax? self = ParseType();
            if (self is null) { return null; }
            SafeCoreTypeSyntax? trait = null;
            if (TakeItemToken("as"))
            {
                trait = ParsePathType();
                if (trait is null) { return null; }
            }
            if (ConsumeGreater() is null || !Expect("::")) { return null; }
            SafeCorePathTypeSyntax? path = ParsePathType();
            return path is null ? null : NewNode(new SafeCoreQualifiedPathTypeSyntax(self, trait, path.Segments, SpanFrom(start, path.Span.End)));
        }

        private ReadOnlyCollection<SafeCoreGenericArgumentSyntax> ParseGenericArguments()
        {
            if (!AtLess) { ReportExpected("'<'"); return new([]); }
            ConsumeLeadingPunctuation();
            var arguments = new List<SafeCoreGenericArgumentSyntax>();
            while (!AtEnd && !AtGreater && !_terminated && Step())
            {
                int start = CurrentStart;
                SafeCoreGenericArgumentSyntax? argument;
                if (IsLifetimeToken(Current))
                {
                    RustToken lifetime = Consume()!;
                    argument = NewNode(new SafeCoreLifetimeArgumentSyntax(lifetime.Text, lifetime.Span));
                }
                else if (At("{") || At("-") || Current is { } literal && IsLiteral(literal))
                {
                    SafeCoreExpressionSyntax? value = ParseConstGenericValue();
                    argument = value is null ? null : NewNode(new SafeCoreConstArgumentSyntax(value, value.Span));
                }
                else
                {
                    SafeCoreTypeSyntax? type = ParseType();
                    if (type is null) { break; }
                    if (At("=") || At(":"))
                    {
                        if (type is not SafeCorePathTypeSyntax { IsAbsolute: false, Segments.Count: 1 } path || path.Segments[0].HasFunctionArguments)
                        {
                            ReportExpected("an associated type name before its constraint");
                            break;
                        }
                        SafeCorePathSegmentSyntax segment = path.Segments[0];
                        if (TakeItemToken("="))
                        {
                            SafeCoreTypeSyntax? assigned = ParseType();
                            argument = assigned is null ? null : NewNode(new SafeCoreAssociatedTypeArgumentSyntax(segment.Name, assigned, SpanFrom(start, assigned.Span.End)) { Arguments = segment.Arguments });
                        }
                        else
                        {
                            Consume();
                            IReadOnlyList<SafeCoreTypeBoundSyntax> bounds = ParseTypeBounds();
                            argument = NewNode(new SafeCoreAssociatedConstraintArgumentSyntax(segment.Name, bounds, SpanFrom(start, PreviousEnd)) { Arguments = segment.Arguments });
                        }
                    }
                    else { argument = NewNode(new SafeCoreTypeArgumentSyntax(type, type.Span)); }
                }
                if (argument is null) { break; }
                arguments.Add(argument);
                if (!TakeItemToken(","))
                {
                    if (!AtGreater) { ReportExpected("',' or '>' in generic arguments"); }
                    break;
                }
            }
            if (ConsumeGreater() is null) { ReportUnterminated("generic argument list"); }
            return arguments.AsReadOnly();
        }

        private SafeCoreExpressionSyntax? ParseConstGenericValue()
        {
            if (At("{"))
            {
                SafeCoreBlockSyntax? block = ParseBlock();
                return block is null ? null : NewNode(new SafeCoreBlockExpressionSyntax(block, block.Span));
            }
            RustToken? minus = At("-") ? Consume() : null;
            if (Current is { } literal && IsLiteral(literal))
            {
                Consume();
                ValidateLiteralSuffix(literal);
                SafeCoreExpressionSyntax value = NewNode(new SafeCoreLiteralExpressionSyntax(literal.Kind, literal.Text, literal.Span));
                if (minus is not null)
                {
                    if (literal.Kind is not (RustTokenKind.IntegerLiteral or RustTokenKind.FloatLiteral))
                    {
                        ReportUnexpected("A negative const argument requires a numeric literal.");
                        return null;
                    }
                    value = NewNode(new SafeCoreUnaryExpressionSyntax("-", value, SpanFrom(minus, value.Span.End)));
                }
                return value;
            }
            if (minus is null && IsIdentifierToken(Current))
            {
                RustToken name = Consume()!;
                return NewNode(new SafeCoreNameExpressionSyntax(name.Text, name.Span));
            }
            ReportExpected("a const literal, identifier, or braced expression");
            return null;
        }

        private SafeCoreFunctionTypeSyntax? ParseFunctionType()
        {
            int start = CurrentStart;
            IReadOnlyList<SafeCoreGenericParameterSyntax> binder = ParseLifetimeBinder();
            if (CurrentText is "unsafe" or "extern" or "async")
            {
                ReportUnsupported("unsafe, foreign, or async function types");
                return null;
            }
            if (!Expect("fn") || !Expect("(")) { return null; }
            var parameters = new List<SafeCoreFunctionTypeParameterSyntax>();
            while (!AtEnd && !At(")") && !_terminated && Step())
            {
                int parameterStart = CurrentStart;
                IReadOnlyList<SafeCoreAttributeSyntax> attributes = ParseAttributes();
                string? name = null;
                if ((IsIdentifierToken(Current) || At("_")) && PeekItemText(1) == ":")
                {
                    name = Consume()!.Text;
                    Consume();
                }
                if (At("...")) { ReportUnsupported("variadic foreign function parameters"); return null; }
                SafeCoreTypeSyntax? type = ParseType();
                if (type is null) { return null; }
                parameters.Add(NewNode(new SafeCoreFunctionTypeParameterSyntax(name, type, SpanFrom(parameterStart, type.Span.End)) { Attributes = attributes }));
                if (!TakeItemToken(",")) { break; }
            }
            if (!Expect(")")) { return null; }
            SafeCoreTypeSyntax? returnType = null;
            if (TakeItemToken("->"))
            {
                returnType = ParseType(allowBounds: false);
                if (returnType is null) { return null; }
            }
            return NewNode(new SafeCoreFunctionTypeSyntax(binder, parameters.AsReadOnly(), returnType, SpanFrom(start, PreviousEnd)));
        }

        private ReadOnlyCollection<SafeCoreTypeBoundSyntax> ParseTypeBounds(bool lifetimesOnly = false, bool allowMultiple = true)
        {
            var bounds = new List<SafeCoreTypeBoundSyntax>();
            while (!AtEnd && !_terminated && Step())
            {
                int start = CurrentStart;
                if (IsLifetimeToken(Current))
                {
                    RustToken lifetime = Consume()!;
                    bounds.Add(NewNode(new SafeCoreLifetimeBoundSyntax(lifetime.Text, lifetime.Span)));
                }
                else if (!lifetimesOnly && (IsNameToken(Current) || CurrentText is "?" or "for" or "(" or "::"))
                {
                    bool parenthesized = TakeItemToken("(");
                    bool optional = TakeItemToken("?");
                    IReadOnlyList<SafeCoreGenericParameterSyntax> binder = ParseLifetimeBinder();
                    SafeCoreTypeSyntax? trait = ParsePathType();
                    if (trait is null) { break; }
                    if (parenthesized && !Expect(")")) { break; }
                    bounds.Add(NewNode(new SafeCoreTraitBoundSyntax(trait, optional, binder, SpanFrom(start, PreviousEnd))));
                }
                else { break; }
                if (!allowMultiple || !TakeItemToken("+")) { break; }
            }
            return bounds.AsReadOnly();
        }

        private IReadOnlyList<SafeCoreGenericParameterSyntax> ParseLifetimeBinder()
        {
            if (!TakeItemToken("for")) { return []; }
            if (!At("<")) { ReportExpected("'<' after 'for'"); return []; }
            IReadOnlyList<SafeCoreGenericParameterSyntax> binder = ParseGenericParameters();
            if (binder.Any(static parameter => parameter.Kind != SafeCoreGenericParameterKind.Lifetime))
            {
                ReportUnexpected("A higher-ranked binder may declare only lifetime parameters.");
            }
            return binder;
        }

        private static bool IsLifetimeToken(RustToken? token) => token is { Kind: RustTokenKind.Lifetime or RustTokenKind.RawLifetime };

        private bool TakeItemToken(string token)
        {
            if (!At(token)) { return false; }
            Consume();
            return true;
        }

        private string? PeekItemText(int offset)
        {
            int index = _pendingToken is null ? _index + offset : _index + offset - 1;
            return index >= 0 && index < _tokens.Count ? _tokens[index].Text : null;
        }
    }
}
