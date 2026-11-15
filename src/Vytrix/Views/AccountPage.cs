using System.Windows;
using System.Windows.Controls;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Services;

namespace Vytrix.Views;

/// <summary>Optional account: sign in several ways and sync your setup across PCs.</summary>
public sealed class AccountPage : PageBase
{
    public override string PageTitle => "Account";
    public override string PageSubtitle => "Optional. Sign in to keep your tweaks and look in sync across your PCs.";

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
        if (!AccountConfig.Configured) { RenderNotSetUp(); return; }
        if (Svc.SignedIn) await RenderSignedInAsync();
        else await RenderSignedOutAsync();
    }

    private void RenderNotSetUp()
    {
        var sp = new StackPanel();
        sp.Children.Add(Para("Accounts let you sign in with Google, Microsoft, GitHub or email, and keep your tweak setup, theme and settings the same on every PC."));
        sp.Children.Add(Para("They aren't switched on in this version of VYTRIX yet. Everything else works without an account, and you can still move your setup with **Settings → Move your setup to another PC**."));
        _root.Children.Add(Kit.Card(sp, "Accounts are coming", "Nothing to do for now."));
    }

    // ───────────────────────── Signed out

    private async Task RenderSignedOutAsync()
    {
        var card = new StackPanel();
        card.Children.Add(Para("Sign in on each PC to sync your tweak setup, Custom profile, theme and appearance settings. Nothing is applied on a PC until you choose to, and tweaks always go through the usual review."));
        var methodsHost = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        methodsHost.Children.Add(new Spinner { Width = 20, Height = 20, Stroke = Ui.Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left });
        card.Children.Add(methodsHost);
        _root.Children.Add(Kit.Card(card, "Sign in", "Choose how you'd like to sign in."));

        List<SignInMethod> methods; bool emailOn, signupOff;
        try { (methods, emailOn, signupOff) = await Svc.MethodsAsync(); }
        catch (Exception ex)
        {
            methodsHost.Children.Clear();
            methodsHost.Children.Add(Kit.Notice(ex is AccountException ? ex.Message : "Couldn't reach the account server.", "warn", Retry()));
            return;
        }
        methodsHost.Children.Clear();

        if (methods.Count > 0)
        {
            var wrap = new WrapPanel();
            foreach (var m in methods)
            {
                var b = new Button { Content = $"Continue with {m.Label}", Style = Ui.Style("Ghost"), MinWidth = 210, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(16, 9, 16, 9) };
                var id = m.Id;
                b.Click += async (_, _) => await RunAsync(b, async () =>
                {
                    Shell.Toast($"Finish signing in with {m.Label} in your browser.", ToastKind.Info);
                    await Svc.SignInWithProviderAsync(id);
                    Shell.Toast($"Signed in as {Svc.Session?.Email}.", ToastKind.Success);
                });
                wrap.Children.Add(b);
            }
            methodsHost.Children.Add(wrap);
        }

        if (emailOn)
        {
            if (methods.Count > 0) methodsHost.Children.Add(new TextBlock { Text = "OR USE EMAIL", Style = Ui.Style("Label"), Margin = new Thickness(0, 10, 0, 8) });
            var email = new TextBox { Tag = "Email", Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
            var pw = new PasswordBox { Width = 320, Height = 36, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Password (at least 8 characters)" };
            var err = new TextBlock { Foreground = Ui.Res("BadBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            methodsHost.Children.Add(email);
            methodsHost.Children.Add(new TextBlock { Text = "Password (at least 8 characters)", Style = Ui.Style("Muted"), FontSize = 11.5, Margin = new Thickness(2, 0, 0, 4) });
            methodsHost.Children.Add(pw);
            var signIn = new Button { Content = "Sign in", Style = Ui.Style("Primary") };
            var create = new Button { Content = "Create account", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0), Visibility = signupOff ? Visibility.Collapsed : Visibility.Visible };
            var forgot = new Button { Content = "Forgot password?", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
            var row = Kit.Row(signIn, create, forgot);
            row.Margin = new Thickness(0, 12, 0, 0);
            methodsHost.Children.Add(row);
            methodsHost.Children.Add(err);

            bool Valid(bool needPassword)
            {
                var e = email.Text.Trim();
                string? problem = !e.Contains('@') || e.Length < 5 ? "Enter your email address." : needPassword && pw.Password.Length < 8 ? "Use a password of at least 8 characters." : null;
                err.Text = problem ?? "";
                err.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
                return problem is null;
            }
            signIn.Click += async (_, _) => { if (Valid(true)) await RunAsync(signIn, () => Svc.SignInWithEmailAsync(email.Text, pw.Password), err); };
            create.Click += async (_, _) =>
            {
                if (!Valid(true)) return;
                await RunAsync(create, async () =>
                {
                    bool signedIn = await Svc.SignUpWithEmailAsync(email.Text, pw.Password);
                    Shell.Toast(signedIn ? "Account created. You're signed in." : "Check your email and open the confirmation link, then sign in here.", signedIn ? ToastKind.Success : ToastKind.Info);
                }, err);
            };
            forgot.Click += async (_, _) =>
            {
                if (!Valid(false)) return;
                await RunAsync(forgot, async () => { await Svc.SendPasswordResetAsync(email.Text); Shell.Toast("If that email has an account, a reset link is on its way.", ToastKind.Info); }, err);
            };
        }

        if (methods.Count == 0 && !emailOn)
            methodsHost.Children.Add(Kit.Notice("No sign-in methods are turned on for this account server yet.", "warn"));

        var priv = new StackPanel();
        priv.Children.Add(Para("Your account stores your email address (and your name and picture if your sign-in provider shares them), plus the setup you choose to sync: which tweaks are on, your Custom profile and your appearance settings. It never includes your files, passwords, computer name or the Assistant's conversations."));
        priv.Children.Add(Para("You can delete your synced data, or your whole account, at any time from this page. Details are in the Terms of Agreement, section 6."));
        var privCard = Kit.Card(priv, "What an account stores");
        privCard.Margin = new Thickness(0, 14, 0, 0);
        _root.Children.Add(privCard);
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
        signOut.Click += async (_, _) => await RunAsync(signOut, Svc.SignOutAsync);
        _root.Children.Add(Kit.Card(who, "Your account", null, signOut));

        var sync = new StackPanel();
        var status = new TextBlock { Style = Ui.Style("Dim"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Text = "Checking what's saved to your account…" };
        sync.Children.Add(status);
        var upload = Ui.IconButton("IcoExport", "Save this PC's setup to my account", "Primary");
        var load = Ui.IconButton("IcoRefresh", "Load my synced setup", "Ghost");
        load.Margin = new Thickness(8, 0, 0, 0);
        load.IsEnabled = false;
        sync.Children.Add(Kit.Row(upload, load));
        var syncCard = Kit.Card(sync, "Sync your setup", "Your tweaks, Custom profile, theme and appearance, the same on every PC you sign in on.");
        syncCard.Margin = new Thickness(0, 14, 0, 0);
        _root.Children.Add(syncCard);

        SyncedSetup? remote = null;
        try
        {
            remote = await Svc.DownloadAsync();
            status.Text = remote is null
                ? "Nothing is saved to your account yet. Save this PC's setup to start syncing."
                : $"Saved {remote.SavedAt.ToLocalTime():ddd d MMM, HH:mm} from a {remote.Device}: {remote.Tweaks.Tweaks.Count} tweaks, {remote.Appearance["theme"]?.GetValue<string>() ?? "default"} theme.";
            load.IsEnabled = remote is not null;
        }
        catch (Exception ex) { status.Text = ex is AccountException ? ex.Message : "Couldn't read your synced setup."; }

        upload.Click += async (_, _) =>
        {
            var local = AccountService.Capture();
            var body = $"This saves {local.Tweaks.Tweaks.Count} tweaks that are on now, your Custom profile's power plan and your appearance settings ({Settings.Current.Theme} theme) to your account"
                       + (remote is not null ? $", replacing what was saved {remote.SavedAt.ToLocalTime():d MMM, HH:mm}." : ".");
            if (!await Shell.ConfirmAsync("Save this PC's setup?", body, "Save to my account")) return;
            await RunAsync(upload, async () => { await Svc.UploadAsync(local); Shell.Toast("Saved to your account.", ToastKind.Success); });
        };
        load.Click += async (_, _) =>
        {
            if (remote is null) return;
            var imp = TweakSetup.FromFile(remote.Tweaks);
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = $"Saved {remote.SavedAt.ToLocalTime():ddd d MMM, HH:mm} from a {remote.Device}.", Style = Ui.Style("Dim"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = $"• Appearance ({remote.Appearance["theme"]?.GetValue<string>() ?? "theme"}, accent, motion and music volume) is applied now.\n• {imp.Usable.Count} tweaks go into your Custom profile. Nothing changes until you review it and click Apply.", TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
            if (imp.Unsupported.Count > 0) body.Children.Add(Kit.Notice($"Not available on this PC: {string.Join(", ", imp.Unsupported.Select(t => t.Name))}.", "warn"));
            if (imp.Unknown.Count > 0) body.Children.Add(Kit.Notice($"{imp.Unknown.Count} tweak(s) need a newer VYTRIX and will be skipped.", "warn"));
            if (!await Shell.ConfirmAsync("Load your synced setup?", body, "Load setup")) return;
            AccountService.ApplyAppearance(remote.Appearance);
            ThemeManager.ApplyTheme(Settings.Current.Theme);
            ThemeManager.Apply(Settings.Current.Accent);
            TweakSetup.LoadIntoCustom(imp);
            Shell.Toast("Appearance applied, and your tweaks are in the Custom profile. Review them, then click Apply.", ToastKind.Success);
            Shell.Navigate("optimization");
        };

        var danger = new StackPanel();
        danger.Children.Add(Para("Deleting your synced data removes the setup saved to your account; your account stays. Deleting your account removes your account and everything saved to it. Neither changes anything on this PC."));
        var delData = new Button { Content = "Delete my synced data", Style = Ui.Style("Ghost") };
        delData.Click += async (_, _) =>
        {
            if (!await Shell.ConfirmAsync("Delete your synced data?", "The setup saved to your account is removed for good. Your account and this PC's settings stay as they are.", "Delete synced data", danger: true)) return;
            await RunAsync(delData, async () => { await Svc.DeleteSyncedDataAsync(); Shell.Toast("Your synced data was deleted.", ToastKind.Success); });
        };
        var delAccount = new Button { Content = "Delete my account", Style = Ui.Style("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
        delAccount.Click += async (_, _) =>
        {
            if (!await Shell.ConfirmAsync("Delete your account?", $"Your account ({s.Email}) and everything saved to it are deleted for good. This can't be undone. Nothing on this PC changes.", "Delete my account", danger: true)) return;
            await RunAsync(delAccount, async () => { await Svc.DeleteAccountAsync(); Shell.Toast("Your account was deleted.", ToastKind.Success); });
        };
        danger.Children.Add(Kit.Row(delData, delAccount));
        var dangerCard = Kit.Card(danger, "Your data");
        dangerCard.Margin = new Thickness(0, 14, 0, 0);
        _root.Children.Add(dangerCard);
    }

    // ───────────────────────── Helpers

    private async Task RunAsync(Button b, Func<Task> work, TextBlock? errorText = null)
    {
        _busy = true;
        b.IsEnabled = false;
        try { await work(); if (errorText is not null) errorText.Visibility = Visibility.Collapsed; }
        catch (Exception ex)
        {
            var msg = ex is AccountException ? ex.Message : "Something went wrong. Try again.";
            Log.Warn("account", $"Account action failed: {ex.GetType().Name}");
            if (errorText is not null) { errorText.Text = msg; errorText.Visibility = Visibility.Visible; }
            else Shell.Toast(msg, ToastKind.Warning);
        }
        finally { b.IsEnabled = true; _busy = false; }
        await RenderAsync();
    }

    private Button Retry()
    {
        var b = new Button { Content = "Try again", Style = Ui.Style("Ghost") };
        b.Click += async (_, _) => await RenderAsync();
        return b;
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
