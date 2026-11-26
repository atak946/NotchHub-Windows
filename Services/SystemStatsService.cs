using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace Notch.Services;

public sealed record SystemStats(double Cpu, double Gpu, double Ram, double RamUsedGb, double RamTotalGb,
                                 double DownBps, double UpBps, int? GpuTemp = null);

/// <summary>CPU, GPU, RAM ve ag hizini olcer. Yalnizca <see cref="Active"/> iken calisir (panel acikken).</summary>
public sealed class SystemStatsService : IDisposable
{
    public event Action<SystemStats>? Updated;
    public volatile bool Active;

    readonly CancellationTokenSource _cts = new();
    PerformanceCounter? _cpu;
    readonly Dictionary<string, PerformanceCounter> _gpu = new();
    DateTime _gpuRefreshed = DateTime.MinValue;
    long _rx, _tx;
    DateTime _netAt = DateTime.MinValue;

    public void Start() => Task.Run(() => Loop(_cts.Token));

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Active) { await Task.Delay(300, ct); continue; }
                Updated?.Invoke(Sample());
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { try { await Task.Delay(1000, ct); } catch { break; } }
        }
    }

    SystemStats Sample()
    {
        // CPU (Gorev Yoneticisi ile ayni olcu)
        if (_cpu == null)
        {
            try { _cpu = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); }
            catch { _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
            _cpu.NextValue();
        }
        double cpu = Math.Clamp(_cpu.NextValue(), 0, 100);

        // GPU: 3D motorlarinin toplam kullanimi
        if ((DateTime.UtcNow - _gpuRefreshed).TotalSeconds > 10)
        {
            _gpuRefreshed = DateTime.UtcNow;
            try
            {
                var names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames()
                    .Where(n => n.EndsWith("engtype_3D", StringComparison.Ordinal)).ToHashSet();
                foreach (var gone in _gpu.Keys.Where(k => !names.Contains(k)).ToList())
                {
                    _gpu[gone].Dispose();
                    _gpu.Remove(gone);
                }
                foreach (var n in names.Where(n => !_gpu.ContainsKey(n)))
                {
                    var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", n);
                    pc.NextValue();
                    _gpu[n] = pc;
                }
            }
            catch { }
        }
        double gpu = 0;
        foreach (var (name, pc) in _gpu.ToList())
        {
            try { gpu += pc.NextValue(); }
            catch { pc.Dispose(); _gpu.Remove(name); }
        }
        gpu = Math.Clamp(gpu, 0, 100);

        // RAM
        var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref ms);
        double total = ms.ullTotalPhys / 1073741824.0, used = (ms.ullTotalPhys - ms.ullAvailPhys) / 1073741824.0;

        // Ag
        long rx = 0, tx = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var st = ni.GetIPStatistics();
                rx += st.BytesReceived; tx += st.BytesSent;
            }
            catch { }
        }
        var now = DateTime.UtcNow;
        double down = 0, up = 0;
        if (_netAt != DateTime.MinValue)
        {
            double dt = (now - _netAt).TotalSeconds;
            if (dt > 0) { down = Math.Max(0, (rx - _rx) / dt); up = Math.Max(0, (tx - _tx) / dt); }
        }
        _rx = rx; _tx = tx; _netAt = now;

        return new SystemStats(cpu, gpu, ms.dwMemoryLoad, used, total, down, up, Nvml.GpuTemperature());
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cpu?.Dispose();
        foreach (var pc in _gpu.Values) pc.Dispose();
    }
}
