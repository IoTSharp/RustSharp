using System.Diagnostics;
using System.Globalization;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Conformance;
/// <summary>Writes a stable, span-bearing syntax snapshot without reflection.</summary>
internal static class SyntaxAstSnapshotWriter
{
    public static string Write(SafeCoreCompilationUnitSyntax root, CancellationToken cancellationToken = default)
    {
        var writer = new Writer(cancellationToken);
        writer.Unit(root);
        return writer.Text.ToString();
    }

    private sealed class Writer(CancellationToken cancellationToken)
    {
        public StringBuilder Text { get; } = new();

        private int _depth;
        private int _lineCount;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private const int MaximumLines = 1_000_000;
        private const int MaximumCharacters = 512 * 1024;
        private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(10);
        private void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_lineCount >= MaximumLines || Text.Length > MaximumCharacters || _clock.Elapsed > MaximumDuration)
            {
                throw new InvalidDataException("AST snapshot exceeded its bounded output or wall-clock budget.");
            }
        }

        private void Line(string kind, TextSpan span, params string[] fields)
        {
            CheckBudget();
            _lineCount++;
            Text.Append(' ', _depth * 2).Append(kind).Append('@').Append(span.Start.ToString(CultureInfo.InvariantCulture)).Append(':').Append(span.Length.ToString(CultureInfo.InvariantCulture));
            foreach (string field in fields)
                Text.Append(' ').Append(field);
            Text.Append('\n');
            CheckBudget();
        }

        private void Children(Action action)
        {
            CheckBudget();
            if (_depth >= 256)
                throw new InvalidDataException("AST snapshot exceeded its nesting budget.");
            _depth++;
            try
            {
                action();
            }
            finally
            {
                _depth--;
            }
        }

        private static string Q(string? value) => value is null ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        private static string B(bool value) => value ? "1" : "0";
        private void Attrs(IReadOnlyList<SafeCoreAttributeSyntax> attrs)
        {
            foreach (var a in attrs)
                Line("attribute", a.Span, $"inner={B(a.IsInner)}", $"doc={B(a.IsDocumentation)}", $"path={Q(a.Path)}", $"args={Q(a.ArgumentsText)}", $"text={Q(a.DocumentationText)}");
        }

        public void Unit(SafeCoreCompilationUnitSyntax n)
        {
            Line("unit", n.Span);
            Children(() =>
            {
                Attrs(n.Attributes);
                foreach (var i in n.Items)
                    Item(i);
            });
        }

        private void Visibility(SafeCoreVisibilitySyntax v) => Line("visibility", v.Span, $"kind={v.Kind}", $"path={Q(v.Path)}");
        private void Item(SafeCoreItemSyntax n)
        {
            switch (n)
            {
                case SafeCoreModuleSyntax x:
                    Line("module", x.Span, $"name={Q(x.Name)}", $"external={B(x.IsExternal)}");
                    Children(() =>
                    {
                        Visibility(x.Visibility);
                        Attrs(x.Attributes);
                        Attrs(x.InnerAttributes);
                        foreach (var i in x.Items)
                            Item(i);
                    });
                    break;
                case SafeCoreUseSyntax x:
                    Line("use", x.Span, $"path={Q(x.Path)}", $"alias={Q(x.Alias)}");
                    Children(() =>
                    {
                        Visibility(x.Visibility);
                        Attrs(x.Attributes);
                        if (x.Tree is not null)
                            UseTree(x.Tree);
                    });
                    break;
                case SafeCoreFunctionSyntax x:
                    Line("function", x.Span, $"name={Q(x.Name)}", $"const={B(x.IsConst)}");
                    Children(() =>
                    {
                        Visibility(x.Visibility);
                        Attrs(x.Attributes);
                        Generics(x.GenericParameters);
                        Parameters(x.Parameters);
                        Type(x.ReturnType);
                        Where(x.WhereClause);
                        Block(x.Body);
                    });
                    break;
                case SafeCoreStructSyntax x:
                    Line("struct", x.Span, $"name={Q(x.Name)}", $"tuple={B(x.IsTupleStruct)}", $"unit={B(x.IsUnitStruct)}");
                    Children(() =>
                    {
                        Visibility(x.Visibility);
                        Attrs(x.Attributes);
                        Generics(x.GenericParameters);
                        Where(x.WhereClause);
                        foreach (var f in x.Fields)
                            Field(f);
                    });
                    break;
                case SafeCoreEnumSyntax x:
                    Line("enum", x.Span, $"name={Q(x.Name)}");
                    Children(() =>
                    {
                        Visibility(x.Visibility);
                        Attrs(x.Attributes);
                        Generics(x.GenericParameters);
                        Where(x.WhereClause);
                        foreach (var v in x.Variants)
                            Variant(v);
                    });
                    break;
                case SafeCoreTypeAliasSyntax x:
                    Line("type-alias", x.Span, $"name={Q(x.Name)}");
                    Children(() =>
                    {
                        Visibility(x.Visibility);
                        Attrs(x.Attributes);
                        Generics(x.GenericParameters);
                        Where(x.WhereClause);
                        Type(x.Type);
                    });
                    break;
                case SafeCoreConstSyntax x:
                    Line("const", x.Span, $"name={Q(x.Name)}");
                    Children(() =>
                    {
                        Visibility(x.Visibility);
                        Attrs(x.Attributes);
                        Type(x.Type);
                        Expr(x.Value);
                    });
                    break;
                case SafeCoreTraitSyntax x:
                    Line("trait", x.Span, $"name={Q(x.Name)}");
                    Children(() =>
                    {
                        Visibility(x.Visibility);
                        Attrs(x.Attributes);
                        Attrs(x.InnerAttributes);
                        Generics(x.GenericParameters);
                        foreach (var b in x.Bounds)
                            Bound(b);
                        Where(x.WhereClause);
                        foreach (var i in x.Items)
                            Associated(i);
                    });
                    break;
                case SafeCoreImplSyntax x:
                    Line("impl", x.Span);
                    Children(() =>
                    {
                        Visibility(x.Visibility);
                        Attrs(x.Attributes);
                        Attrs(x.InnerAttributes);
                        Generics(x.GenericParameters);
                        Type(x.Trait);
                        Type(x.SelfType);
                        Where(x.WhereClause);
                        foreach (var i in x.Items)
                            Associated(i);
                    });
                    break;
                default:
                    throw new InvalidDataException($"Snapshot writer does not support item {n.GetType().Name}.");
            }
        }

        private void UseTree(SafeCoreUseTreeSyntax x)
        {
            Line("use-tree", x.Span, $"kind={x.Kind}", $"absolute={B(x.IsAbsolute)}", $"prefix={Q(string.Join("::", x.Prefix))}", $"alias={Q(x.Alias)}");
            Children(() =>
            {
                foreach (var c in x.Children)
                    UseTree(c);
            });
        }

        private void Field(SafeCoreFieldSyntax x)
        {
            Line("field", x.Span, $"name={Q(x.Name)}", $"public={B(x.IsPublic)}");
            Children(() =>
            {
                if (x.Visibility is not null)
                    Visibility(x.Visibility);
                Attrs(x.Attributes);
                Type(x.Type);
            });
        }

        private void Variant(SafeCoreEnumVariantSyntax x)
        {
            Line("variant", x.Span, $"name={Q(x.Name)}", $"kind={x.Kind}");
            Children(() =>
            {
                Attrs(x.Attributes);
                foreach (var f in x.Fields)
                    Field(f);
                Expr(x.Discriminant);
            });
        }

        private void Generics(IReadOnlyList<SafeCoreGenericParameterSyntax> xs)
        {
            foreach (var x in xs)
            {
                Line("generic", x.Span, $"name={Q(x.Name)}", $"kind={x.Kind}");
                Children(() =>
                {
                    Attrs(x.Attributes);
                    if (x.Constraints.Count != 0)
                        foreach (var c in x.Constraints)
                            Bound(c);
                    else
                        foreach (var b in x.Bounds)
                            Type(b);
                    Type(x.ConstType);
                    Type(x.DefaultType);
                    Expr(x.DefaultValue);
                });
            }
        }

        private void Parameters(IReadOnlyList<SafeCoreParameterSyntax> xs)
        {
            foreach (var x in xs)
            {
                Line("parameter", x.Span);
                Children(() =>
                {
                    Attrs(x.Attributes);
                    Pattern(x.Pattern);
                    Type(x.Type);
                    if (x.Receiver is not null)
                    {
                        Line("receiver", x.Receiver.Span, $"ref={B(x.Receiver.IsByReference)}", $"mut={B(x.Receiver.IsMutable)}", $"life={Q(x.Receiver.Lifetime)}");
                        Children(() => Type(x.Receiver.ExplicitType));
                    }
                });
            }
        }

        private void Where(SafeCoreWhereClauseSyntax? x)
        {
            if (x is null)
                return;
            Line("where", x.Span);
            Children(() =>
            {
                foreach (var p in x.Predicates)
                {
                    Line("where-predicate", p.Span);
                    Children(() =>
                    {
                        switch (p)
                        {
                            case SafeCoreLifetimeWherePredicateSyntax l:
                                Line("lifetime", l.Span, $"name={Q(l.Lifetime)}");
                                foreach (var b in l.Bounds)
                                    Bound(b);
                                break;
                            case SafeCoreTypeWherePredicateSyntax t:
                                Generics(t.GenericParameters);
                                Type(t.Type);
                                foreach (var b in t.Bounds)
                                    Bound(b);
                                break;
                            default:
                                throw new InvalidDataException($"Unknown where predicate {p.GetType().Name}");
                        }
                    });
                }
            });
        }

        private void Associated(SafeCoreAssociatedItemSyntax x)
        {
            Line("associated", x.Span, $"name={Q(x.Name)}", $"visibility={x.Visibility.Kind}", $"visibility-path={Q(x.Visibility.Path)}", $"kind={x switch {SafeCoreAssociatedFunctionSyntax => "function", SafeCoreAssociatedTypeSyntax => "type", SafeCoreAssociatedConstSyntax => "const", _ => throw new InvalidDataException($"Unknown associated item {x.GetType().Name}")}}");
            Children(() =>
            {
                Visibility(x.Visibility);
                Attrs(x.Attributes);
                switch (x)
                {
                    case SafeCoreAssociatedFunctionSyntax f:
                        Line("associated-function", f.Span, $"const={B(f.IsConst)}");
                        Children(() =>
                        {
                            Generics(f.GenericParameters);
                            Parameters(f.Parameters);
                            Type(f.ReturnType);
                            Where(f.WhereClause);
                            Block(f.Body);
                        });
                        break;
                    case SafeCoreAssociatedTypeSyntax t:
                        Generics(t.GenericParameters);
                        foreach (var b in t.Bounds)
                            Bound(b);
                        Where(t.WhereClause);
                        Type(t.Type);
                        break;
                    case SafeCoreAssociatedConstSyntax c:
                        Type(c.Type);
                        Expr(c.Value);
                        break;
                    default:
                        throw new InvalidDataException($"Unknown associated item {x.GetType().Name}");
                }
            });
        }

        private void Bound(SafeCoreTypeBoundSyntax x)
        {
            switch (x)
            {
                case SafeCoreLifetimeBoundSyntax l:
                    Line("lifetime-bound", l.Span, $"name={Q(l.Lifetime)}");
                    break;
                case SafeCoreTraitBoundSyntax t:
                    Line("trait-bound", t.Span, $"optional={B(t.IsOptional)}");
                    Children(() =>
                    {
                        Generics(t.GenericParameters);
                        Type(t.Type);
                    });
                    break;
                default:
                    throw new InvalidDataException($"Unknown bound {x.GetType().Name}");
            }
        }

        private void Type(SafeCoreTypeSyntax? x)
        {
            if (x is null)
                return;
            switch (x)
            {
                case SafeCorePathTypeSyntax p:
                    Line("path-type", p.Span, $"absolute={B(p.IsAbsolute)}");
                    Children(() =>
                    {
                        foreach (var s in p.Segments)
                        {
                            Line("segment", s.Span, $"name={Q(s.Name)}", $"fn={B(s.HasFunctionArguments)}", $"generic={B(s.HasGenericArguments)}");
                            Children(() =>
                            {
                                foreach (var a in s.Arguments)
                                    Arg(a);
                                if (s.Arguments.Count == 0)
                                    foreach (var a in s.GenericArguments)
                                        Type(a);
                                foreach (var a in s.FunctionParameters)
                                    Type(a);
                                Type(s.FunctionReturnType);
                            });
                        }
                    });
                    break;
                case SafeCoreReferenceTypeSyntax r:
                    Line("reference-type", r.Span, $"mut={B(r.IsMutable)}", $"life={Q(r.Lifetime)}");
                    Children(() => Type(r.Inner));
                    break;
                case SafeCoreTupleTypeSyntax t:
                    Line("tuple-type", t.Span, $"trailing={B(t.HasTrailingComma)}");
                    Children(() =>
                    {
                        foreach (var e in t.Elements)
                            Type(e);
                    });
                    break;
                case SafeCoreArrayTypeSyntax a:
                    Line("array-type", a.Span);
                    Children(() =>
                    {
                        Type(a.Element);
                        Expr(a.Length);
                    });
                    break;
                case SafeCoreSliceTypeSyntax s:
                    Line("slice-type", s.Span);
                    Children(() => Type(s.Element));
                    break;
                case SafeCoreUnitTypeSyntax:
                    Line("unit-type", x.Span);
                    break;
                case SafeCoreNeverTypeSyntax:
                    Line("never-type", x.Span);
                    break;
                case SafeCoreFunctionTypeSyntax f:
                    Line("function-type", f.Span);
                    Children(() =>
                    {
                        Generics(f.GenericParameters);
                        foreach (var p in f.Parameters)
                        {
                            Line("function-parameter", p.Span, $"name={Q(p.Name)}");
                            Children(() =>
                            {
                                Attrs(p.Attributes);
                                Type(p.Type);
                            });
                        }

                        Type(f.ReturnType);
                    });
                    break;
                case SafeCoreInferredTypeSyntax:
                    Line("inferred-type", x.Span);
                    break;
                case SafeCoreBoundedTypeSyntax b:
                    Line("bounded-type", b.Span, $"dyn={B(b.IsDynamic)}");
                    Children(() =>
                    {
                        foreach (var z in b.Bounds)
                            Bound(z);
                    });
                    break;
                case SafeCoreQualifiedPathTypeSyntax q:
                    Line("qualified-path-type", q.Span);
                    Children(() =>
                    {
                        Type(q.SelfType);
                        Type(q.Trait);
                        foreach (var s in q.Segments)
                        {
                            Line("segment", s.Span, $"name={Q(s.Name)}", $"fn={B(s.HasFunctionArguments)}", $"generic={B(s.HasGenericArguments)}");
                            foreach (var a in s.Arguments)
                                Arg(a);
                            if (s.Arguments.Count == 0)
                                foreach (var a in s.GenericArguments)
                                    Type(a);
                            foreach (var a in s.FunctionParameters)
                                Type(a);
                            Type(s.FunctionReturnType);
                        }
                    });
                    break;
                default:
                    throw new InvalidDataException($"Unknown type {x.GetType().Name}");
            }
        }

        private void Arg(SafeCoreGenericArgumentSyntax x)
        {
            switch (x)
            {
                case SafeCoreTypeArgumentSyntax t:
                    Line("type-arg", t.Span);
                    Children(() => Type(t.Type));
                    break;
                case SafeCoreLifetimeArgumentSyntax l:
                    Line("lifetime-arg", l.Span, $"name={Q(l.Lifetime)}");
                    break;
                case SafeCoreConstArgumentSyntax c:
                    Line("const-arg", c.Span);
                    Children(() => Expr(c.Value));
                    break;
                case SafeCoreAssociatedTypeArgumentSyntax a:
                    Line("associated-type-arg", a.Span, $"name={Q(a.Name)}");
                    Children(() =>
                    {
                        foreach (var z in a.Arguments)
                            Arg(z);
                        Type(a.Type);
                    });
                    break;
                case SafeCoreAssociatedConstraintArgumentSyntax a:
                    Line("associated-constraint-arg", a.Span, $"name={Q(a.Name)}");
                    Children(() =>
                    {
                        foreach (var z in a.Arguments)
                            Arg(z);
                        foreach (var b in a.Bounds)
                            Bound(b);
                    });
                    break;
                default:
                    throw new InvalidDataException($"Unknown generic arg {x.GetType().Name}");
            }
        }

        private void Block(SafeCoreBlockSyntax? x)
        {
            if (x is null)
                return;
            Line("block", x.Span);
            Children(() =>
            {
                Attrs(x.Attributes);
                foreach (var s in x.Statements)
                    Statement(s);
                Expr(x.TailExpression);
            });
        }

        private void Statement(SafeCoreStatementSyntax x)
        {
            Line("statement", x.Span, $"kind={x.Kind}");
            Children(() =>
            {
                Attrs(x.Attributes);
                switch (x)
                {
                    case SafeCoreLetStatementSyntax l:
                        Pattern(l.Pattern);
                        Type(l.Type);
                        Expr(l.Initializer);
                        Block(l.ElseBlock);
                        break;
                    case SafeCoreReturnStatementSyntax r:
                        Expr(r.Value);
                        break;
                    case SafeCoreExpressionStatementSyntax e:
                        Line("semicolon", e.Span, $"value={B(e.HasSemicolon)}");
                        Expr(e.Expression);
                        break;
                    case SafeCoreItemStatementSyntax i:
                        Item(i.Item);
                        break;
                    case SafeCoreEmptyStatementSyntax:
                        break;
                    default:
                        throw new InvalidDataException($"Unknown statement {x.GetType().Name}");
                }
            });
        }

        private void Expr(SafeCoreExpressionSyntax? x)
        {
            if (x is null)
                return;
            Attrs(x.Attributes);
            if (x is SafeCoreQualifiedNameExpressionSyntax q)
            {
                Line("qualified-name", q.Span);
                Children(() =>
                {
                    Type(q.SelfType);
                    Type(q.TraitType);
                    Expr(q.Path);
                });
                return;
            }

            switch (x)
            {
                case SafeCorePrintExpressionSyntax p:
                    Line("print", p.Span);
                    Children(() =>
                    {
                        foreach (var a in p.Arguments)
                            Expr(a);
                    });
                    break;
                case SafeCoreNameExpressionSyntax n:
                    Line("name", n.Span, $"path={Q(n.Path)}");
                    Children(() =>
                    {
                        foreach (var s in n.Segments)
                        {
                            Line("path-segment", s.Span, $"name={Q(s.Name)}", $"generic={B(s.HasGenericArguments)}");
                            foreach (var a in s.GenericArguments)
                                Arg(a);
                        }
                    });
                    break;
                case SafeCoreLiteralExpressionSyntax l:
                    Line("literal", l.Span, $"kind={l.LiteralKind}", $"raw={Q(l.RawText)}");
                    break;
                case SafeCoreUnaryExpressionSyntax u:
                    Line("unary", u.Span, $"op={Q(u.Operator)}");
                    Children(() => Expr(u.Operand));
                    break;
                case SafeCoreBinaryExpressionSyntax b:
                    Line("binary", b.Span, $"op={Q(b.Operator)}");
                    Children(() =>
                    {
                        Expr(b.Left);
                        Expr(b.Right);
                    });
                    break;
                case SafeCoreCallExpressionSyntax c:
                    Line("call", c.Span);
                    Children(() =>
                    {
                        Expr(c.Callee);
                        foreach (var a in c.Arguments)
                            Expr(a);
                    });
                    break;
                case SafeCoreTupleExpressionSyntax t:
                    Line("tuple", t.Span, $"trailing={B(t.HasTrailingComma)}");
                    Children(() =>
                    {
                        foreach (var a in t.Elements)
                            Expr(a);
                    });
                    break;
                case SafeCoreArrayExpressionSyntax a:
                    Line("array", a.Span);
                    Children(() =>
                    {
                        foreach (var e in a.Elements)
                            Expr(e);
                        Expr(a.RepeatCount);
                    });
                    break;
                case SafeCoreBlockExpressionSyntax b:
                    Line("block-expr", b.Span);
                    Children(() => Block(b.Block));
                    break;
                case SafeCoreIfExpressionSyntax i:
                    Line("if", i.Span);
                    Children(() =>
                    {
                        Expr(i.Condition);
                        Block(i.Then);
                        Expr(i.Else);
                    });
                    break;
                case SafeCoreIndexExpressionSyntax i:
                    Line("index", i.Span);
                    Children(() =>
                    {
                        Expr(i.Target);
                        Expr(i.Index);
                    });
                    break;
                case SafeCoreStructExpressionSyntax s:
                    Line("struct-expr", s.Span);
                    Children(() =>
                    {
                        if (s.Qualifier is not null)
                            Expr(s.Qualifier);
                        Expr(s.Path);
                        foreach (var f in s.Fields)
                        {
                            Line("struct-field", f.Span, $"name={Q(f.Name)}", $"short={B(f.IsShorthand)}");
                            Expr(f.Value);
                            Attrs(f.Attributes);
                        }

                        Expr(s.Base);
                    });
                    break;
                case SafeCoreMemberExpressionSyntax m:
                    Line("member", m.Span, $"name={Q(m.Member)}", $"generic={B(m.HasGenericArguments)}");
                    Children(() =>
                    {
                        Expr(m.Target);
                        foreach (var a in m.GenericArguments)
                            Arg(a);
                    });
                    break;
                case SafeCoreCastExpressionSyntax c:
                    Line("cast", c.Span);
                    Children(() =>
                    {
                        Expr(c.Expression);
                        Type(c.Type);
                    });
                    break;
                case SafeCoreRangeExpressionSyntax r:
                    Line("range", r.Span, $"inclusive={B(r.IsInclusive)}", $"start={B(r.Start is not null)}", $"end={B(r.End is not null)}");
                    Children(() =>
                    {
                        Expr(r.Start);
                        Expr(r.End);
                    });
                    break;
                case SafeCoreMatchExpressionSyntax m:
                    Line("match", m.Span);
                    Children(() =>
                    {
                        Expr(m.Scrutinee);
                        foreach (var a in m.Arms)
                        {
                            Line("arm", a.Span);
                            Children(() =>
                            {
                                Attrs(a.Attributes);
                                Pattern(a.Pattern);
                                Expr(a.Guard);
                                Expr(a.Body);
                            });
                        }
                    });
                    break;
                case SafeCoreLoopExpressionSyntax l:
                    Line("loop", l.Span, $"label={Q(l.Label)}");
                    Children(() => Block(l.Body));
                    break;
                case SafeCoreWhileExpressionSyntax w:
                    Line("while", w.Span, $"label={Q(w.Label)}");
                    Children(() =>
                    {
                        Expr(w.Condition);
                        Block(w.Body);
                    });
                    break;
                case SafeCoreForExpressionSyntax f:
                    Line("for", f.Span, $"label={Q(f.Label)}");
                    Children(() =>
                    {
                        Pattern(f.Pattern);
                        Expr(f.Iterator);
                        Block(f.Body);
                    });
                    break;
                case SafeCoreClosureExpressionSyntax c:
                    Line("closure", c.Span, $"move={B(c.IsMove)}");
                    Children(() =>
                    {
                        foreach (var p in c.Parameters)
                        {
                            Line("closure-parameter", p.Span);
                            Pattern(p.Pattern);
                            Type(p.Type);
                            Attrs(p.Attributes);
                        }

                        Type(c.ReturnType);
                        Expr(c.Body);
                    });
                    break;
                case SafeCoreReturnExpressionSyntax r:
                    Line("return-expr", r.Span);
                    Expr(r.Value);
                    break;
                case SafeCoreBreakExpressionSyntax b:
                    Line("break-expr", b.Span, $"label={Q(b.Label)}");
                    Expr(b.Value);
                    break;
                case SafeCoreContinueExpressionSyntax c:
                    Line("continue-expr", c.Span, $"label={Q(c.Label)}");
                    break;
                case SafeCoreTryExpressionSyntax t:
                    Line("try", t.Span);
                    Expr(t.Operand);
                    break;
                case SafeCoreLetExpressionSyntax l:
                    Line("let-expr", l.Span);
                    Children(() =>
                    {
                        Pattern(l.Pattern);
                        Expr(l.Value);
                    });
                    break;
                case SafeCoreLabeledBlockExpressionSyntax l:
                    Line("labeled-block", l.Span, $"label={Q(l.Label)}");
                    Block(l.Block);
                    break;
                case SafeCoreConstBlockExpressionSyntax c:
                    Line("const-block", c.Span);
                    Block(c.Block);
                    break;
                default:
                    throw new InvalidDataException($"Unknown expression {x.GetType().Name}");
            }
        }

        private void Pattern(SafeCorePatternSyntax? x)
        {
            if (x is null)
                return;
            switch (x)
            {
                case SafeCoreIdentifierPatternSyntax i:
                    Line("identifier-pattern", i.Span, $"name={Q(i.Name)}", $"mut={B(i.IsMutable)}", $"ref={B(i.IsByReference)}");
                    break;
                case SafeCoreWildcardPatternSyntax:
                    Line("wildcard-pattern", x.Span);
                    break;
                case SafeCoreLiteralPatternSyntax l:
                    Line("literal-pattern", l.Span, $"kind={l.LiteralKind}", $"raw={Q(l.RawText)}");
                    break;
                case SafeCoreTuplePatternSyntax t:
                    Line("tuple-pattern", t.Span, $"trailing={B(t.HasTrailingComma)}");
                    Children(() =>
                    {
                        foreach (var p in t.Elements)
                            Pattern(p);
                    });
                    break;
                case SafeCorePathPatternSyntax p:
                    Line("path-pattern", p.Span, $"path={Q(p.Path)}", $"arguments={B(p.HasArguments)}");
                    Children(() =>
                    {
                        if (p.Qualifier is not null)
                            Expr(p.Qualifier);
                        foreach (var s in p.Segments)
                        {
                            Line("path-segment", s.Span, $"name={Q(s.Name)}", $"generic={B(s.HasGenericArguments)}");
                            Children(() =>
                            {
                                foreach (var a in s.GenericArguments)
                                    Arg(a);
                            });
                        }

                        foreach (var a in p.Arguments)
                            Pattern(a);
                    });
                    break;
                case SafeCoreReferencePatternSyntax r:
                    Line("reference-pattern", r.Span, $"mut={B(r.IsMutable)}");
                    Pattern(r.Pattern);
                    break;
                case SafeCoreSlicePatternSyntax s:
                    Line("slice-pattern", s.Span);
                    Children(() =>
                    {
                        foreach (var p in s.Elements)
                            Pattern(p);
                    });
                    break;
                case SafeCoreRestPatternSyntax:
                    Line("rest-pattern", x.Span);
                    break;
                case SafeCoreAtPatternSyntax a:
                    Line("at-pattern", a.Span);
                    Children(() =>
                    {
                        Pattern(a.Binding);
                        Pattern(a.Pattern);
                    });
                    break;
                case SafeCoreOrPatternSyntax o:
                    Line("or-pattern", o.Span);
                    Children(() =>
                    {
                        foreach (var p in o.Alternatives)
                            Pattern(p);
                    });
                    break;
                case SafeCoreRangePatternSyntax r:
                    Line("range-pattern", r.Span, $"inclusive={B(r.IsInclusive)}", $"start={B(r.Start is not null)}", $"end={B(r.End is not null)}");
                    Children(() =>
                    {
                        Pattern(r.Start);
                        Pattern(r.End);
                    });
                    break;
                case SafeCoreStructPatternSyntax s:
                    Line("struct-pattern", s.Span, $"path={Q(s.Path)}", $"rest={B(s.HasRest)}");
                    Children(() =>
                    {
                        if (s.Qualifier is not null)
                            Expr(s.Qualifier);
                        foreach (var segment in s.Segments)
                        {
                            Line("path-segment", segment.Span, $"name={Q(segment.Name)}", $"generic={B(segment.HasGenericArguments)}");
                            Children(() =>
                            {
                                foreach (var argument in segment.GenericArguments)
                                    Arg(argument);
                            });
                        }

                        foreach (var f in s.Fields)
                        {
                            Line("struct-pattern-field", f.Span, $"name={Q(f.Name)}", $"short={B(f.IsShorthand)}");
                            Attrs(f.Attributes);
                            Pattern(f.Pattern);
                        }
                    });
                    break;
                default:
                    throw new InvalidDataException($"Unknown pattern {x.GetType().Name}");
            }
        }
    }
}
