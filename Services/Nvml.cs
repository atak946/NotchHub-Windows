using System;
using System.Runtime.InteropServices;

namespace Notch.Services;

/// <summary>
/// NVIDIA surucusuyle gelen nvml.dll uzerinden GPU sicakligi. Yonetici izni gerekmez.
/// NVIDIA karti yoksa sessizce devre disi kalir.
/// </summary>
internal static class Nvml
{
    static bool _ready, _failed;
    static IntPtr _dev;

    [DllImport("nvml.dll")] static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);

    public static int? GpuTemperature()
    {
        if (_failed) return null;
        try
        {
            if (!_ready)
            {
                if (nvmlInit_v2() != 0 || nvmlDeviceGetHandleByIndex_v2(0, out _dev) != 0) { _failed = true; return null; }
                _ready = true;
            }
            return nvmlDeviceGetTemperature(_dev, 0, out var t) == 0 ? (int)t : null;
        }
        catch { _failed = true; return null; } // nvml.dll yok (NVIDIA disi kart)
    }
}
