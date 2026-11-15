using System.IO;
using System.Text.Json;
using Vytrix.Core;

namespace Vytrix.Services;

/// <summary>A portable list of tweaks, for moving a setup to another PC.</summary>
public sealed record TweakSetupFile
{
    public const string FormatName = "vytrix-tweak-setup";
    /// <summary>Empty unless the file says what it is, so unrelated JSON files are rejected.</summary>
    public string Format { get; init; } = "";
    public int Version { get; init; } = 1;
    public string App { get; init; } = "";
    public DateTime Exported { get; init; }
    public List<string> Tweaks { get; init; } = [];
    public string PowerPlan { get; init; } = "keep";
}

/// <summary>Result of reading a setup file: what can be used here and what can't.</summary>
public sealed record TweakSetupImport(List<Tweak> Usable, List<string> Unknown, List<Tweak> Unsupported, string PowerPlan);

public static class TweakSetup
{
    /// <summary>Every tweak that's on right now (whether VYTRIX or Windows turned it on), plus the Custom profile's power plan.</summary>
    public static TweakSetupFile Current() => new()
    {
        Format = TweakSetupFile.FormatName,
        App = $"VYTRIX {Updater.Current.ToString(3)}",
        Exported = DateTime.Now,
        Tweaks = TweakCatalog.All.Where(t => { try { return t.GetState() == TweakState.Applied; } catch { return false; } }).Select(t => t.Id).ToList(),
        PowerPlan = Settings.Current.CustomProfilePowerPlan,
    };

    public static void Export(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(Current(), Json.Options));
        Log.Info("setup", $"Exported tweak setup to {Path.GetFileName(path)}.");
    }

    /// <summary>Reads a setup file. Nothing is applied: the tweaks go into the Custom profile for review.</summary>
    public static TweakSetupImport Read(string path)
    {
        var text = File.ReadAllText(path);
        if (text.Length > 200_000) throw new InvalidDataException("That file is too large to be a VYTRIX tweak setup.");
        TweakSetupFile? file;
        try { file = JsonSerializer.Deserialize<TweakSetupFile>(text, Json.Options); }
        catch (JsonException) { throw new InvalidDataException("That file isn't a VYTRIX tweak setup."); }
        if (file is null || file.Format != TweakSetupFile.FormatName) throw new InvalidDataException("That file isn't a VYTRIX tweak setup.");
        if (file.Version > 1) throw new InvalidDataException("That setup was made by a newer VYTRIX. Update VYTRIX, then try again.");
        return FromFile(file);
    }

    /// <summary>Sorts a setup's tweaks into usable here, unknown to this version, and unsupported on this PC.</summary>
    public static TweakSetupImport FromFile(TweakSetupFile file)
    {
        var usable = new List<Tweak>();
        var unknown = new List<string>();
        var unsupported = new List<Tweak>();
        foreach (var id in file.Tweaks.Distinct().Take(500))
        {
            if (TweakCatalog.ById(id) is not { } t) { unknown.Add(id); continue; }
            if (t.UnsupportedReason is not null) unsupported.Add(t); else usable.Add(t);
        }
        var plan = file.PowerPlan is "keep" or "balanced" or "performance" ? file.PowerPlan : "keep";
        return new TweakSetupImport(usable, unknown, unsupported, plan);
    }

    /// <summary>Loads the usable tweaks into the Custom profile. The user still reviews and applies it.</summary>
    public static void LoadIntoCustom(TweakSetupImport import)
    {
        Settings.Current.CustomProfileTweaks = import.Usable.Select(t => t.Id).ToList();
        Settings.Current.CustomProfilePowerPlan = import.PowerPlan;
        Settings.Save();
        Log.Info("setup", $"Imported {import.Usable.Count} tweaks into the Custom profile.");
    }
}
