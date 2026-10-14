using System.Diagnostics;
using System.Text;
using RustSharp.Syntax;

namespace RustSharp.Conformance;

/// <summary>Isolated lexical-local resolver for let-ident-integer and ident-read fixtures with real token origins.</summary>
public static class P3MacroScopePrototype
{
    public static P3MacroScopeResult Resolve(string source, IReadOnlyList<P3MacroTokenOrigin> origins,
        P3MacroBuiltinLimits? limits = null, Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(origins);
        cancellationToken.ThrowIfCancellationRequested(); limits ??= new();
        if (!limits.IsWithinCandidate()) return Failure("RSMAC1090", "Invalid frozen resource limits.", 0);
        if (source.Length > limits.MaximumInputBytes || Encoding.UTF8.GetByteCount(source) > limits.MaximumInputBytes || origins.Count > limits.MaximumInputTokens)
            return Failure("RSMAC1005", "Source or origin inventory budget exceeded.", 0);
        var state = new State(limits, Stopwatch.StartNew(), progress, cancellationToken);
        try
        {
            RustLexerOptions options = new() { MaximumSourceLength = limits.MaximumInputBytes, MaximumTokens = limits.MaximumInputTokens,
                MaximumTrivia = limits.MaximumInputBytes, MaximumDiagnostics = 16, MaximumDelimiterDepth = limits.MaximumDepth,
                Timeout = TimeSpan.FromMilliseconds(limits.TimeoutMilliseconds) };
            RustLexResult lex = RustLexer.Lex(source, "scope-prototype.rs", options, cancellationToken);
            state.Step();
            if (lex.IsTruncated || lex.Diagnostics.Any(static item => item.Code is RustLexDiagnosticCodes.LimitReached or RustLexDiagnosticCodes.SourceTooLong or RustLexDiagnosticCodes.DelimiterDepthLimit))
                return Failure("RSMAC1005", "Lexer resource budget exceeded.", state.Steps);
            if (!lex.IsSuccessful) return Failure("RSMAC1002", "Malformed token or delimiter tree.", state.Steps);
            var contexts = new Dictionary<int, P3MacroTokenOrigin>();
            int originBytes = 0;
            foreach (P3MacroTokenOrigin origin in origins)
            {
                state.Step();
                if (origin is null || origin.OriginalSource is null || string.IsNullOrEmpty(origin.ContextId)
                    || origin.ContextId.Length > 64 || !Enum.IsDefined(origin.Kind)
                    || origin.OriginalSpan.Start < 0 || origin.OriginalSpan.Length <= 0
                    || origin.OriginalSpan.Start > origin.OriginalSource.Length - origin.OriginalSpan.Length)
                    return Failure("RSMAC1006", "Invalid original token provenance.", state.Steps);
                if (origin.OriginalSource.Length > limits.MaximumInputBytes) return Failure("RSMAC1005", "Origin source budget exceeded.", state.Steps);
                originBytes += Encoding.UTF8.GetByteCount(origin.OriginalSource);
                if (originBytes > limits.MaximumInputBytes) return Failure("RSMAC1005", "Aggregate origin byte budget exceeded.", state.Steps);
                string original = origin.OriginalSource.Substring(origin.OriginalSpan.Start, origin.OriginalSpan.Length);
                RustLexResult originalLex = RustLexer.Lex(original, "origin.rs", options, cancellationToken);
                if (!originalLex.IsSuccessful || originalLex.Tokens.Count != 1 || originalLex.Tokens[0].Kind != RustTokenKind.Identifier
                    || originalLex.Tokens[0].Span.Length != original.Length || !contexts.TryAdd(origin.ExpandedSpan.Start, origin))
                    return Failure("RSMAC1006", "Origin must identify one exact original identifier; duplicate origins reject.", state.Steps);
            }
            IReadOnlyList<RustToken> tokens = lex.Tokens;
            var parents = new List<int> { -1 };
            var bindings = new List<Binding>();
            var references = new List<P3MacroResolvedReference>();
            int scope = 0, depth = 0, consumedOrigins = 0;
            for (int index = 0; index < tokens.Count && index < limits.MaximumInputTokens; index++)
            {
                state.Step(); RustToken token = tokens[index];
                if (token.Text == "{") { if (++depth > limits.MaximumDepth) return Failure("RSMAC1005", "Scope depth budget exceeded.", state.Steps); parents.Add(scope); scope = parents.Count - 1; continue; }
                if (token.Text == "}") { if (scope == 0) return Failure("RSMAC1002", "Unbalanced scope.", state.Steps); scope = parents[scope]; depth--; continue; }
                if (token.Text == ";") continue;
                bool declaration = token.Kind == RustTokenKind.Keyword && token.Text == "let";
                RustToken name = declaration && index + 4 < tokens.Count ? tokens[index + 1] : token;
                if (name.Kind != RustTokenKind.Identifier || !contexts.TryGetValue(name.Span.Start, out P3MacroTokenOrigin? origin)
                    || origin.ExpandedSpan != name.Span || name.Text != origin.OriginalSource.Substring(origin.OriginalSpan.Start, origin.OriginalSpan.Length))
                    return Failure("RSMAC1006", "Missing or changed identifier provenance; capture context cannot be invented.", state.Steps);
                consumedOrigins++;
                string context = origin.Kind.ToString() + ":" + origin.ContextId;
                if (declaration)
                {
                    if (index + 4 >= tokens.Count || tokens[index + 2].Text != "=" || tokens[index + 3].Kind != RustTokenKind.IntegerLiteral
                        || tokens[index + 3].LiteralSuffix is not null || tokens[index + 4].Text != ";")
                        return Failure("RSMAC1003", "Prototype only accepts let identifier = unsuffixed integer; declarations.", state.Steps);
                    bindings.Add(new(scope, name.Text, context, name.Span)); index += 4; continue;
                }
                if (index + 1 >= tokens.Count || tokens[index + 1].Text != ";") return Failure("RSMAC1003", "Prototype reads require identifier semicolon.", state.Steps);
                Binding? found = null;
                int searchScope = scope;
                for (int level = 0; level <= limits.MaximumDepth && searchScope >= 0 && found is null; level++)
                {
                    state.Step();
                    for (int bindingIndex = bindings.Count - 1; bindingIndex >= 0 && bindingIndex < limits.MaximumInputTokens; bindingIndex--)
                    {
                        state.Step(); Binding candidate = bindings[bindingIndex];
                        if (candidate.Scope == searchScope && candidate.Name == name.Text && candidate.Context == context) { found = candidate; break; }
                    }
                    searchScope = parents[searchScope];
                }
                if (found is null) return Failure("RSMAC1006", "Unresolved local; no cross-context accidental capture fallback.", state.Steps);
                references.Add(new(name.Span, found.Span, context, scope)); index++;
            }
            state.Step();
            if (consumedOrigins != origins.Count) return Failure("RSMAC1006", "Unexpected or unused origin records.", state.Steps);
            return new(true, string.Empty, string.Empty, references.AsReadOnly(), bindings.Count, state.Steps);
        }
        catch (TimeoutException exception) { return Failure("RSMAC1005", exception.Message, state.Steps); }
    }
    private static P3MacroScopeResult Failure(string diagnostic, string message, int steps) => new(false, diagnostic, message, Array.Empty<P3MacroResolvedReference>(), 0, steps);
    private sealed record Binding(int Scope, string Name, string Context, TextSpan Span);
    private sealed class State(P3MacroBuiltinLimits limits, Stopwatch clock, Action<int>? progress, CancellationToken cancellationToken)
    {
        internal int Steps { get; private set; }
        internal void Step() { cancellationToken.ThrowIfCancellationRequested(); if (Steps >= limits.MaximumMatcherSteps || clock.ElapsedMilliseconds > limits.TimeoutMilliseconds) throw new TimeoutException("Scope prototype step/deadline budget exceeded."); Steps++; progress?.Invoke(Steps); cancellationToken.ThrowIfCancellationRequested(); }
    }
}
public enum P3MacroContextKind { DefinitionLocal, CapturedInvocation }
public sealed record P3MacroTokenOrigin(TextSpan ExpandedSpan, P3MacroContextKind Kind, string ContextId, string OriginalSource, TextSpan OriginalSpan);
public sealed record P3MacroResolvedReference(TextSpan ReferenceSpan, TextSpan DeclarationSpan, string Context, int LexicalScope);
public sealed record P3MacroScopeResult(bool IsSuccessful, string Diagnostic, string Message, IReadOnlyList<P3MacroResolvedReference> References, int BindingCount, int MatcherSteps)
{ public bool FormalAcceptanceAllowed { get; } public int ProductionMacroExpansionsExecuted { get; } }
