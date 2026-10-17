using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Liuzhuan.Services.Audio;

// Minimal Windows ABI declarations. Keep GetProcessId's original HRESULT through
// NAudio's public IAudioSessionControl2; positive cross-process results are not S_OK.
internal static class RawCaptureSessions
{
    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr result);
    internal static Enumerator CreateEnumerator()
    {
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"); var iid = typeof(Enumerator).GUID;
        Check(CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref iid, out var pointer));
        // The Windows enumerator is shared. A unique untyped RCW avoids assigning
        // our coclass type to NAudio's RCW and breaking subsequent device refreshes.
        try { return (Enumerator)Marshal.GetUniqueObjectForIUnknown(pointer); }
        finally { Marshal.Release(pointer); }
    }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface Enumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out Device device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out Device device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface Device
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(uint mode, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out DeviceState state);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface Manager
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionId, uint flags, out IAudioSessionControl session);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionId, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out Sessions sessions);
        [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification callback);
        [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification callback);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface Sessions
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }
    internal static void Check(int result) { if (result != 0) Marshal.ThrowExceptionForHR(result < 0 ? result : unchecked((int)0x80004005)); }
    internal static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}
