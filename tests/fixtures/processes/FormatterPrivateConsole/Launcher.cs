#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace FormatterPrivateConsoleFixture;
internal static partial class Win32 {
 [StructLayout(LayoutKind.Sequential)] internal struct Accounting {public long User,Kernel,PeriodUser,PeriodKernel;public uint Faults,Total,Active,Terminated;}
 [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] internal static extern int QueryInformationJobObject(KernelHandle job,int kind,IntPtr buffer,uint length,out uint returned);
}
public sealed record MemberFailure(uint Pid,string Error);
public sealed record JobSnapshot(uint Active,uint Total,uint Assigned,uint Listed,uint ReturnedBytes,IReadOnlyList<ProcessIdentity> Members,IReadOnlyList<MemberFailure> Failures,bool Overflow,bool Coherent);
public sealed class CleanupRecord {
 public int? TerminationReturn {get;set;}public int? TerminationLastError {get;set;}public JobSnapshot? Before {get;set;}public JobSnapshot? Final {get;set;}public bool AccountingZero {get;set;}public bool? HandleClosed {get;set;}public int? HandleCloseLastError {get;set;}public List<string> Errors {get;}=new();public string? Error {get;set;}
}
public sealed class JobLease:IDisposable {
 internal KernelHandle Handle {get;}public CleanupRecord Cleanup {get;}=new();bool closed;
 public JobLease(){ProcessAudit.ValidateLayouts();Win32.Layout<Win32.Accounting>(48,("Active",40));Handle=Win32.CreateJobObjectW(IntPtr.Zero,IntPtr.Zero);Win32.CheckHandle(Handle,"new unnamed private job");try{var limit=new Win32.JobLimit{Basic=new Win32.BasicLimit{Flags=0x2000}};Win32.Check(Win32.SetInformationJobObject(Handle,9,ref limit,144),"kill-on-close; no breakaway flags");RequireExact(Inspect(),Array.Empty<ProcessIdentity>(),0);}catch{Handle.Dispose();throw;}}
 internal void Assign(KernelHandle process,LaunchReceipt receipt){int assigned=Win32.AssignProcessToJobObject(Handle,process);receipt.AssignReturn=assigned;receipt.AssignLastError=Marshal.GetLastPInvokeError();receipt.Assigned=assigned!=0;Win32.Check(assigned,"assign suspended process to owned job");int ok=Win32.IsProcessInJob(process,Handle,out int member);receipt.MembershipReturn=ok;receipt.MembershipLastError=Marshal.GetLastPInvokeError();receipt.MembershipVerified=ok!=0&&member!=0;Win32.Check(ok,"specific owned job membership");Win32.Need(member!=0,"specific job assignment missing");}
 public static void RequireExact(JobSnapshot snapshot,IReadOnlyList<ProcessIdentity> expected,uint total){Win32.Need(snapshot.Coherent&&!snapshot.Overflow&&snapshot.Failures.Count==0&&snapshot.Total==total&&snapshot.Active==expected.Count&&snapshot.Assigned==expected.Count&&snapshot.Listed==expected.Count&&snapshot.Members.OrderBy(x=>x.Pid).SequenceEqual(expected.OrderBy(x=>x.Pid)),"Exact current/cumulative job accounting and full member identities");}
 public JobSnapshot Inspect(){
  IntPtr accounting=Marshal.AllocHGlobal(48),ids=Marshal.AllocHGlobal(168);var rows=new List<ProcessIdentity>();var failures=new List<MemberFailure>();var watch=Stopwatch.StartNew();
  try{Win32.Check(Win32.QueryInformationJobObject(Handle,1,accounting,48,out uint size),"job accounting query");Win32.Need(size==48,"job accounting size");var values=Marshal.PtrToStructure<Win32.Accounting>(accounting);
   int ok=Win32.QueryInformationJobObject(Handle,3,ids,168,out uint returned);int error=Marshal.GetLastPInvokeError();if(ok==0&&error!=234)throw new System.ComponentModel.Win32Exception(error,"job process list");uint assigned=unchecked((uint)Marshal.ReadInt32(ids,0)),count=unchecked((uint)Marshal.ReadInt32(ids,4));if(ok==0||count>20||assigned>20)return new(values.Active,values.Total,assigned,count,returned,rows,failures,true,false);
   bool coherent=returned>=8+count*8&&returned<=168&&assigned==count&&values.Active==count;if(!coherent)failures.Add(new(0,"Assigned/list/active/returned-byte mismatch"));
   for(int index=0;index<count&&index<20&&watch.Elapsed.TotalSeconds<5;index++){uint pid=checked((uint)Marshal.ReadIntPtr(ids,8+index*8).ToInt64());try{using var process=Win32.OpenProcess(0x101400,0,pid);Win32.CheckHandle(process,"job member read");var row=Win32.Identity(process,pid);Win32.Check(Win32.IsProcessInJob(process,Handle,out int member),"job member exact object recheck");Win32.Need(member!=0,"PID no longer in this private job");rows.Add(row);}catch(Exception e){failures.Add(new(pid,e.Message));}}
   if(rows.Count+failures.Count(f=>f.Pid!=0)!=count)failures.Add(new(0,"job snapshot five-second wall bound"));Win32.Check(Win32.QueryInformationJobObject(Handle,1,accounting,48,out uint afterSize),"job accounting final coherence query");var after=Marshal.PtrToStructure<Win32.Accounting>(accounting);if(afterSize!=48||after.Active!=values.Active||after.Total!=values.Total){coherent=false;failures.Add(new(0,"Accounting changed during identity/list snapshot"));}return new(values.Active,values.Total,assigned,count,returned,rows,failures,false,coherent);
  }finally{Marshal.FreeHGlobal(ids);Marshal.FreeHGlobal(accounting);}
 }
 public CleanupRecord Reap(CancellationToken cancellation){
  try{Cleanup.Before=Inspect();if(Cleanup.Before.Active>0||Cleanup.Before.Assigned>0||Cleanup.Before.Listed>0){int ok=Win32.TerminateJobObject(Handle,137);int error=Marshal.GetLastPInvokeError();Cleanup.TerminationReturn=ok;Cleanup.TerminationLastError=error;if(ok==0)Cleanup.Errors.Add("TerminateJobObject Win32="+error);}}
  catch(Exception e){Cleanup.Errors.Add("before/terminate: "+e);}
  var watch=Stopwatch.StartNew();for(int check=0;check<20&&watch.Elapsed.TotalSeconds<10;check++){try{cancellation.ThrowIfCancellationRequested();var snapshot=Inspect();Cleanup.Final=snapshot;if(snapshot.Coherent&&snapshot.Active==0&&snapshot.Assigned==0&&snapshot.Listed==0&&snapshot.Members.Count==0&&snapshot.Failures.Count==0&&!snapshot.Overflow){Cleanup.AccountingZero=true;break;}}catch(Exception e){Cleanup.Errors.Add("reap check"+check+": "+e);if(e is OperationCanceledException)break;}Thread.Sleep(500);}if(!Cleanup.AccountingZero)Cleanup.Errors.Add("Complete accounting/assigned/list zero not proved");Cleanup.Error=Cleanup.Errors.Count==0?null:string.Join("\n",Cleanup.Errors);return Cleanup;
 }
 public void Dispose(){if(closed)return;closed=true;try{Handle.Dispose();}catch(Exception e){Cleanup.Errors.Add("close job: "+e);}finally{Cleanup.HandleClosed=Handle.CloseSucceeded;Cleanup.HandleCloseLastError=Handle.CloseLastError;if(Cleanup.HandleClosed is not true)Cleanup.Errors.Add("CloseHandle(job) Win32="+Cleanup.HandleCloseLastError);}}
}
public sealed class LaunchReceipt {
 public uint Pid {get;set;}public string IntendedCommand {get;set;}="";public ProcessIdentity? Identity {get;set;}public bool IdentityVerified {get;set;}public bool Assigned {get;set;}public bool Resumed {get;set;}
 public int? TerminationReturn {get;set;}public int? TerminationLastError {get;set;}public uint? WaitReturn {get;set;}public bool? ProcessHandleClosed {get;set;}public bool? ThreadHandleClosed {get;set;}public bool OwnProcessReclaimed {get;set;}public string? Error {get;set;}
 public int? AssignReturn {get;set;}public int? AssignLastError {get;set;}public int? MembershipReturn {get;set;}public int? MembershipLastError {get;set;}public bool MembershipVerified {get;set;}public int? ProcessCloseLastError {get;set;}public int? ThreadCloseLastError {get;set;}public List<string> CleanupErrors {get;}=new();public List<string> PublicationErrors {get;}=new();
 public uint? ActualExitCode {get;set;}public int? ExitCodeReadReturn {get;set;}public int? ExitCodeReadLastError {get;set;}
}
public sealed class LaunchFailure:Exception {public LaunchReceipt Receipt {get;}public LaunchFailure(LaunchReceipt receipt,Exception inner):base("Owned launch failed; structured cleanup retained",inner){Receipt=receipt;}}
public sealed class OwnedSuspendedProcess:IDisposable {
 readonly KernelHandle process,thread;readonly JobLease? job;bool closed;public LaunchReceipt Receipt {get;}public ProcessIdentity Identity=>Receipt.Identity??throw new InvalidDataException("Identity absent");public uint? ExitCode {get;private set;}
 OwnedSuspendedProcess(KernelHandle process,KernelHandle thread,JobLease? job,LaunchReceipt receipt){this.process=process;this.thread=thread;this.job=job;Receipt=receipt;}
 public static OwnedSuspendedProcess Start(string exe,IReadOnlyList<string> arguments,string cwd,bool detached,JobLease? job,Action<LaunchReceipt>? persist=null){
  ProcessAudit.ValidateLayouts();Win32.Need(arguments.Count<=8,"Eight bounded arguments");string command=string.Join(" ",new[]{exe}.Concat(arguments).Select(ProcessAudit.Quote));Win32.Need(command.Length<=32767,"Windows command size");var receipt=new LaunchReceipt{IntendedCommand=command};KernelHandle? process=null,thread=null;IntPtr memory=IntPtr.Zero;
  try{memory=Marshal.StringToHGlobalUni(command);var startup=new Win32.Startup{Size=104,Flags=1,Show=0};int ok=Win32.CreateProcessW(exe,memory,IntPtr.Zero,IntPtr.Zero,0,4u|(detached?8u:0u),IntPtr.Zero,cwd,ref startup,out var native);int error=Marshal.GetLastPInvokeError();receipt.Pid=native.Pid;if(native.Process!=IntPtr.Zero)process=new KernelHandle(native.Process);if(native.Thread!=IntPtr.Zero)thread=new KernelHandle(native.Thread);persist?.Invoke(receipt);
   if(ok==0)throw new System.ComponentModel.Win32Exception(error,"CreateProcessW");process=process??throw new InvalidDataException("Process handle absent");thread=thread??throw new InvalidDataException("Thread handle absent");receipt.Identity=Win32.Identity(process,native.Pid);Win32.Need(receipt.Identity.CommandLine==command&&receipt.Identity.ParentPid==(uint)Environment.ProcessId,"Exact created PID/FILETIME/OS command/direct parent");receipt.IdentityVerified=true;persist?.Invoke(receipt);
   if(job!=null){job.Assign(process,receipt);persist?.Invoke(receipt);}return new(process,thread,job,receipt);
  }catch(Exception e){receipt.Error=e.ToString();CleanupOwned(process,thread,receipt,false);try{persist?.Invoke(receipt);}catch(Exception secondary){receipt.PublicationErrors.Add(secondary.ToString());}throw new LaunchFailure(receipt,e);
  }finally{if(memory!=IntPtr.Zero)Marshal.FreeHGlobal(memory);}
 }
 public void Recheck(){Win32.Need(Win32.Identity(process,Identity.Pid)==Identity,"Retained exact identity recheck");if(job!=null){Win32.Check(Win32.IsProcessInJob(process,job.Handle,out int member),"retained specific job recheck");Win32.Need(member!=0,"Specific job changed");}}
 public void Resume(){Recheck();Win32.Need(!Receipt.Resumed,"Only one resume");uint value=Win32.ResumeThread(thread);int error=Marshal.GetLastPInvokeError();if(value==uint.MaxValue)throw new System.ComponentModel.Win32Exception(error,"ResumeThread");Win32.Need(value==1,"Unexpected suspend count");Receipt.Resumed=true;}
 public bool HasExited()=>Win32.WaitForSingleObject(process,0)==0;
 public bool Wait(int seconds,CancellationToken cancellation){Win32.Need(seconds>0&&seconds<=120,"Bounded wait");var watch=Stopwatch.StartNew();for(int check=0;check<20&&watch.Elapsed.TotalSeconds<seconds;check++){cancellation.ThrowIfCancellationRequested();uint value=Win32.WaitForSingleObject(process,(uint)Math.Min(1000,seconds*50));if(value==0){int ok=Win32.GetExitCodeProcess(process,out uint code);Receipt.ExitCodeReadReturn=ok;Receipt.ExitCodeReadLastError=Marshal.GetLastPInvokeError();Win32.Check(ok,"actual exit code");ExitCode=code;Receipt.ActualExitCode=code;return true;}Win32.Need(value==258,"Native wait failed");}return false;}
 static void CleanupOwned(KernelHandle? process,KernelHandle? thread,LaunchReceipt receipt,bool recheck){
  try{if(process!=null&&!process.IsInvalid){uint state=Win32.WaitForSingleObject(process,0);if(state==258){if(recheck&&receipt.Identity!=null)Win32.Need(Win32.Identity(process,receipt.Pid)==receipt.Identity,"Exact retained identity before termination");int ok=Win32.TerminateProcess(process,137);receipt.TerminationReturn=ok;receipt.TerminationLastError=Marshal.GetLastPInvokeError();if(ok==0)receipt.CleanupErrors.Add("TerminateProcess Win32="+receipt.TerminationLastError);}else if(state!=0)receipt.CleanupErrors.Add("Initial wait return="+state);}}
  catch(Exception e){receipt.CleanupErrors.Add("identity/terminate: "+e);}
  finally{try{if(process!=null&&!process.IsInvalid){receipt.WaitReturn=Win32.WaitForSingleObject(process,3000);if(receipt.WaitReturn!=0)receipt.CleanupErrors.Add("Final wait return="+receipt.WaitReturn);else{int ok=Win32.GetExitCodeProcess(process,out uint code);receipt.ExitCodeReadReturn=ok;receipt.ExitCodeReadLastError=Marshal.GetLastPInvokeError();if(ok!=0)receipt.ActualExitCode=code;else receipt.CleanupErrors.Add("Final actual exit read Win32="+receipt.ExitCodeReadLastError);}}}catch(Exception e){receipt.CleanupErrors.Add("wait/exit: "+e);}finally{try{thread?.Dispose();}catch(Exception e){receipt.CleanupErrors.Add("thread close: "+e);}finally{receipt.ThreadHandleClosed=thread?.CloseSucceeded;receipt.ThreadCloseLastError=thread?.CloseLastError;try{process?.Dispose();}catch(Exception e){receipt.CleanupErrors.Add("process close: "+e);}finally{receipt.ProcessHandleClosed=process?.CloseSucceeded;receipt.ProcessCloseLastError=process?.CloseLastError;receipt.OwnProcessReclaimed=receipt.WaitReturn==0&&receipt.ThreadHandleClosed is true&&receipt.ProcessHandleClosed is true;}}}}
 }
 public void Dispose(){if(closed)return;closed=true;CleanupOwned(process,thread,Receipt,true);ExitCode=Receipt.ActualExitCode;}
}
