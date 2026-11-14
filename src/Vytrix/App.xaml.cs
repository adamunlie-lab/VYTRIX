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
        // Developer aid: "--render-tree out.png" renders the Cherry Blossom tree to an image and exits.
        int rt = Array.IndexOf(e.Args, "--render-tree");
        if (rt >= 0 && rt + 1 < e.Args.Length)
        {
            var tree = new SakuraTree();
            var frame = new System.Windows.Controls.Border { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x0D, 0x13)), Child = tree };
            frame.Measure(new Size(SakuraTree.DesignWidth, SakuraTree.DesignHeight));
            frame.Arrange(new Rect(0, 0, SakuraTree.DesignWidth, SakuraTree.DesignHeight));
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)SakuraTree.DesignWidth, (int)SakuraTree.DesignHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(frame);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = File.Create(e.Args[rt + 1])) enc.Save(fs);
            Shutdown();
            return;
        }
        // One instance per user session. An elevated relaunch passes --elevated and waits for the old one to exit.
        _single = new Mutex(false, @"Local\VYTRIX-SingleInstance");
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
        // Updates moved to GitHub releases; existing installs with no feed get the official channel once.
        if (!Settings.Current.GitHubFeedIntroduced)
        {
            if (string.IsNullOrWhiteSpace(Settings.Current.UpdateFeedUrl)) Settings.Current.UpdateFeedUrl = Updater.DefaultFeed;
            Settings.Current.GitHubFeedIntroduced = true;
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
