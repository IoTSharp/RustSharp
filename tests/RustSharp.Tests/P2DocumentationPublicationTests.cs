using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using RustSharp.Compiler.Documentation;
using RustSharp.Syntax;
namespace RustSharp.Tests;
internal static class P2DocumentationPublicationTests
{
 private static readonly string[] Names=["success-files-hashes","success-unicode-crlf","escape-path","absolute-file-path","unsafe-scheme","missing-file","missing-fragment","reparse-parent","case-path-conflict","existing-target-unchanged","pre-cancel","mid-failure-cleanup","mid-cancel-cleanup","aggregate-budget","operation-budget","cleanup-identity","foreign-lease-object","target-race-unchanged","unknown-no-throw","empty-folder-replacement-identity","mid-stage-reparse-no-external-write"];
 internal static List<string> ReceiptRoots {get;}=[];
 internal static IReadOnlyList<TestCase> All {get;}=Names.Select((name,index)=>new TestCase("P2-08.03 publication "+name,()=>RunAsync(index))).ToArray();
 private static DocumentationOutput ActualOutput(bool unicode=false)
 {
  string text=unicode?"//中文😀\r\npub fn f() {}\r\n":"pub fn f() {}";string name=unicode?"子 文.rs":"main.rs";
  var document=new SafeCoreSourceDocument(name,text,Encoding.UTF8.GetBytes(text));var map=new SafeCoreSourceMap(new[]{document},new[]{new SafeCoreSourceMapSegment(new(0,text.Length),document,new(0,text.Length))},text.Length);
  var model=DocumentationModel.Create(SafeCoreSyntax.Parse(text,name),map,new());AssertEx.True(model.IsSuccessful,"Real documentation model required.");var output=DocumentationRenderer.Render(model.Model!,new());AssertEx.True(output.IsSuccessful,"Actual renderer required.");return output;
 }
 private static async Task RunAsync(int mode)
 {
  string root=Path.Combine(Path.GetTempPath(),"RustSharpDocPublication-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);ReceiptRoots.Add(root);string target=Path.Combine(root,"published");var output=mode==20?MultiSourceOutput():ActualOutput(mode==1);var options=new DocumentationOptions();using var cancelled=new CancellationTokenSource();Action<DocumentationPublicationProgress>? progress=null;DateTime replacementBirth=default;
  if(mode is 2 or 3 or 8){var files=new Dictionary<string,string>(output.Files,StringComparer.Ordinal);files.Add(mode==2?"../user.txt":mode==3?Path.Combine(root,"user.txt"):"INDEX.html","preserve");output=output with{Files=new ReadOnlyDictionary<string,string>(files)};}
  if(mode is 4 or 5 or 6){string index=mode==4?"<a href=\"javascript:alert(1)\">x</a>":mode==5?"<a href=\"missing.html\">x</a>":"<a href=\"source/main.rs.html#absent\">x</a>";var files=new Dictionary<string,string>(output.Files,StringComparer.Ordinal){["index.html"]=index};output=new(index,Array.Empty<Diagnostic>()){Files=new ReadOnlyDictionary<string,string>(files)};}
  if(mode==7){string ordinary=Path.Combine(root,"ordinary");Directory.CreateDirectory(ordinary);string link=Path.Combine(root,"linked");Directory.CreateSymbolicLink(link,ordinary);target=Path.Combine(link,"published");}
  if(mode==9){Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"user.txt"),"user-original");}
  if(mode==10)cancelled.Cancel();
  if(mode==11)progress=state=>{if(state.FilesStaged==1)throw new IOException("actual staged-file failure");};
  if(mode==12)progress=state=>{if(state.FilesStaged==1)cancelled.Cancel();};
  if(mode==13)options=new(){MaximumOutputBytes=output.Files.Values.Sum(static value=>Encoding.UTF8.GetByteCount(value))-1};
  if(mode==14)options=new(){MaximumOperations=1};
  if(mode==15)progress=state=>{File.WriteAllText(state.LeasePath+".lease","foreign-lease-owner");throw new IOException("lease identity changed");};
  if(mode==16)progress=state=>{File.WriteAllText(Path.Combine(state.LeasePath,"foreign.txt"),"foreign-original");throw new IOException("unknown object inserted");};
  if(mode==17)progress=state=>{if(state.FilesStaged==1){Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"user.txt"),"user-original");}};
  if(mode==18)progress=state=>{if(state.FilesStaged==1)File.WriteAllText(Path.Combine(state.LeasePath,"foreign.txt"),"foreign-original");};
  if(mode==19)progress=state=>{if(state.FilesStaged==1){string folder=Path.Combine(state.LeasePath,"source");DateTime originalBirth=Directory.GetCreationTimeUtc(folder);Directory.Move(folder,Path.Combine(root,"moved-original-source"));Directory.CreateDirectory(folder);Directory.SetCreationTimeUtc(folder,originalBirth.AddMinutes(1));replacementBirth=Directory.GetCreationTimeUtc(folder);AssertEx.True(replacementBirth!=originalBirth,"Real replacement directory identity must differ.");}};
  if(mode==20)progress=state=>{if(state.FilesStaged==1){string outside=Path.Combine(root,"outside");Directory.CreateDirectory(outside);File.WriteAllText(Path.Combine(outside,"user.txt"),"outside-original");string folder=Path.Combine(state.LeasePath,"source");Directory.Move(folder,Path.Combine(root,"moved-original-source"));Directory.CreateSymbolicLink(folder,outside);}};
  var result=await DocumentationPublisher.PublishNewAsync(output,target,options,progress,cancelled.Token).ConfigureAwait(false);
  if(mode is 0 or 1)
  {
   AssertEx.True(result.Published&&result.Diagnostics.Count==0&&result.RetainedLeasePath is null,"Whole batch should publish.");
   foreach(var file in output.Files){byte[] actual=File.ReadAllBytes(Path.Combine(target,file.Key));AssertEx.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Value))),Convert.ToHexString(SHA256.HashData(actual)),"Published original payload hash differs.");}
   AssertEx.Equal(output.Files.Count,CountFiles(target));
  }
  else
  {
   AssertEx.True(!result.Published&&result.Diagnostics.Any(static diagnostic=>diagnostic.Code=="RSTD2001"),"Failure/cancel must diagnose without publication.");
   if(mode is 9 or 17)AssertEx.Equal("user-original",File.ReadAllText(Path.Combine(target,"user.txt")));else AssertEx.False(Directory.Exists(target),"Failed batch was partially published.");
   if(mode is 15 or 16 or 18 or 19 or 20){AssertEx.True(result.RetainedLeasePath is not null,"Unknown lease identity/object must be retained and reported.");if(mode==15)AssertEx.Equal("foreign-lease-owner",File.ReadAllText(result.RetainedLeasePath!+".lease"));else if(mode is 16 or 18)AssertEx.Equal("foreign-original",File.ReadAllText(Path.Combine(result.RetainedLeasePath!,"foreign.txt")));else if(mode==19){string replacement=Path.Combine(result.RetainedLeasePath!,"source");AssertEx.True(Directory.Exists(replacement),"Replaced empty directory must not be deleted.");AssertEx.Equal(replacementBirth,Directory.GetCreationTimeUtc(replacement));AssertEx.True(File.Exists(Path.Combine(root,"moved-original-source","main.rs.html")),"Moved original payload must remain.");}else{string outside=Path.Combine(root,"outside");AssertEx.Equal("outside-original",File.ReadAllText(Path.Combine(outside,"user.txt")));AssertEx.Equal(1,Directory.EnumerateFileSystemEntries(outside).Take(3).Count(),"Publisher wrote outside the lease through a reparse ancestor.");AssertEx.True((File.GetAttributes(Path.Combine(result.RetainedLeasePath!,"source"))&FileAttributes.ReparsePoint)!=0,"Unknown lease reparse must remain.");AssertEx.True(File.Exists(Path.Combine(root,"moved-original-source","main.rs.html")),"Moved original source page must remain.");}}
   else AssertEx.True(result.RetainedLeasePath is null,"Owned failure/cancel lease must be reclaimed.");
  }
  if(mode is not 15 and not 16 and not 18 and not 19 and not 20)AssertEx.Equal(0,Directory.EnumerateFileSystemEntries(root,".rustsharp-docs-*",SearchOption.TopDirectoryOnly).Take(3).Count(),"Owned staging/marker leak.");
 }
 private static DocumentationOutput MultiSourceOutput()
 {
  string firstText="pub fn f() {}\n",secondText="pub fn g() {}\n";string expanded=firstText+secondText;
  var first=new SafeCoreSourceDocument("main.rs",firstText,Encoding.UTF8.GetBytes(firstText));var second=new SafeCoreSourceDocument("other.rs",secondText,Encoding.UTF8.GetBytes(secondText));
  var map=new SafeCoreSourceMap(new[]{first,second},new[]{new SafeCoreSourceMapSegment(new(0,firstText.Length),first,new(0,firstText.Length)),new SafeCoreSourceMapSegment(new(firstText.Length,secondText.Length),second,new(0,secondText.Length))},expanded.Length);
  var model=DocumentationModel.Create(SafeCoreSyntax.Parse(expanded,"main.rs"),map,new());AssertEx.True(model.IsSuccessful,"Actual two-source model required.");var output=DocumentationRenderer.Render(model.Model!,new());AssertEx.True(output.IsSuccessful&&output.Files.Count==3,"Two source pages and index required.");return output;
 }
 private static int CountFiles(string root)
 {
  int files=0,entries=0;var stack=new Stack<string>();stack.Push(root);var clock=System.Diagnostics.Stopwatch.StartNew();
  for(int round=0;stack.Count>0&&round<16&&clock.Elapsed<TimeSpan.FromSeconds(2);round++)foreach(string path in Directory.EnumerateFileSystemEntries(stack.Pop())){if(++entries>32)throw new InvalidOperationException("Output enumeration exceeded bound.");if(Directory.Exists(path))stack.Push(path);else files++;}
  AssertEx.Equal(0,stack.Count,"Output enumeration did not finish within bound.");return files;
 }
}