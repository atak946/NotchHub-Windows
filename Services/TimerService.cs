using System;
using System.Windows.Threading;

namespace Notch.Services;

/// <summary>Basit geri sayim zamanlayicisi.</summary>
public sealed class TimerService
{
    public TimeSpan Remaining { get; private set; }
    public bool Running { get; private set; }
    public bool Active => Running || Remaining > TimeSpan.Zero;

    public event Action? Tick;
    public event Action? Finished;

    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(200) };
    DateTime _end;

    public TimerService()
    {
        _t.Tick += (_, _) =>
        {
            Remaining = _end - DateTime.Now;
            if (Remaining <= TimeSpan.Zero)
            {
                Remaining = TimeSpan.Zero;
                Running = false;
                _t.Stop();
                Tick?.Invoke();
                Finished?.Invoke();
                return;
            }
            Tick?.Invoke();
        };
    }

    public TimeSpan Total { get; private set; }
    public double Progress => Total > TimeSpan.Zero ? Math.Clamp(1 - Remaining / Total, 0, 1) : 0;

    public void Start(TimeSpan d) { Total = d; Remaining = d; Resume(); }

    public void Resume()
    {
        if (Remaining <= TimeSpan.Zero) return;
        _end = DateTime.Now + Remaining;
        Running = true;
        _t.Start();
        Tick?.Invoke();
    }

    public void Pause()
    {
        if (!Running) return;
        Remaining = _end - DateTime.Now;
        Running = false;
        _t.Stop();
        Tick?.Invoke();
    }

    public void Reset()
    {
        Running = false;
        _t.Stop();
        Remaining = TimeSpan.Zero;
        Tick?.Invoke();
    }

    public void Add(TimeSpan d)
    {
        bool wasActive = Active;
        if (Running) _end += d;
        Remaining += d;
        Total = wasActive ? Total + d : Remaining;
        Tick?.Invoke();
    }

    public static string Format(TimeSpan t)
    {
        t = TimeSpan.FromSeconds(Math.Ceiling(t.TotalSeconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }
}
