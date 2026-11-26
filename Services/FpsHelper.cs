using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Notch.Services;

/// <summary>
/// FPS olcumu. ETW (Windows olay izleme) yonetici izni gerektirdigi icin olcum ayri bir
/// "yardimci" surecte yapilir: Notch normal haklarla calismaya devam eder (surukle-birak bozulmaz).
///
/// Kurulum (bir kez, UAC onayiyla):
///   - Uygulama dosyalari C:\Program Files\Notch'a kopyalanir (yalnizca yonetici degistirebilir;
///     boylece kullanici klasorundeki bir dosya degistirilerek yonetici hakki kazanilamaz).
///   - "Notch\FPS" adli zamanlanmis gorev "en yuksek ayricaliklarla" olusturulur.
/// Yardimci, on plandaki pencerenin FPS'ini saniyede bir C:\Program Files\Notch\fps.txt dosyasina yazar.
/// Yonetici haklariyla calisan surec, kullanicinin degistirebilecegi bir yola asla yazmaz.
/// </summary>
public static class FpsHelper
{
    public const string TaskName = @"Notch\FPS";
    public static readonly string InstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Notch");
    public static readonly string OutputPath = Path.Combine(InstallDir, "fps.txt");

    static readonly Guid DxgiGuid = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3D9Guid = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    // ---------------- Notch tarafi (normal haklar) ----------------

    public static bool IsInstalled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    /// <summary>Eski surumun yardimcisi hala kurulu (ciktisini baska yere yazar): yeniden etkinlestirilmeli.</summary>
    public static bool IsOutdated() => !File.Exists(OutputPath);

    /// <summary>Kurulum/kaldirma icin UAC onayi isteyerek kendini yonetici olarak calistirir.</summary>
    public static Task<bool> RunElevatedAsync(string verb) => Task.Run(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, verb)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            p?.WaitForExit(60000);
            return p?.ExitCode == 0;
        }
        catch { return false; } // kullanici UAC'yi reddetti
    });

    /// <summary>Notch acilirken yardimciyi baslat (gorev zaten yoneticilikle calisir).</summary>
    public static void StartIfInstalled()
    {
        Task.Run(() => { if (IsInstalled()) RunSchtasks($"/Run /TN \"{TaskName}\""); });
    }

    /// <summary>Yardimcinin yazdigi FPS (yalnizca verilen surec icin ve 3 sn'den yeniyse).</summary>
    public static int? Read(int pid)
    {
        try
        {
            using var fs = new FileStream(OutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var parts = sr.ReadToEnd().Trim().Split(' ');
            if (parts.Length < 3) return null;
            if (DateTime.UtcNow.Ticks - long.Parse(parts[2]) > TimeSpan.TicksPerSecond * 3) return null;
            if (int.Parse(parts[0]) != pid) return null;
            int fps = int.Parse(parts[1]);
            return fps > 0 ? fps : null;
        }
        catch { return null; }
    }

    // ---------------- Yonetici tarafi ----------------

    public static int Install()
    {
        try
        {
            RunSchtasks($"/End /TN \"{TaskName}\"");
            foreach (var p in Process.GetProcessesByName("Notch").Where(p => SafePath(p)?.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase) == true))
                try { p.Kill(); p.WaitForExit(3000); } catch { }

            // Tek dosya (.exe) surumunde yalnizca exe'yi kopyala; klasordeki diger dosyalara dokunma.
            // Gelistirme (cok dosyali) surumunde uygulama klasoru zaten yalnizca Notch dosyalarini icerir.
            var baseDir = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(baseDir, "Notch.dll"))) CopyDir(baseDir, InstallDir);
            else
            {
                Directory.CreateDirectory(InstallDir);
                File.Copy(Environment.ProcessPath!, Path.Combine(InstallDir, "Notch.exe"), true);
            }
            var exe = Path.Combine(InstallDir, "Notch.exe");
            int rc = RunSchtasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --fps-helper\" /SC ONLOGON /RL HIGHEST /F");
            if (rc != 0) return rc;
            RunSchtasks($"/Run /TN \"{TaskName}\"");
            return 0;
        }
        catch { return 1; }
    }

    public static int Uninstall()
    {
        RunSchtasks($"/End /TN \"{TaskName}\"");
        RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        foreach (var p in Process.GetProcessesByName("Notch").Where(p => SafePath(p)?.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase) == true))
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        try { Directory.Delete(InstallDir, true); } catch { }
        return 0;
    }

    /// <summary>Yardimci dongusu: on plandaki surecin Present cagrilarini sayar.</summary>
    public static void RunHelper()
    {
        using var single = new Mutex(true, "Notch_FpsHelper", out bool first);
        if (!first) return;

        var meter = new FrameMeter();
        try { TraceEventSession.GetActiveSession("NotchFps")?.Stop(); } catch { }
        using var session = new TraceEventSession("NotchFps") { StopOnDispose = true };
        session.EnableProvider(DxgiGuid, TraceEventLevel.Informational, ulong.MaxValue);
        session.EnableProvider(D3D9Guid, TraceEventLevel.Informational, ulong.MaxValue);
        session.Source.AllEvents += e =>
        {
            int id = (int)e.ID;
            bool present = (e.ProviderGuid == DxgiGuid && (id == 42 || id == 55)) || (e.ProviderGuid == D3D9Guid && id == 1);
            if (present) meter.Add(e.ProcessID, e.TimeStampRelativeMSec, Environment.TickCount64);
        };
        new Thread(() => { try { session.Source.Process(); } catch { } }) { IsBackground = true }.Start();

        int notchMissing = 0;
        while (true)
        {
            Thread.Sleep(1000);
            GetWindowThreadProcessId(GetForegroundWindow(), out uint fg);
            int fps = meter.Fps((int)fg, Environment.TickCount64);
            try { File.WriteAllText(OutputPath, $"{fg} {fps} {DateTime.UtcNow.Ticks}"); } catch { }

            // Notch kapandiysa bir dakika sonra yardimci da kapanir
            if (Mutex.TryOpenExisting("Notch_SingleInstance", out var m)) { m.Dispose(); notchMissing = 0; }
            else if (++notchMissing > 60) break;
        }
    }

    // ---------------- yardimcilar ----------------

    static string? SafePath(Process p) { try { return p.MainModule?.FileName; } catch { return null; } }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    static int RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p == null) return -1;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch { return -1; }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
