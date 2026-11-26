using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Notch.Services;

namespace Notch;

/// <summary>SteelSeries Sonar GAME profili: ses sekmesindeki secici ve otomatik gecis.</summary>
public partial class MainWindow
{
    readonly GameDetector _games = new();
    readonly DispatcherTimer _autoProfile = new() { Interval = TimeSpan.FromSeconds(2) };
    bool _autoBusy;
    string _autoSig = "";                       // ayarlar degisince kurallar yeniden uygulanir
    (SonarScene Scene, string ProfileId)? _autoApplied, _autoPending;
    DateTime _autoPendingSince;

    /// <summary>Ayarlar ekrani icin.</summary>
    public Task<List<SonarProfile>?> GetSonarProfiles() => _sonar.GetGameProfiles();

    void StartAutoProfile()
    {
        _autoProfile.Tick += async (_, _) => await AutoProfileTick();
        _autoProfile.Start();
    }

    /// <summary>
    /// Profil yalnizca durum degisince uygulanir: elle yapilan secim bir sonraki durum degisikligine kadar kalir.
    /// </summary>
    async Task AutoProfileTick()
    {
        var sig = string.Join("|", S.SonarAutoSwitch, S.SonarGameProfile, S.SonarMusicProfile, S.SonarMeetingProfile, S.SonarIdleProfile);
        if (sig != _autoSig) { _autoSig = sig; _autoApplied = _autoPending = null; }
        if (!S.SonarAutoSwitch || _autoBusy) return;

        _autoBusy = true;
        try
        {
            var meetingApps = ProfileRules.Names(S.SonarMeetingApps);
            // Gorusme = listedeki bir uygulama mikrofonu kullaniyor (arka planda acik durmasi yetmez)
            bool meeting = _mic.Users.Any(meetingApps.Contains);
            bool music = _media.HasMedia && _media.Playing;
            var extra = ProfileRules.Names(S.SonarExtraGames);
            var ignored = ProfileRules.Names(S.SonarIgnoredGames);
            bool fullscreen = S.SonarFullscreenIsGame;
            bool game = await Task.Run(() => _games.Find(extra, ignored, fullscreen)) != null;

            var want = ProfileRules.Pick(S, meeting, game, music);
            if (want != _autoPending) { _autoPending = want; _autoPendingSince = DateTime.UtcNow; }
            if (want == null)
            {
                // Kural yok: ayni duruma geri donulurse profil yeniden uygulansin
                if (DateTime.UtcNow - _autoPendingSince >= SceneDelay(null)) _autoApplied = null;
                return;
            }
            if (want == _autoApplied || DateTime.UtcNow - _autoPendingSince < SceneDelay(want.Value.Scene)) return;

            var current = await _sonar.GetSelectedGameProfile();
            if (current == null) return; // Sonar kapali: sonra yeniden dene
            if (current != want.Value.ProfileId)
            {
                if (!await _sonar.SelectProfile(want.Value.ProfileId)) return;
                _sonarSelected = want.Value.ProfileId;
                MarkSonarSelected();
                if (S.SonarNotifySwitch) await ShowProfileToast(want.Value.Scene, want.Value.ProfileId);
            }
            _autoApplied = want;
        }
        catch { }
        finally { _autoBusy = false; }
    }

    // Kisa sureli degisikliklerde (sarki arasi, duraklatma) profil ziplamasin
    static TimeSpan SceneDelay(SonarScene? scene) => TimeSpan.FromSeconds(scene switch
    {
        SonarScene.Meeting => 1,
        SonarScene.Game => 1,
        SonarScene.Music => 4,
        _ => 8,
    });

    async Task ShowProfileToast(SonarScene scene, string id)
    {
        if (_sonarProfiles.All(p => p.Id != id))
        {
            var list = await _sonar.GetGameProfiles();
            if (list != null) { _sonarProfiles = list; RebuildSonar(); }
        }
        var name = _sonarProfiles.FirstOrDefault(p => p.Id == id)?.Name ?? "";
        var why = scene switch
        {
            SonarScene.Meeting => "Görüşme",
            SonarScene.Game => "Oyun",
            SonarScene.Music => "Müzik",
            _ => "Boşta",
        };
        ShowToast("", $"Sonar profili: {name}", why);
    }

    List<SonarProfile> _sonarProfiles = new();
    string? _sonarSelected;
    bool _sonarBusy;
    readonly List<Button> _sonarButtons = new();

    /// <summary>Sekme acilinca listeyi bastan alir; zamanlayicida yalnizca secili profili yeniler.</summary>
    async Task RefreshSonar(bool reload)
    {
        if (reload) ShowSonarAll(false);
        if (_sonarBusy || _mode != Mode.Expanded || TabAudio.IsChecked != true) return;
        _sonarBusy = true;
        try
        {
            var selected = await _sonar.GetSelectedGameProfile();
            if (selected == null) { HideSonar(); return; }

            // Bilinmeyen bir profil secildiyse (GG'de yeni olusturulmus) listeyi yenile
            if (reload || SonarBar.Visibility != Visibility.Visible || _sonarProfiles.All(p => p.Id != selected))
            {
                var list = await _sonar.GetGameProfiles();
                if (list == null) { HideSonar(); return; }
                _sonarSelected = selected;
                if (!list.SequenceEqual(_sonarProfiles) || SonarFavList.Children.Count == 0)
                {
                    _sonarProfiles = list;
                    RebuildSonar();
                }
                SonarBar.Visibility = Visibility.Visible;
            }
            _sonarSelected = selected;
            MarkSonarSelected();
        }
        finally { _sonarBusy = false; }
    }

    void HideSonar()
    {
        SonarBar.Visibility = Visibility.Collapsed;
        ShowSonarAll(false);
    }

    void RebuildSonar()
    {
        _sonarButtons.Clear();
        SonarFavList.Children.Clear();
        SonarOwnList.Children.Clear();
        SonarPresetList.Children.Clear();

        foreach (var p in _sonarProfiles.Where(p => p.Favorite).OrderBy(p => p.FavoritePosition))
            SonarFavList.Children.Add(SonarButton(p, new Thickness(0, 0, 6, 0)));
        if (SonarFavList.Children.Count == 0)
            SonarFavList.Children.Add(new TextBlock { Text = "Sonar'da favori profil yok", Foreground = SubBrush, FontSize = 12 });
        if (SonarAll.Visibility == Visibility.Visible) BuildSonarAll();
    }

    // Yuzlerce hazir profil var: yalnizca "Diger" acilinca olusturulur
    void BuildSonarAll()
    {
        if (SonarOwnList.Children.Count > 0 || SonarPresetList.Children.Count > 0) return;
        foreach (var p in _sonarProfiles.OrderBy(p => p.Name, System.StringComparer.CurrentCultureIgnoreCase))
            (p.Preset ? SonarPresetList : SonarOwnList).Children.Add(SonarButton(p, new Thickness(0, 0, 6, 6)));
        MarkSonarSelected();
    }

    Button SonarButton(SonarProfile p, Thickness margin)
    {
        var btn = new Button
        {
            Style = (Style)FindResource("PillButton"),
            Content = new TextBlock { Text = p.Name }, // TextBlock: "_" erisim tusu sayilmasin
            Tag = p.Id, Margin = margin,
        };
        btn.Click += async (_, _) => await SelectSonar(p);
        _sonarButtons.Add(btn);
        return btn;
    }

    async Task SelectSonar(SonarProfile p)
    {
        if (!await _sonar.SelectProfile(p.Id))
        {
            ShowToast("", "Profil değiştirilemedi", "Sonar'a ulaşılamadı");
            return;
        }
        _sonarSelected = p.Id;
        ShowSonarAll(false);
        MarkSonarSelected();
    }

    void MarkSonarSelected()
    {
        foreach (var b in _sonarButtons)
        {
            // Secili degilse stildeki arka plana don (uzerine gelme efekti korunur)
            if ((string)b.Tag == _sonarSelected)
            {
                b.SetResourceReference(Control.BackgroundProperty, "Accent");
                b.Foreground = Brushes.White;
            }
            else
            {
                b.ClearValue(Control.BackgroundProperty);
                b.ClearValue(Control.ForegroundProperty);
            }
        }
    }

    void ShowSonarAll(bool show)
    {
        SonarAll.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MixerLeft.Visibility = MixerRight.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        SonarMoreBtn.Content = show ? "Kapat" : "Diğer";
        if (show) { BuildSonarAll(); SonarAllScroll.ScrollToTop(); }
    }

    void SonarMore_Click(object sender, RoutedEventArgs e) => ShowSonarAll(SonarAll.Visibility != Visibility.Visible);

    void SonarFav_Wheel(object sender, MouseWheelEventArgs e)
    {
        SonarFavScroll.ScrollToHorizontalOffset(SonarFavScroll.HorizontalOffset - e.Delta / 3.0);
        e.Handled = true;
    }
}
