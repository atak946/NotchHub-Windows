using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Notch.Services;

namespace Notch;

/// <summary>
/// Ilk acilista gosterilen kurulum yardimcisi: ozellikleri tanitir, temel ayarlari ve izinleri sorar.
/// Secimler aninda kaydedilir; ayarlardan veya tepsi menusunden yeniden acilabilir.
/// </summary>
public sealed class OnboardingWindow : Window
{
    readonly MainWindow _main;
    readonly StackPanel _page = new() { Margin = new Thickness(36, 4, 36, 20) };
    readonly TextBlock _title = new() { FontSize = 26, FontWeight = FontWeights.SemiBold };
    readonly TextBlock _sub = new() { FontSize = 13, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly ScrollViewer _scroll;
    readonly Button _back, _next, _skip;
    readonly (string Title, string Sub, Action Build)[] _steps;
    StackPanel? _card;
    int _step;
    bool _dirty;

    static AppSettings S => AppSettings.Current;
    static readonly Brush SubBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9F, 0xA8));
    static readonly Brush LineBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x23, 0x28));
    static readonly Brush CardBrush = new SolidColorBrush(Color.FromRgb(0x16, 0x17, 0x1A));

    public OnboardingWindow(MainWindow main)
    {
        _main = main;
        Title = "Notch Kurulum Yardımcısı";
        Width = 720; Height = 660;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x10, 0x12));
        Foreground = (Brush)Application.Current.FindResource("Fg");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        SourceInitialized += (_, _) => UseDarkTitleBar();

        _steps = new (string, string, Action)[]
        {
            ("Notch'a hoş geldin", "Ekranın üstünde, farenin bir hareketiyle açılan bir kontrol merkezi. Birkaç adımda ayarlayalım.", Welcome),
            ("Neler yapabilir?", "Her biri panelde kendi sekmesinde veya göstergesinde.", Features),
            ("Nasıl çalışsın?", "Bunların hepsini sonradan Ayarlar'dan değiştirebilirsin.", General),
            ("Bildirimler ve gizlilik", "Hangi bilgilerin okunacağını ve internete ne gideceğini sen seç.", Privacy),
            ("İzinler", "Notch normal kullanıcı haklarıyla çalışır; özelliklerin çoğu ek izin istemez.", Permissions),
            ("SteelSeries Sonar", "Oyun, müzik ve görüşmeye göre GAME profili kendiliğinden değişebilir.", Sonar),
            ("Hazırsın", "Bu yardımcıyı Ayarlar → Genel'den veya tepsi simgesinin menüsünden yeniden açabilirsin.", Done),
        };

        var header = new StackPanel { Margin = new Thickness(36, 22, 36, 16) };
        _title.FontFamily = (FontFamily)Application.Current.FindResource("DisplayFont");
        _sub.Foreground = SubBrush;
        header.Children.Add(_title);
        header.Children.Add(_sub);

        _skip = Pill("Atla", (_, _) => Finish());
        _back = Pill("Geri", (_, _) => GoToStep(_step - 1));
        _next = Pill("İleri", (_, _) => { if (_step == _steps.Length - 1) Finish(); else GoToStep(_step + 1); });
        _next.SetResourceReference(Control.BackgroundProperty, "Accent");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_skip);
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        var footer = new Grid();
        footer.Children.Add(_dots);
        footer.Children.Add(buttons);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var bar = new Border { BorderBrush = LineBrush, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(36, 14, 36, 14), Child = footer };
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        _scroll = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(_scroll);
        Content = root;

        // Pencereyi kapatmak da "atla" sayilir; Notch kapanirken kapanmasi sayilmaz
        Closing += (_, _) => { if (!_main.IsQuitting) MarkDone(); };
        GoToStep(0);
    }

    void GoToStep(int step)
    {
        Flush();
        _step = Math.Clamp(step, 0, _steps.Length - 1);
        var s = _steps[_step];
        _title.Text = s.Title;
        _sub.Text = s.Sub;
        _page.Children.Clear();
        _card = null;
        s.Build();
        _scroll.ScrollToTop();

        _dots.Children.Clear();
        for (int i = 0; i < _steps.Length; i++)
        {
            var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), Fill = new SolidColorBrush(Color.FromRgb(0x3A, 0x3C, 0x43)) };
            if (i == _step) dot.SetResourceReference(Shape.FillProperty, "Accent");
            _dots.Children.Add(dot);
        }
        bool last = _step == _steps.Length - 1;
        _back.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
        _skip.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = last ? "Bitir" : "İleri";
    }

    // Metin kutusundaki degisiklik adim degisirken kaydedilir
    void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        AppSettings.Save();
    }

    void MarkDone()
    {
        Flush();
        if (S.OnboardingDone) return;
        S.OnboardingDone = true;
        AppSettings.Save();
    }

    void Finish()
    {
        MarkDone();
        Close();
    }

    // ================================================================
    //  Adimlar
    // ================================================================

    void Welcome()
    {
        Info("", "Açmak için", "Fareyi ekranın en üst kenarında, ortadaki çentiğin üzerinde kısa süre beklet. Uzaklaşınca kapanır.");
        Info("", "Gizlemek veya göstermek için", Keys(S.ToggleHotkey));
        Info("", "Ayarlar için", "Açık paneldeki dişli simgesine veya sistem tepsisindeki Notch simgesine tıkla.");
    }

    void Features()
    {
        var grid = new UniformGrid { Columns = 2 };
        foreach (var (icon, title, text) in new[]
        {
            ("", "Medya", "Kapak, kontroller ve zamanlı şarkı sözleri"),
            ("", "Ses", "Çıkış cihazı, uygulama sesleri, mikrofon, Sonar"),
            ("", "Takvim ve ajanda", "Hava durumu, Outlook etkinlikleri, Katıl düğmesi"),
            ("", "Sistem", "CPU, GPU, bellek, disk ve uyarılar"),
            ("", "Cihazlar", "Bluetooth pil seviyeleri ve monitör parlaklığı"),
            ("", "Zamanlayıcı", "Geri sayım ve Pomodoro"),
            ("", "Pano", "Kopyaladıklarının geçmişi"),
            ("", "Saklama tepsisi", "Dosyayı çentiğe bırak, sonra sürükleyip al"),
            ("", "Notlar", "Hızlı düz metin notu"),
            ("", "Bildirimler", "Windows bildirimleri, geçmiş ve odak modu"),
            ("", "Oyun göstergesi", "Tam ekranda CPU, GPU, ping ve FPS"),
            ("", "Ses göstergesi", "Windows ses kutusu yerine çentik"),
        })
        {
            var sp = new DockPanel();
            var glyph = Glyph(icon);
            DockPanel.SetDock(glyph, Dock.Left);
            sp.Children.Add(glyph);
            sp.Children.Add(Texts(title, text));
            grid.Children.Add(new Border { Background = CardBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 11, 12, 11), Margin = new Thickness(4), Child = sp });
        }
        _page.Children.Add(grid);
    }

    void General()
    {
        Toggle("Windows ile birlikte başlat", "Bilgisayar açıldığında Notch otomatik çalışır.", () => S.AutoStart, v => S.AutoStart = v);
        Toggle("Yalnızca üst kenarda aç", "Fareyi en üst kenara dayaman gerekir; tarayıcı sekmeleriyle çakışmaz.", () => S.EdgeOnly, v => S.EdgeOnly = v);
        Toggle("Tam ekranda gizle", "Oyun veya tam ekran video açıkken boştaki notch kaybolur.", () => S.HideInFullscreen, v => S.HideInFullscreen = v);
        Toggle("Windows ses kutusunu gizle", "Ses tuşlarını klavye kancasıyla Notch yakalar ve göstergeyi kendisi çizer.", () => S.HideWindowsVolumeOsd, v => S.HideWindowsVolumeOsd = v);
        Toggle("Kopyalanan görselleri tepsiye ekle", "Ekran görüntüleri tepsiye düşer; 7 gün sonra silinir.", () => S.ScreenshotToTray, v => S.ScreenshotToTray = v);
    }

    void Privacy()
    {
        Toggle("Windows bildirimlerini göster", "Windows'un bildirim veritabanı okunur. Veriler bilgisayardan çıkmaz.", () => S.NotifyWindows, v => S.NotifyWindows = v);
        Toggle("Mesaj içeriğini göster", "Kapalıysa yalnızca uygulama adı görünür.", () => S.NotifyShowContent, v => S.NotifyShowContent = v);
        Toggle("Windows'un bildirim kutularını gizle", "Bildirimler yalnızca notch'ta görünür. Notch kapanınca eski haline döner.", () => S.SuppressWindowsBanners, v => S.SuppressWindowsBanners = v);
        Toggle("Outlook takvimi", "Masaüstü (klasik) Outlook açıkken etkinlikler okunur.", () => S.OutlookCalendar, v => S.OutlookCalendar = v);
        Toggle("Şarkı sözleri", "Şarkı ve sanatçı adı lrclib.net'e gönderilir.", () => S.Lyrics, v => S.Lyrics = v);

        var city = new TextBox { Style = (Style)Application.Current.FindResource("Input"), Width = 180, Text = S.WeatherCity };
        city.TextChanged += (_, _) => { S.WeatherCity = city.Text.Trim(); _dirty = true; };
        Row("Hava durumu için şehir", "Yalnızca şehir adı Open-Meteo'ya gönderilir. Boşsa kapalı.", city);
        Toggle("Güncellemeleri denetle", "Açılışta GitHub'daki son sürüm numarası okunur.", () => S.AutoCheckUpdates, v => S.AutoCheckUpdates = v);
    }

    void Permissions()
    {
        Row("Windows bildirimleri", "Bir uygulamanın bildirimi notch'ta görünmüyorsa Windows ayarlarında o uygulamanın bildirimleri açık olmalı.",
            Pill("Windows ayarlarını aç", (_, _) => Open("ms-settings:notifications")));

        bool outlook = Process.GetProcessesByName("OUTLOOK").Length > 0;
        Row("Outlook", outlook ? "Klasik Outlook açık; takvim okunabilir." : "Klasik Outlook şu an açık değil. Yeni Outlook takvim erişimine izin vermez.",
            new TextBlock { Text = outlook ? "Algılandı" : "Yok", Foreground = SubBrush });

        var (status, button) = FpsControl.Create(SubBrush);
        Row("Oyun FPS'i (isteğe bağlı)", "Yönetici izni isteyen tek özellik. Küçük bir yardımcı Program Files'a kopyalanır; Notch normal haklarla çalışmaya devam eder.", button, status);

        Note("Mikrofon ve kamera yalnızca \"kullanılıyor mu\" bilgisi için izlenir; ses ve görüntü kaydedilmez.");
    }

    void Sonar()
    {
        var status = new TextBlock { Text = "Sonar aranıyor…", FontSize = 11.5, Foreground = SubBrush, Margin = new Thickness(0, 3, 0, 0) };
        var choose = Pill("Profilleri seç", (_, _) => _main.OpenSettings("sonar"));
        choose.IsEnabled = false;
        Row("Durum", null, choose, status);
        Toggle("Profili otomatik değiştir", "Her durum için profili \"Profilleri seç\" ile belirle.", () => S.SonarAutoSwitch, v => S.SonarAutoSwitch = v);
        Note("Sonar kullanmıyorsan bu adımı geçebilirsin.");
        Detect();

        async void Detect()
        {
            var list = await _main.GetSonarProfiles();
            status.Text = list == null ? "Sonar bulunamadı. SteelSeries GG açıkken bu adıma geri dön." : $"Sonar çalışıyor · {list.Count} GAME profili";
            choose.IsEnabled = list != null;
        }
    }

    void Done()
    {
        Info("", "Paneli aç", "Fareyi ekranın üst kenarında çentiğin üzerinde beklet.");
        Info("", "Kısayollar", $"Göster / gizle: {Keys(S.ToggleHotkey)}\nPano geçmişi: {Keys(S.ClipboardHotkey)}\nMikrofonu aç / kapat: {Keys(S.MicHotkey)}");

        if (Installer.IsSingleFile && !Installer.IsInstalledCopy)
        {
            var install = Pill("Bu bilgisayara kur", (_, _) =>
            {
                MarkDone(); // kurulan kopya yardimciyi yeniden acmasin
                if (Installer.Install()) ((App)Application.Current).QuitForRestart();
            });
            Row("Kurulum (önerilir)", "Başlat menüsüne eklenir ve güncellenebilir. Yönetici izni gerekmez.", install);
        }
    }

    // ================================================================
    //  Yapi taslari
    // ================================================================

    static string Keys(string hotkey) => string.IsNullOrWhiteSpace(hotkey) ? "atanmadı" : hotkey.Replace("+", " + ");

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    static Button Pill(string text, RoutedEventHandler click)
    {
        var b = new Button { Style = (Style)Application.Current.FindResource("PillButton"), Content = text, Height = 32, MinWidth = 72, Margin = new Thickness(8, 0, 0, 0) };
        b.Click += click;
        return b;
    }

    static TextBlock Glyph(string icon) => new()
    {
        Text = icon, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 17,
        Width = 32, VerticalAlignment = VerticalAlignment.Center,
    };

    static StackPanel Texts(string title, string? desc, FrameworkElement? below = null)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
        if (desc != null)
            sp.Children.Add(new TextBlock { Text = desc, FontSize = 11.5, Foreground = SubBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        if (below != null) sp.Children.Add(below);
        return sp;
    }

    /// <summary>Satiri ortak karta ekler.</summary>
    void Append(FrameworkElement row)
    {
        if (_card == null)
        {
            _card = new StackPanel();
            _page.Children.Add(new Border { Background = CardBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Child = _card });
        }
        else _card.Children.Add(new Border { Height = 1, Background = LineBrush, Margin = new Thickness(18, 0, 18, 0) });
        _card.Children.Add(row);
    }

    void Info(string icon, string title, string text)
    {
        var row = new DockPanel { Margin = new Thickness(18, 15, 18, 15) };
        var glyph = Glyph(icon);
        glyph.Width = 40;
        DockPanel.SetDock(glyph, Dock.Left);
        row.Children.Add(glyph);
        row.Children.Add(Texts(title, text));
        Append(row);
    }

    void Row(string title, string? desc, FrameworkElement control, FrameworkElement? below = null)
    {
        var row = new DockPanel { Margin = new Thickness(18, 11, 18, 11) };
        control.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        var texts = Texts(title, desc, below);
        texts.Margin = new Thickness(0, 0, 20, 0);
        row.Children.Add(texts);
        Append(row);
    }

    void Toggle(string title, string desc, Func<bool> get, Action<bool> set)
    {
        var cb = new CheckBox { Style = (Style)Application.Current.FindResource("Switch"), IsChecked = get() };
        void Changed(object s, RoutedEventArgs e) { set(cb.IsChecked == true); _dirty = false; AppSettings.Save(); }
        cb.Checked += Changed;
        cb.Unchecked += Changed;
        Row(title, desc, cb);
    }

    void Note(string text) => _page.Children.Add(new TextBlock { Text = text, Foreground = SubBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 12, 6, 0) });

    void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
