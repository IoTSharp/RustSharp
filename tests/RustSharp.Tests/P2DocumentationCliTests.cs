using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RustSharp.Cli;
using RustSharp.Compiler;
using RustSharp.Compiler.Documentation;
using RustSharp.Syntax;
namespace RustSharp.Tests;
internal static class P2DocumentationCliTests
{
 private static readonly string[] Names=["initial-success-hashes","nested-modules-source-hashes","existing-target-unchanged","missing-manifest","virtual-workspace-unsupported","dependency-unsupported","feature-unsupported","multiple-targets-unsupported","invalid-arguments","invalid-option","noncargo-input","unsafe-module-path","missing-module","malformed-source","pre-cancel","mid-workspace-cancel","deadline-after-workspace","unsafe-output-parent","unsafe-documentation-scheme","missing-documentation-symbol"];
 internal static List<string> ReceiptRoots{get;}=[];
 internal static IReadOnlyList<TestCase> All{get;}=Names.Select((name,index)=>new TestCase("P2-08.03 CLI "+name,()=>RunAsync(index))).ToArray();
 private static async Task RunAsync(int mode)
 {
  string root=Path.Combine(Path.GetTempPath(),"RustSharpDocCli-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"src"));ReceiptRoots.Add(root);
  string manifest=Path.Combine(root,"Cargo.toml"),sourcePath=Path.Combine(root,"src","lib.rs"),target=Path.Combine(root,"published");
  string manifestText="[package]\nname=\"doc_fixture\"\nversion=\"0.1.0\"\nedition=\"2021\"\n[lib]\npath=\"src/lib.rs\"\n";
  string source="//中文😀\r\npub fn f() {}\r\n";
  if(mode==1){source="pub mod child;\r\npub fn f() {}\r\n";File.WriteAllText(Path.Combine(root,"src","child.rs"),"//子😀\r\npub fn g() {}\r\n",new UTF8Encoding(false));}
  if(mode==4)manifestText="[workspace]\nmembers=[]\n";
  if(mode==5){string dep=Path.Combine(root,"dep");Directory.CreateDirectory(Path.Combine(dep,"src"));File.WriteAllText(Path.Combine(dep,"Cargo.toml"),"[package]\nname=\"dep_fixture\"\nversion=\"0.1.0\"\nedition=\"2021\"\n");File.WriteAllText(Path.Combine(dep,"src","lib.rs"),"pub fn d() {}");manifestText+="[dependencies]\ndep_fixture={path=\"dep\"}\n";}
  if(mode==6)manifestText+="[features]\nextra=[]\n";
  if(mode==7){manifestText+="[[bin]]\nname=\"extra\"\npath=\"src/main.rs\"\n";File.WriteAllText(Path.Combine(root,"src","main.rs"),"fn main() {}");}
  if(mode==11){source="#[path=\"../outside.rs\"] pub mod child;";File.WriteAllText(Path.Combine(root,"outside.rs"),"pub fn outside() {}");}
  if(mode==12)source="pub mod child;";
  if(mode==13)source="pub fn";
  if(mode==18)source="/// [javascript:alert]\npub fn f() {}";
  if(mode==19)source="/// [crate::Missing]\npub fn f() {}";
  File.WriteAllText(manifest,manifestText,new UTF8Encoding(false));File.WriteAllText(sourcePath,source,new UTF8Encoding(false));
  string manifestHash=Hash(manifest),sourceHash=Hash(sourcePath);
  if(mode==2){Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"user.txt"),"user-original");}
  if(mode==3)manifest=Path.Combine(root,"missing","Cargo.toml");
  string[] args=["doc",manifest,"--output",target];if(mode==8)args=["doc"];if(mode==9)args=["doc",manifest,"--invalid",target];if(mode==10)args=["doc",sourcePath];
  if(mode==17){string ordinary=Path.Combine(root,"ordinary");Directory.CreateDirectory(ordinary);string linked=Path.Combine(root,"linked");Directory.CreateSymbolicLink(linked,ordinary);target=Path.Combine(linked,"published");args=["doc",manifest,"--output",target];}
  using var cancelled=new CancellationTokenSource();if(mode==14)cancelled.Cancel();bool workspaceObserved=false;
  Action<DocumentationCommandProgress>? progress=null;var limits=new DocumentationCommandOptions();
  if(mode==15)progress=state=>{if(state.Phase=="workspace-loaded"){workspaceObserved=state.SourceFiles>0;cancelled.Cancel();}};
  if(mode==16){limits=new(){Timeout=TimeSpan.FromMilliseconds(500)};progress=state=>{if(state.Phase=="workspace-loaded"){workspaceObserved=state.SourceFiles>0;AssertEx.True(state.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)),"Bounded real deadline did not signal.");}};}
  using var stdout=new StringWriter(CultureInfo.InvariantCulture);using var stderr=new StringWriter(CultureInfo.InvariantCulture);
  int exit=await DocumentationCommand.RunAsync(args,stdout,stderr,limits,progress,cancelled.Token).ConfigureAwait(false);
  if(mode is 0 or 1)
  {
   AssertEx.Equal(0,exit,stderr.ToString());AssertEx.True(stdout.ToString().Contains("Documented ",StringComparison.Ordinal),"Real command success output missing.");
   var workspace=SafeCoreWorkspace.Load(sourcePath);AssertEx.True(workspace.IsSuccessful,"Real expected workspace failed.");var syntax=SafeCoreSyntax.Parse(workspace.SourceText,sourcePath);var options=new DocumentationOptions(){SourceRoot=root};var model=DocumentationModel.Create(syntax,workspace.SourceMap!,options);AssertEx.True(model.IsSuccessful,"Actual model failed.");var expected=DocumentationRenderer.Render(model.Model!,options);AssertEx.True(expected.IsSuccessful,"Actual renderer failed.");
   foreach(var file in expected.Files){string actual=Path.Combine(target,file.Key);AssertEx.True(File.Exists(actual),"Actual Files payload missing.");AssertEx.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Value))),Hash(actual),"Real published payload hash differs.");}
   if(mode==1)AssertEx.True(expected.Files.ContainsKey("source/src/child.rs.html"),"External child source page missing.");
  }
  else
  {
   int expected=mode is 8 or 9 or 10?2:mode is 14 or 15 or 16?130:1;AssertEx.Equal(expected,exit,stderr.ToString());
   if(mode==2)AssertEx.Equal("user-original",File.ReadAllText(Path.Combine(target,"user.txt")));else AssertEx.False(Directory.Exists(target),"Failure/cancel partially published.");
   if(mode is 15 or 16)AssertEx.True(workspaceObserved,"Cancellation/deadline must follow actual workspace loading.");
   AssertEx.True(stderr.GetStringBuilder().Length>0,"Failure diagnostic required.");
  }
  AssertEx.Equal(manifestHash,Hash(Path.Combine(root,"Cargo.toml")),"Original manifest mutated.");AssertEx.Equal(sourceHash,Hash(sourcePath),"Original source mutated.");
  AssertEx.Equal(0,Directory.EnumerateFileSystemEntries(root,".rustsharp-docs-*",SearchOption.TopDirectoryOnly).Take(3).Count(),"Owned CLI lease leaked.");
 }
 private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}