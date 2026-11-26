using System;
using System.Collections.Generic;

namespace Notch.Services;

/// <summary>
/// Kare olaylarindan FPS hesaplar. ETW olaylari uygulamaya toplu halde (tamponlarla) ulasir; saniyede
/// ulasan olay sayisini saymak bu yuzden dalgalanir. Hesap, olaylarin kendi zaman damgalarina dayanir.
/// </summary>
public sealed class FrameMeter
{
    const double WindowMs = 1000;
    const long StaleMs = 2500; // bu surede yeni olay gelmediyse surec kare uretmiyor

    sealed class Track
    {
        public readonly Queue<double> Times = new();
        public double Latest;
        public long Arrived;
    }

    readonly Dictionary<int, Track> _tracks = new();

    /// <param name="timeMs">Olayin zaman damgasi (ms).</param>
    /// <param name="nowMs">Olayin bize ulastigi an (Environment.TickCount64).</param>
    public void Add(int pid, double timeMs, long nowMs)
    {
        lock (_tracks)
        {
            if (!_tracks.TryGetValue(pid, out var t)) _tracks[pid] = t = new Track();
            t.Times.Enqueue(timeMs);
            t.Latest = Math.Max(t.Latest, timeMs);
            t.Arrived = nowMs;
            while (t.Times.Peek() < t.Latest - WindowMs) t.Times.Dequeue();
        }
    }

    /// <summary>Surecin, ulasan son olayindan geriye bir saniyelik FPS'i.</summary>
    public int Fps(int pid, long nowMs)
    {
        lock (_tracks)
        {
            if (!_tracks.TryGetValue(pid, out var t)) return 0;
            if (nowMs - t.Arrived > StaleMs) { _tracks.Remove(pid); return 0; }
            double first = t.Latest;
            int count = 0;
            foreach (var time in t.Times)
            {
                if (time < t.Latest - WindowMs) continue;
                first = Math.Min(first, time);
                count++;
            }
            return t.Latest > first ? (int)Math.Round((count - 1) * 1000 / (t.Latest - first)) : 0;
        }
    }
}
