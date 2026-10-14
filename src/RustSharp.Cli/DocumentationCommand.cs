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
  if(HasWorkspaceOption(arguments))return await RunWorkspaceAsync(arguments,standardOutput,standardError,limits,cancellationToken).ConfigureAwait(false);
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
 private static bool HasWorkspaceOption(string[] args)
 {
  for(int index=2;index<args.Length&&index<9;index++)
  {
   if(args[index]=="--workspace")return true;
   if(args[index] is "--output" or "--timeout-ms" or "--max-output-bytes")index++;
  }
  return false;
 }
 private static async Task<int> RunWorkspaceAsync(string[] args,TextWriter stdout,TextWriter stderr,DocumentationCommandOptions? existingLimits,CancellationToken cancellationToken)
 {
  if(args.Length is <3 or >9||args[0]!="doc"){stderr.WriteLine("rsc: usage: doc <Cargo.toml> --workspace [--output <new-directory>] [--timeout-ms <1..10000>] [--max-output-bytes <1..16777216>]");return 2;}
  string? output=null;int milliseconds=10000,bytes=16777216;bool selected=false,timeoutSelected=false,bytesSelected=false;
  for(int index=2;index<args.Length&&index<9;index++)
  {
   string option=args[index];if(option=="--workspace"&&!selected){selected=true;continue;}
   if(index+1>=args.Length){stderr.WriteLine("rsc: workspace option value required.");return 2;}string value=args[++index];
   if(option=="--output"&&output is null)output=value;
   else if(option=="--timeout-ms"&&!timeoutSelected&&int.TryParse(value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out milliseconds)&&milliseconds is >=1 and <=10000)timeoutSelected=true;
   else if(option=="--max-output-bytes"&&!bytesSelected&&int.TryParse(value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out bytes)&&bytes is >=1 and <=16777216)bytesSelected=true;
   else{stderr.WriteLine("rsc: invalid workspace option.");return 2;}
  }
  if(!selected){stderr.WriteLine("rsc: --workspace required.");return 2;}TimeSpan timeout=TimeSpan.FromMilliseconds(milliseconds);if(existingLimits is not null&&existingLimits.Timeout<timeout)timeout=existingLimits.Timeout;
  if(timeout<=TimeSpan.Zero||timeout>TimeSpan.FromSeconds(10)){stderr.WriteLine("rsc: invalid workspace timeout.");return 2;}
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(timeout);var clock=Stopwatch.StartNew();ConsoleCancelEventHandler cancel=(_,eventArgs)=>{eventArgs.Cancel=true;deadline.Cancel();};Console.CancelKeyPress+=cancel;
  try
  {
   string manifest=Path.GetFullPath(args[1]);if(!string.Equals(Path.GetFileName(manifest),"Cargo.toml",StringComparison.OrdinalIgnoreCase)){stderr.WriteLine("rsc: Cargo.toml required.");return 2;}
   var limits=new DocumentationOptions(){Timeout=Remaining(),MaximumOutputBytes=bytes};var workspace=DocumentationWorkspace.Create(manifest,limits,cancellationToken:deadline.Token);if(!workspace.IsSuccessful)return Fail(workspace.Diagnostics);
   string target=output is null?Path.Combine(Path.GetDirectoryName(manifest)!,"rsc-workspace-doc"):Path.GetFullPath(output);var published=await DocumentationPublisher.PublishNewAsync(workspace.Output!,target,limits with{Timeout=Remaining()},cancellationToken:deadline.Token).ConfigureAwait(false);
   if(!published.Published){if(published.RetainedLeasePath is not null)stderr.WriteLine("rsc: retained documentation lease: "+published.RetainedLeasePath);return Fail(published.Diagnostics);}stdout.WriteLine("Documented workspace "+target);return 0;
  }
  catch(Exception exception)when(exception is OperationCanceledException or TimeoutException){stderr.WriteLine("rsc: workspace documentation cancelled or timed out.");return 130;}
  catch(Exception exception)when(exception is ArgumentException or IOException or UnauthorizedAccessException){stderr.WriteLine("rsc: "+exception.Message);return 1;}
  finally{Console.CancelKeyPress-=cancel;}
  TimeSpan Remaining(){deadline.Token.ThrowIfCancellationRequested();TimeSpan remaining=timeout-clock.Elapsed;if(remaining<=TimeSpan.Zero)throw new OperationCanceledException(deadline.Token);return remaining;}
  int Fail(IReadOnlyList<Diagnostic> diagnostics){for(int index=0;index<diagnostics.Count&&index<100;index++)stderr.WriteLine(diagnostics[index].SourcePath+": "+diagnostics[index].Code+": "+diagnostics[index].Message);return deadline.IsCancellationRequested||clock.Elapsed>=timeout?130:1;}
 }}