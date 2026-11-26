using System;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace Notch.Services;

/// <summary>Kullanici ayarlari. %AppData%\Notch\settings.json icinde saklanir.</summary>
public sealed class AppSettings
{
    // Ilk acilis yardimcisi gosterildi mi
    public bool OnboardingDone { get; set; }

    // Genel
    public bool AutoStart { get; set; } = true;
    public bool Hidden { get; set; }
    public bool HideInFullscreen { get; set; } = true;

    // Acilma
    public int EdgeDwellMs { get; set; } = 350;
    public bool EdgeOnly { get; set; } = true;          // sadece ekranin en ust kenarinda beklerken ac
    public int CloseDelayMs { get; set; } = 450;

    // Kisayollar
    public string ToggleHotkey { get; set; } = "Win+Alt+N";   // Win+Alt+B = Windows HDR kisayolu
    public string ClipboardHotkey { get; set; } = "Win+Shift+V";
    // Notch'u tasima: varsayilan yok, kullanici atar
    public string MoveLeftHotkey { get; set; } = "";
    public string MoveRightHotkey { get; set; } = "";
    public string MoveCenterHotkey { get; set; } = "";
    public string NextMonitorHotkey { get; set; } = "";

    // Ses
    public bool ShowVolume { get; set; } = true;
    public double VolumeSeconds { get; set; } = 1.6;
    public bool WatchSonar { get; set; } = true;
    public bool HideWindowsVolumeOsd { get; set; } = true;  // ses tuslarini yakala, Windows ses kutusu cikmasin

    // Sonar GAME profilini otomatik degistirme (profil kimligi bos = degistirme)
    public bool SonarAutoSwitch { get; set; }
    public string SonarGameProfile { get; set; } = "";
    public string SonarMusicProfile { get; set; } = "";
    public string SonarMeetingProfile { get; set; } = "";
    public string SonarIdleProfile { get; set; } = "";
    public string SonarMeetingApps { get; set; } = "Zoom, ms-teams, Teams, msedgewebview2, webex, CiscoCollabHost, slack, Skype, WhatsApp, chrome, msedge, firefox, brave, opera";
    public string SonarExtraGames { get; set; } = "";
    public string SonarIgnoredGames { get; set; } = "";
    public bool SonarFullscreenIsGame { get; set; } = true;
    public bool SonarNotifySwitch { get; set; } = true;

    // Bildirimler
    public bool NotifyTrack { get; set; } = true;
    public bool NotifyCopy { get; set; } = true;
    public bool NotifyDevice { get; set; } = true;
    public double ToastSeconds { get; set; } = 3;
    public bool NotifyWindows { get; set; } = true;        // WhatsApp, Zoom, Discord... Windows bildirimleri
    public bool NotifyShowContent { get; set; } = true;    // kapaliysa yalnizca uygulama adi gosterilir
    public string NotifyBlockList { get; set; } = "";
    public bool SuppressWindowsBanners { get; set; }        // Windows'un kendi bildirim kutularini gizle      // virgulle ayrilmis uygulama adlari

    // Pano
    public int ClipboardMax { get; set; } = 50;

    // Gorunum
    public bool AccentFromArt { get; set; } = true;
    public string AccentColor { get; set; } = "#FF5A1F";
    public double Scale { get; set; } = 1.0;            // 0.8 - 1.4
    public double Opacity { get; set; } = 1.0;          // 0.5 - 1
    public int OffsetX { get; set; }                    // ekran ortasindan yatay kayma (piksel)
    public string Monitor { get; set; } = "";           // bos = ana ekran; aksi halde ekran cihaz adi

    // Mikrofon
    public string MicHotkey { get; set; } = "Win+Alt+U";
    public bool NotifyMic { get; set; } = true;
    public bool ShowMicIndicator { get; set; } = true;  // bostayken: sessiz / kullanimda gostergesi

    // Cihazlar
    public bool NotifyBluetooth { get; set; } = true;
    public int LowBatteryPercent { get; set; } = 20;

    // Hava durumu
    public string WeatherCity { get; set; } = "";
    public bool ShowWeatherIdle { get; set; } = true;

    // Medya
    public bool Lyrics { get; set; } = true;            // sarki sozleri (lrclib.net)

    // Zamanlayici / Pomodoro
    public int PomodoroWork { get; set; } = 25;
    public int PomodoroBreak { get; set; } = 5;
    public int PomodoroLongBreak { get; set; } = 15;
    public string PomodoroDay { get; set; } = "";
    public int PomodoroDone { get; set; }

    // Ekran goruntusu
    public bool ScreenshotToTray { get; set; } = true;

    // Takvim (masaustu Outlook)
    public bool OutlookCalendar { get; set; } = true;
    public bool FindMeetingLinks { get; set; } = true;  // Zoom/Teams baglantisini etkinlik govdesinde de ara
    public int MeetingReminderMin { get; set; } = 5;    // 0 = hatirlatma yok

    // Odak modu
    public bool FocusMode { get; set; }
    public string FocusHotkey { get; set; } = "";
    public bool FocusDuringPomodoro { get; set; } = true;
    public bool FocusDuringMeetings { get; set; } = true;

    // Izleyiciler
    public bool NotifyDownloads { get; set; } = true;
    public bool DownloadsToTray { get; set; } = true;
    public bool ShowCameraIndicator { get; set; } = true;
    public bool NotifyCamera { get; set; } = true;
    public bool NotifyNetwork { get; set; } = true;

    // Sistem uyarilari
    public bool HealthAlerts { get; set; } = true;
    public int AlertGpuTemp { get; set; } = 85;
    public int AlertRamPercent { get; set; } = 90;
    public int AlertDiskGb { get; set; } = 10;

    // Guncelleme
    public string UpdateSource { get; set; } = "";       // bos = GitHub Releases; aksi halde klasor yolu veya https adresi
    public bool AutoCheckUpdates { get; set; } = true;

    // Oyun
    public bool GameHud { get; set; } = true;           // tam ekranda mini gosterge (CPU/GPU/ping)
    public string PingHost { get; set; } = "1.1.1.1";

    // ------------------------------------------------------------------

    static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notch", "settings.json");

    public static AppSettings Current { get; private set; } = Load();
    public static event Action? Changed;

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                // Yardimcidan onceki surumden gelen kullaniciya gosterme
                if (!json.Contains(nameof(OnboardingDone))) loaded.OnboardingDone = true;
                return loaded;
            }
        }
        catch { }
        var s = new AppSettings();
        // Eski surumde otomatik baslatma kapatildiysa bunu koru
        if (File.Exists(Path.Combine(Path.GetDirectoryName(FilePath)!, "no-autostart"))) s.AutoStart = false;
        return s;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch { }
        Changed?.Invoke();
    }

    public static void ResetToDefaults()
    {
        bool hidden = Current.Hidden;
        Current = new AppSettings { Hidden = hidden, OnboardingDone = Current.OnboardingDone };
        Save();
    }
}

/// <summary>"Win+Shift+N" gibi kisayol metinlerini Win32 RegisterHotKey degerlerine cevirir.</summary>
public static class HotkeyText
{
    public static bool TryParse(string? text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "win": mods |= Native.MOD_WIN; break;
                case "ctrl": mods |= Native.MOD_CONTROL; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "alt": mods |= Native.MOD_ALT; break;
                default:
                    if (!Enum.TryParse<Key>(part, true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0 && mods != 0;
    }

    public static string Format(ModifierKeys mods, Key key)
    {
        var s = "";
        if (mods.HasFlag(ModifierKeys.Windows)) s += "Win+";
        if (mods.HasFlag(ModifierKeys.Control)) s += "Ctrl+";
        if (mods.HasFlag(ModifierKeys.Shift)) s += "Shift+";
        if (mods.HasFlag(ModifierKeys.Alt)) s += "Alt+";
        return s + key;
    }
}
