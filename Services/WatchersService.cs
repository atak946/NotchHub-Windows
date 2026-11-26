using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Notch.Services;

// ======================================================================
//  Indirilenler klasoru: tamamlanan indirmeler
// ======================================================================
public sealed class DownloadsWatcher : IDisposable
{
    public event Action<string>? Completed;

    static readonly HashSet<string> TempExt = new(StringComparer.OrdinalIgnoreCase)
        { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload", ".!ut", ".aria2" };

    readonly Dispatcher _ui;
    FileSystemWatcher? _fsw;
    readonly ConcurrentDictionary<string, (long Size, DateTime Seen)> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer _check = new() { Interval = TimeSpan.FromSeconds(1) };

    public DownloadsWatcher(Dispatcher ui) => _ui = ui;

    public static string DownloadsPath()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
            {
                var s = Marshal.PtrToStringUni(p)!;
                Marshal.FreeCoTaskMem(p);
                return s;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public void Start()
    {
        var dir = DownloadsPath();
        if (!Directory.Exists(dir)) return;
        _fsw = new FileSystemWatcher(dir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size };
        _fsw.Created += (_, e) => Track(e.FullPath);
        _fsw.Renamed += (_, e) => Track(e.FullPath);
        _fsw.EnableRaisingEvents = true;
        _check.Tick += (_, _) => Check();
        _check.Start();
    }

    void Track(string path)
    {
        var name = Path.GetFileName(path);
        if (TempExt.Contains(Path.GetExtension(path)) || name.StartsWith("~$") || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return;
        _pending[path] = (-1, DateTime.Now);
    }

    void Check()
    {
        foreach (var (path, st) in _pending.ToArray())
        {
            try
            {
                if (!File.Exists(path)) { _pending.TryRemove(path, out _); continue; }
                long size = new FileInfo(path).Length;
                if (size != st.Size) { _pending[path] = (size, DateTime.Now); continue; } // hala yaziliyor
                if ((DateTime.Now - st.Seen).TotalSeconds < 1.5) continue;
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { } // kilitli mi?
                _pending.TryRemove(path, out _);
                if (size > 0) Completed?.Invoke(path);
            }
            catch (IOException) { /* hala aciliyor */ }
            catch { _pending.TryRemove(path, out _); }
            if ((DateTime.Now - st.Seen).TotalMinutes > 30) _pending.TryRemove(path, out _);
        }
    }

    public void Dispose() { _check.Stop(); _fsw?.Dispose(); }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}

// ======================================================================
//  Kamera kullanimi (Windows gizlilik kayitlarindan)
// ======================================================================
public sealed class CameraWatcher
{
    public event Action<bool, IReadOnlyList<string>>? Changed;
    public bool InUse { get; private set; }
    public IReadOnlyList<string> Users { get; private set; } = Array.Empty<string>();

    const string Key = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };

    public void Start()
    {
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        Poll();
    }

    void Poll()
    {
        var users = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Key);
            if (root != null)
            {
                foreach (var name in root.GetSubKeyNames())
                {
                    if (name == "NonPackaged")
                    {
                        using var np = root.OpenSubKey(name);
                        if (np == null) continue;
                        foreach (var exe in np.GetSubKeyNames())
                        {
                            using var k = np.OpenSubKey(exe);
                            if (Active(k)) users.Add(Path.GetFileNameWithoutExtension(exe.Replace('#', '\\')));
                        }
                    }
                    else
                    {
                        using var k = root.OpenSubKey(name);
                        if (Active(k)) users.Add(WindowsNotificationService.AppName(name));
                    }
                }
            }
        }
        catch { }
        var list = users.ToList();
        bool inUse = list.Count > 0;
        if (inUse != InUse || !list.SequenceEqual(Users))
        {
            InUse = inUse;
            Users = list;
            Changed?.Invoke(inUse, list);
        }
    }

    static bool Active(RegistryKey? k) =>
        k != null && k.GetValue("LastUsedTimeStop") is long stop && stop == 0 &&
        k.GetValue("LastUsedTimeStart") is long start && start > 0;
}

// ======================================================================
//  Ag ve VPN baglantisi
// ======================================================================
public sealed class NetworkWatcher
{
    public event Action<bool>? OnlineChanged;                // internet var mi
    public event Action<string, bool>? VpnChanged;           // VPN adi, bagli mi

    static readonly string[] VpnWords =
    {
        "vpn", "wireguard", "wintun", "tap-windows", "openvpn", "fortinet", "forticlient", "anyconnect", "cisco",
        "globalprotect", "pangp", "zscaler", "nordlynx", "tailscale", "zerotier", "sonicwall", "checkpoint",
        "juniper", "pulse secure", "f5 ", "array networks", "protonvpn", "expressvpn", "surfshark", "windscribe",
    };

    readonly Dispatcher _ui;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromSeconds(2) };
    bool _online;
    HashSet<string> _vpns = new();

    public NetworkWatcher(Dispatcher ui) => _ui = ui;

    public void Start()
    {
        (_online, _vpns) = Evaluate();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Recheck(); };
        NetworkChange.NetworkAddressChanged += (_, _) => _ui.BeginInvoke(Kick);
        NetworkChange.NetworkAvailabilityChanged += (_, _) => _ui.BeginInvoke(Kick);
    }

    void Kick() { _debounce.Stop(); _debounce.Start(); }

    void Recheck()
    {
        var (online, vpns) = Evaluate();
        if (online != _online) { _online = online; OnlineChanged?.Invoke(online); }
        foreach (var v in vpns.Except(_vpns)) VpnChanged?.Invoke(v, true);
        foreach (var v in _vpns.Except(vpns)) VpnChanged?.Invoke(v, false);
        _vpns = vpns;
    }

    static (bool Online, HashSet<string> Vpns) Evaluate()
    {
        bool online = false;
        var vpns = new HashSet<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var text = (ni.Name + " " + ni.Description).ToLowerInvariant();
                bool vpn = ni.NetworkInterfaceType == NetworkInterfaceType.Ppp || VpnWords.Any(text.Contains);
                if (vpn && !text.Contains("hyper-v") && !text.Contains("vethernet")) { vpns.Add(ni.Name); continue; }
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                try { if (ni.GetIPProperties().GatewayAddresses.Count > 0) online = true; } catch { }
            }
        }
        catch { }
        return (online, vpns);
    }
}
