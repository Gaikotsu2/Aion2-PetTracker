using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PetTracker.App.ViewModels;

namespace PetTracker.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedRow) && viewModel.SelectedRow is { } row)
                PetList.ScrollIntoView(row);
        };
    }

    /// <summary>
    /// Set by App.Quit; closing the window any other way either hides it to the tray (with a notification) or,
    /// when the user chose "Exit the application", runs App.Quit.
    /// </summary>
    public bool ReallyClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ReallyClose)
        {
            e.Cancel = true;
            var app = (App)Application.Current;
            if (_viewModel.Model.Settings.CloseToTray)
            {
                Hide();
                app.NotifyHiddenToTray();
            }
            else
            {
                // Deferred: Quit closes this window again, which must not happen inside OnClosing.
                Dispatcher.BeginInvoke(app.Quit);
            }
        }
        base.OnClosing(e);
    }

    private void EditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible)
            box.Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
    }
}
