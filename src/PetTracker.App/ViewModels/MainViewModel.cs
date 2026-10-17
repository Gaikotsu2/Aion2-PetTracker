using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using PetTracker.App.Infrastructure;
using PetTracker.Core.Domain;

namespace PetTracker.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private string _genus = "All", _level = "Any level", _state = "All", _sort = "Id", _tab = "Collection", _search = "";
    private bool _hasSoulsOnly, _hideSpecial;
    private PetRowViewModel? _selected;
    private string _hotkeyMessage = "";
    private string _toggleOverlayText, _toggleClickThroughText;

    public MainViewModel(TrackerModel model, OverlayViewModel overlay, Func<string?, string?, string?> applyHotkeys, Action restartCapture)
    {
        Model = model;
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
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ResetSessionCommand = new RelayCommand(() => { model.Stats.Reset(DateTime.UtcNow); foreach (var r in model.Rows) r.RefreshSession(); View.Refresh(); model.NotifySession(); });
    }

    public TrackerModel Model { get; }
    public OverlayViewModel Overlay { get; }
    public ICollectionView View { get; }

    public IReadOnlyList<string> GenusOptions { get; } = ["All", "Fera", "Cogni", "Natura", "Varian", "Special"];
    public IReadOnlyList<string> LevelOptions { get; } = ["Any level", "L1", "L2", "L3"];
    public IReadOnlyList<string> StateOptions { get; } = ["All", "Registered", "Unregistered"];
    public IReadOnlyList<string> SortOptions { get; } = ["Id", "Name", "Level", "Progress", "Session"];
    public IReadOnlyList<string> TabOptions { get; } = ["Collection", "Settings"];

    public ICommand ApplyHotkeysCommand { get; }
    public ICommand ApplyAdapterCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenNpcapCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand ResetSessionCommand { get; }

    public string StatusText => Model.Status.Text;
    public string DataFolder => Model.Paths.Folder;

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
