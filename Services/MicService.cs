using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace Notch.Services;

/// <summary>
/// Varsayilan mikrofonun sessiz durumunu ve mikrofonu o an kullanan uygulamalari izler.
/// SteelSeries Sonar mikrofonu surekli isledigi icin Sonar'in kendi surecleri sayilmaz.
/// </summary>
public sealed class MicService : IDisposable
{
    public event Action<bool>? MuteChanged;                 // true = sessiz
    public event Action<bool, IReadOnlyList<string>>? UsageChanged;

    public bool Muted { get; private set; }
    public bool InUse { get; private set; }
    public IReadOnlyList<string> Users { get; private set; } = Array.Empty<string>();
    public string DeviceName { get; private set; } = "";

    readonly MMDeviceEnumerator _enum = new();
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    bool _first = true;
    int _tick;

    public void Start()
    {
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        Poll();
    }

    MMDevice? Default()
    {
        try { return _enum.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
        catch
        {
            try { return _enum.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); } catch { return null; }
        }
    }

    void Poll()
    {
        using (var dev = Default())
        {
            bool muted = false;
            if (dev != null)
            {
                try { muted = dev.AudioEndpointVolume.Mute; DeviceName = VolumeService.PrettyName(dev.FriendlyName); } catch { }
            }
            if (muted != Muted || _first)
            {
                Muted = muted;
                if (!_first) MuteChanged?.Invoke(muted);
            }
        }

        // Kullanim kontrolu biraz daha seyrek (saniyede bir)
        if (_tick++ % 4 == 0)
        {
            var users = FindUsers();
            bool inUse = users.Count > 0;
            if (inUse != InUse || !users.SequenceEqual(Users))
            {
                InUse = inUse;
                Users = users;
                UsageChanged?.Invoke(inUse, users);
            }
        }
        _first = false;
    }

    List<string> FindUsers()
    {
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        int self = Environment.ProcessId;
        foreach (var dev in _enum.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            try
            {
                dev.AudioSessionManager.RefreshSessions();
                var ss = dev.AudioSessionManager.Sessions;
                for (int i = 0; i < ss.Count; i++)
                {
                    var s = ss[i];
                    if (s.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                    int pid = (int)s.GetProcessID;
                    if (pid == 0 || pid == self) continue;
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        if (p.ProcessName.StartsWith("SteelSeries", StringComparison.OrdinalIgnoreCase)) continue;
                        names.Add(p.ProcessName);
                    }
                    catch { }
                }
            }
            catch { }
            finally { dev.Dispose(); }
        }
        return names.ToList();
    }

    public void Toggle() => SetMute(!Muted);

    public void SetMute(bool mute)
    {
        // Hem iletisim hem varsayilan mikrofonu (farkliysa) birlikte sustur
        var ids = new HashSet<string>();
        foreach (var role in new[] { Role.Communications, Role.Console })
        {
            try
            {
                using var d = _enum.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                if (ids.Add(d.ID)) d.AudioEndpointVolume.Mute = mute;
            }
            catch { }
        }
        Poll();
    }

    public void Dispose()
    {
        _poll.Stop();
        _enum.Dispose();
    }
}
