using System.Text.Json.Nodes;
using System.Windows.Threading;
using Vytrix.Core;

namespace Vytrix.Services;

/// <summary>
/// Keeps the account's synced setup current without anyone clicking Save:
/// <list type="bullet">
/// <item>When VYTRIX opens, a setup saved more recently from another PC is offered (never applied silently).</item>
/// <item>After that, any change on this PC (a tweak applied or undone, the theme or appearance changed)
/// is saved to the account a few seconds later. The most recently changed PC wins.</item>
/// </list>
/// Nothing is uploaded until the start-up check has run, so a newer setup from another PC is
/// never overwritten before it has been offered.
/// </summary>
public sealed class AutoSync
{
    public static AutoSync Instance { get; } = new();

    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

    private Dispatcher? _dispatcher;
    private DispatcherTimer? _debounce, _retry;
    private Func<SyncedSetup, Task<bool>>? _offerLoad;
    private bool _ready, _busy, _suppress, _subscribed;

    /// <summary>Raised after a successful automatic save or check, for the Account page's status line.</summary>
    public event Action? StatusChanged;
    public DateTime? LastSync => Settings.Current.LastSyncedUser == AccountService.Instance.Session?.UserId ? Settings.Current.LastSyncedAt : null;
    public string? LastProblem { get; private set; }

    /// <summary>
    /// Starts automatic sync for the signed-in account. <paramref name="offerLoad"/> asks the person whether to load
    /// a newer setup from another PC, loads it if they agree, and returns whether it was loaded.
    /// </summary>
    public async Task StartAsync(Dispatcher dispatcher, Func<SyncedSetup, Task<bool>> offerLoad)
    {
        _dispatcher = dispatcher;
        _offerLoad = offerLoad;
        _ready = false;
        if (!_subscribed)
        {
            _subscribed = true;
            Journal.Changed += Schedule;
            Settings.Changed += Schedule;
        }
        await CheckAtStartAsync();
    }

    /// <summary>Use while applying a loaded setup, so loading doesn't immediately save it back.</summary>
    public IDisposable Suppress() { _suppress = true; return new Scope(() => _suppress = false); }

    /// <summary>
    /// Records a sync point: the account's setup as of <paramref name="setup"/>.SavedAt, and this PC's own state at that
    /// moment (<paramref name="fingerprint"/>). Only changes to this PC after that point are uploaded.
    /// </summary>
    public void MarkSynced(SyncedSetup setup, string? fingerprint = null)
    {
        var s = Settings.Current;
        s.LastSyncedUser = AccountService.Instance.Session?.UserId ?? "";
        s.LastSyncedAt = setup.SavedAt;
        if (fingerprint is not null) s.LastSyncedFingerprint = fingerprint;
        using (Suppress()) Settings.Save();
        LastProblem = null;
        StatusChanged?.Invoke();
    }

    /// <summary>What a setup amounts to, ignoring when and where it was saved.</summary>
    public static string Fingerprint(SyncedSetup s)
    {
        var tweaks = string.Join(",", s.Tweaks.Tweaks.Order(StringComparer.Ordinal));
        var appearance = string.Join(";", s.Appearance.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={Normal(kv.Value)}"));
        return $"{tweaks}|{s.Tweaks.PowerPlan}|{appearance}";
    }

    private static string Normal(JsonNode? v) => v switch
    {
        null => "",
        JsonValue jv when jv.TryGetValue<double>(out var d) => d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToJsonString(),
    };

    // ───────────────────────── Start-up check

    private async Task CheckAtStartAsync()
    {
        var svc = AccountService.Instance;
        if (!svc.SignedIn || !Settings.Current.AutoSync || _busy) return;
        _busy = true;
        try
        {
            var remote = await svc.DownloadAsync();
            var local = await Task.Run(AccountService.Capture);
            var localFp = Fingerprint(local);
            bool sameAccount = Settings.Current.LastSyncedUser == svc.Session?.UserId;

            if (remote is null)
            {
                await svc.UploadAsync(local);            // the first PC on this account
                MarkSynced(local, localFp);
                Log.Info("sync", "Nothing saved to the account yet; saved this PC's setup.");
            }
            else if (Fingerprint(remote) == localFp)
            {
                MarkSynced(remote, localFp);             // already the same
            }
            else if (!sameAccount || Settings.Current.LastSyncedAt is not { } seen || remote.SavedAt > seen.AddSeconds(1))
            {
                // Saved from another PC since this one last synced: offer it.
                Log.Info("sync", $"A newer setup from {remote.Device} is on the account; offering it.");
                bool loaded = _offerLoad is not null && await _offerLoad(remote);   // loading records its own baseline
                if (!loaded)
                {
                    MarkSynced(remote, localFp);
                    Log.Info("sync", "Kept this PC's setup; it will be saved to the account when it next changes.");
                }
            }
            else if (localFp != Settings.Current.LastSyncedFingerprint)
            {
                // This PC changed since its last sync (while VYTRIX was closed or offline): save it now.
                await svc.UploadAsync(local);
                MarkSynced(local, localFp);
                Log.Info("sync", "Saved this PC's newer setup to the account.");
            }
            // Otherwise nothing changed here since the last sync (e.g. loaded tweaks still waiting for review).
            _ready = true;
        }
        catch (Exception ex)
        {
            LastProblem = ex is AccountException ? ex.Message : "Couldn't sync right now.";
            Log.Info("sync", $"Start-up sync check failed ({LastProblem}); retrying in {RetryAfterFailure.TotalMinutes:0} minutes.");
            StatusChanged?.Invoke();
            ScheduleRetry();
        }
        finally { _busy = false; }
    }

    private void ScheduleRetry()
    {
        if (_dispatcher is null) return;
        _retry?.Stop();
        _retry = new DispatcherTimer(RetryAfterFailure, DispatcherPriority.Background, async (_, _) => { _retry?.Stop(); await CheckAtStartAsync(); }, _dispatcher);
        _retry.Start();
    }

    // ───────────────────────── Save on change

    private void Schedule()
    {
        if (_suppress || !_ready || _dispatcher is null || !Settings.Current.AutoSync) return;
        _dispatcher.BeginInvoke(() =>
        {
            _debounce ??= new DispatcherTimer(Debounce, DispatcherPriority.Background, async (_, _) => { _debounce!.Stop(); await SaveIfChangedAsync(); }, _dispatcher);
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private async Task SaveIfChangedAsync()
    {
        var svc = AccountService.Instance;
        if (_busy || !_ready || !svc.SignedIn || !Settings.Current.AutoSync) return;
        _busy = true;
        try
        {
            var local = await Task.Run(AccountService.Capture);
            var fp = Fingerprint(local);
            if (fp == Settings.Current.LastSyncedFingerprint && Settings.Current.LastSyncedUser == svc.Session?.UserId) return;
            await svc.UploadAsync(local);
            MarkSynced(local, fp);
            Log.Info("sync", "Saved this PC's setup to the account automatically.");
        }
        catch (Exception ex)
        {
            LastProblem = ex is AccountException ? ex.Message : "Couldn't sync right now.";
            Log.Info("sync", $"Automatic save failed: {LastProblem}");
            StatusChanged?.Invoke();
        }
        finally { _busy = false; }
    }

    private sealed class Scope(Action end) : IDisposable { public void Dispose() => end(); }
}
