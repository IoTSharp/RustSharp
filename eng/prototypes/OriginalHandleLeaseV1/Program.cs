#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using RustSharp.ProcessContainment;
using RustSharp.AcquisitionPrototype;

internal sealed class HandleEvidence {
 public required ProcessIdentity Identity {get;init;}public bool Borrowed {get;init;}public long OriginalHandle {get;init;}
 public long? ReceiverHandle {get;set;}public bool SameManagedObject {get;set;}public bool NativeUsableAfterLeaseDispose {get;set;}
 public bool? CloseSucceeded {get;set;}public int? CloseError {get;set;}public int? ClosedProbeReturn {get;set;}public int? ClosedProbeError {get;set;}
}
internal sealed class LeaseReport {
 public string Scenario {get;set;}="";public string Lease {get;set;}="";public uint ObserverPid {get;set;}public string? ObserverCommand {get;set;}
 public long Started {get;set;}public long Finished {get;set;}public bool Passed {get;set;}public bool ProductionGateIntegrated {get;}
 public int ActiveCap {get;}=20;public int CumulativeCap {get;}=1024;
 public List<CaptureAttempt> Attempts {get;}=new();public List<HandleEvidence> Handles {get;}=new();
 public bool ExceptionRejected {get;set;}public bool CancellationRejected {get;set;}public bool SecondTransferRejected {get;set;}
 public bool BorrowedUsableAfterLeaseDispose {get;set;}public bool AllAcquisitionOriginalsClosed {get;set;}
 public CleanupRecord? Cleanup {get;set;}public List<LaunchReceipt> Targets {get;}=new();public List<string> Errors {get;}=new();
}
[JsonSourceGenerationOptions(WriteIndented=true)]
[JsonSerializable(typeof(LeaseReport))]
internal sealed partial class LeaseJson:JsonSerializerContext{}
internal static class HandleProbe {
 [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]internal static extern int GetHandleInformation(IntPtr handle,out uint flags);
}
internal static class Program {
 private static int Main(string[] args) {
  try {
   if(args.Length==4&&args[0]=="target")return 0; // Created suspended; never resumed by controls.
   if(args.Length!=3||args[0]!="control"||args[1] is not ("A1" or "A2" or "A3"))throw new ArgumentException("control A1|A2|A3 fresh-absolute-root");
   return Control(args[1],args[2]);
  }catch(Exception error){Console.Error.WriteLine(error);return 1;}
 }
 private static void Usable(KernelHandle handle,ProcessIdentity identity,JobLease job) {
  Win32.Need(!handle.IsClosed&&Win32.Identity(handle,identity.Pid)==identity,"Same original native identity usable");
  Win32.Check(Win32.IsProcessInJob(handle,job.Handle,out int member),"Same original private Job");Win32.Need(member!=0,"Member left exact Job");
 }
 private static HandleEvidence Evidence(CapturedHandle row)=>new(){Identity=row.Identity,Borrowed=row.Borrowed,OriginalHandle=row.OriginalValue};
 private static void Closed(CapturedHandle row,HandleEvidence proof) {
  proof.CloseSucceeded=row.Handle.CloseSucceeded;proof.CloseError=row.Handle.CloseLastError;
  // No new handle allocation between close and this raw native probe.
  int result=HandleProbe.GetHandleInformation(new IntPtr(row.OriginalValue),out _);int error=Marshal.GetLastPInvokeError();proof.ClosedProbeReturn=result;proof.ClosedProbeError=error;
  Win32.Need(row.Handle.IsClosed&&row.Handle.CloseSucceeded is true&&result==0&&error==6,"Exact original native handle closure readback");
 }
 private static void CheckCaptured(IReadOnlyList<CapturedHandle> rows) {
  Win32.Need(rows.Count is >=2 and <=3,"Real borrowed+owned capture batch required");
 }
 private static int Control(string scenario,string root) {
  Win32.Need(OperatingSystem.IsWindows()&&Path.IsPathFullyQualified(root)&&Path.GetFullPath(root)==root&&!Directory.Exists(root)&&!File.Exists(root),"Windows fresh exact absolute root");
  Directory.CreateDirectory(root);string leaseId=Guid.NewGuid().ToString("N");
  using(var ownerFile=new FileStream(Path.Combine(root,"OWNER.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
   using var ownerJson=new Utf8JsonWriter(ownerFile);ownerJson.WriteStartObject();ownerJson.WriteString("lease",leaseId);ownerJson.WriteString("root",root);ownerJson.WriteEndObject();
  }
  var report=new LeaseReport{Scenario=scenario,Lease=leaseId,ObserverPid=checked((uint)Environment.ProcessId),Started=DateTime.UtcNow.ToFileTimeUtc()};
  var targets=new List<OwnedSuspendedProcess>();var borrowed=new Dictionary<ProcessIdentity,KernelHandle>();JobLease? job=null;KernelHandle? borrowedHandle=null;
  OriginalHandleLease? acquisition=null;OwnedHandleReceiver? receiver=null;bool controlProved=false;
  using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
  try {
   report.ObserverCommand=ProcessAudit.Read(report.ObserverPid).CommandLine;job=new JobLease();
   var limit=new Win32.JobLimit{Basic=new Win32.BasicLimit{Flags=0x2008,Active=20}};Win32.Check(Win32.SetInformationJobObject(job.Handle,9,ref limit,144),"Control unchanged active cap20/no breakaway");
   string host=Environment.ProcessPath??throw new IOException("Host absent");string assembly=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"LeaseFixture.dll"));
   Win32.Need(File.Exists(assembly)&&(File.GetAttributes(assembly)&FileAttributes.ReparsePoint)==0,"Existing non-reparse exact LeaseFixture.dll required");
   bool dotnet=string.Equals(Path.GetFileNameWithoutExtension(host),"dotnet",StringComparison.OrdinalIgnoreCase);int targetCount=scenario=="A2"?3:2;
   var setup=System.Diagnostics.Stopwatch.StartNew();
   for(int index=0;index<targetCount&&index<3&&setup.ElapsedMilliseconds<5000;index++) {
    deadline.Token.ThrowIfCancellationRequested();string[] invocation=dotnet?new[]{assembly,"target",scenario,root,leaseId}:new[]{"target",scenario,root,leaseId};
    var target=OwnedSuspendedProcess.Start(host,invocation,root,true,job);targets.Add(target);report.Targets.Add(target.Receipt);
   }
   Win32.Need(targets.Count==targetCount,"Three target/5second setup bound");
   borrowedHandle=Win32.OpenProcess(0x101400,0,targets[0].Identity.Pid);Win32.CheckHandle(borrowedHandle,"Control caller-owned borrowed original");Usable(borrowedHandle,targets[0].Identity,job);borrowed.Add(targets[0].Identity,borrowedHandle);
   if(scenario=="A2") {
    // Both exception and cancellation subcases use real native acquisitions.
    for(int subcase=0;subcase<2;subcase++) {
     deadline.Token.ThrowIfCancellationRequested();Win32.Need(setup.ElapsedMilliseconds<10000,"Two A2 subcases/10second bound");
     var rows=new List<CapturedHandle>();var proofs=new List<HandleEvidence>();acquisition=new OriginalHandleLease(job,borrowed);
     try {
      for(int index=0;index<3&&setup.ElapsedMilliseconds<10000;index++){var row=acquisition.Capture(targets[index].Identity.Pid,deadline.Token);rows.Add(row);var proof=Evidence(row);proofs.Add(proof);report.Handles.Add(proof);}
      Win32.Need(rows.Count==3,"A2 acquired all originals before rejection");
      if(subcase==0){try{receiver=acquisition.Transfer(_=>throw new IOException("CONTROL_FORCED_VALIDATION_EXCEPTION"),deadline.Token);}catch(IOException error) when(error.Message=="CONTROL_FORCED_VALIDATION_EXCEPTION"){report.ExceptionRejected=true;}}
      else{using var cancelled=new CancellationTokenSource();try{receiver=acquisition.Transfer(_=>cancelled.Cancel(),cancelled.Token);}catch(OperationCanceledException){report.CancellationRejected=true;}}
      Win32.Need(receiver==null,"Rejected transfer returned no receiver");
     }finally{acquisition.Dispose();report.Attempts.AddRange(acquisition.Attempts);acquisition=null;}
     for(int index=0;index<rows.Count&&index<3;index++){if(rows[index].Borrowed){Usable(rows[index].Handle,rows[index].Identity,job);proofs[index].NativeUsableAfterLeaseDispose=true;}else Closed(rows[index],proofs[index]);}
     Usable(borrowedHandle,targets[0].Identity,job);
    }
    report.BorrowedUsableAfterLeaseDispose=true;controlProved=report.ExceptionRejected&&report.CancellationRejected;
   } else {
    acquisition=new OriginalHandleLease(job,borrowed);var borrowedRow=acquisition.Capture(targets[0].Identity.Pid,deadline.Token);var ownedRow=acquisition.Capture(targets[1].Identity.Pid,deadline.Token);
    var borrowedProof=Evidence(borrowedRow);var ownedProof=Evidence(ownedRow);report.Handles.Add(borrowedProof);report.Handles.Add(ownedProof);
    receiver=acquisition.Transfer(CheckCaptured,deadline.Token);KernelHandle received=receiver.Get(ownedRow.Identity);ownedProof.ReceiverHandle=received.DangerousGetHandle().ToInt64();ownedProof.SameManagedObject=ReferenceEquals(received,ownedRow.Handle);
    Win32.Need(receiver.Count==1&&ownedProof.SameManagedObject&&ownedProof.ReceiverHandle==ownedProof.OriginalHandle,"Same observer original handle transferred once; borrowed not transferred");
    if(scenario=="A3") {
     try{using var duplicate=acquisition.Transfer(CheckCaptured,deadline.Token);}catch(InvalidDataException){report.SecondTransferRejected=true;}
     Win32.Need(report.SecondTransferRejected&&ReferenceEquals(receiver.Get(ownedRow.Identity),received),"Second transfer rejected without receiver replacement");
     Usable(received,ownedRow.Identity,job);
    }
    acquisition.Dispose();report.Attempts.AddRange(acquisition.Attempts);acquisition=null;
    Usable(received,ownedRow.Identity,job);ownedProof.NativeUsableAfterLeaseDispose=true;Usable(borrowedHandle,targets[0].Identity,job);borrowedProof.NativeUsableAfterLeaseDispose=true;report.BorrowedUsableAfterLeaseDispose=true;
    receiver.Dispose();receiver=null;Closed(ownedRow,ownedProof);Usable(borrowedHandle,targets[0].Identity,job);
    controlProved=scenario=="A1"||report.SecondTransferRejected;
   }
   report.AllAcquisitionOriginalsClosed=true;
  }catch(Exception error){report.Errors.Add(error.ToString());}
  finally {
   // Component ownership cleanup precedes Job cleanup and is not backfilled from it.
   try{acquisition?.Dispose();if(acquisition!=null)report.Attempts.AddRange(acquisition.Attempts);}catch(Exception error){report.Errors.Add("lease dispose: "+error);}
   try{receiver?.Dispose();}catch(Exception error){report.Errors.Add("receiver dispose: "+error);}
   try{borrowedHandle?.Dispose();if(borrowedHandle!=null&&borrowedHandle.CloseSucceeded is not true)report.Errors.Add("caller borrowed CloseHandle failed");}catch(Exception error){report.Errors.Add("borrowed owner close: "+error);}
   if(job!=null){try{report.Cleanup=job.Reap(CancellationToken.None);}catch(Exception error){report.Errors.Add("Job reap: "+error);}}
   // Fixed <=3 owned targets; each original root close uses the existing bounded
   // same-Job cleanup. Do not terminate any process by name or external PID.
   foreach(var target in targets){try{if(report.Cleanup is {AccountingZero:true}&&report.Cleanup.Errors.Count==0)target.DisposeAfterJobReap(report.Cleanup);else target.Dispose();}catch(Exception error){report.Errors.Add("target dispose: "+error);}}
   if(job!=null){try{job.Dispose();}catch(Exception error){report.Errors.Add("Job close: "+error);}}
  }
  bool targetsClosed=targets.Count is >=2 and <=3;foreach(var target in targets)targetsClosed&=target.Receipt.OwnProcessReclaimed&&target.Receipt.CleanupErrors.Count==0;
  report.Finished=DateTime.UtcNow.ToFileTimeUtc();report.Passed=controlProved&&report.AllAcquisitionOriginalsClosed&&report.BorrowedUsableAfterLeaseDispose&&report.Errors.Count==0&&targetsClosed&&report.Cleanup is {AccountingZero:true,HandleClosed:true}&&report.Cleanup.Errors.Count==0;
  File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(report,LeaseJson.Default.LeaseReport));return report.Passed?0:1;
 }
}
