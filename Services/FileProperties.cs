using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CleanTag.Services;

public static class FileProperties
{
    public static void Show(string path, nint owner)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Öğe artık bu konumda bulunmuyor.");
        var info = new ShellExecuteInfo { Size = (uint)Marshal.SizeOf<ShellExecuteInfo>(), Mask = 0x0000000C | 0x00000400, Owner = owner, Verb = "properties", File = path, Show = 5 };
        if (!ShellExecuteExW(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public uint Size, Mask;
        public nint Owner;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb, File, Parameters, Directory;
        public int Show;
        public nint Instance, IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public nint ClassKey;
        public uint HotKey;
        public nint Icon, Process;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);
}
