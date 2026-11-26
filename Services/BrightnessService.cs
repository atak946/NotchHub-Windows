using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Notch.Services;

public sealed class MonitorBrightness
{
    public required string Name { get; init; }
    public required string Device { get; init; }
    public IntPtr Handle { get; init; }
    /// <summary>DDC/CI yanit vermiyorsa ekran kartinin gamma tablosuyla yazilimsal karartma.</summary>
    public bool Software { get; init; }
    public uint Min { get; init; }
    public uint Max { get; init; }
    public uint Current { get; set; }
}

/// <summary>
/// Monitor parlakligi: once DDC/CI (gercek arka isik), olmazsa gamma ile yazilimsal karartma.
/// DDC yavas oldugu icin islemler arka planda ve sinirli hizda yapilir.
/// </summary>
public sealed class BrightnessService : IDisposable
{
    const uint SoftMin = 30;

    readonly List<MonitorBrightness> _monitors = new();
    readonly List<PHYSICAL_MONITOR[]> _handles = new();
    readonly SemaphoreSlim _lock = new(1, 1);
    readonly Dictionary<MonitorBrightness, uint> _wanted = new();
    static readonly Dictionary<string, uint> _softLevels = new();
    bool _applying;

    public async Task<List<MonitorBrightness>> LoadAsync() => await Task.Run(() =>
    {
        _lock.Wait();
        try
        {
            Close();
            var hmons = new List<(IntPtr H, string Dev)>();
            MonitorEnumProc cb = (IntPtr h, IntPtr dc, ref RECT r, IntPtr d) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                GetMonitorInfo(h, ref mi);
                hmons.Add((h, mi.szDevice));
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            hmons.Sort((a, b) => string.CompareOrdinal(a.Dev, b.Dev));

            foreach (var (h, dev) in hmons)
            {
                string fallback = dev.Replace(@"\\.\DISPLAY", "Ekran ");
                string name = EdidName(dev) ?? fallback;
                bool added = false;

                if (GetNumberOfPhysicalMonitorsFromHMONITOR(h, out uint n) && n > 0)
                {
                    var arr = new PHYSICAL_MONITOR[n];
                    if (GetPhysicalMonitorsFromHMONITOR(h, n, arr))
                    {
                        _handles.Add(arr);
                        foreach (var pm in arr)
                        {
                            if (!TryRead(pm.hPhysicalMonitor, out uint min, out uint cur, out uint max)) continue;
                            if (name == fallback && !string.IsNullOrWhiteSpace(pm.szPhysicalMonitorDescription) && !pm.szPhysicalMonitorDescription.StartsWith("Generic"))
                                name = pm.szPhysicalMonitorDescription;
                            _monitors.Add(new MonitorBrightness { Name = name, Device = dev, Handle = pm.hPhysicalMonitor, Min = min, Max = max, Current = cur });
                            added = true;
                        }
                    }
                }

                if (!added && CanGamma(dev))
                {
                    uint lvl;
                    lock (_softLevels) lvl = _softLevels.TryGetValue(dev, out var v) ? v : 100;
                    _monitors.Add(new MonitorBrightness { Name = name, Device = dev, Software = true, Min = SoftMin, Max = 100, Current = lvl });
                }
            }
            return new List<MonitorBrightness>(_monitors);
        }
        catch { return new List<MonitorBrightness>(); }
        finally { _lock.Release(); }
    });

    /// <summary>DDC bazen ilk denemede cevap vermez; birkac kez dene, ust seviye API olmazsa VCP 0x10'u dene.</summary>
    static bool TryRead(IntPtr h, out uint min, out uint cur, out uint max)
    {
        for (int i = 0; i < 3; i++)
        {
            if (GetMonitorBrightness(h, out min, out cur, out max) && max > min) return true;
            if (GetVCPFeatureAndVCPFeatureReply(h, 0x10, out _, out cur, out max) && max > 0) { min = 0; return true; }
            Thread.Sleep(60);
        }
        min = cur = max = 0;
        return false;
    }

    /// <summary>Kaydirici surukleniyorken son degeri uygular (DDC'yi bogmamak icin).</summary>
    public void Set(MonitorBrightness m, uint value)
    {
        value = Math.Clamp(value, m.Min, m.Max);
        m.Current = value;
        if (m.Software)
        {
            lock (_softLevels) _softLevels[m.Device] = value;
            ApplyGamma(m.Device, value);
            return;
        }
        lock (_wanted) _wanted[m] = value;
        if (_applying) return;
        _applying = true;
        Task.Run(async () =>
        {
            while (true)
            {
                KeyValuePair<MonitorBrightness, uint>[] todo;
                lock (_wanted)
                {
                    if (_wanted.Count == 0) { _applying = false; return; }
                    todo = new List<KeyValuePair<MonitorBrightness, uint>>(_wanted).ToArray();
                    _wanted.Clear();
                }
                await _lock.WaitAsync();
                try
                {
                    foreach (var (mon, v) in todo)
                        if (!SetMonitorBrightness(mon.Handle, v)) SetVCPFeature(mon.Handle, 0x10, v);
                }
                catch { }
                finally { _lock.Release(); }
                await Task.Delay(120);
            }
        });
    }

    // ---- Yazilimsal karartma (gamma) ----

    static bool CanGamma(string dev)
    {
        IntPtr dc = CreateDC(null, dev, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        try { var r = new ushort[3 * 256]; return GetDeviceGammaRamp(dc, r); }
        finally { DeleteDC(dc); }
    }

    static void ApplyGamma(string dev, uint percent)
    {
        IntPtr dc = CreateDC(null, dev, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return;
        try
        {
            // Windows asiri koyu tablolari reddeder; reddedilirse biraz daha acik dene.
            for (double f = percent / 100.0; f <= 1.0001; f += 0.05)
            {
                var ramp = new ushort[3 * 256];
                for (int i = 0; i < 256; i++)
                {
                    ushort v = (ushort)Math.Min(65535, i * 257 * f);
                    ramp[i] = ramp[256 + i] = ramp[512 + i] = v;
                }
                if (SetDeviceGammaRamp(dc, ramp)) return;
            }
        }
        finally { DeleteDC(dc); }
    }

    /// <summary>Monitorun gercek adini (orn. "Odyssey G5") EDID'den okur.</summary>
    static string? EdidName(string dev)
    {
        try
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(dev, 0, ref dd, 1 /* EDD_GET_DEVICE_INTERFACE_NAME */)) return null;
            // \\?\DISPLAY#SAM7123#5&abc&0&UID4352#{guid}
            var parts = dd.DeviceID.Split('#');
            if (parts.Length < 3) return null;
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
            if (k?.GetValue("EDID") is not byte[] edid || edid.Length < 128) return null;
            for (int off = 54; off <= 108; off += 18)
            {
                if (edid[off] == 0 && edid[off + 1] == 0 && edid[off + 3] == 0xFC)
                {
                    var s = Encoding.ASCII.GetString(edid, off + 5, 13);
                    int nl = s.IndexOf('\n');
                    if (nl >= 0) s = s[..nl];
                    s = s.Trim();
                    if (s.Length > 0) return s;
                }
            }
        }
        catch { }
        return null;
    }

    void Close()
    {
        foreach (var arr in _handles) try { DestroyPhysicalMonitors((uint)arr.Length, arr); } catch { }
        _handles.Clear();
        _monitors.Clear();
    }

    /// <summary>Cikista yazilimsal karartmayi geri al.</summary>
    public static void RestoreGamma()
    {
        lock (_softLevels)
        {
            foreach (var dev in _softLevels.Keys) ApplyGamma(dev, 100);
            _softLevels.Clear();
        }
    }

    public void Dispose()
    {
        RestoreGamma();
        _lock.Wait(); try { Close(); } finally { _lock.Release(); }
    }

    // ---- Win32 ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string dev, uint i, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMon, out uint count);
    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMon, uint count, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)] static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr h, uint value);
    [DllImport("dxva2.dll")] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, out int type, out uint cur, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string? driver, string device, string? output, IntPtr init);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool GetDeviceGammaRamp(IntPtr dc, [Out] ushort[] ramp);
    [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
}
