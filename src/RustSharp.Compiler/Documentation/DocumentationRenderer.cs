using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text;
using RustSharp.Syntax;
namespace RustSharp.Compiler.Documentation;
public static class DocumentationRenderer
{
 public static DocumentationOutput Render(DocumentationModel model,DocumentationOptions options,CancellationToken cancellationToken=default)
 {
  ArgumentNullException.ThrowIfNull(model);ArgumentNullException.ThrowIfNull(options);
  try{return RenderCore(model,options,cancellationToken);}catch(DocumentationLimitException){return new(null,new[]{new Diagnostic("RSTD0001","Documentation budget exceeded.",new(0,0))});}
 }
 private static DocumentationOutput RenderCore(DocumentationModel model,DocumentationOptions options,CancellationToken cancellation)
 {
  var budget=new DocumentationBudget(options,cancellation);var quota=new Quota(options.MaximumOutputBytes,budget);var errors=new List<Diagnostic>();
  var pages=new Dictionary<SafeCoreSourceDocument,Page>(ReferenceEqualityComparer.Instance);var outputPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var files=new Dictionary<string,string>(StringComparer.Ordinal);
  foreach(var document in model.Sources)
  {
   budget.Step();if(pages.ContainsKey(document))continue;if(pages.Count+2>options.MaximumFiles)throw new DocumentationLimitException();
   string? path=Relative(document,options,budget,errors);if(path is null)continue;string output="source/"+path+".html";
   if(!outputPaths.Add(output)){Error("RSTD1003","Source output paths collide.",document,new(0,0));continue;}pages.Add(document,new(output));
  }
  foreach(var item in model.Items)
  {
   budget.Step();if(!pages.TryGetValue(item.Source.Document,out var page)){Error("RSTD1004","Source document has no safe output page.",item.Source.Document,item.Source.Span);continue;}
   var span=item.Source.Span;if(span.Start<0||span.Length<0||span.Start>item.Source.Document.Text.Length-span.Length){Error("RSTD1004","Invalid original UTF-16 span.",item.Source.Document,span);continue;}
   if(!page.Spans.TryGetValue(span.Start,out var spans))page.Spans.Add(span.Start,spans=[]);spans.Add(span);
  }
  if(errors.Count>0)return new(null,errors.AsReadOnly());
  foreach(var pair in pages)
  {
   var document=pair.Key;var page=pair.Value;var writer=new Writer(quota);writer.Raw("<!doctype html><html><head><meta charset=\"utf-8\"></head><body><pre>");int line=1,start=0;
   // Bounded by validated source length; offset always advances, including the EOF anchor.
   for(int offset=0;offset<=document.Text.Length;offset++)
   {
    budget.Step();bool lineStart=offset==0||document.Text[offset-1]=='\n';bool hasSpans=page.Spans.TryGetValue(offset,out var spans);
    if(lineStart||hasSpans||offset==document.Text.Length||offset-start>=1024&&!char.IsLowSurrogate(document.Text[offset]))
    {
     writer.Encoded(document.Text[start..offset]);start=offset;
     if(lineStart)writer.Raw("<span id=\"L"+Number(line)+"\"></span>");
     if(hasSpans)foreach(var span in spans!){budget.Step();string anchor="L"+Number(line)+"-span-"+Number(span.Start)+"-"+Number(span.Length);page.Anchors.Add(span,anchor);writer.Raw("<span id=\""+anchor+"\" data-utf16-start=\""+Number(span.Start)+"\" data-utf16-length=\""+Number(span.Length)+"\"></span>");}
    }
    if(offset<document.Text.Length&&document.Text[offset]=='\n')line++;
   }
   writer.Raw("</pre></body></html>");files.Add(page.Output,writer.ToString());
  }
  var html=new Writer(quota);html.Raw("<!doctype html><html><head><meta charset=\"utf-8\"></head><body>");int links=0;
  foreach(var item in model.Items)
  {
   budget.Step();var page=pages[item.Source.Document];html.Raw("<section id=\"");html.Encoded(item.Anchor);html.Raw("\"><h2>");html.Encoded(item.Name);html.Raw("</h2><pre>");html.Encoded(item.Signature);html.Raw("</pre><a href=\"");Link();html.Encoded(EscapePath(page.Output,budget)+"#"+page.Anchors[item.Source.Span]);html.Raw("\">source</a><p>");
   int cursor=0;while(cursor<item.Comment.Length)
   {
    budget.Step();int open=item.Comment.IndexOf('[',cursor);if(open<0){html.Encoded(item.Comment[cursor..]);break;}html.Encoded(item.Comment[cursor..open]);int close=item.Comment.IndexOf(']',open+1);if(close<0){html.Encoded(item.Comment[open..]);break;}
    Link();string target=item.Comment[(open+1)..close];string? href=null;foreach(char character in target){budget.Step();}
    if(!target.Contains("::",StringComparison.Ordinal)&&Uri.TryCreate(target,UriKind.Absolute,out Uri? uri)){if(uri.Scheme==Uri.UriSchemeHttps)href=uri.AbsoluteUri;else Error("RSTD1003","Unsafe documentation link scheme.",item.Source.Document,item.Source.Span);}
    else
    {
     var matches=new HashSet<DocumentationItem>();if(model.Aliases.TryGetValue(target,out var aliases))foreach(var alias in aliases){budget.Step();matches.Add(alias);}
     string identity=target.StartsWith("::",StringComparison.Ordinal)?"crate"+target:target;foreach(var candidate in model.Items){budget.Step();if(candidate.Identity==identity||candidate.Name==target)matches.Add(candidate);}
     if(matches.Count==1)href="#"+matches.Single().Anchor;else Error(matches.Count==0?"RSTD1001":"RSTD1002","Missing or ambiguous documentation link: "+target,item.Source.Document,item.Source.Span);
    }
    if(href is not null){html.Raw("<a href=\"");html.Encoded(href);html.Raw("\">");html.Encoded(target);html.Raw("</a>");}cursor=close+1;
   }
   html.Raw("</p></section>");
  }
  html.Raw("</body></html>");if(errors.Count>0)return new(null,errors.AsReadOnly());string index=html.ToString();files.Add("index.html",index);return new(index,Array.Empty<Diagnostic>()){Files=new ReadOnlyDictionary<string,string>(files)};
  void Link(){budget.Step();if(++links>options.MaximumLinks)throw new DocumentationLimitException();}
  void Error(string code,string message,SafeCoreSourceDocument document,TextSpan span)=>errors.Add(new(code,message,span){SourcePath=document.Path});
 }
 private static string? Relative(SafeCoreSourceDocument document,DocumentationOptions options,DocumentationBudget budget,List<Diagnostic> errors)
 {
  string path=document.Path;foreach(char character in path){budget.Step();}
  if(options.SourceRoot is not null)foreach(char character in options.SourceRoot){budget.Step();}
  if(path.Length==0||path.IndexOfAny(['?','#','\0'])>=0)return Fail("RSTD1003","Unsafe source path.");
  if(Path.IsPathRooted(path))
  {
   if(!Path.IsPathFullyQualified(path)||options.SourceRoot is null||!Path.IsPathFullyQualified(options.SourceRoot))return Fail("RSTD1004","Absolute source paths require a fully qualified SourceRoot.");
   try{path=Path.GetRelativePath(Path.GetFullPath(options.SourceRoot),Path.GetFullPath(path));}catch(ArgumentException){return Fail("RSTD1003","Invalid source root or path.");}catch(NotSupportedException){return Fail("RSTD1003","Invalid source root or path.");}
  }
  path=path.Replace('\\','/');if(path.Contains(':')||Path.IsPathRooted(path))return Fail("RSTD1003","Unsafe source path or source outside SourceRoot.");
  foreach(string segment in path.Split('/')){budget.Step();if(segment is "" or "." or ".."||segment.EndsWith(' ')||segment.EndsWith('.')||segment.IndexOfAny(['<','>','"','|','*'])>=0||segment.Any(char.IsControl))return Fail("RSTD1003","Unsafe source path or source outside SourceRoot.");}
  return path;
  string? Fail(string code,string message){errors.Add(new(code,message,new(0,0)){SourcePath=document.Path});return null;}
 }
 private static string EscapePath(string path,DocumentationBudget budget){var parts=path.Split('/');for(int n=0;n<parts.Length;n++){budget.Step();parts[n]=Uri.EscapeDataString(parts[n]);}return string.Join('/',parts);}
 private static string Number(int value)=>value.ToString(CultureInfo.InvariantCulture);
 private sealed class Page(string output){internal string Output{get;}=output;internal Dictionary<int,HashSet<TextSpan>> Spans{get;}=[];internal Dictionary<TextSpan,string> Anchors{get;}=[];}
 private sealed class Quota(int maximum,DocumentationBudget budget)
 {
  private int bytes;internal void Charge(string value){budget.Step();int count=Encoding.UTF8.GetByteCount(value);if(count>maximum-bytes)throw new DocumentationLimitException();bytes+=count;}
  internal void Step()=>budget.Step();
 }
 private sealed class Writer(Quota quota)
 {
  private readonly StringBuilder text=new();internal void Raw(string value){quota.Charge(value);text.Append(value);}
  internal void Encoded(string value){for(int offset=0;offset<value.Length;){quota.Step();int length=Math.Min(1024,value.Length-offset);if(offset+length<value.Length&&char.IsHighSurrogate(value[offset+length-1])&&char.IsLowSurrogate(value[offset+length]))length++;Raw(WebUtility.HtmlEncode(value.Substring(offset,length)));offset+=length;}}
  public override string ToString()=>text.ToString();
 }
}
