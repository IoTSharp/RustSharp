using System.Diagnostics;
using RustSharp.Semantics;
using RustSharp.Syntax;

namespace RustSharp.Tests;

internal static class SafeCorePatternClosureTests
{
    private sealed record SourceCase(string Source, string? Diagnostic = null);
    private static SourceCase Pass(string source) => new(source);
    private static SourceCase Fail(string source, string diagnostic = "RST2002") => new(source, diagnostic);

    public static IReadOnlyList<TestCase> All { get; } =
    [
        Group("type analysis infers closures and reifies noncapturing closures",
            Pass("fn f()->i32 { let identity=|x|x; identity(2) }"),
            Pass("fn f()->i32 { let increment=|x|x+1; increment(2) }"),
            Pass("fn f()->i32 { let increment:fn(i32)->i32=|x|x+1; increment(2) }"),
            Pass("fn f()->bool { let identity=|x:bool|->bool{x}; identity(true) }"),
            Pass("fn f(){let closure=||loop{};let x=1;}"),
            Fail("fn f(){let id=|x|x;id(1);id(true);}"),
            Fail("fn f(){let id:fn(i32)->i32=|x:bool|x;}"),
            Fail("fn f()->i32{let closure=||loop{};}")),
        Group("type analysis tracks closure captures and mutable receivers",
            Pass("fn f()->i32{let base=2;let add=|x|x+base;add(3)}"),
            Pass("fn f(){let mut count=0;let mut add=||{count+=1;};add();}"),
            Pass("fn f(){let mut count=0;let mut add=||{count+=1;};let r=&mut add;r();}"),
            Pass("fn f()->i32{let count=3;let get=move||count;get()}"),
            Fail("fn f(){let base=2;let add:fn(i32)->i32=|x|x+base;}"),
            Fail("fn f(){let mut count=0;let add=||{count+=1;};add();}","RST2003"),
            Fail("fn f(){let mut count=0;let mut add=||{count+=1;};let r=&add;r();}","RST2003")),
        Group("type analysis bounds closure Copy obligations for array repetition",
            Pass("fn f()->i32{let value=||3;let callbacks=[value;2];callbacks[0]()}"),
            Pass("fn f()->i32{let add=|x:i32|x+1;let callbacks=[add;2];callbacks[1](2)}"),
            Pass("fn f(){let mut count=0;let add=||{count+=1;};let callbacks=[add;0];}"),
            Pass("fn f(){let mut count=0;let add=||{count+=1;};let callbacks=[add;1];}"),
            Fail("fn f(){let mut count=0;let add=||{count+=1;};let callbacks=[add;2];}","RST2001"),
            Fail("fn f(){let count=0;let get=||count;let callbacks=[get;2];}","RST2001")),
        Group("type analysis preserves reference pattern binding modes",
            Pass("fn f(pair:&(i32,bool))->i32{let(value,_)=pair;*value}"),
            Pass("fn f(pair:&mut(i32,bool)){let(value,_)=pair;*value+=1;}"),
            Pass("fn f(value:&i32)->i32{let &copied=value;copied}"),
            Pass("fn f(value:&i32)->i32{let (&copied)=value;copied}"),
            Pass("fn f(){let mut value=1;let ref mut r=value;*r=2;}"),
            Fail("fn f(){let value=1;let ref mut r=value;}","RST2003"),
            Fail("fn f(pair:&(i32,bool)){let(ref mut value,_)=pair;}","RST2003")),
        Group("type analysis destructures nominal ADTs and enforces constructor identity",
            Pass("struct S{x:i32,y:bool} fn f(s:S)->i32{let S{x,..}=s;x}"),
            Pass("struct Pair(i32,bool);fn f(p:Pair)->i32{let Pair(x,_)=p;x}"),
            Pass("enum E{A(bool),B} fn f(e:E)->i32{match e{E::A(true)=>1,E::A(false)=>2,E::B=>3}}"),
            Pass("enum E{A{x:bool},B} fn f(e:E)->i32{match e{E::A{x:true}=>1,E::A{x:false}=>2,E::B=>3}}"),
            Fail("struct A(i32);struct B(i32);fn f(a:A){let B(x)=a;}"),
            Fail("struct S{x:i32,y:bool}fn f(s:S){let S{x}=s;}","RST2012"),
            Fail("enum E{A,B}fn f(e:E){let E::A=e;}","RST2011")),
        Group("type analysis proves tuple bool and guarded match coverage",
            Pass("fn f(v:bool)->i32{match v{true=>1,false=>0}}"),
            Pass("fn f(v:(bool,bool))->i32{match v{(true,_)|(false,true)=>1,(false,false)=>0}}"),
            Pass("fn f(v:bool)->i32{match v{true if v=>1,_=>0}}"),
            Pass("enum Empty{}fn f(v:Empty)->i32{match v{}}"),
            Fail("fn f(v:bool)->i32{match v{true=>1}}","RST2009"),
            Fail("fn f(v:bool)->i32{match v{true if v=>1,false=>0}}","RST2009"),
            Fail("fn f(v:(bool,bool))->i32{match v{(true,_)=>1,(false,true)=>0}}","RST2009")),
        Group("type analysis proves scalar interval and alternative pattern coverage",
            Pass("fn f(v:u8)->i32{match v{0..=127=>1,128..=255=>2}}"),
            Pass("fn f(v:i8)->i32{match v{-128..=-1=>1,0..=127=>2}}"),
            Pass("fn f(v:char)->i32{match v{'\\0'..='\\u{d7ff}'=>1,'\\u{e000}'..='\\u{10ffff}'=>2}}"),
            Pass("fn f(v:i32)->i32{match v{x @ 0..=10=>x,_=>0}}"),
            Fail("fn f(v:u8)->i32{match v{0..=127=>1,129..=255=>2}}","RST2009"),
            Fail("fn f(v:u8)->i32{match v{5..=1=>1,_=>0}}","RST2012"),
            Fail("fn f(v:(i32,bool)){match v{(x,_)|(_,x)=>()}}")),
        Group("type analysis checks fixed arrays slice rest and variable slice coverage",
            Pass("fn f(v:&[i32])->i32{match v{[]=>0,[head,..]=>*head}}"),
            Pass("fn f(v:&[i32])->i32{match v{[first,middle @ ..,last]=>*first+*last,_=>0}}"),
            Pass("fn f(v:[i32;3])->i32{let[first,..,last]=v;first+last}"),
            Pass("fn f(v:&[bool])->i32{match v{[]=>0,[true,..]=>1,[false,..]=>2}}"),
            Fail("fn f(v:&[i32])->i32{match v{[head,..]=>*head}}","RST2009"),
            Fail("fn f(v:[i32;2]){let[a,b,c]=v;}"),
            Fail("fn f(v:&[i32]){let[first,..]=v;}","RST2011")),
        Group("type analysis requires let-else divergence and separates closure control flow",
            Pass("fn f(v:&[i32])->i32{let[head,..]=v else{return 0;};*head}"),
            Pass("fn f(v:bool)->i32{let true=v else{return 0;};1}"),
            Pass("enum E{A(i32),B}fn f(v:E)->i32{let E::A(x)=v else{return 0;};x}"),
            Fail("fn f(v:&[i32]){let[head,..]=v else{};}","RST2002"),
            Fail("fn f(v:bool)->i32{let true=v else{return 0;};}"),
            Fail("fn f(v:bool){let true=v;}","RST2011"),
            Fail("fn f(){loop{let closure=||{break;};break;}}","RST2002")),
    ];

    private static TestCase Group(string name, params SourceCase[] sources) => new(name, () => RunAsync(sources));

    private static Task RunAsync(IReadOnlyList<SourceCase> sources)
    {
        AssertEx.True(sources.Count is > 0 and <= 8, "A pattern and closure source group is bounded to eight cases.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var clock = Stopwatch.StartNew();
        for (var index = 0; index < sources.Count; index++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            AssertEx.True(clock.Elapsed < TimeSpan.FromSeconds(20), "The pattern source group exceeded its deadline.");
            SourceCase source = sources[index];
            SafeCoreSyntaxResult syntax = SafeCoreSyntax.Parse(source.Source, "patterns-closures.rs",
                new() { Timeout = TimeSpan.FromSeconds(5) }, cancellation.Token);
            AssertEx.True(syntax.IsSuccessful, $"Syntax: {source.Source}\n{Format(syntax.Diagnostics)}");
            SafeCoreHirResult hir = SafeCoreHirLowering.Lower(syntax, new()
            {
                Timeout = TimeSpan.FromSeconds(5), CancellationToken = cancellation.Token,
                NameResolution = new() { EnableTypeSystemExtensions = true, Timeout = TimeSpan.FromSeconds(5) },
            });
            AssertEx.True(hir.IsSuccessful, $"HIR: {source.Source}\n{Format(hir.Diagnostics)}");
            SafeCoreTypeAnalysisResult result = SafeCoreTypeAnalysis.Check(hir,
                new() { Timeout = TimeSpan.FromSeconds(5) }, cancellation.Token);
            string detail = $"Source {index + 1}: {source.Source}\n{Format(result.Diagnostics)}";
            if (source.Diagnostic is null) AssertEx.True(result.IsSuccessful, detail);
            else
            {
                AssertEx.False(result.IsSuccessful, $"Expected {source.Diagnostic}. {detail}");
                AssertEx.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == source.Diagnostic), detail);
                AssertEx.True(result.Diagnostics.All(diagnostic => diagnostic.SourcePath == "patterns-closures.rs" &&
                    diagnostic.Span.Start >= 0 && diagnostic.Span.End <= source.Source.Length), "Pattern diagnostics need stable valid spans.");
            }
        }
        return Task.CompletedTask;
    }

    private static string Format(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}
