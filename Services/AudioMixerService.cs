using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;

namespace Notch.Services;

public sealed record OutputDevice(string Id, string Name, bool IsDefault, int Volume, bool Muted);

public sealed class AppAudio
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public ImageSource? Icon { get; init; }
    public float Volume { get; set; }
    public bool Muted { get; set; }
    internal List<AudioSessionControl> Sessions { get; } = new();
}

/// <summary>Ses cikis cihazlari ve uygulama bazinda ses seviyeleri (Windows Ses Karistiricisi gibi).</summary>
public sealed class AudioMixerService : IDisposable
{
    readonly MMDeviceEnumerator _enum = new();
    readonly List<MMDevice> _held = new();          // oturumlar yasadigi surece cihazlari tut
    static readonly Dictionary<string, ImageSource?> IconCache = new();

    public List<OutputDevice> GetOutputs()
    {
        string? defId = null;
        try { defId = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
        var list = new List<OutputDevice>();
        foreach (var d in _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var v = d.AudioEndpointVolume;
                list.Add(new OutputDevice(d.ID, VolumeService.PrettyName(d.FriendlyName), d.ID == defId,
                    (int)Math.Round(v.MasterVolumeLevelScalar * 100), v.Mute));
            }
            catch { }
            finally { d.Dispose(); }
        }
        return list.OrderByDescending(o => o.IsDefault).ThenBy(o => o.Name).ToList();
    }

    public void SetDefaultOutput(string id)
    {
        var pc = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            pc.SetDefaultEndpoint(id, 0); // eConsole
            pc.SetDefaultEndpoint(id, 1); // eMultimedia
            pc.SetDefaultEndpoint(id, 2); // eCommunications
        }
        finally { Marshal.ReleaseComObject(pc); }
    }

    public void SetDeviceVolume(string id, float v)
    {
        try { using var d = _enum.GetDevice(id); d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0, 1); } catch { }
    }

    public void SetDeviceMute(string id, bool mute)
    {
        try { using var d = _enum.GetDevice(id); d.AudioEndpointVolume.Mute = mute; } catch { }
    }

    /// <summary>Tum cikis cihazlarindaki ses oturumlarini uygulamaya gore gruplar.</summary>
    public List<AppAudio> GetApps()
    {
        foreach (var d in _held) d.Dispose();
        _held.Clear();

        var apps = new Dictionary<string, AppAudio>();
        int self = Environment.ProcessId;
        foreach (var dev in _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            _held.Add(dev);
            SessionCollection sessions;
            try
            {
                dev.AudioSessionManager.RefreshSessions();
                sessions = dev.AudioSessionManager.Sessions;
            }
            catch { continue; }

            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                try
                {
                    if (s.IsSystemSoundsSession || s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                    int pid = (int)s.GetProcessID;
                    if (pid == 0 || pid == self) continue;
                    var (key, name, path) = Describe(pid);
                    if (key.StartsWith("steelseries", StringComparison.OrdinalIgnoreCase)) continue; // Sonar'in kendi yonlendirmesi
                    if (!apps.TryGetValue(key, out var app))
                    {
                        app = new AppAudio
                        {
                            Key = key, Name = name, Icon = GetIcon(path),
                            Volume = s.SimpleAudioVolume.Volume, Muted = s.SimpleAudioVolume.Mute,
                        };
                        apps[key] = app;
                    }
                    app.Sessions.Add(s);
                }
                catch { }
            }
        }
        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void SetAppVolume(AppAudio app, float v)
    {
        app.Volume = Math.Clamp(v, 0, 1);
        foreach (var s in app.Sessions) try { s.SimpleAudioVolume.Volume = app.Volume; } catch { }
    }

    public static void SetAppMute(AppAudio app, bool mute)
    {
        app.Muted = mute;
        foreach (var s in app.Sessions) try { s.SimpleAudioVolume.Mute = mute; } catch { }
    }

    static (string key, string name, string? path) Describe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string key = p.ProcessName;
            string? path = null;
            string name = key;
            try
            {
                path = p.MainModule?.FileName;
                if (path != null)
                {
                    var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc)) name = desc.Trim();
                }
            }
            catch { /* yonetici olarak calisan surece erisilemez */ }
            if (name.Length > 0) name = char.ToUpper(name[0]) + name[1..];
            return (key, name, path);
        }
        catch { return ($"pid{pid}", $"İşlem {pid}", null); }
    }

    internal static ImageSource? GetIcon(string? path)
    {
        if (path == null) return null;
        if (IconCache.TryGetValue(path, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico != null)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                img = src;
            }
        }
        catch { }
        IconCache[path] = img;
        return img;
    }

    public void Dispose()
    {
        foreach (var d in _held) d.Dispose();
        _enum.Dispose();
    }

    // Belgelenmemis ama Windows 7'den beri kararli: varsayilan ses cihazini degistirir
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr fmt);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr fmt);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a, IntPtr b);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr a, IntPtr b);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fx, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fx, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }
}
