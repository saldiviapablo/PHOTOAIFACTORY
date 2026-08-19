using System.Runtime.InteropServices;
using System.Text;

namespace PhotoAIFactory.Gpu01;

internal sealed record NvmlMemory(double TotalMb, double UsedMb, double FreeMb);

internal sealed class NvmlMonitor : IDisposable
{
    private IntPtr _device;
    private bool _initialized;

    public NvmlMonitor()
    {
        Check(nvmlInit_v2(), "nvmlInit_v2");
        _initialized = true;
        Check(nvmlDeviceGetHandleByIndex_v2(0, out _device), "nvmlDeviceGetHandleByIndex_v2");
    }

    public string DriverVersion
    {
        get { var value = new StringBuilder(96); Check(nvmlSystemGetDriverVersion(value, (uint)value.Capacity), "nvmlSystemGetDriverVersion"); return value.ToString(); }
    }

    public string DeviceName
    {
        get { var value = new StringBuilder(96); Check(nvmlDeviceGetName(_device, value, (uint)value.Capacity), "nvmlDeviceGetName"); return value.ToString(); }
    }

    public NvmlMemory Snapshot()
    {
        Check(nvmlDeviceGetMemoryInfo(_device, out var memory), "nvmlDeviceGetMemoryInfo");
        return new NvmlMemory(ToMb(memory.total), ToMb(memory.used), ToMb(memory.free));
    }

    public void Dispose()
    {
        if (_initialized) { nvmlShutdown(); _initialized = false; }
    }

    private static double ToMb(ulong bytes) => Math.Round(bytes / 1048576d, 3);
    private static void Check(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"NVML {operation} failed with code {result}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo { public ulong total; public ulong free; public ulong used; }

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlShutdown();
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out MemoryInfo memory);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern int nvmlSystemGetDriverVersion(StringBuilder version, uint length);
}
