using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vytrix.Core;

public static class AppPaths
{
    // VYTRIX_DATA_DIR lets automated tests use an isolated data folder.
    public static string Root { get; } = Ensure(Environment.GetEnvironmentVariable("VYTRIX_DATA_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VYTRIX"));
    public static string Logs { get; } = Ensure(Path.Combine(Root, "logs"));
    public static string Backups { get; } = Ensure(Path.Combine(Root, "backups"));
    public static string Reports { get; } = Ensure(Environment.GetEnvironmentVariable("VYTRIX_DATA_DIR") is { Length: > 0 } d ? Path.Combine(d, "reports") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VYTRIX Reports"));
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string JournalFile => Path.Combine(Root, "journal.json");
    public static string GameProfilesFile => Path.Combine(Root, "game-profiles.json");

    private static string Ensure(string p) { Directory.CreateDirectory(p); return p; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback();
        }
        catch (Exception ex)
        {
            Log.Warn("json", $"Could not read {Path.GetFileName(path)}: {ex.Message}. A fresh copy will be used.");
            try { File.Copy(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
        }
        return fallback();
    }

    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);   // atomic replace, never leaves a half-written file
    }
}

public enum LogLevel { Info, Warn, Error, Change }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Area, string Message);

/// <summary>Structured JSON-lines logger. One file per day under %LocalAppData%\VYTRIX\logs.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly List<LogEntry> Recent = [];
    public static event Action<LogEntry>? Written;

    public static IReadOnlyList<LogEntry> RecentEntries { get { lock (Gate) return Recent.ToList(); } }
    public static string CurrentFile => Path.Combine(AppPaths.Logs, $"vytrix-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string area, string msg) => Write(LogLevel.Info, area, msg);
    public static void Warn(string area, string msg) => Write(LogLevel.Warn, area, msg);
    public static void Error(string area, string msg, Exception? ex = null) => Write(LogLevel.Error, area, ex is null ? msg : $"{msg} — {ex.GetType().Name}: {ex.Message}");
    public static void Change(string area, string msg) => Write(LogLevel.Change, area, msg);

    private static void Write(LogLevel level, string area, string msg)
    {
        var e = new LogEntry(DateTime.Now, level, area, msg);
        lock (Gate)
        {
            Recent.Add(e);
            if (Recent.Count > 2000) Recent.RemoveRange(0, 500);
            try
            {
                File.AppendAllText(CurrentFile, JsonSerializer.Serialize(new { t = e.Time.ToString("o"), level = level.ToString(), area, msg }) + Environment.NewLine);
            }
            catch { /* logging must never crash the app */ }
        }
        Written?.Invoke(e);
    }

    public static IEnumerable<LogEntry> ReadDay(DateTime day)
    {
        var path = Path.Combine(AppPaths.Logs, $"vytrix-{day:yyyyMMdd}.log");
        if (!File.Exists(path)) yield break;
        string[] lines;
        try { lines = File.ReadAllLines(path); } catch { yield break; }
        foreach (var line in lines)
        {
            LogEntry? entry = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                entry = new LogEntry(DateTime.Parse(r.GetProperty("t").GetString()!), Enum.Parse<LogLevel>(r.GetProperty("level").GetString()!),
                    r.GetProperty("area").GetString() ?? "", r.GetProperty("msg").GetString() ?? "");
            }
            catch { }
            if (entry is not null) yield return entry;
        }
    }
}

public static class Elevation
{
    public static bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Relaunches VYTRIX through UAC. Returns false if the user declined the prompt.</summary>
    public static bool RelaunchElevated(string? args = null)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            Process.Start(new ProcessStartInfo(exe, args ?? "") { UseShellExecute = true, Verb = "runas" });
            Log.Info("elevation", "Relaunching with administrator rights.");
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Log.Info("elevation", "User declined the UAC prompt.");
            return false;
        }
    }
}

public static class Fmt
{
    public static string Bytes(double b)
    {
        if (b < 0) return "—";
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{b:0} {u[i]}" : $"{b:0.0} {u[i]}";
    }

    public static string Rate(double bps) => bps < 0 ? "—" : Bytes(bps) + "/s";
    public static string Pct(double p) => p < 0 ? "N/A" : $"{p:0}%";

    public static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
}

public static class SysCmd
{
    /// <summary>Runs a built-in Windows command and captures its output. Never uses a shell string.</summary>
    public static async Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, "Timed out."); }
        return (p.ExitCode, (await stdout + await stderr).Trim());
    }

    public static string SystemExe(string name) => Path.Combine(Environment.SystemDirectory, name);

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("shell", $"Could not open {target}", ex); }
    }
}
