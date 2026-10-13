using System.Diagnostics;
using RustSharp.Compiler;
using RustSharp.Compiler.Documentation;
using RustSharp.Syntax;
namespace RustSharp.Cli;
public sealed record DocumentationCommandOptions
{
 public TimeSpan Timeout{get;init;}=TimeSpan.FromSeconds(10);
}
public sealed record DocumentationCommandProgress(string Phase,int SourceFiles,CancellationToken CancellationToken);
public static class DocumentationCommand
{
 // Initial single-root-package/target publication. Workspace, dependencies and feature activation remain unsupported.
 public static async Task<int> RunAsync(string[] arguments,TextWriter standardOutput,TextWriter standardError,DocumentationCommandOptions? limits=null,Action<DocumentationCommandProgress>? progress=null,CancellationToken cancellationToken=default)
 {
  ArgumentNullException.ThrowIfNull(arguments);ArgumentNullException.ThrowIfNull(standardOutput);ArgumentNullException.ThrowIfNull(standardError);
  if(arguments.Length is not (2 or 4)||arguments[0]!="doc"||arguments.Length==4&&arguments[2]!="--output"){standardError.WriteLine("rsc: usage: rsc doc <Cargo.toml> [--output <new-directory>]");return 2;}
  limits??=new();if(limits.Timeout<=TimeSpan.Zero||limits.Timeout>TimeSpan.FromSeconds(10)){standardError.WriteLine("rsc: documentation timeout must be positive and at most10 seconds.");return 2;}
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(limits.Timeout);var clock=Stopwatch.StartNew();
  ConsoleCancelEventHandler cancel=(_,eventArgs)=>{eventArgs.Cancel=true;deadline.Cancel();};Console.CancelKeyPress+=cancel;
  try
  {
   Remaining();string manifest=Path.GetFullPath(arguments[1]);if(!string.Equals(Path.GetFileName(manifest),"Cargo.toml",StringComparison.OrdinalIgnoreCase)){standardError.WriteLine("rsc: documentation requires Cargo.toml.");return 2;}
   var cargo=CargoWorkspace.LoadV1(manifest,new(){Timeout=Remaining()},deadline.Token);if(!cargo.IsSuccessful)return Fail(cargo.Diagnostics);Notify("cargo-loaded",0);
   var package=cargo.RootPackageOrNull;if(package is null||cargo.IsVirtualWorkspace||package.Targets.Count!=1||package.Dependencies.Count!=0||package.Features.Count!=0||package.Targets[0].RequiredFeatures.Count!=0){standardError.WriteLine("rsc: RSTDCLI1001: initial documentation requires one root-package target without dependencies or feature activation; workspace selection remains unsupported.");return 1;}
   string root=Path.GetDirectoryName(manifest)!;string source=package.Targets[0].SourcePath;var workspace=SafeCoreWorkspace.Load(source,new(){Timeout=Remaining(),MaximumFiles=1024,MaximumTotalBytes=16777216,MaximumExpandedLength=1000000,MaximumOperations=1000000},deadline.Token);if(!workspace.IsSuccessful)return Fail(workspace.Diagnostics);Notify("workspace-loaded",workspace.SourceMap!.Documents.Count);
   var syntax=SafeCoreSyntax.Parse(workspace.SourceText,source,new(){Timeout=Remaining(),MaximumSourceLength=1000000,MaximumTokens=250000,MaximumOperations=1000000},deadline.Token);
   var options=new DocumentationOptions(){Timeout=Remaining(),SourceRoot=root};var model=DocumentationModel.Create(syntax,workspace.SourceMap!,options,deadline.Token);if(!model.IsSuccessful)return Fail(model.Diagnostics);
   var rendered=DocumentationRenderer.Render(model.Model!,options with{Timeout=Remaining()},deadline.Token);if(!rendered.IsSuccessful)return Fail(rendered.Diagnostics);Notify("rendered",workspace.SourceMap!.Documents.Count);
   string target=arguments.Length==4?Path.GetFullPath(arguments[3]):Path.Combine(root,"rsc-doc");var published=await DocumentationPublisher.PublishNewAsync(rendered,target,options with{Timeout=Remaining()},cancellationToken:deadline.Token).ConfigureAwait(false);
   if(!published.Published){if(published.RetainedLeasePath is not null)standardError.WriteLine("rsc: retained documentation lease: "+published.RetainedLeasePath);return Fail(published.Diagnostics);}
   standardOutput.WriteLine("Documented "+target);return 0;
  }
  catch(OperationCanceledException){standardError.WriteLine("rsc: documentation cancelled or timed out.");return 130;}
  catch(TimeoutException){standardError.WriteLine("rsc: documentation timed out.");return 130;}
  catch(Exception exception)when(exception is ArgumentException or IOException or UnauthorizedAccessException){standardError.WriteLine("rsc: "+exception.Message);return 1;}
  finally{Console.CancelKeyPress-=cancel;}
  TimeSpan Remaining(){deadline.Token.ThrowIfCancellationRequested();TimeSpan remaining=limits.Timeout-clock.Elapsed;if(remaining<=TimeSpan.Zero)throw new OperationCanceledException(deadline.Token);return remaining;}
  void Notify(string phase,int count){Remaining();progress?.Invoke(new(phase,count,deadline.Token));Remaining();}
  int Fail(IReadOnlyList<Diagnostic> diagnostics){for(int index=0;index<diagnostics.Count&&index<100;index++)standardError.WriteLine(diagnostics[index].SourcePath+": "+diagnostics[index].Code+": "+diagnostics[index].Message);return deadline.IsCancellationRequested||clock.Elapsed>=limits.Timeout?130:1;}
 }
}