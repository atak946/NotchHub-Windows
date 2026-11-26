using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Notch.Services;

namespace Notch;

public partial class MainWindow : Window
{
    enum Mode { Idle, Volume, Toast, Expanded }

    const double Ear = 10;                 // ustteki ice kivrik "kulak" yaricapi
    static readonly CultureInfo Tr = new("tr-TR");
    static readonly Color DefaultAccent = Color.FromRgb(0xFF, 0x5A, 0x1F);
    static readonly Brush SubBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9F, 0xA8));
    static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x79));

    // Servisler
    readonly VolumeService _volume;
    readonly SonarService _sonar = new();
    readonly MediaService _media;
    readonly SystemStatsService _stats = new();
    readonly ClipboardService _clip = new();
    readonly TimerService _timer = new();
    readonly TrayService _tray = new();
    bool _draggingOut;
    Point _dragStart;

    // Kisayollar (Ayarlar'dan degistirilebilir)
    const int HotkeyClipId = 0x4E01, HotkeyToggleId = 0x4E02;
    string? _hotkeyLabel;
    public string? ClipHotkeyError { get; private set; }
    public string? ToggleHotkeyError { get; private set; }
    bool _pasteMode;          // kisayolla acildi
    bool _pasteEntered;       // fare panele en az bir kez girdi mi
    DateTime _pasteDeadline;

    // Durum
    Mode _mode = Mode.Idle;
    FrameworkElement _view;
    readonly Spring _w = new(150), _h = new(28);
    bool _animating;
    TimeSpan _lastRender;
    bool _fullscreen;
    DateTime _lastEndpointVolume = DateTime.MinValue;
    DateTime _calMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    Color _accent = DefaultAccent;
    readonly Random _rng = new();
    IntPtr _hwnd;

    readonly Queue<double> _cpuHist = new(), _gpuHist = new(), _ramHist = new(), _netHist = new();

    readonly DispatcherTimer _transient = new();
    readonly DispatcherTimer _hoverDelay = new() { Interval = TimeSpan.FromMilliseconds(220) };
    readonly DispatcherTimer _leaveDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _eq = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _fsCheck = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _hdrVolHide = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _cursorPoll = new() { Interval = TimeSpan.FromMilliseconds(40) };

    // Bosta notch tiklamalari gecirir (tarayici sekmeleri vb. calismaya devam eder).
    // Panel, fare ekranin en ust kenarinda notch hizasinda kisa bir sure durunca acilir.
    static AppSettings S => AppSettings.Current;
    bool _clickThrough, _cursorOver, _ghost;
    DateTime? _edgeSince;

    public MainWindow()
    {
        InitializeComponent();
        _volume = new VolumeService(Dispatcher);
        _media = new MediaService(Dispatcher);
        _view = IdleView;


        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;

        _transient.Tick += (_, _) => OnTransientEnded();
        _hoverDelay.Tick += (_, _) => { _hoverDelay.Stop(); if (_cursorOver) Expand(); };
        _leaveDelay.Tick += (_, _) => { _leaveDelay.Stop(); if (!_cursorOver && _mode == Mode.Expanded) Collapse(); };
        _cursorPoll.Tick += (_, _) => PollCursor();
        _clock.Tick += (_, _) => OnClock();
        _eq.Tick += (_, _) => AnimateEq();
        _fsCheck.Tick += (_, _) => CheckFullscreen();
        _hdrVolHide.Tick += (_, _) => { _hdrVolHide.Stop(); Fade(HdrVolume, 0, 250); };

        AppSettings.Changed += OnSettingsChanged;
        _leaveDelay.Interval = TimeSpan.FromMilliseconds(S.CloseDelayMs);
        UpdateGeometry();
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == Native.WM_MOUSEACTIVATE && !_typing && !NotesBox.IsMouseOver) { handled = true; return new IntPtr(Native.MA_NOACTIVATE); }
            if (msg == Native.WM_HOTKEY && w.ToInt32() == HotkeyClipId) { handled = true; OnClipboardHotkey(); }
            if (msg == Native.WM_HOTKEY && w.ToInt32() == HotkeyToggleId) { handled = true; ToggleHidden(); }
            if (msg == Native.WM_HOTKEY && w.ToInt32() == HotkeyMicId) { handled = true; _mic.Toggle(); }
            if (msg == Native.WM_HOTKEY && w.ToInt32() == HotkeyFocusId) { handled = true; ToggleFocus(); }
            if (msg == Native.WM_HOTKEY && w.ToInt32() >= HotkeyMoveLeftId && w.ToInt32() <= HotkeyMoveDownId) { handled = true; OnMoveHotkey(w.ToInt32()); }
            return IntPtr.Zero;
        });
        ApplyHotkeys();
        _clip.Attach(_hwnd);
        SetClickThrough(true);
        _cursorPoll.Start();
    }

    // ================================================================
    //  Ayarlar ve kisayollar
    // ================================================================

    void ApplyHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.UnregisterHotKey(_hwnd, HotkeyClipId);
        Native.UnregisterHotKey(_hwnd, HotkeyToggleId);
        ClipHotkeyError = Register(HotkeyClipId, S.ClipboardHotkey);
        ToggleHotkeyError = Register(HotkeyToggleId, S.ToggleHotkey);
        Native.UnregisterHotKey(_hwnd, HotkeyMicId);
        MicHotkeyError = Register(HotkeyMicId, S.MicHotkey);
        RegisterMoveHotkeys();
        _hotkeyLabel = ClipHotkeyError == null && !string.IsNullOrEmpty(S.ClipboardHotkey) ? S.ClipboardHotkey : null;
    }

    string? Register(int id, string text, bool repeat = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return null; // kisayol kapali
        if (!HotkeyText.TryParse(text, out var mods, out var vk)) return "Geçersiz kısayol";
        return Native.RegisterHotKey(_hwnd, id, mods | (repeat ? 0 : Native.MOD_NOREPEAT), vk)
            ? null : "Bu kısayol başka bir uygulama tarafından kullanılıyor";
    }

    string _lastHotkeys = "";

    void OnSettingsChanged()
    {
        var hk = string.Join("|", S.ClipboardHotkey, S.ToggleHotkey, S.MicHotkey,
                             S.MoveLeftHotkey, S.MoveRightHotkey, S.MoveCenterHotkey, S.NextMonitorHotkey, S.FocusHotkey);
        if (hk != _lastHotkeys) { _lastHotkeys = hk; ApplyHotkeys(); }
        _leaveDelay.Interval = TimeSpan.FromMilliseconds(S.CloseDelayMs);
        _clip.Trim();
        RebuildClipList();
        OnMediaChanged();
        OnFeatureSettingsChanged();
        if (S.Hidden && _mode != Mode.Idle) { _pasteMode = false; SetMode(Mode.Idle); }
        ApplyFullscreenVisibility();
    }

    public void SuspendHotkeys()
    {
        Native.UnregisterHotKey(_hwnd, HotkeyClipId);
        Native.UnregisterHotKey(_hwnd, HotkeyToggleId);
        Native.UnregisterHotKey(_hwnd, HotkeyMicId);
        UnregisterMoveHotkeys();
    }

    public void ResumeHotkeys() => ApplyHotkeys();

    SettingsWindow? _settings;

    OnboardingWindow? _onboarding;
    public bool IsQuitting { get; private set; }

    public void OpenOnboarding()
    {
        if (_mode == Mode.Expanded) Collapse();
        if (_onboarding == null)
        {
            _onboarding = new OnboardingWindow(this);
            _onboarding.Closed += (_, _) => _onboarding = null;
            _onboarding.Show();
        }
        if (_onboarding.WindowState == WindowState.Minimized) _onboarding.WindowState = WindowState.Normal;
        _onboarding.Activate();
    }

    public void OpenSettings(string? page = null)
    {
        if (_mode == Mode.Expanded) Collapse();
        if (_settings == null)
        {
            _settings = new SettingsWindow(this);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        if (page != null) _settings.GoTo(page);
        _settings.Activate();
    }

    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    public void ToggleHidden()
    {
        S.Hidden = !S.Hidden;
        AppSettings.Save();
    }

    void SetClickThrough(bool on)
    {
        if (_hwnd == IntPtr.Zero || on == _clickThrough) return;
        _clickThrough = on;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        ex = on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);
    }

    void PollCursor()
    {
        if (!Native.GetCursorPos(out var p) || PresentationSource.FromVisual(NotchHost) == null) return;
        // PointToScreen fiziksel piksel dondurur; GetCursorPos da oyle (PerMonitorV2)
        var tl = NotchHost.PointToScreen(new Point(0, 0));
        var br = NotchHost.PointToScreen(new Point(NotchHost.ActualWidth, NotchHost.ActualHeight));
        bool lDown = Native.LeftButtonDown();
        double margin = _mode == Mode.Expanded ? 6 : lDown ? 14 : 0;
        _cursorOver = p.X >= tl.X - margin && p.X <= br.X + margin && p.Y >= tl.Y && p.Y <= br.Y + margin;

        if (S.Hidden && _mode != Mode.Expanded)
        {
            _cursorOver = false;
            _edgeSince = null;
            SetGhost(false);
            SetClickThrough(true);
            return;
        }

        if (_mode == Mode.Expanded)
        {
            _edgeSince = null;
            SetGhost(false);
            // Kisayolla acildiysa, fare gelene kadar (en fazla 10 sn) acik kal
            if (_pasteMode && !_pasteEntered)
            {
                if (_cursorOver) _pasteEntered = true;
                else
                {
                    if (DateTime.UtcNow > _pasteDeadline) Collapse();
                    return;
                }
            }
            if (_cursorOver || _draggingOut || _typing) _leaveDelay.Stop();
            else if (!_leaveDelay.IsEnabled) _leaveDelay.Start();
            return;
        }

        // Tus basiliyken (dosya suruklenirken) notch'a gelinirse birakilabilir ol
        SetClickThrough(!(lDown && _cursorOver));

        // Fare bostaki notch'un ustundeyken saydamlas: alttaki sekme gorunsun
        SetGhost(_cursorOver && !lDown && _mode == Mode.Idle && !_fullscreen);

        bool atEdge = _cursorOver && (!S.EdgeOnly || p.Y <= tl.Y + 1) && !_fullscreen;
        if (atEdge)
        {
            _edgeSince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _edgeSince.Value).TotalMilliseconds >= S.EdgeDwellMs)
            {
                _edgeSince = null;
                Expand();
            }
        }
        else _edgeSince = null;
    }

    void SetGhost(bool on)
    {
        if (on == _ghost) return;
        _ghost = on;
        double to = on ? 0.18 : 1;
        Fade(Shape, to, on ? 120 : 200);
        Fade(Body, to, on ? 120 : 200);
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _volume.Changed += (n, v, m) => { VolumeKeyHook.Log($"CIHAZ {n} -> %{v}{(m ? " sessiz" : "")}"); Dispatcher.BeginInvoke(() => OnVolume(n, v, m, true)); };
        _volume.DefaultDeviceChanged += n => { if (S.NotifyDevice) ShowToast("", "Ses çıkışı değişti", n); };
        _volume.Start();

        _sonar.Changed += (n, v, m) => Dispatcher.BeginInvoke(() => OnVolume(n, v, m, false));
        _sonar.Start();

        _media.Changed += OnMediaChanged;
        _media.TrackChanged += OnTrackChanged;
        await _media.StartAsync();

        _stats.Updated += s => Dispatcher.BeginInvoke(() => OnStats(s));
        _stats.Start();

        _clip.Copied += OnCopied;
        _clip.HistoryChanged += RebuildClipList;

        _tray.Changed += RebuildTray;
        _tray.Load();
        RebuildTray();

        _timer.Tick += OnTimerTick;
        _timer.Finished += OnTimerFinished;

        StartFeatures();

        _clock.Start();
        _fsCheck.Start();
        OnClock();
        BuildCalendar();
        RebuildClipList();
        UpdateIdle();
        TabMedia.IsChecked = true;
        if (!S.OnboardingDone) OpenOnboarding();
    }

    public void Shutdown()
    {
        IsQuitting = true;
        _volume.Dispose();
        _sonar.Dispose();
        _stats.Dispose();
        StopFeatures();
        Close();
    }

    // ================================================================
    //  Sekil ve animasyon
    // ================================================================

    sealed class Spring
    {
        public double X, V, Target;
        public Spring(double x) { X = Target = x; }

        /// <summary>Hafif yaylanmali (az sonumlu) yay. Hareket bitince false doner.</summary>
        public bool Step(double dt)
        {
            const double k = 340, damping = 0.74;
            double c = 2 * Math.Sqrt(k) * damping;
            double a = k * (Target - X) - c * V;
            V += a * dt;
            X += V * dt;
            if (Math.Abs(Target - X) < 0.25 && Math.Abs(V) < 4) { X = Target; V = 0; return false; }
            return true;
        }
    }

    (double W, double H) TargetSize() => _mode switch
    {
        Mode.Volume => (360, 60),
        Mode.Toast => (390, 64),
        Mode.Expanded => (740, 262),
        _ => (IdleWidth(), 30),
    };

    void Resize()
    {
        var (w, h) = TargetSize();
        _w.Target = w;
        _h.Target = h;
        if (_animating) return;
        _animating = true;
        _lastRender = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRender;
    }

    void OnRender(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (_lastRender == TimeSpan.Zero) { _lastRender = t; return; }
        double dt = (t - _lastRender).TotalSeconds;
        if (dt <= 0) return;
        _lastRender = t;
        dt = Math.Min(dt, 1 / 30.0);
        bool a = _w.Step(dt), b = _h.Step(dt);
        UpdateGeometry();
        if (!a && !b)
        {
            CompositionTarget.Rendering -= OnRender;
            _animating = false;
        }
    }

    void UpdateGeometry()
    {
        double w = Math.Max(40, _w.X), h = Math.Max(16, _h.X);
        double W = w + 2 * Ear;
        double r = Math.Min(h / 2, h > 70 ? 26 : 15);
        NotchHost.Width = W;
        NotchHost.Height = h;

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, 0), true, true);
            c.ArcTo(new Point(Ear, Ear), new Size(Ear, Ear), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(Ear, h - r), true, true);
            c.ArcTo(new Point(Ear + r, h), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
            c.LineTo(new Point(W - Ear - r, h), true, true);
            c.ArcTo(new Point(W - Ear, h - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
            c.LineTo(new Point(W - Ear, Ear), true, true);
            c.ArcTo(new Point(W, 0), new Size(Ear, Ear), 0, false, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        Shape.Data = g;
        Body.Clip = g;
    }

    void SetMode(Mode m)
    {
        FrameworkElement next = m switch
        {
            Mode.Volume => VolumeView,
            Mode.Toast => ToastView,
            Mode.Expanded => ExpandedView,
            _ => IdleView,
        };
        _mode = m;
        SetClickThrough(m != Mode.Expanded);
        if (m != Mode.Idle) SetGhost(false);
        if (next != _view)
        {
            _view.IsHitTestVisible = false;
            Fade(_view, 0, 90);
            next.IsHitTestVisible = true;
            Fade(next, 1, 220, 70);
            _view = next;
        }
        _stats.Active = StatsNeeded();
        UpdateEq();
        Resize();
        ApplyFullscreenVisibility();
    }

    static void Fade(UIElement el, double to, int ms, int delay = 0)
    {
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    static void AnimateScale(ScaleTransform st, double to, int ms = 180)
    {
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    void ShowTransient(Mode m, double seconds)
    {
        if (S.Hidden) return;
        SetMode(m);
        _transient.Stop();
        _transient.Interval = TimeSpan.FromSeconds(seconds);
        _transient.Start();
    }

    // ================================================================
    //  Fare etkilesimi
    // ================================================================

    void Notch_MouseEnter(object sender, MouseEventArgs e)
    {
        _leaveDelay.Stop();
        if (_mode != Mode.Expanded) _hoverDelay.Start();
    }

    void Notch_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverDelay.Stop();
        if (_mode == Mode.Expanded) _leaveDelay.Start();
    }

    void Notch_Click(object sender, MouseButtonEventArgs e)
    {
        if (_mode != Mode.Expanded) { _hoverDelay.Stop(); Expand(); }
    }

    void Expand(RadioButton? tab = null)
    {
        _transient.Stop();
        if (tab != null) tab.IsChecked = true;
        else if (_media.Playing) TabMedia.IsChecked = true;
        UpdateMediaPanel();
        SetMode(Mode.Expanded);
    }

    void Collapse()
    {
        _pasteMode = false;
        EndTyping();
        SetMode(Mode.Idle);
        PumpToasts(); // panel acikken gelen bildirimler
    }

    void OnClipboardHotkey()
    {
        if (_mode == Mode.Expanded && _pasteMode) { Collapse(); return; } // ikinci basis kapatir
        _pasteMode = true;
        _pasteEntered = false;
        _pasteDeadline = DateTime.UtcNow.AddSeconds(10);
        Expand(TabClip);
    }

    void ClipClear_Click(object sender, RoutedEventArgs e) => _clip.Clear();

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelMedia == null) return; // InitializeComponent sirasinda
        PanelMedia.Visibility = TabMedia.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelCal.Visibility = TabCal.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelSys.Visibility = TabSys.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelTools.Visibility = TabTools.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelTray.Visibility = TabTray.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelClip.Visibility = TabClip.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelAudio.Visibility = TabAudio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelDevices.Visibility = TabDevices.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelNotes.Visibility = TabNotes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OnTabShown();
        if (TabClip.IsChecked == true) ClipScroll.ScrollToTop();
        _stats.Active = StatsNeeded();
        if (TabCal.IsChecked == true) BuildCalendar();
    }

    // ================================================================
    //  Tam ekran: bosta iken gizlen
    // ================================================================

    void CheckFullscreen()
    {
        bool fs = Native.IsForegroundFullscreen(_hwnd);
        if (fs == _fullscreen) return;
        _fullscreen = fs;
        _stats.Active = StatsNeeded();
        _ping.Active = HudActive;
        UpdateIdle();
        ApplyFullscreenVisibility();
    }

    void ApplyFullscreenVisibility()
    {
        bool hide = (S.Hidden && _mode != Mode.Expanded) ||
                    (S.HideInFullscreen && _fullscreen && _mode == Mode.Idle && !HudActive);
        double target = hide ? 0 : 1;
        if (Math.Abs(NotchHost.Opacity - target) > 0.01 || NotchHost.HasAnimatedProperties)
            Fade(NotchHost, target, hide ? 300 : 120);
        NotchHost.IsHitTestVisible = !hide;
    }

    // ================================================================
    //  Saat / bosta gorunumu
    // ================================================================

    void OnClock()
    {
        var now = DateTime.Now;
        IdleClock.Text = now.ToString("HH:mm", Tr);
        HdrClock.Text = now.ToString("HH:mm", Tr);
        HdrDate.Text = now.ToString("d MMMM dddd", Tr);
        if (now.Date != _calBuiltFor) BuildCalendar();
        if (_mode == Mode.Expanded && TabMedia.IsChecked == true) UpdateMediaProgress();
    }

    void UpdateIdle()
    {
        bool media = _media.HasMedia;
        bool timer = _timer.Active;

        IdleArt.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        IdleArtBrush.ImageSource = _media.Art;
        if (_media.Art == null) IdleArt.Background = new SolidColorBrush(Color.FromArgb(0x40, _accent.R, _accent.G, _accent.B));
        else IdleArt.Background = IdleArtBrush;

        IdleLeftIcon.Visibility = !media && timer ? Visibility.Visible : Visibility.Collapsed;
        IdleTimer.Visibility = timer ? Visibility.Visible : Visibility.Collapsed;
        IdleTimer.Text = TimerService.Format(_timer.Remaining);
        EqBars.Visibility = media && !timer ? Visibility.Visible : Visibility.Collapsed;

        UpdateIdleExtras();
        UpdateEq();
        if (_mode == Mode.Idle) Resize();
    }

    void UpdateEq()
    {
        bool run = _mode == Mode.Idle && EqBars.Visibility == Visibility.Visible && _media.Playing;
        if (run && !_eq.IsEnabled) _eq.Start();
        if (!run && _eq.IsEnabled)
        {
            _eq.Stop();
            foreach (Rectangle r in EqBars.Children) AnimateHeight(r, 3);
        }
    }

    void AnimateEq()
    {
        foreach (Rectangle r in EqBars.Children) AnimateHeight(r, 3 + _rng.NextDouble() * 11);
    }

    static void AnimateHeight(FrameworkElement el, double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        // 24 fps yeterli; 60 fps saydam pencerede gereksiz CPU harcar
        Timeline.SetDesiredFrameRate(a, 24);
        el.BeginAnimation(HeightProperty, a);
    }

    void SetAccent(Color c)
    {
        _accent = c;
        var b = new SolidColorBrush(c);
        b.Freeze();
        foreach (Rectangle r in EqBars.Children) r.Fill = b;
        MediaFill.Background = b;
    }

    // ================================================================
    //  Ses
    // ================================================================

    void OnVolume(string name, int vol, bool muted, bool fromEndpoint)
    {
        // Ayni degisiklik hem Windows'tan hem Sonar'dan gelebilir: Windows onceliklidir
        if (!S.ShowVolume || (!fromEndpoint && !S.WatchSonar)) return;
        if (fromEndpoint) _lastEndpointVolume = DateTime.UtcNow;
        else if ((DateTime.UtcNow - _lastEndpointVolume).TotalMilliseconds < 700) return;

        if (_mode == Mode.Expanded)
        {
            HdrVolume.Text = $"{name} · {(muted ? "Sessiz" : "%" + vol)}";
            HdrVolume.BeginAnimation(OpacityProperty, null);
            HdrVolume.Opacity = 1;
            _hdrVolHide.Stop();
            _hdrVolHide.Start();
            return;
        }

        bool wasVisible = _mode == Mode.Volume;
        VolName.Text = name;
        VolValue.Text = muted ? "—" : vol.ToString(Tr);
        VolValue.Foreground = muted ? MutedBrush : (Brush)FindResource("Fg");
        VolIcon.Text = VolIconFor(vol, muted);
        VolIcon.Foreground = muted ? MutedBrush : (Brush)FindResource("Fg");
        VolFill.Background = muted ? MutedBrush : (Brush)FindResource("Accent");
        if (wasVisible) AnimateScale(VolScale, vol / 100.0, 140);
        else { VolScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); VolScale.ScaleX = vol / 100.0; }
        // Ekranda bildirim varsa kaybolmasin: siranin basina geri koy, ses bitince tekrar gosterilir
        if (_mode == Mode.Toast && _currentToast != null)
        {
            _toastQueue.AddFirst(_currentToast with { Time = DateTime.Now });
            _currentToast = null;
        }
        ShowTransient(Mode.Volume, S.VolumeSeconds);
    }

    static string VolIconFor(int v, bool muted) =>
        muted ? "" : v == 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";

    // ================================================================
    //  Bildirimler
    // ================================================================

    // Bildirim sirasi: hicbir bildirim kaybolmaz. Ses gostergesi araya girerse ekrandaki bildirim
    // siranin basina geri konur; panel aciksa bildirimler panel kapaninca gosterilir.
    sealed record ToastItem(string Icon, string Title, string Sub, BitmapSource? Art, string Kind, DateTime Time);
    readonly LinkedList<ToastItem> _toastQueue = new();
    ToastItem? _currentToast;

    void ShowToast(string icon, string title, string sub, BitmapSource? art = null, string kind = "")
    {
        if (S.Hidden) return;
        var item = new ToastItem(icon, title, sub, art, kind, DateTime.Now);
        if (kind == "track")
        {
            // Art arda sarki degisirse yalnizca sonuncusu kalsin
            for (var n = _toastQueue.First; n != null;)
            {
                var next = n.Next;
                if (n.Value.Kind == "track") _toastQueue.Remove(n);
                n = next;
            }
            if (_mode == Mode.Toast && _currentToast?.Kind == "track") { DisplayToast(item); return; }
        }
        _toastQueue.AddLast(item);
        while (_toastQueue.Count > 12) _toastQueue.RemoveFirst();
        PumpToasts();
    }

    /// <summary>Notch musaitse siradaki bildirimi gosterir.</summary>
    void PumpToasts()
    {
        if (_mode is Mode.Expanded or Mode.Volume) return; // bittiklerinde tekrar cagrilir
        if (_mode == Mode.Toast && _currentToast != null)
        {
            // Sirada bekleyen varsa ekrandakini kisalt (en az 1.5 sn gorunsun)
            _transient.Interval = TimeSpan.FromSeconds(Math.Min(_transient.Interval.TotalSeconds, 1.5));
            return;
        }
        // 2 dakikadan eski bildirimleri atla
        while (_toastQueue.First is { } f && (DateTime.Now - f.Value.Time).TotalMinutes > 2) _toastQueue.RemoveFirst();
        if (_toastQueue.First is not { } first) return;
        _toastQueue.RemoveFirst();
        DisplayToast(first.Value);
    }

    void DisplayToast(ToastItem t)
    {
        _currentToast = t;
        ToastTitle.Text = t.Title;
        ToastSub.Text = t.Sub;
        ToastSub.Visibility = string.IsNullOrEmpty(t.Sub) ? Visibility.Collapsed : Visibility.Visible;
        if (t.Art != null)
        {
            ToastArtBrush.ImageSource = t.Art;
            ToastArt.Visibility = Visibility.Visible;
            ToastIconBg.Visibility = Visibility.Collapsed;
        }
        else
        {
            ToastIcon.Text = t.Icon;
            ToastArt.Visibility = Visibility.Collapsed;
            ToastIconBg.Visibility = Visibility.Visible;
        }
        double secs = _toastQueue.Count > 0 ? Math.Min(S.ToastSeconds, 2.5) : S.ToastSeconds;
        ShowTransient(Mode.Toast, secs);
    }

    /// <summary>Gecici gorunum (ses/bildirim) suresi doldu.</summary>
    void OnTransientEnded()
    {
        _transient.Stop();
        if (_mode == Mode.Expanded) return;
        _currentToast = null;
        if (_toastQueue.Count > 0 && !S.Hidden)
        {
            SetMode(Mode.Idle);     // gorunumler arasi gecis animasyonu icin
            PumpToasts();
        }
        else SetMode(Mode.Idle);
    }

    void OnCopied(string kind, string? text)
    {
        if (kind == "image") { OnImageCopied(); return; }
        if (!S.NotifyCopy) return;
        switch (kind)
        {
            case "text":
                var oneLine = string.Join(" ", text!.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
                if (oneLine.Length > 80) oneLine = oneLine[..80] + "…";
                ShowToast("", "Kopyalandı", oneLine);
                break;
            case "secret": ShowToast("", "Kopyalandı", "Gizli içerik · geçmişe kaydedilmedi"); break;
            case "image": OnImageCopied(); break;
            case "files": ShowToast("", $"{text} dosya kopyalandı", ""); break;
        }
    }

    void RebuildClipList()
    {
        ClipList.Children.Clear();
        foreach (var item in _clip.History)
        {
            var oneLine = string.Join(" ", item.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            var captured = item;

            var remove = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Content = "", FontSize = 9, Width = 22, Height = 22,
                VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden,
                ToolTip = "Geçmişten sil",
            };
            remove.Click += (_, e) => { e.Handled = true; _clip.Remove(captured); };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = oneLine, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 34, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);

            var btn = new Button
            {
                Style = (Style)FindResource("ClipItem"),
                Content = row,
                Margin = new Thickness(0, 0, 0, 2),
                ToolTip = item.Length > 400 ? item[..400] + "…" : item,
            };
            btn.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
            btn.MouseLeave += (_, _) => remove.Visibility = Visibility.Hidden;
            btn.Click += (_, _) =>
            {
                // Sadece panoya kopyala (yapistirma kullanicida: Ctrl+V)
                _clip.CopyAgain(captured);
                ShowClipCopied();
            };
            ClipList.Children.Add(btn);
        }
        ClipEmpty.Visibility = _clip.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipClearBtn.Visibility = _clip.History.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ClipHeader.Text = ClipHeaderText();
    }

    string ClipHeaderText() =>
        $"Pano geçmişi · {_clip.History.Count}/{ClipboardService.MaxHistory} · tıkla → kopyala" +
        (_hotkeyLabel != null ? $" · {_hotkeyLabel}" : "");

    /// <summary>Baslikta kisa "Kopyalandi" geri bildirimi; kisayolla acildiysa paneli kapatir.</summary>
    void ShowClipCopied()
    {
        ClipHeader.Text = "✓ Panoya kopyalandı · Ctrl+V ile yapıştır";
        ClipHeader.Foreground = (Brush)FindResource("Accent");
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_pasteMode ? 600 : 1500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            ClipHeader.Text = ClipHeaderText();
            ClipHeader.Foreground = SubBrush;
            if (_pasteMode && _mode == Mode.Expanded) Collapse();
        };
        t.Start();
    }

    // ================================================================
    //  Medya
    // ================================================================

    void OnMediaChanged()
    {
        if (S.AccentFromArt && _media.ArtColor is Color c) SetAccent(c); else SetAccent(BaseAccent);
        UpdateIdle();
        UpdateMediaPanel();
    }

    void OnTrackChanged()
    {
        if (!_media.Playing || !S.NotifyTrack || FocusActive) return;
        ShowToast("", _media.Title, _media.Artist, _media.Art, "track");
    }

    void UpdateMediaPanel()
    {
        bool has = _media.HasMedia;
        MediaContent.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        MediaEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (!has) return;

        MediaTitle.Text = _media.Title;
        MediaArtist.Text = _media.Artist;
        MediaApp.Text = _media.AppName.ToUpper(Tr);
        PlayBtn.Content = _media.Playing ? "" : "";
        if (_media.Art != null)
        {
            MediaArt.Background = new ImageBrush(_media.Art) { Stretch = Stretch.UniformToFill };
            MediaArtPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            MediaArt.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
            MediaArtPlaceholder.Visibility = Visibility.Visible;
        }
        UpdateMediaProgress();
    }

    void UpdateMediaProgress()
    {
        var dur = _media.Duration;
        var pos = _media.Position;
        bool hasTl = dur > TimeSpan.Zero;
        MediaPos.Text = hasTl ? FormatTime(pos) : "";
        MediaDur.Text = hasTl ? FormatTime(dur) : "";
        AnimateScale(MediaScale, hasTl ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) : 0, 400);
    }

    static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void Play_Click(object sender, RoutedEventArgs e) => _media.TogglePlay();
    void Next_Click(object sender, RoutedEventArgs e) => _media.Next();
    void Prev_Click(object sender, RoutedEventArgs e) => _media.Previous();

    // ================================================================
    //  Takvim
    // ================================================================

    DateTime _calBuiltFor;

    void BuildCalendar()
    {
        var today = DateTime.Today;
        _calBuiltFor = today;
        CalDayName.Text = today.ToString("dddd", Tr);
        CalDayNum.Text = today.Day.ToString(Tr);
        CalMonthYear.Text = today.ToString("MMMM yyyy", Tr);
        int week = ISOWeek.GetWeekOfYear(today);
        int left = (new DateTime(today.Year, 12, 31) - today).Days;
        CalWeek.Text = $"{week}. hafta · yıl sonuna {left} gün";

        CalGridTitle.Text = _calMonth.ToString("MMMM yyyy", Tr);
        CalGrid.Children.Clear();
        foreach (var d in new[] { "Pt", "Sa", "Ça", "Pe", "Cu", "Ct", "Pz" })
            CalGrid.Children.Add(new TextBlock
            {
                Text = d, Foreground = SubBrush, FontSize = 10.5,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });

        int offset = ((int)_calMonth.DayOfWeek + 6) % 7;
        var start = _calMonth.AddDays(-offset);
        for (int i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            bool isToday = day == today;
            bool inMonth = day.Month == _calMonth.Month;
            var cell = new Border
            {
                Width = 26, Height = 21, CornerRadius = new CornerRadius(10.5),
                Background = isToday ? (Brush)FindResource("Accent") : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = day.Day.ToString(Tr),
                    FontSize = 11.5,
                    FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = isToday ? Brushes.White
                               : inMonth ? (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? SubBrush : (Brush)FindResource("Fg"))
                               : new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            CalGrid.Children.Add(cell);
        }
    }

    void CalPrev_Click(object sender, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(-1); BuildCalendar(); }
    void CalNext_Click(object sender, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(1); BuildCalendar(); }

    // ================================================================
    //  Sistem
    // ================================================================

    void OnStats(SystemStats s)
    {
        _lastStats = s;
        if (HudActive) UpdateHud();
        CpuVal.Text = $"%{s.Cpu:0}";
        GpuVal.Text = $"%{s.Gpu:0}";
        GpuDetail.Text = s.GpuTemp is int gt ? $"{gt}°C" : "";
        RamVal.Text = $"%{s.Ram:0}";
        RamDetail.Text = $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0} GB";
        NetDown.Text = FormatRate(s.DownBps);
        NetUp.Text = FormatRate(s.UpBps);

        Push(_cpuHist, s.Cpu); Push(_gpuHist, s.Gpu); Push(_ramHist, s.Ram); Push(_netHist, s.DownBps);
        DrawSpark(CpuSpark, _cpuHist, 100);
        DrawSpark(GpuSpark, _gpuHist, 100);
        DrawSpark(RamSpark, _ramHist, 100);
        DrawSpark(NetSpark, _netHist, Math.Max(1, _netHist.Max()));
    }

    static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > 30) q.Dequeue();
    }

    static void DrawSpark(Polyline line, Queue<double> data, double max)
    {
        double w = ((FrameworkElement)line.Parent).ActualWidth, h = line.Height;
        if (w <= 0 || data.Count < 2) return;
        var pts = new PointCollection();
        int i = 0, n = 30;
        foreach (var v in data)
        {
            double x = w * (n - data.Count + i) / (n - 1);
            double y = h - 2 - (h - 4) * Math.Clamp(v / max, 0, 1);
            pts.Add(new Point(x, y));
            i++;
        }
        line.Points = pts;
    }

    static string FormatRate(double bps) => bps switch
    {
        >= 1024 * 1024 => $"{bps / 1024 / 1024:0.0} MB/s",
        >= 1024 => $"{bps / 1024:0} KB/s",
        _ => $"{bps:0} B/s",
    };

    // ================================================================
    //  Saklama tepsisi
    // ================================================================

    void Notch_DragEnter(object sender, DragEventArgs e)
    {
        if (_draggingOut || !CanAcceptDrop(e.Data))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        if (_mode != Mode.Expanded || TabTray.IsChecked != true) Expand(TabTray);
        SetDropHighlight(true);
    }

    void Notch_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_draggingOut && CanAcceptDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Notch_DragLeave(object sender, DragEventArgs e) => SetDropHighlight(false);

    void Notch_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        if (_draggingOut) return;
        AcceptDrop(e.Data);
        e.Handled = true;
    }

    void SetDropHighlight(bool on)
    {
        TrayDropZone.Background = on
            ? new SolidColorBrush(Color.FromArgb(0x30, _accent.R, _accent.G, _accent.B))
            : new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
        TrayEmptyText.Text = on ? "Bırak" : "Dosyaları buraya sürükle";
    }

    void RebuildTray()
    {
        TrayList.Children.Clear();
        foreach (var path in _tray.Items) TrayList.Children.Add(MakeTrayTile(path));
        bool any = _tray.Items.Count > 0;
        TrayEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        TrayContent.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        TrayCount.Text = $"{_tray.Items.Count} öğe · çift tıkla: aç · sağ tık: klasörde göster";
    }

    FrameworkElement MakeTrayTile(string path)
    {
        string name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = path;

        var remove = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "", FontSize = 9, Width = 20, Height = 20,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x30, 0x30, 0x36)),
            Visibility = Visibility.Hidden, ToolTip = "Tepsiden kaldır",
        };
        remove.Click += (_, _) => _tray.Remove(path);

        var tile = new Border
        {
            Width = 86, Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = path,
            Padding = new Thickness(4, 6, 4, 4),
            Child = new Grid
            {
                Children =
                {
                    new StackPanel
                    {
                        Children =
                        {
                            new Image
                            {
                                Source = TrayService.GetThumbnail(path), Width = 44, Height = 44,
                                Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
                            },
                            new TextBlock
                            {
                                Text = name, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                                TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
                                MaxHeight = 30, Margin = new Thickness(0, 6, 0, 0),
                            },
                        },
                    },
                    remove,
                },
            },
        };

        tile.MouseEnter += (_, _) => { tile.Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)); remove.Visibility = Visibility.Visible; };
        tile.MouseLeave += (_, _) => { tile.Background = Brushes.Transparent; remove.Visibility = Visibility.Hidden; };
        tile.MouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this);
            if (e.ClickCount == 2) OpenPath(path);
        };
        tile.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || !IsDragGesture(e)) return;
            StartDragOut(new[] { path });
        };
        tile.MouseRightButtonUp += (_, _) => ShowInFolder(path);
        return tile;
    }

    bool IsDragGesture(MouseEventArgs e)
    {
        var d = e.GetPosition(this) - _dragStart;
        return Math.Abs(d.X) > SystemParameters.MinimumHorizontalDragDistance ||
               Math.Abs(d.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    void StartDragOut(string[] paths)
    {
        _tray.Prune();
        paths = paths.Where(p => System.IO.File.Exists(p) || System.IO.Directory.Exists(p)).ToArray();
        if (paths.Length == 0) return;
        _draggingOut = true;
        try
        {
            // Yalnizca kopyala/baglanti: orijinal dosya asla tasinmaz
            DragDrop.DoDragDrop(TrayList, new DataObject(DataFormats.FileDrop, paths),
                                DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch { }
        finally { _draggingOut = false; }
    }

    void TrayDragAll_Down(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    void TrayDragAll_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && IsDragGesture(e))
        {
            ((UIElement)sender).ReleaseMouseCapture();
            StartDragOut(_tray.Items.ToArray());
        }
    }

    void TrayClear_Click(object sender, RoutedEventArgs e) => _tray.Clear();

    void TrayScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        TrayScroll.ScrollToHorizontalOffset(TrayScroll.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    static void OpenPath(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }

    static void ShowInFolder(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch { }
    }

    // ================================================================
    //  Zamanlayici
    // ================================================================

    void TimerPreset_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender).Tag;
        if (tag == "+1") { _timer.Add(TimeSpan.FromMinutes(1)); if (!_timer.Running) _timer.Resume(); }
        else _timer.Start(TimeSpan.FromMinutes(int.Parse(tag, CultureInfo.InvariantCulture)));
    }

    void TimerPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_pomo && !_timer.Active) { StartPomoPhase(); return; }
        if (_timer.Running) _timer.Pause();
        else if (_timer.Remaining > TimeSpan.Zero) _timer.Resume();
        else _timer.Start(TimeSpan.FromMinutes(5));
    }

    void TimerReset_Click(object sender, RoutedEventArgs e) { _timer.Reset(); ResetPomo(); }

    void OnTimerTick()
    {
        TimerText.Text = TimerService.Format(_timer.Remaining);
        TimerScale.ScaleX = _timer.Active ? _timer.Progress : 0;
        TimerPlayBtn.Content = _timer.Running ? "" : "";
        IdleTimer.Text = TimerService.Format(_timer.Remaining);
        bool timerVisible = IdleTimer.Visibility == Visibility.Visible;
        if (timerVisible != _timer.Active) UpdateIdle();
    }

    void OnTimerFinished()
    {
        SystemSounds.Exclamation.Play();
        if (OnPomoFinished()) { UpdateIdle(); return; }
        ShowToast("\uE916", "Süre doldu", DateTime.Now.ToString("HH:mm", Tr));
        UpdateIdle();
    }
}
