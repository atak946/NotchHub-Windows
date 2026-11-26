using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Notch.Services;

/// <summary>
/// "Su an bir oyun acik mi?" sorusunu yanitlar. Iki isaret kullanilir:
///   1) Penceresi olan bir surec oyun kutuphanesi klasorunden (Steam, Epic, Riot...) veya kullanicinin listesinden calisiyor.
///   2) On plandaki cercevesiz tam ekran uygulama (tarayici, oynatici vb. haric). Bir kez algilaninca
///      surec kapanana kadar oyun sayilir; boylece Alt+Tab profili geri degistirmez.
/// Tek is parcacigindan cagrilmalidir.
/// </summary>
public sealed class GameDetector
{
    static readonly string[] LibraryFolders =
    {
        @"\steamapps\common\", @"\epic games\", @"\riot games\", @"\xboxgames\", @"\ea games\",
        @"\origin games\", @"\ubisoft game launcher\games\", @"\gog galaxy\games\", @"\gog games\", @"\rockstar games\",
    };

    // Kutuphane klasorlerinde duran ama oyun olmayan surecler (baslaticilar, araclar)
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "wallpaper32", "wallpaper64", "ui32", "steamwebhelper", "vrmonitor", "vrserver",
        "EpicGamesLauncher", "EpicWebHelper", "RiotClientServices", "Riot Client", "RiotClientUx", "RiotClientUxRender",
        "Launcher", "LauncherPatcher", "RockstarService", "SocialClubHelper", "GalaxyClient", "EADesktop", "UbisoftConnect",
    };

    // Tam ekran olsa da oyun sayilmayanlar
    static readonly HashSet<string> NotFullscreenGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "zen", "msedgewebview2",
        "vlc", "mpv", "mpc-hc64", "mpc-be64", "PotPlayerMini64", "Video.UI", "Microsoft.Media.Player", "Netflix", "Spotify",
        "Zoom", "Teams", "ms-teams", "webex", "slack", "Discord", "WhatsApp", "mstsc", "vmware-vmx", "VirtualBoxVM",
        "POWERPNT", "WINWORD", "EXCEL", "Acrobat", "AcroRd32", "Code", "devenv", "rider64", "idea64", "WindowsTerminal",
        "ApplicationFrameHost", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost", "LockApp", "Notch",
    };

    int _fullscreenPid;
    string _fullscreenName = "";

    /// <summary>Acik oyunun surec adi; oyun yoksa null.</summary>
    public string? Find(IReadOnlySet<string> extra, IReadOnlySet<string> ignored, bool useFullscreen)
    {
        var windowed = WindowedProcesses();

        // Kullanicinin elle ekledikleri: penceresi olmasa da sayilir
        if (extra.Count > 0)
            foreach (var name in extra)
            {
                var found = Process.GetProcessesByName(name);
                foreach (var p in found) p.Dispose();
                if (found.Length > 0) return name;
            }

        foreach (var pid in windowed)
        {
            var path = ImagePath(pid);
            if (path == null) continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (NotGames.Contains(name) || ignored.Contains(name)) continue;
            if (LibraryFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase))) return name;
        }

        if (!useFullscreen) { _fullscreenPid = 0; return null; }

        if (_fullscreenPid != 0)
        {
            // Ayni kimlik baska bir surece verilmis olabilir: adi da karsilastir
            var path = ImagePath((uint)_fullscreenPid);
            if (path != null && Path.GetFileNameWithoutExtension(path) == _fullscreenName && !ignored.Contains(_fullscreenName))
                return _fullscreenName;
            _fullscreenPid = 0;
        }

        var fg = Native.GetForegroundWindow();
        // Basligi olan buyutulmus pencereler (gorev cubugu gizliyken ekrani kaplar) sayilmaz
        if ((Native.GetWindowLong(fg, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) return null;
        if (!Native.IsForegroundFullscreen(IntPtr.Zero)) return null;
        Native.GetWindowThreadProcessId(fg, out uint fgPid);
        var fgPath = ImagePath(fgPid);
        if (fgPath == null || fgPath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) return null;
        var fgName = Path.GetFileNameWithoutExtension(fgPath);
        if (NotFullscreenGames.Contains(fgName) || NotGames.Contains(fgName) || ignored.Contains(fgName)) return null;
        _fullscreenPid = (int)fgPid;
        _fullscreenName = fgName;
        return fgName;
    }

    /// <summary>Gorunur penceresi olan sureclerin kimlikleri.</summary>
    internal static HashSet<uint> WindowedProcesses()
    {
        var pids = new HashSet<uint>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h)) { GetWindowThreadProcessId(h, out uint id); pids.Add(id); }
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    // Process.MainModule korumali (hile korumali) oyunlarda hata verir; bu yontem sinirli yetkiyle calisir
    internal static string? ImagePath(uint pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int len = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000;

    delegate bool EnumProc(IntPtr hwnd, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
}
