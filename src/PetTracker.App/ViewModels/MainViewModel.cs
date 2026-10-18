using System.IO;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using PetTracker.App.Infrastructure;
using System.Collections.ObjectModel;
using PetTracker.Core;
using PetTracker.Core.Domain;
using PetTracker.Core.Protocol;
using PetTracker.Core.Startup;

namespace PetTracker.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private string _genus = "All", _level = "Any level", _state = "All", _sort = "Id", _tab = "Collection", _search = "", _settingsSection = "General";
    private bool _hasSoulsOnly, _hideSpecial;
    private PetRowViewModel? _selected;
    private string _hotkeyMessage = "";
    private string _toggleOverlayText, _toggleClickThroughText;
    private readonly AutoStart _autoStart;
    private readonly string? _exePath;
    private string _opcodeMessage = "", _autoStartMessage = "";

    public MainViewModel(TrackerModel model, OverlayViewModel overlay, Func<string?, string?, string?> applyHotkeys, Action restartCapture,
        AutoStart autoStart, string? exePath)
    {
        Model = model;
        _autoStart = autoStart;
        _exePath = exePath;
        Overlay = overlay;
        _toggleOverlayText = model.Settings.Hotkeys.ToggleOverlay;
        _toggleClickThroughText = model.Settings.Hotkeys.ToggleClickThrough;

        View = CollectionViewSource.GetDefaultView(model.Rows);
        View.Filter = o => o is PetRowViewModel r && Matches(r);
        ApplySort();

        model.SummaryChanged += () => { Raise(nameof(SummaryText)); Raise(nameof(SpecialText)); Raise(nameof(LevelText)); Raise(nameof(ShownText)); View.Refresh(); };
        model.StatusChanged += () => Raise(nameof(StatusText));
        model.SessionChanged += RefreshCaptureProblem;
        model.Catalog.Changed += () => View.Refresh();
        model.RenameRequested += BeginRename;

        ApplyHotkeysCommand = new RelayCommand(() => HotkeyMessage = applyHotkeys(ToggleOverlayText, ToggleClickThroughText) ?? "Hotkeys applied.");
        ApplyAdapterCommand = new RelayCommand(() => { model.SaveSettings(); restartCapture(); });
        OpenDataFolderCommand = new RelayCommand(() => ExternalLinks.Open(model.Paths.Folder, model.Paths.Folder));
        OpenNpcapCommand = new RelayCommand(() => ExternalLinks.Open(ExternalLinks.NpcapUrl, model.Paths.Folder));
        OpenOpcodesFileCommand = new RelayCommand(() => ExternalLinks.Open(model.Paths.Opcodes, model.Paths.Folder, model.Paths.Opcodes));
        SaveOpcodesCommand = new RelayCommand(SaveOpcodes);
        ResetOpcodesCommand = new RelayCommand(ResetOpcodes);
        ReloadOpcodesCommand = new RelayCommand(() =>
        {
            var error = model.ReloadOpcodes("Opcodes re-read from file");
            OpcodeMessage = error is null ? "Reloaded opcodes.json." : "opcodes.json is not valid and was ignored: " + error;
        });
        model.StatusChanged += () => Raise(nameof(StatusTooltip));
        model.OpcodesChanged += LoadOpcodeRows;
        LoadOpcodeRows();
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        Data = new DataTransferViewModel(model, () =>
        {
            ToggleOverlayText = model.Settings.Hotkeys.ToggleOverlay;
            ToggleClickThroughText = model.Settings.Hotkeys.ToggleClickThrough;
            HotkeyMessage = applyHotkeys(ToggleOverlayText, ToggleClickThroughText) ?? "Hotkeys applied.";
            Raise(nameof(AdapterOverride));
        });
        ResetSessionCommand = new RelayCommand(() => { model.Stats.Reset(DateTime.UtcNow); foreach (var r in model.Rows) r.RefreshSession(); View.Refresh(); model.NotifySession(); });
    }

    public TrackerModel Model { get; }
    public OverlayViewModel Overlay { get; }
    public DataTransferViewModel Data { get; }
    public ICollectionView View { get; }

    public IReadOnlyList<string> GenusOptions { get; } = ["All", "Fera", "Cogni", "Natura", "Varian", "Special"];
    public IReadOnlyList<string> LevelOptions { get; } = ["Any level", "L1", "L2", "L3"];
    public IReadOnlyList<string> StateOptions { get; } = ["All", "Registered", "Unregistered"];
    public IReadOnlyList<string> SortOptions { get; } = ["Id", "Name", "Level", "Progress", "Session"];
    public IReadOnlyList<string> TabOptions { get; } = ["Collection", "Settings"];
    public IReadOnlyList<string> SettingsSections { get; } = ["General", "Overlay", "Capture", "Opcodes", "Data", "About"];

    public ICommand ApplyHotkeysCommand { get; }
    public ICommand ApplyAdapterCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenNpcapCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand ResetSessionCommand { get; }

    public string StatusText => Model.Status.Text;
    public string StatusTooltip => Model.StatusTooltip;
    public string DataFolder => Model.Paths.Folder;
    public string VersionText => $"Aion 2 Pet Tracker {AppInfo.Version}";

    // --- opcode editor --------------------------------------------------------------------------------------------

    public ObservableCollection<OpcodeRowViewModel> OpcodeRows { get; } = [];
    public ICommand OpenOpcodesFileCommand { get; }
    public ICommand SaveOpcodesCommand { get; }
    public ICommand ResetOpcodesCommand { get; }
    public ICommand ReloadOpcodesCommand { get; }
    public string OpcodeMessage { get => _opcodeMessage; set => Set(ref _opcodeMessage, value); }

    private void LoadOpcodeRows()
    {
        var defaults = Model.DefaultOpcodes;
        var overrides = Model.OpcodeOverridesOnDisk;
        var names = defaults.Keys.Union(overrides.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n == MessageNames.Heartbeat ? 1 : 0).ThenBy(n => n, StringComparer.OrdinalIgnoreCase);
        OpcodeRows.Clear();
        foreach (var name in names)
            OpcodeRows.Add(new OpcodeRowViewModel(name, defaults.GetValueOrDefault(name) ?? [], overrides.GetValueOrDefault(name)));
    }

    private void SaveOpcodes()
    {
        var ok = true;
        foreach (var row in OpcodeRows)
            ok &= row.Validate();
        if (!ok)
        {
            OpcodeMessage = "Fix the marked rows first (hex opcodes such as 0990, comma separated).";
            return;
        }

        var overrides = new Dictionary<string, IReadOnlyList<ushort>>(StringComparer.OrdinalIgnoreCase);
        var defaults = Model.DefaultOpcodes;
        foreach (var row in OpcodeRows)
        {
            if (row.ParsedOverride() is not { } list)
                continue;
            if (defaults.TryGetValue(row.Name, out var d) && d.Order().SequenceEqual(list.Order()))
                continue; // same as the default: no override needed
            overrides[row.Name] = list;
        }

        // One opcode must not carry two different messages: the parser would pick whichever it meets first.
        var effective = defaults.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, list) in overrides)
            effective[name] = list;
        foreach (var group in effective.Where(kv => kv.Key != MessageNames.Heartbeat)
                     .SelectMany(kv => kv.Value.Select(op => (Name: kv.Key, Op: op))).GroupBy(x => x.Op).Where(g => g.Select(x => x.Name).Distinct().Count() > 1))
        {
            var names = string.Join(" and ", group.Select(x => x.Name).Distinct());
            foreach (var row in OpcodeRows.Where(r => group.Any(x => string.Equals(x.Name, r.Name, StringComparison.OrdinalIgnoreCase))))
                row.SetError($"{OpcodeOverrides.Format(group.Key)} is used by both {names}.");
            OpcodeMessage = $"{OpcodeOverrides.Format(group.Key)} cannot carry both {names}.";
            return;
        }

        Model.SaveOpcodeOverrides(overrides);
        OpcodeMessage = overrides.Count == 0 ? "Saved: all defaults. Opcodes reloaded." : $"Saved {overrides.Count} override(s). Opcodes reloaded; no restart needed.";
    }

    private void ResetOpcodes()
    {
        Model.SaveOpcodeOverrides(new Dictionary<string, IReadOnlyList<ushort>>());
        OpcodeMessage = "Back to the default opcodes (opcodes.json removed).";
    }

    // --- start with Windows -----------------------------------------------------------------------------------------

    /// <summary>Adds or removes HKCU\...\Run\Aion2PetTracker. Only available for the real PetTracker.App.exe.</summary>
    public bool StartWithWindows
    {
        get => _autoStart.IsEnabled;
        set
        {
            if (value == _autoStart.IsEnabled)
                return;
            try
            {
                _autoStart.SetEnabled(value, _exePath);
                AutoStartMessage = value ? $"Starts with Windows, minimized: {AutoStart.BuildCommand(_exePath!)}" : "Will not start with Windows.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                AutoStartMessage = ex.Message;
            }
            Raise();
        }
    }

    public bool CanStartWithWindows => AutoStart.CanRegister(_exePath);
    public string AutoStartMessage { get => _autoStartMessage; private set => Set(ref _autoStartMessage, value); }

    public string SummaryText
    {
        get
        {
            var c = Model.GameCount;
            return $"Collection: {c.CollectionText}";
        }
    }

    public string SpecialText => $"Special: {Model.GameCount.SpecialText} (cash shop / external)";

    public string LevelText
    {
        get
        {
            var s = Model.Summary;
            return string.Join("   ", s.PerLevel.Select((n, i) => $"L{i + 1}: {n}")) + $"   Unregistered: {s.Unregistered.Count}";
        }
    }

    public string ShownText => $"{View.Cast<object>().Count()} of {Model.Rows.Count} shown";

    public string SelectedGenus { get => _genus; set { if (Set(ref _genus, value)) Refilter(); } }
    public string SelectedLevel { get => _level; set { if (Set(ref _level, value)) Refilter(); } }
    public string SelectedState { get => _state; set { if (Set(ref _state, value)) Refilter(); } }
    public string SearchText { get => _search; set { if (Set(ref _search, value)) Refilter(); } }
    public bool HasSoulsOnly { get => _hasSoulsOnly; set { if (Set(ref _hasSoulsOnly, value)) Refilter(); } }
    public bool HideSpecial { get => _hideSpecial; set { if (Set(ref _hideSpecial, value)) Refilter(); } }

    public string SelectedSort
    {
        get => _sort;
        set { if (Set(ref _sort, value)) ApplySort(); }
    }

    public string SelectedTab
    {
        get => _tab;
        set { if (Set(ref _tab, value ?? "Collection")) { Raise(nameof(IsCollectionTab)); Raise(nameof(IsSettingsTab)); } }
    }

    public bool IsCollectionTab => _tab == "Collection";
    public bool IsSettingsTab => _tab == "Settings";

    public string SelectedSettingsSection { get => _settingsSection; set => Set(ref _settingsSection, value ?? "General"); }

    public PetRowViewModel? SelectedRow { get => _selected; set => Set(ref _selected, value); }

    public string ToggleOverlayText { get => _toggleOverlayText; set => Set(ref _toggleOverlayText, value); }
    public string ToggleClickThroughText { get => _toggleClickThroughText; set => Set(ref _toggleClickThroughText, value); }
    public string HotkeyMessage { get => _hotkeyMessage; set => Set(ref _hotkeyMessage, value); }

    public string AdapterOverride { get => Model.AdapterOverride ?? ""; set { Model.AdapterOverride = value; Raise(); } }

    public bool HasCaptureProblem => Model.CaptureProblem is not null;
    public bool ShowNpcapBanner => Model.NpcapMissing;
    public string? CaptureProblem => Model.CaptureProblem;

    public void RefreshCaptureProblem()
    {
        Raise(nameof(ShowNpcapBanner));
        Raise(nameof(HasCaptureProblem));
        Raise(nameof(CaptureProblem));
    }

    /// <summary>Shows the pet in the list in rename mode (used by the overlay's "name" button).</summary>
    public void BeginRename(int petId)
    {
        ClearFilters();
        SelectedTab = "Collection";
        if (Model.Row(petId) is { } row)
        {
            SelectedRow = row;
            row.BeginRename();
        }
    }

    private void ClearFilters()
    {
        _genus = "All"; _level = "Any level"; _state = "All"; _search = ""; _hasSoulsOnly = false; _hideSpecial = false;
        Raise(nameof(SelectedGenus)); Raise(nameof(SelectedLevel)); Raise(nameof(SelectedState));
        Raise(nameof(SearchText)); Raise(nameof(HasSoulsOnly)); Raise(nameof(HideSpecial));
        Refilter();
    }

    private void Refilter()
    {
        View.Refresh();
        Raise(nameof(ShownText));
    }

    private bool Matches(PetRowViewModel r)
    {
        if (_genus != "All" && !string.Equals(r.Genus, _genus, StringComparison.OrdinalIgnoreCase))
            return false;
        if (_level != "Any level" && r.Level.ToString() != _level[1..])
            return false;
        if (_state == "Registered" && !r.Registered)
            return false;
        if (_state == "Unregistered" && r.Registered)
            return false;
        if (_hasSoulsOnly && !r.HasSouls)
            return false;
        if (_hideSpecial && r.IsSpecial)
            return false;
        if (_search.Length > 0
            && !r.DisplayName.Contains(_search, StringComparison.OrdinalIgnoreCase)
            && !r.Id.ToString().Contains(_search, StringComparison.Ordinal)
            && !r.Genus.Contains(_search, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private void ApplySort()
    {
        View.SortDescriptions.Clear();
        switch (_sort)
        {
            case "Name": View.SortDescriptions.Add(new SortDescription(nameof(PetRowViewModel.DisplayName), ListSortDirection.Ascending)); break;
            case "Level": View.SortDescriptions.Add(new SortDescription(nameof(PetRowViewModel.Level), ListSortDirection.Descending)); break;
            case "Progress": View.SortDescriptions.Add(new SortDescription(nameof(PetRowViewModel.ProgressFraction), ListSortDirection.Descending)); break;
            case "Session": View.SortDescriptions.Add(new SortDescription(nameof(PetRowViewModel.SessionGains), ListSortDirection.Descending)); break;
        }
        View.SortDescriptions.Add(new SortDescription(nameof(PetRowViewModel.Id), ListSortDirection.Ascending));
    }
}
