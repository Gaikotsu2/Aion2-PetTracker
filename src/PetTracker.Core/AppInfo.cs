using System.Reflection;

namespace PetTracker.Core;

public static class AppInfo
{
    /// <summary>The product version from Directory.Build.props (for example 0.1.0), without the source-revision suffix.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString(3)
                   ?? "0.0.0";
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? text[..plus] : text;
    }
}
