using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Notch.Services;

/// <summary>Panoya kopyalanan seyleri izler ve son N metni tutar (Ayarlar).</summary>
public sealed class ClipboardService
{
    public event Action<string, string?>? Copied;   // tur ("text" / "image" / "files"), metin veya dosya sayisi
    public event Action? HistoryChanged;
    public List<string> History { get; } = new();
    public static int MaxHistory => Math.Clamp(AppSettings.Current.ClipboardMax, 5, 200);

    /// <summary>Ayar dusurulduyse fazla ogeleri at.</summary>
    public void Trim()
    {
        if (History.Count <= MaxHistory) return;
        History.RemoveRange(MaxHistory, History.Count - MaxHistory);
        HistoryChanged?.Invoke();
    }

    // Sifre yoneticileri bu formatlari koyarak "beni izleme" der
    readonly uint _fmtExclude = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    readonly uint _fmtIgnore = Native.RegisterClipboardFormat("Clipboard Viewer Ignore");
    string? _ignoreNext;
    IntPtr _hwnd;

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        Native.AddClipboardFormatListener(hwnd);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            // Kopyalayan uygulamanin isini bitirmesi icin kisa bir gecikme
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            t.Tick += (_, _) => { t.Stop(); Handle(); };
            t.Start();
        }
        return IntPtr.Zero;
    }

    void Handle()
    {
        if (Native.IsClipboardFormatAvailable(_fmtExclude) || Native.IsClipboardFormatAvailable(_fmtIgnore)) return;
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return;
                if (text == _ignoreNext) { _ignoreNext = null; return; }
                if (LooksSecret(text))
                {
                    Copied?.Invoke("secret", null);
                    return; // sifreye benzeyenleri gecmise kaydetme
                }
                History.Remove(text);
                History.Insert(0, text);
                if (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);
                HistoryChanged?.Invoke();
                Copied?.Invoke("text", text);
            }
            else if (Clipboard.ContainsImage()) Copied?.Invoke("image", null);
            else if (Clipboard.ContainsFileDropList()) Copied?.Invoke("files", Clipboard.GetFileDropList().Count.ToString());
        }
        catch { /* pano baska bir uygulama tarafindan kilitli olabilir */ }
    }

    public void Remove(string text) { if (History.Remove(text)) HistoryChanged?.Invoke(); }
    public void Clear() { History.Clear(); HistoryChanged?.Invoke(); }

    public void CopyAgain(string text)
    {
        _ignoreNext = text;
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); break; }
            catch { System.Threading.Thread.Sleep(30); }
        }
        History.Remove(text);
        History.Insert(0, text);
        HistoryChanged?.Invoke();
    }

    /// <summary>Bosluksuz, 8-64 karakter, harf+rakam+sembol karisimi metinleri sifre say.</summary>
    static bool LooksSecret(string t)
    {
        if (t.Length < 8 || t.Length > 64 || t.Any(char.IsWhiteSpace)) return false;
        if (t.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        int classes = 0;
        if (t.Any(char.IsLower)) classes++;
        if (t.Any(char.IsUpper)) classes++;
        if (t.Any(char.IsDigit)) classes++;
        if (t.Any(c => !char.IsLetterOrDigit(c))) classes++;
        return classes >= 3;
    }
}
