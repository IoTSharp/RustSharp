#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using RustSharp.ProcessContainment;

namespace RustSharp.AcquisitionPrototype;

internal sealed record CapturedHandle(ProcessIdentity Identity,KernelHandle Handle,bool Borrowed,long OriginalValue);
internal sealed class CaptureAttempt {
 public uint Pid {get;init;}public long Started {get;init;}public long Finished {get;set;}
 public long? OriginalHandle {get;set;}public bool Borrowed {get;set;}public ProcessIdentity? Identity {get;set;}
 public bool Validated {get;set;}public string? Error {get;set;}public bool? ClosedOnFailure {get;set;}
}

// An isolated ownership component. It proves acquisition/transfer only, never
// cumulative accounting, ancestry, growth reconciliation or production acceptance.
internal sealed class OriginalHandleLease:IDisposable {
 private readonly JobLease job;
 private readonly IReadOnlyDictionary<ProcessIdentity,KernelHandle> borrowed;
 private Dictionary<ProcessIdentity,KernelHandle> owned=new();
 private readonly List<CapturedHandle> captures=new();
 private readonly Stopwatch lifetime=Stopwatch.StartNew();
 private bool transferred,disposed,transferring,faulted;
 internal List<CaptureAttempt> Attempts {get;}=new();
 internal IReadOnlyList<CapturedHandle> Captures=>captures.AsReadOnly();
 internal OriginalHandleLease(JobLease job,IReadOnlyDictionary<ProcessIdentity,KernelHandle> borrowed) {
  ArgumentNullException.ThrowIfNull(job);ArgumentNullException.ThrowIfNull(borrowed);
  Win32.Need(borrowed.Count<=1024,"Cumulative borrowed inventory cap1024");this.job=job;this.borrowed=borrowed;
 }
 private void Mutable(CancellationToken token) {
  ObjectDisposedException.ThrowIf(disposed,this);token.ThrowIfCancellationRequested();
  Win32.Need(!transferred&&!transferring&&!faulted,"Lease already transferred, faulted or transfer in progress");
  Win32.Need(lifetime.ElapsedMilliseconds<5000,"Acquisition lease5second lifetime bound");
 }
 internal CapturedHandle Capture(uint pid,CancellationToken token) {
  Mutable(token);Win32.Need(pid!=0&&captures.Count<20,"Raw active acquisition cap20");
  var attempt=new CaptureAttempt{Pid=pid,Started=DateTime.UtcNow.ToFileTimeUtc()};Attempts.Add(attempt);
  KernelHandle? pending=null;
  try {
   int prior=0;foreach(var row in captures){token.ThrowIfCancellationRequested();Win32.Need(prior++<20&&lifetime.ElapsedMilliseconds<5000,"Capture duplicate scan20/5seconds");Win32.Need(row.Identity.Pid!=pid,"Duplicate raw PID capture rejected");}
   ProcessIdentity? expected=null;KernelHandle? borrowedHandle=null;int count=0;
   foreach(var pair in borrowed){token.ThrowIfCancellationRequested();Win32.Need(count++<1024&&lifetime.ElapsedMilliseconds<5000,"Borrowed scan1024/5seconds");if(pair.Key.Pid!=pid)continue;Win32.Need(expected==null,"Ambiguous borrowed PID lifetimes");expected=pair.Key;borrowedHandle=pair.Value;}
   KernelHandle handle;
   if(borrowedHandle!=null){handle=borrowedHandle;attempt.Borrowed=true;}
   else{pending=Win32.OpenProcess(0x101400,0,pid);handle=pending;}
   attempt.OriginalHandle=handle.DangerousGetHandle().ToInt64();Win32.CheckHandle(handle,"Capture original process handle");
   Win32.Need(!handle.IsClosed&&Win32.WaitForSingleObject(handle,0)==258,"Only a live exact listed identity can be captured");
   var identity=Win32.Identity(handle,pid);attempt.Identity=identity;
   if(expected!=null)Win32.Need(identity==expected,"Borrowed original full identity mismatch");
   Win32.Check(Win32.IsProcessInJob(handle,job.Handle,out int member),"Capture exact private Job");Win32.Need(member!=0,"Captured member outside exact Job");
   token.ThrowIfCancellationRequested();Win32.Need(lifetime.ElapsedMilliseconds<5000,"Capture5second bound");
   var captured=new CapturedHandle(identity,handle,attempt.Borrowed,attempt.OriginalHandle.Value);
   // Both collections must succeed before relinquishing the local finally owner.
   captures.Add(captured);
   try{if(pending!=null)owned.Add(identity,pending);}catch{captures.RemoveAt(captures.Count-1);throw;}
   pending=null;attempt.Validated=true;return captured;
  }catch(Exception error){faulted=true;attempt.Error=error.ToString();throw;}
  finally{if(pending!=null){pending.Dispose();attempt.ClosedOnFailure=pending.CloseSucceeded;}attempt.Finished=DateTime.UtcNow.ToFileTimeUtc();}
 }
 internal OwnedHandleReceiver Transfer(Action<IReadOnlyList<CapturedHandle>> validate,CancellationToken token) {
  ArgumentNullException.ThrowIfNull(validate);Mutable(token);transferring=true;
  try {
   // A throwing validation leaves all originals owned by this lease.
   validate(captures.AsReadOnly());token.ThrowIfCancellationRequested();int checkedCount=0;
   foreach(var row in captures){token.ThrowIfCancellationRequested();Win32.Need(checkedCount++<20&&lifetime.ElapsedMilliseconds<5000,"Transfer20/5seconds");Win32.Need(!row.Handle.IsClosed&&row.Handle.DangerousGetHandle().ToInt64()==row.OriginalValue,"Original handle object/value retained");Win32.Need(Win32.Identity(row.Handle,row.Identity.Pid)==row.Identity,"Transfer same-handle full identity");Win32.Check(Win32.IsProcessInJob(row.Handle,job.Handle,out int member),"Transfer exact private Job");Win32.Need(member!=0,"Transfer member outside exact Job");}
   token.ThrowIfCancellationRequested();Win32.Need(lifetime.ElapsedMilliseconds<5000,"Transfer5second bound");
   // All allocations precede the ownership commit; no OpenProcess, DuplicateHandle
   // or receiver callbacks occur at or after this move.
   var replacement=new Dictionary<ProcessIdentity,KernelHandle>();var receiver=new OwnedHandleReceiver(owned);
   owned=replacement;transferred=true;return receiver;
  }catch{faulted=true;throw;}finally{transferring=false;}
 }
 public void Dispose() {
  if(disposed)return;disposed=true;
  // This lease can acquire <=20 originals. Disposal cannot skip owned handles
  // because of cancellation/time expiry; SafeHandle close is a nonwaiting call.
  CloseOwned(owned);owned.Clear();
 }
 internal static void CloseOwned(Dictionary<ProcessIdentity,KernelHandle> handles) {
  Win32.Need(handles.Count<=20,"Owned close batch cap20");var watch=Stopwatch.StartNew();List<Exception>? errors=null;
  // Native CloseHandle does not wait. Complete every one of the <=20 mandatory
  // closes even if cancellation/deadline fires; report elapsed deadline afterward.
  foreach(var handle in handles.Values){try{handle.Dispose();}catch(Exception error){(errors??=new()).Add(error);}}
  if(watch.ElapsedMilliseconds>=5000)(errors??=new()).Add(new TimeoutException("Owned nonwaiting close batch exceeded5seconds"));
  if(errors!=null)throw new AggregateException("Every owned original close attempted",errors);
 }
}

internal sealed class OwnedHandleReceiver:IDisposable {
 private readonly Dictionary<ProcessIdentity,KernelHandle> owned;private bool disposed;
 internal OwnedHandleReceiver(Dictionary<ProcessIdentity,KernelHandle> owned){Win32.Need(owned.Count<=20,"Transferred active batch cap20");this.owned=owned;}
 internal int Count=>owned.Count;
 internal KernelHandle Get(ProcessIdentity identity){ObjectDisposedException.ThrowIf(disposed,this);return owned[identity];}
 public void Dispose(){if(disposed)return;disposed=true;OriginalHandleLease.CloseOwned(owned);}
}
