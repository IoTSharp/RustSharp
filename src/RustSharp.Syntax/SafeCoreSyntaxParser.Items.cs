using System.Collections.ObjectModel;

namespace RustSharp.Syntax;

public static partial class SafeCoreSyntax
{
    private sealed partial class Parser
    {
        private SafeCoreFunctionSyntax? ParseFunction(IReadOnlyList<SafeCoreAttributeSyntax> attributes, bool isPublic)
        {
            int start = CurrentStart;
            bool isConst = TakeItemToken("const");
            ParsedFunctionSignature? signature = ParseFunctionSignature(allowReceiver: false);
            if (signature is null) { return null; }
            if (At(";"))
            {
                ReportExpected("a block body for a free function");
                Consume();
                return null;
            }
            SafeCoreBlockSyntax? body = ParseBlock();
            return body is null ? null : NewNode(new SafeCoreFunctionSyntax(
                signature.Name, signature.GenericParameters, signature.Parameters, signature.ReturnType,
                body, isPublic, attributes, SpanFrom(start, body.Span.End))
            {
                WhereClause = signature.WhereClause,
                IsConst = isConst,
            });
        }

        private ParsedFunctionSignature? ParseFunctionSignature(bool allowReceiver)
        {
            if (!Expect("fn")) { return null; }
            (string? name, _) = ConsumeName("function name");
            if (name is null) { return null; }
            IReadOnlyList<SafeCoreGenericParameterSyntax> generics = ParseGenericParameters();
            if (!Expect("(")) { return null; }
            var parameters = new List<SafeCoreParameterSyntax>();
            while (!AtEnd && !At(")") && !_terminated && Step())
            {
                int start = CurrentStart;
                IReadOnlyList<SafeCoreAttributeSyntax> attributes = ParseAttributes();
                SafeCoreParameterSyntax? parameter;
                if (IsReceiverStart())
                {
                    if (!allowReceiver || parameters.Count != 0)
                    {
                        ReportUnexpected("A self receiver is allowed only as the first parameter of an associated function.");
                        return null;
                    }
                    parameter = ParseReceiver();
                }
                else
                {
                    SafeCorePatternSyntax? pattern = ParsePattern();
                    if (pattern is null || !Expect(":")) { return null; }
                    SafeCoreTypeSyntax? type = ParseType();
                    parameter = type is null ? null : NewNode(new SafeCoreParameterSyntax(pattern, type, SpanFrom(start, type.Span.End)));
                }
                if (parameter is null) { return null; }
                parameters.Add(parameter with { Attributes = attributes, Span = SpanFrom(start, parameter.Span.End) });
                if (!TakeItemToken(",")) { break; }
            }
            if (!Expect(")")) { return null; }
            SafeCoreTypeSyntax? returnType = null;
            if (TakeItemToken("->"))
            {
                returnType = ParseType();
                if (returnType is null) { return null; }
            }
            SafeCoreWhereClauseSyntax? where = ParseWhereClause();
            return new ParsedFunctionSignature(name, generics, parameters.AsReadOnly(), returnType, where);
        }

        private bool IsReceiverStart()
        {
            if (At("self") || At("mut") && PeekItemText(1) == "self") { return true; }
            if (!At("&")) { return false; }
            int offset = 1;
            if (PeekItemText(offset) is { } next && next.StartsWith('\'')) { offset++; }
            if (PeekItemText(offset) == "mut") { offset++; }
            return PeekItemText(offset) == "self";
        }

        private SafeCoreParameterSyntax? ParseReceiver()
        {
            int start = CurrentStart;
            bool byReference = TakeItemToken("&");
            string? lifetime = byReference && IsLifetimeToken(Current) ? Consume()!.Text : null;
            bool mutable = TakeItemToken("mut");
            RustToken? self = ConsumeExpected("self");
            if (self is null) { return null; }
            SafeCoreTypeSyntax? explicitType = null;
            if (TakeItemToken(":"))
            {
                if (byReference) { ReportUnexpected("A borrowed self receiver cannot also declare an explicit type."); return null; }
                explicitType = ParseType();
                if (explicitType is null) { return null; }
            }
            SafeCoreTypeSyntax type = explicitType ?? NewNode(new SafeCorePathTypeSyntax(
                [NewNode(new SafeCorePathSegmentSyntax("Self", [], self.Span))], self.Span));
            if (byReference) { type = NewNode(new SafeCoreReferenceTypeSyntax(lifetime, mutable, type, SpanFrom(start, self.Span.End))); }
            TextSpan span = SpanFrom(start, PreviousEnd);
            return NewNode(new SafeCoreParameterSyntax(
                NewNode(new SafeCoreIdentifierPatternSyntax("self", mutable, self.Span)), type, span)
            {
                Receiver = NewNode(new SafeCoreReceiverSyntax(byReference, mutable, lifetime, explicitType, span)),
            });
        }

        private SafeCoreStructSyntax? ParseStruct(IReadOnlyList<SafeCoreAttributeSyntax> attributes, bool isPublic)
        {
            RustToken? start = ConsumeExpected("struct");
            if (start is null) { return null; }
            (string? name, _) = ConsumeName("struct name");
            if (name is null) { return null; }
            IReadOnlyList<SafeCoreGenericParameterSyntax> generics = ParseGenericParameters();
            SafeCoreWhereClauseSyntax? where = ParseWhereClause();
            bool tuple = At("(");
            bool unit = At(";");
            IReadOnlyList<SafeCoreFieldSyntax> fields;
            if (tuple)
            {
                if (where is not null) { ReportUnexpected("A tuple struct's where clause follows its field list."); }
                fields = ParseFields(named: false, allowVisibility: true);
                where = ParseWhereClause() ?? where;
                if (!Expect(";")) { return null; }
            }
            else if (At("{")) { fields = ParseFields(named: true, allowVisibility: true); }
            else if (unit) { fields = []; Consume(); }
            else { ReportExpected("'{', '(' or ';' after a struct declaration"); return null; }
            return NewNode(new SafeCoreStructSyntax(name, generics, fields, tuple, isPublic, attributes, SpanFrom(start, PreviousEnd))
            {
                IsUnitStruct = unit,
                WhereClause = where,
            });
        }

        private ReadOnlyCollection<SafeCoreFieldSyntax> ParseFields(bool named, bool allowVisibility)
        {
            string closing = named ? "}" : ")";
            if (!Expect(named ? "{" : "(")) { return new([]); }
            var fields = new List<SafeCoreFieldSyntax>();
            while (!AtEnd && !At(closing) && !_terminated && Step())
            {
                int start = CurrentStart;
                IReadOnlyList<SafeCoreAttributeSyntax> attributes = ParseAttributes();
                SafeCoreVisibilitySyntax visibility = ParseVisibilitySyntax();
                if (!allowVisibility && visibility.Kind != SafeCoreVisibilityKind.Private)
                {
                    ReportUnexpected("Enum variant fields cannot declare visibility.");
                }
                string? name = null;
                if (named)
                {
                    (name, _) = ConsumeName("field name");
                    if (name is null || !Expect(":")) { break; }
                }
                SafeCoreTypeSyntax? type = ParseType();
                if (type is null) { break; }
                fields.Add(NewNode(new SafeCoreFieldSyntax(name, type, visibility.Kind == SafeCoreVisibilityKind.Public, SpanFrom(start, type.Span.End))
                {
                    Visibility = visibility,
                    Attributes = attributes,
                }));
                if (!TakeItemToken(",")) { break; }
            }
            Expect(closing);
            return fields.AsReadOnly();
        }

        private SafeCoreEnumSyntax? ParseEnum(IReadOnlyList<SafeCoreAttributeSyntax> attributes, bool isPublic)
        {
            RustToken? start = ConsumeExpected("enum");
            if (start is null) { return null; }
            (string? name, _) = ConsumeName("enum name");
            if (name is null) { return null; }
            IReadOnlyList<SafeCoreGenericParameterSyntax> generics = ParseGenericParameters();
            SafeCoreWhereClauseSyntax? where = ParseWhereClause();
            if (!Expect("{")) { return null; }
            var variants = new List<SafeCoreEnumVariantSyntax>();
            while (!AtEnd && !At("}") && !_terminated && Step())
            {
                int variantStart = CurrentStart;
                IReadOnlyList<SafeCoreAttributeSyntax> variantAttributes = ParseAttributes();
                (string? variantName, _) = ConsumeName("enum variant name");
                if (variantName is null) { break; }
                SafeCoreEnumVariantKind kind = SafeCoreEnumVariantKind.Unit;
                IReadOnlyList<SafeCoreFieldSyntax> fields = [];
                if (At("(")) { kind = SafeCoreEnumVariantKind.Tuple; fields = ParseFields(named: false, allowVisibility: false); }
                else if (At("{")) { kind = SafeCoreEnumVariantKind.Struct; fields = ParseFields(named: true, allowVisibility: false); }
                SafeCoreExpressionSyntax? discriminant = null;
                if (TakeItemToken("="))
                {
                    discriminant = ParseExpression();
                    if (discriminant is null) { break; }
                }
                variants.Add(NewNode(new SafeCoreEnumVariantSyntax(variantName, fields, SpanFrom(variantStart, PreviousEnd))
                {
                    Kind = kind,
                    Discriminant = discriminant,
                    Attributes = variantAttributes,
                }));
                if (!TakeItemToken(",")) { break; }
            }
            if (!Expect("}")) { return null; }
            return NewNode(new SafeCoreEnumSyntax(name, generics, variants.AsReadOnly(), isPublic, attributes, SpanFrom(start, PreviousEnd)) { WhereClause = where });
        }

        private SafeCoreTypeAliasSyntax? ParseTypeAlias(IReadOnlyList<SafeCoreAttributeSyntax> attributes, bool isPublic)
        {
            RustToken? start = ConsumeExpected("type");
            if (start is null) { return null; }
            (string? name, _) = ConsumeName("type alias name");
            if (name is null) { return null; }
            IReadOnlyList<SafeCoreGenericParameterSyntax> generics = ParseGenericParameters();
            SafeCoreWhereClauseSyntax? where = ParseWhereClause();
            if (!Expect("=")) { return null; }
            SafeCoreTypeSyntax? type = ParseType();
            if (type is null || !Expect(";")) { return null; }
            return NewNode(new SafeCoreTypeAliasSyntax(name, generics, type, isPublic, attributes, SpanFrom(start, PreviousEnd)) { WhereClause = where });
        }

        private SafeCoreConstSyntax? ParseConst(IReadOnlyList<SafeCoreAttributeSyntax> attributes, bool isPublic)
        {
            RustToken? start = ConsumeExpected("const");
            if (start is null) { return null; }
            (string? name, _) = ConsumeConstName();
            if (name is null || !Expect(":")) { return null; }
            SafeCoreTypeSyntax? type = ParseType();
            if (type is null || !Expect("=")) { return null; }
            SafeCoreExpressionSyntax? value = ParseExpression();
            if (value is null || !Expect(";")) { return null; }
            return NewNode(new SafeCoreConstSyntax(name, type, value, isPublic, attributes, SpanFrom(start, PreviousEnd)));
        }

        private (string? Name, RustToken? Token) ConsumeConstName()
        {
            if (!At("_")) { return ConsumeName("constant name"); }
            RustToken token = Consume()!;
            return (token.Text, token);
        }

        private ReadOnlyCollection<SafeCoreGenericParameterSyntax> ParseGenericParameters()
        {
            if (!At("<")) { return new([]); }
            if (!EnterDepth()) { return new([]); }
            try
            {
                Consume();
                var parameters = new List<SafeCoreGenericParameterSyntax>();
                while (!AtEnd && !AtGreater && !_terminated && Step())
                {
                    int start = CurrentStart;
                    IReadOnlyList<SafeCoreAttributeSyntax> attributes = ParseAttributes();
                    SafeCoreGenericParameterKind kind;
                    string? name;
                    SafeCoreTypeSyntax? constType = null;
                    if (IsLifetimeToken(Current))
                    {
                        kind = SafeCoreGenericParameterKind.Lifetime;
                        name = Consume()!.Text;
                        if (name is "'static" or "'_") { ReportUnexpected("Reserved lifetimes cannot be declared as generic parameters."); }
                    }
                    else
                    {
                        kind = TakeItemToken("const") ? SafeCoreGenericParameterKind.Const : SafeCoreGenericParameterKind.Type;
                        (name, _) = ConsumeName("generic parameter name");
                        if (name is null) { break; }
                        if (kind == SafeCoreGenericParameterKind.Const)
                        {
                            if (!Expect(":")) { break; }
                            constType = ParseType();
                            if (constType is null) { break; }
                        }
                    }
                    IReadOnlyList<SafeCoreTypeBoundSyntax> bounds = [];
                    if (kind != SafeCoreGenericParameterKind.Const && TakeItemToken(":"))
                    {
                        bounds = ParseTypeBounds(lifetimesOnly: kind == SafeCoreGenericParameterKind.Lifetime);
                    }
                    SafeCoreTypeSyntax? defaultType = null;
                    SafeCoreExpressionSyntax? defaultValue = null;
                    if (TakeItemToken("="))
                    {
                        if (kind == SafeCoreGenericParameterKind.Lifetime) { ReportUnexpected("Lifetime parameters cannot have defaults."); break; }
                        if (kind == SafeCoreGenericParameterKind.Const)
                        {
                            defaultValue = ParseConstGenericValue();
                            if (defaultValue is null) { break; }
                        }
                        else
                        {
                            defaultType = ParseType();
                            if (defaultType is null) { break; }
                        }
                    }
                    parameters.Add(NewNode(new SafeCoreGenericParameterSyntax(name,
                        bounds.OfType<SafeCoreTraitBoundSyntax>().Select(static bound => bound.Type).ToArray(), SpanFrom(start, PreviousEnd))
                    {
                        Kind = kind,
                        Constraints = bounds,
                        ConstType = constType,
                        DefaultType = defaultType,
                        DefaultValue = defaultValue,
                        Attributes = attributes,
                    }));
                    if (!TakeItemToken(","))
                    {
                        if (!AtGreater) { ReportExpected("',' or '>' after a generic parameter"); }
                        break;
                    }
                }
                if (ConsumeGreater() is null) { ReportUnterminated("generic parameter list"); }
                return parameters.AsReadOnly();
            }
            finally { ExitDepth(); }
        }

        private SafeCoreWhereClauseSyntax? ParseWhereClause()
        {
            if (!At("where")) { return null; }
            RustToken start = Consume()!;
            var predicates = new List<SafeCoreWherePredicateSyntax>();
            while (!AtEnd && CurrentText is not ("{" or ";" or "=") && !_terminated && Step())
            {
                int predicateStart = CurrentStart;
                if (IsLifetimeToken(Current))
                {
                    RustToken lifetime = Consume()!;
                    if (!Expect(":")) { break; }
                    IReadOnlyList<SafeCoreTypeBoundSyntax> bounds = ParseTypeBounds(lifetimesOnly: true);
                    predicates.Add(NewNode(new SafeCoreLifetimeWherePredicateSyntax(lifetime.Text,
                        bounds.Cast<SafeCoreLifetimeBoundSyntax>().ToArray(), SpanFrom(predicateStart, PreviousEnd))));
                }
                else
                {
                    IReadOnlyList<SafeCoreGenericParameterSyntax> binder = ParseLifetimeBinder();
                    SafeCoreTypeSyntax? type = ParseType();
                    if (type is null || !Expect(":")) { break; }
                    IReadOnlyList<SafeCoreTypeBoundSyntax> bounds = ParseTypeBounds();
                    predicates.Add(NewNode(new SafeCoreTypeWherePredicateSyntax(binder, type, bounds, SpanFrom(predicateStart, PreviousEnd))));
                }
                if (!TakeItemToken(",")) { break; }
            }
            return NewNode(new SafeCoreWhereClauseSyntax(predicates.AsReadOnly(), SpanFrom(start, PreviousEnd)));
        }

        private SafeCoreTraitSyntax? ParseTrait(IReadOnlyList<SafeCoreAttributeSyntax> attributes, bool isPublic)
        {
            RustToken? start = ConsumeExpected("trait");
            if (start is null) { return null; }
            (string? name, _) = ConsumeName("trait name");
            if (name is null) { return null; }
            IReadOnlyList<SafeCoreGenericParameterSyntax> generics = ParseGenericParameters();
            IReadOnlyList<SafeCoreTypeBoundSyntax> bounds = TakeItemToken(":") ? ParseTypeBounds() : [];
            SafeCoreWhereClauseSyntax? where = ParseWhereClause();
            if (!Expect("{")) { return null; }
            (IReadOnlyList<SafeCoreAssociatedItemSyntax> items, IReadOnlyList<SafeCoreAttributeSyntax> inner) = ParseAssociatedItems(isTrait: true, isTraitImplementation: false);
            if (!Expect("}")) { return null; }
            return NewNode(new SafeCoreTraitSyntax(name, generics, bounds, where, items, isPublic, attributes, SpanFrom(start, PreviousEnd)) { InnerAttributes = inner });
        }

        private SafeCoreImplSyntax? ParseImpl(IReadOnlyList<SafeCoreAttributeSyntax> attributes, bool isPublic)
        {
            RustToken? start = ConsumeExpected("impl");
            if (start is null) { return null; }
            if (isPublic) { ReportUnexpected("An implementation cannot declare visibility."); }
            IReadOnlyList<SafeCoreGenericParameterSyntax> generics = ParseGenericParameters();
            if (At("!")) { ReportUnsupported("negative trait implementations"); return null; }
            SafeCoreTypeSyntax? type = ParseType();
            if (type is null) { return null; }
            SafeCoreTypeSyntax? trait = null;
            if (TakeItemToken("for"))
            {
                trait = type;
                if (trait is not SafeCorePathTypeSyntax) { ReportExpected("a trait path before 'for'"); return null; }
                type = ParseType();
                if (type is null) { return null; }
            }
            SafeCoreWhereClauseSyntax? where = ParseWhereClause();
            if (!Expect("{")) { return null; }
            (IReadOnlyList<SafeCoreAssociatedItemSyntax> items, IReadOnlyList<SafeCoreAttributeSyntax> inner) = ParseAssociatedItems(isTrait: false, isTraitImplementation: trait is not null);
            if (!Expect("}")) { return null; }
            return NewNode(new SafeCoreImplSyntax(generics, trait, type, where, items, attributes, SpanFrom(start, PreviousEnd)) { InnerAttributes = inner });
        }

        private (IReadOnlyList<SafeCoreAssociatedItemSyntax> Items, IReadOnlyList<SafeCoreAttributeSyntax> InnerAttributes) ParseAssociatedItems(bool isTrait, bool isTraitImplementation)
        {
            IReadOnlyList<SafeCoreAttributeSyntax> leading = ParseAttributes(allowInner: true);
            SafeCoreAttributeSyntax[] pending = leading.Where(static attribute => !attribute.IsInner).ToArray();
            IReadOnlyList<SafeCoreAttributeSyntax> inner = leading.Where(static attribute => attribute.IsInner).ToArray();
            var items = new List<SafeCoreAssociatedItemSyntax>();
            while (!AtEnd && !At("}") && !_terminated && Step())
            {
                IReadOnlyList<SafeCoreAttributeSyntax> attributes = pending.Length != 0 ? pending : ParseAttributes();
                pending = [];
                SafeCoreVisibilitySyntax visibility = ParseVisibilitySyntax();
                if ((isTrait || isTraitImplementation) && visibility.Kind != SafeCoreVisibilityKind.Private)
                {
                    ReportUnexpected("Trait associated items cannot declare visibility.");
                }
                SafeCoreAssociatedItemSyntax? item = CurrentText switch
                {
                    "fn" => ParseAssociatedFunction(attributes, visibility, isTrait),
                    "const" when PeekItemText(1) == "fn" => ParseAssociatedFunction(attributes, visibility, isTrait),
                    "const" => ParseAssociatedConst(attributes, visibility, isTrait),
                    "type" => ParseAssociatedType(attributes, visibility, isTrait),
                    _ => null,
                };
                if (item is null)
                {
                    if (CurrentText is "unsafe" or "extern" or "async" or "default" or "macro_rules") { ReportUnsupported($"associated item '{CurrentText}'"); }
                    else { ReportExpected("an associated function, type, or constant"); }
                    break;
                }
                items.Add(item);
            }
            if (pending.Length != 0) { ReportExpected("an associated item after its attributes"); }
            return (items.AsReadOnly(), inner);
        }

        private SafeCoreAssociatedFunctionSyntax? ParseAssociatedFunction(IReadOnlyList<SafeCoreAttributeSyntax> attributes, SafeCoreVisibilitySyntax visibility, bool isTrait)
        {
            int start = CurrentStart;
            bool isConst = TakeItemToken("const");
            ParsedFunctionSignature? signature = ParseFunctionSignature(allowReceiver: true);
            if (signature is null) { return null; }
            SafeCoreBlockSyntax? body = null;
            if (At(";"))
            {
                if (!isTrait) { ReportExpected("a body for an implementation method"); }
                Consume();
            }
            else
            {
                body = ParseBlock();
                if (body is null) { return null; }
            }
            return NewNode(new SafeCoreAssociatedFunctionSyntax(signature.Name, signature.GenericParameters, signature.Parameters,
                signature.ReturnType, signature.WhereClause, body, isConst, attributes, visibility, SpanFrom(start, PreviousEnd)));
        }

        private SafeCoreAssociatedTypeSyntax? ParseAssociatedType(IReadOnlyList<SafeCoreAttributeSyntax> attributes, SafeCoreVisibilitySyntax visibility, bool isTrait)
        {
            int start = CurrentStart;
            Consume();
            (string? name, _) = ConsumeName("associated type name");
            if (name is null) { return null; }
            IReadOnlyList<SafeCoreGenericParameterSyntax> generics = ParseGenericParameters();
            IReadOnlyList<SafeCoreTypeBoundSyntax> bounds = TakeItemToken(":") ? ParseTypeBounds() : [];
            SafeCoreWhereClauseSyntax? where = ParseWhereClause();
            SafeCoreTypeSyntax? type = null;
            if (TakeItemToken("="))
            {
                type = ParseType();
                if (type is null) { return null; }
                SafeCoreWhereClauseSyntax? trailingWhere = ParseWhereClause();
                if (where is not null && trailingWhere is not null) { ReportUnexpected("An associated type cannot repeat its where clause."); }
                where ??= trailingWhere;
            }
            else if (!isTrait) { ReportExpected("'=' and an assigned implementation type"); }
            if (!Expect(";")) { return null; }
            return NewNode(new SafeCoreAssociatedTypeSyntax(name, generics, bounds, where, type, attributes, visibility, SpanFrom(start, PreviousEnd)));
        }

        private SafeCoreAssociatedConstSyntax? ParseAssociatedConst(IReadOnlyList<SafeCoreAttributeSyntax> attributes, SafeCoreVisibilitySyntax visibility, bool isTrait)
        {
            int start = CurrentStart;
            Consume();
            (string? name, _) = ConsumeName("associated constant name");
            if (name is null || !Expect(":")) { return null; }
            SafeCoreTypeSyntax? type = ParseType();
            if (type is null) { return null; }
            SafeCoreExpressionSyntax? value = null;
            if (TakeItemToken("="))
            {
                value = ParseExpression();
                if (value is null) { return null; }
            }
            else if (!isTrait) { ReportExpected("'=' and an implementation constant value"); }
            if (!Expect(";")) { return null; }
            return NewNode(new SafeCoreAssociatedConstSyntax(name, type, value, attributes, visibility, SpanFrom(start, PreviousEnd)));
        }

        private sealed record ParsedFunctionSignature(
            string Name,
            IReadOnlyList<SafeCoreGenericParameterSyntax> GenericParameters,
            IReadOnlyList<SafeCoreParameterSyntax> Parameters,
            SafeCoreTypeSyntax? ReturnType,
            SafeCoreWhereClauseSyntax? WhereClause);
    }
}
