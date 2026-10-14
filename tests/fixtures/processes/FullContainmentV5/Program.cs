#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace RustSharp.FullContainmentV5;

internal static class Program
{
 private static readonly string[] Cases={"held-three-natural","root-first-exit","timeout-three","cancel-three","transient-member","inventory-mutation"};
 private static void Save<T>(string path,T value,JsonTypeInfo<T> type)=>File.WriteAllText(path,JsonSerializer.Serialize(value,type));
 private static string AssemblyPath=>Path.Combine(AppContext.BaseDirectory,"FullContainmentV5.dll");
 private static int Main(string[] args)
 {
  try
  {
   if(args.Length==4&&args[0]=="fixture")return Fixture(args[1],int.Parse(args[2],System.Globalization.CultureInfo.InvariantCulture),args[3]);
   if(args.Length!=3||args[0]!="control"||!Cases.Contains(args[1],StringComparer.Ordinal))throw new ArgumentException("control <held-three-natural|root-first-exit|timeout-three|cancel-three|transient-member|inventory-mutation> <fresh-absolute-lease-root>");
   return Control(args[1],args[2]);
  }
  catch(Exception error){Console.Error.WriteLine(error);return 1;}
 }
 private static int Control(string name,string root)
 {
  if(!OperatingSystem.IsWindows()){Console.Error.WriteLine("UNKNOWN: Linux atomic cgroup/manager containment capability has not been established; no fixture launched.");return 3;}
  if(!Path.IsPathFullyQualified(root)||Path.GetFullPath(root)!=root||Directory.Exists(root)||File.Exists(root))throw new IOException("A fresh exact absolute root is required.");
  Directory.CreateDirectory(root);string lease=Guid.NewGuid().ToString("N");Save(Path.Combine(root,"OWNER.json"),new ControlOwner(lease,root,"FullContainmentV5.Control",name,DateTimeOffset.UtcNow),ControlJsonContext.Default.ControlOwner);
  if(name=="inventory-mutation")
  {
   bool rejected=false;
   try{JobLease.RequireExact(new JobSnapshot(0,2,0,0,8,Array.Empty<ProcessIdentity>(),Array.Empty<MemberFailure>(),false,true),Array.Empty<ProcessIdentity>(),1);}
   catch(InvalidDataException){rejected=true;}
   Save(Path.Combine(root,"control.json"),new MutationControl(name,rejected,"STRUCTURAL_NEGATIVE_ONLY",0,false),ControlJsonContext.Default.MutationControl);return rejected?0:1;
  }
  string exe=Environment.ProcessPath??throw new InvalidOperationException("Current executable absent");
  string assembly=AssemblyPath;
  using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(20));
  if(name=="cancel-three")cancellation.CancelAfter(TimeSpan.FromMilliseconds(1800));
  string[] command={assembly,"fixture",name,"0",root};
  FullTreeReport report=FullTreeRunner.Run(exe,command,root,root,lease,3,3,name=="timeout-three"?3:12,65536,cancellation.Token,name=="transient-member"?1000:10);
  bool three=report.Members.Count==3&&report.JobTotal==3&&report.IdentityInventoryComplete;
  bool passed=name switch
  {
   "held-three-natural"=>three&&report.ExecutionSucceeded&&!report.ForcedJobReap,
   "root-first-exit"=>three&&report.FullContainmentVerified&&report.NaturalRootExitObserved&&report.ForcedJobReap&&!report.ExecutionSucceeded,
   "timeout-three"=>three&&report.FullContainmentVerified&&report.Errors.Any(x=>x.Contains(nameof(TimeoutException),StringComparison.Ordinal))&&!report.ExecutionSucceeded,
   "cancel-three"=>three&&report.FullContainmentVerified&&report.Errors.Any(x=>x.Contains(nameof(OperationCanceledException),StringComparison.Ordinal))&&!report.ExecutionSucceeded,
   "transient-member"=>report.JobTotal==2&&report.WholeJobZero&&!report.IdentityInventoryComplete&&!report.FullContainmentVerified&&!report.ExecutionSucceeded,
   _=>false
  };
  Save(Path.Combine(root,"report.json"),report,ControlJsonContext.Default.FullTreeReport);
  var result=new NativeControl(name,passed,"CANDIDATE_NATIVE_CONTROL_ONLY",report.FullContainmentVerified,report.ExecutionSucceeded,report.JobTotal,report.Members.Count,false,"UNKNOWN");
  Save(Path.Combine(root,"control.json"),result,ControlJsonContext.Default.NativeControl);
  Console.WriteLine(JsonSerializer.Serialize(result,ControlJsonContext.Default.NativeControl));return passed?0:1;
 }
 private static int Fixture(string name,int depth,string root)
 {
  if(depth is <0 or >2||!Path.IsPathFullyQualified(root))throw new ArgumentException("Bounded fixture depth/path");
  using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(16));
  Save(Path.Combine(root,"ready-"+depth+".json"),ProcessAudit.Read(checked((uint)Environment.ProcessId)),ControlJsonContext.Default.ProcessIdentity);
  if(name=="transient-member")
  {
   if(depth==1)return 0;
   using var transient=Spawn(name,1,root);transient.Resume();if(!transient.Wait(5,deadline.Token))throw new TimeoutException("Transient child wait");
   File.WriteAllText(Path.Combine(root,"transient-finished"),"ready");deadline.Token.WaitHandle.WaitOne(2200);return 0;
  }
  if(depth==2)
  {
   if(name=="held-three-natural"){deadline.Token.WaitHandle.WaitOne(700);return 0;}
   deadline.Token.WaitHandle.WaitOne(15000);deadline.Token.ThrowIfCancellationRequested();return 0;
  }
  using var child=Spawn(name,depth+1,root);child.Resume();
  var watch=Stopwatch.StartNew();bool ready=false;
  // Comparisons reviewed: at most 300 probes, five seconds, cancellable 20ms backoff.
  for(int poll=0;poll<300&&watch.Elapsed.TotalSeconds<5;poll++)
  {
   deadline.Token.ThrowIfCancellationRequested();if(File.Exists(Path.Combine(root,"ready-2.json"))){ready=true;break;}
   deadline.Token.WaitHandle.WaitOne(20);
  }
  if(!ready)throw new TimeoutException("Grandchild ready handshake");
  if(name=="root-first-exit"&&depth==0){GC.KeepAlive(child);Environment.Exit(0);}
  if(!child.Wait(12,deadline.Token))throw new TimeoutException("Held descendant wait");return child.ExitCode==0?0:1;
 }
 private static OwnedSuspendedProcess Spawn(string name,int depth,string root)
 {
  string exe=Environment.ProcessPath??throw new InvalidOperationException("Fixture executable absent");
  string[] args={AssemblyPath,"fixture",name,depth.ToString(System.Globalization.CultureInfo.InvariantCulture),root};
  return OwnedSuspendedProcess.Start(exe,args,root,true,null,receipt=>Save(Path.Combine(root,"launch-"+depth+".json"),receipt,ControlJsonContext.Default.LaunchReceipt));
 }
}

internal sealed record ControlOwner(string lease,string root,string owner,string name,DateTimeOffset at);
internal sealed record MutationControl(string name,bool passed,string scope,int nativeProcessCount,bool fullP1Closure);
internal sealed record NativeControl(string name,bool passed,string scope,bool FullContainmentVerified,bool ExecutionSucceeded,uint? JobTotal,int observedMembers,bool fullP1Closure,string linux);

[JsonSourceGenerationOptions(WriteIndented=true)]
[JsonSerializable(typeof(ControlOwner))]
[JsonSerializable(typeof(MutationControl))]
[JsonSerializable(typeof(NativeControl))]
[JsonSerializable(typeof(FullTreeReport))]
[JsonSerializable(typeof(ProcessIdentity))]
[JsonSerializable(typeof(LaunchReceipt))]
internal sealed partial class ControlJsonContext:JsonSerializerContext { }
