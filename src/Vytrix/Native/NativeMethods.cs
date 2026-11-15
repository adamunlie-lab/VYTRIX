using System.Runtime.InteropServices;

namespace Vytrix.Native;

/// <summary>P/Invoke surface of VytrixCore.dll (C++). Struct layouts mirror VytrixCore.h.</summary>
public static class VxNative
{
    private const string Dll = "VytrixCore.dll";
    public const double Unavailable = -1.0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
    public struct CpuInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Brand;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string Vendor;
        public uint PhysicalCores;
        public uint LogicalProcessors;
        public uint L2CacheKB;
        public uint L3CacheKB;
        public uint MaxMhz;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
    public struct GpuInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public ulong DedicatedVideoMemory;
        public ulong SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint VendorId;
        public uint DeviceId;
        public int IsSoftware;
        public int HagsSupported;
        public int HagsEnabled;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct MemoryInfo
    {
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong CommitTotal;
        public ulong CommitLimit;
        public ulong CommitPeak;
        public ulong SystemCache;
        public ulong KernelPaged;
        public ulong KernelNonPaged;
        public uint LoadPercent;
        public uint ProcessCount;
        public uint ThreadCount;
        public uint HandleCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct LiveSample
    {
        public double CpuPercent;
        public double GpuPercent;
        public double GpuDedicatedUsed;
        public double GpuTempC;
        public double AcpiTempC;
        public double DiskReadBps;
        public double DiskWriteBps;
        public double NetRecvBps;
        public double NetSendBps;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
    public struct ProcessEntry
    {
        public uint Pid;
        public uint ParentPid;
        public uint SessionId;
        public uint ThreadCount;
        public ulong WorkingSet;
        public ulong PrivateBytes;
        public int HasVisibleWindow;
        public int AccessDenied;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)] public string Path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
    public struct PowerScheme
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 40)] public string Guid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public int IsActive;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int vx_init();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void vx_shutdown();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int vx_get_cpu_info(out CpuInfo info);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int vx_enum_gpus([Out] GpuInfo[]? buffer, int capacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int vx_get_memory(out MemoryInfo info);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern ulong vx_get_uptime_ms();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int vx_is_elevated();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int vx_sample(out LiveSample sample);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int vx_enum_processes([Out] ProcessEntry[]? buffer, int capacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int vx_enum_power_schemes([Out] PowerScheme[]? buffer, int capacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern int vx_set_power_scheme(string guid);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern int vx_duplicate_power_scheme(string sourceGuid, [Out] char[] newGuid, int cch);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern int vx_power_read_ac(string subgroup, string setting, out uint value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern int vx_power_write_ac(string subgroup, string setting, uint value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern int vx_ping(string host, uint timeoutMs, out uint rttMs);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int vx_recycle_bin_query(out ulong bytes, out ulong items);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int vx_recycle_bin_empty();

    public static GpuInfo[] EnumGpus()
    {
        int n = vx_enum_gpus(null, 0);
        if (n <= 0) return [];
        var buf = new GpuInfo[n];
        n = vx_enum_gpus(buf, n);
        return buf.Take(n).ToArray();
    }

    public static ProcessEntry[] EnumProcesses()
    {
        var buf = new ProcessEntry[1024];
        int n = vx_enum_processes(buf, buf.Length);
        if (n > buf.Length)
        {
            buf = new ProcessEntry[n + 128];
            n = Math.Min(vx_enum_processes(buf, buf.Length), buf.Length);
        }
        return buf.Take(Math.Max(0, n)).ToArray();
    }

    public static PowerScheme[] EnumPowerSchemes()
    {
        var buf = new PowerScheme[32];
        int n = vx_enum_power_schemes(buf, buf.Length);
        return buf.Take(Math.Clamp(n, 0, buf.Length)).ToArray();
    }
}

/// <summary>Win32 user-mode helpers used to apply settings live.</summary>
public static class Win32
{
    public const uint SPI_SETMOUSE = 0x0004;
    public const uint SPI_SETMENUSHOWDELAY = 0x006B;
    public const uint SPI_SETANIMATION = 0x0049;
    public const uint SPI_SETKEYBOARDDELAY = 0x0017;
    public const uint SPI_SETDRAGFULLWINDOWS = 0x0025;
    public const uint SPIF_UPDATEINIFILE = 0x01;
    public const uint SPIF_SENDCHANGE = 0x02;
    public const int HWND_BROADCAST = 0xffff;
    public const uint WM_SETTINGCHANGE = 0x001A;

    [StructLayout(LayoutKind.Sequential)]
    public struct ANIMATIONINFO { public uint cbSize; public int iMinAnimate; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, int[] vparam, uint winIni);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, IntPtr vparam, uint winIni);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, ref ANIMATIONINFO vparam, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    public static void BroadcastSettingChange(string area)
    {
        SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, area, 0x0002 /*SMTO_ABORTIFHUNG*/, 2000, out _);
    }
}
