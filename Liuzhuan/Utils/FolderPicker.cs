using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Liuzhuan.Utils;

/// <summary>Windows 原生文件夹选择器，使用 IFileDialog 的 FOS_PICKFOLDERS 模式。</summary>
internal static class FolderPicker
{
    public static string? Show(Window owner)
    {
        var dialog = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), true)!)!;
        IShellItem? item = null;
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | 0x20 | 0x40 | 0x800 | 0x8); // folders, filesystem, existing path, no cwd changes
            dialog.SetTitle("选择素材另存位置");
            dialog.SetOkButtonLabel("选择此文件夹");
            var result = dialog.Show(new WindowInteropHelper(owner).Handle);
            if (result == unchecked((int)0x800704C7)) return null;
            Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out item);
            item.GetDisplayName(0x80058000, out var path); // SIGDN_FILESYSPATH
            try { return Marshal.PtrToStringUni(path); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
            Marshal.ReleaseComObject(dialog);
        }
    }

    // COM vtable 顺序包含 IModalWindow.Show；以下保留到 GetResult 的全部槽位。
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, IntPtr specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint name, out IntPtr value);
    }
}
