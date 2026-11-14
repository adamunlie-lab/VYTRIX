// VYTRIX native core implementation.
//
// Design rules:
//  * Never fabricate a reading. If an API is missing or fails, return
//    VX_UNAVAILABLE and let the UI say so.
//  * No undocumented kernel calls. Everything here uses public Win32 APIs
//    (PDH, DXGI, D3DKMT, PowrProf, ICMP, Toolhelp, PSAPI, Shell) or the
//    vendor's public NVML library when the NVIDIA driver ships it.
#define VYTRIXCORE_EXPORTS
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <winternl.h>
#include <iphlpapi.h>
#include <icmpapi.h>
#include <pdh.h>
#include <pdhmsg.h>
#include <psapi.h>
#include <tlhelp32.h>
#include <powrprof.h>
#include <shellapi.h>
#include <dwmapi.h>
#include <dxgi1_6.h>
#include <d3dkmthk.h>
#include <intrin.h>

#include <string>
#include <vector>
#include <unordered_map>
#include <unordered_set>
#include <algorithm>

#include "VytrixCore.h"

#pragma comment(lib, "pdh.lib")
#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "powrprof.lib")
#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "user32.lib")

namespace {

// ---------------------------------------------------------------- state
SRWLOCK g_lock = SRWLOCK_INIT;
bool    g_initialised = false;

PDH_HQUERY   g_query = nullptr;
PDH_HCOUNTER g_cpuCounter = nullptr;
PDH_HCOUNTER g_gpuEngineCounter = nullptr;
PDH_HCOUNTER g_gpuMemCounter = nullptr;
PDH_HCOUNTER g_thermalCounter = nullptr;
PDH_HCOUNTER g_diskRead = nullptr;
PDH_HCOUNTER g_diskWrite = nullptr;
PDH_HCOUNTER g_netRecv = nullptr;
PDH_HCOUNTER g_netSend = nullptr;

std::wstring g_primaryGpuLuidKey;   // "luid_0xHHHHHHHH_0xLLLLLLLL"
uint32_t     g_primaryGpuVendor = 0;

// NVML (NVIDIA Management Library) — loaded dynamically, optional.
typedef int (*nvmlInit_t)();
typedef int (*nvmlShutdown_t)();
typedef int (*nvmlDeviceGetCount_t)(unsigned int*);
typedef int (*nvmlDeviceGetHandleByIndex_t)(unsigned int, void**);
typedef int (*nvmlDeviceGetTemperature_t)(void*, int, unsigned int*);
HMODULE                       g_nvml = nullptr;
nvmlShutdown_t                g_nvmlShutdown = nullptr;
nvmlDeviceGetTemperature_t    g_nvmlGetTemp = nullptr;
void*                         g_nvmlDevice = nullptr;

// ---------------------------------------------------------------- helpers
void CopyStr(wchar_t* dst, size_t cch, const std::wstring& src) {
    if (!dst || cch == 0) return;
    wcsncpy_s(dst, cch, src.c_str(), _TRUNCATE);
}

std::wstring LuidKey(LONG high, DWORD low) {
    wchar_t buf[64];
    swprintf_s(buf, L"luid_0x%08X_0x%08X", static_cast<unsigned>(high), static_cast<unsigned>(low));
    return buf;
}

PDH_HCOUNTER AddCounter(const wchar_t* path) {
    PDH_HCOUNTER c = nullptr;
    if (PdhAddEnglishCounterW(g_query, path, 0, &c) != ERROR_SUCCESS) return nullptr;
    return c;
}

bool ReadDouble(PDH_HCOUNTER c, double& value) {
    if (!c) return false;
    PDH_FMT_COUNTERVALUE v{};
    if (PdhGetFormattedCounterValue(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, nullptr, &v) != ERROR_SUCCESS) return false;
    if (v.CStatus != PDH_CSTATUS_VALID_DATA && v.CStatus != PDH_CSTATUS_NEW_DATA) return false;
    value = v.doubleValue;
    return true;
}

// Reads a wildcard counter into (instance, value) pairs.
bool ReadArray(PDH_HCOUNTER c, std::vector<std::pair<std::wstring, double>>& out) {
    out.clear();
    if (!c) return false;
    DWORD bytes = 0, count = 0;
    PDH_STATUS s = PdhGetFormattedCounterArrayW(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, &bytes, &count, nullptr);
    if (s != PDH_MORE_DATA) return false;
    std::vector<BYTE> buffer(bytes);
    auto items = reinterpret_cast<PDH_FMT_COUNTERVALUE_ITEM_W*>(buffer.data());
    s = PdhGetFormattedCounterArrayW(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, &bytes, &count, items);
    if (s != ERROR_SUCCESS) return false;
    out.reserve(count);
    for (DWORD i = 0; i < count; ++i) {
        if (items[i].FmtValue.CStatus != PDH_CSTATUS_VALID_DATA && items[i].FmtValue.CStatus != PDH_CSTATUS_NEW_DATA) continue;
        out.emplace_back(items[i].szName ? items[i].szName : L"", items[i].FmtValue.doubleValue);
    }
    return true;
}

void InitNvml() {
    wchar_t sys[MAX_PATH];
    GetSystemDirectoryW(sys, MAX_PATH);
    std::wstring candidates[] = {
        std::wstring(sys) + L"\\nvml.dll",
        L"C:\\Program Files\\NVIDIA Corporation\\NVSMI\\nvml.dll",
    };
    for (auto& path : candidates) {
        g_nvml = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (g_nvml) break;
    }
    if (!g_nvml) return;
    auto init = reinterpret_cast<nvmlInit_t>(GetProcAddress(g_nvml, "nvmlInit_v2"));
    auto count = reinterpret_cast<nvmlDeviceGetCount_t>(GetProcAddress(g_nvml, "nvmlDeviceGetCount_v2"));
    auto byIndex = reinterpret_cast<nvmlDeviceGetHandleByIndex_t>(GetProcAddress(g_nvml, "nvmlDeviceGetHandleByIndex_v2"));
    g_nvmlGetTemp = reinterpret_cast<nvmlDeviceGetTemperature_t>(GetProcAddress(g_nvml, "nvmlDeviceGetTemperature"));
    g_nvmlShutdown = reinterpret_cast<nvmlShutdown_t>(GetProcAddress(g_nvml, "nvmlShutdown"));
    unsigned n = 0;
    if (!init || !count || !byIndex || !g_nvmlGetTemp || init() != 0 || count(&n) != 0 || n == 0 || byIndex(0, &g_nvmlDevice) != 0) {
        g_nvmlDevice = nullptr;
        g_nvmlGetTemp = nullptr;
        if (g_nvmlShutdown && init) g_nvmlShutdown();
        FreeLibrary(g_nvml);
        g_nvml = nullptr;
    }
}

struct GpuEnum {
    std::vector<VxGpuInfo> gpus;
};

void QueryHags(VxGpuInfo& g) {
    g.hagsSupported = -1;
    g.hagsEnabled = -1;
    D3DKMT_OPENADAPTERFROMLUID open{};
    open.AdapterLuid.LowPart = g.luidLow;
    open.AdapterLuid.HighPart = g.luidHigh;
    if (D3DKMTOpenAdapterFromLuid(&open) != 0) return;
    D3DKMT_WDDM_2_7_CAPS caps{};
    D3DKMT_QUERYADAPTERINFO q{};
    q.hAdapter = open.hAdapter;
    q.Type = KMTQAITYPE_WDDM_2_7_CAPS;
    q.pPrivateDriverData = &caps;
    q.PrivateDriverDataSize = sizeof(caps);
    if (D3DKMTQueryAdapterInfo(&q) == 0) {
        g.hagsSupported = caps.HwSchSupported ? 1 : 0;
        g.hagsEnabled = caps.HwSchEnabled ? 1 : 0;
    }
    D3DKMT_CLOSEADAPTER close{ open.hAdapter };
    D3DKMTCloseAdapter(&close);
}

GpuEnum EnumerateGpus() {
    GpuEnum result;
    IDXGIFactory1* factory = nullptr;
    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory)))) return result;
    IDXGIAdapter1* adapter = nullptr;
    for (UINT i = 0; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; ++i) {
        DXGI_ADAPTER_DESC1 d{};
        if (SUCCEEDED(adapter->GetDesc1(&d))) {
            VxGpuInfo g{};
            CopyStr(g.name, _countof(g.name), d.Description);
            g.dedicatedVideoMemory = d.DedicatedVideoMemory;
            g.sharedSystemMemory = d.SharedSystemMemory;
            g.luidLow = d.AdapterLuid.LowPart;
            g.luidHigh = d.AdapterLuid.HighPart;
            g.vendorId = d.VendorId;
            g.deviceId = d.DeviceId;
            g.isSoftware = (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) ? 1 : 0;
            if (!g.isSoftware) QueryHags(g);
            // DXGI can list the same LUID twice on some hybrid systems.
            bool dup = false;
            for (auto& e : result.gpus) if (e.luidLow == g.luidLow && e.luidHigh == g.luidHigh) dup = true;
            if (!dup) result.gpus.push_back(g);
        }
        adapter->Release();
    }
    factory->Release();
    return result;
}

void PickPrimaryGpu() {
    auto e = EnumerateGpus();
    const VxGpuInfo* best = nullptr;
    for (auto& g : e.gpus) {
        if (g.isSoftware) continue;
        if (!best || g.dedicatedVideoMemory > best->dedicatedVideoMemory) best = &g;
    }
    if (best) {
        g_primaryGpuLuidKey = LuidKey(best->luidHigh, best->luidLow);
        g_primaryGpuVendor = best->vendorId;
    }
}

std::unordered_set<DWORD> PidsWithVisibleWindows() {
    std::unordered_set<DWORD> pids;
    EnumWindows([](HWND hwnd, LPARAM lp) -> BOOL {
        auto set = reinterpret_cast<std::unordered_set<DWORD>*>(lp);
        if (!IsWindowVisible(hwnd)) return TRUE;
        if (GetWindow(hwnd, GW_OWNER) != nullptr) return TRUE;
        LONG_PTR ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        if (ex & WS_EX_TOOLWINDOW) return TRUE;
        if (GetWindowTextLengthW(hwnd) == 0) return TRUE;
        int cloaked = 0;
        if (SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked) return TRUE;
        DWORD pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        set->insert(pid);
        return TRUE;
    }, reinterpret_cast<LPARAM>(&pids));
    return pids;
}

struct ProcessorPowerInformation {
    ULONG Number;
    ULONG MaxMhz;
    ULONG CurrentMhz;
    ULONG MhzLimit;
    ULONG MaxIdleState;
    ULONG CurrentIdleState;
};

bool GuidFromString(const wchar_t* s, GUID& g) {
    if (!s) return false;
    std::wstring str(s);
    if (str.empty()) return false;
    if (str.front() != L'{') str = L"{" + str + L"}";
    return CLSIDFromString(str.c_str(), &g) == NOERROR;
}

std::wstring GuidToString(const GUID& g) {
    wchar_t buf[64];
    swprintf_s(buf, L"%08lx-%04hx-%04hx-%02hhx%02hhx-%02hhx%02hhx%02hhx%02hhx%02hhx%02hhx",
        g.Data1, g.Data2, g.Data3, g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3],
        g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7]);
    return buf;
}

} // namespace

#pragma comment(lib, "ole32.lib")

// ====================================================================== API

VX_API const wchar_t* vx_version(void) { return L"1.0.0"; }

VX_API int32_t vx_init(void) {
    AcquireSRWLockExclusive(&g_lock);
    if (g_initialised) { ReleaseSRWLockExclusive(&g_lock); return 1; }

    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);

    if (PdhOpenQueryW(nullptr, 0, &g_query) == ERROR_SUCCESS) {
        g_cpuCounter = AddCounter(L"\\Processor Information(_Total)\\% Processor Utility");
        if (!g_cpuCounter) g_cpuCounter = AddCounter(L"\\Processor(_Total)\\% Processor Time");
        g_gpuEngineCounter = AddCounter(L"\\GPU Engine(*)\\Utilization Percentage");
        g_gpuMemCounter = AddCounter(L"\\GPU Adapter Memory(*)\\Dedicated Usage");
        g_thermalCounter = AddCounter(L"\\Thermal Zone Information(*)\\Temperature");
        g_diskRead = AddCounter(L"\\PhysicalDisk(_Total)\\Disk Read Bytes/sec");
        g_diskWrite = AddCounter(L"\\PhysicalDisk(_Total)\\Disk Write Bytes/sec");
        g_netRecv = AddCounter(L"\\Network Interface(*)\\Bytes Received/sec");
        g_netSend = AddCounter(L"\\Network Interface(*)\\Bytes Sent/sec");
        PdhCollectQueryData(g_query);   // rate counters need a baseline sample
    }

    PickPrimaryGpu();
    if (g_primaryGpuVendor == 0x10DE) InitNvml();

    g_initialised = true;
    ReleaseSRWLockExclusive(&g_lock);
    return 1;
}

VX_API void vx_shutdown(void) {
    AcquireSRWLockExclusive(&g_lock);
    if (g_query) { PdhCloseQuery(g_query); g_query = nullptr; }
    if (g_nvml) {
        if (g_nvmlShutdown) g_nvmlShutdown();
        FreeLibrary(g_nvml);
        g_nvml = nullptr;
        g_nvmlDevice = nullptr;
    }
    if (g_initialised) WSACleanup();
    g_initialised = false;
    ReleaseSRWLockExclusive(&g_lock);
}

VX_API int32_t vx_is_elevated(void) {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return 0;
    TOKEN_ELEVATION e{};
    DWORD size = 0;
    BOOL ok = GetTokenInformation(token, TokenElevation, &e, sizeof(e), &size);
    CloseHandle(token);
    return ok && e.TokenIsElevated ? 1 : 0;
}

VX_API uint64_t vx_get_uptime_ms(void) { return GetTickCount64(); }

VX_API int32_t vx_get_cpu_info(VxCpuInfo* out) {
    if (!out) return 0;
    ZeroMemory(out, sizeof(*out));

    int regs[4]{};
    __cpuid(regs, 0);
    char vendor[13]{};
    memcpy(vendor + 0, &regs[1], 4);
    memcpy(vendor + 4, &regs[3], 4);
    memcpy(vendor + 8, &regs[2], 4);
    MultiByteToWideChar(CP_ACP, 0, vendor, -1, out->vendor, _countof(out->vendor));

    __cpuid(regs, 0x80000000);
    if (static_cast<unsigned>(regs[0]) >= 0x80000004) {
        char brand[49]{};
        for (int i = 0; i < 3; ++i) {
            __cpuid(regs, 0x80000002 + i);
            memcpy(brand + i * 16, regs, 16);
        }
        std::string b(brand);
        b.erase(0, b.find_first_not_of(' '));
        b.erase(b.find_last_not_of(" \0") + 1);
        MultiByteToWideChar(CP_ACP, 0, b.c_str(), -1, out->brand, _countof(out->brand));
    }

    DWORD len = 0;
    GetLogicalProcessorInformationEx(RelationAll, nullptr, &len);
    std::vector<BYTE> buf(len);
    if (len && GetLogicalProcessorInformationEx(RelationAll,
            reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buf.data()), &len)) {
        for (DWORD off = 0; off < len;) {
            auto info = reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buf.data() + off);
            if (info->Relationship == RelationProcessorCore) {
                out->physicalCores++;
                for (WORD gi = 0; gi < info->Processor.GroupCount; ++gi)
                    out->logicalProcessors += static_cast<uint32_t>(__popcnt64(info->Processor.GroupMask[gi].Mask));
            } else if (info->Relationship == RelationCache) {
                if (info->Cache.Level == 2) out->l2CacheKB += info->Cache.CacheSize / 1024;
                if (info->Cache.Level == 3) out->l3CacheKB += info->Cache.CacheSize / 1024;
            }
            off += info->Size;
        }
    }

    if (out->logicalProcessors > 0) {
        std::vector<ProcessorPowerInformation> ppi(out->logicalProcessors);
        if (CallNtPowerInformation(ProcessorInformation, nullptr, 0, ppi.data(),
                static_cast<ULONG>(ppi.size() * sizeof(ProcessorPowerInformation))) == 0)
            out->maxMhz = ppi[0].MaxMhz;
    }
    return 1;
}

VX_API int32_t vx_enum_gpus(VxGpuInfo* out, int32_t capacity) {
    auto e = EnumerateGpus();
    int32_t n = 0;
    for (auto& g : e.gpus) {
        if (out && n < capacity) out[n] = g;
        ++n;
    }
    return n;
}

VX_API int32_t vx_get_memory(VxMemoryInfo* out) {
    if (!out) return 0;
    ZeroMemory(out, sizeof(*out));
    MEMORYSTATUSEX ms{ sizeof(ms) };
    if (!GlobalMemoryStatusEx(&ms)) return 0;
    out->totalPhys = ms.ullTotalPhys;
    out->availPhys = ms.ullAvailPhys;
    out->loadPercent = ms.dwMemoryLoad;
    PERFORMANCE_INFORMATION pi{ sizeof(pi) };
    if (GetPerformanceInfo(&pi, sizeof(pi))) {
        const uint64_t page = pi.PageSize;
        out->commitTotal = pi.CommitTotal * page;
        out->commitLimit = pi.CommitLimit * page;
        out->commitPeak = pi.CommitPeak * page;
        out->systemCache = pi.SystemCache * page;
        out->kernelPaged = pi.KernelPaged * page;
        out->kernelNonPaged = pi.KernelNonpaged * page;
        out->processCount = pi.ProcessCount;
        out->threadCount = pi.ThreadCount;
        out->handleCount = pi.HandleCount;
    }
    return 1;
}

VX_API int32_t vx_sample(VxLiveSample* out) {
    if (!out) return 0;
    out->cpuPercent = VX_UNAVAILABLE;
    out->gpuPercent = VX_UNAVAILABLE;
    out->gpuDedicatedUsed = VX_UNAVAILABLE;
    out->gpuTempC = VX_UNAVAILABLE;
    out->acpiTempC = VX_UNAVAILABLE;
    out->diskReadBps = VX_UNAVAILABLE;
    out->diskWriteBps = VX_UNAVAILABLE;
    out->netRecvBps = VX_UNAVAILABLE;
    out->netSendBps = VX_UNAVAILABLE;

    AcquireSRWLockExclusive(&g_lock);
    if (!g_initialised || !g_query) { ReleaseSRWLockExclusive(&g_lock); return 0; }
    PdhCollectQueryData(g_query);

    double v = 0;
    if (ReadDouble(g_cpuCounter, v)) out->cpuPercent = std::clamp(v, 0.0, 100.0);
    if (ReadDouble(g_diskRead, v)) out->diskReadBps = v;
    if (ReadDouble(g_diskWrite, v)) out->diskWriteBps = v;

    std::vector<std::pair<std::wstring, double>> items;

    // GPU: Task Manager reports the busiest engine. Sum every process's
    // share per physical engine, then take the max engine on the primary GPU.
    if (!g_primaryGpuLuidKey.empty() && ReadArray(g_gpuEngineCounter, items)) {
        std::unordered_map<std::wstring, double> perEngine;
        for (auto& [name, val] : items) {
            size_t l = name.find(L"luid_");
            size_t t = name.find(L"_engtype_");
            if (l == std::wstring::npos || t == std::wstring::npos) continue;
            std::wstring key = name.substr(l, t - l);
            if (key.compare(0, g_primaryGpuLuidKey.size(), g_primaryGpuLuidKey) != 0) continue;
            perEngine[key] += val;
        }
        double maxEngine = 0;
        for (auto& [k, val] : perEngine) maxEngine = std::max(maxEngine, val);
        out->gpuPercent = std::clamp(maxEngine, 0.0, 100.0);
    }

    if (!g_primaryGpuLuidKey.empty() && ReadArray(g_gpuMemCounter, items)) {
        double used = 0; bool found = false;
        for (auto& [name, val] : items)
            if (name.compare(0, g_primaryGpuLuidKey.size(), g_primaryGpuLuidKey) == 0) { used += val; found = true; }
        if (found) out->gpuDedicatedUsed = used;
    }

    if (ReadArray(g_thermalCounter, items)) {
        double maxC = VX_UNAVAILABLE;
        for (auto& [name, kelvin] : items) {
            double c = kelvin - 273.15;
            if (c > 1.0 && c < 125.0) maxC = std::max(maxC, c);   // discard nonsense zones
        }
        out->acpiTempC = maxC;
    }

    if (ReadArray(g_netRecv, items)) {
        double s = 0; for (auto& [n, val] : items) s += val; out->netRecvBps = s;
    }
    if (ReadArray(g_netSend, items)) {
        double s = 0; for (auto& [n, val] : items) s += val; out->netSendBps = s;
    }

    if (g_nvmlDevice && g_nvmlGetTemp) {
        unsigned t = 0;
        if (g_nvmlGetTemp(g_nvmlDevice, 0 /*NVML_TEMPERATURE_GPU*/, &t) == 0) out->gpuTempC = t;
    }

    ReleaseSRWLockExclusive(&g_lock);
    return 1;
}

VX_API int32_t vx_enum_processes(VxProcess* out, int32_t capacity) {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    auto windowed = PidsWithVisibleWindows();
    PROCESSENTRY32W pe{ sizeof(pe) };
    int32_t n = 0;
    for (BOOL ok = Process32FirstW(snap, &pe); ok; ok = Process32NextW(snap, &pe)) {
        if (pe.th32ProcessID == 0) continue;   // System Idle Process
        if (out && n < capacity) {
            VxProcess& p = out[n];
            ZeroMemory(&p, sizeof(p));
            p.pid = pe.th32ProcessID;
            p.parentPid = pe.th32ParentProcessID;
            p.threadCount = pe.cntThreads;
            CopyStr(p.name, _countof(p.name), pe.szExeFile);
            DWORD sid = 0;
            if (ProcessIdToSessionId(p.pid, &sid)) p.sessionId = sid;
            p.hasVisibleWindow = windowed.count(p.pid) ? 1 : 0;
            HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, p.pid);
            if (h) {
                DWORD cch = _countof(p.path);
                QueryFullProcessImageNameW(h, 0, p.path, &cch);
                PROCESS_MEMORY_COUNTERS_EX pmc{};
                pmc.cb = sizeof(pmc);
                if (GetProcessMemoryInfo(h, reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&pmc), sizeof(pmc))) {
                    p.workingSet = pmc.WorkingSetSize;
                    p.privateBytes = pmc.PrivateUsage;
                }
                CloseHandle(h);
            } else {
                p.accessDenied = 1;
            }
        }
        ++n;
    }
    CloseHandle(snap);
    return n;
}

VX_API int32_t vx_enum_power_schemes(VxPowerScheme* out, int32_t capacity) {
    GUID* active = nullptr;
    PowerGetActiveScheme(nullptr, &active);
    int32_t n = 0;
    for (ULONG i = 0;; ++i) {
        GUID g{};
        DWORD size = sizeof(g);
        if (PowerEnumerate(nullptr, nullptr, nullptr, ACCESS_SCHEME, i, reinterpret_cast<UCHAR*>(&g), &size) != ERROR_SUCCESS) break;
        if (out && n < capacity) {
            VxPowerScheme& s = out[n];
            ZeroMemory(&s, sizeof(s));
            CopyStr(s.guid, _countof(s.guid), GuidToString(g));
            DWORD nameBytes = sizeof(s.name);
            if (PowerReadFriendlyName(nullptr, &g, nullptr, nullptr, reinterpret_cast<UCHAR*>(s.name), &nameBytes) != ERROR_SUCCESS)
                CopyStr(s.name, _countof(s.name), L"(unnamed plan)");
            s.isActive = (active && IsEqualGUID(*active, g)) ? 1 : 0;
        }
        ++n;
    }
    if (active) LocalFree(active);
    return n;
}

VX_API int32_t vx_set_power_scheme(const wchar_t* guid) {
    GUID g{};
    if (!GuidFromString(guid, g)) return 0;
    return PowerSetActiveScheme(nullptr, &g) == ERROR_SUCCESS ? 1 : 0;
}

VX_API int32_t vx_duplicate_power_scheme(const wchar_t* sourceGuid, wchar_t* newGuidOut, int32_t cch) {
    GUID src{};
    if (!GuidFromString(sourceGuid, src)) return 0;
    GUID* dest = nullptr;
    if (PowerDuplicateScheme(nullptr, &src, &dest) != ERROR_SUCCESS || !dest) return 0;
    CopyStr(newGuidOut, cch, GuidToString(*dest));
    LocalFree(dest);
    return 1;
}

VX_API int32_t vx_power_read_ac(const wchar_t* subgroupGuid, const wchar_t* settingGuid, uint32_t* value) {
    GUID sub{}, set{};
    if (!value || !GuidFromString(subgroupGuid, sub) || !GuidFromString(settingGuid, set)) return 0;
    GUID* active = nullptr;
    if (PowerGetActiveScheme(nullptr, &active) != ERROR_SUCCESS || !active) return 0;
    DWORD v = 0;
    DWORD rc = PowerReadACValueIndex(nullptr, active, &sub, &set, &v);
    LocalFree(active);
    if (rc != ERROR_SUCCESS) return 0;
    *value = v;
    return 1;
}

VX_API int32_t vx_power_write_ac(const wchar_t* subgroupGuid, const wchar_t* settingGuid, uint32_t value) {
    GUID sub{}, set{};
    if (!GuidFromString(subgroupGuid, sub) || !GuidFromString(settingGuid, set)) return 0;
    GUID* active = nullptr;
    if (PowerGetActiveScheme(nullptr, &active) != ERROR_SUCCESS || !active) return 0;
    DWORD rc = PowerWriteACValueIndex(nullptr, active, &sub, &set, value);
    if (rc == ERROR_SUCCESS) rc = PowerSetActiveScheme(nullptr, active);   // re-apply so it takes effect now
    LocalFree(active);
    return rc == ERROR_SUCCESS ? 1 : 0;
}

VX_API int32_t vx_ping(const wchar_t* host, uint32_t timeoutMs, uint32_t* rttMs) {
    if (!host || !rttMs) return 3;
    *rttMs = 0;
    ADDRINFOW hints{};
    hints.ai_family = AF_INET;
    ADDRINFOW* res = nullptr;
    if (GetAddrInfoW(host, nullptr, &hints, &res) != 0 || !res) return 2;
    IPAddr addr = reinterpret_cast<sockaddr_in*>(res->ai_addr)->sin_addr.S_un.S_addr;
    FreeAddrInfoW(res);

    HANDLE icmp = IcmpCreateFile();
    if (icmp == INVALID_HANDLE_VALUE) return 3;
    char payload[32] = "VYTRIX-latency-diagnostic-probe";
    BYTE reply[sizeof(ICMP_ECHO_REPLY) + sizeof(payload) + 8 + 64];
    DWORD r = IcmpSendEcho(icmp, addr, payload, sizeof(payload), nullptr, reply, sizeof(reply), timeoutMs);
    int32_t status = 1;
    if (r > 0) {
        auto er = reinterpret_cast<PICMP_ECHO_REPLY>(reply);
        if (er->Status == IP_SUCCESS) { *rttMs = er->RoundTripTime; status = 0; }
        else status = (er->Status == IP_REQ_TIMED_OUT) ? 1 : 3;
    } else {
        status = (GetLastError() == IP_REQ_TIMED_OUT) ? 1 : 3;
    }
    IcmpCloseHandle(icmp);
    return status;
}

VX_API int32_t vx_recycle_bin_query(uint64_t* bytes, uint64_t* items) {
    SHQUERYRBINFO info{ sizeof(info) };
    if (FAILED(SHQueryRecycleBinW(nullptr, &info))) return 0;
    if (bytes) *bytes = static_cast<uint64_t>(info.i64Size);
    if (items) *items = static_cast<uint64_t>(info.i64NumItems);
    return 1;
}

VX_API int32_t vx_recycle_bin_empty(void) {
    HRESULT hr = SHEmptyRecycleBinW(nullptr, nullptr, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
    // S_OK, or E_UNEXPECTED when the bin is already empty.
    return SUCCEEDED(hr) || hr == E_UNEXPECTED ? 1 : 0;
}
