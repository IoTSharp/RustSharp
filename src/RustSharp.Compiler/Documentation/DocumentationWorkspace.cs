using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using RustSharp.Syntax;
namespace RustSharp.Compiler.Documentation;
public sealed record DocumentationWorkspaceResult(DocumentationOutput? Output,IReadOnlyList<Diagnostic> Diagnostics)
{public bool IsSuccessful=>Output is not null&&Diagnostics.Count==0;}
public static class DocumentationWorkspace
{
 // Select only actual root/member manifests. Dependency graphs and feature activation are diagnosed rather than omitted.
 public static DocumentationWorkspaceResult Create(string manifestPath,DocumentationOptions? limits=null,Action<int,CancellationToken>? progress=null,CancellationToken cancellationToken=default)
 {
  ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);limits??=new();var clock=Stopwatch.StartNew();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
  try
  {
   var budget=new DocumentationBudget(limits,deadline.Token);deadline.CancelAfter(limits.Timeout);string manifest=Path.GetFullPath(manifestPath);
   var cargo=CargoWorkspace.LoadV1(manifest,new(){Timeout=Remaining()},deadline.Token);if(!cargo.IsSuccessful)return new(null,cargo.Diagnostics);
   var roots=new HashSet<string>(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);if(cargo.RootPackageOrNull is not null)roots.Add(cargo.RootPackageOrNull.ManifestPath);
   foreach(string member in cargo.WorkspaceMemberPaths){budget.Step();roots.Add(member);}if(roots.Count is <1 or >64)return Fail("No supported workspace root/member selection.");
   var selected=cargo.Packages.Where(package=>roots.Contains(package.ManifestPath)).OrderBy(static package=>package.ManifestPath,StringComparer.Ordinal).ToArray();if(selected.Length!=roots.Count)return Fail("Workspace member metadata is incomplete.");
   var files=new Dictionary<string,string>(StringComparer.Ordinal);var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var index=new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"></head><body><ul>");int bytes=0,sourceCharacters=0,itemCount=0,linkCount=0;
   for(int number=0;number<selected.Length&&number<64;number++)
   {
    budget.Step();var package=selected[number];if(package.Targets.Count!=1||package.Dependencies.Count!=0||package.Features.Count!=0||package.Targets[0].RequiredFeatures.Count!=0)return Fail("Dependencies, feature activation or multiple targets require explicit documentation selection; no family was omitted.",package.ManifestPath);
    string packageRoot=Path.GetDirectoryName(package.ManifestPath)!;string source=package.Targets[0].SourcePath;var workspace=SafeCoreWorkspace.Load(source,new(){Timeout=Remaining(),MaximumFiles=1024,MaximumExpandedLength=1000000},deadline.Token);if(!workspace.IsSuccessful)return new(null,workspace.Diagnostics);
    foreach(var document in workspace.SourceMap!.Documents){budget.Step();if(document.Text.Length>1000000-sourceCharacters)throw new DocumentationLimitException();sourceCharacters+=document.Text.Length;}
    var syntax=SafeCoreSyntax.Parse(workspace.SourceText,source,new(){Timeout=Remaining()},deadline.Token);var options=limits with{Timeout=Remaining(),SourceRoot=packageRoot};var model=DocumentationModel.Create(syntax,workspace.SourceMap!,options,deadline.Token);if(!model.IsSuccessful)return new(null,model.Diagnostics);if(model.Model!.Items.Count>limits.MaximumItems-itemCount)throw new DocumentationLimitException();itemCount+=model.Model.Items.Count;var output=DocumentationRenderer.Render(model.Model!,options with{Timeout=Remaining()},deadline.Token);if(!output.IsSuccessful)return new(null,output.Diagnostics);
    string prefix="packages/pkg-"+number.ToString("D3",CultureInfo.InvariantCulture)+"/";foreach(var file in output.Files){budget.Step();Add(prefix+file.Key,file.Value);}
    index.Append("<li><a href=\"").Append(prefix).Append("index.html\">").Append(WebUtility.HtmlEncode(package.Name)).Append("</a></li>");progress?.Invoke(number+1,deadline.Token);Remaining();
   }
   index.Append("</ul></body></html>");string html=index.ToString();Add("index.html",html);var combined=new DocumentationOutput(html,Array.Empty<Diagnostic>()){Files=new ReadOnlyDictionary<string,string>(files)};return new(combined,Array.Empty<Diagnostic>());
   void Add(string path,string value){budget.Step();for(int position=0,iteration=0;position<value.Length&&iteration<=limits.MaximumLinks;iteration++){budget.Step();int found=value.IndexOf("<a ",position,StringComparison.Ordinal);if(found<0)break;if(++linkCount>limits.MaximumLinks)throw new DocumentationLimitException();position=found+3;}if(files.Count>=limits.MaximumFiles||!names.Add(path))throw new DocumentationLimitException();int count=Encoding.UTF8.GetByteCount(value);if(count>limits.MaximumOutputBytes-bytes)throw new DocumentationLimitException();bytes+=count;files.Add(path,value);}
  }
  catch(DocumentationLimitException){return Fail("Workspace documentation aggregate limit exceeded.");}
  TimeSpan Remaining(){deadline.Token.ThrowIfCancellationRequested();TimeSpan remaining=limits.Timeout-clock.Elapsed;if(remaining<=TimeSpan.Zero)throw new OperationCanceledException(deadline.Token);return remaining;}
  DocumentationWorkspaceResult Fail(string message,string? path=null)=>new(null,new[]{new Diagnostic("RSTD3001",message,new(0,0)){SourcePath=path??manifestPath}});
 }
}