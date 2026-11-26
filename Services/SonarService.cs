using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Notch.Services;

public sealed record SonarProfile(string Id, string Name, bool Favorite, int FavoritePosition, bool Preset);

/// <summary>SteelSeries Sonar'in yerel API'si: kanal seslerini (Master dahil) izler, GAME profillerini yonetir.</summary>
public sealed class SonarService : IDisposable
{
    public event Action<string, int, bool>? Changed;

    const string CoreProps = @"C:\ProgramData\SteelSeries\SteelSeries Engine 3\coreProps.json";

    static readonly Dictionary<string, string> Names = new()
    {
        ["master"] = "Sonar · Master",
        ["game"] = "Sonar · Game",
        ["chatRender"] = "Sonar · Chat",
        ["media"] = "Sonar · Media",
        ["aux"] = "Sonar · Aux",
    };

    readonly HttpClient _http = new(new HttpClientHandler
    {
        // GG yalnizca 127.0.0.1 uzerinde kendinden imzali sertifika kullaniyor; baska adreste dogrulama atlanmaz
        ServerCertificateCustomValidationCallback = (req, _, _, errors) =>
            errors == SslPolicyErrors.None || req.RequestUri?.IsLoopback == true,
        AllowAutoRedirect = false,
    })
    { Timeout = TimeSpan.FromSeconds(1) };

    readonly CancellationTokenSource _cts = new();

    // Profil listesi buyuk (~1 MB) oldugu icin ses yoklamasindan daha uzun zaman asimi kullanir
    readonly HttpClient _profileHttp = new() { Timeout = TimeSpan.FromSeconds(5) };
    string? _profileUrl;

    public void Start() => Task.Run(() => Loop(_cts.Token));

    /// <summary>GAME kanalinin tum profilleri. Sonar'a ulasilamiyorsa null.</summary>
    public Task<List<SonarProfile>?> GetGameProfiles() => WithSonar(async b =>
    {
        using var doc = JsonDocument.Parse(await _profileHttp.GetStringAsync(b + "/configs?vad=game"));
        var list = new List<SonarProfile>();
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            if (c.GetProperty("virtualAudioDevice").GetString() != "game") continue;
            list.Add(new SonarProfile(
                c.GetProperty("id").GetString()!,
                c.GetProperty("name").GetString() ?? "",
                c.TryGetProperty("isFavorite", out var f) && f.ValueKind == JsonValueKind.True,
                c.TryGetProperty("favoritePosition", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : int.MaxValue,
                c.TryGetProperty("isPreset", out var pr) && pr.ValueKind == JsonValueKind.True));
        }
        return list;
    });

    /// <summary>GAME kanalinda secili profilin kimligi.</summary>
    public Task<string?> GetSelectedGameProfile() => WithSonar(async b =>
    {
        using var doc = JsonDocument.Parse(await _profileHttp.GetStringAsync(b + "/configs/selected"));
        foreach (var c in doc.RootElement.EnumerateArray())
            if (c.GetProperty("virtualAudioDevice").GetString() == "game") return c.GetProperty("id").GetString();
        return null;
    });

    public async Task<bool> SelectProfile(string id) => await WithSonar(async b =>
    {
        using var res = await _profileHttp.PutAsync($"{b}/configs/{Uri.EscapeDataString(id)}/select", null);
        return res.IsSuccessStatusCode ? "ok" : null;
    }) != null;

    async Task<T?> WithSonar<T>(Func<string, Task<T?>> call) where T : class
    {
        try
        {
            _profileUrl ??= await Find();
            return _profileUrl == null ? null : await call(_profileUrl);
        }
        catch
        {
            _profileUrl = null; // Sonar yeniden baslamis olabilir
            return null;
        }
    }

    async Task Loop(CancellationToken ct)
    {
        string? baseUrl = null;
        Dictionary<string, (int, bool)>? last = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (baseUrl == null)
                {
                    baseUrl = await Find();
                    last = null;
                    if (baseUrl == null) { await Task.Delay(5000, ct); continue; }
                }
                var cur = await Read(baseUrl);
                if (last != null)
                {
                    foreach (var kv in cur)
                    {
                        if (!last.TryGetValue(kv.Key, out var old) || old != kv.Value)
                        {
                            Changed?.Invoke(Names[kv.Key], kv.Value.Item1, kv.Value.Item2);
                            break;
                        }
                    }
                }
                last = cur;
                await Task.Delay(250, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch
            {
                baseUrl = null; // Sonar yeniden baslamis olabilir
                try { await Task.Delay(3000, ct); } catch { break; }
            }
        }
    }

    async Task<string?> Find()
    {
        if (!File.Exists(CoreProps)) return null;
        using var props = JsonDocument.Parse(await File.ReadAllTextAsync(CoreProps));
        var gg = props.RootElement.GetProperty("ggEncryptedAddress").GetString();
        if (!IsLocal($"https://{gg}")) return null;
        using var apps = JsonDocument.Parse(await _http.GetStringAsync($"https://{gg}/subApps"));
        var sonar = apps.RootElement.GetProperty("subApps").GetProperty("sonar");
        if (!sonar.GetProperty("isRunning").GetBoolean()) return null;
        var web = sonar.GetProperty("metadata").GetProperty("webServerAddress").GetString();
        return IsLocal(web) ? web : null;
    }

    static bool IsLocal(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsLoopback;

    async Task<Dictionary<string, (int, bool)>> Read(string b)
    {
        var mode = JsonSerializer.Deserialize<string>(await _http.GetStringAsync(b + "/mode")) ?? "classic";
        using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{b}/volumeSettings/{mode}"));
        var r = new Dictionary<string, (int, bool)>();
        if (doc.RootElement.GetProperty("masters").TryGetProperty(mode, out var m) && m.TryGetProperty("volume", out var mv))
            r["master"] = (Pct(mv), m.TryGetProperty("muted", out var mm) && mm.GetBoolean());
        foreach (var dev in doc.RootElement.GetProperty("devices").EnumerateObject())
        {
            if (!Names.ContainsKey(dev.Name)) continue;
            if (dev.Value.TryGetProperty(mode, out var v) && v.TryGetProperty("volume", out var vv))
                r[dev.Name] = (Pct(vv), v.TryGetProperty("muted", out var mu) && mu.GetBoolean());
        }
        return r;
    }

    static int Pct(JsonElement e) => (int)Math.Round(e.GetDouble() * 100);

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        _profileHttp.Dispose();
    }
}
