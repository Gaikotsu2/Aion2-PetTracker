namespace PetTracker.Core.Domain;

/// <summary>
/// Where a pet can be obtained, for pets limited to one place: the optional "source" field in data/pets.json.
/// Independent of the genus (a Fera pet can be a boss drop) and of the Collection Status count.
/// </summary>
public static class PetSource
{
    public const string Dungeon = "Dungeon";
    public const string Shop = "Shop";

    public static bool IsDungeon(string? source) => string.Equals(source?.Trim(), Dungeon, StringComparison.OrdinalIgnoreCase);

    public static bool IsShop(string? source) => string.Equals(source?.Trim(), Shop, StringComparison.OrdinalIgnoreCase);

    /// <summary>"Dungeon" or "Shop" in canonical spelling; null for anything else.</summary>
    public static string? Normalize(string? source) => IsDungeon(source) ? Dungeon : IsShop(source) ? Shop : null;

    public const string DungeonTooltip = "Dungeon exclusive — drops only in dungeons (for example from bosses)";
    public const string ShopTooltip = "Shop exclusive — only sold in a shop";

    public static string? TooltipFor(string? source) => IsDungeon(source) ? DungeonTooltip : IsShop(source) ? ShopTooltip : null;
}
