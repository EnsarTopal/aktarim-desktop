using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Aktarim.Core.Tests")]

namespace Aktarim.Core;

// Denying write and delete sharing protects each checked directory until the operation ends.
internal sealed class SafeDirectories : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];

    public static SafeDirectories Open(string path, bool create = false, string? privateFrom = null)
    {
        var guard = new SafeDirectories();
        try
        {
            path = Path.GetFullPath(path);
            var volume = Path.GetPathRoot(path) ?? throw new IOException("Invalid directory path.");
            string current = volume;
            guard.Lock(current);
            foreach (string part in path[volume.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (create && !Directory.Exists(current)) Directory.CreateDirectory(current);
                guard.Lock(current);
                if (privateFrom != null && (current.Equals(privateFrom, StringComparison.OrdinalIgnoreCase) || current.StartsWith(privateFrom + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    if (OperatingSystem.IsWindows()) Restrict(current);
                    else if (!OperatingSystem.IsBrowser()) File.SetUnixFileMode(current, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            return guard;
        }
        catch { guard.Dispose(); throw; }
    }

    private void Lock(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Directory links are not allowed.");
            return;
        }
        var handle = CreateFile(path, 0x80, 0x1, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("Cannot safely open directory.", new Win32Exception(error)); }
        handles.Add(handle);
        if (!GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<AttributeTag>()))
            throw new IOException("Cannot verify directory.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0)
            throw new IOException("Directory links are not allowed.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Restrict(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Current user is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); }

    public static FileStream CreateStaged(string path)
    {
        if (!OperatingSystem.IsWindows()) return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.WriteThrough);
        var handle = CreateFile(path, 0xC0010000, 0, IntPtr.Zero, 1, 0x80000080, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("Cannot safely create destination file.", new Win32Exception(error)); }
        return new FileStream(handle, FileAccess.ReadWrite, 65536, isAsync: false);
    }

    public static void Publish(FileStream file, string staged, string target)
    {
        if (!OperatingSystem.IsWindows()) { File.Move(staged, target, false); return; }
        byte[] name = System.Text.Encoding.Unicode.GetBytes(target);
        int nameOffset = (int)Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.FileName));
        int length = checked(nameOffset + name.Length);
        IntPtr info = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(new byte[length], 0, info, length);
            Marshal.WriteInt32(info, (int)Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.FileNameLength)), name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(info, nameOffset), name.Length);
            if (!SetFileInformationByHandle(file.SafeFileHandle, 3, info, (uint)length))
                throw new IOException("Cannot safely publish destination file.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes; public uint Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct RenameInfo { public byte Replace; public IntPtr RootDirectory; public uint FileNameLength; public ushort FileName; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, IntPtr info, uint size);
}
