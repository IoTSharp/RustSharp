#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RustSharp.ProcessContainment;
public sealed record ProcessObservation(long Sequence,long StartedUtcFileTime,long FinishedUtcFileTime,uint Total,uint Active,int RetainedIdentities);
public sealed record RetainedJobObservation(uint BeforeActive,uint Listed,uint AfterActive,uint Total,IReadOnlyList<ProcessIdentity> TerminalMembers,int LiveMembers,int TerminatingWaits);
public sealed class RetainedMemberProof {
 public required ProcessIdentity Identity {get;init;}
 public bool Terminal {get;set;} public uint? ExitCode {get;set;}
 public bool BirthRechecked {get;set;} public bool? HandleClosed {get;set;}
 public string? Error {get;set;}
 public TerminalWaitResult? TerminalWait {get;set;}
}
public sealed class FullTreeReport {
 public LaunchReceipt? Root {get;set;} public CleanupRecord? JobCleanup {get;set;}
 public List<RetainedMemberProof> Members {get;}=new();public List<string> Errors {get;}=new();
 public uint? JobTotal {get;set;} public uint? RootExitCode {get;set;}
 public bool IdentityInventoryComplete {get;set;} public bool WholeJobZero {get;set;}
 public bool AllRetainedHandlesTerminalAndClosed {get;set;} public bool FullContainmentVerified {get;set;}
 public bool ExecutionSucceeded {get;set;} public bool NaturalRootExitObserved {get;set;} public bool ForcedJobReap {get;set;} public string CallerLease {get;set;}=""; public string OwnerSha256 {get;set;}=""; public bool OriginalP1ScopePassed {get;}
 public bool LinuxContainmentSupported {get;}
 public RetainedJobObservation? LastRetainedObservation {get;set;}
 public int RetainedObservationCount {get;set;}
 public long MaximumOutputBytes {get;set;}public long AggregateOutputBytes {get;set;}public bool AggregateOutputBudgetExceeded {get;set;}public bool AggregateOutputBudgetObservedWhileRunning {get;set;}
 public long MaximumOutputBytesPerStream {get;set;}public long MaximumTotalOutputBytes {get;set;}public long StandardOutputBytes {get;set;}public long StandardErrorBytes {get;set;}
 public bool StandardOutputBudgetExceeded {get;set;}public bool StandardErrorBudgetExceeded {get;set;}public bool PerStreamOutputBudgetExceeded {get;set;}public bool PerStreamOutputBudgetObservedWhileRunning {get;set;}
 public long ObservationCount {get;set;}public List<ProcessObservation> Observations {get;}=new();
 public JobSnapshotAudit? LastNativeSnapshot {get;set;}public JobSnapshotAudit? FailedNativeSnapshot {get;set;}
}
public static class FullTreeRunner {
 public static FullTreeReport Run(string executable,IReadOnlyList<string> arguments,string workingDirectory,string callerLeaseRoot,string callerLease,
  int maximumActiveProcesses,int maximumTotalProcesses,int timeoutSeconds,long maximumOutputBytes,CancellationToken cancellationToken,int pollingMilliseconds=10)
  =>Run(executable,arguments,workingDirectory,callerLeaseRoot,callerLease,maximumActiveProcesses,maximumTotalProcesses,timeoutSeconds,maximumOutputBytes,maximumOutputBytes,cancellationToken,pollingMilliseconds);
 public static FullTreeReport Run(string executable,IReadOnlyList<string> arguments,string workingDirectory,string callerLeaseRoot,string callerLease,
  int maximumActiveProcesses,int maximumTotalProcesses,int timeoutSeconds,long maximumOutputBytesPerStream,long maximumTotalOutputBytes,CancellationToken cancellationToken,int pollingMilliseconds=10)
 {
  if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("FAIL_CLOSED: an owned escape-resistant cgroup/manager boundary has not been implemented. A process group is insufficient.");
  ArgumentNullException.ThrowIfNull(arguments);cancellationToken.ThrowIfCancellationRequested();
  if(maximumActiveProcesses is <1 or >20||maximumTotalProcesses<maximumActiveProcesses||maximumTotalProcesses>1024||timeoutSeconds is <1 or >3600||maximumOutputBytesPerStream is <1 or >1073741824||maximumTotalOutputBytes is <1 or >1073741824||pollingMilliseconds is <1 or >1000)
   throw new ArgumentOutOfRangeException(nameof(maximumActiveProcesses),"Explicit unchanged caller caps and finite timeout/bytes required.");
  if(Path.GetFullPath(workingDirectory)!=workingDirectory||!Directory.Exists(workingDirectory))throw new ArgumentException("Exact existing working directory required.",nameof(workingDirectory));
  string root=ProcessAudit.VerifyRoot(callerLeaseRoot,cancellationToken),stdout=Path.Combine(root,"stdout.bin"),stderr=Path.Combine(root,"stderr.bin");
  if(!Guid.TryParseExact(callerLease,"N",out _))throw new ArgumentException("Exact32hex caller lease required.",nameof(callerLease));
  string ownerPath=Path.Combine(root,"OWNER.json");
  if((File.GetAttributes(ownerPath)&FileAttributes.ReparsePoint)!=0)throw new IOException("OWNER reparse rejected.");
  byte[] ownerBytes;
  using(var leaseDeadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
  {
   leaseDeadline.CancelAfter(TimeSpan.FromSeconds(5));
   using var ownerFile=new FileStream(ownerPath,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.Asynchronous);
   if(ownerFile.Length is <1 or >65536)throw new IOException("OWNER byte quota65536.");
   ownerBytes=new byte[checked((int)ownerFile.Length)];ownerFile.ReadExactlyAsync(ownerBytes.AsMemory(),leaseDeadline.Token).AsTask().GetAwaiter().GetResult();
   leaseDeadline.Token.ThrowIfCancellationRequested();
  }
  using(var ownerJson=System.Text.Json.JsonDocument.Parse(ownerBytes))
  {
   if(ownerJson.RootElement.GetProperty("lease").GetString()!=callerLease||!string.Equals(ownerJson.RootElement.GetProperty("root").GetString(),root,StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("Caller OWNER lease/root mismatch; no target launched.");
  }
  var report=new FullTreeReport{CallerLease=callerLease,MaximumOutputBytes=maximumOutputBytesPerStream,MaximumOutputBytesPerStream=maximumOutputBytesPerStream,MaximumTotalOutputBytes=maximumTotalOutputBytes,OwnerSha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ownerBytes))};var retained=new Dictionary<ProcessIdentity,KernelHandle>();JobLease? job=null;OwnedSuspendedProcess? process=null;
  var clock=Stopwatch.StartNew();bool coherent=true;int maximumPolls=checked(timeoutSeconds*1000/pollingMilliseconds+1);
  void ObserveOutput()
  {
   bool running=process!=null&&process.Receipt.Resumed&&!process.HasExited();long a=new FileInfo(stdout).Length,b=new FileInfo(stderr).Length;
   report.StandardOutputBytes=a;report.StandardErrorBytes=b;report.AggregateOutputBytes=checked(a+b);
   report.StandardOutputBudgetExceeded|=a>maximumOutputBytesPerStream;report.StandardErrorBudgetExceeded|=b>maximumOutputBytesPerStream;
   report.PerStreamOutputBudgetExceeded|=a>maximumOutputBytesPerStream||b>maximumOutputBytesPerStream;
   report.PerStreamOutputBudgetObservedWhileRunning|=running&&(a>maximumOutputBytesPerStream||b>maximumOutputBytesPerStream);
   if(report.AggregateOutputBytes>maximumTotalOutputBytes){report.AggregateOutputBudgetExceeded=true;report.AggregateOutputBudgetObservedWhileRunning|=running;throw new IOException("Aggregate raw output byte quota exceeded whileRunning="+running);}
   if(a>maximumOutputBytesPerStream||b>maximumOutputBytesPerStream)throw new IOException("Per-stream raw output byte quota exceeded whileRunning="+running);
  }
  void Observe()
  {
   long observedAt=DateTime.UtcNow.ToFileTimeUtc();
   void RecordObservation(uint total,uint active){report.ObservationCount++;if(report.Observations.Count==16)report.Observations.RemoveAt(8);report.Observations.Add(new(report.ObservationCount,observedAt,DateTime.UtcNow.ToFileTimeUtc(),total,active,retained.Count));}
   // Once every permitted cumulative member has an owned handle, no new identity may
   // be admitted. Observe exits through those same handles, never by reopening a PID.
   if(retained.Count==maximumTotalProcesses){try{report.LastRetainedObservation=ObserveRetained(job!,retained,cancellationToken);report.RetainedObservationCount++;RecordObservation(report.LastRetainedObservation.Total,report.LastRetainedObservation.AfterActive);}catch(OperationCanceledException){throw;}catch(Exception){coherent=false;throw;}ObserveOutput();return;}
   JobSnapshot snapshot;try{snapshot=job!.Inspect(cancellationToken);report.LastNativeSnapshot=snapshot.Acquisition;}catch(Exception){report.FailedNativeSnapshot=job!.LastAcquisition;throw;}if(snapshot.Overflow||!snapshot.Coherent||snapshot.Failures.Count!=0){report.FailedNativeSnapshot=snapshot.Acquisition;coherent=false;throw new InvalidDataException("Incomplete native member snapshot; fail closed.");}
   if(snapshot.Total>maximumTotalProcesses||snapshot.Active>maximumActiveProcesses)throw new InvalidDataException("Original caller process cap exceeded.");
   var captureClock=Stopwatch.StartNew();int index=0;
   foreach(ProcessIdentity identity in snapshot.Members){if(index++>=20||captureClock.Elapsed.TotalSeconds>=5)throw new TimeoutException("Member20/5seconds bound.");cancellationToken.ThrowIfCancellationRequested();if(retained.ContainsKey(identity))continue;
    KernelHandle? handle=null;try{handle=Win32.OpenProcess(0x101400,0,identity.Pid);Win32.CheckHandle(handle,"retain specific job member");Win32.Need(Win32.Identity(handle,identity.Pid)==identity,"PID/birth/full command/parent recheck");Win32.Check(Win32.IsProcessInJob(handle,job.Handle,out int member),"retained member specific job");Win32.Need(member!=0,"Member left owned job");if(retained.Count>=maximumTotalProcesses)throw new InvalidDataException("Retained identity quota.");retained.Add(identity,handle);handle=null;}finally{handle?.Dispose();}
   }
   RecordObservation(snapshot.Total,snapshot.Active);ObserveOutput();
  }
  try
  {
   job=new JobLease();var limit=new Win32.JobLimit{Basic=new Win32.BasicLimit{Flags=0x2008,Active=checked((uint)maximumActiveProcesses)}};
   Win32.Check(Win32.SetInformationJobObject(job.Handle,9,ref limit,144),"Kill-on-close plus caller active cap; no breakaway");
   process=OwnedSuspendedProcess.StartCaptured(executable,arguments,workingDirectory,job,root,stdout,stderr,receipt=>report.Root=receipt);
   Observe();process.Resume();bool rootExited=false;
   for(int poll=0;poll<maximumPolls&&clock.Elapsed.TotalSeconds<timeoutSeconds;poll++)
   {
    cancellationToken.ThrowIfCancellationRequested();Observe();
    if(process.HasExited()){if(!process.Wait(1,cancellationToken))throw new TimeoutException("Root actual exit read.");report.RootExitCode=process.ExitCode;report.NaturalRootExitObserved=true;rootExited=true;break;}
    cancellationToken.WaitHandle.WaitOne(pollingMilliseconds);
   }
   if(!rootExited)throw new TimeoutException("Whole invocation finite poll/time deadline.");
  }
  catch(Exception failure){report.Errors.Add(failure.ToString());}
  finally
  {
   if(job!=null){try{report.JobCleanup=job.Reap(CancellationToken.None);report.JobTotal=report.JobCleanup.Final?.Total;report.WholeJobZero=report.JobCleanup.AccountingZero;report.ForcedJobReap=report.JobCleanup.TerminationReturn.HasValue;}catch(Exception error){report.Errors.Add("job-reap: "+error);}}
   if(process!=null){try{if(report.JobCleanup is {AccountingZero:true}&&report.JobCleanup.Errors.Count==0)process.DisposeAfterJobReap(report.JobCleanup);else process.Dispose();report.Root=process.Receipt;report.RootExitCode=process.ExitCode;report.ForcedJobReap|=process.Receipt.TerminationReturn.HasValue;}catch(Exception error){report.Errors.Add("root-close: "+error);try{process.Dispose();}catch(Exception secondary){report.Errors.Add("root-close-final: "+secondary);}}}
   var terminalClock=Stopwatch.StartNew();int checkedHandles=0;bool terminal=true;
   foreach(var pair in retained)
   {
    var proof=new RetainedMemberProof{Identity=pair.Key};report.Members.Add(proof);
     try{if(checkedHandles++>=maximumTotalProcesses||terminalClock.Elapsed.TotalSeconds>=10){proof.Error="Retained terminal identity count/time bound.";terminal=false;continue;}
     int remaining=checked((int)Math.Max(1,Math.Min(3000,10000-terminalClock.ElapsedMilliseconds)));proof.TerminalWait=Win32.WaitOwnedTerminal(pair.Value,pair.Key,remaining,CancellationToken.None);proof.Terminal=proof.TerminalWait.Terminal;proof.BirthRechecked=proof.TerminalWait.BirthVerified;proof.ExitCode=proof.TerminalWait.ExitCode;
     if(!proof.Terminal)proof.Error="Retained exact handle did not signal inside bounded terminal cleanup";
     if(!proof.Terminal||!proof.BirthRechecked)terminal=false;
    }catch(Exception error){proof.Error=error.ToString();terminal=false;}
    finally{try{pair.Value.Dispose();proof.HandleClosed=pair.Value.CloseSucceeded;if(proof.HandleClosed is not true)terminal=false;}catch(Exception error){proof.Error=error.ToString();terminal=false;}}
   }
   if(job!=null){try{job.Dispose();}catch(Exception error){report.Errors.Add("job-close: "+error);}}
   foreach(string path in new[]{stdout,stderr}){try{if(File.Exists(path)&&new FileInfo(path).Length>maximumOutputBytesPerStream)report.Errors.Add("Final raw byte quota: "+path);}catch(Exception error){report.Errors.Add("output-final: "+error);}}
   try{if(File.Exists(stdout)&&File.Exists(stderr)){report.StandardOutputBytes=new FileInfo(stdout).Length;report.StandardErrorBytes=new FileInfo(stderr).Length;report.AggregateOutputBytes=checked(report.StandardOutputBytes+report.StandardErrorBytes);report.StandardOutputBudgetExceeded|=report.StandardOutputBytes>maximumOutputBytesPerStream;report.StandardErrorBudgetExceeded|=report.StandardErrorBytes>maximumOutputBytesPerStream;report.PerStreamOutputBudgetExceeded|=report.StandardOutputBudgetExceeded||report.StandardErrorBudgetExceeded;if(report.AggregateOutputBytes>maximumTotalOutputBytes){report.AggregateOutputBudgetExceeded=true;report.Errors.Add("Final aggregate raw output byte quota exceeded");}}}catch(Exception error){report.Errors.Add("aggregate-output-final: "+error);}
   report.IdentityInventoryComplete=coherent&&report.JobTotal.HasValue&&report.JobTotal.Value==retained.Count;
   report.AllRetainedHandlesTerminalAndClosed=terminal&&report.IdentityInventoryComplete;
   report.FullContainmentVerified=report.WholeJobZero&&report.JobCleanup?.HandleClosed is true&&report.JobCleanup.Errors.Count==0&&report.AllRetainedHandlesTerminalAndClosed&&report.Root?.OwnProcessReclaimed is true&&report.Root.CleanupErrors.Count==0&&report.Root.PublicationErrors.Count==0;
   report.ExecutionSucceeded=report.Errors.Count==0&&report.RootExitCode==0&&report.FullContainmentVerified&&report.NaturalRootExitObserved&&!report.ForcedJobReap;
   if(report.ForcedJobReap&&report.NaturalRootExitObserved)report.Errors.Add("Root exited but descendants required forced Job reaping; natural execution cannot pass.");
   if(!report.IdentityInventoryComplete)report.Errors.Add("Cumulative Job process count differs from retained native identities; transient members are UNKNOWN, never full-tree proof.");
  }
  return report;
 }
 private static RetainedJobObservation ObserveRetained(JobLease job,IReadOnlyDictionary<ProcessIdentity,KernelHandle> retained,CancellationToken cancellation)
 {
  Win32.Need(retained.Count is >0 and <=20,"Complete bounded retained inventory required");
  IntPtr accounting=Marshal.AllocHGlobal(48),ids=Marshal.AllocHGlobal(168);var clock=Stopwatch.StartNew();
  try
  {
   Win32.Check(Win32.QueryInformationJobObject(job.Handle,1,accounting,48,out uint bytes),"retained before accounting");Win32.Need(bytes==48,"retained before accounting bytes");var before=Marshal.PtrToStructure<Win32.Accounting>(accounting);
   Win32.Need(before.Total==retained.Count&&before.Active<=before.Total,"No unknown cumulative member in retained mode");
   Win32.Check(Win32.QueryInformationJobObject(job.Handle,3,ids,168,out uint returned),"retained raw PID list");
   uint assigned=unchecked((uint)Marshal.ReadInt32(ids)),listed=unchecked((uint)Marshal.ReadInt32(ids,4));
   Win32.Need(assigned==listed&&listed<=retained.Count&&returned>=8+listed*8&&returned<=168,"Exact retained raw list bounds/counts");
   var pids=new HashSet<uint>();
   for(int index=0;index<listed&&index<20&&clock.Elapsed.TotalSeconds<5;index++)
   {
    cancellation.ThrowIfCancellationRequested();uint pid=checked((uint)Marshal.ReadIntPtr(ids,8+index*8).ToInt64());
    Win32.Need(pids.Add(pid)&&System.Linq.Enumerable.Any(retained.Keys,k=>k.Pid==pid),"Unknown/duplicate raw member PID rejected");
   }
   Win32.Need(pids.Count==listed,"Retained raw list five-second bound");
   var terminal=new List<ProcessIdentity>();int checkedMembers=0,live=0,terminatingWaits=0;
   foreach(var pair in retained)
   {
    cancellation.ThrowIfCancellationRequested();Win32.Need(checkedMembers++<20&&clock.Elapsed.TotalSeconds<5,"Retained identity20/5second bound");
    Win32.Check(Win32.GetProcessTimes(pair.Value,out long birth,out _,out _,out _),"retained same-handle creation");Win32.Need(birth==pair.Key.CreationFileTime,"Retained creation mismatch/PID reuse rejected");
    uint state=Win32.WaitForSingleObject(pair.Value,0);Win32.Need(state is 0 or 258,"Retained native wait state");
    if(state==258)
    {
     try{Win32.Need(Win32.Identity(pair.Value,pair.Key.Pid)==pair.Key,"Retained live complete identity");Win32.Check(Win32.IsProcessInJob(pair.Value,job.Handle,out int member),"retained live specific Job");Win32.Need(member!=0,"Retained live member escaped Job");}
     catch(NativeProcessTerminatingException){ConfirmRetainedTermination(pair.Value,pair.Key,cancellation);terminatingWaits++;}
    }
   }
   Win32.Check(Win32.QueryInformationJobObject(job.Handle,1,accounting,48,out bytes),"retained after accounting");Win32.Need(bytes==48,"retained after accounting bytes");var after=Marshal.PtrToStructure<Win32.Accounting>(accounting);
   // All count movement must be monotone exits of already owned identities. New
   // cumulative membership, unknown PIDs and malformed counters remain failures.
   Win32.Need(after.Total==before.Total&&before.Active>=listed&&listed>=after.Active,"Unexplained accounting/list transition rejected");
   checkedMembers=0;
   foreach(var pair in retained)
   {
    cancellation.ThrowIfCancellationRequested();Win32.Need(checkedMembers++<20&&clock.Elapsed.TotalSeconds<5,"Terminal reconcile20/5second bound");
    Win32.Check(Win32.GetProcessTimes(pair.Value,out long birth,out _,out _,out _),"terminal reconcile same-handle creation");Win32.Need(birth==pair.Key.CreationFileTime,"Terminal reconcile creation mismatch");
    uint state=Win32.WaitForSingleObject(pair.Value,0);Win32.Need(state is 0 or 258,"Terminal reconcile native wait");
    if(state==0){Win32.Check(Win32.GetExitCodeProcess(pair.Value,out _),"terminal reconcile actual exit");terminal.Add(pair.Key);}else{Win32.Need(pids.Contains(pair.Key.Pid),"Unlisted retained live process rejected");live++;}
   }
   Win32.Need(live<=after.Active&&retained.Count-after.Active<=terminal.Count,"Every count reduction requires same-handle terminal proof");
   return new(before.Active,listed,after.Active,after.Total,terminal,live,terminatingWaits);
  }
  finally{Marshal.FreeHGlobal(ids);Marshal.FreeHGlobal(accounting);}
 }
 private static void ConfirmRetainedTermination(KernelHandle handle,ProcessIdentity identity,CancellationToken cancellation)
 {
  var watch=Stopwatch.StartNew();
  // Exact comparison exits: 20 native waits, 500ms wall, cancellation every25ms.
  // Invoked only for STATUS_PROCESS_IS_TERMINATING on an already owned identity.
  for(int check=0;check<20&&watch.ElapsedMilliseconds<500;check++)
  {
   cancellation.ThrowIfCancellationRequested();Win32.Check(Win32.GetProcessTimes(handle,out long birth,out _,out _,out _),"terminating same-handle birth");Win32.Need(birth==identity.CreationFileTime,"Terminating retained birth mismatch");
   uint state=Win32.WaitForSingleObject(handle,25);Win32.Need(state is 0 or 258,"Terminating retained native wait failed");
   if(state==0){Win32.Check(Win32.GetProcessTimes(handle,out long terminalBirth,out _,out _,out _),"terminal same-handle birth");Win32.Need(terminalBirth==identity.CreationFileTime,"Terminal retained birth mismatch");Win32.Check(Win32.GetExitCodeProcess(handle,out _),"terminal retained actual exit");return;}
  }
  throw new TimeoutException("Known terminating handle did not signal within20checks/500ms; fail closed.");
 }
}
