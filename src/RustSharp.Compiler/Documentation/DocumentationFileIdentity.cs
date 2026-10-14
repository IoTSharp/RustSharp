using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RustSharp.Compiler.Documentation;

// statx layout and open/AT constants reconciled against actual Ubuntu headers; native production behavior remains subject to full regression.
// An O_PATH descriptor pins the object, so unlink/recreate cannot recycle its inode during publication.
internal sealed partial class DocumentationFileIdentity : IDisposable
{
    private readonly DateTime portableBirth;
    private readonly SafeFileHandle? handle;
    private readonly LinuxSnapshot snapshot;
    private DocumentationFileIdentity(DateTime birth) => portableBirth = birth;
    private DocumentationFileIdentity(SafeFileHandle descriptor, LinuxSnapshot value) { handle = descriptor; snapshot = value; }

    internal static DocumentationFileIdentity Capture(string path)
    {
        if (!OperatingSystem.IsLinux()) return new(File.GetCreationTimeUtc(path));
        SafeFileHandle? owned = null;
        try
        {
            // Linux UAPI: O_PATH | O_NOFOLLOW | O_CLOEXEC. Never follow the final symlink.
            int descriptor = Open(path, 0x200000 | 0x20000 | 0x80000);
            if (descriptor < 0) throw NativeError("open identity");
            owned = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            LinuxSnapshot byHandle = Read(descriptor, "", 0x1000 | 0x100);
            LinuxSnapshot byPath = Read(-100, path, 0x100 | 0x800);
            if (byHandle != byPath) throw new IOException("Publication object changed during identity capture.");
            var result = new DocumentationFileIdentity(owned, byHandle);
            owned = null;
            return result;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        { throw new IOException("Linux stable filesystem identity is unavailable; publication was retained.", error); }
        finally { owned?.Dispose(); }
    }

    internal bool Matches(string path)
    {
        try
        {
            if (handle is null) return File.GetCreationTimeUtc(path) == portableBirth;
            if (handle.IsClosed || handle.IsInvalid) return false;
            bool added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                return Read(checked((int)handle.DangerousGetHandle()), "", 0x1000 | 0x100) == snapshot
                    && Read(-100, path, 0x100 | 0x800) == snapshot;
            }
            finally { if (added) handle.DangerousRelease(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException) { return false; }
    }

    public void Dispose() => handle?.Dispose();

    private static LinuxSnapshot Read(int directory, string path, int flags)
    {
        const uint required = 0x101; // STATX_TYPE | STATX_INO; device is always returned.
        if (Statx(directory, path, flags, 0xFFF, out StatxBuffer data) != 0) throw NativeError("statx identity");
        ushort type = (ushort)(data.Mode & 0xF000);
        if ((data.Mask & required) != required || type is not (0x4000 or 0x8000)) throw new IOException("Unknown or reparse publication object.");
        bool hasBirth = (data.Mask & 0x800) != 0;
        // Ignore ctime/mtime: legitimate owned writes change them. Retain available immutable btime.
        return new(data.DeviceMajor, data.DeviceMinor, data.Inode, type, hasBirth, hasBirth ? data.BirthSeconds : 0, hasBirth ? data.BirthNanoseconds : 0);
    }

    private static IOException NativeError(string operation)
    {
        int error = Marshal.GetLastPInvokeError();
        return new IOException(operation + " failed: " + new Win32Exception(error).Message, new Win32Exception(error));
    }

    private readonly record struct LinuxSnapshot(uint DeviceMajor, uint DeviceMinor, ulong Inode, ushort Type, bool HasBirth, long BirthSeconds, uint BirthNanoseconds);

    // Actual Ubuntu /usr/include/linux/stat.h SHA C20889137B96F2A0A3FF3A250842FE226426CB67DCE84A2354B50CAB2AB348E2: 0x100 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(80)] internal long BirthSeconds;
        [FieldOffset(88)] internal uint BirthNanoseconds;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(int directory, string path, int flags, uint mask, out StatxBuffer buffer);
}
// One method scope owns every successful capture, including objects not yet added to the publisher inventory.
// The using declaration disposes after publisher finally/retained-path calculation on every exit path.
internal sealed class DocumentationIdentityScope : IDisposable
{
    private readonly List<DocumentationFileIdentity> identities = [];
    internal DocumentationFileIdentity Capture(string path)
    {
        var identity = DocumentationFileIdentity.Capture(path);
        try { identities.Add(identity); return identity; }
        catch { identity.Dispose(); throw; }
    }
    public void Dispose()
    {
        for (int index = identities.Count - 1; index >= 0; index--) identities[index].Dispose();
        identities.Clear();
    }
}