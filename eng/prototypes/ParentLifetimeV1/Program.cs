#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using RustSharp.ProcessContainment;
using RustSharp.ParentPrototype;

internal sealed record CreatorEvidence(ProcessIdentity Identity,bool? ProcessClosed,bool? ThreadClosed);
internal sealed record NegativeEvidence(string Name,string EvidenceKind,bool Rejected,string Reason);
internal sealed class ParentReport {
 public string Scenario {get;set;}="";public string Lease {get;set;}="";public uint ObserverPid {get;set;}public bool Passed {get;set;}public bool ProductionGateIntegrated {get;}
 public List<LifetimeRow> Before {get;}=new();public List<LifetimeRow> After {get;}=new();public ChainProof? Proof {get;set;}
 public List<NegativeEvidence> SyntheticNegatives {get;}=new();public List<string> Errors {get;}=new();public List<TerminalWaitResult> Terminal {get;}=new();
 public LaunchReceipt? Root {get;set;}public CleanupRecord? Cleanup {get;set;}public bool ObserverHandlesClosed {get;set;}public bool EventHandlesClosed {get;set;}public bool ExitedAncestorOriginalStillUsable {get;set;}
}
[JsonSourceGenerationOptions(WriteIndented=true)]
[JsonSerializable(typeof(ParentReport))]
[JsonSerializable(typeof(CreatorEvidence))]
internal sealed partial class ParentJson:JsonSerializerContext{}
internal static class Events {
 [DllImport("kernel32.dll",EntryPoint="CreateEventW",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]private static extern IntPtr Create(IntPtr a,int manual,int initial,string name);
 [DllImport("kernel32.dll",EntryPoint="OpenEventW",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]private static extern IntPtr Open(uint access,int inherit,string name);
 [DllImport("kernel32.dll",EntryPoint="SetEvent",ExactSpelling=true,SetLastError=true)]internal static extern int Signal(KernelHandle value);
 internal static KernelHandle Get(string name,bool create){var handle=new KernelHandle(create?Create(IntPtr.Zero,1,0,name):Open(0x100002,0,name));int error=Marshal.GetLastPInvokeError();try{Win32.CheckHandle(handle,"Owned event");Win32.Need(!create||error!=183,"Fresh event namespace");return handle;}catch{handle.Dispose();throw;}}
 internal static void Wait(KernelHandle handle,int milliseconds,CancellationToken token){var clock=Stopwatch.StartNew();for(int check=0;check<400&&clock.ElapsedMilliseconds<milliseconds;check++){token.ThrowIfCancellationRequested();uint value=Win32.WaitForSingleObject(handle,25);Win32.Need(value is 0 or 258,"Event native wait");if(value==0)return;}throw new TimeoutException("Event400checks/finite wall bound");}
}
internal static class Program {
 private static readonly string[] Names={"child-ready","root-published","tree-ready","exit-parent","stop"};
 private static int Main(string[] args){try{if(args.Length==6&&args[0]=="target")return Target(args[1],args[2],args[3],args[4],args[5]);if(args.Length!=3||args[0]!="control"||args[1] is not ("held" or "exited"))throw new ArgumentException("control held|exited fresh-root");return Control(args[1],args[2]);}catch(Exception error){Console.Error.WriteLine(error);return 1;}}
 private static string AssemblyPath(){string path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"ParentFixture.dll"));Win32.Need(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Actual ParentFixture.dll required");return path;}
 private static string[] Invocation(string role,string scenario,string root,string prefix,string lease){string host=Environment.ProcessPath??throw new IOException("Host missing");return string.Equals(Path.GetFileNameWithoutExtension(host),"dotnet",StringComparison.OrdinalIgnoreCase)?new[]{AssemblyPath(),"target",role,scenario,root,prefix,lease}:new[]{"target",role,scenario,root,prefix,lease};}
 private static int Target(string role,string scenario,string root,string prefix,string lease){
  using var stop=Events.Get(prefix+".stop",false);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
  if(role=="child"){using var ready=Events.Get(prefix+".child-ready",false);Win32.Check(Events.Signal(ready),"Child ready");Events.Wait(stop,15000,deadline.Token);return 0;}
  Win32.Need(role is "root" or "parent","Known target role");string next=role=="root"?"parent":"child";var child=OwnedSuspendedProcess.Start(Environment.ProcessPath!,Invocation(next,scenario,root,prefix,lease),root,true,null);
  try{child.Resume();child.ReleaseCreatorOriginals();File.WriteAllText(Path.Combine(root,role+"-creator.json"),JsonSerializer.Serialize(new CreatorEvidence(child.Identity,child.Receipt.ProcessHandleClosed,child.Receipt.ThreadHandleClosed),ParentJson.Default.CreatorEvidence));}
  catch{child.Dispose();throw;}
  using var published=Events.Get(prefix+".root-published",false);
  if(role=="root"){Win32.Check(Events.Signal(published),"Root receipt published");Events.Wait(stop,15000,deadline.Token);return 0;}
  using var childReady=Events.Get(prefix+".child-ready",false);using var treeReady=Events.Get(prefix+".tree-ready",false);Events.Wait(childReady,7000,deadline.Token);Events.Wait(published,7000,deadline.Token);Win32.Check(Events.Signal(treeReady),"Complete tree receipts closed");
  if(scenario=="exited"){using var exit=Events.Get(prefix+".exit-parent",false);Events.Wait(exit,15000,deadline.Token);}else Events.Wait(stop,15000,deadline.Token);return 0;
 }
 private static CreatorEvidence ReadCreator(string root,string role){string path=Path.Combine(root,role+"-creator.json");Win32.Need(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)==0&&new FileInfo(path).Length is >0 and <=65536,"Bounded actual creator receipt");var row=JsonSerializer.Deserialize(File.ReadAllBytes(path),ParentJson.Default.CreatorEvidence)??throw new IOException("Creator absent");Win32.Need(row.ProcessClosed is true&&row.ThreadClosed is true,"Creator originals closed without terminating child");return row;}
 private static void Negatives(ParentReport report,CancellationToken token){
  var root=new ProcessIdentity(1,100,99,"synthetic-root");var parent=new ProcessIdentity(2,200,1,"synthetic-parent");var child=new ProcessIdentity(3,300,2,"synthetic-child");
  object syntheticJob=new();LifetimeRow R(ProcessIdentity id,long exit=0)=>new(id,id.CreationFileTime,exit,1000,1001,exit!=0,true,"synthetic-structural-only",0,syntheticJob);
  var clock=Stopwatch.StartNew();for(int item=0;item<8&&clock.ElapsedMilliseconds<5000;item++) {
   token.ThrowIfCancellationRequested();var rows=new List<LifetimeRow>{R(root),R(parent),R(child)};string name;
   switch(item){case 0:name="missing-parent";rows.RemoveAt(1);break;case 1:name="child-before-parent-birth";rows[1]=R(parent with{CreationFileTime=350});break;case 2:name="child-after-parent-exit";rows[1]=R(parent,250);break;case 3:name="equal-parent-child-birth";rows[1]=R(parent with{CreationFileTime=300});break;case 4:name="equal-child-birth-parent-exit";rows[1]=R(parent,300);break;case 5:name="duplicate-pid-lifetimes";rows.Add(R(parent with{CreationFileTime=150}));break;case 6:name="cycle";rows[1]=R(parent with{ParentPid=3});break;default:name="cross-job-token";rows[2]=rows[2] with{JobToken=new object()};break;}
   var result=ParentLifetime.Validate(root,child,rows,token);report.SyntheticNegatives.Add(new(name,"synthetic-structural-only",!result.Accepted,result.Reason));Win32.Need(!result.Accepted,"Synthetic negative accepted: "+name);
  }
  Win32.Need(report.SyntheticNegatives.Count==8,"Eight-negative/5second bound");
 }
 private static int Control(string scenario,string root){
  Win32.Need(OperatingSystem.IsWindows()&&Path.IsPathFullyQualified(root)&&Path.GetFullPath(root)==root&&!Directory.Exists(root)&&!File.Exists(root),"Windows fresh exact absolute root");Directory.CreateDirectory(root);string lease=Guid.NewGuid().ToString("N"),prefix="Local\\RustSharp.Parent."+lease;
  using(var file=new FileStream(Path.Combine(root,"OWNER.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None)){using var json=new Utf8JsonWriter(file);json.WriteStartObject();json.WriteString("root",root);json.WriteString("lease",lease);json.WriteEndObject();}
  var report=new ParentReport{Scenario=scenario,Lease=lease,ObserverPid=checked((uint)Environment.ProcessId)};var events=new Dictionary<string,KernelHandle>();var owned=new List<KernelHandle>();var identities=new List<ProcessIdentity>();var pins=new List<RetainedLifetime>();JobLease? job=null;OwnedSuspendedProcess? target=null;
  using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));bool proved=false;
  try{
   var setup=Stopwatch.StartNew();for(int index=0;index<5&&setup.ElapsedMilliseconds<5000;index++){deadline.Token.ThrowIfCancellationRequested();events.Add(Names[index],Events.Get(prefix+"."+Names[index],true));}Win32.Need(events.Count==5,"Five-event/5second bound");
   job=new JobLease();var limit=new Win32.JobLimit{Basic=new Win32.BasicLimit{Flags=0x2008,Active=20}};Win32.Check(Win32.SetInformationJobObject(job.Handle,9,ref limit,144),"Unchanged active20 no breakaway");
   target=OwnedSuspendedProcess.Start(Environment.ProcessPath!,Invocation("root",scenario,root,prefix,lease),root,true,job);report.Root=target.Receipt;target.Resume();Events.Wait(events["tree-ready"],10000,deadline.Token);
   var parent=ReadCreator(root,"root").Identity;var child=ReadCreator(root,"parent").Identity;Win32.Need(parent.ParentPid==target.Identity.Pid&&child.ParentPid==parent.Pid,"Real direct ancestry receipts");
   identities.Add(target.Identity);identities.Add(parent);identities.Add(child);pins.Add(new RetainedLifetime(target.RootOriginal(),target.Identity,job));
   var capture=Stopwatch.StartNew();for(int index=1;index<3&&capture.ElapsedMilliseconds<5000;index++){deadline.Token.ThrowIfCancellationRequested();var handle=Win32.OpenProcess(0x101400,0,identities[index].Pid);owned.Add(handle);Win32.CheckHandle(handle,"Observer acquire once");pins.Add(new RetainedLifetime(handle,identities[index],job));}Win32.Need(pins.Count==3,"Three retained identities/5seconds");
   for(int index=0;index<3&&capture.ElapsedMilliseconds<5000;index++)report.Before.Add(pins[index].Read(deadline.Token));Win32.Need(report.Before.Count==3,"Before3/5seconds");
   if(scenario=="exited"){Win32.Check(Events.Signal(events["exit-parent"]),"Release natural ancestor exit");var terminal=Win32.WaitOwnedTerminal(owned[0],parent,3000,deadline.Token);Win32.Need(terminal.Terminal&&terminal.ExitCode==0,"Actual parent natural0 with original retained handle");}
   var sample=Stopwatch.StartNew();for(int index=0;index<3&&sample.ElapsedMilliseconds<5000;index++)report.After.Add(pins[index].Read(deadline.Token));Win32.Need(report.After.Count==3,"After3/5seconds");
   report.Proof=ParentLifetime.Validate(target.Identity,child,report.After,deadline.Token);report.ExitedAncestorOriginalStillUsable=scenario=="exited"&&report.After[1].Terminal&&report.After[1].OriginalHandle==report.Before[1].OriginalHandle&&report.After[1].Birth==report.Before[1].Birth;
   Win32.Need(report.Proof.Accepted&&(scenario=="held"||report.ExitedAncestorOriginalStillUsable),"Complete actual retained-original lifetime proof");Negatives(report,deadline.Token);proved=true;
   Win32.Check(Events.Signal(events["stop"]),"Release actual tree naturally");var natural=Stopwatch.StartNew();
   for(int index=0;index<3&&natural.ElapsedMilliseconds<9000;index++){KernelHandle handle=index==0?target.RootOriginal():owned[index-1];var terminal=Win32.WaitOwnedTerminal(handle,identities[index],3000,deadline.Token);report.Terminal.Add(terminal);Win32.Need(terminal.Terminal&&terminal.ExitCode==0,"Actual natural0 tree terminal");}Win32.Need(report.Terminal.Count==3,"Natural3/9seconds");
  }catch(Exception error){report.Errors.Add(error.ToString());}
  finally{
   if(job!=null){try{report.Cleanup=job.Reap(CancellationToken.None);}catch(Exception error){report.Errors.Add("Job reap: "+error);}}
   if(target!=null){try{if(report.Cleanup is {AccountingZero:true}&&report.Cleanup.Errors.Count==0)target.DisposeAfterJobReap(report.Cleanup);else target.Dispose();report.Root=target.Receipt;}catch(Exception error){report.Errors.Add("Root close: "+error);}}
   bool closed=owned.Count==2;foreach(var handle in owned){try{handle.Dispose();closed&=handle.CloseSucceeded is true;}catch(Exception error){closed=false;report.Errors.Add("Observer close: "+error);}}report.ObserverHandlesClosed=closed;
   bool eventClosed=events.Count==5;foreach(var handle in events.Values){try{handle.Dispose();eventClosed&=handle.CloseSucceeded is true;}catch(Exception error){eventClosed=false;report.Errors.Add("Event close: "+error);}}report.EventHandlesClosed=eventClosed;
   if(job!=null){try{job.Dispose();}catch(Exception error){report.Errors.Add("Job close: "+error);}}
  }
  report.Passed=proved&&report.Errors.Count==0&&report.Terminal.Count==3&&report.ObserverHandlesClosed&&report.EventHandlesClosed&&report.Root is {OwnProcessReclaimed:true,TerminationReturn:null}&&report.Root.CleanupErrors.Count==0&&report.Cleanup is {AccountingZero:true,HandleClosed:true,TerminationReturn:null}&&report.Cleanup.Errors.Count==0;
  File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(report,ParentJson.Default.ParentReport));return report.Passed?0:1;
 }
}
namespace RustSharp.ProcessContainment {
 public sealed partial class OwnedSuspendedProcess {
  internal KernelHandle RootOriginal()=>process;
  internal void ReleaseCreatorOriginals(){Win32.Need(!closed&&Receipt.Resumed&&Win32.Identity(process,Identity.Pid)==Identity,"Actual creator original identity before close");thread.Dispose();process.Dispose();Receipt.ThreadHandleClosed=thread.CloseSucceeded;Receipt.ProcessHandleClosed=process.CloseSucceeded;Win32.Need(Receipt.ThreadHandleClosed is true&&Receipt.ProcessHandleClosed is true,"Creator original close");closed=true;}
 }
}
