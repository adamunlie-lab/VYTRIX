using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Services;

namespace Vytrix;

/// <summary>
/// The sign-in screen. VYTRIX opens here until the person is signed in to their VYTRIX account;
/// the main window only opens after a successful sign-in (or with a saved session).
/// </summary>
public sealed class SignInWindow : Window
{
    private enum Mode { SignIn, Create, Forgot, ResetSent, ResetCode, CheckEmail, SignUpCode }

    private static AccountService Svc => AccountService.Instance;

    private readonly StackPanel _form = new() { Width = 340, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _sceneHost = new() { IsHitTestVisible = false, ClipToBounds = true };
    private Action<bool>? _setSceneRunning;
    private Mode _mode = Mode.SignIn;
    private string _email = "";
    private string _code = "";
    private string? _message;
    private bool _messageIsError;
    private bool _busy;

    // Sign-in methods from the account server, loaded once.
    private List<SignInMethod>? _providers;
    private bool _emailOn = true, _signupOff;
    private string? _serverProblem;

    /// <summary>Raised once the person is signed in.</summary>
    public event Action? SignedIn;

    public SignInWindow()
    {
        Title = "Sign in - VYTRIX";
        Width = 960; Height = 620; MinWidth = 760; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Res("BgBrush");
        Foreground = Ui.Res("TextBrush");
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/vytrix.ico")); } catch { }
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 46, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(BuildBrandPanel());

        var right = new Grid();
        Grid.SetColumn(right, 1);
        right.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _form, Margin = new Thickness(0, 46, 0, 0), Focusable = false });
        right.Children.Add(ChromeButtons());
        root.Children.Add(right);
        if (ThemeManager.Current == ThemeManager.Christmas && Settings.Current.ShowChristmasLights)
        {
            _lights = new ChristmasLights();
            Grid.SetColumnSpan(_lights, 2);
            root.Children.Add(_lights);
        }
        StateChanged += (_, _) => root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        Content = root;

        Activated += (_, _) => UpdateScene();
        Deactivated += (_, _) => UpdateScene();
        StateChanged += (_, _) => UpdateScene();
        Loaded += async (_, _) => { Render(); UpdateScene(); await LoadMethodsAsync(); };
        Closed += (_, _) => _setSceneRunning?.Invoke(false);
    }

    // ───────────────────────── Layout

    private Border BuildBrandPanel()
    {
        var panel = new Grid();
        panel.Children.Add(_sceneHost);
        AddScene();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(30, 26, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        top.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = Ui.Res("Surface2Brush"), BorderBrush = Ui.Res("BorderStrongBrush"), BorderThickness = new Thickness(1),
            Child = new System.Windows.Shapes.Path { Data = Ui.Icon("LogoMark"), Fill = (Brush)Application.Current.Resources["LogoBrush"], Stretch = Stretch.Uniform, Width = 21, Height = 19, Margin = new Thickness(0, 2, 0, 0) },
        });
        var name = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "VYTRIX", FontFamily = (FontFamily)Application.Current.Resources["NumberFont"], FontSize = 20, FontWeight = FontWeights.SemiBold });
        name.Children.Add(new TextBlock { Text = "Precision Performance", FontSize = 11, Foreground = Ui.Res("TextMutedBrush") });
        top.Children.Add(name);
        panel.Children.Add(top);

        var pitch = new StackPanel { Margin = new Thickness(30, 0, 30, 30), VerticalAlignment = VerticalAlignment.Bottom };
        pitch.Children.Add(new TextBlock { Text = "Your PC, tuned and reversible.", FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        pitch.Children.Add(new TextBlock
        {
            Text = "Sign in to your VYTRIX account to start. Your account keeps your tweaks, profile and look in sync on every PC you use.",
            FontSize = 12.5, LineHeight = 19, Foreground = Ui.Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        panel.Children.Add(pitch);

        return new Border { Background = Ui.Res("SidebarBrush"), BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(0, 0, 1, 0), Child = panel };
    }

    /// <summary>The active theme's scenery behind the brand panel; it animates only while this window is active.</summary>
    private void AddScene()
    {
        var theme = ThemeManager.Current;
        FrameworkElement? scene = null;
        double w = 0, h = 0;
        if (theme == ThemeManager.Christmas) { var t = new ChristmasTree(); scene = t; w = ChristmasTree.DesignWidth; h = ChristmasTree.DesignHeight; _setSceneRunning = t.SetRunning; }
        else if (theme == ThemeManager.CherryBlossom) { var t = new SakuraTree(); scene = t; w = SakuraTree.DesignWidth; h = SakuraTree.DesignHeight; _setSceneRunning = t.SetRunning; }
        else if (theme == ThemeManager.NewYear) { var t = new FireworksSky(); t.ShowStill(); scene = t; w = FireworksSky.DesignWidth; h = FireworksSky.DesignHeight; _setSceneRunning = t.SetRunning; }
        if (scene is null) return;
        _sceneHost.Children.Add(new Viewbox
        {
            Child = scene, Stretch = Stretch.Uniform, Width = 420, Height = 420 * h / w, Opacity = 0.55,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -40, 70),
        });
    }

    private ChristmasLights? _lights;

    private void UpdateScene()
    {
        bool animate = IsActive && WindowState != WindowState.Minimized && !Settings.Current.ReduceMotion;
        _setSceneRunning?.Invoke(animate);
        _lights?.SetRunning(animate);
    }
    private StackPanel ChromeButtons()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        Button B(string icon, double size, string tip, Action click)
        {
            var b = new Button { Style = Ui.Style("ChromeButton"), ToolTip = tip, Content = new System.Windows.Shapes.Path { Data = Ui.Icon(icon), Style = Ui.Style("Icon"), Width = size, Height = size } };
            b.Click += (_, _) => click();
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            return b;
        }
        sp.Children.Add(B("IcoMin", 11, "Minimise", () => WindowState = WindowState.Minimized));
        sp.Children.Add(B("IcoClose", 11, "Close", Close));
        return sp;
    }

    // ───────────────────────── Form

    private async Task LoadMethodsAsync()
    {
        _serverProblem = null;
        try
        {
            (_providers, _emailOn, _signupOff) = await Svc.MethodsAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("account", $"Couldn't load sign-in methods: {ex.GetType().Name}");
            _providers = [];
            _serverProblem = "VYTRIX couldn't reach the account server. Check your internet connection, then try again.";
        }
        Render();
    }

    private void Render()
    {
        _form.Children.Clear();
        switch (_mode)
        {
            case Mode.CheckEmail: RenderCheckEmail(); return;
            case Mode.Forgot: RenderForgot(); return;
            case Mode.ResetSent: RenderResetSent(); return;
            case Mode.ResetCode: RenderResetCode(); return;
            case Mode.SignUpCode: RenderSignUpCode(); return;
        }
        bool create = _mode == Mode.Create;
        _form.Children.Add(Heading(create ? "Create your account" : "Sign in to VYTRIX",
            create ? "One account for every PC you use VYTRIX on." : "Welcome back. Sign in to continue."));

        if (_providers is null)
        {
            _form.Children.Add(new Spinner { Width = 22, Height = 22, Stroke = Ui.Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) });
            return;
        }
        if (_serverProblem is not null)
        {
            var retry = new Button { Content = "Try again", Style = Ui.Style("Ghost") };
            retry.Click += async (_, _) => { _providers = null; Render(); await LoadMethodsAsync(); };
            _form.Children.Add(Kit.Notice(_serverProblem, "warn", retry));
            return;
        }

        foreach (var m in _providers)
        {
            var b = new Button { Content = $"Continue with {m.Label}", Style = Ui.Style("Ghost"), HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 0, 8) };
            var id = m.Id;
            b.Click += async (_, _) =>
            {
                ShowMessage($"Finish signing in with {m.Label} in your browser.", false);
                await RunAsync(b, () => Svc.SignInWithProviderAsync(id));
            };
            _form.Children.Add(b);
        }

        if (!_emailOn)
        {
            if (_providers.Count == 0) _form.Children.Add(Kit.Notice("No sign-in methods are turned on for the account server yet.", "warn"));
            AddMessage();
            return;
        }
        if (_providers.Count > 0) _form.Children.Add(new TextBlock { Text = "OR USE EMAIL", Style = Ui.Style("Label"), Margin = new Thickness(0, 8, 0, 10), HorizontalAlignment = HorizontalAlignment.Center });

        var email = Field("Email address", new TextBox { Text = _email, Tag = "you@example.com", Height = 38 });
        var pw = new PasswordBox { Height = 38 };
        var pwField = Field(create ? "Password (at least 8 characters)" : "Password", pw);
        PasswordBox? pw2 = create ? new PasswordBox { Height = 38 } : null;

        _form.Children.Add(email.Host);
        _form.Children.Add(pwField.Host);
        if (pw2 is not null) _form.Children.Add(Field("Confirm password", pw2).Host);

        if (!create)
        {
            var forgot = new Button { Content = "Forgot password?", Style = Ui.Style("Link"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -4, 0, 4) };
            forgot.Click += (_, _) => { _email = ((TextBox)email.Input).Text.Trim(); _message = null; _mode = Mode.Forgot; Render(); };
            _form.Children.Add(forgot);
        }

        var submit = new Button { Content = create ? "Create account" : "Sign in", Style = Ui.Style("Primary"), HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 10, 0, 0), IsDefault = true };
        _form.Children.Add(submit);
        AddMessage();

        submit.Click += async (_, _) =>
        {
            var address = ((TextBox)email.Input).Text.Trim();
            _email = address;
            string? problem = !LooksLikeEmail(address) ? "Enter your email address."
                : pw.Password.Length == 0 ? "Enter your password."
                : create && pw.Password.Length < 8 ? "Use a password of at least 8 characters."
                : create && pw2!.Password != pw.Password ? "The passwords don't match."
                : null;
            if (problem is not null) { ShowMessage(problem, true); return; }
            if (create)
                await RunAsync(submit, async () =>
                {
                    bool signedIn = await Svc.SignUpWithEmailAsync(address, pw.Password);
                    if (!signedIn) { _mode = Mode.CheckEmail; _message = null; }
                });
            else
                await RunAsync(submit, () => Svc.SignInWithEmailAsync(address, pw.Password));
        };

        if (!(create && _signupOff))
        {
            var swap = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
            swap.Children.Add(new TextBlock { Text = create ? "Already have an account?" : "New to VYTRIX?", Foreground = Ui.Res("TextDimBrush"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
            if (create || !_signupOff)
            {
                var link = new Button { Content = create ? "Sign in" : "Create an account", Style = Ui.Style("Link"), Margin = new Thickness(6, 0, 0, 0) };
                link.Click += (_, _) => { _email = ((TextBox)email.Input).Text.Trim(); _message = null; _mode = create ? Mode.SignIn : Mode.Create; Render(); };
                swap.Children.Add(link);
            }
            _form.Children.Add(swap);
        }

        if (create)
            _form.Children.Add(TermsLine("By creating an account you agree to the "));

        Dispatcher.BeginInvoke(() => { if (((TextBox)email.Input).Text.Length == 0) email.Input.Focus(); else pw.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void RenderForgot()
    {
        _form.Children.Add(Heading("Reset your password", "We'll email you a link to choose a new password."));
        var email = Field("Email address", new TextBox { Text = _email, Tag = "you@example.com", Height = 38 });
        _form.Children.Add(email.Host);
        var send = PrimaryButton("Email me a reset link");
        _form.Children.Add(send);
        AddMessage();
        send.Click += async (_, _) =>
        {
            var address = ((TextBox)email.Input).Text.Trim();
            _email = address;
            if (!LooksLikeEmail(address)) { ShowMessage("Enter your email address.", true); return; }
            await RunAsync(send, async () =>
            {
                await Svc.SendPasswordResetAsync(address);
                _mode = Mode.ResetSent;
                _message = null;
            });
        };
        _form.Children.Add(BackLink());
        Dispatcher.BeginInvoke(() => email.Input.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>The reset email is on its way: its link opens a page to choose a new password.</summary>
    private void RenderResetSent()
    {
        _form.Children.Add(Heading("Check your email", null));
        _form.Children.Add(Para($"If {_email} has a VYTRIX account, we've emailed it a reset link. Open the link, choose a new password on the page that opens, then sign in here with it."));
        _form.Children.Add(Para("The link works once and expires after an hour. It can take a minute to arrive, so check your spam folder too."));
        var back = PrimaryButton("Back to sign in");
        back.Click += (_, _) => { _message = null; _mode = Mode.SignIn; Render(); };
        _form.Children.Add(back);
        AddMessage();
        _form.Children.Add(SmallLink("Send the link again", async b => await RunAsync(b, async () =>
        {
            await Svc.SendPasswordResetAsync(_email);
            _message = "Sent again. Use the link in the newest email.";
            _messageIsError = false;
        }, keepMessage: true)));
        _form.Children.Add(SmallLink("Got a code instead?", _ => { _message = null; _code = ""; _mode = Mode.ResetCode; Render(); return Task.CompletedTask; }));
    }

    /// <summary>Type a reset code from the email and choose a new password; signs in when it works.</summary>
    private void RenderResetCode()
    {
        _form.Children.Add(Heading("Choose a new password", $"Type the code from the email we sent to {_email}, then choose a new password."));
        var code = CodeBox();
        _form.Children.Add(Field("Code from the email", code).Host);
        var pw = new PasswordBox { Height = 38 };
        var pw2 = new PasswordBox { Height = 38 };
        _form.Children.Add(Field("New password (at least 8 characters)", pw).Host);
        _form.Children.Add(Field("Confirm new password", pw2).Host);
        var save = PrimaryButton("Save password and sign in");
        _form.Children.Add(save);
        AddMessage();
        save.Click += async (_, _) =>
        {
            _code = code.Text;
            string? problem = code.Text.Count(char.IsDigit) < 6 ? "Enter the code from the email."
                : pw.Password.Length < 8 ? "Use a password of at least 8 characters."
                : pw2.Password != pw.Password ? "The passwords don't match."
                : null;
            if (problem is not null) { ShowMessage(problem, true); return; }
            await RunAsync(save, () => Svc.ResetPasswordWithCodeAsync(_email, code.Text, pw.Password));
        };
        _form.Children.Add(BackLink());
        Dispatcher.BeginInvoke(() => code.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>After creating an account: confirm it with the link in the email, then sign in.</summary>
    private void RenderCheckEmail()
    {
        _form.Children.Add(Heading("Check your email", null));
        _form.Children.Add(Para($"We sent a confirmation link to {_email}. Open it to confirm your account, then come back and sign in."));
        _form.Children.Add(Para("It can take a minute to arrive. Check your spam folder if you don't see it."));
        var ok = PrimaryButton("I've confirmed my email, sign in");
        ok.Click += (_, _) => { _mode = Mode.SignIn; _message = null; Render(); };
        _form.Children.Add(ok);
        AddMessage();
        _form.Children.Add(SmallLink("Send the email again", async b => await RunAsync(b, async () =>
        {
            await Svc.ResendSignUpCodeAsync(_email);
            _message = "Sent again. Use the link in the newest email.";
            _messageIsError = false;
        }, keepMessage: true)));
        _form.Children.Add(SmallLink("Got a code instead?", _ => { _message = null; _code = ""; _mode = Mode.SignUpCode; Render(); return Task.CompletedTask; }));
    }

    /// <summary>Type a confirmation code from the sign-up email; signs in when it works.</summary>
    private void RenderSignUpCode()
    {
        _form.Children.Add(Heading("Enter your code", $"Type the code from the email we sent to {_email}."));
        var code = CodeBox();
        _form.Children.Add(Field("Code from the email", code).Host);
        var ok = PrimaryButton("Confirm and sign in");
        _form.Children.Add(ok);
        AddMessage();
        ok.Click += async (_, _) =>
        {
            _code = code.Text;
            if (code.Text.Count(char.IsDigit) < 6) { ShowMessage("Enter the code from the email.", true); return; }
            await RunAsync(ok, () => Svc.ConfirmSignUpAsync(_email, code.Text));
        };
        _form.Children.Add(BackLink());
        Dispatcher.BeginInvoke(() => code.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static Button SmallLink(string text, Func<Button, Task> click)
    {
        var b = new Button { Content = text, Style = Ui.Style("Link"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        b.Click += async (_, _) => await click(b);
        return b;
    }

    private TextBox CodeBox() => new()
    {
        Text = _code, Tag = "123456", Height = 44, MaxLength = 12, FontSize = 20, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    private static Button PrimaryButton(string text) => new()
    {
        Content = text, Style = Ui.Style("Primary"), HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 10, 0, 0), IsDefault = true,
    };

    // ───────────────────────── Helpers

    private async Task RunAsync(Button b, Func<Task> work, bool keepMessage = false)
    {
        if (_busy) return;
        _busy = true;
        b.IsEnabled = false;
        var label = b.Content;
        b.Content = "Please wait…";
        try
        {
            await work();
            if (!keepMessage) _message = null;
        }
        catch (Exception ex)
        {
            Log.Warn("account", $"Sign-in action failed: {(ex is AccountException ? ex.Message : ex.GetType().Name)}");
            _message = ex is AccountException ? ex.Message : "Something went wrong. Check your connection and try again.";
            _messageIsError = true;
            // Signing in before confirming the email goes to the code screen (with a way to get a new code).
            if (_message == AccountService.NotConfirmedMessage) { _mode = Mode.CheckEmail; _code = ""; }
        }
        finally { _busy = false; b.IsEnabled = true; b.Content = label; }

        if (Svc.SignedIn) { SignedIn?.Invoke(); return; }
        Render();
    }

    private void ShowMessage(string text, bool error)
    {
        _message = text;
        _messageIsError = error;
        if (_form.Children.OfType<FrameworkElement>().FirstOrDefault(c => c.Tag as string == "message") is { } old) _form.Children.Remove(old);
        AddMessage();
    }

    private void AddMessage()
    {
        if (_message is null) return;
        var n = Kit.Notice(_message, _messageIsError ? "error" : "info");
        n.Tag = "message";
        n.Margin = new Thickness(0, 12, 0, 0);
        // Keep it right under the main button (before the "create an account" line).
        int at = _form.Children.OfType<Button>().LastOrDefault(b => b.Style == Ui.Style("Primary")) is { } primary ? _form.Children.IndexOf(primary) + 1 : _form.Children.Count;
        _form.Children.Insert(at, n);
    }

    private static bool LooksLikeEmail(string s) => s.Length >= 5 && s.IndexOf('@') > 0 && s.LastIndexOf('.') > s.IndexOf('@') && !s.Contains(' ');

    private static StackPanel Heading(string title, string? subtitle)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (subtitle is not null) sp.Children.Add(new TextBlock { Text = subtitle, Foreground = Ui.Res("TextDimBrush"), FontSize = 13, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        return sp;
    }

    private static (StackPanel Host, Control Input) Field(string label, Control input)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Ui.Res("TextDimBrush"), Margin = new Thickness(1, 0, 0, 6) });
        input.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationName(input, label);
        sp.Children.Add(input);
        return (sp, input);
    }

    private static void AutomationName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);

    private static TextBlock Para(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20, Margin = new Thickness(0, 0, 0, 10), Foreground = Ui.Res("TextDimBrush"),
    };

    private Button BackLink()
    {
        var b = new Button { Content = "Back to sign in", Style = Ui.Style("Link"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        b.Click += (_, _) => { _message = null; _mode = Mode.SignIn; Render(); };
        return b;
    }

    private UIElement TermsLine(string prefix)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = Ui.Res("TextMutedBrush"), Margin = new Thickness(0, 16, 0, 0), TextAlignment = TextAlignment.Center };
        tb.Inlines.Add(new System.Windows.Documents.Run(prefix));
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("VYTRIX Terms of Agreement")) { Foreground = Ui.Res("AccentHoverBrush") };
        link.Click += (_, _) => ShowTerms();
        tb.Inlines.Add(link);
        tb.Inlines.Add(new System.Windows.Documents.Run("."));
        return tb;
    }

    private void ShowTerms()
    {
        var w = new Window
        {
            Owner = this, Title = $"VYTRIX Terms of Agreement (v{Terms.Version})", Width = 680, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Ui.Res("BgBrush"), ShowInTaskbar = false,
            Content = new ScrollViewer
            {
                Margin = new Thickness(18), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = Terms.Text, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("TextBrush") },
            },
        };
        w.ShowDialog();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _mode != Mode.SignIn && !_busy) { _message = null; _mode = Mode.SignIn; Render(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
