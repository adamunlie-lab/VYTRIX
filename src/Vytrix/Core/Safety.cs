using System.IO;
using System.Management;
using Microsoft.Win32;

namespace Vytrix.Core;

public enum Hive { HKCU, HKLM }

/// <summary>Exact previous state of one registry value, captured before VYTRIX changes it.</summary>
public sealed class RegSnapshot
{
    public Hive Hive { get; set; }
    public string Path { get; set; } = "";
    public string? Name { get; set; }             // null = the key's (Default) value; ignored when KeyOnly
    public bool KeyOnly { get; set; }             // the op only ensures the key exists
    public bool ValueExisted { get; set; }
    public string? CreatedRoot { get; set; }      // first key in Path that did not exist; deleted on rollback
    public RegistryValueKind Kind { get; set; }
    public string? StringData { get; set; }
    public long? NumberData { get; set; }
    public string[]? MultiData { get; set; }
    public string? BinaryData { get; set; }      // base64

    public string Display => $@"{Hive}\{Path}{(KeyOnly ? "" : "\\" + (Name ?? "(Default)"))}";
}

public static class Reg
{
    public static RegistryKey Base(Hive h) => RegistryKey.OpenBaseKey(h == Hive.HKCU ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);

    public static object? Read(Hive h, string path, string? name)
    {
        using var b = Base(h);
        using var k = b.OpenSubKey(path);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public static bool KeyExists(Hive h, string path)
    {
        using var b = Base(h);
        using var k = b.OpenSubKey(path);
        return k is not null;
    }

    public static RegSnapshot Capture(Hive h, string path, string? name, bool keyOnly = false)
    {
        var s = new RegSnapshot { Hive = h, Path = path, Name = name, KeyOnly = keyOnly };
        using var b = Base(h);
        // Find the first missing key so rollback can remove exactly what we created.
        var parts = path.Split('\\');
        for (int i = 1; i <= parts.Length; i++)
        {
            var sub = string.Join('\\', parts.Take(i));
            using var probe = b.OpenSubKey(sub);
            if (probe is null) { s.CreatedRoot = sub; break; }
        }
        if (keyOnly || s.CreatedRoot is not null) return s;
        using var k = b.OpenSubKey(path);
        var v = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (v is null) return s;
        s.ValueExisted = true;
        s.Kind = k!.GetValueKind(name);
        switch (v)
        {
            case int i32: s.NumberData = i32; break;
            case long i64: s.NumberData = i64; break;
            case string str: s.StringData = str; break;
            case string[] multi: s.MultiData = multi; break;
            case byte[] bin: s.BinaryData = Convert.ToBase64String(bin); break;
        }
        return s;
    }

    public static void Write(Hive h, string path, string? name, object value, RegistryValueKind kind)
    {
        using var b = Base(h);
        using var k = b.CreateSubKey(path, true) ?? throw new IOException($"Cannot open {h}\\{path}");
        k.SetValue(name ?? "", value, kind);
    }

    public static void EnsureKey(Hive h, string path)
    {
        using var b = Base(h);
        using var k = b.CreateSubKey(path, true);
    }

    public static void DeleteValue(Hive h, string path, string? name)
    {
        using var b = Base(h);
        using var k = b.OpenSubKey(path, true);
        k?.DeleteValue(name ?? "", false);
    }

    public static void Restore(RegSnapshot s)
    {
        using var b = Base(s.Hive);
        if (s.CreatedRoot is not null)
        {
            b.DeleteSubKeyTree(s.CreatedRoot, false);
            return;
        }
        if (s.KeyOnly) return;
        if (!s.ValueExisted) { DeleteValue(s.Hive, s.Path, s.Name); return; }
        object data = s.Kind switch
        {
            RegistryValueKind.DWord => unchecked((int)(s.NumberData ?? 0)),
            RegistryValueKind.QWord => s.NumberData ?? 0L,
            RegistryValueKind.MultiString => s.MultiData ?? [],
            RegistryValueKind.Binary => Convert.FromBase64String(s.BinaryData ?? ""),
            _ => s.StringData ?? "",
        };
        Write(s.Hive, s.Path, s.Name, data, s.Kind);
    }

    /// <summary>Exports a key to a .reg file with reg.exe before it is modified.</summary>
    public static string? ExportBackup(Hive h, string path, string label)
    {
        try
        {
            // Only an existing key has prior data worth exporting. A key VYTRIX is about to create
            // is covered by the snapshot's CreatedRoot (deleted on rollback). Never climb to a parent:
            // that could export an entire hive branch such as HKLM\SOFTWARE.
            var probe = path;
            if (!KeyExists(h, probe)) return null;
            var safe = string.Concat(label.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
            var file = System.IO.Path.Combine(AppPaths.Backups, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}.reg");
            var (code, _) = SysCmd.RunAsync(SysCmd.SystemExe("reg.exe"), ["export", $@"{h}\{probe}", file, "/y"], 30_000).GetAwaiter().GetResult();
            return code == 0 ? file : null;
        }
        catch (Exception ex)
        {
            Log.Warn("backup", $"Registry export failed for {h}\\{path}: {ex.Message}");
            return null;
        }
    }
}

public enum ChangeKind { Tweak, Service, Startup, Dns, PowerPlan, FullscreenOptimization, Cleanup, Profile }

public sealed class ChangeRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Time { get; set; } = DateTime.Now;
    public ChangeKind Kind { get; set; }
    public string Target { get; set; } = "";        // e.g. tweak id, service name, adapter id
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public List<RegSnapshot> Snapshots { get; set; } = [];
    public Dictionary<string, string> Extra { get; set; } = [];
    public List<string> BackupFiles { get; set; } = [];
    public bool Reversible { get; set; } = true;
    public bool RolledBack { get; set; }
    public DateTime? RolledBackAt { get; set; }
}

/// <summary>
/// Persistent record of every modification VYTRIX makes, with the exact prior
/// state needed to roll it back. Stored in %LocalAppData%\VYTRIX\journal.json.
/// </summary>
public static class Journal
{
    private static readonly object Gate = new();
    private static List<ChangeRecord> _records = Json.Load(AppPaths.JournalFile, () => new List<ChangeRecord>());
    public static event Action? Changed;

    public static IReadOnlyList<ChangeRecord> All { get { lock (Gate) return _records.OrderByDescending(r => r.Time).ToList(); } }

    public static ChangeRecord? ActiveFor(ChangeKind kind, string target)
    {
        lock (Gate) return _records.Where(r => r.Kind == kind && r.Target == target && !r.RolledBack && r.Reversible).OrderBy(r => r.Time).FirstOrDefault();
    }

    public static void Add(ChangeRecord r)
    {
        lock (Gate) { _records.Add(r); Save(); }
        Log.Change(r.Kind.ToString().ToLowerInvariant(), $"{r.Title} — {r.Detail}");
        Changed?.Invoke();
    }

    public static void MarkRolledBack(ChangeRecord r)
    {
        lock (Gate) { r.RolledBack = true; r.RolledBackAt = DateTime.Now; Save(); }
        Changed?.Invoke();
    }

    /// <summary>Marks every open record for a target as rolled back (used when a later revert supersedes them).</summary>
    public static void CloseAll(ChangeKind kind, string target)
    {
        lock (Gate)
        {
            foreach (var r in _records.Where(r => r.Kind == kind && r.Target == target && !r.RolledBack)) { r.RolledBack = true; r.RolledBackAt = DateTime.Now; }
            Save();
        }
        Changed?.Invoke();
    }

    public static void Prune(int keep = 1000)
    {
        lock (Gate)
        {
            if (_records.Count <= keep) return;
            _records = _records.OrderByDescending(r => r.Time).Take(keep).ToList();
            Save();
        }
    }

    private static void Save()
    {
        try { Json.Save(AppPaths.JournalFile, _records); }
        catch (Exception ex) { Log.Error("journal", "Failed to save change journal", ex); }
    }
}

public sealed record RestorePointInfo(uint Sequence, string Description, DateTime Created);

/// <summary>System Restore integration through the documented WMI SystemRestore class.</summary>
public static class RestorePoints
{
    public static List<RestorePointInfo> List()
    {
        var list = new List<RestorePointInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT * FROM SystemRestore");
            foreach (ManagementObject mo in searcher.Get())
            {
                var created = ManagementDateTimeConverter.ToDateTime((string)mo["CreationTime"]);
                list.Add(new RestorePointInfo((uint)mo["SequenceNumber"], (string)mo["Description"], created));
            }
        }
        catch (Exception ex) { Log.Warn("restore", $"Could not list restore points: {ex.Message}"); }
        return list.OrderByDescending(r => r.Created).ToList();
    }

    /// <summary>
    /// Creates a restore point. Windows silently skips creation if another point was
    /// made in the last 24 hours, so we verify and report what actually happened.
    /// </summary>
    public static async Task<(bool Ok, string Message)> CreateAsync(string description)
    {
        if (!Elevation.IsAdmin) return (false, "Creating a restore point requires administrator rights.");
        return await Task.Run(() =>
        {
            try
            {
                var before = List().FirstOrDefault();
                using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
                using var inParams = cls.GetMethodParameters("CreateRestorePoint");
                inParams["Description"] = description;
                inParams["RestorePointType"] = 12;   // MODIFY_SETTINGS
                inParams["EventType"] = 100;         // BEGIN_SYSTEM_CHANGE
                using var result = cls.InvokeMethod("CreateRestorePoint", inParams, null);
                var rc = Convert.ToUInt32(result["ReturnValue"]);
                if (rc == 0x422 /*ERROR_SERVICE_DISABLED*/ || rc == 1058)
                    return (false, "System Protection is turned off for the system drive. Enable it in System Properties → System Protection.");
                if (rc != 0) return (false, $"Windows returned error 0x{rc:X} while creating the restore point.");
                var after = List().FirstOrDefault();
                if (after is not null && (before is null || after.Sequence != before.Sequence))
                {
                    Log.Change("restore", $"Created restore point #{after.Sequence}: {description}");
                    return (true, $"Restore point created: {description}");
                }
                return (false, before is not null
                    ? $"Windows skipped creation because a restore point already exists from {before.Created:g} (Windows allows one every 24 hours). That point still protects you."
                    : "Windows did not create a restore point. System Protection may be disabled.");
            }
            catch (Exception ex)
            {
                Log.Error("restore", "Restore point creation failed", ex);
                return (false, $"Restore point creation failed: {ex.Message}");
            }
        });
    }

    public static void OpenSystemRestore() => SysCmd.Open(SysCmd.SystemExe("rstrui.exe"));
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "Christmas";
    public string Accent { get; set; } = "Crimson";
    /// <summary>Set once the Christmas theme has been introduced to an existing install.</summary>
    public bool ChristmasIntroduced { get; set; }
    public double MusicVolume { get; set; } = 0.55;
    /// <summary>Set once the Cherry Blossom theme has been made the default for an existing install.</summary>
    public bool CherryBlossomIntroduced { get; set; }
    public bool ShowFallingPetals { get; set; } = true;
    /// <summary>Twinkling lights around the window's edge in the Christmas theme.</summary>
    public bool ShowChristmasLights { get; set; } = true;
    public bool CreateRestorePointBeforeProfiles { get; set; } = true;
    public bool ExportRegistryBeforeChanges { get; set; } = true;
    public bool ConfirmBeforeApplying { get; set; } = true;
    public bool ReduceMotion { get; set; }
    public int TelemetryIntervalMs { get; set; } = 1000;
    public string UpdateFeedUrl { get; set; } = Services.Updater.DefaultFeed;
    public bool CheckUpdatesOnStart { get; set; } = true;
    /// <summary>Download and verify updates in the background; installing still asks first.</summary>
    public bool AutoDownloadUpdates { get; set; } = true;
    /// <summary>Set once the GitHub update channel has been applied to an existing install.</summary>
    public bool GitHubFeedIntroduced { get; set; }
    public string ActiveProfile { get; set; } = "";
    public string AcceptedTermsVersion { get; set; } = "";
    public DateTime? AcceptedTermsAt { get; set; }
    public List<string> CustomProfileTweaks { get; set; } = [];
    public string CustomProfilePowerPlan { get; set; } = "keep";   // keep | balanced | performance
    /// <summary>Let the assistant read this PC's readings, tweak states, top apps and change history.</summary>
    public bool AssistantSharePcDetails { get; set; } = true;
    /// <summary>"local" = free AI on this PC through Ollama; "claude" = Claude with the user's own key.</summary>
    public string AssistantMode { get; set; } = "local";
    public string AssistantLocalModel { get; set; } = "qwen3:4b";
    /// <summary>Switch Christmas → New Year after Boxing Day, and back after 6 January.</summary>
    public bool SeasonalThemes { get; set; } = true;
    /// <summary>True when the New Year theme was chosen by the seasonal switch (so it may be switched back).</summary>
    public bool NewYearThemeAutomatic { get; set; }
    /// <summary>Save this PC's setup to the account when it changes, and offer newer setups from other PCs.</summary>
    public bool AutoSync { get; set; } = true;
    /// <summary>The account, time and contents of the last sync, so only real changes are uploaded.</summary>
    public string LastSyncedUser { get; set; } = "";
    public DateTime? LastSyncedAt { get; set; }
    public string LastSyncedFingerprint { get; set; } = "";
}

/// <summary>The VYTRIX Terms of Agreement (legal\TERMS.txt, embedded at build time).</summary>
public static class Terms
{
    /// <summary>Bump when the terms text changes materially; users are asked to accept again.</summary>
    public const string Version = "1.6";   // 1.1: GitHub update checks; 1.2: AI assistant (Claude); 1.3: free local AI mode; 1.4: optional accounts; 1.5: account required; 1.6: automatic sync

    public static string Text { get; } = Load();

    public static bool Accepted => Settings.Current.AcceptedTermsVersion == Version;

    public static void Accept()
    {
        Settings.Current.AcceptedTermsVersion = Version;
        Settings.Current.AcceptedTermsAt = DateTime.Now;
        Settings.Save();
        Log.Info("terms", $"Terms of Agreement v{Version} accepted.");
    }

    private static string Load()
    {
        using var s = typeof(Terms).Assembly.GetManifestResourceStream("Vytrix.TERMS.txt");
        if (s is null) return "The Terms of Agreement could not be loaded. Please reinstall VYTRIX.";
        using var r = new StreamReader(s);
        return r.ReadToEnd().Replace("\r\n", "\n");
    }
}

public static class Settings
{
    public static AppSettings Current { get; } = Json.Load(AppPaths.SettingsFile, () => new AppSettings());
    public static event Action? Changed;
    public static void Save()
    {
        try { Json.Save(AppPaths.SettingsFile, Current); } catch (Exception ex) { Log.Error("settings", "Failed to save settings", ex); }
        Changed?.Invoke();
    }
}
