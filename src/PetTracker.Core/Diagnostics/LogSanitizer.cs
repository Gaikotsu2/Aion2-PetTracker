using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PetTracker.Core.Diagnostics;

/// <summary>
/// Strips what could identify the person from a log line: IP addresses (replaced by a per-session token, so "same address"
/// stays readable), MAC addresses, the Windows user name, the machine name and the profile folders in paths. Applied to every
/// line before it is queued, and again when a report is assembled.
/// </summary>
public sealed partial class LogSanitizer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _ips = [];
    private readonly List<(string Value, string Token)> _pathRoots = [];
    private readonly Regex? _userName;
    private readonly Regex? _machineName;

    public LogSanitizer(string? userName = null, string? machineName = null, IEnumerable<(string Path, string Token)>? pathRoots = null)
    {
        userName ??= Environment.UserName;
        machineName ??= Environment.MachineName;
        _userName = WholeWord(userName);
        _machineName = WholeWord(machineName);

        var roots = pathRoots ?? [
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
        ];
        foreach (var (path, token) in roots.Where(r => r.Path.Length > 3).OrderByDescending(r => r.Path.Length))
            _pathRoots.Add((path.TrimEnd('\\', '/'), token));
    }

    private static Regex? WholeWord(string name) =>
        name.Length < 2 ? null : new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b")]
    private static partial Regex MacPattern();

    [GeneratedRegex(@"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.]*\d)")]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?<![\w:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?:%\d+)?(?![\w:])")]
    private static partial Regex Ipv6Pattern();

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\\/\s""'<>|:]+", RegexOptions.IgnoreCase)]
    private static partial Regex UserFolderPattern();

    [GeneratedRegex(@"(?<=\bin )[A-Za-z]:\\(?:[^\r\n:]*\\)?(?=[^\\\r\n:]+:line)")]
    private static partial Regex StackPathPattern();

    public string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        try
        {
            text = MacPattern().Replace(text, "<mac>");
            foreach (var (value, token) in _pathRoots)
                text = text.Replace(value, token, StringComparison.OrdinalIgnoreCase);
            text = UserFolderPattern().Replace(text, m => m.Value[..3] + @"\Users\<user>");
            text = StackPathPattern().Replace(text, "");
            text = Ipv4Pattern().Replace(text, IPv4);
            text = Ipv6Pattern().Replace(text, IPv6);
            // Machine name first: it often contains the user name ("ALICE-PC").
            if (_machineName is not null)
                text = _machineName.Replace(text, "<host>");
            if (_userName is not null)
                text = _userName.Replace(text, "<user>");
        }
        catch (RegexMatchTimeoutException)
        {
            return "<line removed: could not be sanitized>";
        }
        return text;
    }

    private string IPv4(Match m)
    {
        if (!IPAddress.TryParse(m.Value, out var ip) || m.Value.Split('.').Any(p => p.Length > 3 || int.Parse(p) > 255))
            return m.Value;
        return Token(ip);
    }

    private string IPv6(Match m)
    {
        var value = m.Value;
        if (value.Count(c => c == ':') < 2 || !value.Any(Uri.IsHexDigit) || TimeLike().IsMatch(value))
            return value;
        return IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? Token(ip) : value;
    }

    [GeneratedRegex(@"^\d{1,2}:\d{2}(:\d{2})?$")]
    private static partial Regex TimeLike();

    private string Token(IPAddress ip)
    {
        var key = ip.ToString();
        lock (_gate)
        {
            if (!_ips.TryGetValue(key, out var token))
                _ips[key] = token = $"ip#{_ips.Count + 1}({Kind(ip)})";
            return token;
        }
    }

    private static string Kind(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return "loopback";
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
                ? "private" : "public";
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC ? "private" : "public";
    }
}
