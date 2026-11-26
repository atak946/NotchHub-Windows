using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Notch.Services;
using WinForms = System.Windows.Forms;

namespace Notch;

/// <summary>
/// Ek ozellikler: ses karistirici, mikrofon, cihazlar/pil, hava durumu, sarki sozleri,
/// pomodoro, notlar, gorsel yakalama, oyun gostergesi, gorunum ve konum.
/// </summary>
public partial class MainWindow
{
    static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notch");
    static readonly string CapturesDir = Path.Combine(DataDir, "captures");
    static readonly string NotesPath = Path.Combine(DataDir, "notes.txt");

    const int HotkeyMicId = 0x4E03;
    const int HotkeyMoveLeftId = 0x4E10, HotkeyMoveRightId = 0x4E11, HotkeyMoveUpId = 0x4E12, HotkeyMoveDownId = 0x4E13;
    public string? MicHotkeyError { get; private set; }

    readonly AudioMixerService _mixer = new();
    readonly MicService _mic = new();
    DevicesService? _devices;
    readonly WeatherService _weather = new();
    readonly LyricsService _lyrics = new();
    readonly PingService _ping = new();
    readonly WindowsNotificationService _winNotify = new();
    readonly VolumeKeyHook _volKeys = new();

    readonly DispatcherTimer _lyricTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _mixerRefresh = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _notesSave = new() { Interval = TimeSpan.FromMilliseconds(700) };

    bool _typing;
    SystemStats? _lastStats;
    int? _fps;
    readonly DispatcherTimer _fpsPoll = new() { Interval = TimeSpan.FromSeconds(1) };
    string _lastCity = "";
    Color _baseAccent = DefaultAccent;

    bool HudActive => S.GameHud && _fullscreen && !S.Hidden;
    bool StatsNeeded() => (_mode == Mode.Expanded && TabSys.IsChecked == true) || HudActive;
    Color BaseAccent => _baseAccent;

    // ================================================================
    //  Baslatma
    // ================================================================

    void StartFeatures()
    {
        Directory.CreateDirectory(DataDir);
        ApplyAppearance();
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PositionWindow);
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PositionWindow, DispatcherPriority.Background);

        // Mikrofon
        _mic.MuteChanged += muted =>
        {
            if (S.NotifyMic)
                ShowToast(muted ? "\uEC54" : "\uE720", muted ? "Mikrofon kapalı" : "Mikrofon açık", _mic.DeviceName);
            UpdateMicUi();
            UpdateIdle();
        };
        _mic.UsageChanged += (_, _) => { UpdateMicUi(); UpdateIdle(); };
        _mic.Start();

        // Cihazlar
        _devices = new DevicesService(Dispatcher);
        _devices.ConnectionChanged += (name, connected) =>
        {
            if (S.NotifyBluetooth) ShowToast("\uE702", connected ? "Bağlandı" : "Bağlantı kesildi", name);
        };
        _devices.LowBattery += b => ShowToast(BatteryIcon(b.Percent), "Pil azaldı", $"{b.Name} · %{b.Percent}");
        _devices.BatteriesChanged += RebuildDevices;
        _devices.Start();

        // Hava durumu
        _weather.Updated += UpdateWeatherUi;
        _lastCity = S.WeatherCity;
        _weather.Start();

        // Sarki sozleri
        _media.Changed += () => { if (_media.HasMedia) _ = _lyrics.Load(_media.Title, _media.Artist, _media.Duration); };
        _lyrics.Changed += UpdateLyricUi;
        _lyricTimer.Tick += (_, _) => UpdateLyricUi();
        _lyricTimer.Start();

        // Oyun gostergesi
        _ping.Updated += _ => Dispatcher.BeginInvoke(UpdateHud);
        _ping.Start();
        _clock.Tick += (_, _) => { if (HudActive) UpdateHud(); };
        FpsHelper.StartIfInstalled();

        // Ses tuslari: Windows ses kutusu yerine notch
        _volKeys.Enabled = S.HideWindowsVolumeOsd;
        _volKeys.Install();

        // Windows bildirimleri
        _winNotify.Received += n => Dispatcher.BeginInvoke(() => ShowWinNotification(n));
        ApplyBanners();
        _winNotify.Start();
        _fpsPoll.Tick += (_, _) =>
        {
            if (!HudActive) { _fps = null; return; }
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
            _fps = FpsHelper.Read((int)pid);
        };
        _fpsPoll.Start();

        // Karistirici
        _mixerRefresh.Tick += (_, _) => { RefreshMixer(); _ = RefreshSonar(false); };

        // Notlar
        try { NotesBox.Text = File.Exists(NotesPath) ? File.ReadAllText(NotesPath) : ""; } catch { }
        NotesHint.Visibility = NotesBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _notesSave.Tick += (_, _) => { _notesSave.Stop(); SaveNotes(); };
        Deactivated += (_, _) => EndTyping();

        CleanupCaptures();
        StartAutoProfile();
        StartMore();
        UpdateFocusUi();
        UpdateTimerMode();
        UpdateMicUi();
        UpdateWeatherUi();
    }

    void StopFeatures()
    {
        SaveNotes();
        _autoProfile.Stop();
        StopMore();
        _mic.Dispose();
        _devices?.Stop();
        _ping.Dispose();
        _winNotify.Dispose();
        BannerControl.RestoreAll(); // Notch kapaninca Windows bildirim kutulari geri gelir
        _volKeys.Dispose();
        _mixer.Dispose();
    }

    void OnFeatureSettingsChanged()
    {
        ApplyAppearance();
        if (_lastCity != S.WeatherCity) { _lastCity = S.WeatherCity; _ = _weather.Refresh(); }
        _ping.Active = HudActive;
        _volKeys.Enabled = S.HideWindowsVolumeOsd;
        ApplyBanners();
        _stats.Active = StatsNeeded();
        if (_media.HasMedia) _ = _lyrics.Load(_media.Title, _media.Artist, _media.Duration);
        UpdateLyricUi();
        UpdateTimerMode();
        UpdateWeatherUi();
        UpdateFocusUi();
        _outlook.Refresh();
        UpdateIdle();
    }

    void OnTabShown()
    {
        bool audio = TabAudio.IsChecked == true;
        if (audio) { _appsSig = ""; RefreshMixer(); _ = RefreshSonar(true); _mixerRefresh.Start(); }
        else _mixerRefresh.Stop();
        if (TabDevices.IsChecked == true && _devices != null) _ = _devices.RefreshBatteries();
        if (TabTools.IsChecked == true) UpdateTimerMode();
        if (TabCal.IsChecked == true) UpdateWeatherUi();
        if (TabNotes.IsChecked != true) EndTyping();
        OnMoreTabShown();
    }

    // ================================================================
    //  Bosta gorunum ekleri (mikrofon, hava, oyun gostergesi)
    // ================================================================

    double IdleWidth()
    {
        if (HudActive && _mode == Mode.Idle) return 330;
        double w = 140;
        if (_media.HasMedia || _timer.Active) w += 100;
        if (IdleWeather.Visibility == Visibility.Visible) w += 56;
        if (IdleMic.Visibility == Visibility.Visible) w += 22;
        return w + IdleMoreWidth();
    }

    void UpdateIdleExtras()
    {
        bool hud = HudActive;
        IdleHud.Visibility = hud ? Visibility.Visible : Visibility.Collapsed;
        IdleClock.Visibility = hud ? Visibility.Collapsed : Visibility.Visible;
        if (hud)
        {
            IdleArt.Visibility = IdleLeftIcon.Visibility = EqBars.Visibility = IdleTimer.Visibility = Visibility.Collapsed;
            UpdateHud();
        }

        // Mikrofon: sessiz = kirmizi, kullanimda = turuncu
        if (S.ShowMicIndicator && (_mic.Muted || _mic.InUse))
        {
            IdleMic.Visibility = Visibility.Visible;
            IdleMic.Text = _mic.Muted ? "\uEC54" : "\uE720";
            IdleMic.Foreground = new SolidColorBrush(_mic.Muted ? Color.FromRgb(0xFF, 0x45, 0x3A) : Color.FromRgb(0xFF, 0x9F, 0x0A));
        }
        else IdleMic.Visibility = Visibility.Collapsed;

        UpdateIdleMore();

        var wx = _weather.Current;
        if (!hud && S.ShowWeatherIdle && wx != null)
        {
            IdleWeather.Visibility = Visibility.Visible;
            IdleWeather.Text = $"{WeatherService.Icon(wx.Code, wx.IsDay)} {wx.Temp:0}°";
        }
        else IdleWeather.Visibility = Visibility.Collapsed;
    }

    void UpdateHud()
    {
        if (!HudActive) return;
        var s = _lastStats;
        var parts = new List<string?>
        {
            s != null ? $"CPU %{s.Cpu:0}" : "CPU –",
            s != null ? $"GPU %{s.Gpu:0}" + (s.GpuTemp is int t ? $" {t}°" : "") : "GPU –",
            _fps is int f ? $"{f} FPS" : null,
            _ping.LastMs is int ms ? $"{ms} ms" : "– ms",
            DateTime.Now.ToString("HH:mm", Tr),
        };
        IdleHud.Text = string.Join("   ·   ", parts.Where(x => x != null));
    }

    // ================================================================
    //  Windows bildirimleri
    // ================================================================

    void ApplyBanners()
    {
        if (S.NotifyWindows && S.SuppressWindowsBanners) BannerControl.ApplyAll();
        else BannerControl.RestoreAll();
    }

    void ShowWinNotification(WinNotification n)
    {
        if (!S.NotifyWindows) return;
        // Yeni bir uygulamadan ilk bildirim geldiyse onun da Windows kutusunu kapat
        if (S.SuppressWindowsBanners) BannerControl.Suppress(n.Aumid);
        var blocked = S.NotifyBlockList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (blocked.Any(b => n.App.Contains(b, StringComparison.CurrentCultureIgnoreCase))) return;
        AddToHistory(n);
        if (FocusActive) return; // odak modu: yalnizca gecmise kaydet

        string title, sub;
        if (S.NotifyShowContent)
        {
            title = $"{n.App} · {n.Title}";
            sub = n.Body.Length > 0 ? n.Body : "Yeni bildirim";
        }
        else
        {
            title = n.App;
            sub = "Yeni bildirim";
        }
        var avatar = S.NotifyShowContent && n.ImagePath != null ? LoadThumb(n.ImagePath) : null;
        ShowToast("", title, sub, avatar);
    }

    // ================================================================
    //  Ses karistirici
    // ================================================================

    string _appsSig = "";
    List<AppAudio> _apps = new();
    bool _updatingMixer;
    readonly Dictionary<string, (Slider Sl, TextBlock Val, Button Mute)> _appRows = new();

    void RefreshMixer()
    {
        if (_mode != Mode.Expanded || TabAudio.IsChecked != true) return;
        List<OutputDevice> outs;
        try { outs = _mixer.GetOutputs(); _apps = _mixer.GetApps(); }
        catch { return; }
        RebuildOutputs(outs);

        var def = outs.FirstOrDefault(o => o.IsDefault);
        var sig = string.Join("|", _apps.Select(a => a.Key)) + "#" + def?.Id;
        if (sig != _appsSig)
        {
            _appsSig = sig;
            RebuildApps(def);
            return;
        }

        // Yalnizca degerleri guncelle (surukleneni elleme)
        _updatingMixer = true;
        if (def != null && _appRows.TryGetValue("\u0001master", out var mr) && !mr.Sl.IsMouseCaptureWithin)
            SetRow(mr, def.Volume / 100.0, def.Muted);
        foreach (var a in _apps)
            if (_appRows.TryGetValue(a.Key, out var r) && !r.Sl.IsMouseCaptureWithin) SetRow(r, a.Volume, a.Muted);
        _updatingMixer = false;
    }

    static void SetRow((Slider Sl, TextBlock Val, Button Mute) r, double v, bool muted)
    {
        r.Sl.Value = v * 100;
        r.Val.Text = $"{v * 100:0}";
        r.Mute.Content = muted ? "\uE74F" : "\uE767";
        r.Mute.Foreground = muted ? MutedBrush : SubBrush;
    }

    void RebuildOutputs(List<OutputDevice> outs)
    {
        OutList.Children.Clear();
        foreach (var o in outs)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock
            {
                Text = o.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = o.IsDefault ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = o.IsDefault ? (Brush)FindResource("Fg") : SubBrush,
            });
            if (o.IsDefault)
            {
                var check = new TextBlock { Text = "\uE73E", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = (Brush)FindResource("Accent"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(check, 1);
                g.Children.Add(check);
            }
            var btn = new Button { Style = (Style)FindResource("ClipItem"), Content = g, ToolTip = o.IsDefault ? "Varsayılan çıkış" : "Varsayılan yap" };
            var id = o.Id;
            btn.Click += (_, _) =>
            {
                try { _mixer.SetDefaultOutput(id); } catch { }
                _appsSig = "";
                RefreshMixer();
            };
            OutList.Children.Add(btn);
        }
    }

    void RebuildApps(OutputDevice? def)
    {
        AppList.Children.Clear();
        _appRows.Clear();
        if (def != null)
        {
            var id = def.Id;
            AddMixerRow("\u0001master", def.Name, null, "\uE767", def.Volume / 100.0, def.Muted,
                v => _mixer.SetDeviceVolume(id, (float)v), m => _mixer.SetDeviceMute(id, m), bold: true);
        }
        foreach (var a in _apps)
        {
            var key = a.Key;
            AddMixerRow(key, a.Name, a.Icon, null, a.Volume, a.Muted,
                v => { var cur = _apps.FirstOrDefault(x => x.Key == key); if (cur != null) AudioMixerService.SetAppVolume(cur, (float)v); },
                m => { var cur = _apps.FirstOrDefault(x => x.Key == key); if (cur != null) AudioMixerService.SetAppMute(cur, m); });
        }
        if (_apps.Count == 0)
            AppList.Children.Add(new TextBlock { Text = "Şu an ses çalan uygulama yok", Foreground = SubBrush, FontSize = 12, Margin = new Thickness(4, 10, 0, 0) });
    }

    void AddMixerRow(string key, string name, ImageSource? icon, string? glyph, double vol, bool muted,
                     Action<double> setVol, Action<bool> setMute, bool bold = false)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1), Height = 30 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

        UIElement ic = icon != null
            ? new Image { Source = icon, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center }
            : new TextBlock { Text = glyph ?? "\uE8D6", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = SubBrush };
        g.Children.Add(ic);

        var label = new TextBlock
        {
            Text = name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(4, 0, 6, 0), ToolTip = name,
        };
        Grid.SetColumn(label, 1);
        g.Children.Add(label);

        var sl = new Slider { Style = (Style)FindResource("FlatSlider"), Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sl, 2);
        g.Children.Add(sl);

        var val = new TextBlock { FontSize = 11.5, Foreground = SubBrush, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(val, 3);
        g.Children.Add(val);

        var mute = new Button { Style = (Style)FindResource("IconButton"), Width = 26, Height = 26, FontSize = 12, ToolTip = "Sessize al" };
        Grid.SetColumn(mute, 4);
        g.Children.Add(mute);

        var row = (sl, val, mute);
        _appRows[key] = row;
        _updatingMixer = true;
        SetRow(row, vol, muted);
        _updatingMixer = false;

        bool isMuted = muted;
        sl.ValueChanged += (_, e) =>
        {
            val.Text = $"{e.NewValue:0}";
            if (!_updatingMixer) setVol(e.NewValue / 100.0);
        };
        mute.Click += (_, _) =>
        {
            isMuted = !isMuted;
            setMute(isMuted);
            SetRow(row, sl.Value / 100.0, isMuted);
        };
        AppList.Children.Add(g);
    }

    // ================================================================
    //  Mikrofon
    // ================================================================

    void Mic_Click(object sender, RoutedEventArgs e) => _mic.Toggle();

    void UpdateMicUi()
    {
        MicName.Text = string.IsNullOrEmpty(_mic.DeviceName) ? "Mikrofon" : _mic.DeviceName;
        MicState.Text = _mic.Muted ? "Sessiz · tıkla aç"
            : _mic.InUse ? "Kullanımda: " + string.Join(", ", _mic.Users)
            : "Açık · tıkla sessize al";
        MicBtn.Content = _mic.Muted ? "\uEC54" : "\uE720";
        MicBtn.Foreground = _mic.Muted ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : (Brush)FindResource("Fg");
        MicBtn.ToolTip = MicHotkeyError == null && S.MicHotkey.Length > 0 ? $"Aç/kapa ({S.MicHotkey})" : "Aç/kapa";
    }

    // ================================================================
    //  Cihazlar ve pil
    // ================================================================

    static string BatteryIcon(int pct) => ((char)(0xEBA0 + Math.Clamp((int)Math.Round(pct / 10.0), 0, 10))).ToString();

    void RebuildDevices()
    {
        DeviceList.Children.Clear();
        var list = _devices?.Batteries ?? new List<BatteryDevice>();
        foreach (var b in list)
        {
            bool low = b.Percent <= S.LowBatteryPercent && !b.Charging;
            var color = low ? Color.FromRgb(0xFF, 0x45, 0x3A) : ((SolidColorBrush)FindResource("Accent")).Color;
            var bar = new Grid { Height = 4, Margin = new Thickness(0, 8, 0, 0) };
            bar.Children.Add(new Border { Background = (Brush)FindResource("Track"), CornerRadius = new CornerRadius(2) });
            bar.Children.Add(new Border
            {
                Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left,
                Width = 126 * b.Percent / 100.0,
            });
            var tile = new Border
            {
                Style = (Style)FindResource("TileBorder"), Width = 150, Height = 96,
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = b.IsSystem ? "\uE7F8" : "\uE702", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Foreground = SubBrush },
                        new TextBlock { Text = b.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0), ToolTip = b.Name },
                        new TextBlock { Text = $"%{b.Percent}" + (b.Charging ? " ⚡" : ""), FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(low ? color : Colors.White) },
                        bar,
                    },
                },
            };
            DeviceList.Children.Add(tile);
        }
        DevicesEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================
    //  Hava durumu
    // ================================================================

    void UpdateWeatherUi()
    {
        var wx = _weather.Current;
        if (wx == null)
        {
            WxContent.Visibility = Visibility.Collapsed;
            WxEmpty.Visibility = Visibility.Visible;
            WxEmpty.Text = _weather.Error ?? (S.WeatherCity.Length == 0 ? "Hava durumu için Ayarlar'dan şehir gir" : "Yükleniyor…");
        }
        else
        {
            WxContent.Visibility = Visibility.Visible;
            WxEmpty.Visibility = Visibility.Collapsed;
            WxCity.Text = wx.City;
            WxIcon.Text = WeatherService.Icon(wx.Code, wx.IsDay);
            WxTemp.Text = $"{wx.Temp:0}°";
            WxDesc.Text = WeatherService.Describe(wx.Code);
            WxDetail.Text = $"Hissedilen {wx.Feels:0}° · Nem %{wx.Humidity} · {wx.Wind:0} km/s";
            WxHours.Children.Clear();
            var now = DateTime.Now;
            foreach (var h in wx.Hours.Where(h => h.Time > now).Take(4))
            {
                WxHours.Children.Add(new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = h.Time.ToString("HH:mm", Tr), FontSize = 10, Foreground = SubBrush, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = WeatherService.Icon(h.Code, h.Time.Hour is >= 7 and < 19), FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = $"{h.Temp:0}°", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                });
            }
        }
        UpdateIdle();
    }

    // ================================================================
    //  Sarki sozleri
    // ================================================================

    void UpdateLyricUi()
    {
        string? line = S.Lyrics && _media.HasMedia ? _lyrics.LineAt(_media.Position) : null;
        if (line == null)
        {
            if (MediaLyric.Visibility != Visibility.Collapsed) MediaLyric.Visibility = Visibility.Collapsed;
            return;
        }
        if (MediaLyric.Visibility != Visibility.Visible) MediaLyric.Visibility = Visibility.Visible;
        if (MediaLyric.Text != line)
        {
            MediaLyric.Text = line;
            if (_mode == Mode.Expanded) { MediaLyric.Opacity = 0; Fade(MediaLyric, 1, 250); }
        }
    }

    // ================================================================
    //  Pomodoro
    // ================================================================

    bool _pomo, _pomoBreak;
    int _pomoCycle;

    void ModeTimer_Click(object sender, RoutedEventArgs e) { _pomo = false; _timer.Reset(); ResetPomo(); }
    void ModePomo_Click(object sender, RoutedEventArgs e) { _pomo = true; _timer.Reset(); ResetPomo(); }

    void ResetPomo()
    {
        _pomoBreak = false;
        _pomoCycle = 0;
        UpdateTimerMode();
    }

    void StartPomoPhase()
    {
        int min = !_pomoBreak ? S.PomodoroWork
                : (_pomoCycle > 0 && _pomoCycle % 4 == 0) ? S.PomodoroLongBreak : S.PomodoroBreak;
        _timer.Start(TimeSpan.FromMinutes(Math.Max(1, min)));
        UpdateTimerMode();
    }

    /// <summary>Pomodoro modundaysa bir sonraki asamaya gecer ve true doner.</summary>
    bool OnPomoFinished()
    {
        if (!_pomo) return false;
        if (!_pomoBreak)
        {
            _pomoCycle++;
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            if (S.PomodoroDay != today) { S.PomodoroDay = today; S.PomodoroDone = 0; }
            S.PomodoroDone++;
            AppSettings.Save();
            _pomoBreak = true;
            bool longBreak = _pomoCycle % 4 == 0;
            ShowToast("\uE916", longBreak ? "Uzun mola zamanı" : "Mola zamanı", $"{_pomoCycle}. pomodoro tamamlandı · bugün {S.PomodoroDone}");
        }
        else
        {
            _pomoBreak = false;
            ShowToast("\uE916", "Çalışma zamanı", "Yeni pomodoro başladı");
        }
        StartPomoPhase();
        return true;
    }

    void UpdateTimerMode()
    {
        var sel = new SolidColorBrush(Color.FromArgb(0x55, BaseAccent.R, BaseAccent.G, BaseAccent.B));
        var off = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        ModeTimerBtn.Background = _pomo ? off : sel;
        ModePomoBtn.Background = _pomo ? sel : off;
        TimerPresets.Visibility = _pomo ? Visibility.Collapsed : Visibility.Visible;

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        int done = S.PomodoroDay == today ? S.PomodoroDone : 0;
        TimerTitle.Text = _pomo
            ? $"{(_pomoBreak ? "Mola" : "Çalışma")} · {S.PomodoroWork}/{S.PomodoroBreak} dk · bugün {done} pomodoro"
            : "";
        TimerTitle.Visibility = _pomo ? Visibility.Visible : Visibility.Collapsed;
        if (_pomo && !_timer.Active) TimerText.Text = $"{S.PomodoroWork:00}:00";
    }

    // ================================================================
    //  Notlar (yazmak icin pencerenin odak almasi gerekir)
    // ================================================================

    void Notes_MouseDown(object sender, MouseButtonEventArgs e) => BeginTyping();

    void BeginTyping()
    {
        if (_typing) return;
        _typing = true;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex & ~Native.WS_EX_NOACTIVATE);
        Activate();
        NotesBox.Focus();
    }

    void EndTyping()
    {
        if (!_typing) return;
        _typing = false;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE);
        Keyboard.ClearFocus();
        SaveNotes();
    }

    void Notes_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (NotesHint == null) return;
        NotesHint.Visibility = NotesBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _notesSave.Stop();
        _notesSave.Start();
    }

    void SaveNotes()
    {
        try { Directory.CreateDirectory(DataDir); File.WriteAllText(NotesPath, NotesBox.Text); } catch { }
    }

    // ================================================================
    //  Kopyalanan gorseller / ekran goruntuleri -> tepsi
    // ================================================================

    void OnImageCopied()
    {
        if (!S.ScreenshotToTray)
        {
            if (S.NotifyCopy) ShowToast("\uEB9F", "Görsel kopyalandı", "");
            return;
        }
        try
        {
            using var img = WinForms.Clipboard.GetImage();
            if (img == null) return;
            Directory.CreateDirectory(CapturesDir);
            var path = Path.Combine(CapturesDir, $"Görsel {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            img.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            _tray.Add(new[] { path });
            ShowToast("\uEB9F", "Görsel tepsiye eklendi", "Tepsiden sürükleyip istediğin yere bırak", LoadThumb(path));
        }
        catch { }
    }

    static BitmapSource? LoadThumb(string path)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.DecodePixelWidth = 120;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    /// <summary>Tepside olmayan, 7 gunden eski yakalamalari ve birakilan ogeleri siler.</summary>
    void CleanupCaptures()
    {
        foreach (var dir in new[] { CapturesDir, Path.Combine(DataDir, "tray-items") })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir))
                {
                    if (_tray.Items.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
                    if (File.GetCreationTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f);
                }
            }
            catch { }
        }
    }

    // ================================================================
    //  Tepsiye metin / gorsel / baglanti birakma
    // ================================================================

    static bool CanAcceptDrop(IDataObject d) =>
        d.GetDataPresent(DataFormats.FileDrop) || d.GetDataPresent(DataFormats.Bitmap) ||
        d.GetDataPresent("UniformResourceLocatorW") || d.GetDataPresent(DataFormats.UnicodeText);

    void AcceptDrop(IDataObject d)
    {
        try
        {
            if (d.GetData(DataFormats.FileDrop) is string[] paths) { _tray.Add(paths); return; }

            var dir = Path.Combine(DataDir, "tray-items");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss");

            if (d.GetData(DataFormats.Bitmap) is BitmapSource bmp)
            {
                var path = Path.Combine(dir, $"Görsel {stamp}.png");
                using (var fs = File.Create(path))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    enc.Save(fs);
                }
                _tray.Add(new[] { path });
                return;
            }

            if (d.GetData("UniformResourceLocatorW") is MemoryStream ms)
            {
                var url = System.Text.Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0').Trim();
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    var name = string.Concat(uri.Host.Split(Path.GetInvalidFileNameChars()));
                    var path = Path.Combine(dir, $"{name} {stamp}.url");
                    File.WriteAllText(path, $"[InternetShortcut]\r\nURL={url}\r\n");
                    _tray.Add(new[] { path });
                    return;
                }
            }

            if (d.GetData(DataFormats.UnicodeText) is string text && text.Trim().Length > 0)
            {
                var path = Path.Combine(dir, $"Metin {stamp}.txt");
                File.WriteAllText(path, text);
                _tray.Add(new[] { path });
            }
        }
        catch { }
    }

    // ================================================================
    //  Gorunum, ekran ve konum
    // ================================================================

    static Color? ParseColor(string? s)
    {
        try { return s != null ? (Color)ColorConverter.ConvertFromString(s) : null; } catch { return null; }
    }

    void ApplyAppearance()
    {
        _baseAccent = ParseColor(S.AccentColor) ?? DefaultAccent;
        var brush = new SolidColorBrush(_baseAccent);
        brush.Freeze();
        Application.Current.Resources["Accent"] = brush;
        if (!(S.AccentFromArt && _media.ArtColor != null)) SetAccent(_baseAccent);

        double scale = Math.Clamp(S.Scale, 0.8, 1.4);
        NotchScaleT.ScaleX = NotchScaleT.ScaleY = scale;
        Width = 780 * scale;
        Height = 272 * scale;
        RootGrid.Opacity = Math.Clamp(S.Opacity, 0.4, 1);
        Dispatcher.BeginInvoke(PositionWindow, DispatcherPriority.Background);
    }

    static WinForms.Screen TargetScreen() =>
        WinForms.Screen.AllScreens.FirstOrDefault(x => x.DeviceName == AppSettings.Current.Monitor) ?? WinForms.Screen.PrimaryScreen!;

    void PositionWindow()
    {
        if (_hwnd == IntPtr.Zero) return;
        var scr = TargetScreen().Bounds;
        if (!Native.GetWindowRect(_hwnd, out var r)) return;
        int w = r.Right - r.Left;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int maxOff = Math.Max(0, (int)(scr.Width / 2 - 120 * dpi));
        int off = Math.Clamp((int)(S.OffsetX * dpi), -maxOff, maxOff);
        int x = scr.Left + (scr.Width - w) / 2 + off;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, scr.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // Notch'u tasima kisayollari (Ayarlar > Kisayollar; varsayilan atanmamis)
    readonly Dictionary<string, string?> _hkErrors = new();

    /// <summary>Ayarlar ekrani icin: ilgili kisayolun kayit hatasi (yoksa null).</summary>
    public string? HotkeyError(string setting) => setting switch
    {
        nameof(AppSettings.ToggleHotkey) => ToggleHotkeyError,
        nameof(AppSettings.ClipboardHotkey) => ClipHotkeyError,
        nameof(AppSettings.MicHotkey) => MicHotkeyError,
        _ => _hkErrors.TryGetValue(setting, out var e) ? e : null,
    };

    void RegisterMoveHotkeys()
    {
        UnregisterMoveHotkeys();
        _hkErrors[nameof(AppSettings.MoveLeftHotkey)] = Register(HotkeyMoveLeftId, S.MoveLeftHotkey, repeat: true);
        _hkErrors[nameof(AppSettings.MoveRightHotkey)] = Register(HotkeyMoveRightId, S.MoveRightHotkey, repeat: true);
        _hkErrors[nameof(AppSettings.MoveCenterHotkey)] = Register(HotkeyMoveUpId, S.MoveCenterHotkey);
        _hkErrors[nameof(AppSettings.NextMonitorHotkey)] = Register(HotkeyMoveDownId, S.NextMonitorHotkey);
        _hkErrors[nameof(AppSettings.FocusHotkey)] = Register(HotkeyFocusId, S.FocusHotkey);
    }

    void UnregisterMoveHotkeys()
    {
        for (int id = HotkeyMoveLeftId; id <= HotkeyMoveDownId; id++) Native.UnregisterHotKey(_hwnd, id);
        Native.UnregisterHotKey(_hwnd, HotkeyFocusId);
    }

    void OnMoveHotkey(int id)
    {
        var scr = TargetScreen();
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int maxOff = (int)(scr.Bounds.Width / dpi / 2 - 120);
        switch (id)
        {
            case HotkeyMoveLeftId: S.OffsetX = Math.Max(-maxOff, S.OffsetX - 40); break;
            case HotkeyMoveRightId: S.OffsetX = Math.Min(maxOff, S.OffsetX + 40); break;
            case HotkeyMoveUpId: S.OffsetX = 0; break;
            case HotkeyMoveDownId:
                var all = WinForms.Screen.AllScreens;
                if (all.Length < 2) return;
                int i = Array.FindIndex(all, x => x.DeviceName == scr.DeviceName);
                var next = all[(i + 1) % all.Length];
                S.Monitor = next.Primary ? "" : next.DeviceName;
                S.OffsetX = 0;
                AppSettings.Save();
                Dispatcher.BeginInvoke(() => ShowToast("\uE7F4", "Notch taşındı", ScreenLabel(next)), DispatcherPriority.Background);
                return;
        }
        AppSettings.Save();
    }

    public static string ScreenLabel(WinForms.Screen s)
    {
        var n = s.DeviceName.Replace(@"\\.\DISPLAY", "Ekran ");
        return s.Primary ? n + " (ana)" : n;
    }
}
