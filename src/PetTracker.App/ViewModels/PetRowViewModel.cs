using System.Windows.Input;
using PetTracker.App.Infrastructure;
using PetTracker.Core.Domain;

namespace PetTracker.App.ViewModels;

/// <summary>One pet as shown in the collection list and (when pinned) on the overlay.</summary>
public sealed class PetRowViewModel : ObservableObject
{
    private readonly TrackerModel _model;
    private PetState? _state;
    private bool _editing;
    private string _editText = "";
    private IReadOnlyList<string> _suggestions = [];

    public PetRowViewModel(int id, TrackerModel model)
    {
        Id = id;
        _model = model;
        BeginRenameCommand = new RelayCommand(BeginRename);
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => IsEditing = false);
        PickSuggestionCommand = new RelayCommand(p => { if (p is string s) EditText = s; });
        TogglePinCommand = new RelayCommand(() => IsPinned = !IsPinned);
        OpenLinkCommand = new RelayCommand(() => ExternalLinks.Open(ExternalLinks.PetUrl(Id), _model.Paths.Folder));
    }

    public int Id { get; }
    public ICommand BeginRenameCommand { get; }
    public ICommand CommitRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }
    public ICommand PickSuggestionCommand { get; }
    public ICommand TogglePinCommand { get; }
    public ICommand OpenLinkCommand { get; }

    public string DisplayName => _model.Catalog.DisplayName(Id);
    public bool HasName => _model.Catalog.HasName(Id);
    public string Genus => _model.Catalog.GenusOf(Id) ?? _model.NameList.GenusFor(_model.Catalog.NameOf(Id)) ?? "";
    public bool IsSpecial => _model.Catalog.IsSpecial(Id);
    public string SpecialTooltip => PetGenus.SpecialTooltip;
    public string Source => _model.Catalog.SourceOf(Id) ?? "";
    public bool HasSource => Source.Length > 0;
    public bool IsDungeon => _model.Catalog.IsDungeon(Id);
    public bool IsShop => _model.Catalog.IsShop(Id);
    public string? SourceTooltip => PetSource.TooltipFor(Source);
    public string LinkTooltip => "Open on TH.GL (aion2.th.gl) in your browser";
    public int Level => _state?.Level ?? 0;
    public string LevelText => Level > 0 ? $"L{Level}" : "-";
    public bool Registered => Level > 0;
    public int Progress => Math.Max(_state?.Progress ?? 0, 0);
    public int? Threshold => _model.Collection.Rules.ThresholdFor(Level);
    public bool IsMax => _state is not null && Threshold is null;
    public bool InCollection => _state is not null;
    public bool NeedsResync => _state?.NeedsResync ?? false;
    public int SessionGains => _model.Stats.GainsFor(Id);

    public string RegisteredText => !InCollection ? "" : Registered ? "yes" : "no";

    public string ProgressText => !InCollection ? "" : IsMax ? "MAX" : $"{Progress}/{Threshold}";

    public double ProgressFraction => !InCollection ? 0 : IsMax ? 1 : Threshold is { } t and > 0 ? Math.Min(1.0, Progress / (double)t) : 0;

    public bool HasSouls => Progress > 0;

    public bool IsPinned
    {
        get => _model.IsPinned(Id);
        set
        {
            if (_model.IsPinned(Id) == value)
                return;
            _model.SetPinned(Id, value);
            Raise();
        }
    }

    public bool IsEditing
    {
        get => _editing;
        set
        {
            if (Set(ref _editing, value) && !value)
                Suggestions = [];
        }
    }

    public string EditText
    {
        get => _editText;
        set
        {
            if (!Set(ref _editText, value))
                return;
            Suggestions = IsEditing ? SuggestNames(value) : [];
        }
    }

    /// <summary>The community list is incomplete, so names already given to other pets are offered too.</summary>
    private List<string> SuggestNames(string text)
    {
        var known = _model.Catalog.KnownIds.Select(_model.Catalog.NameOf).OfType<string>()
            .Where(n => n.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase));
        return _model.NameList.Suggest(text).Select(e => e.Name).Concat(known)
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(n => n != text).Take(8).ToList();
    }

    public IReadOnlyList<string> Suggestions
    {
        get => _suggestions;
        private set
        {
            _suggestions = value;
            Raise();
            Raise(nameof(HasSuggestions));
        }
    }

    public bool HasSuggestions => _suggestions.Count > 0;

    public void Update(PetState? state)
    {
        _state = state;
        RaiseAll();
    }

    public void RefreshName()
    {
        Raise(nameof(DisplayName));
        Raise(nameof(HasName));
        Raise(nameof(Genus));
        Raise(nameof(IsSpecial));
        Raise(nameof(Source));
        Raise(nameof(HasSource));
        Raise(nameof(IsDungeon));
        Raise(nameof(IsShop));
        Raise(nameof(SourceTooltip));
    }

    public void RefreshSession() => Raise(nameof(SessionGains));

    public void RefreshPin() => Raise(nameof(IsPinned));

    public void BeginRename()
    {
        _editText = _model.Catalog.NameOf(Id) ?? "";
        Raise(nameof(EditText));
        IsEditing = true;
    }

    private void CommitRename()
    {
        IsEditing = false;
        _model.Catalog.SetName(Id, EditText);
    }

    private void RaiseAll()
    {
        foreach (var n in new[]
                 {
                     nameof(Level), nameof(LevelText), nameof(Registered), nameof(Progress), nameof(Threshold), nameof(IsMax),
                     nameof(InCollection), nameof(NeedsResync), nameof(ProgressText), nameof(ProgressFraction), nameof(HasSouls),
                     nameof(SessionGains), nameof(RegisteredText),
                 })
            Raise(n);
    }
}
