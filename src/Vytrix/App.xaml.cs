using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Vytrix.Controls;
using Vytrix.Core;
using Vytrix.Native;
using Vytrix.Services;

namespace Vytrix;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Developer aid: "--dump-tweak-states out.txt" writes every tweak's detected state (read-only) and exits.
        int ds = Array.IndexOf(e.Args, "--dump-tweak-states");
        if (ds >= 0 && ds + 1 < e.Args.Length)
        {
            VxNative.vx_init();
            SystemInfo.LoadNative();
            var lines = TweakCatalog.All.Select(t =>
            {
                var st = t.GetState();
                var how = st != TweakState.Applied ? "" : t.AppliedByVytrix ? "  [applied by VYTRIX]" : t.IsOnElsewhere() ? $"  [already on via {t.AlsoOnSource}]" : "  [already on in Windows]";
                return $"{st,-12} {t.ComponentLabel,-18} {t.Name}{how}";
            });
            File.WriteAllLines(e.Args[ds + 1], lines);
            Shutdown();
            return;
        }
        // Developer aid: "--render-tree out.png" / "--render-christmas out.png" render a theme's scene to an image and exit.
        int rt = Array.IndexOf(e.Args, "--render-tree");
        int rc = Array.IndexOf(e.Args, "--render-christmas");
        int rn = Array.IndexOf(e.Args, "--render-newyear");
        if (rc >= 0) rt = rc;
        if (rn >= 0) rt = rn;
        if (rt >= 0 && rt + 1 < e.Args.Length)
        {
            System.Windows.FrameworkElement tree = rn >= 0 ? new FireworksSky() : rc >= 0 ? new ChristmasTree() : new SakuraTree();
            if (tree is FireworksSky fw) fw.ShowStill();   // show the bursts fully bloomed rather than a dark sky
            double w = rn >= 0 ? FireworksSky.DesignWidth : rc >= 0 ? ChristmasTree.DesignWidth : SakuraTree.DesignWidth;
            double h = rn >= 0 ? FireworksSky.DesignHeight : rc >= 0 ? ChristmasTree.DesignHeight : SakuraTree.DesignHeight;
            var bg = rn >= 0 ? System.Windows.Media.Color.FromRgb(0x09, 0x0C, 0x1C) : rc >= 0 ? System.Windows.Media.Color.FromRgb(0x09, 0x12, 0x10) : System.Windows.Media.Color.FromRgb(0x14, 0x0D, 0x13);
            var frame = new System.Windows.Controls.Border { Background = new System.Windows.Media.SolidColorBrush(bg), Child = tree };
            frame.Measure(new Size(w, h));
            frame.Arrange(new Rect(0, 0, w, h));
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w, (int)h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(frame);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = File.Create(e.Args[rt + 1])) enc.Save(fs);
            Shutdown();
            return;
        }
        // One instance per user session. An elevated relaunch passes --elevated and waits for the old one to exit.
        // A QA/test copy with its own data folder (VYTRIX_DATA_DIR) gets its own instance slot,
        // so it can run beside the user's real VYTRIX without touching it.
        string? dataDir = Environment.GetEnvironmentVariable("VYTRIX_DATA_DIR");
        string slot = string.IsNullOrWhiteSpace(dataDir) ? "" : "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataDir.Trim().ToLowerInvariant())))[..16];
        _single = new Mutex(false, @"Local\VYTRIX-SingleInstance" + slot);
        var wait = e.Args.Contains("--elevated") ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
        bool owned;
        try { owned = _single.WaitOne(wait); } catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            MessageBox.Show("VYTRIX is already running.", "VYTRIX", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("crash", "Unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("task", "Unobserved task exception", ex.Exception); ex.SetObserved(); };

        base.OnStartup(e);
        Log.Info("app", $"VYTRIX {Updater.Current.ToString(3)} starting. Admin={Elevation.IsAdmin}, OS={Environment.OSVersion.Version}");

        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "VytrixCore.dll")))
        {
            MessageBox.Show("VytrixCore.dll is missing from the application folder. Please reinstall VYTRIX.", "VYTRIX", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        try
        {
            VxNative.vx_init();
            SystemInfo.LoadNative();
        }
        catch (Exception ex)
        {
            Log.Error("app", "Native core failed to initialise", ex);
            MessageBox.Show($"VYTRIX could not start its system core:\n{ex.Message}", "VYTRIX", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(3);
            return;
        }

        // Cherry Blossom became the default theme; switch existing installs over once.
        if (!Settings.Current.CherryBlossomIntroduced)
        {
            Settings.Current.Theme = ThemeManager.CherryBlossom;
            Settings.Current.Accent = "Sakura";
            Settings.Current.CherryBlossomIntroduced = true;
            Settings.Save();
        }
        // The Christmas edition switches existing installs to the Christmas theme once (Settings can switch back).
        if (!Settings.Current.ChristmasIntroduced)
        {
            Settings.Current.Theme = ThemeManager.Christmas;
            Settings.Current.Accent = ThemeManager.DefaultAccent(ThemeManager.Christmas);
            Settings.Current.ChristmasIntroduced = true;
            Settings.Current.CherryBlossomIntroduced = true;
            Settings.Save();
        }
        // Updates moved to GitHub releases; existing installs with no feed get the official channel once.
        if (!Settings.Current.GitHubFeedIntroduced)
        {
            if (string.IsNullOrWhiteSpace(Settings.Current.UpdateFeedUrl)) Settings.Current.UpdateFeedUrl = Updater.DefaultFeed;
            Settings.Current.GitHubFeedIntroduced = true;
            Settings.Save();
        }
        // Seasonal themes: Christmas becomes New Year after Boxing Day, and back to Graphite after 6 January.
        if (Settings.Current.SeasonalThemes && ThemeManager.SeasonalSwitch(Settings.Current.Theme, DateTime.Now, Settings.Current.NewYearThemeAutomatic) is { } seasonal)
        {
            Log.Info("theme", $"Seasonal switch: {Settings.Current.Theme} → {seasonal}.");
            Settings.Current.NewYearThemeAutomatic = seasonal == ThemeManager.NewYear;
            Settings.Current.Theme = seasonal;
            Settings.Current.Accent = ThemeManager.DefaultAccent(seasonal);
            Settings.Save();
        }
        ThemeManager.ApplyTheme(Settings.Current.Theme);
        ThemeManager.Apply(Settings.Current.Accent);
        Journal.Prune();
        _ = SystemInfo.LoadWmiAsync();

        var win = new MainWindow();
        MainWindow = win;
        win.Show();
        Telemetry.Instance.Start(Dispatcher);
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("ui", "Unhandled UI exception", e.Exception);
        (MainWindow as MainWindow)?.Toast($"Something went wrong: {e.Exception.Message}. Details were written to the log.", ToastKind.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Telemetry.Instance.Stop();
        try { VxNative.vx_shutdown(); } catch { }
        Log.Info("app", "VYTRIX exited.");
        try { _single?.ReleaseMutex(); } catch { }
        _single?.Dispose();
        base.OnExit(e);
    }
}
