using System.Security.Cryptography;
using System.Text;
using RustSharp.Compiler.Documentation;
namespace RustSharp.Tests;
internal static class P2DocumentationWorkspaceTests
{
 private static readonly string[] Names=["virtual-two-members","root-plus-member","dependency-family-diagnosed","feature-family-diagnosed","cancel-between-members","aggregate-file-budget","existing-target-preserved"];
 internal static List<string> ReceiptRoots{get;}=[];
 internal static IReadOnlyList<TestCase> All{get;}=Names.Select((name,index)=>new TestCase("P2-08.03 workspace "+name,()=>RunAsync(index))).ToArray();
 private static async Task RunAsync(int mode)
 {
  string root=Path.Combine(Path.GetTempPath(),"RustSharpDocWorkspace-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);ReceiptRoots.Add(root);
  WritePackage(Path.Combine(root,"a"),"alpha");WritePackage(Path.Combine(root,"b"),"beta");string manifest=Path.Combine(root,"Cargo.toml");
  if(mode==1){WritePackage(root,"root_package");File.AppendAllText(manifest,"[workspace]\nmembers=[\"a\",\"b\"]\n");}else File.WriteAllText(manifest,"[workspace]\nmembers=[\"a\",\"b\"]\n");
  if(mode==2)File.AppendAllText(Path.Combine(root,"b","Cargo.toml"),"[dependencies]\nalpha={path=\"../a\"}\n");
  if(mode==3)File.AppendAllText(Path.Combine(root,"a","Cargo.toml"),"[features]\nextra=[]\n");
  byte[] before=File.ReadAllBytes(manifest);using var cancelled=new CancellationTokenSource();var limits=new DocumentationOptions();Action<int,CancellationToken>? progress=null;
  if(mode==4)progress=(count,_)=>{if(count==1)cancelled.Cancel();};if(mode==5)limits=new(){MaximumFiles=4};
  if(mode==4){AssertEx.Throws<OperationCanceledException>(()=>DocumentationWorkspace.Create(manifest,limits,progress,cancelled.Token));return;}
  var result=DocumentationWorkspace.Create(manifest,limits,progress,cancelled.Token);
  if(mode is 2 or 3 or 5){AssertEx.True(!result.IsSuccessful&&result.Output is null&&result.Diagnostics.Any(static d=>d.Code=="RSTD3001"),"Unsupported family or aggregate budget must reject without partial output.");return;}
  AssertEx.True(result.IsSuccessful,"Actual Cargo/workspace/model/renderer path failed.");int packages=mode==1?3:2;AssertEx.Equal(packages*2+1,result.Output!.Files.Count);
  string target=Path.Combine(root,"published");if(mode==6){Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"user.txt"),"user-original");}
  var published=await DocumentationPublisher.PublishNewAsync(result.Output,target,new()).ConfigureAwait(false);
  if(mode==6){AssertEx.False(published.Published,"Existing target must remain untouched.");AssertEx.Equal("user-original",File.ReadAllText(Path.Combine(target,"user.txt")));}
  else{AssertEx.True(published.Published,"Aggregated package-relative href payload must publish atomically.");foreach(var file in result.Output.Files)AssertEx.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Value))),Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(target,file.Key)))));}
  AssertEx.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(manifest)),"Original manifest changed.");
  static void WritePackage(string path,string name){Directory.CreateDirectory(Path.Combine(path,"src"));File.WriteAllText(Path.Combine(path,"Cargo.toml"),"[package]\nname=\""+name+"\"\nversion=\"0.1.0\"\nedition=\"2021\"\n[lib]\npath=\"src/lib.rs\"\n");File.WriteAllText(Path.Combine(path,"src","lib.rs"),"pub fn f() {}\n");}
 }
}