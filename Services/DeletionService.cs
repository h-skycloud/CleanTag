using System.Runtime.InteropServices;
using CleanTag.Core;

namespace CleanTag.Services;

public static class DeletionService
{
    // Called only after the app's separate all-drives confirmation. Never invoked by tests.
    public static Task EmptyRecycleBinAsync(nint owner) => Task.Run(() =>
    {
        Marshal.ThrowExceptionForHR(SHEmptyRecycleBinW(owner, null, 0x0001 | 0x0002 | 0x0004));
    });
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHEmptyRecycleBinW(nint owner, string? root, uint flags);
    public static Task DeleteAsync(string root, FileEntry entry, bool permanent, nint owner)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                FileSafety.Validate(root, entry);
                if (permanent) File.Delete(entry.FullPath);
                else Recycle(entry.FullPath, owner);
                if (File.Exists(entry.FullPath)) throw new IOException("Dosya silinemedi; kart yerinde bırakıldı.");
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "CleanTag file operation" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    private static void Recycle(string path, nint owner)
    {
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
            throw new IOException("Bu konumda güvenli Çöp Kutusu işlemi desteklenmiyor. Dosya silinmedi.");
        IFileOperation? operation = null; IShellItem? item = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!;
            operation = (IFileOperation)Activator.CreateInstance(type)!;
            // Recycle explicitly. No connected HTML folders, no recursive delete, no permanent fallback.
            const uint flags = 0x00080000 | 0x00100000 | 0x20000000 | 0x0004 | 0x0010 | 0x0400 | 0x2000 | 0x1000 | 0x4000;
            operation.SetOperationFlags(flags); operation.SetOwnerWindow(owner);
            Guid iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref iid, out item));
            var sink = new RecycleSink();
            operation.DeleteItem(item, sink); operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            if (aborted || sink.Blocked) throw new IOException("Çöp Kutusu işlemi tamamlanamadı. Kalıcı silmeye geçilmedi.");
            if (!sink.Completed) throw new IOException("Windows silme işlemini doğrulamadı.");
            Marshal.ThrowExceptionForHR(sink.Result);
            GC.KeepAlive(sink);
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
            if (operation != null) Marshal.ReleaseComObject(operation);
        }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
}

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    void BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, out nint ppv);
    void GetParent(out IShellItem parent);
    void GetDisplayName(uint name, out nint value);
    void GetAttributes(uint mask, out uint attributes);
    void Compare(IShellItem other, uint hint, out int order);
}

[ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    void Advise(nint sink, out uint cookie);
    void Unadvise(uint cookie);
    void SetOperationFlags(uint flags);
    void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
    void SetProgressDialog(nint dialog);
    void SetProperties(nint properties);
    void SetOwnerWindow(nint hwnd);
    void ApplyPropertiesToItem(IShellItem item);
    void ApplyPropertiesToItems(nint items);
    void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
    void RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string name);
    void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, nint sink);
    void MoveItems(nint items, IShellItem destination);
    void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, nint sink);
    void CopyItems(nint items, IShellItem destination);
    void DeleteItem(IShellItem item, IFileOperationProgressSink sink);
    void DeleteItems(nint items);
    void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, nint sink);
    void PerformOperations();
    void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}

[ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int result);
    [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
    [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
    [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
    [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
    [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
    [PreserveSig] int PreNewItem(uint flags, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostNewItem(uint flags, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IShellItem? created);
    [PreserveSig] int UpdateProgress(uint total, uint done);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RecycleSink : IFileOperationProgressSink
{
    public bool Completed { get; private set; }
    public bool Blocked { get; private set; }
    public int Result { get; private set; }
    public int PreDeleteItem(uint flags, IShellItem item)
    {
        if ((flags & 0x80) != 0) return 0;
        Blocked = true; return unchecked((int)0x80004004);
    }
    public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created) { Completed = true; Result = result; return 0; }
    public int StartOperations() => 0;
    public int FinishOperations(int result) => 0;
    public int PreRenameItem(uint f, IShellItem i, string n) => 0;
    public int PostRenameItem(uint f, IShellItem i, string n, int r, IShellItem? c) => 0;
    public int PreMoveItem(uint f, IShellItem i, IShellItem d, string n) => 0;
    public int PostMoveItem(uint f, IShellItem i, IShellItem d, string n, int r, IShellItem? c) => 0;
    public int PreCopyItem(uint f, IShellItem i, IShellItem d, string n) => 0;
    public int PostCopyItem(uint f, IShellItem i, IShellItem d, string n, int r, IShellItem? c) => 0;
    public int PreNewItem(uint f, IShellItem d, string n) => 0;
    public int PostNewItem(uint f, IShellItem d, string n, string t, uint a, int r, IShellItem? c) => 0;
    public int UpdateProgress(uint t, uint d) => 0;
    public int ResetTimer() => 0;
    public int PauseTimer() => 0;
    public int ResumeTimer() => 0;
}
