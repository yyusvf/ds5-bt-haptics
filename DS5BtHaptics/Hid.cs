using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DS5BtHaptics;

/// <summary>Minimal Win32 HID access (hid.dll / cfgmgr32), no extra drivers needed.</summary>
internal static class Native
{
    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool HidD_GetProductString(SafeFileHandle h, char[] buf, int len);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int reportType, byte[] caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetButtonCaps(int reportType, byte[] caps, ref ushort len, IntPtr pp);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List_SizeW(out int len, ref Guid cls, string? devId, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_ListW(ref Guid cls, string? devId, char[] buf, int len, int flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, out int written, IntPtr ovl);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, out int read, IntPtr ovl);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);

    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint p);

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    public const int HidP_Output = 1;
    public const int CapsStructSize = 72; // sizeof(HIDP_VALUE_CAPS) == sizeof(HIDP_BUTTON_CAPS)
    public const int CapsReportIdOffset = 2;
}

internal sealed class DualSenseDevice : IDisposable
{
    public const ushort SonyVid = 0x054C;
    public static readonly ushort[] DualSensePids = { 0x0CE6, 0x0DF2 }; // DualSense, DualSense Edge

    public string Path { get; }
    public ushort Pid { get; }
    public string Product { get; }
    public Native.HIDP_CAPS Caps { get; }
    public SortedSet<byte> OutputReportIds { get; }
    /// <summary>Over Bluetooth the full input report 0x31 is 78 bytes; over USB report 0x01 is 64 bytes.</summary>
    public bool IsBluetooth => Caps.InputReportByteLength >= 78 || OutputReportIds.Contains(0x31);

    readonly SafeFileHandle _handle;
    readonly object _writeLock = new();
    SafeFileHandle? _readHandle; // separate handle: a blocking ReadFile would otherwise serialize our writes

    DualSenseDevice(string path, SafeFileHandle h, ushort pid, string product, Native.HIDP_CAPS caps, SortedSet<byte> outIds)
    {
        Path = path; _handle = h; Pid = pid; Product = product; Caps = caps; OutputReportIds = outIds;
    }

    public static List<DualSenseDevice> Enumerate()
    {
        var result = new List<DualSenseDevice>();
        foreach (var path in HidPaths())
        {
            var h = Native.CreateFileW(path, 0xC0000000 /*R|W*/, 3 /*share R|W*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            var attr = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
            if (!Native.HidD_GetAttributes(h, ref attr) || attr.VendorID != SonyVid || !DualSensePids.Contains(attr.ProductID))
            {
                h.Dispose();
                continue;
            }
            if (!Native.HidD_GetPreparsedData(h, out var pp)) { h.Dispose(); continue; }
            Native.HidP_GetCaps(pp, out var caps);
            var ids = new SortedSet<byte>();
            CollectReportIds(pp, caps.NumberOutputValueCaps, Native.HidP_GetValueCaps, ids);
            CollectReportIds(pp, caps.NumberOutputButtonCaps, Native.HidP_GetButtonCaps, ids);
            Native.HidD_FreePreparsedData(pp);

            var name = new char[128];
            var product = Native.HidD_GetProductString(h, name, name.Length * 2) ? new string(name).TrimEnd('\0') : "?";
            result.Add(new DualSenseDevice(path, h, attr.ProductID, product, caps, ids));
        }
        return result;
    }

    delegate int GetCapsFn(int type, byte[] caps, ref ushort len, IntPtr pp);

    static void CollectReportIds(IntPtr pp, ushort count, GetCapsFn fn, SortedSet<byte> ids)
    {
        if (count == 0) return;
        var buf = new byte[count * Native.CapsStructSize];
        ushort n = count;
        if (fn(Native.HidP_Output, buf, ref n, pp) != 0x00110000 /*HIDP_STATUS_SUCCESS*/) return;
        for (var i = 0; i < n; i++) ids.Add(buf[i * Native.CapsStructSize + Native.CapsReportIdOffset]);
    }

    static IEnumerable<string> HidPaths()
    {
        Native.HidD_GetHidGuid(out var guid);
        if (Native.CM_Get_Device_Interface_List_SizeW(out var len, ref guid, null, 0) != 0 || len <= 1) yield break;
        var buf = new char[len];
        if (Native.CM_Get_Device_Interface_ListW(ref guid, null, buf, len, 0) != 0) yield break;
        foreach (var p in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries)) yield return p;
    }

    public byte[]? GetFeature(byte reportId)
    {
        var buf = new byte[Math.Max((int)Caps.FeatureReportByteLength, 2)];
        buf[0] = reportId;
        return Native.HidD_GetFeature(_handle, buf, buf.Length) ? buf : null;
    }

    /// <summary>
    /// Writes a complete BT output report (report id first, CRC already filled in).
    /// Windows requires the buffer to be exactly OutputReportByteLength long, so shorter
    /// reports are zero-padded behind their CRC.
    /// </summary>
    public void Write(ReadOnlySpan<byte> report)
    {
        var len = Math.Max((int)Caps.OutputReportByteLength, report.Length);
        var buf = new byte[len];
        report.CopyTo(buf);
        bool ok;
        lock (_writeLock) ok = Native.WriteFile(_handle, buf, buf.Length, out _, IntPtr.Zero);
        if (!ok)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteFile report 0x{report[0]:X2} ({report.Length} B) failed");
    }

    public int LastReadError { get; private set; }

    /// <summary>Blocking read of the next input report (report id first). Null when the device is gone.</summary>
    public byte[]? ReadInput()
    {
        _readHandle ??= Native.CreateFileW(Path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (_readHandle.IsInvalid) return null;
        var buf = new byte[Caps.InputReportByteLength];
        if (Native.ReadFile(_readHandle, buf, buf.Length, out var n, IntPtr.Zero)) return buf[..n];
        LastReadError = Marshal.GetLastWin32Error();
        return null;
    }

    /// <summary>Sends a feature report (id first) padded to FeatureReportByteLength.</summary>
    public bool SetFeature(ReadOnlySpan<byte> report)
    {
        var buf = new byte[Math.Max((int)Caps.FeatureReportByteLength, report.Length)];
        report.CopyTo(buf);
        lock (_writeLock) return Native.HidD_SetFeature(_handle, buf, buf.Length);
    }

    public void Dispose()
    {
        _handle.Dispose();
        _readHandle?.Dispose();
    }
}
