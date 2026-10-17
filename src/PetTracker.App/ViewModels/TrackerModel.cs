using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using PetTracker.App.Infrastructure;
using PetTracker.Core;
using PetTracker.Core.Capture;
using PetTracker.Core.Domain;
using PetTracker.Core.Messages;
using PetTracker.Core.Persistence;
using PetTracker.Core.Protocol;

namespace PetTracker.App.ViewModels;

/// <summary>One line of the live feed.</summary>
public sealed class FeedItem : ObservableObject
{
    private readonly DomainEvent _event;
    private readonly TrackerModel _model;

    public FeedItem(DomainEvent e, TrackerModel model)
    {
        _event = e;
        _model = model;
        PetId = DomainEventFormatter.PetIdOf(e);
        NameCommand = new RelayCommand(() => { if (PetId is { } id) model.RequestRename(id); });
        Refresh();
    }

    public int? PetId { get; }
    public ICommand NameCommand { get; }
    public string Time => _event.Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string Text { get; private set; } = "";
    public bool IsWarning => _event is Inconsistency;
    public bool IsHighlight => _event is PetRegistered or PetLevelUp;

    /// <summary>Special pets (cash shop / events) get a subtle tag in the feed.</summary>
    public bool IsSpecial { get; private set; }
    public string SpecialTooltip => PetGenus.SpecialTooltip;

    /// <summary>Unknown pets get a small "name" button.</summary>
    public bool ShowNameButton { get; private set; }

    public void Refresh()
    {
        Text = DomainEventFormatter.Describe(_event, _model.Catalog) ?? "";
        ShowNameButton = PetId is { } id && !_model.Catalog.HasName(id);
        IsSpecial = PetId is { } sid && _model.Catalog.IsSpecial(sid);
        Raise(nameof(IsSpecial));
        Raise(nameof(Text));
        Raise(nameof(ShowNameButton));
    }
}

/// <summary>
/// Everything the windows share, living on the UI thread: collection, catalog, session stats, settings, the rows and the
/// feed. The capture pipeline runs on its own thread and hands over only parsed pet messages (through the Dispatcher).
/// </summary>
public sealed class TrackerModel : ObservableObject, IDisposable
{
    public const int FeedLength = 8;

    private readonly Dispatcher _ui;
    private readonly SettingsStore _settingsStore;
    private readonly CollectionStore _collectionStore;
    private readonly EventLog _log;
    private readonly Dictionary<int, PetRowViewModel> _rows = [];
    private readonly HashSet<int> _pinned;
    private Pipeline? _pipeline;
    private bool _dirty;
    private bool _disposed;
    private DateTime _lastSave = DateTime.UtcNow;
    private DateTime? _firstHeartbeat;
    private long _lastDesyncs;
    private DateTime _lastDesyncAt = DateTime.MinValue;
    private bool _petEventSeen;
    private bool _npcapMissing;
    private TrackerStatus _status = new(TrackerStatusKind.WaitingForGame, "Waiting for game");

    public TrackerModel(AppPaths paths)
    {
        _ui = Dispatcher.CurrentDispatcher;
        Paths = paths;
        _settingsStore = new SettingsStore(paths.Settings);
        _collectionStore = new CollectionStore(paths.Collection);
        _log = new EventLog(paths.Events);
        Settings = _settingsStore.Load();
        Registry = OpcodeRegistry.LoadDefault();
        Catalog = PetCatalog.LoadDefault(paths.Folder);
        NameList = PetNameList.LoadDefault();
        Collection = new PetCollection();
        Stats = new SessionStats(DateTime.UtcNow);
        _pinned = Settings.PinnedPets.ToHashSet();

        _collectionStore.LoadInto(Collection);
        Catalog.Changed += OnCatalogChanged;
        SyncRows();
        UpdateStatus();
    }

    public AppPaths Paths { get; }
    public AppSettings Settings { get; }
    public OpcodeRegistry Registry { get; }
    public PetCatalog Catalog { get; }
    public PetNameList NameList { get; }
    public PetCollection Collection { get; }
    public SessionStats Stats { get; }
    public ObservableCollection<PetRowViewModel> Rows { get; } = [];
    public ObservableCollection<FeedItem> Feed { get; } = [];

    public TrackerStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            Raise();
            StatusChanged?.Invoke();
        }
    }

    /// <summary>Why the capture is not running (Npcap missing, bad adapter), or null.</summary>
    public string? CaptureProblem { get; private set; }
    public bool NpcapMissing => _npcapMissing;

    public event Action? StatusChanged;
    public event Action? PinsChanged;
    public event Action? SummaryChanged;
    public event Action? SessionChanged;
    public event Action<int>? RenameRequested;

    // --- persisted, bindable settings ---------------------------------------------------------------------------

    public double Opacity
    {
        get => Settings.Opacity;
        set { value = Math.Clamp(value, 0.2, 1.0); if (Settings.Opacity != value) { Settings.Opacity = value; Raise(); } }
    }

    public double Scale
    {
        get => Settings.Scale;
        set { value = Math.Clamp(value, 0.6, 2.0); if (Settings.Scale != value) { Settings.Scale = value; Raise(); } }
    }

    public bool ClickThrough
    {
        get => Settings.ClickThrough;
        set { if (Settings.ClickThrough != value) { Settings.ClickThrough = value; Raise(); Raise(nameof(Unlocked)); } }
    }

    public bool Unlocked => !ClickThrough;

    public string? AdapterOverride
    {
        get => Settings.AdapterOverride;
        set => Settings.AdapterOverride = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // --- pets ---------------------------------------------------------------------------------------------------

    public bool IsPinned(int id) => _pinned.Contains(id);

    public void SetPinned(int id, bool pinned)
    {
        if (pinned ? !_pinned.Add(id) : !_pinned.Remove(id))
            return;
        Settings.PinnedPets = _pinned.Order().ToList();
        PinsChanged?.Invoke();
    }

    public IEnumerable<PetRowViewModel> PinnedRows => Rows.Where(r => _pinned.Contains(r.Id));

    public PetRowViewModel? Row(int id) => _rows.GetValueOrDefault(id);

    public CollectionSummary Summary => CollectionSummary.From(Collection.Pets, Collection.Rules.MaxLevel);

    /// <summary>"161/200" as the game shows it (Special pets excluded) plus the Special pets on their own.</summary>
    public GameCollectionCount GameCount => Catalog.GameCount(Collection.Pets);

    public void RequestRename(int id) => RenameRequested?.Invoke(id);

    public void NotifySession() => SessionChanged?.Invoke();

    private PetRowViewModel EnsureRow(int id)
    {
        if (_rows.TryGetValue(id, out var row))
            return row;
        row = new PetRowViewModel(id, this);
        _rows[id] = row;
        var index = 0;
        while (index < Rows.Count && Rows[index].Id < id)
            index++;
        Rows.Insert(index, row);
        return row;
    }

    private void SyncRows()
    {
        var known = Collection.Pets.ToDictionary(p => p.Id);
        foreach (var id in Catalog.KnownIds.Concat(known.Keys))
            EnsureRow(id);
        foreach (var (id, row) in _rows.ToList())
        {
            if (!known.ContainsKey(id) && !Catalog.KnownIds.Contains(id))
            {
                _rows.Remove(id);
                Rows.Remove(row);
                continue;
            }
            row.Update(known.GetValueOrDefault(id));
        }
        SummaryChanged?.Invoke();
    }

    private void OnCatalogChanged()
    {
        SyncRows();
        foreach (var row in _rows.Values)
            row.RefreshName();
        foreach (var item in Feed)
            item.Refresh();
    }

    // --- capture ------------------------------------------------------------------------------------------------

    /// <summary>Starts the pipeline on a background thread; a missing Npcap is reported through <see cref="CaptureProblem"/>.</summary>
    public void StartCapture()
    {
        _npcapMissing = false;
        CaptureProblem = null;
        var options = new PipelineOptions { AdapterOverride = Settings.AdapterOverride };
        Task.Run(() =>
        {
            Pipeline? pipeline = null;
            try
            {
                pipeline = new Pipeline(options);
                pipeline.FramesDecoded += OnFrames;
                Interlocked.Exchange(ref _pipeline, pipeline)?.Dispose();
                pipeline.Start();
            }
            catch (NpcapNotAvailableException ex)
            {
                Interlocked.CompareExchange(ref _pipeline, null, pipeline);
                pipeline?.Dispose();
                _ui.BeginInvoke(() => { _npcapMissing = true; CaptureProblem = ex.Message; UpdateStatus(); });
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _pipeline, null, pipeline);
                pipeline?.Dispose();
                _ui.BeginInvoke(() => { CaptureProblem = ex.Message; UpdateStatus(); });
            }
        });
    }

    public void RestartCapture()
    {
        _firstHeartbeat = null;
        StartCapture();
    }

    private void OnFrames(DecodedFrames decoded)
    {
        // Pipeline thread: parse only, apply on the UI thread.
        var errors = new List<string>();
        var messages = PetMessageParser.Extract(Registry, decoded.Frames, errors.Add).ToList();
        if (messages.Count == 0 && errors.Count == 0)
            return;
        var when = new DateTime(decoded.Ticks, DateTimeKind.Utc);
        _ui.BeginInvoke(() =>
        {
            if (_disposed)
                return;
            foreach (var error in errors)
                Publish([new Inconsistency(when, null, "unparsable pet frame " + error, NeedsResync: false)]);
            ApplyMessages(messages, when);
        });
    }

    /// <summary>Applies parsed messages and fans the resulting events out to the stats, log, feed and rows.</summary>
    public void ApplyMessages(IEnumerable<PetMessage> messages, DateTime when)
    {
        foreach (var message in messages)
        {
            _petEventSeen = true;
            Publish(Collection.Apply(message, when));
        }
        UpdateStatus();
    }

    private void Publish(IReadOnlyList<DomainEvent> events)
    {
        if (events.Count == 0)
            return;
        _dirty = true;
        _log.Append(events);

        var snapshot = false;
        var session = false;
        foreach (var e in events)
        {
            Stats.Observe(e);
            session |= e is SoulGained;
            snapshot |= e is SnapshotLoaded;

            if (DomainEventFormatter.Describe(e, Catalog) is not null)
            {
                Feed.Insert(0, new FeedItem(e, this));
                while (Feed.Count > FeedLength)
                    Feed.RemoveAt(Feed.Count - 1);
            }
        }

        if (snapshot)
        {
            SyncRows();
        }
        else
        {
            foreach (var id in events.Select(DomainEventFormatter.PetIdOf).OfType<int>().Distinct())
            {
                EnsureRow(id).Update(Collection.Get(id));
                if (session)
                    _rows[id].RefreshSession();
            }
            SummaryChanged?.Invoke();
        }
        if (session)
            SessionChanged?.Invoke();
    }

    /// <summary>Called by a timer: finishes message bursts, refreshes the status pill, saves now and then.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        Publish(Collection.Tick(now));
        UpdateStatus();
        SessionChanged?.Invoke();
        if (_dirty && now - _lastSave > TimeSpan.FromSeconds(5))
            Save();
    }

    private void UpdateStatus()
    {
        var now = DateTime.UtcNow;
        var s = _pipeline?.Status;
        if (s is not null)
        {
            if (s.Heartbeats > 0)
                _firstHeartbeat ??= now;
            else
                _firstHeartbeat = null;
            if (s.Desyncs > _lastDesyncs)
                _lastDesyncAt = now;
            _lastDesyncs = s.Desyncs;
        }

        Status = TrackerStatus.Evaluate(new TrackerStatusInput(
            _npcapMissing,
            now - _lastDesyncAt < TimeSpan.FromSeconds(30),
            s?.Heartbeats ?? 0,
            s?.Connections.Count ?? 0,
            _firstHeartbeat,
            Collection.SnapshotAt,
            _petEventSeen,
            now));
    }

    // --- persistence --------------------------------------------------------------------------------------------

    public void Save()
    {
        try
        {
            _collectionStore.Save(Collection);
            _dirty = false;
            _lastSave = DateTime.UtcNow;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void SaveSettings()
    {
        try { _settingsStore.Save(Settings); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Publish(Collection.Flush());
        Save();
        SaveSettings();
        Interlocked.Exchange(ref _pipeline, null)?.Dispose();
    }
}
