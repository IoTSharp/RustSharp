using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P3MacroRepeatedTemplatePrototypeTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P3 repeated template prototype star zero emits empty output", () => OutputAsync(Definition("*","",""),"","")),
        new("P3 repeated template prototype star many retains all source spans", SpansAsync),
        new("P3 repeated template prototype plus emits nonempty items", () => OutputAsync(Definition("+","",""),"a b","a b")),
        new("P3 repeated template prototype optional emits zero or one", OptionalAsync),
        new("P3 repeated template prototype adds comma separators", () => OutputAsync(Definition("*","",","),"a b","a , b")),
        new("P3 repeated template prototype removes matcher commas", () => OutputAsync(Definition("*",",",""),"a,b","a b")),
        new("P3 repeated template prototype rejects operator mismatch", () => RejectAsync("macro_rules! m { ($($x:ident)*)=>{$($x)+}; }","a","RSMAC1004")),
        new("P3 repeated template prototype rejects multiple template references", () => RejectAsync("macro_rules! m { ($($x:ident)*)=>{$($x $x)*}; }","a","RSMAC1001")),
        new("P3 repeated template prototype rejects template prefix", () => RejectAsync("macro_rules! m { ($($x:ident)*)=>{prefix $($x)*}; }","a","RSMAC1001")),
        new("P3 repeated template prototype rejects nested repetition", () => RejectAsync("macro_rules! m { ($([$($x:ident),*]);*)=>{$([$($x),*]);*}; }","[a]","RSMAC1001")),
        new("P3 repeated template prototype enforces output token exact and over boundary", TokensAsync),
        new("P3 repeated template prototype enforces output byte exact and over boundary", BytesAsync),
        new("P3 repeated template prototype enforces aggregate input byte exact and over boundary", InputAsync),
        new("P3 repeated template prototype enforces aggregate step exact and over boundary", StepsAsync),
        new("P3 repeated template prototype propagates pre entry cancellation", CancelAsync),
        new("P3 repeated template prototype propagates mid output cancellation", MidCancelAsync),
    ];
    private static string Definition(string op,string inputSeparator,string outputSeparator) => "macro_rules! m { ($($x:ident)"+inputSeparator+op+")=>{$($x)"+outputSeparator+op+"}; }";
    private static Task OutputAsync(string d,string input,string expected) { Success(P3MacroRepeatedTemplatePrototype.Substitute(d,input),expected);return Task.CompletedTask; }
    private static Task RejectAsync(string d,string input,string diagnostic) { Rejected(P3MacroRepeatedTemplatePrototype.Substitute(d,input),diagnostic);return Task.CompletedTask; }
    private static Task SpansAsync()
    {
        string d=Definition("*",",",",");const string input=" alpha, /*x*/ beta";var r=P3MacroRepeatedTemplatePrototype.Substitute(d,input);Success(r,"alpha , beta");AssertEx.Equal(3,r.Tokens.Count);
        AssertEx.Equal("alpha",input.Substring(r.Tokens[0].InputSpan!.Value.Start,r.Tokens[0].InputSpan!.Value.Length));
        AssertEx.Equal("beta",input.Substring(r.Tokens[2].InputSpan!.Value.Start,r.Tokens[2].InputSpan!.Value.Length));
        AssertEx.Equal("$x",d.Substring(r.Tokens[0].TemplateSpan.Start,r.Tokens[0].TemplateSpan.Length));
        AssertEx.Equal(",",d.Substring(r.Tokens[1].TemplateSpan.Start,r.Tokens[1].TemplateSpan.Length));AssertEx.True(r.Tokens[1].InputSpan is null,"Generated separator has template provenance only.");return Task.CompletedTask;
    }
    private static Task OptionalAsync() { string d=Definition("?","","");Success(P3MacroRepeatedTemplatePrototype.Substitute(d,""),"");Success(P3MacroRepeatedTemplatePrototype.Substitute(d,"a"),"a");Rejected(P3MacroRepeatedTemplatePrototype.Substitute(d,"a b"),"RSMAC1004");return Task.CompletedTask; }
    private static Task TokensAsync() { string d=Definition("*",",",",");Success(P3MacroRepeatedTemplatePrototype.Substitute(d,"a,b",new(){MaximumOutputTokens=3}),"a , b");Rejected(P3MacroRepeatedTemplatePrototype.Substitute(d,"a,b",new(){MaximumOutputTokens=2}),"RSMAC1005");return Task.CompletedTask; }
    private static Task BytesAsync() { string d=Definition("*",",",",");Success(P3MacroRepeatedTemplatePrototype.Substitute(d,"a,b",new(){MaximumOutputBytes=5}),"a , b");Rejected(P3MacroRepeatedTemplatePrototype.Substitute(d,"a,b",new(){MaximumOutputBytes=4}),"RSMAC1005");return Task.CompletedTask; }
    private static Task InputAsync() { string d=Definition("*","","");int bytes=d.Length+1;Success(P3MacroRepeatedTemplatePrototype.Substitute(d,"a",new(){Input=new(){MaximumInputBytes=bytes}}),"a");Rejected(P3MacroRepeatedTemplatePrototype.Substitute(d,"a",new(){Input=new(){MaximumInputBytes=bytes-1}}),"RSMAC1005");return Task.CompletedTask; }
    private static Task StepsAsync() { string d=Definition("*","","");var first=P3MacroRepeatedTemplatePrototype.Substitute(d,"a");Success(first,"a");Success(P3MacroRepeatedTemplatePrototype.Substitute(d,"a",new(){Input=new(){MaximumMatcherSteps=first.MatcherSteps}}),"a");Rejected(P3MacroRepeatedTemplatePrototype.Substitute(d,"a",new(){Input=new(){MaximumMatcherSteps=first.MatcherSteps-1}}),"RSMAC1005");return Task.CompletedTask; }
    private static Task CancelAsync() { using var c=new CancellationTokenSource();c.Cancel();AssertEx.Throws<OperationCanceledException>(()=>P3MacroRepeatedTemplatePrototype.Substitute(Definition("*","",""),"a",cancellationToken:c.Token));return Task.CompletedTask; }
    private static Task MidCancelAsync() { string d=Definition("*",",",",");var first=P3MacroRepeatedTemplatePrototype.Substitute(d,"a,b");Success(first,"a , b");using var c=new CancellationTokenSource(TimeSpan.FromSeconds(5));int observed=0;AssertEx.Throws<OperationCanceledException>(()=>P3MacroRepeatedTemplatePrototype.Substitute(d,"a,b",progress:step=>{observed=step;if(step==first.MatcherSteps-1)c.Cancel();},cancellationToken:c.Token));AssertEx.Equal(first.MatcherSteps-1,observed);return Task.CompletedTask; }
    private static void Success(P3MacroRepeatedTemplateResult r,string expected) { AssertEx.True(r.IsSuccessful,r.Message);AssertEx.Equal(expected,r.Output);AssertEx.True(r.TemplateSpan is not null,"Template repetition span retained.");AssertEx.False(r.FormalAcceptanceAllowed,"Prototype cannot accept a leaf.");AssertEx.Equal(0,r.ProductionMacroExpansionsExecuted); }
    private static void Rejected(P3MacroRepeatedTemplateResult r,string diagnostic) { AssertEx.False(r.IsSuccessful,"Unsupported or over-budget repeated template rejects.");AssertEx.Equal(diagnostic,r.Diagnostic);AssertEx.Equal(string.Empty,r.Output);AssertEx.Equal(0,r.Tokens.Count);AssertEx.Equal(0,r.Items.Count);AssertEx.True(r.TemplateSpan is null,"No partial template result publishes."); }
}
