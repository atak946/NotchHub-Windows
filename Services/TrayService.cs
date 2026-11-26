using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Notch.Services;

/// <summary>
/// Saklama tepsisi: notch'a birakilan dosya/klasorlerin yollarini tutar (dosyalar kopyalanmaz, yerinde kalir).
/// Liste uygulama kapansa da korunur.
/// </summary>
public sealed class TrayService
{
    static readonly string StorePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notch", "tray.json");

    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico" };

    public List<string> Items { get; } = new();
    public event Action? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath)) ?? new();
                Items.AddRange(list.Where(Exists));
            }
        }
        catch { }
    }

    public int Add(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var p in paths)
        {
            if (!Exists(p) || Items.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
            Items.Add(p);
            added++;
        }
        if (added > 0) Save();
        return added;
    }

    public void Remove(string path) { if (Items.Remove(path)) Save(); }
    public void Clear() { Items.Clear(); Save(); }

    /// <summary>Tasinmis/silinmis dosyalari listeden atar.</summary>
    public void Prune() { if (Items.RemoveAll(p => !Exists(p)) > 0) Save(); }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Items));
        }
        catch { }
        Changed?.Invoke();
    }

    static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    // ---------- Simgeler ----------

    public static ImageSource? GetThumbnail(string path)
    {
        try
        {
            if (File.Exists(path) && ImageExt.Contains(Path.GetExtension(path)))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 96;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
        }
        catch { }
        return GetShellIcon(path);
    }

    static ImageSource? GetShellIcon(string path)
    {
        var info = new SHFILEINFO();
        var r = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
        if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DestroyIcon(info.hIcon); }
    }

    const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}
