#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RustSharp.ProcessContainment;
internal static partial class CaptureNative
{
 [StructLayout(LayoutKind.Sequential)] internal struct Startup {
  internal uint Size; internal nint Reserved,Desktop,Title; internal uint X,Y,XSize,YSize,XChars,YChars,Fill,Flags;
  internal ushort Show,ReservedCount; internal nint ReservedBytes,Input,Output,Error;
 }
 [StructLayout(LayoutKind.Sequential)] internal struct StartupEx {internal Startup Startup;internal nint Attributes;}
 [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo {internal nint Process,Thread;internal uint Pid,Tid;}
 [LibraryImport("kernel32.dll",EntryPoint="CreateFileW",StringMarshalling=StringMarshalling.Utf16,SetLastError=true)]
 internal static partial nint CreateFile(string name,uint access,uint share,nint security,uint creation,uint flags,nint template);
 [LibraryImport("kernel32.dll",SetLastError=true)] internal static partial int SetHandleInformation(nint handle,uint mask,uint flags);
 [LibraryImport("kernel32.dll",SetLastError=true)] internal static partial int InitializeProcThreadAttributeList(nint list,int count,int flags,ref nuint bytes);
 [LibraryImport("kernel32.dll",SetLastError=true)] internal static partial int UpdateProcThreadAttribute(nint list,uint flags,nuint attribute,nint value,nuint bytes,nint previous,nint returned);
 [LibraryImport("kernel32.dll")] internal static partial void DeleteProcThreadAttributeList(nint list);
 [LibraryImport("kernel32.dll",EntryPoint="CreateProcessW",StringMarshalling=StringMarshalling.Utf16,SetLastError=true)]
 internal static partial int CreateProcess(string application,nint command,nint processAttributes,nint threadAttributes,int inherit,uint flags,nint environment,string directory,ref StartupEx startup,out ProcessInfo process);
}
public sealed partial class OwnedSuspendedProcess
{
 public static OwnedSuspendedProcess StartCaptured(string exe,IReadOnlyList<string> arguments,string cwd,JobLease job,string callerLeaseRoot,string stdoutPath,string stderrPath,Action<LaunchReceipt>? persist=null)
 {
  ArgumentNullException.ThrowIfNull(job);ProcessAudit.ValidateLayouts();
  Win32.Need(arguments.Count<=24&&Path.IsPathFullyQualified(exe),"Capture exact executable/24 arguments");
  string root=Path.GetFullPath(callerLeaseRoot);Win32.Need(root==callerLeaseRoot&&Directory.Exists(root),"Capture exact caller lease root");
  DirectoryInfo? ancestor=new(root);var pathClock=Stopwatch.StartNew();
  for(int depth=0;ancestor!=null&&depth<16&&pathClock.ElapsedMilliseconds<1000;depth++){Win32.Need((ancestor.Attributes&FileAttributes.ReparsePoint)==0,"No capture root reparse");ancestor=ancestor.Parent;}
  Win32.Need(ancestor==null,"Capture root ancestry16/1second");
  foreach(string path in new[]{stdoutPath,stderrPath})Win32.Need(Path.IsPathFullyQualified(path)&&Path.GetDirectoryName(Path.GetFullPath(path))==root&&!File.Exists(path)&&!Directory.Exists(path),"Fresh exact caller lease output");
  Win32.Need(stdoutPath!=stderrPath,"Separate binary stdout/stderr");
  string command=string.Join(" ",new[]{exe}.Concat(arguments).Select(ProcessAudit.Quote));Win32.Need(command.Length<=32767,"Windows capture command bound");
  var receipt=new LaunchReceipt{IntendedCommand=command};KernelHandle? process=null,thread=null,input=null,output=null,error=null;
  nint commandMemory=0,attributeList=0,handleList=0;bool initialized=false;
  try
  {
   input=new KernelHandle(CaptureNative.CreateFile(@"\\.\NUL",0x80000000,3,0,3,0x80,0));Win32.CheckHandle(input,"capture stdin NUL");
   output=new KernelHandle(CaptureNative.CreateFile(stdoutPath,0x40000000,1,0,1,0x80,0));Win32.CheckHandle(output,"CreateNew binary stdout");
   error=new KernelHandle(CaptureNative.CreateFile(stderrPath,0x40000000,1,0,1,0x80,0));Win32.CheckHandle(error,"CreateNew binary stderr");
   foreach(KernelHandle handle in new[]{input,output,error})Win32.Check(CaptureNative.SetHandleInformation(handle.DangerousGetHandle(),1,1),"Exact capture handle inherit flag");
   nuint bytes=0;int probe=CaptureNative.InitializeProcThreadAttributeList(0,1,0,ref bytes);int probeError=Marshal.GetLastPInvokeError();
   Win32.Need(probe==0&&probeError==122&&bytes>0&&bytes<=65536,"Attribute list exact sizing");
   attributeList=Marshal.AllocHGlobal(checked((int)bytes));Win32.Check(CaptureNative.InitializeProcThreadAttributeList(attributeList,1,0,ref bytes),"Initialize capture handle list");initialized=true;
   handleList=Marshal.AllocHGlobal(3*IntPtr.Size);Marshal.WriteIntPtr(handleList,0,input.DangerousGetHandle());Marshal.WriteIntPtr(handleList,IntPtr.Size,output.DangerousGetHandle());Marshal.WriteIntPtr(handleList,2*IntPtr.Size,error.DangerousGetHandle());
   Win32.Check(CaptureNative.UpdateProcThreadAttribute(attributeList,0,0x20002,handleList,checked((nuint)(3*IntPtr.Size)),0,0),"Only three inherited handles");
   var startup=new CaptureNative.StartupEx{Startup=new CaptureNative.Startup{Size=112,Flags=0x100,Input=input.DangerousGetHandle(),Output=output.DangerousGetHandle(),Error=error.DangerousGetHandle()},Attributes=attributeList};
   Win32.Need(IntPtr.Size==8&&Marshal.SizeOf<CaptureNative.Startup>()==104&&Marshal.SizeOf<CaptureNative.StartupEx>()==112,"Capture x64 startup layouts");
   commandMemory=Marshal.StringToHGlobalUni(command);
   int ok=CaptureNative.CreateProcess(exe,commandMemory,0,0,1,0x0008000C,0,cwd,ref startup,out var native);int lastError=Marshal.GetLastPInvokeError();receipt.Pid=native.Pid;
   if(native.Process!=0)process=new KernelHandle(native.Process);if(native.Thread!=0)thread=new KernelHandle(native.Thread);persist?.Invoke(receipt);
   if(ok==0)throw new System.ComponentModel.Win32Exception(lastError,"CreateProcess capture suspended/detached");
   process=process??throw new InvalidDataException("Capture process handle absent");thread=thread??throw new InvalidDataException("Capture thread handle absent");
   receipt.Identity=Win32.Identity(process,native.Pid);Win32.Need(receipt.Identity.CommandLine==command&&receipt.Identity.ParentPid==(uint)Environment.ProcessId,"Capture native full command/birth/direct parent");receipt.IdentityVerified=true;persist?.Invoke(receipt);
   job.Assign(process,receipt);persist?.Invoke(receipt);return new(process,thread,job,receipt);
  }
  catch(Exception failure){receipt.Error=failure.ToString();CleanupOwned(process,thread,receipt,false);try{persist?.Invoke(receipt);}catch(Exception secondary){receipt.PublicationErrors.Add(secondary.ToString());}throw new LaunchException(receipt,failure);}
  finally
  {
   if(commandMemory!=0)Marshal.FreeHGlobal(commandMemory);if(initialized)CaptureNative.DeleteProcThreadAttributeList(attributeList);if(attributeList!=0)Marshal.FreeHGlobal(attributeList);if(handleList!=0)Marshal.FreeHGlobal(handleList);
   input?.Dispose();output?.Dispose();error?.Dispose();
  }
 }
}
