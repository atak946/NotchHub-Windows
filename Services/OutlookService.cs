using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace Notch.Services;

public sealed record CalEvent(string Subject, DateTime Start, DateTime End, string Location, bool AllDay, string? JoinUrl, bool Busy)
{
    public bool IsNow => DateTime.Now >= Start && DateTime.Now < End;
    public string Key => Subject + "|" + Start.Ticks;
}

/// <summary>
/// Masaustu (klasik) Outlook'un takviminden bugunun ve yarinin etkinliklerini okur.
/// Outlook acik degilse baslatmaz; yalnizca acikken okur. Veriler bilgisayardan cikmaz.
/// </summary>
public sealed class OutlookService : IDisposable
{
    public event Action? Updated;
    public List<CalEvent> Events { get; private set; } = new();
    public string? Status { get; private set; }

    static readonly Regex JoinLink = new(
        @"https://(?:[\w-]+\.)?(?:zoom\.us/(?:j|my|w)/[^\s""<>]+|teams\.microsoft\.com/l/meetup-join/[^\s""<>]+|teams\.live\.com/meet/[^\s""<>]+|meet\.google\.com/[a-z\-]+|[\w-]+\.webex\.com/[^\s""<>]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly AutoResetEvent _wake = new(false);
    volatile bool _stop;
    readonly Thread _thread;

    public OutlookService()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Notch-Outlook" };
        _thread.SetApartmentState(ApartmentState.STA); // Outlook COM STA ister
    }

    public void Start() => _thread.Start();
    public void Refresh() => _wake.Set();

    void Loop()
    {
        while (!_stop)
        {
            try { Fetch(); } catch { Status = "Takvim okunamadı"; }
            Updated?.Invoke();
            _wake.WaitOne(TimeSpan.FromMinutes(3));
        }
    }

    void Fetch()
    {
        if (!AppSettings.Current.OutlookCalendar) { Events = new(); Status = null; return; }
        if (Process.GetProcessesByName("OUTLOOK").Length == 0)
        {
            Status = "Masaüstü Outlook açık değil";
            return; // son bilinen etkinlikler kalsin
        }

        var type = Type.GetTypeFromProgID("Outlook.Application");
        if (type == null) { Status = "Outlook bulunamadı"; Events = new(); return; }

        dynamic? app = null, ns = null, folder = null, items = null, list = null;
        var result = new List<CalEvent>();
        try
        {
            app = Activator.CreateInstance(type)!;   // calisan Outlook'a baglanir
            ns = app.GetNamespace("MAPI");
            folder = ns.GetDefaultFolder(9);          // olFolderCalendar
            items = folder.Items;
            items.IncludeRecurrences = true;
            items.Sort("[Start]");
            var from = DateTime.Today;
            var to = DateTime.Today.AddDays(2);
            var cul = CultureInfo.CurrentCulture;
            list = items.Restrict($"[End] >= '{from.ToString("g", cul)}' AND [Start] < '{to.ToString("g", cul)}'");

            dynamic? it = list.GetFirst();
            int guard = 0;
            while (it != null && guard++ < 60)
            {
                try
                {
                    string subject = (string?)it.Subject ?? "(Konu yok)";
                    DateTime start = it.Start, end = it.End;
                    string location = (string?)it.Location ?? "";
                    bool allDay = it.AllDayEvent;
                    int busy = it.BusyStatus;                     // 2 = mesgul, 3 = ofis disi
                    string? url = JoinLink.Match(location) is { Success: true } m ? m.Value : null;
                    if (url == null && AppSettings.Current.FindMeetingLinks)
                    {
                        try { string body = (string?)it.Body ?? ""; var bm = JoinLink.Match(body); if (bm.Success) url = bm.Value; }
                        catch { /* Outlook guvenlik politikasi govdeye erisimi engelleyebilir */ }
                    }
                    if (end > from && start < to)
                        result.Add(new CalEvent(subject.Trim(), start, end, location.Trim(), allDay, url?.TrimEnd('.', ',', ')'), busy >= 2));
                }
                catch { }
                finally { Release(it); }
                it = list.GetNext();
            }
            Events = result.OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.Start).ToList();
            Status = null;
        }
        finally
        {
            Release(list); Release(items); Release(folder); Release(ns); Release(app);
        }
    }

    static void Release(object? o)
    {
        try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
    }

    /// <summary>Simdi devam eden veya en yakin gelecekteki (tum gun olmayan) etkinlik.</summary>
    public CalEvent? NextOrCurrent() =>
        Events.Where(e => !e.AllDay && e.End > DateTime.Now).OrderBy(e => e.Start).FirstOrDefault();

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
    }
}
