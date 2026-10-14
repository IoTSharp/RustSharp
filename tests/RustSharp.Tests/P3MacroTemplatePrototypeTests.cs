using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P3MacroTemplatePrototypeTests
{
    private const string Ident="macro_rules! m { ($x:ident)=>{$x}; }";
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P3 template prototype substitutes actual ident capture with spans", IdentAsync),
        new("P3 template prototype substitutes actual literal capture", () => OutputAsync("macro_rules! m { ($x:literal)=>{$x}; }","\"中\"","\"中\"")),
        new("P3 template prototype substitutes actual tt group tokens", TokenTreeAsync),
        new("P3 template prototype emits ordinary template group", () => OutputAsync("macro_rules! m { ($x:ident)=>{[$x + 1]}; }","value","[ value + 1 ]")),
        new("P3 template prototype substitutes repeated reference without repetition syntax", () => OutputAsync("macro_rules! m { ($x:ident)=>{$x + $x}; }","value","value + value")),
        new("P3 template prototype emits selected later arm template", LaterAsync),
        new("P3 template prototype rejects unbound reference", () => RejectAsync("macro_rules! m { ($x:ident)=>{$y}; }","value","RSMAC1004")),
        new("P3 template prototype rejects repetition capability", () => RejectAsync("macro_rules! m { ($($x:ident),*)=>{$($x),*}; }","a,b","RSMAC1001")),
        new("P3 template prototype rejects matcher group capability", () => RejectAsync("macro_rules! m { ([$x:ident])=>{$x}; }","[a]","RSMAC1001")),
        new("P3 template prototype enforces output token exact and over boundary", TokensAsync),
        new("P3 template prototype enforces output UTF8 byte exact and over boundary", BytesAsync),
        new("P3 template prototype enforces output nesting exact and over boundary", DepthAsync),
        new("P3 template prototype enforces aggregate input bytes", InputAsync),
        new("P3 template prototype enforces aggregate step exact and over boundary", StepsAsync),
        new("P3 template prototype propagates pre entry cancellation", CancelAsync),
        new("P3 template prototype propagates cancellation before output publication", MidCancelAsync),
    ];
    private static Task OutputAsync(string definition,string input,string expected) { Success(P3MacroTemplatePrototype.Substitute(definition,input),expected); return Task.CompletedTask; }
    private static Task RejectAsync(string definition,string input,string diagnostic) { Rejected(P3MacroTemplatePrototype.Substitute(definition,input),diagnostic); return Task.CompletedTask; }
    private static Task IdentAsync()
    {
        const string input="  value ";var r=P3MacroTemplatePrototype.Substitute(Ident,input);Success(r,"value");
        AssertEx.Equal(1,r.Tokens.Count);AssertEx.Equal(2,r.Tokens[0].InputSpan!.Value.Start);
        var t=r.Tokens[0];AssertEx.Equal("$x",Ident.Substring(t.TemplateSpan.Start,t.TemplateSpan.Length));AssertEx.Equal("value",r.Output.Substring(t.OutputSpan.Start,t.OutputSpan.Length));return Task.CompletedTask;
    }
    private static Task TokenTreeAsync()
    {
        const string input="[a /*keep original ranges*/ (b)]";var r=P3MacroTemplatePrototype.Substitute("macro_rules! m { ($x:tt)=>{$x}; }",input);Success(r,"[ a ( b ) ]");
        AssertEx.Equal(6,r.Tokens.Count);AssertEx.Equal("b",input.Substring(r.Tokens[3].InputSpan!.Value.Start,r.Tokens[3].InputSpan!.Value.Length));return Task.CompletedTask;
    }
    private static Task LaterAsync() { var r=P3MacroTemplatePrototype.Substitute("macro_rules! m { (tag)=>{0}; ($x:ident)=>{[$x]}; }","value");Success(r,"[ value ]");AssertEx.Equal(1,r.SelectedRule!.Value);return Task.CompletedTask; }
    private static Task TokensAsync() { const string d="macro_rules! m { ($x:ident)=>{$x + 1}; }";Success(P3MacroTemplatePrototype.Substitute(d,"a",new(){MaximumOutputTokens=3}),"a + 1");Rejected(P3MacroTemplatePrototype.Substitute(d,"a",new(){MaximumOutputTokens=2}),"RSMAC1005");return Task.CompletedTask; }
    private static Task BytesAsync() { const string d="macro_rules! m { ($x:literal)=>{$x}; }";Success(P3MacroTemplatePrototype.Substitute(d,"\"中\"",new(){MaximumOutputBytes=5}),"\"中\"");Rejected(P3MacroTemplatePrototype.Substitute(d,"\"中\"",new(){MaximumOutputBytes=4}),"RSMAC1005");return Task.CompletedTask; }
    private static Task DepthAsync() { const string d="macro_rules! m { ($x:tt)=>{[$x]}; }";Success(P3MacroTemplatePrototype.Substitute(d,"(a)",new(){MaximumOutputDepth=2}),"[ ( a ) ]");Rejected(P3MacroTemplatePrototype.Substitute(d,"(a)",new(){MaximumOutputDepth=1}),"RSMAC1005");return Task.CompletedTask; }
    private static Task InputAsync() { Rejected(P3MacroTemplatePrototype.Substitute(Ident,"value",new(){Input=new(){MaximumInputBytes=Ident.Length+4}}),"RSMAC1005");return Task.CompletedTask; }
    private static Task StepsAsync() { var first=P3MacroTemplatePrototype.Substitute(Ident,"a");Success(first,"a");Success(P3MacroTemplatePrototype.Substitute(Ident,"a",new(){Input=new(){MaximumMatcherSteps=first.MatcherSteps}}),"a");Rejected(P3MacroTemplatePrototype.Substitute(Ident,"a",new(){Input=new(){MaximumMatcherSteps=first.MatcherSteps-1}}),"RSMAC1005");return Task.CompletedTask; }
    private static Task CancelAsync() { using var c=new CancellationTokenSource();c.Cancel();AssertEx.Throws<OperationCanceledException>(()=>P3MacroTemplatePrototype.Substitute(Ident,"a",cancellationToken:c.Token));return Task.CompletedTask; }
    private static Task MidCancelAsync() { var first=P3MacroTemplatePrototype.Substitute(Ident,"a");Success(first,"a");using var c=new CancellationTokenSource(TimeSpan.FromSeconds(5));int observed=0;AssertEx.Throws<OperationCanceledException>(()=>P3MacroTemplatePrototype.Substitute(Ident,"a",progress:step=>{observed=step;if(step==first.MatcherSteps-1)c.Cancel();},cancellationToken:c.Token));AssertEx.Equal(first.MatcherSteps-1,observed);return Task.CompletedTask; }
    private static void Success(P3MacroTemplateResult r,string expected) { AssertEx.True(r.IsSuccessful,r.Message);AssertEx.Equal(expected,r.Output);AssertEx.False(r.FormalAcceptanceAllowed,"Prototype cannot accept P3.");AssertEx.Equal(0,r.ProductionMacroExpansionsExecuted); }
    private static void Rejected(P3MacroTemplateResult r,string code) { AssertEx.False(r.IsSuccessful,"Unsupported or over-budget substitution rejects.");AssertEx.Equal(code,r.Diagnostic);AssertEx.Equal(string.Empty,r.Output);AssertEx.Equal(0,r.Tokens.Count);AssertEx.Equal(0,r.Captures.Count);AssertEx.True(r.SelectedRule is null,"No partial result publishes."); }
}
