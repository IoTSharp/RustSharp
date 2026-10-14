using System.Security.Cryptography;
using System.Text;
using RustSharp.Cli;
using RustSharp.Compiler;
using RustSharp.Compiler.Documentation;
namespace RustSharp.Tests;
internal static class P2DocumentationWorkspaceCliTests
{
 private static readonly string[] Names=["default-virtual-rejected","optin-virtual-two-members","optin-root-plus-members","existing-target-unchanged","dependency-family-rejected","feature-family-rejected","output-byte-budget","real-child-deadline"];
 internal static List<string> ReceiptRoots{get;}=[];
 internal static List<BoundedProcessResult> Processes{get;}=[];
 internal static IReadOnlyList<TestCase> All{get;}=Names.Select((name,index)=>new TestCase("P2-08.03 workspace CLI process "+name,()=>RunCaseAsync(index))).ToArray();
 internal static async Task RunCaseAsync(int mode,CancellationToken cancellationToken=default)
 {
  string root=Path.Combine(Path.GetTempPath(),"RustSharpDocWorkspaceCli-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);ReceiptRoots.Add(root);WritePackage(Path.Combine(root,"a"),"alpha");WritePackage(Path.Combine(root,"b"),"beta");string manifest=Path.Combine(root,"Cargo.toml"),target=Path.Combine(root,"published");
  if(mode==2){WritePackage(root,"root_package");File.AppendAllText(manifest,"[workspace]\nmembers=[\"a\",\"b\"]\n");}else File.WriteAllText(manifest,"[workspace]\nmembers=[\"a\",\"b\"]\n");
  if(mode==4)File.AppendAllText(Path.Combine(root,"b","Cargo.toml"),"[dependencies]\nalpha={path=\"../a\"}\n");if(mode==5)File.AppendAllText(Path.Combine(root,"a","Cargo.toml"),"[features]\nextra=[]\n");
  string original=Hash(manifest);if(mode==3){Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"user.txt"),"user-original");}
  string cli=Path.Combine(AppContext.BaseDirectory,"rsc.dll");if(!File.Exists(cli)||System.Reflection.AssemblyName.GetAssemblyName(cli).Name!="rsc")throw new InvalidOperationException("Exact candidate rsc.dll identity required.");string host=Environment.ProcessPath??throw new InvalidOperationException("Actual dotnet host identity unavailable.");if(Path.GetFileNameWithoutExtension(host)!="dotnet"||!File.Exists(cli))throw new InvalidOperationException("Runner must be launched by exact dotnet with candidate rsc assembly.");
  var arguments=new List<string>{cli,"doc",manifest};if(mode!=0)arguments.Add("--workspace");arguments.Add("--output");arguments.Add(target);if(mode==6){arguments.Add("--max-output-bytes");arguments.Add("1");}if(mode==7){arguments.Add("--timeout-ms");arguments.Add("1");}
  BoundedProcessStarted? started=null;var process=await new BoundedProcessRunner().RunAsync(new(host,arguments,root,TimeSpan.FromSeconds(20),identity=>started=identity),cancellationToken).ConfigureAwait(false);Processes.Add(process);cancellationToken.ThrowIfCancellationRequested();
  AssertEx.True(started is not null&&started.ProcessId>0&&started.StartedAt!=default&&started.ParentProcessId>0,"Actual child PID/birth/parent/command identity required.");AssertEx.True(process.Termination==BoundedProcessTermination.Exited&&!process.OutputTruncated&&!process.OutputReadTimedOut&&!process.ProcessTreeCleanupIncomplete,"Bounded actual child did not complete coherently.");
  int expected=mode is 1 or 2?0:mode==7?130:1;AssertEx.Equal(expected,process.ExitCode??-1,process.StandardError);
  if(mode is 1 or 2){var output=DocumentationWorkspace.Create(manifest,cancellationToken:cancellationToken);AssertEx.True(output.IsSuccessful,"Actual expected workspace failed.");foreach(var file in output.Output!.Files)AssertEx.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Value))),Hash(Path.Combine(target,file.Key)),"Actual child published bytes differ.");}
  else if(mode==3)AssertEx.Equal("user-original",File.ReadAllText(Path.Combine(target,"user.txt")));else AssertEx.False(Directory.Exists(target),"Actual child failure partially published.");
  AssertEx.Equal(original,Hash(manifest),"Original manifest changed.");AssertEx.Equal(0,Directory.EnumerateFileSystemEntries(root,".rustsharp-docs-*",SearchOption.TopDirectoryOnly).Take(3).Count(),"Actual child leaked owned lease.");
  static void WritePackage(string path,string name){Directory.CreateDirectory(Path.Combine(path,"src"));File.WriteAllText(Path.Combine(path,"Cargo.toml"),"[package]\nname=\""+name+"\"\nversion=\"0.1.0\"\nedition=\"2021\"\n[lib]\npath=\"src/lib.rs\"\n");File.WriteAllText(Path.Combine(path,"src","lib.rs"),"pub fn f() {}\n");}
 }
 private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}