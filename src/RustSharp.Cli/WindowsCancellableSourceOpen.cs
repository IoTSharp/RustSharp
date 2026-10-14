using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;


namespace RustSharp.Cli;

// Review-only fixture concept. Never integrated into the production CLI by this worker.

internal sealed partial class WindowsCancellableSourceOpen : IDisposable
{

    private static readonly List<WindowsCancellableSourceOpen> Retained = [];
    private static readonly object RetainedGate = new();
    private readonly object gate = new();
    private readonly Thread worker;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly string sourcePath;
    private readonly CancellationToken token;
    private readonly CancellationTokenSource ownedCancellation;
    private NativeThreadHandle? nativeThread;
    private SafeFileHandle? opened;
    private CancellationTokenRegistration registration;
    private bool entered, returned, completed, joined, disposed, transferred;
    private int cleanupCalls;
    public OpenEvidence Evidence { get; } = new();


    public sealed class OpenEvidence
    {

        public uint ThreadId { get; internal set; }
        public long ThreadBirth { get; internal set; }
        public bool TokenRegistered { get; internal set; }
        public bool NativeCreateEntered { get; internal set; }
        public bool NativeCreateReturned { get; internal set; }
        public bool ActualThreadPendingObserved { get; internal set; }
        public bool NativeJoinVerified { get; internal set; }
        public int? ActualCreateError { get; internal set; }
        public bool ActualCreateAborted { get; internal set; }
        public bool ActualMetadataAborted { get; internal set; }
        public bool FileIdentityMatched { get; internal set; }
        public bool RetainedOwnerRequiresOuterJob { get; internal set; }
        public bool? NativeThreadCloseSucceeded { get; internal set; }
        public bool CliPendingProven { get; }
        public bool CliCancellationProven { get; }
        public List<string> Events { get; } = [];
        public List<string> Errors { get; } = [];
    }

    private WindowsCancellableSourceOpen(string path, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        sourcePath = Path.GetFullPath(path);
        CheckAncestors(sourcePath, cancellationToken);
        ownedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        token = ownedCancellation.Token;
        worker = new Thread(Execute) { IsBackground = false, Name = "RustSharp-owned-sync-open" };
    }

    internal static FileStream OpenRead(string path, int bufferSize, CancellationToken cancellationToken)
    {
        var owner = new WindowsCancellableSourceOpen(path, cancellationToken);
        FileStream? stream = null;
        Exception? primary = null;
        try
        {
            owner.Start();
            if (!owner.JoinBounded(10000))
                throw new IOException("fmt native source-open resource failure: owned foreground thread did not join; owner retained.");
            if (owner.Evidence.Errors.Count != 0)
                throw new IOException("fmt native source-open failed: " + string.Join("; ", owner.Evidence.Errors));
            if ((owner.Evidence.ActualCreateAborted || owner.Evidence.ActualMetadataAborted) && cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            if (owner.Evidence.ActualCreateError is int error && error != 0)
                throw MapOpenError(path, error);
            cancellationToken.ThrowIfCancellationRequested();
            SafeFileHandle handle = owner.TakeJoinedHandle();
            try { stream = new FileStream(handle, FileAccess.Read, bufferSize, isAsync: true); }
            catch { handle.Dispose(); throw; }
        }
        catch (Exception error) { primary = error; }
        try { owner.Dispose(); }
        catch (Exception cleanup) { primary = new IOException("fmt native source-open cleanup failed.", primary is null ? cleanup : new AggregateException(primary, cleanup)); }
        FormatterNativeOpenProbe.Finished(path, owner.Evidence.ThreadId, owner.Evidence.ThreadBirth,
            owner.Evidence.ActualCreateError, owner.Evidence.NativeJoinVerified,
            owner.Evidence.NativeThreadCloseSucceeded is true);
        if (owner.Evidence.RetainedOwnerRequiresOuterJob || owner.Evidence.NativeThreadCloseSucceeded is not true)
            primary = new IOException("fmt native source-open resource failure: native owner was not fully reclaimed.", primary);
        if (primary is not null)
        {
            stream?.Dispose();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        }
        return stream ?? throw new IOException("fmt native source-open returned no verified stream.");
    }

    private static Exception MapOpenError(string path, int error) => error switch
    {
        2 => new FileNotFoundException(new Win32Exception(error).Message, path),
        3 => new DirectoryNotFoundException(new Win32Exception(error).Message),
        5 => new UnauthorizedAccessException(new Win32Exception(error).Message),
        206 => new PathTooLongException(new Win32Exception(error).Message),
        _ => new IOException(new Win32Exception(error).Message, unchecked((int)(0x80070000u | (uint)error)))
    };

    private static void CheckAncestors(string path, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        DirectoryInfo? parent = Directory.GetParent(path);
        for (int depth = 0; parent is not null && depth < 128 && clock.ElapsedMilliseconds < 2000; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("fmt refuses a linked source ancestor.");
            parent = parent.Parent;
        }
        if (parent is not null) throw new IOException("fmt source ancestry bound exceeded.");
    }
    public void Start() { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); if (worker.ThreadState != System.Threading.ThreadState.Unstarted) throw new InvalidOperationException("Single start only"); worker.Start(); } }

    public bool WaitReady(int milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        return ready.Wait(milliseconds, cancellationToken);
    }

    public bool ObserveActualPendingCreate()
    {
        lock (gate)
        {
            if (!entered || returned || nativeThread is null) return false;
            RecheckThread(nativeThread);
            if (GetThreadIOPendingFlag(nativeThread, out int pending) == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "GetThreadIOPendingFlag");
            Evidence.ActualThreadPendingObserved |= pending != 0;
            return pending != 0; // Actual thread I/O flag; external ordinary-file/oplock witness is still required.
        }
    }

    public bool JoinBounded(int maximumMilliseconds)
    {
        if (maximumMilliseconds is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maximumMilliseconds));
        var clock = Stopwatch.StartNew();
        for (int check = 0; check < 400 && clock.ElapsedMilliseconds < maximumMilliseconds; check++)
        {
            // Retry after real token cancellation prevents cancel-before-CreateFile ERROR_NOT_FOUND from being lost.
            if (token.IsCancellationRequested) RequestCancel();
            if (worker.Join(Math.Min(50, Math.Max(1, maximumMilliseconds - checked((int)clock.ElapsedMilliseconds)))))
            {
                lock (gate)
                {
                    if (nativeThread is null || WaitForSingleObject(nativeThread, 0) != 0)
                    { joined = true; Evidence.Errors.Add("Managed exit but native bootstrap/exit evidence unavailable"); return true; }
                    joined = true; Evidence.NativeJoinVerified = true; return true;
                }
            }
            if (check % 20 == 0) Console.Error.WriteLine("sync-open joined-wait progress=" + check);
        }
        lock (RetainedGate) if (!Retained.Contains(this)) Retained.Add(this);
        Evidence.RetainedOwnerRequiresOuterJob = true;
        return false;
    }

    public SafeFileHandle TakeJoinedHandle()
    {
        lock (gate)
        {
            if (!joined || !returned || opened is null || opened.IsInvalid || !Evidence.FileIdentityMatched || transferred)
                throw new InvalidOperationException("Verified joined successful open required");
            token.ThrowIfCancellationRequested();
            transferred = true; return opened;
        }
    }

    private void Execute()
    {
        try
        {
            lock (gate)
            {
                uint tid = GetCurrentThreadId();
                nativeThread = OpenThread(0x00100041, 0, tid); // TERMINATE only for CancelSynchronousIo, QUERY_INFORMATION, SYNCHRONIZE.
                if (nativeThread.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "OpenThread");
                if (GetThreadTimes(nativeThread, out long birth, out long exit, out _, out _) == 0 || exit != 0)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "GetThreadTimes bootstrap");
                Evidence.ThreadId = tid; Evidence.ThreadBirth = birth; RecheckThread(nativeThread);
            }
            registration = token.Register(RequestCancel); Evidence.TokenRegistered = true; ready.Set();
            FormatterNativeOpenProbe.Ready(sourcePath, Evidence.ThreadId, Evidence.ThreadBirth, Evidence.TokenRegistered);
            if (token.IsCancellationRequested) { Evidence.Events.Add("Cancelled before native CreateFile; no native abort proof"); return; }
            lock (gate) { entered = true; Evidence.NativeCreateEntered = true; }
            SafeFileHandle result = CreateFileW(sourcePath, 0x80000000, 1, 0, 3, 0x48000080, 0);
            int error = Marshal.GetLastPInvokeError();
            lock (gate)
            {
                opened = result; returned = true; Evidence.NativeCreateReturned = true;
                Evidence.ActualCreateError = result.IsInvalid ? error : 0;
                Evidence.ActualCreateAborted = result.IsInvalid && error == 995;
                
            }
            if (!result.IsInvalid)
            {
                bool matched = MatchLease(result);
                lock (gate) Evidence.FileIdentityMatched = matched;
            }
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 995 && token.IsCancellationRequested)
        { lock (gate) Evidence.ActualMetadataAborted = true; }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { /* Safe cancellation during post-open validation; successful native open remains separate evidence. */ }
        catch (Exception error) { lock (gate) Evidence.Errors.Add(error.ToString()); }
        finally { lock (gate) completed = true; ready.Set(); }
    }

    private void RequestCancel()
    {
        try
        {
        lock (gate)
        {
            if (nativeThread is null || completed || joined) return;
            RecheckThread(nativeThread);
            int result = CancelSynchronousIo(nativeThread); int error = Marshal.GetLastPInvokeError();
            Evidence.Events.Add("CancelSynchronousIo return=" + result + " lastError=" + error);
            if (result == 0 && error != 1168) Evidence.Errors.Add("CancelSynchronousIo error=" + error);
        }
        }
        catch (Exception error)
        {
            lock (gate) Evidence.Errors.Add("cancel callback: " + error);
        }
    }

    private void RecheckThread(NativeThreadHandle handle)
    {
        if (GetThreadId(handle) != Evidence.ThreadId || GetProcessIdOfThread(handle) != checked((uint)Environment.ProcessId) ||
            GetThreadTimes(handle, out long birth, out _, out _, out _) == 0 || birth != Evidence.ThreadBirth)
            throw new InvalidDataException("Retained native thread ID/birth/process identity changed");
    }

    private bool MatchLease(SafeFileHandle handle)
    {
        nint data = Marshal.AllocHGlobal(65536);
        try
        {
            if (GetFileInformationByHandleEx(handle, 18, data, 24) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "source native file identity");
            ulong volume = unchecked((ulong)Marshal.ReadInt64(data));
            ulong low = unchecked((ulong)Marshal.ReadInt64(data, 8)), high = unchecked((ulong)Marshal.ReadInt64(data, 16));
            if (volume == 0 || (low == 0 && high == 0)) throw new IOException("fmt source native identity unavailable.");
            if (GetFileInformationByHandleEx(handle, 0, data, 40) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "source native attributes");
            if (Marshal.ReadInt64(data) <= 0 || (Marshal.ReadInt32(data, 32) & 0x410) != 0)
                throw new IOException("fmt refuses a linked or non-file source.");
            string normalized = ReadNativePath(handle, data, 0);
            bool samePath = normalized.Equals(sourcePath, StringComparison.OrdinalIgnoreCase);
            if (!samePath)
            {
                // Native opened-name preserves a legal 8.3 alias without dropping native file ID or link checks.
                string openedName = ReadNativePath(handle, data, 8);
                samePath = openedName.Equals(sourcePath, StringComparison.OrdinalIgnoreCase);
            }
            if (!samePath)
            {
                // Resolve only on this owned cancellable worker; no main-thread blocking name lookup.
                uint longChars = GetLongPathNameW(sourcePath, data, 32768);
                if (longChars is 0 or >= 32768)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "source long-name identity");
                string longPath = Marshal.PtrToStringUni(data, checked((int)longChars)) ?? "";
                samePath = Path.GetFullPath(longPath).Equals(normalized, StringComparison.OrdinalIgnoreCase);
            }
            if (!samePath) throw new IOException("fmt source native path identity differs from the requested path.");
            CheckAncestors(sourcePath, token);
            return true;
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    private static string ReadNativePath(SafeFileHandle handle, nint data, uint flags)
    {
        uint chars = GetFinalPathNameByHandleW(handle, data, 32768, flags);
        if (chars is 0 or >= 32768) throw new Win32Exception(Marshal.GetLastPInvokeError(), "source native path identity");
        string native = Marshal.PtrToStringUni(data, checked((int)chars)) ?? "";
        if (!native.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new IOException("fmt source native path lacks its expected volume prefix.");
        return Path.GetFullPath(native.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + native[8..] : native[4..]);
    }

    public void Dispose()
    {
        if (disposed) return;
        if (cleanupCalls >= 2) { Evidence.Errors.Add("Two cleanup attempts exhausted; owner retained"); return; }
        cleanupCalls++;
        ownedCancellation.Cancel();
        if (!joined && worker.ThreadState != System.Threading.ThreadState.Unstarted && !JoinBounded(2500)) return;
        if (!joined && worker.ThreadState == System.Threading.ThreadState.Unstarted) joined = true;
        registration.Dispose(); // No callback may use a closed thread handle.
        if (!transferred) opened?.Dispose();
        nativeThread?.Dispose(); Evidence.NativeThreadCloseSucceeded = nativeThread?.CloseSucceeded;
        ready.Dispose(); ownedCancellation.Dispose(); disposed = true;
        GC.SuppressFinalize(this);
        lock (RetainedGate) Retained.Remove(this);
    }

    private sealed class NativeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeThreadHandle() : base(true) { }
        internal bool? CloseSucceeded { get; private set; }
        protected override bool ReleaseHandle() { int result = CloseHandle(handle); CloseSucceeded = result != 0; return result != 0; }
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial NativeThreadHandle OpenThread(uint access, int inherit, uint tid);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial uint GetThreadId(NativeThreadHandle thread);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial uint GetProcessIdOfThread(NativeThreadHandle thread);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int GetThreadTimes(NativeThreadHandle thread, out long creation, out long exit, out long kernel, out long user);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int GetThreadIOPendingFlag(NativeThreadHandle thread, out int pending);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int CancelSynchronousIo(NativeThreadHandle thread);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial uint WaitForSingleObject(NativeThreadHandle thread, uint milliseconds);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] private static partial SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int GetFileInformationByHandleEx(SafeFileHandle file, int kind, nint buffer, uint bytes);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, nint buffer, uint characters, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] private static partial uint GetLongPathNameW(string path, nint buffer, uint characters);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial int CloseHandle(nint handle);
}
