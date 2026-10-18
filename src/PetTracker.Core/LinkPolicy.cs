using System.Text.RegularExpressions;

namespace PetTracker.Core;

/// <summary>
/// The only web addresses the app may hand to the shell: npcap.com, the PetSouls.dev start page and one TH.GL pet page.
/// The pet pattern is exact (https, host aion2.th.gl, path /db/pets/pet_ followed by digits, no query, fragment, port or user info).
/// </summary>
public static partial class LinkPolicy
{
    public const string NpcapUrl = "https://npcap.com";

    /// <summary>Where the exported PetSouls code is pasted (Settings → Import on the site).</summary>
    public const string PetSoulsUrl = "https://petsouls.dev/";

    [GeneratedRegex(@"\Ahttps://aion2\.th\.gl/db/pets/pet_[0-9]{1,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PetUrlPattern();

    public static string PetUrl(int petId) => $"https://aion2.th.gl/db/pets/pet_{petId}";

    public static bool IsPetUrl(string? url) => url is not null && PetUrlPattern().IsMatch(url);

    public static bool IsAllowedUrl(string? url) =>
        url is not null && (string.Equals(url, NpcapUrl, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(url, PetSoulsUrl, StringComparison.OrdinalIgnoreCase)
                            || IsPetUrl(url));
}
