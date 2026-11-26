using System;
using System.Collections.Generic;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Notch.Services;

/// <summary>
/// Tum aktif ses cikis cihazlarinin Windows ses seviyesini olay tabanli izler.
/// Hoparlor topuzu (orn. Logi Z407) cihazin Windows sesini degistirdigi icin aninda yakalanir.
/// </summary>
public sealed class VolumeService : IMMNotificationClient, IDisposable
{
    public event Action<string, int, bool>? Changed;        // ad, %, sessiz
    public event Action<string>? DefaultDeviceChanged;       // yeni varsayilan cihazin adi

    readonly Dispatcher _ui;
    readonly MMDeviceEnumerator _enum = new();
    readonly List<(MMDevice Dev, AudioEndpointVolumeNotificationDelegate Handler)> _subs = new();
    readonly DispatcherTimer _resubscribe;
    string? _lastDefaultId;

    public VolumeService(Dispatcher ui)
    {
        _ui = ui;
        _resubscribe = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(600) };
        _resubscribe.Tick += (_, _) =>
        {
            _resubscribe.Stop();
            Subscribe();
        };
    }

    public void Start()
    {
        Subscribe();
        try { _lastDefaultId = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
        _enum.RegisterEndpointNotificationCallback(this);
    }

    void Subscribe()
    {
        Unsubscribe();
        foreach (var dev in _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var name = PrettyName(dev.FriendlyName);
                var vol = dev.AudioEndpointVolume;
                int lastV = (int)Math.Round(vol.MasterVolumeLevelScalar * 100);
                bool lastM = vol.Mute;
                AudioEndpointVolumeNotificationDelegate h = d =>
                {
                    int v = (int)Math.Round(d.MasterVolume * 100);
                    if (v == lastV && d.Muted == lastM) return;
                    lastV = v; lastM = d.Muted;
                    Changed?.Invoke(name, v, d.Muted);
                };
                vol.OnVolumeNotification += h;
                _subs.Add((dev, h));
            }
            catch { /* erisilemeyen cihazi atla */ }
        }
    }

    void Unsubscribe()
    {
        foreach (var (dev, h) in _subs)
        {
            try { dev.AudioEndpointVolume.OnVolumeNotification -= h; dev.Dispose(); } catch { }
        }
        _subs.Clear();
    }

    public static string PrettyName(string n)
    {
        const string sonar = "SteelSeries Sonar - ";
        int p = n.IndexOf(" (", StringComparison.Ordinal);
        string outer = p > 0 ? n[..p] : n;
        if (outer.StartsWith(sonar, StringComparison.Ordinal))
            return "Sonar · " + outer[sonar.Length..].Replace("Gaming", "Game");
        if (p > 0 && n.EndsWith(')')) return n[(p + 2)..^1];
        return n;
    }

    void QueueResubscribe() => _ui.BeginInvoke(() => { _resubscribe.Stop(); _resubscribe.Start(); });

    // IMMNotificationClient
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => QueueResubscribe();
    public void OnDeviceAdded(string pwstrDeviceId) => QueueResubscribe();
    public void OnDeviceRemoved(string deviceId) => QueueResubscribe();
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render || role != Role.Multimedia || defaultDeviceId == _lastDefaultId) return;
        _lastDefaultId = defaultDeviceId;
        _ui.BeginInvoke(() =>
        {
            try
            {
                using var d = _enum.GetDevice(defaultDeviceId);
                DefaultDeviceChanged?.Invoke(PrettyName(d.FriendlyName));
            }
            catch { }
        });
    }

    public void Dispose()
    {
        try { _enum.UnregisterEndpointNotificationCallback(this); } catch { }
        Unsubscribe();
        _enum.Dispose();
    }
}
