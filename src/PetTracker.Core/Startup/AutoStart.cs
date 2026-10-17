using Microsoft.Win32;

namespace PetTracker.Core.Startup;

/// <summary>One value in the current user's Run key. Behind an interface so tests never touch the real registry.</summary>
public interface IRunKeyStore
{
    string? Get(string name);
    void Set(string name, string command);
    void Remove(string name);
}

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run, current user only.</summary>
public sealed class RegistryRunKeyStore : IRunKeyStore
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void Remove(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>"Start with Windows": a Run entry pointing at the current exe with --minimized.</summary>
public sealed class AutoStart(IRunKeyStore store)
{
    public const string ValueName = "Aion2PetTracker";
    public const string AppExeName = "PetTracker.App.exe";
    public const string MinimizedArgument = "--minimized";

    /// <summary>The exact command line stored in the Run key: the quoted exe path, a space, --minimized.</summary>
    public static string BuildCommand(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("The exe path is empty.", nameof(exePath));
        return $"\"{exePath.Trim().Trim('"')}\" {MinimizedArgument}";
    }

    /// <summary>False when running through "dotnet PetTracker.App.dll" or any other host, where the Run entry would be wrong.</summary>
    public static bool CanRegister(string? exePath) =>
        !string.IsNullOrEmpty(exePath) && string.Equals(Path.GetFileName(exePath), AppExeName, StringComparison.OrdinalIgnoreCase);

    public string? CurrentCommand => store.Get(ValueName);

    public bool IsEnabled => CurrentCommand is not null;

    /// <summary>Enabled and pointing at <paramref name="exePath"/> (not at an older install location).</summary>
    public bool IsEnabledFor(string exePath) =>
        string.Equals(CurrentCommand, BuildCommand(exePath), StringComparison.OrdinalIgnoreCase);

    public void SetEnabled(bool enabled, string? exePath)
    {
        if (!enabled)
        {
            store.Remove(ValueName);
            return;
        }
        if (!CanRegister(exePath))
            throw new InvalidOperationException($"Start with Windows needs the real {AppExeName}, not '{exePath}'.");
        store.Set(ValueName, BuildCommand(exePath!));
    }
}
