#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace RustSharp.ProcessContainment;
internal sealed class NativeProcessTerminatingException:IOException {
 internal NativeProcessTerminatingException():base("NT command query STATUS_PROCESS_IS_TERMINATING (0xC000010A)"){}
}
public sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid {
 public KernelHandle():base(true){}
 internal KernelHandle(IntPtr value):base(true){SetHandle(value);}
 public bool? CloseSucceeded {get;private set;}
 public int? CloseLastError {get;private set;}
 protected override bool ReleaseHandle(){int native=Win32.CloseHandle(handle);CloseLastError=Marshal.GetLastPInvokeError();bool result=native!=0;CloseSucceeded=result;return result;}
}
public sealed record ProcessIdentity(uint Pid,long CreationFileTime,uint ParentPid,string CommandLine);
public sealed record ParentChain(IReadOnlyList<ProcessIdentity> Nodes,bool Complete,string StopReason);
public sealed record TerminalWaitResult(bool Terminal,bool BirthVerified,uint? ExitCode,int Checks,long ElapsedMilliseconds);
internal static partial class Win32 {
 internal static TerminalWaitResult WaitOwnedTerminal(KernelHandle handle,ProcessIdentity expected,int milliseconds,CancellationToken cancellation)
 {
  Need(milliseconds is >0 and <=3000,"Owned terminal wait1..3000ms");var watch=Stopwatch.StartNew();int checks=0;
  // At most120 native waits, at most caller milliseconds, cancellation every25ms.
  for(int index=0;index<120&&watch.ElapsedMilliseconds<milliseconds;index++)
  {
   cancellation.ThrowIfCancellationRequested();Check(GetProcessTimes(handle,out long birth,out _,out _,out _),"owned terminal wait birth");Need(birth==expected.CreationFileTime,"Owned terminal same-handle creation mismatch");
   uint interval=checked((uint)Math.Max(1,Math.Min(25,milliseconds-watch.ElapsedMilliseconds)));uint state=WaitForSingleObject(handle,interval);checks++;Need(state is 0 or 258,"Owned terminal native wait error");
   if(state==0){Check(GetProcessTimes(handle,out long after,out _,out _,out _),"owned terminal final birth");Need(after==expected.CreationFileTime,"Owned terminal final creation mismatch");Check(GetExitCodeProcess(handle,out uint code),"owned terminal actual exit");return new(true,true,code,checks,watch.ElapsedMilliseconds);}
  }
  return new(false,true,null,checks,watch.ElapsedMilliseconds);
 }
 [StructLayout(LayoutKind.Sequential)] internal struct Startup {
  public uint Size;public IntPtr Reserved,Desktop,Title;public uint X,Y,XSize,YSize,XChars,YChars,Fill,Flags;public ushort Show,ReservedSize;public IntPtr ReservedBytes,Input,Output,Error;
 }
 [StructLayout(LayoutKind.Sequential)] internal struct Created {public IntPtr Process,Thread;public uint Pid,Tid;}
 // PBI offset40's inherited PID and NT class60 are Windows-specific internals.
 // Known v2 runtime verified them; any unsupported status blocks instead of guessing.
 [StructLayout(LayoutKind.Sequential)] internal struct Basic {public IntPtr Reserved,Peb,Reserved2,Reserved3,Pid,Parent;}
 [StructLayout(LayoutKind.Sequential)] internal struct BasicLimit {public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinWs,MaxWs;public uint Active;public UIntPtr Affinity;public uint Priority,Scheduling;}
 [StructLayout(LayoutKind.Sequential)] internal struct Io {public ulong A,B,C,D,E,F;}
 [StructLayout(LayoutKind.Sequential)] internal struct JobLimit {public BasicLimit Basic;public Io Io;public UIntPtr A,B,C,D;}
 // BOOL is an explicit 32-bit int. UTF-16 W API; command is caller-owned mutable HGlobal.
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] internal static extern int CreateProcessW(string application,IntPtr command,IntPtr processAttributes,IntPtr threadAttributes,int inheritHandles,uint flags,IntPtr environment,string directory,ref Startup startup,out Created created);
 [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] internal static extern KernelHandle CreateJobObjectW(IntPtr attributes,IntPtr name);
 [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] internal static extern int SetInformationJobObject(KernelHandle job,int kind,ref JobLimit information,uint bytes);
 [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] internal static extern int AssignProcessToJobObject(KernelHandle job,KernelHandle process);
 [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] internal static extern int IsProcessInJob(KernelHandle process,KernelHandle job,out int member);
 [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] internal static extern int TerminateJobObject(KernelHandle job,uint code);
 [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] internal static extern int TerminateProcess(KernelHandle process,uint code);
 [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] internal static extern uint ResumeThread(KernelHandle thread);
 [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] internal static extern uint WaitForSingleObject(KernelHandle process,uint milliseconds);
 [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] internal static extern int GetExitCodeProcess(KernelHandle process,out uint code);
 [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] internal static extern int GetProcessTimes(KernelHandle process,out long creation,out long exit,out long kernel,out long user);
 [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] internal static extern KernelHandle OpenProcess(uint access,int inherit,uint pid);
 [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)] internal static extern int CloseHandle(IntPtr handle);
 [DllImport("ntdll.dll",EntryPoint="NtQueryInformationProcess",ExactSpelling=true)] internal static extern int QueryBasic(KernelHandle process,uint kind,ref Basic information,uint bytes,out uint returned);
 [DllImport("ntdll.dll",EntryPoint="NtQueryInformationProcess",ExactSpelling=true)] internal static extern int QueryCommand(KernelHandle process,uint kind,IntPtr information,uint bytes,out uint returned);
 internal static void Check(int result,string operation){int error=Marshal.GetLastPInvokeError();if(result==0)throw new System.ComponentModel.Win32Exception(error,operation);}
 internal static void CheckHandle(KernelHandle handle,string operation){int error=Marshal.GetLastPInvokeError();if(handle.IsInvalid)throw new System.ComponentModel.Win32Exception(error,operation);}
 internal static void Need(bool condition,string message){if(!condition)throw new InvalidDataException(message);}
 internal static void Layout<T>(int size,params (string Field,int Offset)[] fields){Need(Marshal.SizeOf<T>()==size,typeof(T).Name+" size");foreach(var f in fields)Need(Marshal.OffsetOf<T>(f.Field).ToInt32()==f.Offset,typeof(T).Name+"."+f.Field+" offset");}
 internal static void Audit(){
  Need(OperatingSystem.IsWindows()&&IntPtr.Size==8,"Windows x64 only");Layout<Startup>(104,("Flags",60),("ReservedBytes",72),("Input",80));Layout<Created>(24,("Pid",16));Layout<Basic>(48,("Pid",32),("Parent",40));Layout<BasicLimit>(64,("MinWs",24),("Affinity",48));Layout<Io>(48);Layout<JobLimit>(144,("Io",64),("A",112));
 }
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] internal static extern int QueryFullProcessImageNameW(KernelHandle process,uint flags,[Out] char[] image,ref uint size);
 internal static string Command(KernelHandle process){
  const int size=65536;IntPtr buffer=Marshal.AllocHGlobal(size);
  try{int status=QueryCommand(process,60,buffer,size,out uint returned);if(unchecked((uint)status)==0xC000010Au)throw new NativeProcessTerminatingException();Need(status==0&&returned>=16&&returned<=size,"NT command query status=0x"+status.ToString("X8",System.Globalization.CultureInfo.InvariantCulture));
   int length=(ushort)Marshal.ReadInt16(buffer);IntPtr text=Marshal.ReadIntPtr(buffer,8);long lower=buffer.ToInt64(),pointer=text.ToInt64();Need(length%2==0&&pointer>=lower&&pointer<=lower+size-length,"UNICODE_STRING pointer/length bounds");return Marshal.PtrToStringUni(text,length/2)??throw new InvalidDataException("Missing OS command");
  }finally{Marshal.FreeHGlobal(buffer);}
 }
 internal static ProcessIdentity Identity(KernelHandle process,uint expected){
  Check(GetProcessTimes(process,out long creation,out _,out _,out _),"GetProcessTimes");var basic=new Basic();int status=QueryBasic(process,0,ref basic,48,out uint returned);Need(status==0&&returned==48&&basic.Pid.ToInt64()==expected,"NT PID/parent identity status=0x"+status.ToString("X8",System.Globalization.CultureInfo.InvariantCulture));
  string command=Command(process);Check(GetProcessTimes(process,out long after,out _,out _,out _),"identity creation recheck");Need(after==creation,"Exact native FILETIME changed");return new(expected,creation,checked((uint)basic.Parent.ToInt64()),command);
 }
}
public static class ProcessAudit {
 private static readonly System.Buffers.SearchValues<char> InvalidArgumentChars=System.Buffers.SearchValues.Create("\0\r\n");
 public static string VerifyRoot(string root,CancellationToken token){Win32.Need(Path.IsPathFullyQualified(root),"Absolute exclusive root required");string exact=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);var clock=Stopwatch.StartNew();DirectoryInfo? node=new(exact);for(int depth=0;node!=null&&depth<64&&clock.Elapsed.TotalSeconds<5;depth++){token.ThrowIfCancellationRequested();Win32.Need(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)==0,"Existing non-reparse root ancestry required");node=node.Parent;}Win32.Need(node==null,"Root ancestry64-node/5-second bound");return exact;}
 public static string ImagePath(ProcessIdentity expected){using var process=Win32.OpenProcess(0x101400,0,expected.Pid);Win32.CheckHandle(process,"Retained native image query");Win32.Need(Win32.Identity(process,expected.Pid)==expected,"Exact identity before image query");var image=new char[32768];uint size=32768;Win32.Check(Win32.QueryFullProcessImageNameW(process,0,image,ref size),"QueryFullProcessImageNameW");Win32.Need(size>0&&size<32768&&Path.IsPathFullyQualified(new string(image,0,checked((int)size)))&&Win32.Identity(process,expected.Pid)==expected,"Absolute native image with exact identity recheck");return Path.GetFullPath(new string(image,0,checked((int)size)));}
 public static ProcessIdentity Read(uint pid){using var handle=Win32.OpenProcess(0x101400,0,pid);Win32.CheckHandle(handle,"OpenProcess exact query/synchronize target");return Win32.Identity(handle,pid);}
 public static BoundaryChain ToBoundary(uint first,ProcessIdentity host,CancellationToken cancellation){
  var watch=Stopwatch.StartNew();var rows=new List<ProcessIdentity>();var seen=new HashSet<uint>();uint current=first;
  try{for(int index=0;index<16&&watch.Elapsed.TotalSeconds<5;index++){cancellation.ThrowIfCancellationRequested();Win32.Need(current!=0&&seen.Add(current),"Host absent or ancestry cycle");var row=Read(current);rows.Add(row);if(current==host.Pid){Win32.Need(row==host&&Read(current)==host,"Live host exact PID/FILETIME/command/parent double check");return new(rows,true,false,"Reviewed host boundary reached; upstream system ancestry remains separately incomplete");}current=row.ParentPid;}
   return new(rows,false,false,"16-node / 5-second boundary before reviewed host");
  }catch(Exception e){return new(rows,false,false,e.GetType().Name+": "+e.Message);}
 }
 public static ParentChain Capture(uint first,CancellationToken cancellation){
  var watch=Stopwatch.StartNew();var rows=new List<ProcessIdentity>();var seen=new HashSet<uint>();uint current=first;
  try{
   // Manually reviewed comparisons: <=16 process nodes and <=5 seconds; no retry.
   for(int index=0;index<16&&watch.Elapsed.TotalSeconds<5;index++){
    cancellation.ThrowIfCancellationRequested();if(current==0)return new(rows,true,"parent PID zero");Win32.Need(seen.Add(current),"Parent chain cycle");
    using var handle=Win32.OpenProcess(0x1400,0,current);Win32.CheckHandle(handle,"OpenProcess query-only ancestor");var row=Win32.Identity(handle,current);rows.Add(row);current=row.ParentPid;
   }
   return new(rows,current==0,current==0?"parent PID zero":"16-node / 5-second boundary");
  }catch(Exception e){return new(rows,false,e.GetType().Name+": "+e.Message);}
 }
 public static string Quote(string value){
  Win32.Need(value.Length<=32767&&value.AsSpan().IndexOfAny(InvalidArgumentChars)<0,"Bounded literal Windows argument");bool quoting=value.Length==0;foreach(char item in value){if(char.IsWhiteSpace(item)||item=='"'){quoting=true;break;}}if(!quoting)return value;var b=new StringBuilder("\"");int slashes=0;
  foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='"')b.Append('\\',slashes*2+1).Append(c);else b.Append('\\',slashes).Append(c);slashes=0;}return b.Append('\\',slashes*2).Append('"').ToString();
 }
 public static void ValidateLayouts()=>Win32.Audit();
}
public sealed record BoundaryChain(IReadOnlyList<ProcessIdentity> Nodes,bool BoundaryVerified,bool SystemAncestorsComplete,string StopReason);
