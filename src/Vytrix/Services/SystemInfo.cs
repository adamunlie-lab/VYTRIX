using System.IO;
using System.Management;
using System.Windows.Threading;
using Vytrix.Core;
using Vytrix.Native;

namespace Vytrix.Services;

public sealed record MemoryModule(string Bank, string Manufacturer, string PartNumber, ulong Capacity, uint SpeedMhz);
public sealed record DiskDevice(string Model, ulong Size, string MediaType, string BusType, string Health);
public sealed record VolumeInfo(string Name, string Label, string Format, long Total, long Free)
{
    public long Used => Total - Free;
    public double UsedPct => Total > 0 ? Used * 100.0 / Total : 0;
}

/// <summary>Static hardware and OS information, gathered once (WMI is slow) on a background thread.</summary>
public static class SystemInfo
{
    public static VxNative.CpuInfo Cpu { get; private set; }
    public static IReadOnlyList<VxNative.GpuInfo> Gpus { get; private set; } = [];
    public static VxNative.GpuInfo? PrimaryGpu => Gpus.Where(g => g.IsSoftware == 0).OrderByDescending(g => g.DedicatedVideoMemory).Cast<VxNative.GpuInfo?>().FirstOrDefault();
    public static string OsName { get; private set; } = "Windows";
    public static string OsVersion { get; private set; } = "";
    public static string OsBuild { get; private set; } = "";
    public static DateTime? InstallDate { get; private set; }
    public static string ComputerName { get; } = Environment.MachineName;
    public static string Motherboard { get; private set; } = "Unavailable";
    public static string Bios { get; private set; } = "Unavailable";
    public static string SystemModel { get; private set; } = "Unavailable";
    public static bool IsLaptop { get; private set; }
    public static IReadOnlyList<MemoryModule> MemoryModules { get; private set; } = [];
    public static IReadOnlyList<DiskDevice> Disks { get; private set; } = [];
    public static ulong TotalMemory { get; private set; }
    public static bool Loaded { get; private set; }
    public static event Action? LoadedChanged;

    public static void LoadNative()
    {
        VxNative.vx_get_cpu_info(out var cpu);
        Cpu = cpu;
        Gpus = VxNative.EnumGpus();
        if (VxNative.vx_get_memory(out var m) == 1) TotalMemory = m.TotalPhys;
        ReadOsFromRegistry();
    }

    private static void ReadOsFromRegistry()
    {
        const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = Reg.Read(Hive.HKLM, cv, "ProductName") as string ?? "Windows";
        var build = Reg.Read(Hive.HKLM, cv, "CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString();
        var ubr = Reg.Read(Hive.HKLM, cv, "UBR") is int u ? u : 0;
        // ProductName still says "Windows 10" on Windows 11 for compatibility; correct it from the build number.
        if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        OsName = product;
        OsVersion = Reg.Read(Hive.HKLM, cv, "DisplayVersion") as string ?? Reg.Read(Hive.HKLM, cv, "ReleaseId") as string ?? "";
        OsBuild = $"{build}.{ubr}";
    }

    public static Task LoadWmiAsync() => Task.Run(() =>
    {
        try
        {
            foreach (var mo in Query("SELECT InstallDate FROM Win32_OperatingSystem"))
                InstallDate = ManagementDateTimeConverter.ToDateTime((string)mo["InstallDate"]);
            foreach (var mo in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                Motherboard = $"{mo["Manufacturer"]} {mo["Product"]}".Trim();
            foreach (var mo in Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
            {
                var date = mo["ReleaseDate"] is string d ? ManagementDateTimeConverter.ToDateTime(d).ToString("yyyy-MM-dd") : "";
                Bios = $"{mo["Manufacturer"]} {mo["SMBIOSBIOSVersion"]} {date}".Trim();
            }
            foreach (var mo in Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                SystemModel = $"{mo["Manufacturer"]} {mo["Model"]}".Trim();
            foreach (var mo in Query("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
                if (mo["ChassisTypes"] is ushort[] types) IsLaptop = types.Any(t => t is 8 or 9 or 10 or 11 or 12 or 14 or 18 or 21 or 30 or 31 or 32);

            MemoryModules = Query("SELECT BankLabel, DeviceLocator, Manufacturer, PartNumber, Capacity, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory")
                .Select(mo => new MemoryModule(
                    $"{mo["DeviceLocator"]}".Trim(),
                    $"{mo["Manufacturer"]}".Trim(),
                    $"{mo["PartNumber"]}".Trim(),
                    Convert.ToUInt64(mo["Capacity"] ?? 0UL),
                    Convert.ToUInt32(mo["ConfiguredClockSpeed"] ?? mo["Speed"] ?? 0u))).ToList();

            var disks = new List<DiskDevice>();
            try
            {
                using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, Size, MediaType, BusType, HealthStatus FROM MSFT_PhysicalDisk");
                foreach (ManagementObject mo in s.Get())
                {
                    string media = Convert.ToUInt16(mo["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unknown" };
                    string bus = Convert.ToUInt16(mo["BusType"] ?? 0) switch { 7 => "USB", 8 => "RAID", 11 => "SATA", 17 => "NVMe", 10 => "SAS", 6 => "Fibre", _ => "Other" };
                    string health = Convert.ToUInt16(mo["HealthStatus"] ?? 5) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
                    disks.Add(new DiskDevice($"{mo["FriendlyName"]}", Convert.ToUInt64(mo["Size"] ?? 0UL), media, bus, health));
                }
            }
            catch (Exception ex) { Log.Warn("sysinfo", $"Storage WMI unavailable: {ex.Message}"); }
            Disks = disks;
        }
        catch (Exception ex) { Log.Warn("sysinfo", $"WMI query failed: {ex.Message}"); }
        Loaded = true;
        LoadedChanged?.Invoke();
    });

    private static List<ManagementObject> Query(string wql)
    {
        using var s = new ManagementObjectSearcher(wql);
        return s.Get().Cast<ManagementObject>().ToList();
    }

    public static List<VolumeInfo> Volumes() =>
        DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => new VolumeInfo(d.Name, string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel, d.DriveFormat, d.TotalSize, d.TotalFreeSpace))
            .ToList();
}

/// <summary>Live telemetry pump. Samples the native core on a background thread and publishes on the UI thread.</summary>
public sealed class Telemetry
{
    public static Telemetry Instance { get; } = new();
    public const int HistoryLength = 90;

    // Until the first sample arrives every reading is "unavailable", never a default zero.
    public VxNative.LiveSample Last { get; private set; } = new()
    {
        CpuPercent = VxNative.Unavailable, GpuPercent = VxNative.Unavailable, GpuDedicatedUsed = VxNative.Unavailable,
        GpuTempC = VxNative.Unavailable, AcpiTempC = VxNative.Unavailable, DiskReadBps = VxNative.Unavailable,
        DiskWriteBps = VxNative.Unavailable, NetRecvBps = VxNative.Unavailable, NetSendBps = VxNative.Unavailable,
    };
    /// <summary>False until the first real sample has been taken. UIs show "Measuring…" until then.</summary>
    public bool HasSample { get; private set; }
    public VxNative.MemoryInfo Memory { get; private set; }
    public double[] CpuHistory { get; } = new double[HistoryLength];
    public double[] GpuHistory { get; } = new double[HistoryLength];
    public double[] RamHistory { get; } = new double[HistoryLength];
    public double[] NetHistory { get; } = new double[HistoryLength];
    public int BackgroundProcesses { get; private set; }
    public int TotalProcesses { get; private set; }
    public event Action? Updated;

    private Telemetry()
    {
        // -1 = "no sample yet": graphs start empty instead of drawing a fake zero line.
        foreach (var h in new[] { CpuHistory, GpuHistory, RamHistory, NetHistory }) Array.Fill(h, -1.0);
    }

    private Timer? _timer;
    private Dispatcher? _dispatcher;
    private int _tick;
    private int _busy;

    public void Start(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = new Timer(_ => Tick(), null, 250, Settings.Current.TelemetryIntervalMs);
    }

    public void SetInterval(int ms) => _timer?.Change(ms, ms);

    public void Stop() => _timer?.Dispose();

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            VxNative.vx_sample(out var s);
            VxNative.vx_get_memory(out var m);
            Last = s;
            Memory = m;
            HasSample = true;
            Push(CpuHistory, s.CpuPercent);
            Push(GpuHistory, s.GpuPercent);
            Push(RamHistory, m.TotalPhys > 0 ? (m.TotalPhys - m.AvailPhys) * 100.0 / m.TotalPhys : 0);
            Push(NetHistory, Math.Max(0, s.NetRecvBps) + Math.Max(0, s.NetSendBps));
            if (_tick++ % 5 == 0)
            {
                var procs = VxNative.EnumProcesses();
                TotalProcesses = procs.Length;
                BackgroundProcesses = procs.Count(p => p.HasVisibleWindow == 0);
            }
            _dispatcher?.BeginInvoke(() => Updated?.Invoke(), DispatcherPriority.Background);
        }
        catch (Exception ex) { Log.Error("telemetry", "Sample failed", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static void Push(double[] buf, double v)
    {
        lock (buf)
        {
            Array.Copy(buf, 1, buf, 0, buf.Length - 1);
            buf[^1] = v;
        }
    }
}
