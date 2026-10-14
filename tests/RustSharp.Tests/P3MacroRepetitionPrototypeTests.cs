using RustSharp.Conformance;

namespace RustSharp.Tests;

internal static class P3MacroRepetitionPrototypeTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("P3 repetition prototype star matches zero identifiers", () => MatchAsync("*","","")),
        new("P3 repetition prototype star matches one and many identifiers", StarAsync),
        new("P3 repetition prototype plus matches one and many identifiers", PlusAsync),
        new("P3 repetition prototype optional matches zero or one identifier", OptionalAsync),
        new("P3 repetition prototype comma star returns real item spans", CommaStarAsync),
        new("P3 repetition prototype comma plus matches one and many", CommaPlusAsync),
        new("P3 repetition prototype rejects zero plus items", () => RejectAsync(Definition("+",""),"","RSMAC1004")),
        new("P3 repetition prototype rejects multiple optional items", () => RejectAsync(Definition("?",""),"a b","RSMAC1004")),
        new("P3 repetition prototype rejects trailing comma", () => RejectAsync(Definition("*",","),"a,","RSMAC1004")),
        new("P3 repetition prototype rejects wrong separator", () => RejectAsync(Definition("+",","),"a;b","RSMAC1004")),
        new("P3 repetition prototype rejects flat semicolon outside candidate subset", () => RejectAsync(Definition("*",";"),"a;b","RSMAC1001")),
        new("P3 repetition prototype rejects nested repetition", () => RejectAsync("macro_rules! m { ($([$($x:ident),*]);*)=>{}; }","[a]","RSMAC1001")),
        new("P3 repetition prototype rejects multiple captured metavariables", () => RejectAsync("macro_rules! m { ($($x:ident $y:ident)*)=>{}; }","a b","RSMAC1001")),
        new("P3 repetition prototype enforces aggregate byte exact and over boundary", BytesAsync),
        new("P3 repetition prototype propagates pre entry cancellation", CancelAsync),
        new("P3 repetition prototype propagates mid item cancellation", MidCancelAsync),
    ];
    private static string Definition(string op,string separator) => "macro_rules! m { ($($x:ident)"+separator+op+")=>{}; }";
    private static Task MatchAsync(string op,string separator,string input) { Success(P3MacroRepetitionPrototype.Match(Definition(op,separator),input),0);return Task.CompletedTask; }
    private static Task StarAsync() { Success(P3MacroRepetitionPrototype.Match(Definition("*",""),"a"),1);Success(P3MacroRepetitionPrototype.Match(Definition("*",""),"a b c"),3);return Task.CompletedTask; }
    private static Task PlusAsync() { Success(P3MacroRepetitionPrototype.Match(Definition("+",""),"a"),1);Success(P3MacroRepetitionPrototype.Match(Definition("+",""),"a b"),2);return Task.CompletedTask; }
    private static Task OptionalAsync() { Success(P3MacroRepetitionPrototype.Match(Definition("?",""),""),0);Success(P3MacroRepetitionPrototype.Match(Definition("?",""),"a"),1);return Task.CompletedTask; }
    private static Task CommaStarAsync()
    {
        string d=Definition("*",",");Success(P3MacroRepetitionPrototype.Match(d,""),0);const string input=" alpha, /*x*/ beta";var r=P3MacroRepetitionPrototype.Match(d,input);Success(r,2);
        AssertEx.Equal("alpha",input.Substring(r.Items[0].InputSpan.Start,r.Items[0].InputSpan.Length));AssertEx.Equal("beta",input.Substring(r.Items[1].InputSpan.Start,r.Items[1].InputSpan.Length));
        var span=r.Items[0].DefinitionCaptureSpan;AssertEx.Equal("$x:ident",d.Substring(span.Start,span.Length));AssertEx.Equal(1,r.Items[1].Index);return Task.CompletedTask;
    }
    private static Task CommaPlusAsync() { Success(P3MacroRepetitionPrototype.Match(Definition("+",","),"a"),1);Success(P3MacroRepetitionPrototype.Match(Definition("+",","),"a,b"),2);return Task.CompletedTask; }
    private static Task RejectAsync(string definition,string input,string diagnostic) { Rejected(P3MacroRepetitionPrototype.Match(definition,input),diagnostic);return Task.CompletedTask; }
    private static Task BytesAsync() { string d=Definition("*",",");const string input="a,b";int bytes=d.Length+input.Length;Success(P3MacroRepetitionPrototype.Match(d,input,new(){MaximumInputBytes=bytes}),2);Rejected(P3MacroRepetitionPrototype.Match(d,input,new(){MaximumInputBytes=bytes-1}),"RSMAC1005");return Task.CompletedTask; }
    private static Task CancelAsync() { using var c=new CancellationTokenSource();c.Cancel();AssertEx.Throws<OperationCanceledException>(()=>P3MacroRepetitionPrototype.Match(Definition("*",""),"a",cancellationToken:c.Token));return Task.CompletedTask; }
    private static Task MidCancelAsync()
    {
        string d=Definition("*",",");var first=P3MacroRepetitionPrototype.Match(d,"alpha,beta");Success(first,2);using var c=new CancellationTokenSource(TimeSpan.FromSeconds(5));int observed=0;
        AssertEx.Throws<OperationCanceledException>(()=>P3MacroRepetitionPrototype.Match(d,"alpha,beta",progress:step=>{observed=step;if(step==first.MatcherSteps-2)c.Cancel();},cancellationToken:c.Token));AssertEx.Equal(first.MatcherSteps-2,observed);return Task.CompletedTask;
    }
    private static void Success(P3MacroRepetitionResult r,int count) { AssertEx.True(r.IsSuccessful,r.Message);AssertEx.Equal(count,r.ItemCount);AssertEx.True(r.RuleSpan is not null && r.RepetitionSpan is not null,"Matched definition spans retained.");AssertEx.False(r.FormalAcceptanceAllowed,"Prototype cannot accept a leaf.");AssertEx.Equal(0,r.ProductionMacroExpansionsExecuted); }
    private static void Rejected(P3MacroRepetitionResult r,string diagnostic) { AssertEx.False(r.IsSuccessful,"Unsupported or malformed repetition rejects.");AssertEx.Equal(diagnostic,r.Diagnostic);AssertEx.Equal(0,r.ItemCount);AssertEx.True(r.RuleSpan is null && r.RepetitionSpan is null,"No partial matched structure publishes."); }
}
