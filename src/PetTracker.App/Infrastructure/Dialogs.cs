using System.Windows;
using Microsoft.Win32;

namespace PetTracker.App.Infrastructure;

/// <summary>File pickers and confirmations owned by the main window, so view models never touch view types.</summary>
internal static class Dialogs
{
    private const string JsonFilter = "JSON files (*.json)|*.json|All files (*.*)|*.*";

    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    public static string? SaveJson(string title, string fileName)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = fileName, Filter = JsonFilter, DefaultExt = ".json", AddExtension = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public static string? OpenJson(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = JsonFilter, CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public static bool Confirm(string title, string text)
    {
        var owner = Owner;
        var result = owner is null
            ? MessageBox.Show(text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel)
            : MessageBox.Show(owner, text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        return result == MessageBoxResult.OK;
    }
}
