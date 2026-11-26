using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Notch.Services;

public sealed record AppEntry(string Process, string Name, string? Path, bool Running);

/// <summary>Uygulama secici icin bilgisayardaki uygulamalar: acik olanlar ve Baslat menusundekiler.</summary>
public static class AppCatalog
{
    // Baslat menusunde olmayan veya dosya aciklamasi anlasilmayan bilinen surecler
    static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Zoom"] = "Zoom", ["ms-teams"] = "Microsoft Teams", ["Teams"] = "Microsoft Teams (eski)",
        ["msedgewebview2"] = "Edge WebView2 (Teams, WhatsApp…)", ["webex"] = "Webex", ["CiscoCollabHost"] = "Cisco Webex",
        ["slack"] = "Slack", ["Skype"] = "Skype", ["WhatsApp"] = "WhatsApp", ["Discord"] = "Discord",
        ["chrome"] = "Google Chrome", ["msedge"] = "Microsoft Edge", ["firefox"] = "Firefox", ["brave"] = "Brave", ["opera"] = "Opera",
    };

    public static string DisplayName(string process) => Known.TryGetValue(process, out var n) ? n : process;

    public static AppEntry FromExe(string path)
    {
        var process = Path.GetFileNameWithoutExtension(path);
        string name = DisplayName(process);
        if (!Known.ContainsKey(process))
        {
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) name = desc.Trim();
            }
            catch { }
        }
        return new AppEntry(process, name, path, false);
    }

    /// <summary>Surec adina gore tekil liste; acik olanlar once. Yavas olabilir, arka planda cagrilmali.</summary>
    public static List<AppEntry> Load()
    {
        var apps = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";

        // Acik uygulamalar: oturumdaki, Windows klasoru disindaki surecler (tepsidekiler dahil)
        var windowed = GameDetector.WindowedProcesses();
        var visible = new HashSet<string>(Known.Keys, StringComparer.OrdinalIgnoreCase); // listede kalacaklar
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session || p.Id == Environment.ProcessId) continue;
                    if (windowed.Contains((uint)p.Id)) visible.Add(p.ProcessName);
                    if (apps.ContainsKey(p.ProcessName)) continue;
                    var path = GameDetector.ImagePath((uint)p.Id);
                    if (path == null || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                    apps[p.ProcessName] = FromExe(path) with { Running = true };
                }
                catch { }
            }
        }

        // Yuklu uygulamalar: Baslat menusu kisayollari (kisayol adi daha okunaklidir)
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type != null)
        {
            dynamic shell = Activator.CreateInstance(type)!;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
            {
                var root = Environment.GetFolderPath(folder);
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*.lnk", options))
                {
                    try
                    {
                        string target = shell.CreateShortcut(file).TargetPath;
                        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)
                            || target.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                        var process = Path.GetFileNameWithoutExtension(target);
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (process.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("kaldır", StringComparison.OrdinalIgnoreCase)) continue;
                        visible.Add(process);
                        if (!apps.TryGetValue(process, out var existing)) apps[process] = new AppEntry(process, name, target, false);
                        else if (existing.Running && !Known.ContainsKey(process)) apps[process] = existing with { Name = name };
                    }
                    catch { }
                }
            }
        }

        // Penceresi ve kisayolu olmayan arka plan servisleri listeyi kalabaliklastirmasin
        return apps.Values.Where(a => visible.Contains(a.Process)).OrderByDescending(a => a.Running).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
