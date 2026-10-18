using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using PetTracker.App.Infrastructure;
using PetTracker.Core;
using PetTracker.Core.Domain;
using PetTracker.Core.Persistence;

namespace PetTracker.App.ViewModels;

/// <summary>Settings → Data: PetSouls.dev export/import and the tracker's own backup/restore.</summary>
public sealed class DataTransferViewModel : ObservableObject
{
    private readonly TrackerModel _model;
    private readonly Action _settingsRestored;
    private string _importText = "", _petSoulsMessage = "", _backupMessage = "";

    public DataTransferViewModel(TrackerModel model, Action settingsRestored)
    {
        _model = model;
        _settingsRestored = settingsRestored;
        CopyPetSoulsCodeCommand = new RelayCommand(CopyPetSoulsCode);
        SavePetSoulsFileCommand = new RelayCommand(SavePetSoulsFile);
        OpenPetSoulsCommand = new RelayCommand(() => ExternalLinks.Open(LinkPolicy.PetSoulsUrl, model.Paths.Folder));
        ImportPetSoulsCommand = new RelayCommand(_ => ImportPetSouls(ImportText), _ => ImportText.Trim().Length > 0);
        LoadPetSoulsFileCommand = new RelayCommand(LoadPetSoulsFile);
        ExportBackupCommand = new RelayCommand(ExportBackup);
        RestoreBackupCommand = new RelayCommand(RestoreBackup);
    }

    public ICommand CopyPetSoulsCodeCommand { get; }
    public ICommand SavePetSoulsFileCommand { get; }
    public ICommand OpenPetSoulsCommand { get; }
    public ICommand ImportPetSoulsCommand { get; }
    public ICommand LoadPetSoulsFileCommand { get; }
    public ICommand ExportBackupCommand { get; }
    public ICommand RestoreBackupCommand { get; }

    public string ImportText { get => _importText; set => Set(ref _importText, value ?? ""); }
    public string PetSoulsMessage { get => _petSoulsMessage; private set => Set(ref _petSoulsMessage, value); }
    public string BackupMessage { get => _backupMessage; private set => Set(ref _backupMessage, value); }

    private PetSoulsProfile CurrentProfile() => PetSoulsFormat.FromCollection(_model.Collection.Pets, _model.Settings.PetSouls);

    private string ExtrasNote(PetSoulsProfile p) => p.Targets.Count + p.Watch.Count + p.Other.Count == 0
        ? ""
        : $" Includes {p.Targets.Count} target(s), {p.Watch.Count} watch entr{(p.Watch.Count == 1 ? "y" : "ies")} and {p.Other.Count} other item(s) from your last PetSouls import.";

    // --- PetSouls export --------------------------------------------------------------------------------------------

    private void CopyPetSoulsCode()
    {
        var profile = CurrentProfile();
        try
        {
            Clipboard.SetText(PetSoulsFormat.ToCode(profile));
            PetSoulsMessage = $"Copied a code with {profile.Levels.Count} registered pet(s). On PetSouls.dev open Settings, paste it under Import." + ExtrasNote(profile);
        }
        catch (ExternalException ex)
        {
            PetSoulsMessage = "The clipboard is busy (another program holds it). Try again. " + ex.Message;
        }
    }

    private void SavePetSoulsFile()
    {
        if (Dialogs.SaveJson("Save PetSouls profile", $"petsouls-{DateTime.Now:yyyy-MM-dd}.json") is not { } path)
            return;
        var profile = CurrentProfile();
        try
        {
            AtomicFile.WriteAllText(path, PetSoulsFormat.ToFileJson(profile));
            PetSoulsMessage = $"Saved {profile.Levels.Count} registered pet(s) to {path}. Load it on PetSouls.dev under Settings → Import." + ExtrasNote(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PetSoulsMessage = "Could not save: " + ex.Message;
        }
    }

    // --- PetSouls import --------------------------------------------------------------------------------------------

    private void LoadPetSoulsFile()
    {
        if (Dialogs.OpenJson("Load PetSouls profile or code") is not { } path)
            return;
        try
        {
            if (new FileInfo(path).Length > 1_000_000)
            {
                PetSoulsMessage = "That file is too large to be a PetSouls profile.";
                return;
            }
            ImportPetSouls(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PetSoulsMessage = "Could not read the file: " + ex.Message;
        }
    }

    private void ImportPetSouls(string text)
    {
        if (!PetSoulsFormat.TryParse(text, out var profile, out var error))
        {
            PetSoulsMessage = error!;
            return;
        }

        var now = DateTime.UtcNow;
        var current = _model.Collection.Pets.ToDictionary(p => p.Id);
        var states = new List<PetState>();
        int changed = 0, unregistered = 0;
        foreach (var id in current.Keys.Union(profile!.Levels.Keys).Order())
        {
            var level = profile.Levels.GetValueOrDefault(id);
            current.TryGetValue(id, out var cur);
            if (cur is not null && cur.Level == level)
            {
                states.Add(cur);
                continue;
            }
            changed++;
            if (cur is { Level: > 0 } && level == 0)
                unregistered++;
            // A different level makes the souls count meaningless; the next login snapshot brings the real one.
            states.Add(new PetState(id, level, 0, level > 0, now, NeedsResync: false));
        }
        var known = _model.Catalog.KnownIds.ToHashSet();
        var unknown = profile.Levels.Keys.Count(id => !known.Contains(id));

        var summary = $"The code has {profile.Levels.Count} registered pet(s). {changed} pet(s) change level"
                      + (unregistered > 0 ? $", {unregistered} of them become unregistered because the code does not list them" : "")
                      + (unknown > 0 ? $". {unknown} pet id(s) are not in the tracker's catalog" : "")
                      + ".\n\nSouls progress is reset to 0 where the level changes. The next login snapshot from the game overwrites all of this with the real state."
                      + "\n\nA backup of the current state is saved first. Import now?";
        if (!Dialogs.Confirm("Import PetSouls code", summary))
            return;

        if (!TrySafetyBackup(out var backupPath, out var backupError))
        {
            PetSoulsMessage = "Import cancelled: the safety backup could not be written. " + backupError;
            return;
        }
        _model.ReplaceCollection(states, $"PetSouls import: {profile.Levels.Count} levels, {changed} changed; safety backup {backupPath}");
        _model.Settings.PetSouls = profile.ToExtras();
        _model.SaveSettings();
        ImportText = "";
        PetSoulsMessage = $"Imported: {changed} pet(s) changed level." + (profile.Skipped > 0 ? $" {profile.Skipped} invalid entr{(profile.Skipped == 1 ? "y was" : "ies were")} skipped." : "")
                          + $" Previous state saved to {backupPath}.";
    }

    // --- own backup -------------------------------------------------------------------------------------------------

    private void ExportBackup()
    {
        if (Dialogs.SaveJson("Export tracker backup", $"aion2-pet-tracker-backup-{DateTime.Now:yyyy-MM-dd}.json") is not { } path)
            return;
        try
        {
            var backup = _model.CreateBackup();
            BackupFile.Write(path, backup);
            BackupMessage = $"Backup saved: {backup.Pets.Count} pet(s), {backup.PetNames.Count} custom name(s), settings. {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BackupMessage = "Could not save the backup: " + ex.Message;
        }
    }

    private void RestoreBackup()
    {
        if (Dialogs.OpenJson("Restore tracker backup") is not { } path)
            return;
        if (!BackupFile.TryRead(path, out var backup, out var error))
        {
            BackupMessage = error!;
            return;
        }

        var text = $"Restore the backup from {backup!.CreatedAt.ToLocalTime():g} (tracker {backup.AppVersion})?\n\n"
                   + $"{backup.Pets.Count} pet(s), {backup.PetNames.Count} custom name(s), pins, overlay look, hotkeys and adapter replace what is here now. "
                   + "The overlay position stays as it is.\n\nA backup of the current state is saved first.";
        if (!Dialogs.Confirm("Restore backup", text))
            return;

        if (!TrySafetyBackup(out var backupPath, out var backupError))
        {
            BackupMessage = "Restore cancelled: the safety backup could not be written. " + backupError;
            return;
        }
        try
        {
            _model.Catalog.ReplaceOverrides(backup.PetNames);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BackupMessage = "Could not write pet-names.json: " + ex.Message;
            return;
        }
        _model.ReplaceCollection(backup.ToStates(), $"backup restored from {path}; safety backup {backupPath}");
        _model.ApplySettings(backup.Settings);
        _settingsRestored();
        BackupMessage = $"Restored {backup.Pets.Count} pet(s) and {backup.PetNames.Count} custom name(s). Previous state saved to {backupPath}."
                        + " A restored adapter override takes effect after Capture → Apply and restart capture.";
    }

    private bool TrySafetyBackup(out string? path, out string? error)
    {
        try
        {
            path = _model.WriteSafetyBackup();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            path = null;
            error = ex.Message;
            return false;
        }
    }
}
