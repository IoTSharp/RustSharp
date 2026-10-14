using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P3MacroRuleParserPrototypeTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P3 rule parser prototype parses ident capture and exact span", IdentAsync),
        new("P3 rule parser prototype parses literal and tt fragments", FragmentsAsync),
        new("P3 rule parser prototype parses multiple rule arms", ArmsAsync),
        new("P3 rule parser prototype parses star repetition", () => RepetitionAsync("*", "")),
        new("P3 rule parser prototype parses plus repetition", () => RepetitionAsync("+", "")),
        new("P3 rule parser prototype parses optional repetition", () => RepetitionAsync("?", "")),
        new("P3 rule parser prototype parses comma star repetition", () => RepetitionAsync("*", ",")),
        new("P3 rule parser prototype parses comma plus repetition", () => RepetitionAsync("+", ",")),
        new("P3 rule parser prototype parses nested separated repetition", NestedAsync),
        new("P3 rule parser prototype rejects unsupported fragment", () => RejectAsync("macro_rules! m { ($x:expr)=>{$x}; }", "RSMAC1001")),
        new("P3 rule parser prototype rejects missing fragment", () => RejectAsync("macro_rules! m { ($x)=>{$x}; }", "RSMAC1004")),
        new("P3 rule parser prototype rejects missing metavariable name", () => RejectAsync("macro_rules! m { ($:ident)=>{}; }", "RSMAC1004")),
        new("P3 rule parser prototype rejects duplicate bindings", () => RejectAsync("macro_rules! m { ($x:ident $x:ident)=>{$x}; }", "RSMAC1006")),
        new("P3 rule parser prototype rejects unknown template capture", () => RejectAsync("macro_rules! m { ($x:ident)=>{$y}; }", "RSMAC1004")),
        new("P3 rule parser prototype rejects transcriber depth mismatch", () => RejectAsync("macro_rules! m { ($($x:ident)*)=>{$x}; }", "RSMAC1004")),
        new("P3 rule parser prototype rejects optional separator", () => RejectAsync("macro_rules! m { ($($x:ident),?)=>{}; }", "RSMAC1004")),
        new("P3 rule parser prototype rejects missing repetition operator", () => RejectAsync("macro_rules! m { ($($x:ident))=>{}; }", "RSMAC1004")),
        new("P3 rule parser prototype rejects malformed delimiters", () => RejectAsync("macro_rules! m { ($x:ident]=>{}; }", "RSMAC1002")),
        new("P3 rule parser prototype rejects missing rule arrow", () => RejectAsync("macro_rules! m { ($x:ident) {$x}; }", "RSMAC1003")),
        new("P3 rule parser prototype rejects empty rule inventory", () => RejectAsync("macro_rules! m {}", "RSMAC1003")),
        new("P3 rule parser prototype enforces source byte boundary", BytesAsync),
        new("P3 rule parser prototype enforces exact matcher step boundary", StepsAsync),
        new("P3 rule parser prototype propagates pre entry cancellation", CancelAsync),
        new("P3 rule parser prototype propagates recursive parsing cancellation", MidCancelAsync),
    ];
    private static Task IdentAsync()
    {
        const string source="macro_rules! m { ($x:ident)=>{$x}; }"; P3MacroRuleParseResult r=P3MacroRuleParserPrototype.Parse(source); Success(r);
        AssertEx.Equal("m",r.Name); AssertEx.Equal(1,r.Rules.Count); AssertEx.Equal("ident",r.Rules[0].Captures[0].Fragment);
        var span=r.Rules[0].Captures[0].Span; AssertEx.Equal("$x:ident",source.Substring(span.Start,span.Length)); return Task.CompletedTask;
    }
    private static Task FragmentsAsync() { var r=P3MacroRuleParserPrototype.Parse("macro_rules! m { ($x:literal $y:tt)=>{$x $y}; }"); Success(r); AssertEx.Equal(2,r.Rules[0].Captures.Count); return Task.CompletedTask; }
    private static Task ArmsAsync() { var r=P3MacroRuleParserPrototype.Parse("macro_rules! m { ()=>{}; ($x:ident)=>{$x}; }"); Success(r); AssertEx.Equal(2,r.Rules.Count); return Task.CompletedTask; }
    private static Task RepetitionAsync(string op,string separator) { string source="macro_rules! m { ($($x:ident)"+separator+op+")=>{$($x)"+separator+op+"}; }"; var r=P3MacroRuleParserPrototype.Parse(source); Success(r); AssertEx.Equal("repetition",r.Rules[0].Matcher[0].Kind); AssertEx.Equal(op,r.Rules[0].Matcher[0].Text); AssertEx.Equal(separator,r.Rules[0].Matcher[0].Detail); AssertEx.Equal(1,r.Rules[0].Captures[0].RepetitionDepth); return Task.CompletedTask; }
    private static Task NestedAsync() { var r=P3MacroRuleParserPrototype.Parse("macro_rules! m { ($([$($x:ident),*]);*)=>{$([$($x),*]);*}; }"); Success(r); AssertEx.Equal(2,r.Rules[0].Captures[0].RepetitionDepth); return Task.CompletedTask; }
    private static Task RejectAsync(string source,string diagnostic) { Rejected(P3MacroRuleParserPrototype.Parse(source),diagnostic); return Task.CompletedTask; }
    private static Task BytesAsync() { const string source="macro_rules! m { ()=>{}; }"; Success(P3MacroRuleParserPrototype.Parse(source,new(){MaximumInputBytes=source.Length})); Rejected(P3MacroRuleParserPrototype.Parse(source,new(){MaximumInputBytes=source.Length-1}),"RSMAC1005"); return Task.CompletedTask; }
    private static Task StepsAsync() { const string source="macro_rules! m { ($x:ident)=>{$x}; }"; var first=P3MacroRuleParserPrototype.Parse(source); Success(first); Success(P3MacroRuleParserPrototype.Parse(source,new(){MaximumMatcherSteps=first.MatcherSteps})); Rejected(P3MacroRuleParserPrototype.Parse(source,new(){MaximumMatcherSteps=first.MatcherSteps-1}),"RSMAC1005"); return Task.CompletedTask; }
    private static Task CancelAsync() { using var c=new CancellationTokenSource();c.Cancel();AssertEx.Throws<OperationCanceledException>(()=>P3MacroRuleParserPrototype.Parse("",cancellationToken:c.Token));return Task.CompletedTask; }
    private static Task MidCancelAsync() { using var c=new CancellationTokenSource(TimeSpan.FromSeconds(5));int observed=0;AssertEx.Throws<OperationCanceledException>(()=>P3MacroRuleParserPrototype.Parse("macro_rules! m { ($($x:ident),*)=>{$($x),*}; }",progress:step=>{observed=step;if(step==6)c.Cancel();},cancellationToken:c.Token));AssertEx.Equal(6,observed);return Task.CompletedTask; }
    private static void Success(P3MacroRuleParseResult r) { AssertEx.True(r.IsSuccessful,r.Message);AssertEx.False(r.FormalAcceptanceAllowed,"Prototype cannot accept P3.");AssertEx.Equal(0,r.ProductionMacroExpansionsExecuted); }
    private static void Rejected(P3MacroRuleParseResult r,string diagnostic) { AssertEx.False(r.IsSuccessful,"Malformed or unsupported definition rejects.");AssertEx.Equal(diagnostic,r.Diagnostic);AssertEx.Equal(0,r.Rules.Count);AssertEx.True(r.NameSpan is null,"No partial definition is exposed."); }
}
