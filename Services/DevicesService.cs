using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Notch.Services;

public sealed record BatteryDevice(string Name, int Percent, bool IsSystem, bool Charging);

/// <summary>
/// Bluetooth baglanti olaylari ve pil seviyeleri.
/// Pil bilgisi Windows'un "Bluetooth pil" ozelligini bildiren cihazlardan (kulaklik, mouse, klavye) okunur.
/// 2.4 GHz alicili (dongle) cihazlarin pili Windows'a bildirilmez.
/// </summary>
public sealed class DevicesService
{
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    public event Action<string, bool>? ConnectionChanged;   // ad, baglandi mi
    public event Action<BatteryDevice>? LowBattery;
    public event Action? BatteriesChanged;

    public List<BatteryDevice> Batteries { get; private set; } = new();

    readonly Dispatcher _ui;
    readonly Dictionary<string, string> _names = new();
    readonly HashSet<string> _warned = new();
    DeviceWatcher? _classic, _le;
    readonly DispatcherTimer _batteryPoll = new() { Interval = TimeSpan.FromSeconds(60) };

    public DevicesService(Dispatcher ui) => _ui = ui;

    public void Start()
    {
        _classic = Watch(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
        _le = Watch(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
        _batteryPoll.Tick += async (_, _) => await RefreshBatteries();
        _batteryPoll.Start();
        _ = RefreshBatteries();
    }

    DeviceWatcher? Watch(string selector)
    {
        try
        {
            var w = DeviceInformation.CreateWatcher(selector);
            bool ready = false;
            w.Added += (sender, d) => _ui.BeginInvoke(() =>
            {
                if (string.IsNullOrWhiteSpace(d.Name)) return;
                bool known = _names.ContainsKey(d.Id);
                _names[d.Id] = d.Name;
                if (ready && !known)
                {
                    ConnectionChanged?.Invoke(d.Name, true);
                    _ = RefreshBatteries();
                }
            });
            w.Removed += (sender, u) => _ui.BeginInvoke(() =>
            {
                if (_names.Remove(u.Id, out var name))
                {
                    ConnectionChanged?.Invoke(name, false);
                    _ = RefreshBatteries();
                }
            });
            w.Updated += (_, _) => { };
            w.EnumerationCompleted += (_, _) => _ui.BeginInvoke(() => ready = true);
            w.Start();
            return w;
        }
        catch { return null; }
    }

    public async Task RefreshBatteries()
    {
        var list = new List<BatteryDevice>();

        // Dizustu pil
        try
        {
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            if (ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery &&
                ps.BatteryLifePercent <= 1)
            {
                list.Add(new BatteryDevice("Bu bilgisayar", (int)Math.Round(ps.BatteryLifePercent * 100), true,
                    ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online));
            }
        }
        catch { }

        // Bluetooth pil bildiren cihazlar
        try
        {
            var devs = await DeviceInformation.FindAllAsync("", new[] { BatteryKey }, DeviceInformationKind.Device);
            foreach (var d in devs)
            {
                if (d.Properties.TryGetValue(BatteryKey, out var v) && v is byte b && !string.IsNullOrWhiteSpace(d.Name))
                {
                    if (list.Any(x => x.Name == d.Name)) continue;
                    list.Add(new BatteryDevice(d.Name, b, false, false));
                }
            }
        }
        catch { }

        Batteries = list;
        BatteriesChanged?.Invoke();

        int threshold = AppSettings.Current.LowBatteryPercent;
        foreach (var bd in list)
        {
            if (bd.Percent <= threshold && !bd.Charging)
            {
                if (_warned.Add(bd.Name)) LowBattery?.Invoke(bd);
            }
            else if (bd.Percent > threshold + 5) _warned.Remove(bd.Name);
        }
    }

    public void Stop()
    {
        try { _classic?.Stop(); } catch { }
        try { _le?.Stop(); } catch { }
        _batteryPoll.Stop();
    }
}
