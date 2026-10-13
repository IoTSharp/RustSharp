using System.Net;
using System.Text;
using RustSharp.Compiler.Documentation;
using RustSharp.Syntax;
namespace RustSharp.Tests;
internal static class P2DocumentationDepthTests
{
 private static readonly (string Name,string Source,string[] Identities,string? Error)[] Cases=[
 ("public-field-visibility","pub struct Row { pub x:i32, hidden:i32 }",["crate::Row","crate::Row::x"],null),
 ("enum-variants-fields","pub enum E { A, B(i32), C { x:i32 } }",["crate::E::A","crate::E::B","crate::E::B::0","crate::E::C","crate::E::C::x"],null),
 ("trait-associated-items","pub trait T { fn f(); type A; const C:i32; }",["crate::T::f","crate::T::A","crate::T::C"],null),
 ("glob-reexport-distinct-links","pub mod inner { pub struct W; pub struct V; } pub use inner::*;\n/// [crate::W] [crate::V]\npub fn f() {}",["crate::inner::W","crate::inner::V"],null),
 ("labelled-local-link","pub struct W;\n/// [label](crate::W)\npub fn f() {}",["crate::W"],null),
 ("labelled-https-title","/// [site](https://example.org \"title & <b>\")\npub fn f() {}",["crate::f"],null),
 ("labelled-unsafe-scheme","/// [label](javascript:alert)\npub fn f() {}",[],"RSTD1003")];
 internal static IReadOnlyList<TestCase> All{get;}=Cases.Select((test,index)=>new TestCase("P2-08.03 depth "+test.Name,()=>Run(index))).ToArray();
 private static Task Run(int index)
 {
  var test=Cases[index];const string path="depth.rs";var syntax=SafeCoreSyntax.Parse(test.Source,path);var document=new SafeCoreSourceDocument(path,test.Source,Encoding.UTF8.GetBytes(test.Source));var map=new SafeCoreSourceMap(new[]{document},new[]{new SafeCoreSourceMapSegment(new(0,test.Source.Length),document,new(0,test.Source.Length))},test.Source.Length);
  var model=DocumentationModel.Create(syntax,map,new());AssertEx.True(model.IsSuccessful,"Actual syntax/model failed; preserve case: "+test.Name);
  foreach(string identity in test.Identities)AssertEx.True(model.Model!.Items.Any(item=>item.Identity==identity),"Public inventory missing "+identity);
  if(index==0){AssertEx.False(model.Model!.Items.Any(static item=>item.Identity=="crate::Row::hidden"),"Private field leaked.");AssertEx.True(model.Model.Items.Any(static item=>item.Identity=="crate::Row::x"&&item.Kind=="public-field"&&item.Signature.Contains("i32",StringComparison.Ordinal)),"Public field kind/type lost.");}
  if(index==2){string[] kinds=["associated-function","associated-type","associated-const"];foreach(string kind in kinds)AssertEx.True(model.Model!.Items.Any(item=>item.Kind==kind),"Trait associated kind lost.");}
  var output=DocumentationRenderer.Render(model.Model!,new());
  if(test.Error is not null){AssertEx.True(!output.IsSuccessful&&output.Html is null&&output.Files.Count==0&&output.Diagnostics.Any(d=>d.Code==test.Error&&d.SourcePath==path&&d.Span.Length>0),"Unsafe target must produce source-spanned RSTD1003 without publication.");return Task.CompletedTask;}
  AssertEx.True(output.IsSuccessful,"Real renderer did not satisfy depth contract: "+test.Name+" "+string.Join("; ",output.Diagnostics.Select(static d=>d.Code+":"+d.Message)));
  if(index==3){AssertCanonical("crate::inner::W");AssertCanonical("crate::inner::V");}
  if(index==4){AssertCanonical("crate::W");AssertEx.True(output.Html!.Contains(">label</a>",StringComparison.Ordinal),"Labelled symbol link lost label.");}
  if(index==5)AssertEx.True(output.Html!.Contains("href=\"https://example.org/\"",StringComparison.Ordinal)&&output.Html.Contains(">site</a>",StringComparison.Ordinal)&&output.Html.Contains("title=\"title &amp; &lt;b&gt;\"",StringComparison.Ordinal),"HTTPS target/label/title encoding lost.");
  return Task.CompletedTask;
  void AssertCanonical(string identity){var item=model.Model!.Items.Single(item=>item.Identity==identity);AssertEx.True(output.Html!.Contains("href=\"#"+WebUtility.HtmlEncode(item.Anchor)+"\"",StringComparison.Ordinal),"Canonical href absent: "+identity);AssertEx.True(output.Html.Contains("id=\""+WebUtility.HtmlEncode(item.Anchor)+"\"",StringComparison.Ordinal),"Canonical fragment target absent: "+identity);}
 }
}