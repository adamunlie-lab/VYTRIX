using Microsoft.Win32;
using Vytrix.Core;
using Vytrix.Services;
using Xunit;

namespace Vytrix.Tests;

/// <summary>The backup and rollback system: the most important guarantees VYTRIX makes.</summary>
public class RegistrySnapshotTests
{
    [Fact]
    public void Restores_original_dword()
    {
        using var k = new ScratchKey();
        Reg.Write(Hive.HKCU, k.Path, "Value", 7, RegistryValueKind.DWord);
        var snap = Reg.Capture(Hive.HKCU, k.Path, "Value");
        Reg.Write(Hive.HKCU, k.Path, "Value", 0, RegistryValueKind.DWord);
        Reg.Restore(snap);
        Assert.Equal(7, Reg.Read(Hive.HKCU, k.Path, "Value"));
    }

    [Fact]
    public void Restores_string_binary_and_multistring_exactly()
    {
        using var k = new ScratchKey();
        byte[] bin = [3, 0, 0, 0, 0xAA, 0xBB];
        Reg.Write(Hive.HKCU, k.Path, "S", "%TEMP%\\x", RegistryValueKind.ExpandString);
        Reg.Write(Hive.HKCU, k.Path, "B", bin, RegistryValueKind.Binary);
        Reg.Write(Hive.HKCU, k.Path, "M", new[] { "a", "b" }, RegistryValueKind.MultiString);
        var snaps = new[] { "S", "B", "M" }.Select(n => Reg.Capture(Hive.HKCU, k.Path, n)).ToList();
        Reg.Write(Hive.HKCU, k.Path, "S", "changed", RegistryValueKind.String);
        Reg.Write(Hive.HKCU, k.Path, "B", new byte[] { 1 }, RegistryValueKind.Binary);
        Reg.DeleteValue(Hive.HKCU, k.Path, "M");
        snaps.ForEach(Reg.Restore);

        using var hkcu = Reg.Base(Hive.HKCU);
        using var key = hkcu.OpenSubKey(k.Path)!;
        Assert.Equal("%TEMP%\\x", key.GetValue("S", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("S"));
        Assert.Equal(bin, (byte[])key.GetValue("B")!);
        Assert.Equal(new[] { "a", "b" }, (string[])key.GetValue("M")!);
    }

    [Fact]
    public void Value_that_did_not_exist_is_deleted_on_rollback()
    {
        using var k = new ScratchKey();
        Reg.EnsureKey(Hive.HKCU, k.Path);
        var snap = Reg.Capture(Hive.HKCU, k.Path, "New");
        Assert.False(snap.ValueExisted);
        Reg.Write(Hive.HKCU, k.Path, "New", 1, RegistryValueKind.DWord);
        Reg.Restore(snap);
        Assert.Null(Reg.Read(Hive.HKCU, k.Path, "New"));
        Assert.True(Reg.KeyExists(Hive.HKCU, k.Path));   // pre-existing key is kept
    }

    [Fact]
    public void Keys_created_by_a_change_are_removed_on_rollback()
    {
        using var k = new ScratchKey();
        Reg.EnsureKey(Hive.HKCU, k.Path);
        var deep = k.Path + @"\Created\Deeper";
        var snap = Reg.Capture(Hive.HKCU, deep, null);
        Assert.Equal(k.Path + @"\Created", snap.CreatedRoot);
        Reg.Write(Hive.HKCU, deep, null, "", RegistryValueKind.String);
        Reg.Restore(snap);
        Assert.False(Reg.KeyExists(Hive.HKCU, k.Path + @"\Created"));
        Assert.True(Reg.KeyExists(Hive.HKCU, k.Path));
    }
}

public class TweakEngineTests
{
    private static Tweak Synthetic(ScratchKey k, Risk risk = Risk.Low) => new()
    {
        Id = "test.synthetic." + Guid.NewGuid().ToString("N"), Name = "Synthetic", Category = TweakCategory.Windows,
        Description = "d", ExpectedEffect = "e", Explanation = "x", Risk = risk,
        Ops = [RegOp.DWord(Hive.HKCU, k.Path, "A", 1, 5), RegOp.Str(Hive.HKCU, k.Path + @"\Sub", "B", "on", null)],
    };

    [Fact]
    public async Task Apply_then_revert_restores_exact_prior_state_and_journals_it()
    {
        using var k = new ScratchKey();
        Reg.Write(Hive.HKCU, k.Path, "A", 42, RegistryValueKind.DWord);   // user's own value, not the Windows default
        var t = Synthetic(k);
        Assert.Equal(TweakState.NotApplied, t.GetState());

        var (ok, _) = await TweakEngine.ApplyAsync(t);
        Assert.True(ok);
        Assert.Equal(TweakState.Applied, t.GetState());
        var record = Journal.ActiveFor(ChangeKind.Tweak, t.Id);
        Assert.NotNull(record);
        Assert.Equal(2, record!.Snapshots.Count);

        (ok, _) = await TweakEngine.RevertAsync(t);
        Assert.True(ok);
        Assert.Equal(42, Reg.Read(Hive.HKCU, k.Path, "A"));          // not the "Windows default" of 5
        Assert.False(Reg.KeyExists(Hive.HKCU, k.Path + @"\Sub"));     // created key removed
        Assert.Null(Journal.ActiveFor(ChangeKind.Tweak, t.Id));
    }

    [Fact]
    public async Task Revert_without_journal_falls_back_to_windows_defaults()
    {
        using var k = new ScratchKey();
        var t = Synthetic(k);
        Reg.Write(Hive.HKCU, k.Path, "A", 1, RegistryValueKind.DWord);
        Reg.Write(Hive.HKCU, k.Path + @"\Sub", "B", "on", RegistryValueKind.String);
        Assert.Equal(TweakState.Applied, t.GetState());
        var (ok, msg) = await TweakEngine.RevertAsync(t);
        Assert.True(ok);
        Assert.Contains("Windows default", msg);
        Assert.Equal(5, Reg.Read(Hive.HKCU, k.Path, "A"));
        Assert.Null(Reg.Read(Hive.HKCU, k.Path + @"\Sub", "B"));
    }

    [Fact]
    public async Task Rollback_record_restores_registry_based_change()
    {
        using var k = new ScratchKey();
        var t = Synthetic(k);
        await TweakEngine.ApplyAsync(t);
        var rec = Journal.ActiveFor(ChangeKind.Tweak, t.Id)!;
        // A record for an unknown tweak id takes the generic snapshot path.
        var generic = new ChangeRecord { Kind = ChangeKind.Startup, Target = "x", Title = "t", Snapshots = rec.Snapshots };
        var (ok, _) = await TweakEngine.RollbackRecordAsync(generic);
        Assert.True(ok);
        Assert.True(generic.RolledBack);
        Assert.Null(Reg.Read(Hive.HKCU, k.Path, "A"));
    }

    [Fact]
    public async Task Admin_tweak_is_refused_without_elevation_and_reports_failure()
    {
        if (Elevation.IsAdmin) return;   // only meaningful in standard mode
        var t = TweakCatalog.All.First(x => x.RequiresAdmin && x.UnsupportedReason is null);
        var (ok, msg) = await TweakEngine.ApplyAsync(t);
        Assert.False(ok);
        Assert.Contains("administrator", msg);
    }

    [Fact]
    public async Task Unsupported_tweak_never_reports_success()
    {
        var t = new Tweak
        {
            Id = "test.unsupported", Name = "U", Category = TweakCategory.Windows, Description = "d", ExpectedEffect = "e", Explanation = "x",
            MinBuild = int.MaxValue, Ops = [RegOp.DWord(Hive.HKCU, ScratchKey.Root + @"\never", "A", 1, null)],
        };
        Assert.Equal(TweakState.Unsupported, t.GetState());
        var (ok, _) = await TweakEngine.ApplyAsync(t);
        Assert.False(ok);
        Assert.False(Reg.KeyExists(Hive.HKCU, ScratchKey.Root + @"\never"));
    }

    // ───────── Settings that were already on before VYTRIX

    private static Tweak DefaultOn(ScratchKey k) => new()
    {
        Id = "test.defaulton." + Guid.NewGuid().ToString("N"), Name = "Default-on", Category = TweakCategory.Windows,
        Description = "d", ExpectedEffect = "e", Explanation = "x",
        // Windows default (value absent) is already 1 = on; 0 = off.
        Ops = [RegOp.DWord(Hive.HKCU, k.Path, "Feature", 1, 1) with { OffValue = 0 }],
    };

    [Fact]
    public void Setting_on_by_windows_default_is_detected_as_on()
    {
        using var k = new ScratchKey();
        Reg.EnsureKey(Hive.HKCU, k.Path);   // value absent: Windows default = on
        var t = DefaultOn(k);
        Assert.Equal(TweakState.Applied, t.GetState());
        Assert.False(t.AppliedByVytrix);
        Assert.True(t.AlreadyOnInWindows);
    }

    [Fact]
    public void Setting_turned_on_by_the_user_is_detected_as_on()
    {
        using var k = new ScratchKey();
        Reg.Write(Hive.HKCU, k.Path, "Feature", 1, RegistryValueKind.DWord);
        Assert.True(DefaultOn(k).AlreadyOnInWindows);
    }

    [Fact]
    public async Task Turning_off_a_default_on_setting_really_turns_it_off()
    {
        using var k = new ScratchKey();
        Reg.EnsureKey(Hive.HKCU, k.Path);
        var t = DefaultOn(k);
        var (ok, msg) = await TweakEngine.RevertAsync(t);
        Assert.True(ok, msg);
        Assert.Equal(0, Reg.Read(Hive.HKCU, k.Path, "Feature"));   // explicit off, not "restore default (= on)"
        Assert.Equal(TweakState.NotApplied, t.GetState());
    }

    [Fact]
    public async Task Setting_on_through_another_route_is_detected_and_turned_off_there()
    {
        using var k = new ScratchKey();
        Reg.Write(Hive.HKCU, k.Path, "Alt", 0, RegistryValueKind.DWord);   // e.g. Windows Settings' own switch
        var t = new Tweak
        {
            Id = "test.alt." + Guid.NewGuid().ToString("N"), Name = "Alt", Category = TweakCategory.Windows, Description = "d", ExpectedEffect = "e", Explanation = "x",
            Ops = [RegOp.DWord(Hive.HKCU, k.Path, "Policy", 1, null)],
            AlsoOnWhen = () => Reg.Read(Hive.HKCU, k.Path, "Alt") is 0,
            AlsoOnSource = "test settings",
            TurnOffElsewhere = () => TweakEngine.SetElsewhere(Hive.HKCU, k.Path, "Alt", 1, "test", "alt on"),
        };
        Assert.Equal(TweakState.Applied, t.GetState());     // on, though VYTRIX's own value is absent
        Assert.True(t.AlreadyOnInWindows);
        var (ok, msg) = await TweakEngine.RevertAsync(t);
        Assert.True(ok, msg);
        Assert.Equal(1, Reg.Read(Hive.HKCU, k.Path, "Alt"));
        Assert.Equal(TweakState.NotApplied, t.GetState());
    }

    [Fact]
    public async Task Revert_never_claims_success_while_still_on_elsewhere()
    {
        using var k = new ScratchKey();
        Reg.Write(Hive.HKCU, k.Path, "Alt", 0, RegistryValueKind.DWord);
        var t = new Tweak
        {
            Id = "test.alt2." + Guid.NewGuid().ToString("N"), Name = "Alt2", Category = TweakCategory.Windows, Description = "d", ExpectedEffect = "e", Explanation = "x",
            Ops = [RegOp.DWord(Hive.HKCU, k.Path, "Policy", 1, null)],
            AlsoOnWhen = () => Reg.Read(Hive.HKCU, k.Path, "Alt") is 0,
            AlsoOnSource = "a policy VYTRIX does not change",
        };
        var (ok, msg) = await TweakEngine.RevertAsync(t);
        Assert.False(ok);
        Assert.Contains("still on", msg);
        Assert.Contains("a policy VYTRIX does not change", msg);
    }

    [Fact]
    public async Task Tweak_applied_by_vytrix_is_marked_as_such()
    {
        using var k = new ScratchKey();
        var t = Synthetic(k);
        await TweakEngine.ApplyAsync(t);
        Assert.True(t.AppliedByVytrix);
        Assert.False(t.AlreadyOnInWindows);
        await TweakEngine.RevertAsync(t);
    }

    [Fact]
    public void Journal_persists_to_disk_in_test_folder()
    {
        Journal.Add(new ChangeRecord { Kind = ChangeKind.Cleanup, Target = "test", Title = "persist", Reversible = false });
        Assert.StartsWith(TestEnvironment.DataDir, AppPaths.JournalFile);
        // Other tests may be replacing the file at this instant (atomic tmp+move), so retry briefly.
        string text = "";
        for (int i = 0; i < 20; i++)
        {
            try { text = System.IO.File.ReadAllText(AppPaths.JournalFile); break; }
            catch (System.IO.IOException) { Thread.Sleep(50); }
        }
        Assert.Contains("persist", text);
    }
}
