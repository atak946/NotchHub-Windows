using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Notch.Services;
using WinForms = System.Windows.Forms;

namespace Notch;

/// <summary>Sayfali ayarlar ekrani. Sayfalar koddan, ortak satir bilesenleriyle olusturulur.</summary>
public partial class SettingsWindow : Window
{
    readonly MainWindow _main;
    readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    StackPanel? _rows;
    string _page = "general";
    readonly List<Action> _errorRefreshers = new();

    static AppSettings S => AppSettings.Current;
    static readonly Brush SubBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9F, 0xA8));
    static readonly Brush LineBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x23, 0x28));
    static readonly Brush ErrBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));

    // Sayfa kimligi, simge, baslik, aciklama
    static readonly (string Id, string Icon, string Title, string Sub)[] Pages =
    {
        ("general", "", "Genel", "Başlangıç ve görünürlük"),
        ("look", "", "Görünüm", "Boyut, renk, saydamlık, ekran ve konum"),
        ("interact", "", "Etkileşim", "Notch'un ne zaman açılıp kapanacağı"),
        ("keys", "", "Kısayollar", "Tüm klavye kısayolları. Kutuya tıklayıp yeni tuş birleşimine bas."),
        ("audio", "", "Ses ve mikrofon", "Ses göstergesi, Sonar ve mikrofon durumu"),
        ("sonar", "", "Sonar profilleri", "Oyun, müzik ve görüşmeye göre GAME profilini otomatik değiştirme"),
        ("notify","", "Bildirimler", "Notch'ta hangi olayların gösterileceği"),
        ("calendar", "", "Takvim ve toplantılar", "Masaüstü Outlook takvimi, hatırlatmalar ve Katıl düğmesi"),
        ("focus", "", "Odak modu", "Bildirimleri susturma ve otomatik kurallar"),
        ("media", "", "Medya ve hava durumu", "Şarkı sözleri ve hava durumu"),
        ("clip", "", "Pano ve tepsi", "Kopyalama geçmişi ve saklama tepsisi"),
        ("timer", "", "Zamanlayıcı", "Pomodoro süreleri"),
        ("health", "", "Sistem uyarıları", "Isınma, bellek ve disk uyarıları"),
        ("game", "", "Oyun", "Tam ekranda gösterilen mini performans göstergesi"),
        ("update", "", "Kurulum ve güncelleme", "Bu bilgisayara kurma ve yeni sürümleri alma"),
        ("about", "", "Hakkında", "Sürüm, geliştirici ve bağlantılar"),
    };

    public SettingsWindow(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        SourceInitialized += (_, _) => UseDarkTitleBar();
        VersionText.Text = "Ayarlar · v" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0");

        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); AppSettings.Save(); RefreshErrors(); };
        PreviewKeyDown += OnPreviewKeyDown;

        foreach (var p in Pages)
        {
            var rb = new RadioButton { Style = (Style)FindResource("NavItem"), Content = p.Title, Tag = p.Icon };
            var id = p.Id;
            rb.Checked += (_, _) => ShowPage(id);
            Nav.Children.Add(rb);
        }
        ((RadioButton)Nav.Children[0]).IsChecked = true;

        AppSettings.Changed += OnExternalChange;
        Closed += (_, _) =>
        {
            AppSettings.Changed -= OnExternalChange;
            if (_recording != null) _main.ResumeHotkeys();
            if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); AppSettings.Save(); }
        };
    }

    /// <summary>Verilen sayfayi acar (ör. "sonar").</summary>
    public void GoTo(string id)
    {
        int i = Array.FindIndex(Pages, p => p.Id == id);
        if (i >= 0) ((RadioButton)Nav.Children[i]).IsChecked = true;
    }

    // Kisayolla gizleme gibi disaridan gelen degisiklikler
    bool _suppressExternal;
    void OnExternalChange()
    {
        if (_suppressExternal || _recording != null) return;
        Dispatcher.BeginInvoke(() => { if (!_saveDebounce.IsEnabled) ShowPage(_page, keepScroll: true); });
    }

    void Save(bool immediate = false)
    {
        _suppressExternal = true;
        if (immediate) { _saveDebounce.Stop(); AppSettings.Save(); RefreshErrors(); }
        else { _saveDebounce.Stop(); _saveDebounce.Start(); }
        Dispatcher.BeginInvoke(() => _suppressExternal = false, DispatcherPriority.Background);
    }

    void RefreshErrors() { foreach (var a in _errorRefreshers) a(); }

    // ================================================================
    //  Sayfalar
    // ================================================================

    void ShowPage(string id, bool keepScroll = false)
    {
        _page = id;
        double off = PageScroll.VerticalOffset;
        var p = Pages.First(x => x.Id == id);
        PageTitle.Text = p.Title;
        PageSub.Text = p.Sub;
        PageHost.Children.Clear();
        _errorRefreshers.Clear();

        switch (id)
        {
            case "general":
                Section("Başlangıç");
                Row("Windows ile birlikte başlat", "Bilgisayar açıldığında Notch otomatik çalışır.",
                    Toggle(() => S.AutoStart, v => S.AutoStart = v));
                var assistant = new Button { Style = (Style)FindResource("PillButton"), Content = "Aç", Height = 32, Margin = new Thickness(0) };
                assistant.Click += (_, _) => _main.OpenOnboarding();
                Row("Kurulum yardımcısı", "Özelliklerin tanıtımı, temel ayarlar ve izinler.", assistant);
                Section("Görünürlük");
                Row("Notch'u gizle", $"Tamamen gizler; ses ve bildirimler de gösterilmez.{KeyHint(S.ToggleHotkey)}",
                    Toggle(() => S.Hidden, v => S.Hidden = v, immediate: true));
                Row("Tam ekranda gizle", "Oyun veya tam ekran video açıkken boştaki notch kaybolur.",
                    Toggle(() => S.HideInFullscreen, v => S.HideInFullscreen = v));
                break;

            case "look":
                Section("Boyut ve saydamlık");
                Row("Boyut", "Notch'un ve panelin ölçeği.",
                    SliderCtl(80, 140, 5, () => S.Scale * 100, v => S.Scale = v / 100, v => $"%{v:0}"));
                Row("Opaklık", "Düşük değerler notch'u yarı saydam yapar.",
                    SliderCtl(40, 100, 5, () => S.Opacity * 100, v => S.Opacity = v / 100, v => $"%{v:0}"));
                Section("Renk");
                Row("Vurgu rengi", null, Swatches());
                Row("Rengi şarkı kapağından al", "Müzik çalarken vurgu rengi kapak resmine uyar.",
                    Toggle(() => S.AccentFromArt, v => S.AccentFromArt = v));
                Section("Ekran ve konum");
                Row("Ekran", "Notch'un duracağı monitör.", MonitorPicker());
                Row("Yatay konum", "Ekranın ortasına göre kaydırma.",
                    SliderCtl(-900, 900, 20, () => S.OffsetX, v => S.OffsetX = (int)v, v => v == 0 ? "Orta" : $"{v:+0;-0} px", resetValue: 0));
                break;

            case "interact":
                Section("Açılma");
                Row("Yalnızca üst kenarda aç", "Açıkken fareyi ekranın en üst kenarına dayaman gerekir; tarayıcı sekmeleriyle çakışmaz.",
                    Toggle(() => S.EdgeOnly, v => S.EdgeOnly = v));
                Row("Açılma gecikmesi", "Panel açılmadan önce farenin beklemesi gereken süre.",
                    SliderCtl(100, 1500, 50, () => S.EdgeDwellMs, v => S.EdgeDwellMs = (int)v, v => $"{v / 1000:0.00} sn"));
                Section("Kapanma");
                Row("Kapanma gecikmesi", "Fare panelden çıktıktan sonra kapanma süresi.",
                    SliderCtl(150, 2000, 50, () => S.CloseDelayMs, v => S.CloseDelayMs = (int)v, v => $"{v / 1000:0.00} sn"));
                break;

            case "keys":
                Section("Genel");
                HotkeyRow("Notch'u göster / gizle", nameof(AppSettings.ToggleHotkey), () => S.ToggleHotkey, v => S.ToggleHotkey = v);
                HotkeyRow("Pano geçmişini aç", nameof(AppSettings.ClipboardHotkey), () => S.ClipboardHotkey, v => S.ClipboardHotkey = v);
                HotkeyRow("Mikrofonu aç / kapat", nameof(AppSettings.MicHotkey), () => S.MicHotkey, v => S.MicHotkey = v);
                HotkeyRow("Odak modunu aç / kapat", nameof(AppSettings.FocusHotkey), () => S.FocusHotkey, v => S.FocusHotkey = v);
                Section("Notch'u taşı");
                HotkeyRow("Sola kaydır", nameof(AppSettings.MoveLeftHotkey), () => S.MoveLeftHotkey, v => S.MoveLeftHotkey = v);
                HotkeyRow("Sağa kaydır", nameof(AppSettings.MoveRightHotkey), () => S.MoveRightHotkey, v => S.MoveRightHotkey = v);
                HotkeyRow("Ortala", nameof(AppSettings.MoveCenterHotkey), () => S.MoveCenterHotkey, v => S.MoveCenterHotkey = v);
                HotkeyRow("Sonraki ekrana taşı", nameof(AppSettings.NextMonitorHotkey), () => S.NextMonitorHotkey, v => S.NextMonitorHotkey = v);
                PageHost.Children.Add(Hint("Silmek için kutuya tıklayıp Backspace, vazgeçmek için Esc. En az bir değiştirici tuş (Win, Ctrl, Shift, Alt) gerekir. " +
                                           "Windows'un kendi kısayolları (ör. Win+Alt+B = HDR) burada çalışmayabilir; o durumda kırmızı uyarı çıkar."));
                break;

            case "audio":
                Section("Ses göstergesi");
                Row("Ses değişince göster", "Topuz, klavye veya uygulamalardan ses değiştiğinde notch açılır.",
                    Toggle(() => S.ShowVolume, v => S.ShowVolume = v));
                Row("Windows ses kutusunu gizle", "Klavyedeki ses tuşlarını Notch yakalar ve sesi kendisi değiştirir; Windows'un kendi ses kutusu çıkmaz.",
                    Toggle(() => S.HideWindowsVolumeOsd, v => S.HideWindowsVolumeOsd = v));
                Row("SteelSeries Sonar kanallarını izle", "Master, Game, Chat, Media ve Aux kanalları.",
                    Toggle(() => S.WatchSonar, v => S.WatchSonar = v));
                Row("Ekranda kalma süresi", null,
                    SliderCtl(0.5, 5, 0.1, () => S.VolumeSeconds, v => S.VolumeSeconds = Math.Round(v, 1), v => $"{v:0.0} sn"));
                Section("Mikrofon");
                Row("Mikrofon açılıp kapanınca bildir", null, Toggle(() => S.NotifyMic, v => S.NotifyMic = v));
                Row("Mikrofon göstergesi", "Boştaki notch'ta: sessizken kırmızı, bir uygulama kullanırken turuncu simge.",
                    Toggle(() => S.ShowMicIndicator, v => S.ShowMicIndicator = v));
                break;

            case "sonar":
                SonarPage();
                break;

            case "notify":
                Section("Olaylar");
                Row("Şarkı değişince", null, Toggle(() => S.NotifyTrack, v => S.NotifyTrack = v));
                Row("Bir şey kopyalanınca", null, Toggle(() => S.NotifyCopy, v => S.NotifyCopy = v));
                Row("Ses çıkış cihazı değişince", null, Toggle(() => S.NotifyDevice, v => S.NotifyDevice = v));
                Row("Bluetooth cihaz bağlanınca / kopunca", null, Toggle(() => S.NotifyBluetooth, v => S.NotifyBluetooth = v));
                Row("İndirme tamamlanınca", "İndirilenler klasörüne yeni dosya düşünce.", Toggle(() => S.NotifyDownloads, v => S.NotifyDownloads = v));
                Row("İnen dosyaları tepsiye ekle", "Sürükleyip istediğin yere bırakabilmen için.", Toggle(() => S.DownloadsToTray, v => S.DownloadsToTray = v));
                Row("Kamera açılınca", null, Toggle(() => S.NotifyCamera, v => S.NotifyCamera = v));
                Row("Kamera göstergesi", "Bir uygulama kamerayı kullanırken boştaki notch'ta yeşil nokta.", Toggle(() => S.ShowCameraIndicator, v => S.ShowCameraIndicator = v));
                Row("İnternet / VPN bağlantısı değişince", null, Toggle(() => S.NotifyNetwork, v => S.NotifyNetwork = v));
                Section("Windows bildirimleri");
                Row("WhatsApp, Zoom, Discord… bildirimlerini göster", "Windows'a gelen bildirimler notch'ta da görünür. Veriler bilgisayardan çıkmaz.",
                    Toggle(() => S.NotifyWindows, v => S.NotifyWindows = v));
                Row("Windows'un kendi bildirim kutularını gizle", "Bildirimler yalnızca notch'ta görünür; bildirim merkezinde durmaya devam eder. Engellenen uygulamalar Windows'ta görünür. Notch kapanınca eski haline döner.",
                    Toggle(() => S.SuppressWindowsBanners, v => S.SuppressWindowsBanners = v));
                Row("Mesaj içeriğini göster", "Kapalıysa yalnızca uygulama adı görünür (ekran paylaşırken işe yarar).",
                    Toggle(() => S.NotifyShowContent, v => S.NotifyShowContent = v));
                Row("Engellenen uygulamalar", "Virgülle ayır, ör. Chrome, Steam",
                    TextCtl(() => S.NotifyBlockList, v => S.NotifyBlockList = v, "Uygulama adları"));
                Section("Pil");
                Row("Düşük pil uyarısı", "Bu seviyenin altına düşen cihazlar için bir kez uyarır.",
                    SliderCtl(5, 50, 5, () => S.LowBatteryPercent, v => S.LowBatteryPercent = (int)v, v => $"%{v:0}"));
                Section("Süre");
                Row("Bildirimin ekranda kalma süresi", null,
                    SliderCtl(1, 10, 0.5, () => S.ToastSeconds, v => S.ToastSeconds = Math.Round(v, 1), v => $"{v:0.0} sn"));
                break;

            case "calendar":
                Section("Outlook");
                Row("Outlook takvimini göster", "Masaüstü (klasik) Outlook açıkken bugünün ve yarının etkinlikleri Ajanda sekmesinde görünür. Veriler bilgisayardan çıkmaz.",
                    Toggle(() => S.OutlookCalendar, v => S.OutlookCalendar = v));
                Row("Toplantı bağlantılarını bul", "Zoom / Teams / Meet bağlantısı etkinlik açıklamasında aranır. Bazı şirket bilgisayarlarında Outlook bir izin uyarısı gösterebilir; o durumda kapat.",
                    Toggle(() => S.FindMeetingLinks, v => S.FindMeetingLinks = v));
                Row("Hatırlatma", "Toplantıdan kaç dakika önce notch'ta uyarı çıksın (0 = kapalı).",
                    SliderCtl(0, 30, 1, () => S.MeetingReminderMin, v => S.MeetingReminderMin = (int)v, v => v == 0 ? "Kapalı" : $"{v:0} dk"));
                PageHost.Children.Add(Hint("Yeni Outlook (Microsoft Store sürümü) takvim erişimine izin vermez; klasik Outlook gerekir. Toplantıya 15 dakika kala boştaki notch'ta geri sayım görünür."));
                break;

            case "focus":
                Section("Odak modu");
                Row("Odak modu", "Açıkken bildirimler notch'ta gösterilmez, sessizce bildirim geçmişine kaydedilir. Ses göstergesi ve zamanlayıcı çalışmaya devam eder.",
                    Toggle(() => S.FocusMode, v => S.FocusMode = v, immediate: true));
                Section("Otomatik");
                Row("Pomodoro çalışırken", "Çalışma süresinde otomatik açılır, molada kapanır.",
                    Toggle(() => S.FocusDuringPomodoro, v => S.FocusDuringPomodoro = v));
                Row("Toplantı sırasında", "Outlook'ta 'meşgul' görünen bir toplantı devam ederken otomatik açılır.",
                    Toggle(() => S.FocusDuringMeetings, v => S.FocusDuringMeetings = v));
                PageHost.Children.Add(Hint("Odak modunu panelin üstündeki ay simgesinden veya Kısayollar sayfasında atayacağın tuşla da açıp kapatabilirsin."));
                break;

            case "health":
                Section("Uyarılar");
                Row("Sistem uyarılarını göster", null, Toggle(() => S.HealthAlerts, v => S.HealthAlerts = v));
                Row("GPU sıcaklığı", "Bu sıcaklığı geçince uyar (NVIDIA).",
                    SliderCtl(60, 100, 1, () => S.AlertGpuTemp, v => S.AlertGpuTemp = (int)v, v => $"{v:0}°C"));
                Row("Bellek (RAM) kullanımı", null,
                    SliderCtl(70, 99, 1, () => S.AlertRamPercent, v => S.AlertRamPercent = (int)v, v => $"%{v:0}"));
                Row("Sistem diski boş alanı", "Bunun altına düşünce uyar.",
                    SliderCtl(1, 100, 1, () => S.AlertDiskGb, v => S.AlertDiskGb = (int)v, v => $"{v:0} GB"));
                PageHost.Children.Add(Hint("Her uyarı bir kez gösterilir; değer normale dönüp yeniden aşılınca tekrar uyarır. CPU sıcaklığı yönetici sürücüsü gerektirdiği için yok."));
                break;

            case "update":
                UpdatePage();
                break;

            case "about":
                AboutPage();
                break;

            case "media":
                Section("Şarkı sözleri");
                Row("Şarkı sözlerini göster", "Medya panelinde zamanlı sözler. Şarkı adı ve sanatçı lrclib.net'e gönderilir.",
                    Toggle(() => S.Lyrics, v => S.Lyrics = v));
                Section("Hava durumu");
                Row("Şehir", "Open-Meteo'dan alınır; yalnızca şehir adı gönderilir. Boş bırakırsan kapanır.",
                    TextCtl(() => S.WeatherCity, v => S.WeatherCity = v, "ör. İstanbul"));
                Row("Boştaki notch'ta göster", "Saatin yanında sıcaklık ve hava simgesi.",
                    Toggle(() => S.ShowWeatherIdle, v => S.ShowWeatherIdle = v));
                break;

            case "clip":
                Section("Pano geçmişi");
                Row("En fazla öğe", "Daha eskiler otomatik silinir.",
                    SliderCtl(10, 200, 10, () => S.ClipboardMax, v => S.ClipboardMax = (int)v, v => $"{v:0}"));
                PageHost.Children.Add(Hint("Şifreye benzeyen metinler ve şifre yöneticilerinin kopyaladıkları hiçbir zaman kaydedilmez."));
                Section("Saklama tepsisi");
                Row("Kopyalanan görselleri tepsiye ekle", "Ekran görüntüsü (Win+Shift+S) veya kopyalanan resimler tepsiye düşer; 7 gün sonra silinir.",
                    Toggle(() => S.ScreenshotToTray, v => S.ScreenshotToTray = v));
                break;

            case "timer":
                Section("Pomodoro");
                Row("Çalışma süresi", null, SliderCtl(5, 90, 5, () => S.PomodoroWork, v => S.PomodoroWork = (int)v, v => $"{v:0} dk"));
                Row("Kısa mola", null, SliderCtl(1, 30, 1, () => S.PomodoroBreak, v => S.PomodoroBreak = (int)v, v => $"{v:0} dk"));
                Row("Uzun mola", "Her 4 pomodoroda bir.", SliderCtl(5, 60, 5, () => S.PomodoroLongBreak, v => S.PomodoroLongBreak = (int)v, v => $"{v:0} dk"));
                break;

            case "game":
                Section("Oyun göstergesi");
                Row("Tam ekranda performans göstergesi", "Oyun veya tam ekran uygulama açıkken notch'ta CPU, GPU, ping ve saat.",
                    Toggle(() => S.GameHud, v => S.GameHud = v));
                Row("Ping adresi", "Gecikmenin ölçüleceği sunucu.", TextCtl(() => S.PingHost, v => S.PingHost = v, "1.1.1.1"));
                Section("FPS ölçümü");
                FpsRow();
                PageHost.Children.Add(Hint("FPS, Windows'un olay izleme sistemiyle (ETW) ölçülür ve yönetici izni gerektirir. Bunun için küçük bir yardımcı " +
                    "C:\\Program Files\\Notch klasörüne kopyalanır ve oturum açılınca yönetici haklarıyla başlar; Notch'un kendisi normal haklarla çalışmaya devam eder. " +
                    "DirectX 9–12 oyunlarında çalışır; bazı Vulkan/OpenGL oyunlarında görünmeyebilir. " +
                    "Özel tam ekran (exclusive fullscreen) oyunlarda hiçbir pencere görünmez; kenarlıksız pencere modunu kullan. " +
                    "Bir güncelleme yardımcının da yenilenmesini gerektirirse burada Güncelle düğmesi çıkar."));
                break;
        }

        if (keepScroll) Dispatcher.BeginInvoke(() => PageScroll.ScrollToVerticalOffset(off), DispatcherPriority.Loaded);
        else PageScroll.ScrollToTop();
    }

    void UpdatePage()
    {
        Section("Sürüm");
        string where = Installer.IsInstalledCopy ? "Bu bilgisayara kurulu" : Installer.IsSingleFile ? "Kurulmadan çalışıyor" : "Geliştirme sürümü";
        Row($"Notch v{Installer.CurrentVersion.ToString(3)}", where, new TextBlock());

        if (Installer.IsSingleFile && !Installer.IsInstalledCopy)
        {
            var install = new Button { Style = (Style)FindResource("PillButton"), Content = "Bu bilgisayara kur", Height = 32, Margin = new Thickness(0), Background = (Brush)FindResource("Accent") };
            install.Click += (_, _) =>
            {
                if (Installer.Install()) ((App)Application.Current).QuitForRestart();
            };
            Row("Kur", "Başlat menüsüne eklenir, 'Program Ekle/Kaldır'dan kaldırılabilir. Yönetici izni gerekmez.", install);
        }

        Section("Güncelleme");
        Row("Güncelleme kaynağı", "Boş bırakırsan GitHub Releases kullanılır. İstersen version.txt, Notch.exe ve Notch.exe.sig olan bir klasör veya https adresi yaz.",
            TextCtl(() => S.UpdateSource, v => S.UpdateSource = v, "GitHub Releases"));
        Row("Açılışta denetle", null, Toggle(() => S.AutoCheckUpdates, v => S.AutoCheckUpdates = v));

        var status = new TextBlock { FontSize = 11.5, Foreground = SubBrush, Margin = new Thickness(0, 3, 0, 0), Text = _main.PendingUpdate != null ? $"Yeni sürüm hazır: v{_main.PendingUpdate.Version.ToString(3)}" : "" };
        var btn = new Button { Style = (Style)FindResource("PillButton"), Height = 32, Margin = new Thickness(0) };
        void SetBtn()
        {
            bool has = _main.PendingUpdate != null;
            btn.Content = has ? "Güncelle ve yeniden başlat" : "Şimdi denetle";
            btn.Background = has ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        }
        SetBtn();
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            status.Text = _main.PendingUpdate != null ? "İndiriliyor…" : "Denetleniyor…";
            status.Text = _main.PendingUpdate != null ? await _main.ApplyUpdateAsync() : await _main.CheckUpdatesAsync(silent: false);
            SetBtn();
            btn.IsEnabled = true;
        };
        var left = new StackPanel();
        left.Children.Add(status);
        Row("Güncellemeler", null, btn, left);

        if (!Installer.IsSingleFile)
            PageHost.Children.Add(Hint("Bu, geliştirme (çok dosyalı) sürümü; kurma ve güncelleme yalnızca tek dosyalık Notch.exe sürümünde çalışır."));
        PageHost.Children.Add(Hint("Yalnızca imzası doğrulanan sürümler kurulur. Güncelleme denetimi açıksa açılışta GitHub'dan sürüm numarası okunur."));
    }

    void AboutPage()
    {
        const string profile = "https://github.com/atak946";
        const string repo = profile + "/NotchHub-Windows";

        Section("Uygulama");
        Row($"NotchHub for Windows v{Installer.CurrentVersion.ToString(3)}", "Windows için MacBook tarzı çentik: canlı bir kontrol merkezi.", new TextBlock());
        Row("Lisans", "MIT. Apple veya SteelSeries ile bağlantılı değildir.", LinkButton("Oku", repo + "/blob/main/LICENSE"));

        Section("Geliştirici");
        Row("Murat ATAK", "github.com/atak946", LinkButton("GitHub profili", profile));

        Section("Bağlantılar");
        Row("Kaynak kodu", "github.com/atak946/NotchHub-Windows", LinkButton("Depoyu aç", repo));
        Row("Sürüm notları", "Her sürümde nelerin değiştiği.", LinkButton("Sürümler", repo + "/releases"));
        Row("Sorun bildir veya öneride bulun", "GitHub'da yeni bir kayıt açılır.", LinkButton("Bildir", repo + "/issues/new/choose"));
        Row("macOS sürümü", "NotchHub", LinkButton("Aç", profile + "/NotchHub"));
    }

    Button LinkButton(string text, string url)
    {
        var b = new Button { Style = (Style)FindResource("PillButton"), Content = text, Height = 32, Margin = new Thickness(0), ToolTip = url };
        b.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        };
        return b;
    }

    void FpsRow()
    {
        var (status, button) = FpsControl.Create(SubBrush);
        Row("Oyun FPS'ini göster", null, button, status);
    }

    static string KeyHint(string hk) => string.IsNullOrWhiteSpace(hk) ? "" : $" Kısayol: {hk.Replace("+", " + ")}";

    // ================================================================
    //  Yapi taslari
    // ================================================================

    void Section(string title)
    {
        PageHost.Children.Add(new TextBlock
        {
            Text = title.ToUpper(new System.Globalization.CultureInfo("tr-TR")),
            FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = SubBrush,
            Margin = new Thickness(4, PageHost.Children.Count == 0 ? 6 : 28, 0, 10),
        });
        _rows = new StackPanel();
        PageHost.Children.Add(new Border
        {
            Background = (Brush)FindResource("CardBg"), CornerRadius = new CornerRadius(14),
            BorderBrush = LineBrush, BorderThickness = new Thickness(1), Child = _rows,
        });
    }

    TextBlock Hint(string text) => new()
    {
        Text = text, Foreground = SubBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(6, 10, 6, 0), LineHeight = 17,
    };

    void Row(string title, string? desc, FrameworkElement control, FrameworkElement? below = null)
    {
        if (_rows == null) Section("");
        if (_rows!.Children.Count > 0)
            _rows.Children.Add(new Border { Height = 1, Background = LineBrush, Margin = new Thickness(18, 0, 18, 0) });

        var g = new Grid { Margin = new Thickness(18, 14, 18, 14), MinHeight = 28 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        left.Children.Add(new TextBlock { Text = title, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(desc))
            left.Children.Add(new TextBlock { Text = desc, FontSize = 11.5, Foreground = SubBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), LineHeight = 16 });
        if (below != null) left.Children.Add(below);
        g.Children.Add(left);

        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        _rows.Children.Add(g);
    }

    CheckBox Toggle(Func<bool> get, Action<bool> set, bool immediate = false)
    {
        var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = get() };
        void H(object s, RoutedEventArgs e) { set(cb.IsChecked == true); Save(immediate); }
        cb.Checked += H;
        cb.Unchecked += H;
        return cb;
    }

    FrameworkElement SliderCtl(double min, double max, double step, Func<double> get, Action<double> set,
                               Func<double, string> fmt, double? resetValue = null)
    {
        var sl = new Slider
        {
            Style = (Style)FindResource("FlatSlider"), Minimum = min, Maximum = max, Width = 200,
            TickFrequency = step, IsSnapToTickEnabled = true, Value = Math.Clamp(get(), min, max),
        };
        var val = new TextBlock { Width = 64, TextAlignment = TextAlignment.Right, Foreground = SubBrush, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Text = fmt(sl.Value) };
        sl.ValueChanged += (_, e) => { val.Text = fmt(e.NewValue); set(e.NewValue); Save(); };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(sl);
        sp.Children.Add(val);
        if (resetValue is double rv)
        {
            var b = new Button { Style = (Style)FindResource("IconButton"), Content = "", FontSize = 12, Width = 30, Height = 30, ToolTip = "Sıfırla", Margin = new Thickness(6, 0, 0, 0) };
            b.Click += (_, _) => sl.Value = rv;
            sp.Children.Add(b);
        }
        return sp;
    }

    FrameworkElement TextCtl(Func<string> get, Action<string> set, string placeholder)
    {
        var tb = new TextBox { Style = (Style)FindResource("Input"), Width = 220, Text = get() };
        var ph = new TextBlock { Text = placeholder, Foreground = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), IsHitTestVisible = false, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        ph.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        void Commit()
        {
            var v = tb.Text.Trim();
            if (v == get()) return;
            set(v);
            Save(immediate: true);
        }
        tb.TextChanged += (_, _) => ph.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        tb.LostKeyboardFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); } };
        var g = new Grid();
        g.Children.Add(tb);
        g.Children.Add(ph);
        return g;
    }

    FrameworkElement Swatches()
    {
        string[] colors = { "#FF5A1F", "#FF375F", "#BF5AF2", "#5E5CE6", "#0A84FF", "#64D2FF", "#30D158", "#FFD60A" };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var hex in colors)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            bool sel = string.Equals(S.AccentColor, hex, StringComparison.OrdinalIgnoreCase);
            var dot = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(4, 0, 0, 0),
                Background = new SolidColorBrush(c), Cursor = Cursors.Hand, ToolTip = hex,
                BorderBrush = sel ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(sel ? 2.5 : 1),
            };
            dot.MouseLeftButtonUp += (_, _) => { S.AccentColor = hex; Save(immediate: true); ShowPage(_page, keepScroll: true); };
            sp.Children.Add(dot);
        }
        return sp;
    }

    FrameworkElement MonitorPicker()
    {
        var sp = new WrapPanel { MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Right };
        var screens = WinForms.Screen.AllScreens;
        foreach (var scr in screens)
        {
            bool sel = scr.Primary ? string.IsNullOrEmpty(S.Monitor) || S.Monitor == scr.DeviceName : S.Monitor == scr.DeviceName;
            var b = new Button
            {
                Style = (Style)FindResource("PillButton"), Content = MainWindow.ScreenLabel(scr) + $"  {scr.Bounds.Width}×{scr.Bounds.Height}",
                Height = 30, Margin = new Thickness(6, 3, 0, 3),
                Background = sel ? new SolidColorBrush(Color.FromArgb(0x55, AccentColor.R, AccentColor.G, AccentColor.B)) : new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            };
            var name = scr.Primary ? "" : scr.DeviceName;
            b.Click += (_, _) => { S.Monitor = name; S.OffsetX = 0; Save(immediate: true); ShowPage(_page, keepScroll: true); };
            sp.Children.Add(b);
        }
        return sp;
    }

    static Color AccentColor => ((SolidColorBrush)Application.Current.Resources["Accent"]).Color;

    // ================================================================
    //  Kisayol kutusu (tus kapakcigi gorunumu)
    // ================================================================

    sealed class Recording
    {
        public required Border Box;
        public required Action<string> Set;
        public required Action Redraw;
    }
    Recording? _recording;

    void HotkeyRow(string title, string setting, Func<string> get, Action<string> set)
    {
        var err = new TextBlock { Foreground = ErrBrush, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        void RefreshErr()
        {
            var e = _main.HotkeyError(setting);
            err.Text = e ?? "";
            err.Visibility = e == null ? Visibility.Collapsed : Visibility.Visible;
        }
        _errorRefreshers.Add(RefreshErr);
        RefreshErr();

        var caps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var box = new Border
        {
            MinWidth = 150, Height = 34, CornerRadius = new CornerRadius(9), Padding = new Thickness(6, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x20, 0x24)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x30, 0x36)),
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Child = caps, ToolTip = "Değiştirmek için tıkla",
        };

        void Redraw(string? recordingText = null)
        {
            caps.Children.Clear();
            if (recordingText != null)
            {
                caps.Children.Add(new TextBlock { Text = recordingText, Foreground = (Brush)FindResource("Accent"), FontSize = 12.5, Margin = new Thickness(6, 0, 6, 0) });
                return;
            }
            var hk = get();
            if (string.IsNullOrWhiteSpace(hk))
            {
                caps.Children.Add(new TextBlock { Text = "Atanmadı", Foreground = SubBrush, FontSize = 12.5, Margin = new Thickness(6, 0, 6, 0) });
                return;
            }
            foreach (var k in hk.Split('+'))
                caps.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2E, 0x34)), CornerRadius = new CornerRadius(6),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3F, 0x46)), BorderThickness = new Thickness(1, 1, 1, 2),
                    Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(2, 0, 2, 0),
                    Child = new TextBlock { Text = KeyLabel(k.Trim()), FontSize = 11.5, FontWeight = FontWeights.SemiBold },
                });
        }
        Redraw();

        box.MouseLeftButtonUp += (_, _) =>
        {
            CancelRecording();
            _main.SuspendHotkeys(); // kayit sirasinda mevcut kisayollar tetiklenmesin
            box.BorderBrush = (Brush)FindResource("Accent");
            Redraw("Tuşlara bas…");
            _recording = new Recording
            {
                Box = box,
                Set = v => { set(v); Save(immediate: true); },
                Redraw = () => { box.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x30, 0x36)); Redraw(); },
            };
            Focus();
        };

        var clear = new Button { Style = (Style)FindResource("IconButton"), Content = "", FontSize = 10, Width = 30, Height = 30, ToolTip = "Kaldır", Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) => { CancelRecording(); set(""); Save(immediate: true); Redraw(); };

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(box);
        right.Children.Add(clear);
        Row(title, null, right, err);
    }

    static string KeyLabel(string k) => k switch
    {
        "Left" => "←", "Right" => "→", "Up" => "↑", "Down" => "↓",
        "Return" or "Enter" => "Enter", "Space" => "Boşluk", "Back" => "Backspace",
        "OemComma" => ",", "OemPeriod" => ".", "OemMinus" => "-", "OemPlus" => "+",
        _ when k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1]) => k[1..],
        _ => k,
    };

    void CancelRecording()
    {
        if (_recording == null) return;
        _recording.Redraw();
        _recording = null;
        _main.ResumeHotkeys();
        RefreshErrors();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording == null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { CancelRecording(); return; }
        if (key is Key.Back or Key.Delete)
        {
            var r = _recording;
            _recording = null;
            r.Set("");
            r.Redraw();
            _main.ResumeHotkeys();
            RefreshErrors();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt
            or Key.RightAlt or Key.LWin or Key.RWin or Key.System) return; // yalniz degistirici: bekle

        var mods = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;
        if (mods == ModifierKeys.None) return; // degistirici sart

        var rec = _recording;
        _recording = null;
        _main.ResumeHotkeys();       // once eski kisayollari geri al, sonra yenisini kaydet
        rec.Set(HotkeyText.Format(mods, key));
        rec.Redraw();
        RefreshErrors();
    }

    // ================================================================

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        CancelRecording();
        AppSettings.ResetToDefaults();
        ShowPage(_page);
    }

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // koyu mod
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // yuvarlak koseler
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
