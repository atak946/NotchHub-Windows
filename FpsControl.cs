using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Notch.Services;

namespace Notch;

/// <summary>FPS yardimcisinin durum yazisi ve dugmesi; ayarlar ve kurulum yardimcisi ortak kullanir.</summary>
static class FpsControl
{
    public static (TextBlock Status, Button Button) Create(Brush subBrush)
    {
        var status = new TextBlock { Text = "Kontrol ediliyor…", FontSize = 11.5, Foreground = subBrush, Margin = new Thickness(0, 3, 0, 0) };
        var btn = new Button { Style = (Style)Application.Current.FindResource("PillButton"), Content = "…", Height = 32, Margin = new Thickness(0), IsEnabled = false };
        bool installed = false, outdated = false;

        async void Refresh()
        {
            installed = await Task.Run(FpsHelper.IsInstalled);
            outdated = installed && FpsHelper.IsOutdated();
            bool ok = installed && !outdated;
            status.Text = outdated ? "Yardımcı güncel değil veya çalışmıyor" : ok ? "Etkin · oyun göstergesinde FPS görünür" : "Kapalı";
            status.Foreground = ok ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)) : subBrush;
            btn.Content = outdated ? "Güncelle (yönetici izni)" : ok ? "Kaldır" : "Etkinleştir (yönetici izni)";
            btn.Background = ok ? new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)) : (Brush)Application.Current.FindResource("Accent");
            btn.IsEnabled = true;
        }

        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            status.Text = "Windows onayı bekleniyor…";
            await FpsHelper.RunElevatedAsync(installed && !outdated ? "--uninstall-fps" : "--install-fps");
            await Task.Delay(2000); // yardimci ilk olcumu yazsin
            Refresh();
        };
        Refresh();
        return (status, btn);
    }
}
