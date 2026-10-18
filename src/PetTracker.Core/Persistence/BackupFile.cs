using System.Text.Json;
using PetTracker.Core.Domain;

namespace PetTracker.Core.Persistence;

/// <summary>
/// One file with everything worth moving to another PC: the collection, the user's own pet names and the settings (pins,
/// overlay look, hotkeys, adapter, PetSouls extras). events.jsonl is history only and can be large, so it is not included.
/// </summary>
public sealed record Backup(
    string App,
    int Version,
    DateTime CreatedAt,
    string AppVersion,
    List<CollectionStore.StoredPet> Pets,
    List<PetInfo> PetNames,
    AppSettings Settings)
{
    public const string AppId = "Aion2PetTracker";
    public const int CurrentVersion = 1;

    public IReadOnlyList<PetState> ToStates() =>
        Pets.Select(p => new PetState(p.Id, p.Level, p.Progress, p.Level > 0, p.LastUpdated, p.NeedsResync)).ToList();
}

public static class BackupFile
{
    public static Backup Create(IEnumerable<PetState> pets, IEnumerable<PetInfo> petNames, AppSettings settings, DateTime? createdAt = null) => new(
        Backup.AppId,
        Backup.CurrentVersion,
        createdAt ?? DateTime.UtcNow,
        AppInfo.Version,
        pets.Select(p => new CollectionStore.StoredPet(p.Id, p.Level, p.Progress, p.Registered, p.LastUpdated, p.NeedsResync)).ToList(),
        petNames.ToList(),
        settings);

    public static void Write(string path, Backup backup) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(backup, Json.Options));

    /// <summary>Reads a backup written by <see cref="Write"/>. Never throws for bad content; <paramref name="error"/> says why.</summary>
    public static bool TryRead(string path, out Backup? backup, out string? error)
    {
        backup = null;
        error = null;
        try
        {
            var read = JsonSerializer.Deserialize<Backup>(File.ReadAllText(path), Json.Options);
            if (read is null || read.App != Backup.AppId)
            {
                error = "This file is not an Aion 2 Pet Tracker backup.";
                return false;
            }
            if (read.Version > Backup.CurrentVersion)
            {
                error = $"This backup was made by a newer version of the tracker (format {read.Version}). Update the tracker first.";
                return false;
            }
            backup = read with { Pets = read.Pets ?? [], PetNames = read.PetNames ?? [], Settings = read.Settings ?? new AppSettings() };
            return true;
        }
        catch (JsonException ex)
        {
            error = "The backup file is not valid JSON: " + ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }
}
