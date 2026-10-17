using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using PetTracker.App.Infrastructure;
using PetTracker.App.ViewModels;

namespace PetTracker.App.Views;

public partial class OverlayWindow : Window
{
    private readonly TrackerModel _model;

    public OverlayWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _model = viewModel.Model;

        var s = _model.Settings;
        Width = Math.Max(s.OverlayWidth, MinWidth);
        Height = Math.Max(s.OverlayHeight, MinHeight);
        if (s.OverlayLeft is { } left && s.OverlayTop is { } top && IsOnScreen(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.WorkArea.Right - Width - 24;
            Top = SystemParameters.WorkArea.Top + 80;
        }

        // Create our handle now so the styles and hotkeys can be applied before the window is ever shown.
        SourceInitialized += (_, _) =>
        {
            Native.GuardOverlayStyles(this, () => _model.ClickThrough);
            ApplyClickThrough();
        };
        _model.PropertyChanged += OnModelChanged;
        MouseLeftButtonDown += OnDrag;
        SizeChanged += (_, _) => { if (IsLoaded) { s.OverlayWidth = ActualWidth; s.OverlayHeight = ActualHeight; } };
        LocationChanged += (_, _) => { if (IsLoaded) { s.OverlayLeft = Left; s.OverlayTop = Top; } };
    }

    private static bool IsOnScreen(double left, double top) =>
        left > SystemParameters.VirtualScreenLeft - 50 && top > SystemParameters.VirtualScreenTop - 50
        && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
        && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

    public void EnsureHandle() => new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();

    public void ApplyClickThrough()
    {
        ResizeMode = _model.ClickThrough ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        Native.ApplyOverlayStyles(this, _model.ClickThrough);
        // WPF may flush its cached styles after this call returns; re-apply once it has.
        Dispatcher.BeginInvoke(() => Native.ApplyOverlayStyles(this, _model.ClickThrough), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackerModel.ClickThrough))
            ApplyClickThrough();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (_model.ClickThrough || e.ButtonState != MouseButtonState.Pressed)
            return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }
}
