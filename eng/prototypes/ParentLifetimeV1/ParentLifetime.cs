#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading;
using RustSharp.ProcessContainment;
namespace RustSharp.ParentPrototype;

internal sealed record LifetimeRow(ProcessIdentity Identity,long Birth,long Exit,long ObservedStarted,long ObservedFinished,bool Terminal,bool OriginalIdentityAndJobVerified,string EvidenceKind,long OriginalHandle,[property:JsonIgnore] object? JobToken=null);
internal sealed record ChainProof(bool Accepted,string Reason,IReadOnlyList<ProcessIdentity> ChildToRoot);
// Consumes retained handles only. No OpenProcess or PID lookup exists here.
internal sealed class RetainedLifetime {
 private readonly KernelHandle handle;private readonly JobLease job;private readonly ProcessIdentity identity;private readonly long original;
 internal RetainedLifetime(KernelHandle handle,ProcessIdentity identity,JobLease job) {
  this.handle=handle;this.identity=identity;this.job=job;original=handle.DangerousGetHandle().ToInt64();
  Win32.Need(!handle.IsClosed&&Win32.WaitForSingleObject(handle,0)==258,"Pin live retained original");
  Win32.Need(Win32.Identity(handle,identity.Pid)==identity,"Pin full identity on original");
  Win32.Check(Win32.IsProcessInJob(handle,job.Handle,out int member),"Pin exact private Job");Win32.Need(member!=0,"Pin outside Job");
 }
 internal LifetimeRow Read(CancellationToken cancellation) {
  cancellation.ThrowIfCancellationRequested();long started=DateTime.UtcNow.ToFileTimeUtc();
  Win32.Need(!handle.IsClosed&&handle.DangerousGetHandle().ToInt64()==original,"Retained original remains open");
  uint state=Win32.WaitForSingleObject(handle,0);Win32.Need(state is 0 or 258,"Original native wait");
  Win32.Check(Win32.GetProcessTimes(handle,out long birth,out long exit,out _,out _),"Original native birth/exit");Win32.Need(birth==identity.CreationFileTime,"Same original creation identity");
  if(state==258){Win32.Need(exit==0&&Win32.Identity(handle,identity.Pid)==identity,"Live full retained identity");Win32.Check(Win32.IsProcessInJob(handle,job.Handle,out int member),"Live exact Job");Win32.Need(member!=0&&Win32.WaitForSingleObject(handle,0)==258,"Live state/membership changed during sample");}
  else{Win32.Need(exit>birth,"Terminal original native exit interval");Win32.Check(Win32.GetExitCodeProcess(handle,out _),"Terminal original actual exit");}
  cancellation.ThrowIfCancellationRequested();return new(identity,birth,exit,started,DateTime.UtcNow.ToFileTimeUtc(),state==0,true,"native-retained-original",original,job);
 }
}
internal static class ParentLifetime {
 internal static ChainProof Validate(ProcessIdentity root,ProcessIdentity child,IReadOnlyList<LifetimeRow> rows,CancellationToken cancellation) {
  var path=new List<ProcessIdentity>();var byPid=new Dictionary<uint,LifetimeRow>();var clock=Stopwatch.StartNew();
  ChainProof Reject(string reason)=>new(false,reason,path);
  if(rows.Count is <1 or >1024)return Reject("inventory-cap1024");
  for(int index=0;index<rows.Count&&index<1024&&clock.ElapsedMilliseconds<5000;index++) {
   cancellation.ThrowIfCancellationRequested();var row=rows[index];
   if(row.JobToken==null||!row.OriginalIdentityAndJobVerified||row.Birth!=row.Identity.CreationFileTime||row.Birth<=0||row.ObservedFinished<row.ObservedStarted||row.Birth>=row.ObservedStarted||row.Terminal!=(row.Exit!=0)||row.Exit!=0&&row.Exit<=row.Birth)return Reject("invalid-original-lifetime");
   if(!byPid.TryAdd(row.Identity.Pid,row))return Reject("duplicate-pid-lifetimes");
  }
  if(byPid.Count!=rows.Count)return Reject("inventory-wall-bound");
  if(!byPid.TryGetValue(root.Pid,out var originalRoot)||originalRoot.Identity!=root)return Reject("exact-root-missing");
  for(int index=0;index<rows.Count&&index<1024&&clock.ElapsedMilliseconds<5000;index++){cancellation.ThrowIfCancellationRequested();if(!ReferenceEquals(rows[index].JobToken,originalRoot.JobToken))return Reject("cross-job-token");}
  if(clock.ElapsedMilliseconds>=5000)return Reject("job-token-wall-bound");
  if(!byPid.TryGetValue(child.Pid,out var current)||current.Identity!=child)return Reject("exact-child-missing");
  var visited=new HashSet<uint>();
  for(int depth=0;depth<rows.Count&&depth<1024&&clock.ElapsedMilliseconds<5000;depth++) {
   cancellation.ThrowIfCancellationRequested();if(!visited.Add(current.Identity.Pid))return Reject("parent-cycle");path.Add(current.Identity);
   if(current.Identity==root) {
    // Resolve the complete original-root chain before evaluating strict intervals.
    for(int edge=0;edge<path.Count-1&&edge<1024&&clock.ElapsedMilliseconds<5000;edge++) {
     cancellation.ThrowIfCancellationRequested();var younger=byPid[path[edge].Pid];var parent=byPid[path[edge+1].Pid];
     if(younger.Birth<=parent.Birth)return Reject(younger.Birth==parent.Birth?"equal-birth-ambiguity":"child-before-parent-birth");
     long upper=parent.Terminal?parent.Exit:parent.ObservedStarted;
     if(younger.Birth>=upper)return Reject(younger.Birth==upper?"equal-upper-ambiguity":"child-after-parent-lifetime");
    }
    if(clock.ElapsedMilliseconds>=5000)return Reject("edge-wall-bound");return new(true,"complete-original-root-lifetime-chain",path);
   }
   if(!byPid.TryGetValue(current.Identity.ParentPid,out var parentRow))return Reject("missing-retained-parent");current=parentRow;
  }
  return Reject(visited.Contains(current.Identity.Pid)?"parent-cycle":"chain-count-or-wall-bound");
 }
}
