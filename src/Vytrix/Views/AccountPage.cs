using System.Windows;
using System.Windows.Controls;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Services;

namespace Vytrix.Views;

/// <summary>The signed-in account: sync your setup across PCs, sign out, or delete your data.</summary>
public sealed class AccountPage : PageBase
{
    public override string PageTitle => "Account";
    public override string PageSubtitle => "Your VYTRIX account keeps your tweaks and look in sync across your PCs.";

    private readonly StackPanel _root = new();
    private AccountService Svc => AccountService.Instance;
    private bool _busy;

    protected override void Build()
    {
        Body.Children.Add(_root);
        Svc.Changed += () => Dispatcher.BeginInvoke(() => _ = RenderAsync());
    }

    public override void OnShown() => _ = RenderAsync();

    private async Task RenderAsync()
    {
        if (_busy) return;
        _root.Children.Clear();
        // Signing out returns VYTRIX to its sign-in screen, so this page only has a signed-in view.
        if (Svc.SignedIn) await RenderSignedInAsync();
    }

    // ───────────────────────── Signed in

    private async Task RenderSignedInAsync()
    {
        var s = Svc.Session!;
        var who = new StackPanel();
        var provider = AccountService.KnownProviders.FirstOrDefault(p => p.Id == s.Provider).Label ?? (s.Provider == "email" ? "email" : s.Provider);
        who.Children.Add(new TextBlock { Text = s.Email.Length > 0 ? s.Email : "Signed in", Style = Ui.Style("H2") });
        who.Children.Add(new TextBlock { Text = $"Signed in with {provider}", Style = Ui.Style("Muted"), Margin = new Thickness(0, 3, 0, 0) });
        var signOut = new Button { Content = "Sign out", Style = Ui.Style("Ghost") };
        signOut.Click += async (_, _) =>
        {
            if (!await Shell.ConfirmAsync("Sign out of VYTRIX?", "VYTRIX goes back to its sign-in screen. Tweaks you've applied stay on this PC.", "Sign out")) return;
            await RunAsync(signOut, Svc.SignOutAsync);
        };
        _root.Children.Add(Kit.Card(who, "Your account", null, signOut));

        var sync = new StackPanel();
        var autoRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        autoRow.ColumnDefinitions.Add(new ColumnDefinition());
        autoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var autoText = new StackPanel();
        autoText.Children.Add(new TextBlock { Text = "Sync automatically", FontWeight = FontWeights.SemiBold, FontSize = 13 });
        autoText.Children.Add(new TextBlock { Text = "Saves this PC's setup to your account whenever it changes, and offers a newer setup from your other PCs when VYTRIX opens. Tweaks from another PC always wait in the Custom profile for you to review.", Style = Ui.Style("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 16, 0) });
        autoRow.Children.Add(autoText);
        var autoSwitch = Kit.Switch(Settings.Current.AutoSync, "Sync automatically");
        Grid.SetColumn(autoSwitch, 1);
        autoSwitch.VerticalAlignment = VerticalAlignment.Top;
        autoRow.Children.Add(autoSwitch);
        sync.Children.Add(autoRow);

        var status = new TextBlock { Style = Ui.Style("Dim"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Text = "Checking what's saved to your account…" };
        sync.Children.Add(status);
        var upload = Ui.IconButton("IcoExport", "Save this PC's setup now", "Primary");
        var load = Ui.IconButton("IcoRefresh", "Load my synced setup", "Ghost");
        load.Margin = new Thickness(8, 0, 0, 0);
        load.IsEnabled = false;
        sync.Children.Add(Kit.Row(upload, load));
        var syncCard = Kit.Card(sync, "Sync your setup", "Your tweaks, Custom profile, theme and appearance, the same on every PC you sign in on.");
        syncCard.Margin = new Thickness(0, 14, 0, 0);
        _root.Children.Add(syncCard);

        autoSwitch.Click += async (_, _) =>
        {
            Settings.Current.AutoSync = autoSwitch.IsChecked == true;
            Settings.Save();
            Log.Info("sync", $"Automatic sync turned {(Settings.Current.AutoSync ? "on" : "off")}.");
            if (Settings.Current.AutoSync) { await Shell.StartAutoSyncAsync(); await RenderAsync(); }
        };

        SyncedSetup? remote = null;
        try
        {
            remote = await Svc.DownloadAsync();
            var last = AutoSync.Instance.LastSync;
            status.Text = remote is null
                ? "Nothing is saved to your account yet." + (Settings.Current.AutoSync ? " This PC's setup is saved the next time it changes." : " Save this PC's setup to start syncing.")
                : $"Saved {remote.SavedAt.ToLocalTime():ddd d MMM, HH:mm} from a {remote.Device}: {remote.Tweaks.Tweaks.Count} tweaks, {remote.Appearance["theme"]?.GetValue<string>() ?? "default"} theme."
                  + (Settings.Current.AutoSync && last is { } l && l >= remote.SavedAt.AddSeconds(-1) ? " This PC is in sync." : "");
            if (AutoSync.Instance.LastProblem is { } p && Settings.Current.AutoSync) status.Text += $" Last automatic sync didn't work: {p}";
            load.IsEnabled = remote is not null;
        }
        catch (Exception ex) { status.Text = ex is AccountException ? ex.Message : "Couldn't read your synced setup."; }

        upload.Click += async (_, _) =>
        {
            var local = AccountService.Capture();
            var body = $"This saves {local.Tweaks.Tweaks.Count} tweaks that are on now, your Custom profile's power plan and your appearance settings ({Settings.Current.Theme} theme) to your account"
                       + (remote is not null ? $", replacing what was saved {remote.SavedAt.ToLocalTime():d MMM, HH:mm}." : ".");
            if (!await Shell.ConfirmAsync("Save this PC's setup?", body, "Save to my account")) return;
            await RunAsync(upload, async () =>
            {
                await Svc.UploadAsync(local);
                AutoSync.Instance.MarkSynced(local, AutoSync.Fingerprint(local));
                Shell.Toast("Saved to your account.", ToastKind.Success);
            });
        };
        load.Click += async (_, _) =>
        {
            if (remote is null) return;
            if (await OfferLoadAsync(Shell, remote, automatic: false)) await RenderAsync();
        };

        var danger = new StackPanel();
        danger.Children.Add(Para("Deleting your synced data removes the setup saved to your account; your account stays. Deleting your account removes your account and everything saved to it. Neither changes anything on this PC."));
        var delData = new Button { Content = "Delete my synced data", Style = Ui.Style("Ghost") };
        delData.Click += async (_, _) =>
        {
            if (!await Shell.ConfirmAsync("Delete your synced data?", "The setup saved to your account is removed for good, and automatic sync is turned off so it isn't saved again. Your account and this PC's settings stay as they are.", "Delete synced data", danger: true)) return;
            await RunAsync(delData, async () =>
            {
                await Svc.DeleteSyncedDataAsync();
                Settings.Current.AutoSync = false;
                Settings.Current.LastSyncedFingerprint = "";
                Settings.Current.LastSyncedAt = null;
                Settings.Save();
                Shell.Toast("Your synced data was deleted, and automatic sync is off.", ToastKind.Success);
            });
        };
        var delAccount = new Button { Content = "Delete my account", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
        delAccount.Click += async (_, _) =>
        {
            if (!await Shell.ConfirmAsync("Delete your account?", $"Your account ({s.Email}) and everything saved to it are deleted for good. This can't be undone. Nothing on this PC changes.", "Delete my account", danger: true)) return;
            await RunAsync(delAccount, () => Svc.DeleteAccountAsync());   // VYTRIX then returns to its sign-in screen
        };
        danger.Children.Add(Kit.Row(delData, delAccount));
        var dangerCard = Kit.Card(danger, "Your data");
        dangerCard.Margin = new Thickness(0, 14, 0, 0);
        _root.Children.Add(dangerCard);
    }

    // ───────────────────────── Helpers

    /// <summary>
    /// Shows what loading a synced setup does and loads it if the person agrees: appearance is applied,
    /// tweaks go into the Custom profile for review. Used by the Load button and by automatic sync.
    /// </summary>
    internal static async Task<bool> OfferLoadAsync(MainWindow shell, SyncedSetup remote, bool automatic)
    {
        var imp = TweakSetup.FromFile(remote.Tweaks);
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = automatic
                ? $"Your account has a newer setup, saved {remote.SavedAt.ToLocalTime():ddd d MMM, HH:mm} from a {remote.Device}."
                : $"Saved {remote.SavedAt.ToLocalTime():ddd d MMM, HH:mm} from a {remote.Device}.",
            Style = Ui.Style("Dim"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock { Text = $"• Appearance ({remote.Appearance["theme"]?.GetValue<string>() ?? "theme"}, accent, motion and music volume) is applied now.\n• {imp.Usable.Count} tweaks go into your Custom profile. Nothing changes until you review it and click Apply.", TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
        if (imp.Unsupported.Count > 0) body.Children.Add(Kit.Notice($"Not available on this PC: {string.Join(", ", imp.Unsupported.Select(t => t.Name))}.", "warn"));
        if (imp.Unknown.Count > 0) body.Children.Add(Kit.Notice($"{imp.Unknown.Count} tweak(s) need a newer VYTRIX and will be skipped.", "warn"));
        if (automatic) body.Children.Add(new TextBlock { Text = "If you keep this PC's setup instead, it's saved to your account the next time you change something here.", Style = Ui.Style("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        if (!await shell.ConfirmAsync(automatic ? "Load your setup from your other PC?" : "Load your synced setup?", body, "Load setup", cancelText: automatic ? "Keep this PC's" : "Cancel")) return false;
        using (AutoSync.Instance.Suppress())
        {
            AccountService.ApplyAppearance(remote.Appearance);
            ThemeManager.ApplyTheme(Settings.Current.Theme);
            ThemeManager.Apply(Settings.Current.Accent);
            TweakSetup.LoadIntoCustom(imp);
        }
        // The baseline is this PC as it is now (the tweaks still wait for review), so only later changes here are uploaded.
        AutoSync.Instance.MarkSynced(remote, AutoSync.Fingerprint(AccountService.Capture()));
        Log.Info("sync", $"Loaded the synced setup from {remote.Device} ({imp.Usable.Count} tweaks into the Custom profile).");
        shell.Toast("Appearance applied, and your tweaks are in the Custom profile. Review them, then click Apply.", ToastKind.Success);
        shell.Navigate("optimization");
        return true;
    }

    private async Task RunAsync(Button b, Func<Task> work, TextBlock? errorText = null)
    {
        _busy = true;
        b.IsEnabled = false;
        try { await work(); if (errorText is not null) errorText.Visibility = Visibility.Collapsed; }
        catch (Exception ex)
        {
            var msg = ex is AccountException ? ex.Message : "Something went wrong. Try again.";
            Log.Warn("account", $"Account action failed: {(ex is AccountException ? ex.Message : ex.GetType().Name)}");
            if (errorText is not null) { errorText.Text = msg; errorText.Visibility = Visibility.Visible; }
            else Shell.Toast(msg, ToastKind.Warning);
        }
        finally { b.IsEnabled = true; _busy = false; }
        await RenderAsync();
    }

    private static TextBlock Para(string text)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20, Margin = new Thickness(0, 0, 0, 10), Foreground = Ui.Res("TextDimBrush") };
        var parts = text.Split("**");
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) tb.Inlines.Add(new System.Windows.Documents.Run(parts[i]) { FontWeight = i % 2 == 1 ? FontWeights.SemiBold : FontWeights.Normal });
        return tb;
    }
}
