using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace IKEMENLab.App.Services;

public sealed record GpuMemoryReading(string AdapterName, long DedicatedUsedBytes, long DedicatedTotalBytes)
{
    public double Fraction => DedicatedTotalBytes <= 0 ? 0 : Math.Clamp((double)DedicatedUsedBytes / DedicatedTotalBytes, 0, 1);
}

/// <summary>
/// System-wide dedicated video memory usage (the Windows counterpart of the macOS VRAM bar).
/// Total comes from DXGI adapter descriptions; usage from the "GPU Adapter Memory" performance
/// counters that Task Manager uses. Any failure yields null so the UI shows a neutral "—".
/// </summary>
public sealed partial class GpuMemoryMonitor : IDisposable
{
    private IntPtr _query;
    private IntPtr _counter;
    private bool _primed;
    private readonly IReadOnlyList<Adapter> _adapters;

    public GpuMemoryMonitor()
    {
        _adapters = SafeEnumerateAdapters();
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
            if (PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _counter) != 0)
            {
                _counter = IntPtr.Zero;
            }
        }
        catch (DllNotFoundException) { _query = IntPtr.Zero; }
        catch (EntryPointNotFoundException) { _query = IntPtr.Zero; }
    }

    public bool IsAvailable => _query != IntPtr.Zero && _counter != IntPtr.Zero && _adapters.Count > 0;

    /// <summary>Samples the counters. Call from a background thread.</summary>
    public GpuMemoryReading? Read()
    {
        if (!IsAvailable) return null;
        if (PdhCollectQueryData(_query) != 0) return null;
        if (!_primed)
        {
            // Some counters need two collections before the first formatted value is valid.
            _primed = true;
            PdhCollectQueryData(_query);
        }

        var usageByLuid = ReadUsageByLuid();
        if (usageByLuid is null) return null;

        // Prefer the adapter with the most dedicated memory (the discrete GPU on hybrid systems).
        foreach (var adapter in _adapters.OrderByDescending(a => a.DedicatedBytes))
        {
            if (usageByLuid.TryGetValue(adapter.LuidKey, out var used))
            {
                return new GpuMemoryReading(adapter.Name, used, adapter.DedicatedBytes);
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    private Dictionary<string, long>? ReadUsageByLuid()
    {
        uint bufferSize = 0;
        uint itemCount = 0;
        var status = PdhGetFormattedCounterArray(_counter, PdhFmtLarge, ref bufferSize, ref itemCount, IntPtr.Zero);
        if (status != PdhMoreData || bufferSize == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = PdhGetFormattedCounterArray(_counter, PdhFmtLarge, ref bufferSize, ref itemCount, buffer);
            if (status != 0) return null;

            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItemLarge>();
            for (var i = 0; i < itemCount; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItemLarge>(buffer + i * itemSize);
                var name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                var match = LuidPattern().Match(name);
                if (!match.Success || item.Status != 0) continue;
                var key = LuidKey(Convert.ToUInt32(match.Groups[1].Value, 16), Convert.ToInt32(match.Groups[2].Value, 16));
                result[key] = result.GetValueOrDefault(key) + item.LargeValue;
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // luid_0x00000000_0x0000D1B2_phys_0  (high part first, then low part)
    [GeneratedRegex(@"luid_0x([0-9a-f]{8})_0x([0-9a-f]{8})", RegexOptions.IgnoreCase)]
    private static partial Regex LuidPattern();

    private static string LuidKey(uint high, int low) => $"{high:X8}:{(uint)low:X8}";

    private sealed record Adapter(string Name, long DedicatedBytes, string LuidKey);

    // ------------------------------------------------------------------ DXGI

    private static IReadOnlyList<Adapter> SafeEnumerateAdapters()
    {
        try { return EnumerateAdapters(); }
        catch (Exception) { return Array.Empty<Adapter>(); }
    }

    private static List<Adapter> EnumerateAdapters()
    {
        var adapters = new List<Adapter>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(ref iid, out var factory) != 0 || factory == IntPtr.Zero) return adapters;

        try
        {
            var enumAdapters1 = VTable<EnumAdapters1Fn>(factory, 12);
            for (uint i = 0; enumAdapters1(factory, i, out var adapter) == 0; i++)
            {
                try
                {
                    var getDesc1 = VTable<GetDesc1Fn>(adapter, 10);
                    if (getDesc1(adapter, out var desc) != 0) continue;
                    if ((desc.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE
                    var dedicated = (long)desc.DedicatedVideoMemory;
                    if (dedicated <= 0) continue;
                    adapters.Add(new Adapter(desc.Description.TrimEnd('\0'), dedicated,
                        LuidKey((uint)desc.AdapterLuidHigh, (int)desc.AdapterLuidLow)));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        return adapters;
    }

    private static T VTable<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Fn(IntPtr self, out DxgiAdapterDesc1 desc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    // ------------------------------------------------------------------ PDH

    private const uint PdhFmtLarge = 0x00000400;
    private const int PdhMoreData = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItemLarge
    {
        public IntPtr Name;
        public uint Status;
        private uint _padding;
        public long LargeValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}
