using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCoreGenericAggregateTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("generic aggregates retain nominal templates and infer constructed values", ConstructsAsync),
        new("generic aggregates infer from result contexts and enforce repeated parameters", InferenceAsync),
        new("generic aggregate fields retain visibility across modules", PrivacyAsync),
        new("generic aggregates reject missing duplicate unknown and ill-typed fields", InvalidFieldsAsync),
        new("generic aggregates enforce nominal bounds and finite layout", BoundsAndLayoutAsync),
    ];

    private static Task ConstructsAsync()
    {
        SafeCoreGenericAnalysisProgram program = Accept("struct Box<T>{ value:T } struct Pair<T,U>(T,U); struct Empty; " +
            "fn wrap<T>(x:T)->Box<T>{ Box{value:x} } fn main(){ let b=wrap(12); let x:i32=b.value; " +
            "let p=Pair::<i32,bool>(x,true); let a:i32=p.0; let flag:bool=p.1; let unit=Empty; let t=(a,flag); let y:bool=t.1; }");
        AssertEx.Equal(3, program.NominalTypes.Length);
        SafeCoreGenericNominalDefinition box = program.NominalTypes.Single(static item => item.Id == "crate::Box");
        AssertEx.Equal("value", box.Fields.Single().Name);
        AssertEx.Equal(RustType.Parameter(box.Parameters.Single()), box.Fields.Single().Type);
        AssertEx.True(program.Specializations.Any(static item => item.PlannedSignature.ReturnType.Name == "crate::Box"),
            "Generic constructors must yield closed nominal return evidence.");
        Accept("struct Box<T>{value:T} fn main(){let b=Box{r#value:1};let n:i32=b.r#value;}");
        Accept("struct Box<T>{café:T} fn main(){let b=Box{cafe\u0301:1};let n:i32=b.cafe\u0301;}");
        return Task.CompletedTask;
    }

    private static Task InferenceAsync()
    {
        Accept("struct Box<T>{value:T} fn make<T>()->T{make::<T>()} fn wrap<T>(x:T)->Box<T>{Box::<T>{value:x}} " +
            "fn main(){let b:Box<bool>=Box{value:make()}; let x:bool=b.value;}");
        Accept("struct Pair<T>(T,T); fn take<T>(p:Pair<T>)->T{p.0} fn main(){let x:i32=take(Pair(1,2));}");
        Reject("struct Pair<T>(T,T); fn main(){Pair(1,true);}");
        Reject("struct Box<T>{value:T} fn bad<T>(x:T)->Box<bool>{Box{value:x}} fn main(){}");
        return Task.CompletedTask;
    }

    private static Task PrivacyAsync()
    {
        Accept("mod a{pub struct Box<T>{pub value:T} pub struct Pair<T>(pub T); } " +
            "fn main(){let b=a::Box{value:1};let x:i32=b.value;let p=a::Pair(true);let flag:bool=p.0;}");
        Reject("mod a{pub struct Box<T>{value:T}} fn main(){let b=a::Box{value:1};}");
        Reject("mod a{pub struct Pair<T>(T);} fn main(){a::Pair(1);}");
        Reject("mod a{pub struct Box<T>{value:T} pub fn make()->Box<i32>{Box{value:1}}} fn main(){let b=a::make();b.value;}");
        Accept("mod a{pub struct Box<T>{pub(crate) value:T}} fn main(){let b=a::Box{value:1};b.value;}");
        return Task.CompletedTask;
    }

    private static Task InvalidFieldsAsync()
    {
        foreach (string body in new[] { "Box{}", "Box{value:1,value:2}", "Box{other:1}", "Box::<bool>{value:1}", "Box::<bool,i32>{value:true}" })
            Reject("struct Box<T>{value:T} fn main(){" + body + ";}");
        Reject("struct Box<T>{value:T} fn main(){let b=Box{value:1};b.other;}");
        Reject("fn main(){let pair=(1,true);pair.2;}");
        Reject("struct Pair<T>(T); fn main(){Pair(1,2);}");
        return Task.CompletedTask;
    }

    private static Task BoundsAndLayoutAsync()
    {
        Accept("trait Mark{} impl Mark for i32{} struct Box<T:Mark>{value:T} " +
            "fn wrap<T:Mark>(x:T)->Box<T>{Box{value:x}} fn main(){wrap(1);}");
        Reject("trait Mark{} struct Box<T:Mark>{value:T} fn main(){Box{value:1};}");
        Reject("struct Recursive<T>{value:T,next:Recursive<T>} fn main(){}");
        Accept("struct Box<T>{value:T} struct Pair<T>(Box<T>,Box<T>); fn main(){let p=Pair(Box{value:1},Box{value:2});let x:i32=p.0.value;}");
        return Task.CompletedTask;
    }

    private static SafeCoreGenericAnalysisProgram Accept(string source)
    {
        SafeCoreGenericAnalysisResult result = Check(source);
        AssertEx.True(result.IsSuccessful, string.Join("; ", result.Diagnostics));
        return result.Program!;
    }

    private static void Reject(string source)
    {
        SafeCoreGenericAnalysisResult result = Check(source);
        AssertEx.False(result.IsSuccessful, source);
        AssertEx.True(result.Program is null && result.Diagnostics.Count > 0, "Invalid aggregate source must expose no partial evidence.");
    }

    private static SafeCoreGenericAnalysisResult Check(string source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return SafeCoreGenericAnalysis.Check(SafeCoreSyntax.Parse(source, "aggregate.rs", null, deadline.Token),
            cancellationToken: deadline.Token);
    }
}
