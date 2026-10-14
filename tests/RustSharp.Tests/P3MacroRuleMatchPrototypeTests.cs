using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P3MacroRuleMatchPrototypeTests
{
    private const string Ident = "macro_rules! m { ($x:ident)=>{$x}; }";
    private const string Literal = "macro_rules! m { ($x:literal)=>{$x}; }";
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P3 rule match prototype captures ident and source spans", IdentAsync),
        new("P3 rule match prototype captures unsuffixed decimal literal", () => LiteralAsync("1_024")),
        new("P3 rule match prototype captures cooked UTF8 string literal", () => LiteralAsync("\"中\\n\"")),
        new("P3 rule match prototype captures recursively balanced tt group", TokenTreeAsync),
        new("P3 rule match prototype consumes plain tokens and capture", PrefixAsync),
        new("P3 rule match prototype selects first matching arm", FirstAsync),
        new("P3 rule match prototype falls back to later matching arm", LaterAsync),
        new("P3 rule match prototype rejects signed literal", () => RejectAsync(Literal,"-1","RSMAC1003")),
        new("P3 rule match prototype rejects suffixed literal", () => RejectAsync(Literal,"1u32","RSMAC1003")),
        new("P3 rule match prototype rejects raw string literal", () => RejectAsync(Literal,"r\"raw\"","RSMAC1003")),
        new("P3 rule match prototype rejects unconsumed input", () => RejectAsync(Ident,"a b","RSMAC1003")),
        new("P3 rule match prototype explicitly rejects repetition", () => RejectAsync("macro_rules! m { ($($x:ident),*)=>{$($x),*}; }","a,b","RSMAC1001")),
        new("P3 rule match prototype explicitly rejects nested matcher group", () => RejectAsync("macro_rules! m { ([$x:ident])=>{$x}; }","[a]","RSMAC1001")),
        new("P3 rule match prototype enforces aggregate source byte boundary", BytesAsync),
        new("P3 rule match prototype propagates pre entry cancellation", CancelAsync),
        new("P3 rule match prototype propagates matching cancellation", MidCancelAsync),
    ];
    private static Task IdentAsync()
    {
        const string input="  name42 "; var r=P3MacroRuleMatchPrototype.Match(Ident,input); Success(r,0,1);
        AssertEx.Equal("name42",r.Captures[0].Text); AssertEx.Equal(2,r.Captures[0].InputSpan.Start);
        var span=r.Captures[0].DefinitionSpan; AssertEx.Equal("$x:ident",Ident.Substring(span.Start,span.Length)); return Task.CompletedTask;
    }
    private static Task LiteralAsync(string input) { var r=P3MacroRuleMatchPrototype.Match(Literal,input); Success(r,0,1); AssertEx.Equal(input,r.Captures[0].Text); return Task.CompletedTask; }
    private static Task TokenTreeAsync() { const string input="[a (b {c})]"; var r=P3MacroRuleMatchPrototype.Match("macro_rules! m { ($x:tt)=>{$x}; }",input); Success(r,0,1); AssertEx.True(r.Captures[0].IsDelimited,"Actual lexer delimiter group is captured intact."); AssertEx.Equal(input,r.Captures[0].Text); return Task.CompletedTask; }
    private static Task PrefixAsync() { var r=P3MacroRuleMatchPrototype.Match("macro_rules! m { (tag => $x:ident)=>{$x}; }","tag => value"); Success(r,0,1); AssertEx.Equal("value",r.Captures[0].Text); return Task.CompletedTask; }
    private static Task FirstAsync() { var r=P3MacroRuleMatchPrototype.Match("macro_rules! m { ($x:ident)=>{$x}; ($y:ident)=>{$y}; }","value"); Success(r,0,1); AssertEx.Equal("x",r.Captures[0].Name); return Task.CompletedTask; }
    private static Task LaterAsync() { var r=P3MacroRuleMatchPrototype.Match("macro_rules! m { (tag)=>{}; ($y:ident)=>{$y}; }","value"); Success(r,1,1); AssertEx.Equal("y",r.Captures[0].Name); return Task.CompletedTask; }
    private static Task RejectAsync(string definition,string input,string code) { Rejected(P3MacroRuleMatchPrototype.Match(definition,input),code); return Task.CompletedTask; }
    private static Task BytesAsync() { const string input="a"; int bytes=Ident.Length+input.Length; Success(P3MacroRuleMatchPrototype.Match(Ident,input,new(){MaximumInputBytes=bytes}),0,1); Rejected(P3MacroRuleMatchPrototype.Match(Ident,input,new(){MaximumInputBytes=bytes-1}),"RSMAC1005"); return Task.CompletedTask; }
    private static Task CancelAsync() { using var c=new CancellationTokenSource();c.Cancel();AssertEx.Throws<OperationCanceledException>(()=>P3MacroRuleMatchPrototype.Match(Ident,"a",cancellationToken:c.Token));return Task.CompletedTask; }
    private static Task MidCancelAsync()
    {
        var first=P3MacroRuleMatchPrototype.Match(Ident,"name");Success(first,0,1);using var c=new CancellationTokenSource(TimeSpan.FromSeconds(5));int observed=0;
        AssertEx.Throws<OperationCanceledException>(()=>P3MacroRuleMatchPrototype.Match(Ident,"name",progress:step=>{observed=step;if(step==first.MatcherSteps-1)c.Cancel();},cancellationToken:c.Token));
        AssertEx.Equal(first.MatcherSteps-1,observed,"Aggregate progress cancels after real rule and fragment parsing, before publication.");return Task.CompletedTask;
    }
    private static void Success(P3MacroRuleMatchResult r,int arm,int captures) { AssertEx.True(r.IsSuccessful,r.Message);AssertEx.Equal(arm,r.SelectedRule!.Value);AssertEx.Equal(captures,r.Captures.Count);AssertEx.True(r.RuleSpan is not null,"Selected rule retains its definition span.");AssertEx.False(r.FormalAcceptanceAllowed,"Prototype cannot accept a leaf.");AssertEx.Equal(0,r.ProductionMacroExpansionsExecuted); }
    private static void Rejected(P3MacroRuleMatchResult r,string code) { AssertEx.False(r.IsSuccessful,"Invalid or unsupported matcher input rejects.");AssertEx.Equal(code,r.Diagnostic);AssertEx.Equal(0,r.Captures.Count);AssertEx.True(r.SelectedRule is null && r.RuleSpan is null,"No partial captures or selected arm on rejection."); }
}
