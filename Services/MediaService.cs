using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Notch.Services;

/// <summary>Windows medya kontrolu (Spotify, tarayici, Medya Oynatici...) uzerinden calan icerigi izler.</summary>
public sealed class MediaService
{
    readonly Dispatcher _ui;
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _s;
    bool _initial = true;

    TimeSpan _pos, _end;
    DateTimeOffset _posAt;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string AppName { get; private set; } = "";
    public bool Playing { get; private set; }
    public BitmapSource? Art { get; private set; }
    public Color? ArtColor { get; private set; }
    public bool HasMedia => _s != null && Title.Length > 0;

    public TimeSpan Duration => _end;
    public TimeSpan Position
    {
        get
        {
            var p = _pos;
            if (Playing) p += DateTimeOffset.Now - _posAt;
            if (_end > TimeSpan.Zero && p > _end) p = _end;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }

    public event Action? Changed;
    public event Action? TrackChanged;

    public MediaService(Dispatcher ui) => _ui = ui;

    public async Task StartAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => _ui.BeginInvoke(Attach);
            _mgr.SessionsChanged += (_, _) => _ui.BeginInvoke(Attach);
            Attach();
        }
        catch { /* medya API'si kullanilamiyor */ }
    }

    void Attach()
    {
        var s = _mgr?.GetCurrentSession();
        if (_s != null)
        {
            if (s != null && s.SourceAppUserModelId == _s.SourceAppUserModelId) return;
            _s.MediaPropertiesChanged -= OnProps;
            _s.PlaybackInfoChanged -= OnPlayback;
            _s.TimelinePropertiesChanged -= OnTimeline;
        }
        _s = s;
        _initial = true;
        Title = Artist = AppName = "";
        Art = null; ArtColor = null; Playing = false;
        if (s == null) { Changed?.Invoke(); return; }

        AppName = PrettyApp(s.SourceAppUserModelId);
        s.MediaPropertiesChanged += OnProps;
        s.PlaybackInfoChanged += OnPlayback;
        s.TimelinePropertiesChanged += OnTimeline;
        UpdatePlayback();
        UpdateTimeline();
        _ = UpdatePropsAsync();
    }

    void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        _ui.BeginInvoke(() => _ = UpdatePropsAsync());
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        _ui.BeginInvoke(UpdatePlayback);
    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        _ui.BeginInvoke(UpdateTimeline);

    async Task UpdatePropsAsync()
    {
        var s = _s;
        if (s == null) return;
        GlobalSystemMediaTransportControlsSessionMediaProperties p;
        try { p = await s.TryGetMediaPropertiesAsync(); } catch { return; }
        if (!ReferenceEquals(s, _s)) return;

        string t = p.Title ?? "", a = p.Artist ?? "";
        bool trackChanged = t != Title || a != Artist;
        Title = t; Artist = a;

        if ((trackChanged || Art == null) && p.Thumbnail != null)
        {
            if (trackChanged) { Art = null; ArtColor = null; }
            try
            {
                using var ras = await p.Thumbnail.OpenReadAsync();
                using var st = ras.AsStreamForRead();
                var ms = new MemoryStream();
                await st.CopyToAsync(ms);
                ms.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 240;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                if (ReferenceEquals(s, _s) && Title == t)
                {
                    Art = bmp;
                    ArtColor = ColorUtil.Vibrant(bmp);
                }
            }
            catch { }
        }
        else if (trackChanged)
        {
            Art = null; ArtColor = null;
        }

        Changed?.Invoke();
        if (trackChanged && !_initial && t.Length > 0) TrackChanged?.Invoke();
        _initial = false;
    }

    void UpdatePlayback()
    {
        if (_s == null) return;
        try
        {
            var info = _s.GetPlaybackInfo();
            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing != Playing)
            {
                // Pozisyon tahminini dondur/yeniden baslat
                _pos = Position;
                _posAt = DateTimeOffset.Now;
                Playing = playing;
            }
        }
        catch { }
        Changed?.Invoke();
    }

    void UpdateTimeline()
    {
        if (_s == null) return;
        try
        {
            var tl = _s.GetTimelineProperties();
            _end = tl.EndTime - tl.StartTime;
            _pos = tl.Position;
            _posAt = tl.LastUpdatedTime.Year < 2000 ? DateTimeOffset.Now : tl.LastUpdatedTime;
        }
        catch { }
        Changed?.Invoke();
    }

    public async void Seek(TimeSpan pos)
    {
        try
        {
            if (_s == null) return;
            var tl = _s.GetTimelineProperties();
            await _s.TryChangePlaybackPositionAsync((tl.StartTime + pos).Ticks);
            _pos = pos;
            _posAt = DateTimeOffset.Now;
            Changed?.Invoke();
        }
        catch { }
    }

    public async void TogglePlay() { try { if (_s != null) await _s.TryTogglePlayPauseAsync(); } catch { } }
    public async void Next() { try { if (_s != null) await _s.TrySkipNextAsync(); } catch { } }
    public async void Previous() { try { if (_s != null) await _s.TrySkipPreviousAsync(); } catch { } }

    static string PrettyApp(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        int bang = id.IndexOf('!');
        if (bang >= 0) id = id[(bang + 1)..];
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];
        int us = id.IndexOf('_');
        if (us > 0) id = id[..us];
        int dot = id.LastIndexOf('.');
        if (dot >= 0 && dot < id.Length - 1) id = id[(dot + 1)..];
        if (id.Equals("ZuneMusic", StringComparison.OrdinalIgnoreCase)) id = "Medya Oynatıcı";
        return id.Length > 0 ? char.ToUpper(id[0]) + id[1..] : id;
    }
}
