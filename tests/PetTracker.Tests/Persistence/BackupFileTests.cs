using PetTracker.Core.Domain;
using PetTracker.Core.Persistence;

namespace PetTracker.Tests.Persistence;

public class BackupFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pettracker-backup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void RoundTripsPetsNamesAndSettings()
    {
        var path = Path.Combine(_folder, "b.json");
        var settings = new AppSettings { Opacity = 0.5, PinnedPets = [1005], PetSouls = new PetSoulsExtras { Watch = ["Grave Rat"] } };
        settings.Hotkeys.ToggleOverlay = "Ctrl+Alt+O";
        var pets = new[] { new PetState(1005, 2, 30, true, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), false), new PetState(1003, 0, 4, false, DateTime.UnixEpoch, true) };
        BackupFile.Write(path, BackupFile.Create(pets, [new PetInfo(1005, "Klawy")], settings));

        Assert.True(BackupFile.TryRead(path, out var backup, out var error), error);
        Assert.Equal(pets, backup!.ToStates());
        Assert.Equal("Klawy", Assert.Single(backup.PetNames).Name);
        Assert.Equal(0.5, backup.Settings.Opacity);
        Assert.Equal([1005], backup.Settings.PinnedPets);
        Assert.Equal("Ctrl+Alt+O", backup.Settings.Hotkeys.ToggleOverlay);
        Assert.Equal(["Grave Rat"], backup.Settings.PetSouls!.Watch);
    }

    [Theory]
    [InlineData("""{"app":"something-else","version":1}""")]
    [InlineData("""{"levels":{"1005":2}}""")]
    [InlineData("""{"app":"Aion2PetTracker","version":99}""")]
    [InlineData("not json")]
    public void RejectsOtherFiles(string content)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "x.json");
        File.WriteAllText(path, content);
        Assert.False(BackupFile.TryRead(path, out var backup, out var error));
        Assert.Null(backup);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void CatalogOverridesCanBeReplaced()
    {
        var catalog = new PetCatalog(Path.Combine(_folder, "pet-names.json"));
        catalog.SetName(1005, "Old");
        catalog.ReplaceOverrides([new PetInfo(1006, "New"), new PetInfo(1007, "  ")]);
        Assert.Null(catalog.NameOf(1005));
        Assert.Equal("New", Assert.Single(catalog.Overrides).Name);
    }
}
