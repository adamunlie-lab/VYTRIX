using System.IO;
using System.Runtime.CompilerServices;

namespace Vytrix.Tests;

/// <summary>
/// Points VYTRIX at an isolated data folder before any test touches AppPaths,
/// so tests never read or modify the real journal, settings or logs.
/// </summary>
internal static class TestEnvironment
{
    public static readonly string DataDir = Path.Combine(Path.GetTempPath(), "vytrix-tests-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(DataDir);
        Environment.SetEnvironmentVariable("VYTRIX_DATA_DIR", DataDir);
    }
}

/// <summary>All registry tests use this scratch key under HKCU and delete it afterwards.</summary>
public sealed class ScratchKey : IDisposable
{
    public const string Root = @"Software\VYTRIX-Tests";
    public string Path { get; } = $@"{Root}\{Guid.NewGuid():N}";

    public void Dispose()
    {
        using var hkcu = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64);
        hkcu.DeleteSubKeyTree(Path, false);
        using var root = hkcu.OpenSubKey(Root);
        if (root is not null && root.SubKeyCount == 0 && root.ValueCount == 0) hkcu.DeleteSubKey(Root, false);
    }
}
