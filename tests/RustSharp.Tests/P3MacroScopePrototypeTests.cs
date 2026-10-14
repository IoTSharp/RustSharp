using System.Diagnostics;
using RustSharp.Conformance;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class P3MacroScopePrototypeTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P3 scope prototype resolves definition site local by real token span", LocalAsync),
        new("P3 scope prototype resolves retained invocation context", CapturedAsync),
        new("P3 scope prototype isolates definition and caller names", IsolatesAsync),
        new("P3 scope prototype rejects accidental caller capture", AccidentalAsync),
        new("P3 scope prototype nested scope shadows local", NestedAsync),
        new("P3 scope prototype leaving nested scope restores outer local", RestoresAsync),
        new("P3 scope prototype same scope later let shadows earlier let", LaterAsync),
        new("P3 scope prototype rejects read before declaration", BeforeAsync),
        new("P3 scope prototype rejects sibling scope escape", SiblingAsync),
        new("P3 scope prototype rejects distinct expansion identity capture", DistinctAsync),
        new("P3 scope prototype rejects changed captured spelling", ChangedAsync),
        new("P3 scope prototype rejects missing original provenance", MissingAsync),
        new("P3 scope prototype rejects duplicate original provenance", DuplicateAsync),
        new("P3 scope prototype rejects malformed token tree", MalformedAsync),
        new("P3 scope prototype rejects unsupported initializer syntax", UnsupportedAsync),
        new("P3 scope prototype enforces exact matcher step boundary", StepAsync),
        new("P3 scope prototype enforces source byte budget", BytesAsync),
        new("P3 scope prototype propagates pre entry cancellation", CancelAsync),
        new("P3 scope prototype propagates resolver cancellation", MidCancelAsync),
    ];
    private static List<P3MacroTokenOrigin> Origins(string source, params string[] contexts)
    {
        RustToken[] names = RustLexer.Lex(source).Tokens.Where(static token => token.Kind == RustTokenKind.Identifier).ToArray();
        AssertEx.True(names.Length <= 32, "Fixture origin count has a fixed bound.");
        AssertEx.True(contexts.Length == 0 || contexts.Length == names.Length, "Fixture contexts cover each real identifier.");
        var result = new List<P3MacroTokenOrigin>(); var clock = Stopwatch.StartNew();
        for (int index = 0; index < names.Length && index < 32; index++)
        {
            if (clock.ElapsedMilliseconds > 1000) throw new TimeoutException("Fixture origin deadline.");
            string context = contexts.Length == 0 ? "definition" : contexts[index];
            result.Add(new(names[index].Span, context.StartsWith("caller", StringComparison.Ordinal) ? P3MacroContextKind.CapturedInvocation : P3MacroContextKind.DefinitionLocal,
                context, names[index].Text, new(0, names[index].Text.Length)));
        }
        return result;
    }
    private static P3MacroScopeResult Run(string source, params string[] contexts) => P3MacroScopePrototype.Resolve(source, Origins(source, contexts));
    private static Task LocalAsync() { P3MacroScopeResult r = Run("let temp=0; temp;"); Success(r, 1); AssertEx.Equal(new TextSpan(4,4), r.References[0].DeclarationSpan); return Task.CompletedTask; }
    private static Task CapturedAsync() { Success(Run("let caller=0; caller;", "caller-use", "caller-use"), 1); return Task.CompletedTask; }
    private static Task IsolatesAsync() { P3MacroScopeResult r=Run("let temp=0; let temp=1; temp; temp;", "caller-use","definition","caller-use","definition"); Success(r,2); AssertEx.True(r.References[0].DeclarationSpan != r.References[1].DeclarationSpan, "Same spelling resolves distinct lexical identities."); return Task.CompletedTask; }
    private static Task AccidentalAsync() { Reject(Run("let temp=0; temp;", "caller-use","definition"),"RSMAC1006"); return Task.CompletedTask; }
    private static Task NestedAsync() { P3MacroScopeResult r=Run("let x=0; { let x=1; x; }"); Success(r,1); AssertEx.Equal(15,r.References[0].DeclarationSpan.Start); return Task.CompletedTask; }
    private static Task RestoresAsync() { P3MacroScopeResult r=Run("let x=0; { let x=1; x; } x;"); Success(r,2); AssertEx.Equal(4,r.References[1].DeclarationSpan.Start); return Task.CompletedTask; }
    private static Task LaterAsync() { P3MacroScopeResult r=Run("let x=0; let x=1; x;"); Success(r,1); AssertEx.Equal(13,r.References[0].DeclarationSpan.Start); return Task.CompletedTask; }
    private static Task BeforeAsync() { Reject(Run("x; let x=0;"),"RSMAC1006"); return Task.CompletedTask; }
    private static Task SiblingAsync() { Reject(Run("{let x=0;} {x;}"),"RSMAC1006"); return Task.CompletedTask; }
    private static Task DistinctAsync() { Reject(Run("let x=0; x;","expansion-1","expansion-2"),"RSMAC1006"); return Task.CompletedTask; }
    private static Task ChangedAsync() { const string s="let x=0; x;"; var o=Origins(s); o[1]=o[1] with { OriginalSource="y" }; Reject(P3MacroScopePrototype.Resolve(s,o),"RSMAC1006"); return Task.CompletedTask; }
    private static Task MissingAsync() { const string s="let x=0; x;"; var o=Origins(s); o.RemoveAt(1); Reject(P3MacroScopePrototype.Resolve(s,o),"RSMAC1006"); return Task.CompletedTask; }
    private static Task DuplicateAsync() { const string s="let x=0; x;"; var o=Origins(s); o.Add(o[0]); Reject(P3MacroScopePrototype.Resolve(s,o),"RSMAC1006"); return Task.CompletedTask; }
    private static Task MalformedAsync() { Reject(Run("{let x=0;]"),"RSMAC1002"); return Task.CompletedTask; }
    private static Task UnsupportedAsync() { Reject(Run("let x=true; x;"),"RSMAC1003"); return Task.CompletedTask; }
    private static Task StepAsync() { const string s="let x=0; x;"; var o=Origins(s); var first=P3MacroScopePrototype.Resolve(s,o); Success(first,1); Success(P3MacroScopePrototype.Resolve(s,o,new(){MaximumMatcherSteps=first.MatcherSteps}),1); Reject(P3MacroScopePrototype.Resolve(s,o,new(){MaximumMatcherSteps=first.MatcherSteps-1}),"RSMAC1005"); return Task.CompletedTask; }
    private static Task BytesAsync() { const string s="let x=0; x;"; Reject(P3MacroScopePrototype.Resolve(s,Origins(s),new(){MaximumInputBytes=s.Length-1}),"RSMAC1005"); return Task.CompletedTask; }
    private static Task CancelAsync() { using var c=new CancellationTokenSource(); c.Cancel(); AssertEx.Throws<OperationCanceledException>(()=>P3MacroScopePrototype.Resolve("",[],cancellationToken:c.Token)); return Task.CompletedTask; }
    private static Task MidCancelAsync() { using var c=new CancellationTokenSource(TimeSpan.FromSeconds(5)); const string s="let x=0; x;"; int observed=0; AssertEx.Throws<OperationCanceledException>(()=>P3MacroScopePrototype.Resolve(s,Origins(s),progress:step=>{observed=step;if(step==6)c.Cancel();},cancellationToken:c.Token)); AssertEx.Equal(6,observed); return Task.CompletedTask; }
    private static void Success(P3MacroScopeResult result,int references) { AssertEx.True(result.IsSuccessful,result.Message); AssertEx.Equal(references,result.References.Count); AssertEx.False(result.FormalAcceptanceAllowed,"Prototype cannot accept a leaf."); AssertEx.Equal(0,result.ProductionMacroExpansionsExecuted); }
    private static void Reject(P3MacroScopeResult result,string code) { AssertEx.False(result.IsSuccessful,"Invalid context/source must reject."); AssertEx.Equal(code,result.Diagnostic); AssertEx.Equal(0,result.References.Count); }
}
