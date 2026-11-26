using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace Notch.Services;

/// <summary>Oyun gostergesi icin gecikme (ping) olcer. Yalnizca Active iken calisir.</summary>
public sealed class PingService : IDisposable
{
    public volatile bool Active;
    public int? LastMs { get; private set; }
    public event Action<int?>? Updated;

    readonly CancellationTokenSource _cts = new();

    public void Start() => Task.Run(() => Loop(_cts.Token));

    async Task Loop(CancellationToken ct)
    {
        using var ping = new Ping();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Active) { await Task.Delay(500, ct); continue; }
                var reply = await ping.SendPingAsync(AppSettings.Current.PingHost, 1500);
                LastMs = reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : null;
                Updated?.Invoke(LastMs);
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                LastMs = null;
                Updated?.Invoke(null);
                try { await Task.Delay(3000, ct); } catch { break; }
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
