using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace RustSharp.Cli;

/// <summary>Explicit private-console control fixture. Normal fmt calls never enter this path.</summary>
internal static partial class FormatterCancellationProbe
{
    private const string ConfigurationVariable = "RUSTSHARP_FMT_CANCELLATION_LEASE";
    private const string ProbeRoot = @"D:\GitHub\RustSharp\artifacts\p2-supervision\formatter-control-v18";

    internal static int Run(string[] args)
    {
        string? configuration = Environment.GetEnvironmentVariable(ConfigurationVariable);
        return string.IsNullOrEmpty(configuration)
            ? FormatterCommand.Run(args)
            : FormatterCommand.RunWithCancellationHandlerReady(args,
                token => PublishReadyAndWait(configuration, args, token));
    }

    private static void PublishReadyAndWait(string configuration, string[] args, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        void Guard()
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed >= TimeSpan.FromSeconds(8))
                throw new TimeoutException("fmt private control probe exceeded eight seconds.");
        }
        Guard();
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            IntPtr.Size != 8 || args.Length != 2 || args[0] != "fmt")
            throw new InvalidDataException("Private cancellation probe requires Windows x64 fmt directory mode.");
        string path = Path.GetFullPath(configuration);
        string? runtime = Path.GetDirectoryName(path);
        if (!Path.IsPathFullyQualified(configuration) || Path.GetFileName(path) != "cancellation-probe-lease.json" ||
            (runtime != Path.Combine(ProbeRoot, "no-signal-trial") && runtime != Path.Combine(ProbeRoot, "ctrl-c-trial")))
            throw new InvalidDataException("Private cancellation lease is outside the two exact owned directories.");
        DirectoryInfo? ancestor = new(runtime);
        for (int depth = 0; ancestor is not null && depth < 64; depth++)
        {
            Guard();
            if (!ancestor.Exists || (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Private cancellation probe refuses missing or linked ancestry.");
            ancestor = ancestor.Parent;
        }
        if (ancestor is not null || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            new FileInfo(path).Length > 16_384)
            throw new InvalidDataException("Private cancellation lease ancestry/file bound failed.");
        byte[] leaseBytes = File.ReadAllBytes(path);
        Guard();
        string leaseHash = Convert.ToHexString(SHA256.HashData(leaseBytes));
        using var document = JsonDocument.Parse(leaseBytes);
        JsonElement lease = document.RootElement;
        string nonce = lease.GetProperty("Nonce").GetString() ?? "";
        string mode = lease.GetProperty("Mode").GetString() ?? "";
        string source = lease.GetProperty("SourceDirectory").GetString() ?? "";
        string cli = lease.GetProperty("CliPath").GetString() ?? "";
        string cliHash = lease.GetProperty("CliSha256").GetString() ?? "";
        string candidate = lease.GetProperty("CandidateSha").GetString() ?? "";
        uint pid = lease.GetProperty("CliPid").GetUInt32();
        long birth = lease.GetProperty("CliBirth100ns").GetInt64();
        uint parent = lease.GetProperty("HelperPid").GetUInt32();
        long parentBirth = lease.GetProperty("HelperBirth100ns").GetInt64();
        if (lease.GetProperty("TaskId").GetString() != "P2-08.02.CTRL-v2" ||
            !Guid.TryParseExact(nonce, "N", out _) ||
            mode != (runtime.EndsWith("\\no-signal-trial", StringComparison.Ordinal) ? "resume-no-signal" : "resume-ctrl-c-probe") ||
            source != Path.Combine(runtime, "fixture-" + nonce) || Path.GetFullPath(args[1]) != source ||
            pid != (uint)Environment.ProcessId || parent == pid || parent == 0 ||
            candidate.Length != 40 || candidate == "aee97ccdc2509fb64693631979fa4e93c3a31578" ||
            cli != Path.Combine(ProbeRoot, "cli-bin", "rsc.dll") || cliHash.Length != 64 ||
            Environment.ProcessPath != @"C:\Program Files\dotnet\dotnet.exe" ||
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "rsc.dll")) != cli ||
            (File.GetAttributes(cli) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Private cancellation lease identity/mode/candidate/source binding differs.");
        if (new FileInfo(cli).Length > 16_777_216 ||
            !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cli))).Equals(cliHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Actual private candidate CLI bytes differ from the owned lease.");
        Guard();
        if (Marshal.SizeOf<BasicInfo>() != 48 || Marshal.OffsetOf<BasicInfo>(nameof(BasicInfo.Parent)).ToInt32() != 40)
            throw new InvalidDataException("Private native process layout differs.");
        using var self = Process.GetCurrentProcess();
        using var helper = Process.GetProcessById(checked((int)parent));
        int selfTimes = GetProcessTimes(self.SafeHandle, out long actualBirth, out long exit, out _, out _);
        int parentTimes = GetProcessTimes(helper.SafeHandle, out long actualParentBirth, out long parentExit, out _, out _);
        int status = QueryBasic(self.SafeHandle, 0, out BasicInfo basic, 48, out uint returned);
        if (selfTimes == 0 || parentTimes == 0 || actualBirth != birth || actualParentBirth != parentBirth ||
            exit != 0 || parentExit != 0 || parentBirth > birth || status != 0 || returned != 48 ||
            basic.Pid != pid || basic.Parent != parent)
            throw new InvalidDataException("Actual held native CLI/helper birth or direct parent differs.");
        var console = new uint[3];
        uint consoleCount = GetConsoleProcessList(console, 3);
        if (consoleCount != 2 || !console.AsSpan(0, 2).Contains(pid) || !console.AsSpan(0, 2).Contains(parent))
            throw new InvalidDataException("Actual private console must contain the exact CLI and helper pair.");
        Guard();
        string ready = Path.Combine(runtime, "cancellation-handler-ready.json");
        using (var output = new FileStream(ready, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteString("TaskId", "P2-08.02.CTRL-v2");
            writer.WriteString("Nonce", nonce);
            writer.WriteString("Mode", mode);
            writer.WriteNumber("CliPid", pid);
            writer.WriteNumber("CliBirth100ns", birth);
            writer.WriteNumber("HelperPid", parent);
            writer.WriteNumber("HelperBirth100ns", parentBirth);
            writer.WriteString("LeaseSha256", leaseHash);
            writer.WriteString("CliSha256", cliHash);
            writer.WriteBoolean("HandlerRegistered", true);
            writer.WriteBoolean("PendingKernelIoProven", false);
            writer.WriteEndObject();
            writer.Flush();
            output.Flush(flushToDisk: true);
        }
        Guard();
        if (mode == "resume-no-signal") return;
        // Real CTRL+C cancels the command token. This is a probe wait, not pending kernel I/O.
        for (int check = 0; check < 320 && clock.Elapsed < TimeSpan.FromSeconds(8); check++)
        {
            Guard();
            if (check % 40 == 0) Console.Error.WriteLine("rsc fmt: private cancellation handler ready.");
            token.WaitHandle.WaitOne(25);
        }
        Guard();
        throw new TimeoutException("Private cancellation probe did not receive real CTRL+C.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        internal nint Status, Peb, Affinity, Priority;
        internal nuint Pid, Parent;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetProcessTimes(SafeProcessHandle process, out long creation, out long exit,
        out long kernel, out long user);

    [LibraryImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static partial int QueryBasic(SafeProcessHandle process, int information, out BasicInfo basic,
        uint bytes, out uint returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetConsoleProcessList(
        [Out, MarshalUsing(CountElementName = nameof(count))] uint[] pids, uint count);
}
