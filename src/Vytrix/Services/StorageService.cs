using System.IO;
using Vytrix.Core;
using Vytrix.Native;

namespace Vytrix.Services;

public sealed class CleanupCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> Roots { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool Recursive { get; init; } = true;
    public TimeSpan MinAge { get; init; } = TimeSpan.FromHours(24);
    public string? FilePattern { get; init; }
    public bool IsRecycleBin { get; init; }

    // Scan results
    public long Bytes { get; set; }
    public int FileCount { get; set; }
    public List<FileInfo> Files { get; } = [];
    public bool Selected { get; set; }
    public string? Note { get; set; }
    public string SizeText => Fmt.Bytes(Bytes);
}

public sealed record FolderSize(string Name, string Path, long Bytes, int Files)
{
    public string SizeText => Fmt.Bytes(Bytes);
}

/// <summary>
/// Safe cleanup: only well-known cache/temp locations, never user documents.
/// Every run is previewed first; files in use or modified in the last 24 h are skipped.
/// </summary>
public static class StorageService
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Common => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static List<CleanupCategory> Categories() =>
    [
        new() { Id = "usertemp", Name = "User temporary files", Description = "Files apps left in your %TEMP% folder. Only files older than 24 hours are included.", Roots = [Path.GetTempPath()], Selected = true },
        new() { Id = "wintemp", Name = "Windows temporary files", Description = "C:\\Windows\\Temp. Older than 24 hours only.", Roots = [Path.Combine(Win, "Temp")], RequiresAdmin = true, Selected = true },
        new() { Id = "shader", Name = "DirectX shader cache", Description = "Compiled shaders. Games rebuild them automatically; the first launch afterwards may stutter briefly.", Roots = [Path.Combine(Local, "D3DSCache")], MinAge = TimeSpan.FromDays(7) },
        new() { Id = "crashdumps", Name = "Application crash dumps", Description = "Memory dumps written when apps crash. Only needed when reporting a crash to a developer.", Roots = [Path.Combine(Local, "CrashDumps")], MinAge = TimeSpan.Zero, Selected = true },
        new() { Id = "wer", Name = "Windows Error Reporting files", Description = "Archived and queued error reports.", Roots = [Path.Combine(Common, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(Common, @"Microsoft\Windows\WER\ReportQueue"), Path.Combine(Local, @"Microsoft\Windows\WER\ReportArchive")], RequiresAdmin = true, MinAge = TimeSpan.Zero, Selected = true },
        new() { Id = "thumbs", Name = "Thumbnail cache", Description = "Explorer's thumbnail database. Rebuilt automatically; files Explorer has open are skipped.", Roots = [Path.Combine(Local, @"Microsoft\Windows\Explorer")], FilePattern = "thumbcache_*.db", Recursive = false, MinAge = TimeSpan.Zero },
        new() { Id = "deliveryopt", Name = "Delivery Optimization cache", Description = "Update pieces kept for sharing with other PCs. Windows re-downloads if needed.", Roots = [Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")], RequiresAdmin = true, MinAge = TimeSpan.Zero },
        new() { Id = "recycle", Name = "Recycle Bin", Description = "Files you already deleted. Emptying the Recycle Bin is permanent.", Roots = [], IsRecycleBin = true, MinAge = TimeSpan.Zero },
    ];

    public static Task ScanAsync(IEnumerable<CleanupCategory> cats, CancellationToken ct = default) => Task.Run(() =>
    {
        foreach (var c in cats)
        {
            c.Files.Clear(); c.Bytes = 0; c.FileCount = 0; c.Note = null;
            if (c.IsRecycleBin)
            {
                if (VxNative.vx_recycle_bin_query(out var b, out var n) == 1) { c.Bytes = (long)b; c.FileCount = (int)n; }
                continue;
            }
            if (c.RequiresAdmin && !Elevation.IsAdmin) { c.Note = "Requires administrator"; }
            var cutoff = DateTime.Now - c.MinAge;
            foreach (var root in c.Roots)
            {
                if (!Directory.Exists(root)) continue;
                var opts = new EnumerationOptions { RecurseSubdirectories = c.Recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, c.FilePattern ?? "*", opts); } catch { continue; }
                foreach (var f in files)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.LastWriteTime > cutoff) continue;
                        c.Files.Add(fi);
                        c.Bytes += fi.Length;
                        c.FileCount++;
                    }
                    catch { }
                }
            }
        }
    }, ct);

    public static Task<(long Freed, int Deleted, int Skipped)> CleanAsync(IEnumerable<CleanupCategory> cats) => Task.Run(() =>
    {
        long freed = 0; int deleted = 0, skipped = 0;
        foreach (var c in cats.Where(c => c.Selected))
        {
            if (c.IsRecycleBin)
            {
                if (VxNative.vx_recycle_bin_empty() == 1) { freed += c.Bytes; deleted += c.FileCount; }
                continue;
            }
            if (c.RequiresAdmin && !Elevation.IsAdmin) { skipped += c.FileCount; continue; }
            foreach (var f in c.Files)
            {
                try
                {
                    // Re-verify location at delete time: never delete outside the category roots.
                    if (!c.Roots.Any(r => f.FullName.StartsWith(r, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; }
                    var len = f.Length;
                    f.Attributes = FileAttributes.Normal;
                    f.Delete();
                    freed += len; deleted++;
                }
                catch { skipped++; }   // in use or access denied
            }
            foreach (var root in c.Roots.Where(Directory.Exists))
                RemoveEmptyDirs(root);
        }
        Journal.Add(new ChangeRecord
        {
            Kind = ChangeKind.Cleanup, Target = "cleanup", Reversible = false,
            Title = $"Cleanup freed {Fmt.Bytes(freed)}",
            Detail = $"{deleted} files removed from {string.Join(", ", cats.Where(c => c.Selected).Select(c => c.Name))}. {skipped} skipped (in use or protected).",
        });
        return (freed, deleted, skipped);
    });

    private static void RemoveEmptyDirs(string root)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                         .OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
        }
        catch { }
    }

    /// <summary>Sizes the main folders of the user profile. Read-only.</summary>
    public static async Task<List<FolderSize>> LargeFoldersAsync(IProgress<FolderSize>? progress = null, CancellationToken ct = default)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var targets = new List<(string, string)>
        {
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("Downloads", Path.Combine(profile, "Downloads")),
            ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
            ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            ("AppData\\Local", Local),
            ("AppData\\Roaming", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
        };
        var result = new List<FolderSize>();
        foreach (var (name, path) in targets.DistinctBy(t => t.Item2))
        {
            if (ct.IsCancellationRequested) break;
            if (!Directory.Exists(path)) continue;
            var fs = await Task.Run(() => Measure(name, path, ct), ct);
            result.Add(fs);
            progress?.Report(fs);
        }
        return result.OrderByDescending(r => r.Bytes).ToList();
    }

    /// <summary>Lists the biggest immediate subfolders of a folder. Read-only.</summary>
    public static Task<List<FolderSize>> ChildrenAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<FolderSize>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(path))
            {
                if (ct.IsCancellationRequested) break;
                try { if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; } catch { continue; }
                list.Add(Measure(Path.GetFileName(d), d, ct));
            }
        }
        catch { }
        return list.OrderByDescending(f => f.Bytes).Take(25).ToList();
    }, ct);

    private static FolderSize Measure(string name, string path, CancellationToken ct)
    {
        long bytes = 0; int files = 0;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var fi in new DirectoryInfo(path).EnumerateFiles("*", opts))
            {
                if (ct.IsCancellationRequested) break;
                try { bytes += fi.Length; files++; } catch { }
            }
        }
        catch { }
        return new FolderSize(name, path, bytes, files);
    }
}
