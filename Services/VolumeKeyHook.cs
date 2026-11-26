using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Notch.Services;

/// <summary>
/// Klavyedeki ses tuslarini (ac / kis / sessiz) Windows'tan once yakalar ve sesi kendisi degistirir.
/// Windows tusu hic gormedigi icin kendi ses kutusunu (OSD) gostermez; yalnizca notch'un gostergesi cikar.
/// Not: Tus yakalama yalnizca Enabled iken yapilir; kapaliyken tuslar normal sekilde Windows'a gider.
/// </summary>
public sealed class VolumeKeyHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    const uint VK_MUTE = 0xAD, VK_DOWN = 0xAE, VK_UP = 0xAF;
    const float Step = 0.02f; // Windows ile ayni adim

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? name);

    readonly LowLevelKeyboardProc _proc;   // GC toplamasin diye alanda tutulur
    readonly MMDeviceEnumerator _enum = new();
    IntPtr _hook;

    public bool Enabled { get; set; }

    // Teshis: %AppData%\Notch\debug-keys dosyasi varsa ses olaylari keys.log'a yazilir
    static readonly string DebugFlag = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notch", "debug-keys");
    static readonly string DebugLog = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notch", "keys.log");
    public static readonly bool DebugOn = System.IO.File.Exists(DebugFlag);
    public static void Log(string line)
    {
        if (!DebugOn) return;
        try { System.IO.File.AppendAllText(DebugLog, $"{DateTime.Now:HH:mm:ss.fff} {line}\r\n"); } catch { }
    }

    public VolumeKeyHook() => _proc = HookProc;

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    IntPtr HookProc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0 && Enabled)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
            bool volumeKey = kb.vkCode is VK_MUTE or VK_DOWN or VK_UP;
            // Not: Bazi klavyeler ses tuslarini kendi yazilimlari uzerinden "yapay" (injected) olarak
            // gonderir (or. SteelSeries GG). Bu yuzden yapay tuslar da yakalanir.
            if (volumeKey)
            {
                Log($"TUS vk=0x{kb.vkCode:X2} msg=0x{w.ToInt32():X} flags=0x{kb.flags:X} YAKALANDI");
                int msg = w.ToInt32();
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN) Apply(kb.vkCode);
                return new IntPtr(1); // tusu yut: Windows ses kutusu acilmaz
            }
        }
        return CallNextHookEx(_hook, code, w, l);
    }

    void Apply(uint vk)
    {
        try
        {
            using var dev = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var v = dev.AudioEndpointVolume;
            if (vk == VK_MUTE) { v.Mute = !v.Mute; return; }
            float cur = v.MasterVolumeLevelScalar;
            float next = vk == VK_UP ? cur + Step : cur - Step;
            next = MathF.Round(Math.Clamp(next, 0f, 1f) * 50f) / 50f; // %2'lik adimlara oturt
            v.MasterVolumeLevelScalar = next;
            if (vk == VK_UP && v.Mute) v.Mute = false; // Windows gibi: sesi acmak sessizi kaldirir
        }
        catch { }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _enum.Dispose();
    }
}
