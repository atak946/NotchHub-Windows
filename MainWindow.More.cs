using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Notch.Services;

namespace Notch;

/// <summary>
/// Ajanda (Outlook), bildirim gecmisi, odak modu, indirmeler, kamera, parlaklik, ag/VPN,
/// sistem uyarilari ve guncelleme.
/// </summary>
public partial class MainWindow
{
    const int HotkeyFocusId = 0x4E04;

    readonly OutlookService _outlook = new();
    DownloadsWatcher? _downloads;
    readonly CameraWatcher _camera = new();
    NetworkWatcher? _network;
    readonly BrightnessService _brightness = new();
    readonly DispatcherTimer _health = new() { Interval = TimeSpan.FromSeconds(15) };

    readonly List<WinNotification> _history = new();
    readonly HashSet<string> _reminded = new();
    bool _alertGpu, _alertRam, _alertDisk;
    bool _lastFocus;

    public Installer.UpdateInfo? PendingUpdate { get; private set; }

    // ================================================================
    //  Baslatma
    // ================================================================

    void StartMore()
    {
        // Outlook takvimi
        _outlook.Updated += () => Dispatcher.BeginInvoke(() => { RebuildAgenda(); UpdateIdle(); });
        _outlook.Start();
        _clock.Tick += (_, _) => OnMoreClock();

        // Indirmeler
        _downloads = new DownloadsWatcher(Dispatcher);
        _downloads.Completed += path => Dispatcher.BeginInvoke(() => OnDownloadCompleted(path));
        _downloads.Start();

        // Kamera
        _camera.Changed += (inUse, users) =>
        {
            if (S.NotifyCamera && inUse) ShowToast("", "Kamera açıldı", string.Join(", ", users));
            UpdateIdle();
        };
        _camera.Start();

        // Ag / VPN
        _network = new NetworkWatcher(Dispatcher);
        _network.OnlineChanged += online => { if (S.NotifyNetwork) ShowToast(online ? "" : "", online ? "İnternet geri geldi" : "İnternet bağlantısı koptu", ""); };
        _network.VpnChanged += (name, up) => { if (S.NotifyNetwork) ShowToast("", up ? "VPN bağlandı" : "VPN bağlantısı kesildi", name); };
        _network.Start();

        // Sistem uyarilari
        _health.Tick += (_, _) => CheckHealth();
        _health.Start();

        // Guncelleme
        if (S.AutoCheckUpdates) _ = CheckUpdatesAsync(silent: true);

        RebuildHistory();
        RebuildAgenda();
    }

    void StopMore()
    {
        _outlook.Dispose();
        _downloads?.Dispose();
        _brightness.Dispose();
        _health.Stop();
    }

    void OnMoreTabShown()
    {
        if (TabAgenda.IsChecked == true) { _outlook.Refresh(); RebuildAgenda(); }
        if (TabHistory.IsChecked == true) RebuildHistory();
        if (TabDevices.IsChecked == true) _ = LoadBrightness();
        PanelAgenda.Visibility = TabAgenda.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelHistory.Visibility = TabHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnMoreClock()
    {
        // Odak modu otomatik degisebilir (pomodoro / toplanti): gostergeyi guncelle
        bool f = FocusActive;
        if (f != _lastFocus) { _lastFocus = f; UpdateFocusUi(); UpdateIdle(); }

        // Toplanti hatirlatmasi
        int min = S.MeetingReminderMin;
        if (min > 0)
        {
            foreach (var e in _outlook.Events.Where(e => !e.AllDay))
            {
                var left = e.Start - DateTime.Now;
                if (left > TimeSpan.Zero && left <= TimeSpan.FromMinutes(min) && _reminded.Add(e.Key))
                    ShowToast("", $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} dk sonra: {e.Subject}",
                        e.JoinUrl != null ? "Katılmak için notch'u aç → Ajanda" : e.Location);
            }
        }
        if (DateTime.Now.Second == 0) UpdateIdle(); // toplanti geri sayimi dakikada bir
    }

    // ================================================================
    //  Bosta gostergeler
    // ================================================================

    double IdleMoreWidth()
    {
        double w = 0;
        if (IdleMeeting.Visibility == Visibility.Visible) w += 70;
        if (IdleFocus.Visibility == Visibility.Visible) w += 22;
        if (IdleCam.Visibility == Visibility.Visible) w += 16;
        return w;
    }

    void UpdateIdleMore()
    {
        IdleFocus.Visibility = FocusActive ? Visibility.Visible : Visibility.Collapsed;
        IdleCam.Visibility = S.ShowCameraIndicator && _camera.InUse ? Visibility.Visible : Visibility.Collapsed;

        var next = _outlook.NextOrCurrent();
        if (next != null && !HudActive)
        {
            var left = next.Start - DateTime.Now;
            if (next.IsNow) { IdleMeeting.Text = "● Toplantıda"; IdleMeeting.Visibility = Visibility.Visible; }
            else if (left <= TimeSpan.FromMinutes(15)) { IdleMeeting.Text = $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} dk"; IdleMeeting.Visibility = Visibility.Visible; }
            else IdleMeeting.Visibility = Visibility.Collapsed;
            IdleMeeting.ToolTip = next.Subject;
        }
        else IdleMeeting.Visibility = Visibility.Collapsed;
    }

    // ================================================================
    //  Odak modu
    // ================================================================

    bool FocusActive =>
        S.FocusMode ||
        (S.FocusDuringPomodoro && _pomo && _timer.Running && !_pomoBreak) ||
        (S.FocusDuringMeetings && _outlook.Events.Any(e => !e.AllDay && e.Busy && e.IsNow));

    void Focus_Click(object sender, RoutedEventArgs e) => ToggleFocus();

    public void ToggleFocus()
    {
        S.FocusMode = !S.FocusMode;
        AppSettings.Save();
        ShowToastForce("", S.FocusMode ? "Odak modu açık" : "Odak modu kapalı",
            S.FocusMode ? "Bildirimler sessizce geçmişe kaydedilir" : "");
    }

    void UpdateFocusUi()
    {
        bool f = FocusActive;
        FocusBtn.Foreground = f ? new SolidColorBrush(Color.FromRgb(0x5E, 0x5C, 0xE6)) : SubBrush;
        FocusBtn.ToolTip = f ? (S.FocusMode ? "Odak modu açık (kapatmak için tıkla)" : "Odak modu otomatik açık (pomodoro/toplantı)")
                             : "Odak modu: bildirimleri sustur";
    }

    /// <summary>Odak modundan bagimsiz gosterilen bildirim (odak modunun kendi bildirimi gibi).</summary>
    void ShowToastForce(string icon, string title, string sub)
    {
        _toastQueue.AddFirst(new ToastItem(icon, title, sub, null, "system", DateTime.Now));
        PumpToasts();
    }

    // ================================================================
    //  Bildirim gecmisi
    // ================================================================

    void AddToHistory(WinNotification n)
    {
        _history.Insert(0, n);
        if (_history.Count > 50) _history.RemoveAt(_history.Count - 1);
        if (TabHistory.IsChecked == true) RebuildHistory();
    }

    void RebuildHistory()
    {
        HistoryList.Children.Clear();
        foreach (var n in _history)
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var head = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
            head.Inlines.Add(new System.Windows.Documents.Run(n.App) { Foreground = (Brush)FindResource("Accent"), FontWeight = FontWeights.SemiBold });
            head.Inlines.Add(new System.Windows.Documents.Run("  " + n.Title) { FontWeight = FontWeights.SemiBold });
            g.Children.Add(head);
            var time = new TextBlock { Text = n.Time.ToString("HH:mm", Tr), FontSize = 11, Foreground = SubBrush, HorizontalAlignment = HorizontalAlignment.Right };
            g.Children.Add(time);
            if (n.Body.Length > 0)
            {
                var body = new TextBlock { Text = n.Body, FontSize = 12, Foreground = SubBrush, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34, Margin = new Thickness(0, 2, 0, 0) };
                Grid.SetRow(body, 1);
                g.Children.Add(body);
            }
            var btn = new Button { Style = (Style)FindResource("ClipItem"), Content = g, Margin = new Thickness(0, 0, 0, 2), ToolTip = "Uygulamayı aç" };
            var aumid = n.Aumid;
            btn.Click += (_, _) => OpenApp(aumid);
            HistoryList.Children.Add(btn);
        }
        HistoryEmpty.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryHeader.Text = $"Son {_history.Count} bildirim · tıkla: uygulamayı aç" + (FocusActive ? " · odak modu açık" : "");
    }

    void HistoryClear_Click(object sender, RoutedEventArgs e) { _history.Clear(); RebuildHistory(); }

    static void OpenApp(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{aumid}") { UseShellExecute = true }); } catch { }
    }

    // ================================================================
    //  Ajanda (Outlook)
    // ================================================================

    void RebuildAgenda()
    {
        AgendaList.Children.Clear();
        var events = _outlook.Events;
        string? lastDay = null;
        foreach (var e in events)
        {
            string day = e.Start.Date == DateTime.Today ? "BUGÜN" : e.Start.Date == DateTime.Today.AddDays(1) ? "YARIN" : e.Start.ToString("d MMMM", Tr).ToUpper(Tr);
            if (e.AllDay && e.Start.Date < DateTime.Today) day = "BUGÜN";
            if (day != lastDay)
            {
                AgendaList.Children.Add(new TextBlock { Text = day, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = SubBrush, Margin = new Thickness(4, lastDay == null ? 0 : 10, 0, 4) });
                lastDay = day;
            }
            bool past = e.End <= DateTime.Now;
            var g = new Grid { Margin = new Thickness(0, 0, 0, 4), Opacity = past ? 0.45 : 1 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = e.IsNow ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromRgb(0xBF, 0x5A, 0xF2)) });

            var when = new TextBlock
            {
                Text = e.AllDay ? "Tüm gün" : $"{e.Start:HH:mm} – {e.End:HH:mm}", FontSize = 12, Margin = new Thickness(10, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center, Foreground = e.IsNow ? (Brush)FindResource("Accent") : SubBrush,
                FontWeight = e.IsNow ? FontWeights.SemiBold : FontWeights.Normal,
            };
            Grid.SetColumn(when, 1);
            g.Children.Add(when);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = e.Subject, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (e.Location.Length > 0 && !e.Location.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                info.Children.Add(new TextBlock { Text = e.Location, FontSize = 11, Foreground = SubBrush, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(info, 2);
            g.Children.Add(info);

            if (e.JoinUrl != null && !past)
            {
                var join = new Button
                {
                    Style = (Style)FindResource("PillButton"), Content = "Katıl", Margin = new Thickness(8, 0, 0, 0), Height = 28,
                    Background = e.IsNow || e.Start - DateTime.Now < TimeSpan.FromMinutes(15) ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                    ToolTip = e.JoinUrl,
                };
                var url = e.JoinUrl;
                join.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
                Grid.SetColumn(join, 3);
                g.Children.Add(join);
            }
            AgendaList.Children.Add(new Border { Style = (Style)FindResource("TileBorder"), Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(8, 8, 10, 8), Child = g });
        }
        AgendaEmpty.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AgendaEmptyText.Text = !S.OutlookCalendar ? "Outlook takvimi Ayarlar'dan kapalı"
                             : _outlook.Status ?? "Bugün ve yarın için etkinlik yok";
    }

    // ================================================================
    //  Indirmeler
    // ================================================================

    void OnDownloadCompleted(string path)
    {
        if (S.DownloadsToTray) _tray.Add(new[] { path });
        if (!S.NotifyDownloads) return;
        var name = Path.GetFileName(path);
        ShowToast("", "İndirme tamamlandı", S.DownloadsToTray ? name + " · tepside" : name,
                  Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" ? LoadThumb(path) : null);
    }

    // ================================================================
    //  Monitor parlakligi
    // ================================================================

    async System.Threading.Tasks.Task LoadBrightness()
    {
        var mons = await _brightness.LoadAsync();
        BrightnessList.Children.Clear();
        foreach (var m in mons)
        {
            var g = new Grid { Height = 34, Margin = new Thickness(4, 0, 4, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14, Foreground = SubBrush, VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            label.Inlines.Add(new System.Windows.Documents.Run(m.Name));
            if (m.Software)
            {
                label.Inlines.Add(new System.Windows.Documents.Run("  yazılım") { FontSize = 10.5, Foreground = SubBrush });
                label.ToolTip = "Monitör DDC/CI'ye yanıt vermiyor; görüntü yazılımla karartılıyor (arka ışık değişmez). Monitör menüsünde DDC/CI'yi açarsan gerçek parlaklık kullanılır.";
            }
            Grid.SetColumn(label, 1);
            g.Children.Add(label);
            var sl = new Slider { Style = (Style)FindResource("FlatSlider"), Minimum = m.Min, Maximum = m.Max, Value = m.Current, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(sl, 2);
            g.Children.Add(sl);
            var val = new TextBlock { Text = $"{m.Current}", FontSize = 12, Foreground = SubBrush, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(val, 3);
            g.Children.Add(val);
            var mon = m;
            sl.ValueChanged += (_, e) => { val.Text = $"{e.NewValue:0}"; _brightness.Set(mon, (uint)Math.Round(e.NewValue)); };
            BrightnessList.Children.Add(g);
        }
        if (mons.Count == 0)
            BrightnessList.Children.Add(new TextBlock { Text = "Parlaklığı ayarlanabilen monitör bulunamadı", FontSize = 11.5, Foreground = SubBrush, Margin = new Thickness(6, 0, 0, 8) });
        DevicesEmpty.Visibility = Visibility.Collapsed;
        if ((_devices?.Batteries.Count ?? 0) == 0)
            DeviceList.Children.Add(new TextBlock { Text = "Pil bilgisi bildiren Bluetooth cihaz yok", FontSize = 11.5, Foreground = SubBrush, Margin = new Thickness(6, 4, 0, 0) });
    }

    // ================================================================
    //  Sistem uyarilari
    // ================================================================

    void CheckHealth()
    {
        if (!S.HealthAlerts) return;
        try
        {
            if (Nvml.GpuTemperature() is int t)
            {
                if (t >= S.AlertGpuTemp && !_alertGpu) { _alertGpu = true; ShowToast("", "GPU çok ısındı", $"{t}°C"); }
                else if (t < S.AlertGpuTemp - 5) _alertGpu = false;
            }

            var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
            Native.GlobalMemoryStatusEx(ref ms);
            if (ms.dwMemoryLoad >= S.AlertRamPercent && !_alertRam) { _alertRam = true; ShowToast("", "RAM neredeyse dolu", $"%{ms.dwMemoryLoad} kullanımda"); }
            else if (ms.dwMemoryLoad < S.AlertRamPercent - 5) _alertRam = false;

            var root = Path.GetPathRoot(Environment.SystemDirectory)!;
            double freeGb = new DriveInfo(root).AvailableFreeSpace / 1073741824.0;
            if (freeGb < S.AlertDiskGb && !_alertDisk) { _alertDisk = true; ShowToast("", "Disk dolmak üzere", $"{root.TrimEnd('\\')} sürücüsünde {freeGb:0.0} GB boş"); }
            else if (freeGb > S.AlertDiskGb + 2) _alertDisk = false;
        }
        catch { }
    }

    // ================================================================
    //  Guncelleme
    // ================================================================

    public async System.Threading.Tasks.Task<string> CheckUpdatesAsync(bool silent)
    {
        try
        {
            PendingUpdate = await Installer.CheckAsync();
            if (PendingUpdate == null) return $"Güncel (v{Installer.CurrentVersion.ToString(3)})";
            if (silent) ShowToast("", "Güncelleme var", $"v{PendingUpdate.Version.ToString(3)} · Ayarlar → Güncelleme");
            return $"Yeni sürüm: v{PendingUpdate.Version.ToString(3)}";
        }
        catch (Exception ex) { return "Denetlenemedi: " + ex.Message; }
    }

    public async System.Threading.Tasks.Task<string> ApplyUpdateAsync()
    {
        if (PendingUpdate == null) return "Güncelleme yok";
        if (!Installer.IsSingleFile) return "Geliştirme sürümü güncellenemez";
        try
        {
            if (await Installer.ApplyAsync(PendingUpdate))
            {
                ((App)Application.Current).QuitForRestart();
                return "Yeniden başlatılıyor…";
            }
            return "Güncelleme indirilemedi";
        }
        catch (Exception ex) { return "Güncellenemedi: " + ex.Message; }
    }

    // ================================================================
    //  Medya: ilerleme cubugunda sarma
    // ================================================================

    void MediaSeek_Click(object sender, MouseButtonEventArgs e)
    {
        var dur = _media.Duration;
        if (dur <= TimeSpan.Zero) return;
        double frac = Math.Clamp(e.GetPosition(MediaSeek).X / MediaSeek.ActualWidth, 0, 1);
        _media.Seek(TimeSpan.FromTicks((long)(dur.Ticks * frac)));
        AnimateScale(MediaScale, frac, 200);
    }
}
