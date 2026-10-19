using System.IO;
﻿using System.Windows;
using System.Windows.Threading;
using PetTracker.App.Infrastructure;
using PetTracker.App.ViewModels;
using PetTracker.App.Views;
using PetTracker.Core.Persistence;
using PetTracker.Core.Startup;

namespace PetTracker.App;

public partial class App : Application
{
    private TrackerModel? _model;
    private OverlayWindow? _overlay;
    private MainWindow? _main;
    private HotkeyService? _hotkeys;
    private DispatcherTimer? _timer;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _clickThroughItem;
    private bool _exiting;
    private SingleInstanceGuard? _guard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // "--data <folder>" keeps a test run away from the real %AppData% files (and from the real running tracker).
        var options = StartupOptions.Parse(e.Args);
        var paths = AppPaths.For(options.DataFolder);
        var autoExitSeconds = options.ExitAfterSeconds;

        // One tracker per data folder: a later launch asks the first to show its window (or quit) and leaves.
        _guard = new SingleInstanceGuard(SingleInstanceGuard.KeyFor(paths.Folder));
        if (!_guard.IsFirst)
        {
            if (options.Quit)
                _guard.SignalQuit();
            else if (!options.Minimized)
                _guard.SignalShow();
            _guard.Dispose();
            _guard = null;
            Shutdown();
            return;
        }
        if (options.Quit) // nothing running
        {
            _guard.Dispose();
            _guard = null;
            Shutdown();
            return;
        }
        _guard.Listen(() => Dispatcher.BeginInvoke(new Action(ShowCollection)), () => Dispatcher.BeginInvoke(new Action(Quit)));

        _model = new TrackerModel(paths);
        var overlayVm = new OverlayViewModel(_model);
        var mainVm = new MainViewModel(_model, overlayVm, ApplyHotkeys, _model.RestartCapture, new AutoStart(new RegistryRunKeyStore()), Environment.ProcessPath);

        _overlay = new OverlayWindow(overlayVm);
        _overlay.EnsureHandle();
        _main = new MainWindow(mainVm);

        _hotkeys = new HotkeyService(_overlay);
        mainVm.HotkeyMessage = ApplyHotkeys(_model.Settings.Hotkeys.ToggleOverlay, _model.Settings.Hotkeys.ToggleClickThrough) ?? "";

        _model.RenameRequested += _ => ShowCollection();
        CreateTray();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => _model.Tick();
        _timer.Start();

        if (_model.Settings.OverlayVisible)
            _overlay.Show();
        if (!options.Minimized)
            _main.Show(); // --minimized (Start with Windows): overlay only, the collection window stays in the tray

        _model.StartCapture();

        if (autoExitSeconds > 0)
        {
            var exit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(autoExitSeconds) };
            exit.Tick += (_, _) => Quit();
            exit.Start();
        }
    }

    private string? ApplyHotkeys(string? toggleOverlay, string? toggleClickThrough)
    {
        if (_hotkeys is null || _model is null)
            return null;
        var first = _hotkeys.Set(1, toggleOverlay, ToggleOverlay);
        var second = _hotkeys.Set(2, toggleClickThrough, ToggleClickThrough);
        if (first is null) _model.Settings.Hotkeys.ToggleOverlay = toggleOverlay!.Trim();
        if (second is null) _model.Settings.Hotkeys.ToggleClickThrough = toggleClickThrough!.Trim();
        if (first is null || second is null)
            _model.SaveSettings();
        return first ?? second;
    }

    private void ToggleOverlay()
    {
        if (_overlay is null || _model is null)
            return;
        if (_overlay.IsVisible)
            _overlay.Hide();
        else
            _overlay.Show();
        _model.Settings.OverlayVisible = _overlay.IsVisible;
    }

    private void ToggleClickThrough()
    {
        if (_model is not null)
            _model.ClickThrough = !_model.ClickThrough;
    }

    private void ShowOverlay()
    {
        if (_overlay is null || _model is null)
            return;
        _overlay.Show();
        _model.Settings.OverlayVisible = true;
    }

    private void ShowCollection()
    {
        if (_main is null)
            return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
            _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    private void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show overlay", null, (_, _) => Dispatcher.Invoke(ShowOverlay));
        menu.Items.Add("Hide overlay", null, (_, _) => Dispatcher.Invoke(() => { if (_overlay!.IsVisible) ToggleOverlay(); }));
        _clickThroughItem = new System.Windows.Forms.ToolStripMenuItem("Click-through") { CheckOnClick = false };
        _clickThroughItem.Click += (_, _) => Dispatcher.Invoke(ToggleClickThrough);
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add("Show collection", null, (_, _) => Dispatcher.Invoke(ShowCollection));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Quit));
        menu.Opening += (_, _) => _clickThroughItem.Checked = _model!.ClickThrough;

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Aion 2 Pet Tracker",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowCollection);
    }

    private bool _trayNoticeShown;

    /// <summary>Tells the user (once per session) that the tracker keeps running in the tray.</summary>
    public void NotifyHiddenToTray()
    {
        if (_trayNoticeShown || _tray is null)
            return;
        _trayNoticeShown = true;
        _tray.ShowBalloonTip(5000, "Pet Tracker is still running",
            "The overlay stays up and tracking continues. Double-click the tray icon to reopen, or right-click it and choose Exit.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    internal void Quit()
    {
        if (_exiting)
            return;
        _exiting = true;
        _timer?.Stop();
        _hotkeys?.Dispose();
        _guard?.Dispose();
        _guard = null;
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        if (_overlay is not null)
            _model!.Settings.OverlayVisible = _overlay.IsVisible;
        _model?.Dispose();
        if (_main is not null)
            _main.ReallyClose = true;
        _overlay?.Close();
        _main?.Close();
        Shutdown();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var folder = _model?.Paths.Folder ?? AppPaths.DefaultFolder;
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "crash.log"), $"{DateTime.Now:O}\n{e.Exception}\n\n");
        }
        catch (IOException) { }
        MessageBox.Show(e.Exception.Message, "Aion 2 Pet Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Quit();
    }
}
