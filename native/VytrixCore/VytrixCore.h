// VYTRIX native core — system-level telemetry and Windows API access.
// All exports use the C ABI (cdecl) and plain structs so they can be
// marshalled from .NET without custom marshalling code.
#pragma once
#include <stdint.h>

#ifdef VYTRIXCORE_EXPORTS
#define VX_API extern "C" __declspec(dllexport)
#else
#define VX_API extern "C" __declspec(dllimport)
#endif

// Sentinel used for any reading that is not available on this machine.
// The UI must display "Unavailable" rather than a number when it sees it.
#define VX_UNAVAILABLE (-1.0)

#pragma pack(push, 8)

typedef struct VxCpuInfo {
    wchar_t  brand[80];
    wchar_t  vendor[16];
    uint32_t physicalCores;
    uint32_t logicalProcessors;
    uint32_t l2CacheKB;
    uint32_t l3CacheKB;
    uint32_t maxMhz;          // from CallNtPowerInformation, 0 if unknown
} VxCpuInfo;

typedef struct VxGpuInfo {
    wchar_t  name[128];
    uint64_t dedicatedVideoMemory;
    uint64_t sharedSystemMemory;
    uint32_t luidLow;
    int32_t  luidHigh;
    uint32_t vendorId;
    uint32_t deviceId;
    int32_t  isSoftware;
    int32_t  hagsSupported;   // -1 unknown, 0 no, 1 yes
    int32_t  hagsEnabled;     // -1 unknown, 0 no, 1 yes
} VxGpuInfo;

typedef struct VxMemoryInfo {
    uint64_t totalPhys;
    uint64_t availPhys;
    uint64_t commitTotal;
    uint64_t commitLimit;
    uint64_t commitPeak;
    uint64_t systemCache;
    uint64_t kernelPaged;
    uint64_t kernelNonPaged;
    uint32_t loadPercent;
    uint32_t processCount;
    uint32_t threadCount;
    uint32_t handleCount;
} VxMemoryInfo;

typedef struct VxLiveSample {
    double cpuPercent;          // % Processor Utility (matches Task Manager)
    double gpuPercent;          // busiest engine of primary GPU, or VX_UNAVAILABLE
    double gpuDedicatedUsed;    // bytes, or VX_UNAVAILABLE
    double gpuTempC;            // NVML (NVIDIA) only, or VX_UNAVAILABLE
    double acpiTempC;           // ACPI thermal zone (max), or VX_UNAVAILABLE
    double diskReadBps;
    double diskWriteBps;
    double netRecvBps;
    double netSendBps;
} VxLiveSample;

typedef struct VxProcess {
    uint32_t pid;
    uint32_t parentPid;
    uint32_t sessionId;
    uint32_t threadCount;
    uint64_t workingSet;
    uint64_t privateBytes;
    int32_t  hasVisibleWindow;
    int32_t  accessDenied;
    wchar_t  name[260];
    wchar_t  path[520];
} VxProcess;

typedef struct VxPowerScheme {
    wchar_t guid[40];
    wchar_t name[128];
    int32_t isActive;
} VxPowerScheme;

#pragma pack(pop)

// Lifecycle
VX_API int32_t  vx_init(void);
VX_API void     vx_shutdown(void);
VX_API const wchar_t* vx_version(void);

// Static information
VX_API int32_t  vx_get_cpu_info(VxCpuInfo* out);
VX_API int32_t  vx_enum_gpus(VxGpuInfo* out, int32_t capacity);
VX_API int32_t  vx_get_memory(VxMemoryInfo* out);
VX_API uint64_t vx_get_uptime_ms(void);
VX_API int32_t  vx_is_elevated(void);

// Live telemetry (call roughly once per second)
VX_API int32_t  vx_sample(VxLiveSample* out);

// Processes
VX_API int32_t  vx_enum_processes(VxProcess* out, int32_t capacity);

// Power plans
VX_API int32_t  vx_enum_power_schemes(VxPowerScheme* out, int32_t capacity);
VX_API int32_t  vx_set_power_scheme(const wchar_t* guid);
VX_API int32_t  vx_duplicate_power_scheme(const wchar_t* sourceGuid, wchar_t* newGuidOut, int32_t cch);
// Read/write a plugged-in (AC) power setting of the ACTIVE scheme. Write re-applies the scheme.
VX_API int32_t  vx_power_read_ac(const wchar_t* subgroupGuid, const wchar_t* settingGuid, uint32_t* value);
VX_API int32_t  vx_power_write_ac(const wchar_t* subgroupGuid, const wchar_t* settingGuid, uint32_t value);

// Network diagnostics. Returns 0 success, 1 timeout, 2 resolve failure, 3 other error.
VX_API int32_t  vx_ping(const wchar_t* host, uint32_t timeoutMs, uint32_t* rttMs);

// Recycle Bin
VX_API int32_t  vx_recycle_bin_query(uint64_t* bytes, uint64_t* items);
VX_API int32_t  vx_recycle_bin_empty(void);
