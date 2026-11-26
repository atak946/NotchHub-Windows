using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace Notch.Services;

/// <summary>
/// Windows'un uygulama bazindaki "Bildirim basliklarini goster" ayarini (ShowBanner) yonetir.
/// Kapatilinca bildirim kutusu ekranda cikmaz ama bildirim merkezine ve veritabanina yazilmaya
/// devam eder; boylece Notch gostermeye devam eder.
/// Yalnizca Notch'un degistirdigi uygulamalar kaydedilir ve geri alinirken yalnizca onlara dokunulur.
/// </summary>
public static class BannerControl
{
    const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    static readonly string StatePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notch", "banners.json");

    static HashSet<string>? _changed;

    static HashSet<string> Changed
    {
        get
        {
            if (_changed != null) return _changed;
            try { _changed = File.Exists(StatePath) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(StatePath)) : null; } catch { }
            return _changed ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    static void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(Changed));
        }
        catch { }
    }

    /// <summary>Windows'un kendi sistem/guvenlik bildirimleri: her zaman ekranda gorunsun.</summary>
    static bool IsSystem(string aumid) =>
        aumid.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase) ||
        aumid.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase) ||
        aumid.StartsWith("windows.immersivecontrolpanel", StringComparison.OrdinalIgnoreCase) ||
        aumid.Contains("SecurityCenter", StringComparison.OrdinalIgnoreCase) ||
        aumid.Contains("Defender", StringComparison.OrdinalIgnoreCase);

    static bool IsExcluded(string aumid) => IsSystem(aumid) || IsBlocked(aumid);

    static bool IsBlocked(string aumid)
    {
        var app = WindowsNotificationService.AppName(aumid);
        return AppSettings.Current.NotifyBlockList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(b => app.Contains(b, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Kayitli tum uygulamalarin (engellenenler haric) bildirim kutusunu kapatir.</summary>
    public static void ApplyAll()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SettingsKey);
            if (root == null) return;
            foreach (var aumid in root.GetSubKeyNames()) Suppress(aumid, save: false);
        }
        catch { }
        // Sonradan engellenen (veya sistem) uygulamalarin kutusunu geri ac
        foreach (var aumid in Changed.Where(IsExcluded).ToList()) Restore(aumid);
        SaveState();
    }

    /// <summary>Tek bir uygulamanin bildirim kutusunu kapatir (yeni bildirim gonderen uygulamalar icin).</summary>
    public static void Suppress(string aumid, bool save = true)
    {
        if (string.IsNullOrEmpty(aumid) || IsExcluded(aumid) || Changed.Contains(aumid)) return;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"{SettingsKey}\{aumid}");
            var cur = k.GetValue("ShowBanner");
            if (cur is int v && v == 0) return; // kullanici zaten kapatmis: bizim degil, dokunma
            k.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
            Changed.Add(aumid);
            if (save) SaveState();
        }
        catch { }
    }

    static void Restore(string aumid)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"{SettingsKey}\{aumid}", writable: true);
            k?.DeleteValue("ShowBanner", throwOnMissingValue: false);
        }
        catch { }
        Changed.Remove(aumid);
    }

    /// <summary>Notch'un kapattigi tum bildirim kutularini eski haline getirir.</summary>
    public static void RestoreAll()
    {
        foreach (var aumid in Changed.ToList()) Restore(aumid);
        SaveState();
    }
}
