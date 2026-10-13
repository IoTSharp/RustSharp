using System.Text;
using RustSharp.Compiler.Documentation;
using RustSharp.Syntax;
namespace RustSharp.Tests;
internal static class P2DocumentationTests
{
 private static readonly string[] RequiredItemKinds=["module","function","struct","enum","type-alias","const","trait","associated-function","associated-type","associated-const","public-field","variant"];
 private static readonly string[] Names=["doc-public-items","doc-private-items","doc-generics-signatures","doc-source-links","doc-local-links","doc-cross-module-links","doc-missing-link","doc-ambiguous-link","doc-malformed-comment","doc-escape-markup","doc-unsafe-link","doc-budget"];
 private static readonly string[] Sources=["pub trait T { fn f(); type A; const C:i32; } pub struct W { pub x:i32 } pub enum E { A } pub type X=i32; pub const C:i32=1; pub mod m {} pub fn f() {}","mod hidden { pub fn h() {} } pub(crate) fn p() {} pub fn v() {}","pub fn f<'a,T: Copy>(x:&'a T)->&'a T { x }","\n\npub fn f() {}","pub struct W;\n/// [crate::W]\npub fn f() {}","mod hidden { pub struct W; } pub use hidden::W as E;\n/// [crate::E] [https://example.org]\npub fn f() {}","/// [crate::Missing]\npub fn f() {}","pub mod a { pub struct W; } pub mod b { pub struct W; }\n/// [W]\npub fn f() {}","/// bare\rcarriage\npub fn f() {}","/// <script>alert(1)</script> & \"quote\"\npub fn f() {}","/// [javascript:alert]\npub fn f() {}","pub fn a() {} pub fn b() {}"];
 public static IReadOnlyList<TestCase> All { get; }=Enumerable.Range(0,12).Select(i=>new TestCase("P2-08.03 "+Names[i],()=>Run(i))).ToArray();
 private static Task Run(int i)
 {
  string source=Sources[i]; string path=i==3?"dir/a b.rs":"main.rs";var syntax=SafeCoreSyntax.Parse(source,path);var document=new SafeCoreSourceDocument(path,source,Encoding.UTF8.GetBytes(source));var map=new SafeCoreSourceMap(new[]{document},new[]{new SafeCoreSourceMapSegment(new(0,source.Length),document,new(0,source.Length))},source.Length);var result=DocumentationModel.Create(syntax,map,new());
  if(i==8){AssertEx.True(!result.IsSuccessful&&result.Diagnostics.Any(static d=>d.Code=="RSL1014"),"Malformed doc must diagnose before rendering.");return Task.CompletedTask;}
  AssertEx.True(result.IsSuccessful,"Real syntax/source-map model failed: "+Names[i]);var output=DocumentationRenderer.Render(result.Model!,new());
  if(i is 6 or 7 or 10){string code=i==6?"RSTD1001":i==7?"RSTD1002":"RSTD1003";AssertEx.True(!output.IsSuccessful&&output.Html is null&&output.Diagnostics.Any(d=>d.Code==code&&d.SourcePath==path),"Source-spanned failure must not publish: "+code);return Task.CompletedTask;}
  AssertEx.True(output.IsSuccessful,"Actual renderer failed: "+Names[i]);
  switch(i){case 0:AssertEx.True(RequiredItemKinds.All(k=>result.Model!.Items.Any(item=>item.Kind==k)),"Frozen item kinds reduced.");break;case 1:AssertEx.Equal(1,result.Model!.Items.Count);break;case 2:AssertEx.True(result.Model!.Items[0].Signature.Contains("'a",StringComparison.Ordinal)&&result.Model.Items[0].Signature.Contains("T: Copy",StringComparison.Ordinal),"Signature lost bounds.");break;case 3:AssertEx.True(output.Html!.Contains("dir/a%20b.rs#L3-span-",StringComparison.Ordinal),"Original source path/line/span lost.");break;case 9:AssertEx.True(output.Html!.Contains("&lt;script&gt;",StringComparison.Ordinal)&&!output.Html.Contains("<script>",StringComparison.Ordinal),"Unsafe markup.");break;case 11:var limited=DocumentationModel.Create(syntax,map,new(){MaximumItems=1});AssertEx.True(!limited.IsSuccessful&&limited.Diagnostics.Any(static d=>d.Code=="RSTD0001"),"Item budget must diagnose.");var bytes=DocumentationRenderer.Render(result.Model!,new(){MaximumOutputBytes=1});AssertEx.True(!bytes.IsSuccessful&&bytes.Diagnostics.Any(static d=>d.Code=="RSTD0001"),"Complete HTML quota missing.");using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();AssertEx.Throws<OperationCanceledException>(()=>DocumentationModel.Create(syntax,map,new(),cancelled.Token));}break;}
  return Task.CompletedTask;
 }
}
