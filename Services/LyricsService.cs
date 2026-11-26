using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Notch.Services;

/// <summary>
/// lrclib.net'ten (ucretsiz, anahtarsiz) zaman damgali sarki sozlerini alir.
/// Servise yalnizca sarki adi, sanatci ve sure gonderilir.
/// </summary>
public sealed class LyricsService
{
    public event Action? Changed;

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    readonly Dictionary<string, List<(TimeSpan T, string Line)>?> _cache = new();
    List<(TimeSpan T, string Line)>? _lines;
    string _key = "";

    static readonly Regex LrcLine = new(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$", RegexOptions.Compiled);

    public bool HasLyrics => _lines is { Count: > 0 };

    public LyricsService() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("Notch/1.0 (https://github.com)");

    public async Task Load(string title, string artist, TimeSpan duration)
    {
        if (!AppSettings.Current.Lyrics || title.Length == 0) { Set(null, ""); return; }
        var key = artist + "\u0001" + title;
        if (key == _key) return;
        _key = key;
        if (_cache.TryGetValue(key, out var cached)) { Set(cached, key); return; }
        Set(null, key);

        List<(TimeSpan, string)>? lines = null;
        try
        {
            var url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            if (duration > TimeSpan.Zero) url += $"&duration={(int)Math.Round(duration.TotalSeconds)}";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                // Sure uyusmazsa aramaya dus
                resp = await _http.GetAsync($"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}");
                if (resp.IsSuccessStatusCode)
                {
                    using var arr = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    foreach (var item in arr.RootElement.EnumerateArray())
                    {
                        lines = Parse(item);
                        if (lines != null) break;
                    }
                }
            }
            else
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                lines = Parse(doc.RootElement);
            }
        }
        catch { }

        _cache[key] = lines;
        if (_key == key) Set(lines, key);
    }

    static List<(TimeSpan, string)>? Parse(JsonElement e)
    {
        if (!e.TryGetProperty("syncedLyrics", out var s) || s.ValueKind != JsonValueKind.String) return null;
        var list = new List<(TimeSpan, string)>();
        foreach (var raw in s.GetString()!.Split('\n'))
        {
            var m = LrcLine.Match(raw.Trim());
            if (!m.Success) continue;
            var t = TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) +
                    TimeSpan.FromSeconds(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            list.Add((t, m.Groups[3].Value.Trim()));
        }
        return list.Count > 0 ? list : null;
    }

    void Set(List<(TimeSpan, string)>? lines, string key)
    {
        _lines = lines;
        Changed?.Invoke();
    }

    /// <summary>Verilen pozisyondaki satir (bos satirlar "♪" olur).</summary>
    public string? LineAt(TimeSpan pos)
    {
        if (_lines == null) return null;
        string? cur = null;
        foreach (var (t, line) in _lines)
        {
            if (t > pos + TimeSpan.FromMilliseconds(150)) break;
            cur = line.Length == 0 ? "♪" : line;
        }
        return cur;
    }
}
