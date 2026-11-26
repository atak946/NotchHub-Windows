using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace Notch.Services;

/// <summary>
/// Kullanici bazinda kurulum (yonetici gerekmez), kaldirma ve guncelleme.
///   Kurulum yeri : %LocalAppData%\Programs\Notch\Notch.exe
///   Baslat menusu: Notch kisayolu
///   Program Ekle/Kaldir: "Notch" girdisi (Notch.exe --uninstall)
/// Guncelleme kaynagi bir klasor veya https adresidir; icinde version.txt, Notch.exe ve Notch.exe.sig bulunur.
/// Indirilen exe yalnizca imzasi <see cref="UpdatePublicKey"/> ile dogrulanirsa kurulur (bkz. tools/release.ps1).
/// </summary>
public static class Installer
{
    public static readonly string InstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Notch");
    public static string InstalledExe => Path.Combine(InstallDir, "Notch.exe");
    static readonly string StartMenuLink = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Notch.lnk");
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Notch";

    // Guncellemeleri imzalayan anahtarin acik yarisi. Projeyi catallayan, kendi anahtarini buraya yazmali.
    const string UpdatePublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEgfehotkK5vIuGawfFSX5uKVHIIk+y17fI3eYK3lPL/kE0GZ7s9I8rpTKKR6H/ulvSxl0mTFb95deoJXZ/odBAA==";

    /// <summary>Ayarlarda baska bir kaynak yazilmadiysa guncellemeler GitHub Releases'ten alinir.</summary>
    public const string DefaultUpdateSource = "https://github.com/atak946/NotchHub-Windows/releases/latest/download";

    public static Version CurrentVersion => typeof(Installer).Assembly.GetName().Version ?? new Version(1, 0, 0);

    /// <summary>Tek dosyalik (.exe) surum mu? Gelistirme surumu cok dosyalidir ve kurulamaz.</summary>
    public static bool IsSingleFile => !File.Exists(Path.Combine(AppContext.BaseDirectory, "Notch.dll"));

    public static bool IsInstalledCopy =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------
    //  Kurulum
    // ------------------------------------------------------------------

    /// <summary>Bu .exe'yi kurulum klasorune kopyalar, kisayol ve kaldirma girdisi olusturur, kurulu kopyayi baslatir.</summary>
    public static bool Install()
    {
        try
        {
            Directory.CreateDirectory(InstallDir);
            File.Copy(Environment.ProcessPath!, InstalledExe, true);
            WriteRegistration();
            CreateShortcut();
            StartAfterExit(InstalledExe);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Kurulum başarısız: " + ex.Message, "Notch", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    static void WriteRegistration()
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "Notch");
        k.SetValue("DisplayVersion", CurrentVersion.ToString(3));
        k.SetValue("Publisher", "Notch");
        k.SetValue("DisplayIcon", InstalledExe);
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try { k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); } catch { }
    }

    static void CreateShortcut()
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        dynamic shell = Activator.CreateInstance(t)!;
        dynamic lnk = shell.CreateShortcut(StartMenuLink);
        lnk.TargetPath = InstalledExe;
        lnk.WorkingDirectory = InstallDir;
        lnk.Description = "Notch";
        lnk.Save();
    }

    /// <summary>Bu surec kapaninca verilen exe'yi baslatir (tek kopya kilidi serbest kalsin diye).</summary>
    public static void StartAfterExit(string exe)
    {
        Process.Start(new ProcessStartInfo(exe, $"--wait-for-exit {Environment.ProcessId}") { UseShellExecute = false });
    }

    // ------------------------------------------------------------------
    //  Kaldirma (Notch.exe --uninstall)
    // ------------------------------------------------------------------

    public static int Uninstall()
    {
        if (MessageBox.Show("Notch bu bilgisayardan kaldırılsın mı?", "Notch'u kaldır",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return 1;
        bool wipeData = MessageBox.Show("Ayarlar, notlar ve tepsi listesi de silinsin mi?", "Notch'u kaldır",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // Calisan Notch'u kapat (yonetici yardimcisi haric)
        foreach (var p in Process.GetProcessesByName("Notch").Where(p => p.Id != Environment.ProcessId))
            try { p.Kill(); p.WaitForExit(3000); } catch { }

        BannerControl.RestoreAll();
        try { using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); run?.DeleteValue("Notch", false); } catch { }
        try { File.Delete(StartMenuLink); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        if (FpsHelper.IsInstalled()) FpsHelper.RunElevatedAsync("--uninstall-fps").Wait();
        if (wipeData)
            try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notch"), true); } catch { }

        // Calisan exe kendini silemez: kapandiktan sonra klasoru sil
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        MessageBox.Show("Notch kaldırıldı.", "Notch", MessageBoxButton.OK, MessageBoxImage.Information);
        return 0;
    }

    // ------------------------------------------------------------------
    //  Guncelleme
    // ------------------------------------------------------------------

    public sealed record UpdateInfo(Version Version, string Source);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static bool IsUrl(string s) => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    static string Combine(string src, string file) => IsUrl(src) ? src.TrimEnd('/') + "/" + file : Path.Combine(src, file);

    public static async Task<UpdateInfo?> CheckAsync()
    {
        var src = AppSettings.Current.UpdateSource?.Trim() ?? "";
        if (src.Length == 0) src = DefaultUpdateSource;
        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("güncelleme adresi https olmalı");
        string text = IsUrl(src) ? await Http.GetStringAsync(Combine(src, "version.txt"))
                                 : await File.ReadAllTextAsync(Combine(src, "version.txt"));
        if (!Version.TryParse(text.Trim(), out var v)) return null;
        return v > CurrentVersion ? new UpdateInfo(v, src) : null;
    }

    /// <summary>Yeni exe'yi indirir, calisan exe'nin yerine koyar ve yeniden baslatir. Basarida true.</summary>
    public static async Task<bool> ApplyAsync(UpdateInfo u)
    {
        if (!IsSingleFile) return false; // gelistirme surumu guncellenmez
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;
        var tmp = Path.Combine(dir, "Notch.new.exe");
        var old = Path.Combine(dir, "Notch.old.exe");

        if (IsUrl(u.Source))
        {
            await using var s = await Http.GetStreamAsync(Combine(u.Source, "Notch.exe"));
            await using var f = File.Create(tmp);
            await s.CopyToAsync(f);
        }
        else File.Copy(Combine(u.Source, "Notch.exe"), tmp, true);

        // Imza ve surum denetimi gecmeyen dosya hicbir zaman calistirilmaz
        try
        {
            var signature = IsUrl(u.Source)
                ? await Http.GetByteArrayAsync(Combine(u.Source, "Notch.exe.sig"))
                : await File.ReadAllBytesAsync(Combine(u.Source, "Notch.exe.sig"));
            if (!VerifySignature(tmp, signature))
                throw new InvalidOperationException("imza doğrulanamadı");

            // Eski (imzali) bir surumun yeni diye sunulmasini engelle
            var info = FileVersionInfo.GetVersionInfo(tmp);
            if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) <= CurrentVersion)
                throw new InvalidOperationException("indirilen dosya daha yeni bir sürüm değil");
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }

        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);          // calisan exe yeniden adlandirilabilir
        File.Move(tmp, exe);
        if (IsInstalledCopy) try { WriteRegistration(); } catch { }
        StartAfterExit(exe);
        return true;
    }

    static bool VerifySignature(string path, byte[] signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdatePublicKey), out _);
        using var file = File.OpenRead(path);
        return key.VerifyData(file, signature, HashAlgorithmName.SHA256);
    }

    /// <summary>Onceki guncellemeden kalan eski exe'yi temizler.</summary>
    public static void CleanupOld()
    {
        try { var old = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "Notch.old.exe"); if (File.Exists(old)) File.Delete(old); } catch { }
    }
}
