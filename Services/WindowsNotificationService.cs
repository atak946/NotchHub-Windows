using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace Notch.Services;

public sealed record WinNotification(string App, string Title, string Body, string? ImagePath, DateTime Time, string Aumid);

/// <summary>
/// Windows bildirimlerini (WhatsApp, Zoom, Discord, Teams...) yakalar.
/// Windows her bildirimi kullanicinin kendi klasorundeki wpndatabase.db'ye yazar; bu dosya
/// degistikce gecici bir kopyasi salt okunur acilir ve yeni bildirimler okunur.
/// Hicbir veri bilgisayardan disari cikmaz.
/// </summary>
public sealed class WindowsNotificationService : IDisposable
{
    public event Action<WinNotification>? Received;

    static readonly string DbDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");
    static readonly string TempDir = Path.Combine(Path.GetTempPath(), "Notch-wpn");

    readonly CancellationTokenSource _cts = new();
    long _lastId = -1;
    DateTime _lastStamp;

    public void Start() => Task.Run(() => Loop(_cts.Token));

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (AppSettings.Current.NotifyWindows) Poll();
            }
            catch { /* dosya o an kilitli olabilir; bir sonraki turda tekrar dene */ }
            try { await Task.Delay(1500, ct); } catch { break; }
        }
    }

    void Poll()
    {
        var db = Path.Combine(DbDir, "wpndatabase.db");
        if (!File.Exists(db)) return;
        var stamp = Max(File.GetLastWriteTimeUtc(db), File.GetLastWriteTimeUtc(db + "-wal"));
        if (stamp == _lastStamp && _lastId >= 0) return;
        _lastStamp = stamp;

        Directory.CreateDirectory(TempDir);
        var copy = Path.Combine(TempDir, "w.db");
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var src = db + ext;
            var dst = copy + ext;
            if (File.Exists(src)) CopyShared(src, dst);
            else if (File.Exists(dst)) File.Delete(dst);
        }

        var cs = new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        using var con = new SqliteConnection(cs);
        con.Open();

        if (_lastId < 0)
        {
            // Ilk calistirmada eski bildirimleri gosterme
            using var c0 = con.CreateCommand();
            c0.CommandText = "SELECT IFNULL(MAX(Id), 0) FROM Notification";
            _lastId = Convert.ToInt64(c0.ExecuteScalar());
            return;
        }

        using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT n.Id, n.Payload, n.ArrivalTime, h.PrimaryId
                            FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId
                            WHERE n.Type = 'toast' AND n.Id > $id ORDER BY n.Id";
        cmd.Parameters.AddWithValue("$id", _lastId);
        var found = new List<WinNotification>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                _lastId = Math.Max(_lastId, r.GetInt64(0));
                var payload = r.IsDBNull(1) ? null : r.GetValue(1) switch
                {
                    byte[] b => System.Text.Encoding.UTF8.GetString(b),
                    string s => s,
                    _ => null,
                };
                var n = Parse(payload, r.IsDBNull(3) ? "" : r.GetString(3));
                if (n != null) found.Add(n);
            }
        }
        con.Close();
        SqliteConnection.ClearAllPools();

        // Bir anda cok bildirim gelirse yalnizca sonuncusunu goster
        if (found.Count > 0) Received?.Invoke(found[^1]);
    }

    static WinNotification? Parse(string? payload, string aumid)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            var x = XElement.Parse(payload);
            var texts = x.Descendants("text").Select(t => t.Value.Trim()).Where(t => t.Length > 0).ToList();
            if (texts.Count == 0) return null;
            string? img = null;
            foreach (var im in x.Descendants("image"))
            {
                var src = (string?)im.Attribute("src");
                if (src == null) continue;
                if (src.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) src = Uri.UnescapeDataString(src[8..]).Replace('/', '\\');
                if (Path.IsPathRooted(src) && File.Exists(src)) { img = src; break; }
            }
            return new WinNotification(AppName(aumid), texts[0], string.Join(" · ", texts.Skip(1)), img, DateTime.Now, aumid);
        }
        catch { return null; }
    }

    static readonly (string Key, string Name)[] Known =
    {
        ("whatsapp", "WhatsApp"), ("zoom", "Zoom"), ("discord", "Discord"), ("teams", "Teams"),
        ("telegram", "Telegram"), ("slack", "Slack"), ("outlook", "Outlook"), ("mail", "Posta"),
        ("chrome", "Chrome"), ("msedge", "Edge"), ("firefox", "Firefox"), ("spotify", "Spotify"),
        ("steam", "Steam"), ("signal", "Signal"), ("instagram", "Instagram"), ("skype", "Skype"),
    };

    public static string AppName(string aumid)
    {
        var low = aumid.ToLowerInvariant();
        foreach (var (k, n) in Known) if (low.Contains(k)) return n;
        var id = aumid;
        int bang = id.IndexOf('!');
        if (bang > 0) id = id[..bang];
        int us = id.IndexOf('_');
        if (us > 0) id = id[..us];
        if (id.Contains('\\')) id = Path.GetFileNameWithoutExtension(id);
        var parts = id.Split('.', StringSplitOptions.RemoveEmptyEntries);
        id = parts.Length > 0 ? parts[^1] : id;
        return id.Length > 0 ? char.ToUpper(id[0]) + id[1..] : "Bildirim";
    }

    static void CopyShared(string src, string dst)
    {
        using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        i.CopyTo(o);
    }

    static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    public void Dispose()
    {
        _cts.Cancel();
        // Gecici kopya bildirim icerigi barindirir: kapanista sil
        try { Directory.Delete(TempDir, true); } catch { }
    }
}
