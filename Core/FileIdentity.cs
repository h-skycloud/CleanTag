using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CleanTag.Core;

public static class FileIdentity
{
    // File IDs are an observation, not a global content identity. Hard links retain separate paths.
    public static string? TryRead(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            return GetFileInformationByHandleEx(handle, 18, out var info, 24)
                ? $"{info.Volume:X16}:{info.High:X16}{info.Low:X16}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong Volume, Low, High; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdInfo info, uint size);
}
