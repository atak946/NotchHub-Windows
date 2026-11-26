using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Notch.Services;

namespace Notch;

/// <summary>Ayarlar: Sonar otomatik profil sayfasi.</summary>
public partial class SettingsWindow
{
    List<SonarProfile>? _profiles;

    void SonarPage()
    {
        Section("Otomatik geçiş");
        Row("Profili otomatik değiştir", "Sonar'ın GAME profili o an ne yaptığına göre değişir. Elle yaptığın seçim bir sonraki durum değişikliğine kadar korunur.",
            Toggle(() => S.SonarAutoSwitch, v => S.SonarAutoSwitch = v, immediate: true));
        Row("Geçişte bildir", null, Toggle(() => S.SonarNotifySwitch, v => S.SonarNotifySwitch = v));

        Section("Profiller");
        var status = new TextBlock { FontSize = 11.5, Foreground = SubBrush, Margin = new Thickness(0, 3, 0, 0), Text = "Profiller okunuyor…" };
        var pickers = new List<Action>
        {
            ProfileRow("Toplantı / görüşme", "Aşağıdaki listedeki bir uygulama mikrofonu kullanırken. En yüksek öncelik.",
                () => S.SonarMeetingProfile, v => S.SonarMeetingProfile = v, status),
            ProfileRow("Oyun açıkken", "Görüşme yoksa.", () => S.SonarGameProfile, v => S.SonarGameProfile = v),
            ProfileRow("Müzik dinlerken", "Oyun kapalıyken bir şey çalıyorsa (Spotify, YouTube…).",
                () => S.SonarMusicProfile, v => S.SonarMusicProfile = v),
            ProfileRow("Boştayken", "Yukarıdakilerin hiçbiri yokken.", () => S.SonarIdleProfile, v => S.SonarIdleProfile = v),
        };

        Section("Algılama");
        var appLists = new List<Action>
        {
            AppListRow("Görüşme uygulamaları", "Mikrofonu kullandıklarında görüşme sayılır. Oyunda sesli sohbet profili değiştirmesin diye Discord listede yok.",
                () => S.SonarMeetingApps, v => S.SonarMeetingApps = v),
        };
        Row("Tam ekran uygulamaları oyun say", "Steam, Epic, Riot vb. klasörlerindeki oyunlar her zaman algılanır. Bu ayar diğer çerçevesiz tam ekran oyunları da yakalar.",
            Toggle(() => S.SonarFullscreenIsGame, v => S.SonarFullscreenIsGame = v));
        appLists.Add(AppListRow("Ek oyunlar", "Kendiliğinden algılanmayan oyunlar.",
            () => S.SonarExtraGames, v => S.SonarExtraGames = v));
        appLists.Add(AppListRow("Oyun sayılmayacaklar", "Yanlışlıkla oyun sanılan uygulamalar.",
            () => S.SonarIgnoredGames, v => S.SonarIgnoredGames = v));

        LoadProfiles(pickers, status);
        _ = LoadApps(appLists);
    }

    // ---------------- Uygulama secici ----------------

    List<AppEntry>? _apps;

    async Task LoadApps(List<Action> redraws)
    {
        try { _apps = await Task.Run(AppCatalog.Load); }
        catch { _apps ??= new(); }
        foreach (var redraw in redraws) redraw();
    }

    /// <summary>Secili uygulamalar etiket olarak durur; "Uygulama ekle" listeden veya .exe dosyasindan ekler.</summary>
    Action AppListRow(string title, string desc, Func<string> get, Action<string> set)
    {
        var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var add = new Button { Style = (Style)FindResource("PillButton"), Content = "Uygulama ekle", Height = 32, Margin = new Thickness(0) };

        List<string> Selected() => ProfileRules.Names(get()).ToList();

        void Change(List<string> names)
        {
            set(string.Join(", ", names));
            Save(immediate: true);
            Redraw();
        }

        void Redraw()
        {
            chips.Children.Clear();
            foreach (var process in Selected())
            {
                var app = _apps?.FirstOrDefault(a => a.Process.Equals(process, StringComparison.OrdinalIgnoreCase));
                var content = AppLabel(app?.Path, app?.Name ?? AppCatalog.DisplayName(process));
                content.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 8, Foreground = SubBrush, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                var chip = new Button { Style = (Style)FindResource("PillButton"), Content = content, Height = 28, ToolTip = $"{process}.exe · kaldırmak için tıkla" };
                chip.Click += (_, _) => Change(Selected().Where(n => !n.Equals(process, StringComparison.OrdinalIgnoreCase)).ToList());
                chips.Children.Add(chip);
            }
            if (chips.Children.Count == 0)
                chips.Children.Add(new TextBlock { Text = "Henüz uygulama yok", FontSize = 11.5, Foreground = SubBrush });
        }

        var (popup, search, items) = SearchPopup(add);
        var browse = new Button { Style = (Style)FindResource("ClipItem"), Content = "Listede yok mu? Dosyadan seç (.exe)…", Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(browse, Dock.Bottom);
        ((DockPanel)((Border)popup.Child).Child).Children.Insert(0, browse);

        void Add(string process)
        {
            popup.IsOpen = false;
            var names = Selected();
            if (!names.Contains(process, StringComparer.OrdinalIgnoreCase)) names.Add(process);
            Change(names);
        }

        void Fill()
        {
            items.Children.Clear();
            if (_apps == null) { items.Children.Add(PopupNote("Uygulamalar okunuyor…")); return; }
            var q = search.Text.Trim();
            var selected = ProfileRules.Names(get());
            var matches = _apps.Where(a => !selected.Contains(a.Process)
                && (a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || a.Process.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
            bool? running = null;
            foreach (var a in matches.Take(80))
            {
                if (running != a.Running)
                {
                    running = a.Running;
                    items.Children.Add(PopupNote(a.Running ? "Şu an açık" : "Yüklü uygulamalar"));
                }
                var item = new Button { Style = (Style)FindResource("ClipItem"), Content = AppLabel(a.Path, a.Name), ToolTip = a.Path };
                item.Click += (_, _) => Add(a.Process);
                items.Children.Add(item);
            }
            if (matches.Count > 80) items.Children.Add(PopupNote($"+{matches.Count - 80} uygulama daha · aramak için yaz"));
            else if (matches.Count == 0) items.Children.Add(PopupNote("Eşleşen uygulama yok"));
        }

        browse.Click += (_, _) =>
        {
            popup.IsOpen = false;
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Uygulamalar (*.exe)|*.exe", Title = title };
            if (dialog.ShowDialog(this) != true) return;
            var app = AppCatalog.FromExe(dialog.FileName);
            _apps?.Add(app);
            Add(app.Process);
        };
        search.TextChanged += (_, _) => Fill();
        add.Click += async (_, _) =>
        {
            search.Text = "";
            Fill();
            popup.IsOpen = true;
            search.Focus();
            // Liste her acilista yenilenir: yeni acilan uygulama hemen gorunsun
            await LoadApps(new() { Redraw, () => { if (popup.IsOpen) Fill(); } });
        };

        var host = new Grid();
        host.Children.Add(add);
        host.Children.Add(popup);
        Row(title, desc, host, chips);
        Redraw();
        return Redraw;
    }

    StackPanel AppLabel(string? path, string name)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = AudioMixerService.GetIcon(path);
        if (icon != null) sp.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 7, 0) });
        sp.Children.Add(new TextBlock { Text = name, Foreground = (Brush)FindResource("Fg"), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis });
        return sp;
    }

    TextBlock PopupNote(string text) => new() { Text = text, FontSize = 11, Foreground = SubBrush, Margin = new Thickness(8, 4, 0, 3) };

    /// <summary>Arama kutulu acilir liste.</summary>
    (Popup Popup, TextBox Search, StackPanel Items) SearchPopup(UIElement target)
    {
        var search = new TextBox { Style = (Style)FindResource("Input") };
        var items = new StackPanel();
        var body = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        body.Children.Add(search);
        body.Children.Add(new ScrollViewer { Content = items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) });
        var popup = new Popup
        {
            PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4,
            Child = new Border
            {
                Width = 300, MaxHeight = 340, Padding = new Thickness(8), CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1C, 0x20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x30, 0x36)), BorderThickness = new Thickness(1), Child = body,
            },
        };
        return (popup, search, items);
    }

    async void LoadProfiles(List<Action> pickers, TextBlock status)
    {
        foreach (var redraw in pickers) redraw(); // onceki listeden adlar hemen gorunsun
        var list = await _main.GetSonarProfiles();
        if (list != null) _profiles = list;
        status.Text = list == null ? "Sonar'a ulaşılamıyor. SteelSeries GG açıkken bu sayfayı yeniden aç." : "";
        status.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var redraw in pickers) redraw();
    }

    /// <summary>Aramali profil secici. Donen eylem dugme yazisini yeniler.</summary>
    Action ProfileRow(string title, string desc, Func<string> get, Action<string> set, FrameworkElement? below = null)
    {
        var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        var btn = new Button { Style = (Style)FindResource("PillButton"), Content = label, Height = 32, Width = 220, Margin = new Thickness(0) };
        void Redraw()
        {
            var id = get();
            label.Text = id.Length == 0 ? "Değiştirme"
                : _profiles?.FirstOrDefault(p => p.Id == id)?.Name ?? (_profiles == null ? "…" : "Profil bulunamadı");
        }

        var (popup, search, items) = SearchPopup(btn);

        void Add(string id, string name)
        {
            var item = new Button
            {
                Style = (Style)FindResource("ClipItem"),
                Content = new TextBlock { Text = name, Foreground = id == get() ? (Brush)FindResource("Accent") : (Brush)FindResource("Fg"), TextTrimming = TextTrimming.CharacterEllipsis },
            };
            item.Click += (_, _) => { popup.IsOpen = false; set(id); Save(immediate: true); Redraw(); };
            items.Children.Add(item);
        }

        void Fill()
        {
            items.Children.Clear();
            var q = search.Text.Trim();
            if (q.Length == 0) Add("", "Değiştirme");
            // Once favoriler, sonra kendi profillerin, en sonda hazir profiller
            var matches = (_profiles ?? new())
                .Where(p => p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(p => p.Favorite).ThenBy(p => p.Preset).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            foreach (var p in matches.Take(60)) Add(p.Id, p.Name);
            if (matches.Count > 60)
                items.Children.Add(new TextBlock { Text = $"+{matches.Count - 60} profil daha · aramak için yaz", FontSize = 11, Foreground = SubBrush, Margin = new Thickness(8, 4, 0, 2) });
            else if (matches.Count == 0)
                items.Children.Add(new TextBlock { Text = "Eşleşen profil yok", FontSize = 11.5, Foreground = SubBrush, Margin = new Thickness(8, 4, 0, 2) });
        }

        search.TextChanged += (_, _) => Fill();
        btn.Click += (_, _) => { search.Text = ""; Fill(); popup.IsOpen = true; search.Focus(); };

        var host = new Grid();
        host.Children.Add(btn);
        host.Children.Add(popup);
        Row(title, desc, host, below);
        Redraw();
        return Redraw;
    }
}
