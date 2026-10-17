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

    /// <summary>Set by App.Quit; closing the window any other way hides it to the tray (with a notification).</summary>
    public bool ReallyClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ReallyClose)
        {
            e.Cancel = true;
            Hide();
            ((App)Application.Current).NotifyHiddenToTray();
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
