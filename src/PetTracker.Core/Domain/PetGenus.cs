namespace PetTracker.Core.Domain;

/// <summary>Genus names. Special pets (cash shop, events, other non-gameplay sources) do not count toward the game's Collection Status.</summary>
public static class PetGenus
{
    public const string Special = "Special";

    public static bool IsSpecial(string? genus) => string.Equals(genus?.Trim(), Special, StringComparison.OrdinalIgnoreCase);

    public static bool CountsTowardCollection(string? genus) => !IsSpecial(genus);

    public const string SpecialTooltip = "Special — obtained outside normal gameplay (cash shop / events); not counted in Collection Status";
}

/// <summary>"Collection: 161/200" as the game computes it (Special pets excluded), plus the Special pets on their own.</summary>
public sealed record GameCollectionCount(int Registered, int Total, int SpecialRegistered, int SpecialTotal)
{
    public string CollectionText => $"{Registered}/{Total}";
    public string SpecialText => $"{SpecialRegistered}/{SpecialTotal}";

    /// <summary>"161/200, Special 1/9"</summary>
    public override string ToString() => $"{CollectionText}, Special {SpecialText}";
}
