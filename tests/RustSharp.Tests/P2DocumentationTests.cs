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
  if(i is 6 or 7 or 10){string code=i==6?"RSTD1001":i==7?"RSTD1002":"RSTD1003";AssertEx.True(!output.IsSuccessful&&output.Html is null&&output.Files.Count==0&&output.Diagnostics.Any(d=>d.Code==code&&d.SourcePath==path),"Source-spanned failure must not publish: "+code);return Task.CompletedTask;}
  AssertEx.True(output.IsSuccessful,"Actual renderer failed: "+Names[i]);
  switch(i){case 0:AssertEx.True(RequiredItemKinds.All(k=>result.Model!.Items.Any(item=>item.Kind==k)),"Frozen item kinds reduced.");break;case 1:AssertEx.Equal(1,result.Model!.Items.Count);break;case 2:AssertEx.True(result.Model!.Items[0].Signature.Contains("'a",StringComparison.Ordinal)&&result.Model.Items[0].Signature.Contains("T: Copy",StringComparison.Ordinal),"Signature lost bounds.");break;case 3:AssertEx.True(output.Html!.Contains("source/dir/a%20b.rs.html#L3-span-",StringComparison.Ordinal),"Original source path/line/span lost.");SourcePages();break;case 9:AssertEx.True(output.Html!.Contains("&lt;script&gt;",StringComparison.Ordinal)&&!output.Html.Contains("<script>",StringComparison.Ordinal),"Unsafe markup.");break;case 11:SourceBudgets(result.Model!,output);var limited=DocumentationModel.Create(syntax,map,new(){MaximumItems=1});AssertEx.True(!limited.IsSuccessful&&limited.Diagnostics.Any(static d=>d.Code=="RSTD0001"),"Item budget must diagnose.");var bytes=DocumentationRenderer.Render(result.Model!,new(){MaximumOutputBytes=1});AssertEx.True(!bytes.IsSuccessful&&bytes.Diagnostics.Any(static d=>d.Code=="RSTD0001"),"Complete HTML quota missing.");using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();AssertEx.Throws<OperationCanceledException>(()=>DocumentationModel.Create(syntax,map,new(),cancelled.Token));}break;}
  return Task.CompletedTask;
 }
 private static void SourcePages()
 {
  string root=Path.Combine(Path.GetTempPath(),"RustSharpDocSourceRoot");
  string main="//中国😀\r\npub mod child;\r\n";
  string child="//文😀\r\npub fn f() {}\r\n";
  string prefix="pub mod child {";string expanded=prefix+child+"}";
  var first=new SafeCoreSourceDocument(Path.Combine(root,"src","main.rs"),main,Encoding.UTF8.GetBytes(main));
  var second=new SafeCoreSourceDocument(Path.Combine(root,"src","子 文.rs"),child,Encoding.UTF8.GetBytes(child));
  var map=new SafeCoreSourceMap(new[]{first,second},new[]{
   new SafeCoreSourceMapSegment(new(0,prefix.Length),first,new(main.IndexOf("pub mod",StringComparison.Ordinal),"pub mod child;".Length),true),
   new SafeCoreSourceMapSegment(new(prefix.Length,child.Length),second,new(0,child.Length)),
   new SafeCoreSourceMapSegment(new(prefix.Length+child.Length,1),first,new(main.IndexOf(';'),1),true)},expanded.Length);
  var syntax=SafeCoreSyntax.Parse(expanded,first.Path);var result=DocumentationModel.Create(syntax,map,new(){SourceRoot=root});
  AssertEx.True(result.IsSuccessful,"Expanded external module must use actual original source map.");
  var output=DocumentationRenderer.Render(result.Model!,new(){SourceRoot=root});
  AssertEx.True(output.IsSuccessful&&output.Files.Count==3,"Index and both original source pages required.");
  AssertEx.Equal(output.Html!,output.Files["index.html"]);
  foreach(var item in result.Model!.Items)
  {
   string relative=Path.GetRelativePath(root,item.Source.Document.Path).Replace('\\','/');string file="source/"+relative+".html";
   int line=1+item.Source.Document.Text[..item.Source.Span.Start].Count(static c=>c=='\n');
   string anchor=$"L{line}-span-{item.Source.Span.Start}-{item.Source.Span.Length}";
   string href=string.Join("/",file.Split('/').Select(Uri.EscapeDataString))+"#"+anchor;
   AssertEx.True(output.Html!.Contains("href=\""+System.Net.WebUtility.HtmlEncode(href)+"\"",StringComparison.Ordinal),"Source href must identify the actual emitted file.");
   AssertEx.True(output.Files[file].Contains("id=\""+anchor+"\"",StringComparison.Ordinal),"Every UTF-16 fragment must exist in its output file.");
   AssertEx.True(output.Files[file].Contains("id=\"L2\"",StringComparison.Ordinal),"CRLF counts as one source line.");
   AssertEx.True(output.Files[file].Contains("data-utf16-start=\""+item.Source.Span.Start+"\"",StringComparison.Ordinal),"Original UTF-16 start lost.");
  }
  var function=result.Model.Items.Single(static item=>item.Name=="f");AssertEx.Equal(child.IndexOf("pub fn",StringComparison.Ordinal),function.Source.Span.Start);
  var functionSyntax=((SafeCoreModuleSyntax)syntax.Root!.Items.Single()).Items.OfType<SafeCoreFunctionSyntax>().Single();
  AssertEx.Equal("pub",syntax.GetText(functionSyntax.Visibility.Span));
  AssertEx.Equal(child.IndexOf("pub fn",StringComparison.Ordinal),map.MapSpan(functionSyntax.Visibility.Span).Span.Start);
  AssertEx.Equal(child.IndexOf("fn f",StringComparison.Ordinal),map.MapSpan(functionSyntax.Span).Span.Start);
  AssertEx.True(function.Signature.StartsWith("pub fn",StringComparison.Ordinal),"Documentation signature and source span must include the actual visibility modifier.");
  AssertEx.True(System.Net.WebUtility.HtmlDecode(output.Files["source/src/子 文.rs.html"]).Contains("//文😀\r\n",StringComparison.Ordinal),"Source Unicode/surrogate pair or CRLF was changed.");
  var noRoot=DocumentationRenderer.Render(result.Model,new());AssertEx.True(!noRoot.IsSuccessful&&noRoot.Files.Count==0&&noRoot.Diagnostics.Any(d=>d.Code=="RSTD1004"&&d.SourcePath==first.Path),"Missing SourceRoot must diagnose absolute originals.");
  var outside=DocumentationRenderer.Render(result.Model,new(){SourceRoot=Path.Combine(root,"other")});AssertEx.True(!outside.IsSuccessful&&outside.Files.Count==0&&outside.Diagnostics.Any(d=>d.Code=="RSTD1003"&&d.SourcePath==second.Path),"External document outside root must diagnose and publish nothing.");
  var unexpanded=SafeCoreSyntax.Parse(main,first.Path);var singleMap=new SafeCoreSourceMap(new[]{first},new[]{new SafeCoreSourceMapSegment(new(0,main.Length),first,new(0,main.Length))},main.Length);
  var missingModule=DocumentationModel.Create(unexpanded,singleMap,new(){SourceRoot=root});AssertEx.True(!missingModule.IsSuccessful&&missingModule.Diagnostics.Any(d=>d.Code=="RSTD1001"&&d.SourcePath==first.Path),"Unexpanded public external module must retain original-path diagnostic.");
 }
 private static void SourceBudgets(DocumentationModel model,DocumentationOutput output)
 {
  int total=output.Files.Values.Sum(static text=>Encoding.UTF8.GetByteCount(text));
  var exact=DocumentationRenderer.Render(model,new(){MaximumOutputBytes=total});AssertEx.True(exact.IsSuccessful,"Aggregate output quota must accept the exact full byte count.");
  var shortByOne=DocumentationRenderer.Render(model,new(){MaximumOutputBytes=total-1});BudgetFailure(shortByOne,"Index and source page closing bytes must be included.");
  BudgetFailure(DocumentationRenderer.Render(model,new(){MaximumFiles=1}),"Source page and index must both count against file budget.");
  BudgetFailure(DocumentationRenderer.Render(model,new(){MaximumLinks=1}),"Every emitted source link must count against link budget.");
  BudgetFailure(DocumentationRenderer.Render(model,new(){MaximumOperations=16}),"Source scans and output operations must count against operation budget.");
  BudgetFailure(DocumentationRenderer.Render(model,new(){Timeout=TimeSpan.Zero}),"Nonpositive wall budget must fail.");
  using var cancelled=new CancellationTokenSource();cancelled.Cancel();AssertEx.Throws<OperationCanceledException>(()=>DocumentationRenderer.Render(model,new(),cancelled.Token));
  static void BudgetFailure(DocumentationOutput failure,string message)=>AssertEx.True(!failure.IsSuccessful&&failure.Html is null&&failure.Files.Count==0&&failure.Diagnostics.Any(static d=>d.Code=="RSTD0001"),message);
 }
}
