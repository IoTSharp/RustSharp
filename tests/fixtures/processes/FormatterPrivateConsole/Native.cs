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

namespace FormatterPrivateConsoleFixture;
public sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid {
 private KernelHandle():base(true){}
 internal KernelHandle(IntPtr value):base(true){SetHandle(value);}
 public bool? CloseSucceeded {get;private set;}
 public int? CloseLastError {get;private set;}
 protected override bool ReleaseHandle(){int native=Win32.CloseHandle(handle);CloseLastError=Marshal.GetLastPInvokeError();bool result=native!=0;CloseSucceeded=result;return result;}
}
public sealed record ProcessIdentity(uint Pid,long CreationFileTime,uint ParentPid,string CommandLine);
public sealed record ParentChain(IReadOnlyList<ProcessIdentity> Nodes,bool Complete,string StopReason);
internal static partial class Win32 {
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
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] internal static extern int QueryFullProcessImageNameW(KernelHandle process,uint flags,StringBuilder image,ref uint size);
 internal static string Command(KernelHandle process){
  const int size=65536;IntPtr buffer=Marshal.AllocHGlobal(size);
  try{int status=QueryCommand(process,60,buffer,size,out uint returned);Need(status==0&&returned>=16&&returned<=size,"NT command query status=0x"+status.ToString("X8"));
   int length=(ushort)Marshal.ReadInt16(buffer);IntPtr text=Marshal.ReadIntPtr(buffer,8);long lower=buffer.ToInt64(),pointer=text.ToInt64();Need(length%2==0&&pointer>=lower&&pointer<=lower+size-length,"UNICODE_STRING pointer/length bounds");return Marshal.PtrToStringUni(text,length/2)??throw new InvalidDataException("Missing OS command");
  }finally{Marshal.FreeHGlobal(buffer);}
 }
 internal static ProcessIdentity Identity(KernelHandle process,uint expected){
  Check(GetProcessTimes(process,out long creation,out _,out _,out _),"GetProcessTimes");var basic=new Basic();int status=QueryBasic(process,0,ref basic,48,out uint returned);Need(status==0&&returned==48&&basic.Pid.ToInt64()==expected,"NT PID/parent identity status=0x"+status.ToString("X8"));
  string command=Command(process);Check(GetProcessTimes(process,out long after,out _,out _,out _),"identity creation recheck");Need(after==creation,"Exact native FILETIME changed");return new(expected,creation,checked((uint)basic.Parent.ToInt64()),command);
 }
}
public static class ProcessAudit {
 public static string VerifyRoot(string root,CancellationToken token){Win32.Need(Path.IsPathFullyQualified(root),"Absolute exclusive root required");string exact=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);var clock=Stopwatch.StartNew();DirectoryInfo? node=new(exact);for(int depth=0;node!=null&&depth<64&&clock.Elapsed.TotalSeconds<5;depth++){token.ThrowIfCancellationRequested();Win32.Need(node.Exists&&(node.Attributes&FileAttributes.ReparsePoint)==0,"Existing non-reparse root ancestry required");node=node.Parent;}Win32.Need(node==null,"Root ancestry64-node/5-second bound");return exact;}
 public static string ImagePath(ProcessIdentity expected){using var process=Win32.OpenProcess(0x101400,0,expected.Pid);Win32.CheckHandle(process,"Retained native image query");Win32.Need(Win32.Identity(process,expected.Pid)==expected,"Exact identity before image query");var image=new StringBuilder(32768);uint size=32768;Win32.Check(Win32.QueryFullProcessImageNameW(process,0,image,ref size),"QueryFullProcessImageNameW");Win32.Need(size>0&&size<32768&&Path.IsPathFullyQualified(image.ToString())&&Win32.Identity(process,expected.Pid)==expected,"Absolute native image with exact identity recheck");return Path.GetFullPath(image.ToString());}
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
  Win32.Need(value.Length<=32767&&value.IndexOfAny(new[]{'\0','\r','\n'})<0,"Bounded literal Windows argument");var b=new StringBuilder("\"");int slashes=0;
  foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='"')b.Append('\\',slashes*2+1).Append(c);else b.Append('\\',slashes).Append(c);slashes=0;}return b.Append('\\',slashes*2).Append('"').ToString();
 }
 public static void ValidateLayouts()=>Win32.Audit();
}
public sealed record BoundaryChain(IReadOnlyList<ProcessIdentity> Nodes,bool BoundaryVerified,bool SystemAncestorsComplete,string StopReason);

public sealed record PublicationStageError(string Stage,string Error);
public sealed class PublicationFailure:IOException {
 public string Target {get;}public string Temporary {get;}public string? PrimaryError {get;}public IReadOnlyList<PublicationStageError> SecondaryErrors {get;}public bool Published {get;}public bool? TemporaryRemoved {get;}
 public PublicationFailure(string target,string temporary,Exception? primary,IReadOnlyList<PublicationStageError> secondary,bool published,bool? removed):base("Atomic publication failed; primary and every cleanup error retained",primary){Target=target;Temporary=temporary;PrimaryError=primary?.ToString();SecondaryErrors=secondary;Published=published;TemporaryRemoved=removed;}
 public override string ToString()=>base.ToString()+"\nTarget="+Target+"; Temporary="+Temporary+"; Published="+Published+"; TemporaryRemoved="+TemporaryRemoved+"\n"+string.Join("\n",SecondaryErrors.Select(x=>x.Stage+": "+x.Error));
}

public sealed class BoundedIo {
 readonly CancellationToken token;readonly Stopwatch lifetime=Stopwatch.StartNew();readonly int seconds;long sharedBytes;int files,chunks;readonly string root,nonce;
 public long SharedBytes=>sharedBytes;public int Files=>files;public int Chunks=>chunks;
 public BoundedIo(string root,string nonce,CancellationToken token,int seconds){this.root=Path.GetFullPath(root);this.nonce=nonce;this.token=token;this.seconds=seconds;Win32.Need(seconds>0&&seconds<=120,"I/O lifetime120 bound");}
 void Check(Stopwatch operation){token.ThrowIfCancellationRequested();Win32.Need(lifetime.Elapsed.TotalSeconds<seconds&&operation.Elapsed.TotalSeconds<30,"I/O monotonic lifetime/operation deadline");}
 string Ordinary(string path){Win32.Need(Path.IsPathFullyQualified(path),"Absolute I/O file required");string exact=Path.GetFullPath(path);Win32.Need((File.GetAttributes(exact)&(FileAttributes.Directory|FileAttributes.ReparsePoint))==0,"Ordinary file; no reparse I/O");return exact;}
 public byte[] Read(string path){var watch=Stopwatch.StartNew();Check(watch);Win32.Need(++files<=100,"Shared I/O100 file bound");string exact=Ordinary(path);using var input=new FileStream(exact,FileMode.Open,FileAccess.Read,FileShare.Read,16384,FileOptions.SequentialScan);Win32.Need(input.Length<=1048576,"Input file1MiB bound");using var result=new MemoryStream();var buffer=new byte[16384];
  // Comparisons manually checked: <=100files, <=1200chunks, <=16MiB, <=1MiB/file, <=30s/op and bounded lifetime.
  for(int index=0;index<66;index++){Check(watch);int count=input.Read(buffer,0,buffer.Length);Check(watch);if(count==0)return result.ToArray();Win32.Need(++chunks<=1200&&(sharedBytes+=count)<=16777216&&result.Length+count<=1048576,"Shared/per-file byte/chunk bound");result.Write(buffer,0,count);}throw new InvalidDataException("Read chunk66 bound");
 }
 public string Text(string path)=>new UTF8Encoding(false,true).GetString(Read(path));
 public string Hash(string path)=>Convert.ToHexString(SHA256.HashData(Read(path)));
 public void Publish(string path,string text,bool fresh,bool cleanup=false){var watch=Stopwatch.StartNew();void PublicationCheck(){if(cleanup)Win32.Need(watch.Elapsed.TotalSeconds<5,"Final cleanup publication5-second bound");else Check(watch);}PublicationCheck();string exact=Path.GetFullPath(path);Win32.Need(exact.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Publication stays in exclusive root");byte[] bytes=new UTF8Encoding(false).GetBytes(text);Win32.Need(bytes.Length<=1048576,"Publication1MiB bound");string temporary=exact+"."+nonce+"."+Guid.NewGuid().ToString("N")+".owned-write";
  FileStream? file=null;Exception? primary=null;var secondary=new List<PublicationStageError>();bool published=false,created=false;bool? removed=null;
  try{file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,16384,FileOptions.WriteThrough);created=true;for(int offset=0;offset<bytes.Length;offset+=16384){PublicationCheck();file.Write(bytes,offset,Math.Min(16384,bytes.Length-offset));}file.Flush(true);file.Dispose();file=null;PublicationCheck();File.Move(temporary,exact,!fresh);published=true;removed=true;}
  catch(Exception error){primary=error;}
  finally{
   if(file!=null)try{file.Dispose();}catch(Exception closeError){secondary.Add(new("stream-close",closeError.ToString()));}
   if(created&&!published)try{string resolved=Path.GetFullPath(temporary);Win32.Need(resolved.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&resolved.EndsWith(".owned-write",StringComparison.Ordinal),"Exact owned publication temporary");Win32.Need((File.GetAttributes(resolved)&(FileAttributes.Directory|FileAttributes.ReparsePoint))==0,"Owned temporary ordinary file");File.Delete(resolved);try{File.GetAttributes(resolved);throw new IOException("Owned publication temporary still present after deletion");}catch(FileNotFoundException){removed=true;}catch(DirectoryNotFoundException){removed=true;}}
   catch(FileNotFoundException){removed=true;}catch(DirectoryNotFoundException){removed=true;}catch(Exception cleanupError){removed=false;secondary.Add(new("owned-temporary-cleanup",cleanupError.ToString()));}
  }
  if(primary!=null||secondary.Count!=0)throw new PublicationFailure(exact,temporary,primary,secondary,published,removed);
 }
}
