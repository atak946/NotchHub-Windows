using System;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Notch.Services;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Notch;

public partial class App : Application
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "Notch";

    Mutex? _mutex;
    WinForms.NotifyIcon? _tray;
    WinForms.ToolStripMenuItem? _hideItem;
    MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Yonetici modlari (FPS yardimcisi): arayuz acilmaz
        if (e.Args.Length > 0)
        {
            switch (e.Args[0])
            {
                case "--fps-helper": FpsHelper.RunHelper(); Shutdown(0); return;
                case "--install-fps": Shutdown(FpsHelper.Install()); return;
                case "--uninstall-fps": Shutdown(FpsHelper.Uninstall()); return;
                case "--uninstall": Shutdown(Installer.Uninstall()); return;
                case "--wait-for-exit":
                    // Kurulum/guncelleme sonrasi: eski surec kapanana kadar bekle, sonra normal basla
                    if (e.Args.Length > 1 && int.TryParse(e.Args[1], out int pid))
                        try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15000); } catch { }
                    break;
            }
        }
        Installer.CleanupOld();

        _mutex = new Mutex(true, "Notch_SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        // Beklenmeyen hatalar uygulamayi kapatmasin; %AppData%\Notch\error.log'a yazilsin
        DispatcherUnhandledException += (_, ev) => { LogError(ev.Exception); ev.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => LogError(ev.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ev) => { LogError(ev.Exception); ev.SetObserved(); };

        ApplyAutostart();
        AppSettings.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            ApplyAutostart();
            if (_hideItem != null) _hideItem.Checked = AppSettings.Current.Hidden;
        });

        _window = new MainWindow();
        _window.Show();
        CreateTray();
    }

    void CreateTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Ayarlar…", null, (_, _) => _window?.OpenSettings());
        _hideItem = new WinForms.ToolStripMenuItem("Notch'u gizle") { Checked = AppSettings.Current.Hidden };
        _hideItem.Click += (_, _) => _window?.ToggleHidden();
        menu.Items.Add(_hideItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => Quit());

        _tray = new WinForms.NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Notch",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) _window?.OpenSettings(); };
    }

    static Drawing.Icon MakeIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddArc(3, 4, 12, 12, 90, 180);
            path.AddArc(17, 4, 12, 12, 270, 180);
            path.CloseFigure();
            g.FillPath(Drawing.Brushes.White, path);
            using var dot = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 90, 31));
            g.FillEllipse(dot, 20, 7, 6, 6);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    static void ApplyAutostart()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (AppSettings.Current.AutoStart) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(RunName, false);
        }
        catch { }
    }

    static void LogError(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notch");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { }
    }

    /// <summary>Guncelleme sonrasi: yeni surum baslayabilsin diye kapan.</summary>
    public void QuitForRestart() => Quit();

    void Quit()
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _window?.Shutdown();
        Shutdown();
    }
}
