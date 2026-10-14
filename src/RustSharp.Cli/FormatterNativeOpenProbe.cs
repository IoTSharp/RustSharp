using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
namespace RustSharp.Cli;
internal static partial class FormatterNativeOpenProbe
{
    private static string? runtime, nonce, source;
    private static long processBirth;
    private static bool handler;
    internal static bool Enabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RUSTSHARP_FMT_OPEN_PROBE_LEASE"));
    internal static void HandlerReady(string[] args)
    {
        string leasePath=Path.GetFullPath(Environment.GetEnvironmentVariable("RUSTSHARP_FMT_OPEN_PROBE_LEASE")??throw new InvalidDataException("Probe lease absent"));
        const string root=@"D:\GitHub\RustSharp\artifacts\p2-supervision\formatter-fresh-round37\runtime";
        string dir=Path.GetDirectoryName(leasePath)!;
        if(Path.GetFileName(leasePath)!="cli-probe-lease.json"||Path.GetDirectoryName(dir)!=root||!Guid.TryParseExact(Path.GetFileName(dir),"N",out _)||args.Length!=2||args[0]!="fmt")
            throw new InvalidDataException("Exact actual CLI probe scope required");
        var clock=Stopwatch.StartNew();DirectoryInfo? parent=new(dir);
        for(int depth=0;parent is not null&&depth<128&&clock.ElapsedMilliseconds<2000;depth++)
        {if(!parent.Exists||(parent.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked probe ancestry");parent=parent.Parent;}
        if(parent is not null||new FileInfo(leasePath).Length>16384||(File.GetAttributes(leasePath)&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)throw new InvalidDataException("Probe lease bound");
        using var doc=JsonDocument.Parse(File.ReadAllBytes(leasePath));var lease=doc.RootElement;
        nonce=lease.GetProperty("nonce").GetString();source=lease.GetProperty("source").GetString();
        string cli=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"rsc.dll"));
        if(nonce!=Path.GetFileName(dir)||source!=Path.Combine(dir,"source.rs")||Path.GetFullPath(args[1])!=source||cli!=lease.GetProperty("cliPath").GetString()||cli!=Path.GetFullPath(Path.Combine(root,"..","cli-bin","rsc.dll"))||new FileInfo(cli).Length>16777216)
            throw new InvalidDataException("Probe source/candidate differs");
        using(var bytes=File.OpenRead(cli))
            if(Convert.ToHexString(SHA256.HashData(bytes))!=lease.GetProperty("cliSha256").GetString())throw new InvalidDataException("Actual CLI hash differs");
        string pdb=Path.ChangeExtension(cli,".pdb");
        using(var pdbBytes=File.OpenRead(pdb))
            if(Convert.ToHexString(SHA256.HashData(pdbBytes))!=lease.GetProperty("cliPdbSha256").GetString())throw new InvalidDataException("Actual CLI PDB hash differs");
        if(typeof(FormatterNativeOpenProbe).Assembly.ManifestModule.ModuleVersionId.ToString("D")!=lease.GetProperty("cliMvid").GetString())throw new InvalidDataException("Actual loaded CLI MVID differs");
        using var self=Process.GetCurrentProcess();uint driverPid=lease.GetProperty("driverPid").GetUInt32();using var driver=Process.GetProcessById(checked((int)driverPid));
        if(GetProcessTimes(self.SafeHandle,out processBirth,out long exited,out _,out _)==0||exited!=0||GetProcessTimes(driver.SafeHandle,out long driverBirth,out long driverExit,out _,out _)==0||driverExit!=0||driverBirth.ToString(CultureInfo.InvariantCulture)!=lease.GetProperty("driverBirth").GetString()||QueryBasic(self.SafeHandle,0,out Basic basic,48,out uint size)!=0||size!=48||basic.Parent!=driverPid)
            throw new InvalidDataException("Actual CLI/driver native parent or birth differs");
        runtime=dir;handler=true;
    }
    internal static void Ready(string path,uint tid,long birth,bool registered)
    {
        if(!Enabled)return;
        if(!handler||runtime is null||source!=Path.GetFullPath(path)||!registered||GetCurrentThreadId()!=tid)throw new InvalidDataException("Actual handler/token/native thread registration absent");
        Write("open-ready.json",tid,birth,registered,null,false,false);
    }
    internal static void Finished(string path,uint tid,long birth,int? error,bool joined,bool closed)
    {
        if(!Enabled)return;
        if(!handler||runtime is null||source!=Path.GetFullPath(path))throw new InvalidDataException("Actual completed source differs");
        Write("open-finished.json",tid,birth,true,error,joined,closed);
    }
    private static void Write(string name,uint tid,long birth,bool registered,int? error,bool joined,bool closed)
    {
        if (runtime is null || nonce is null || !Guid.TryParseExact(nonce,"N",out _) ||
            name is not ("open-ready.json" or "open-finished.json") ||
            (File.GetAttributes(runtime)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Exact live nonce publication owner required");
        string finalPath=Path.Combine(runtime,name);
        string temporary=Path.Combine(runtime,"."+nonce+"."+Environment.ProcessId.ToString(CultureInfo.InvariantCulture)+"."+name+".tmp");
        // This exact CreateNew temporary belongs only to this nonce/process publication.
        // Never overwrite a prior receipt; failed temporary files remain evidence.
        using (var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            using var json=new Utf8JsonWriter(output);
            json.WriteStartObject();json.WriteString("nonce",nonce);json.WriteNumber("pid",Environment.ProcessId);json.WriteString("processBirth",processBirth.ToString(CultureInfo.InvariantCulture));json.WriteString("source",source);
            json.WriteNumber("threadId",tid);json.WriteString("threadBirth",birth.ToString(CultureInfo.InvariantCulture));json.WriteBoolean("tokenRegistered",registered);json.WriteBoolean("handlerRegistered",handler);
            if(error is int value)json.WriteNumber("createError",value);else json.WriteNull("createError");
            json.WriteBoolean("nativeExitAndJoin",joined);json.WriteBoolean("threadHandleClosed",closed);json.WriteEndObject();json.Flush();output.Flush(flushToDisk:true);
        }
        // Both writer and stream are closed before the same-directory no-overwrite rename.
        if((File.GetAttributes(runtime)&FileAttributes.ReparsePoint)!=0)
            throw new IOException("Probe publication ancestry changed; temporary retained");
        File.Move(temporary,finalPath,overwrite:false);
    }
    [StructLayout(LayoutKind.Sequential)]private struct Basic{internal nint Status,Peb,Affinity,Priority;internal nuint Pid,Parent;}
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll",SetLastError=true)]private static partial int GetProcessTimes(SafeProcessHandle process,out long birth,out long exit,out long kernel,out long user);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]private static partial uint GetCurrentThreadId();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("ntdll.dll",EntryPoint="NtQueryInformationProcess")]private static partial int QueryBasic(SafeProcessHandle process,int kind,out Basic basic,uint bytes,out uint returned);
}