using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using PetTracker.App.Infrastructure;
using PetTracker.Core;
using PetTracker.Core.Capture;
using PetTracker.Core.Diagnostics;
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

    /// <summary>Dungeon / Shop exclusive pets get a tag too.</summary>
    public bool IsDungeon { get; private set; }
    public bool IsShop { get; private set; }
    public string DungeonTooltip => PetSource.DungeonTooltip;
    public string ShopTooltip => PetSource.ShopTooltip;

    /// <summary>Unknown pets get a small "name" button.</summary>
    public bool ShowNameButton { get; private set; }

    public void Refresh()
    {
        Text = DomainEventFormatter.Describe(_event, _model.Catalog) ?? "";
        ShowNameButton = PetId is { } id && !_model.Catalog.HasName(id);
        IsSpecial = PetId is { } sid && _model.Catalog.IsSpecial(sid);
        IsDungeon = PetId is { } did && _model.Catalog.IsDungeon(did);
        IsShop = PetId is { } hid && _model.Catalog.IsShop(hid);
        Raise(nameof(IsSpecial));
        Raise(nameof(IsDungeon));
        Raise(nameof(IsShop));
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
    private OpcodeRegistry _registry;
    private readonly OpcodeRegistry _defaultRegistry;
    private readonly FrameBuffer _buffer = new();
    private readonly FramingDriftMonitor _drift = new();
    private int _lastFlowId = -1;
    private DateTime _lastDetectAt = DateTime.MinValue;
    private bool _detecting;
    private string? _dismissedDetection;
    private OpcodeDetection? _detection;
    private TrackerStatus _status = new(TrackerStatusKind.WaitingForGame, "Waiting for game");

    public TrackerModel(AppPaths paths)
    {
        _ui = Dispatcher.CurrentDispatcher;
        Paths = paths;
        _settingsStore = new SettingsStore(paths.Settings);
        _collectionStore = new CollectionStore(paths.Collection);
        _log = new EventLog(paths.Events);
        Settings = _settingsStore.Load();
        _defaultRegistry = OpcodeRegistry.LoadDefault();
        _registry = OpcodeOverrides.Merge(_defaultRegistry, OpcodeOverrides.Load(paths.Opcodes, out var opcodeError));
        Catalog = PetCatalog.LoadDefault(paths.Folder);
        NameList = PetNameList.LoadDefault();
        Collection = new PetCollection();
        Stats = new SessionStats(DateTime.UtcNow);
        _pinned = Settings.PinnedPets.ToHashSet();

        _collectionStore.LoadInto(Collection);
        Catalog.Changed += OnCatalogChanged;
        SyncRows();
        UpdateStatus();

        // Proves in the log that data/ was found next to the exe (also in single-file mode).
        Note("Startup", $"Aion2PetTracker {AppInfo.Version}; base {AppContext.BaseDirectory}; data folder {paths.Folder}; " +
                        $"{Catalog.KnownIds.Count()} pets in the catalog; opcodes {Describe(_registry)}");
        if (opcodeError is not null)
            Note("OpcodesIgnored", $"{paths.Opcodes} is not valid and was ignored: {opcodeError}");
    }

    public AppPaths Paths { get; }
    public AppSettings Settings { get; }

    /// <summary>The effective opcode map (defaults plus the user's opcodes.json). Replaced when the override changes.</summary>
    public OpcodeRegistry Registry => Volatile.Read(ref _registry);
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
            DiagLog.Info("Status", $"{_status.Kind} -> {value.Kind}: {value.Text}");
            _status = value;
            Raise();
            Raise(nameof(StatusTooltip));
            StatusChanged?.Invoke();
        }
    }

    /// <summary>Why the capture is not running (Npcap missing, bad adapter), or null.</summary>
    public string? CaptureProblem { get; private set; }
    public bool NpcapMissing => _npcapMissing;

    /// <summary>The running pipeline's counters, or null while it is not running.</summary>
    public PipelineStatus? CaptureStatus => _pipeline?.Status;

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
        DiagLog.Info("Capture", "starting capture");
        Task.Run(() =>
        {
            Pipeline? pipeline = null;
            try
            {
                pipeline = new Pipeline(options) { OpcodeName = OpcodeNameOf };
                pipeline.FramesDecoded += OnFrames;
                Interlocked.Exchange(ref _pipeline, pipeline)?.Dispose();
                pipeline.Start();
            }
            catch (NpcapNotAvailableException ex)
            {
                DiagLog.Error("Capture", "capture could not start: Npcap missing or unusable", ex);
                Interlocked.CompareExchange(ref _pipeline, null, pipeline);
                pipeline?.Dispose();
                _ui.BeginInvoke(() => { _npcapMissing = true; CaptureProblem = ex.Message; UpdateStatus(); });
            }
            catch (Exception ex)
            {
                DiagLog.Error("Capture", "capture could not start", ex);
                Interlocked.CompareExchange(ref _pipeline, null, pipeline);
                pipeline?.Dispose();
                _ui.BeginInvoke(() => { CaptureProblem = ex.Message; UpdateStatus(); });
            }
        });
    }

    private string? OpcodeNameOf(ushort opcode)
    {
        var registry = Registry;
        return registry.MessageNamesKnown.FirstOrDefault(n => registry.Is(n, opcode));
    }

    public void RestartCapture()
    {
        DiagLog.Info("Capture", "restart requested");
        _firstHeartbeat = null;
        _buffer.Clear();
        _lastFlowId = -1;
        _drift.Reset();
        StartCapture();
    }

    // --- opcodes: live reload, detection fallback, editor --------------------------------------------------------

    public event Action? OpcodesChanged;
    public event Action? DetectionChanged;

    public IReadOnlyDictionary<string, IReadOnlyList<ushort>> DefaultOpcodes => _defaultRegistry.ToDictionary();

    /// <summary>What opcodes.json overrides right now (read from disk), or empty.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ushort>> OpcodeOverridesOnDisk => OpcodeOverrides.Load(Paths.Opcodes, out _);

    /// <summary>Writes the override file (an empty map deletes it) and switches to the new map without a restart.</summary>
    public void SaveOpcodeOverrides(IReadOnlyDictionary<string, IReadOnlyList<ushort>> overrides)
    {
        OpcodeOverrides.Save(Paths.Opcodes, overrides);
        ReloadOpcodes("Opcodes saved");
    }

    /// <summary>Re-reads opcodes.json and swaps the registry live; the next frame already uses it.</summary>
    public string? ReloadOpcodes(string reason = "Opcodes reloaded")
    {
        var overrides = OpcodeOverrides.Load(Paths.Opcodes, out var error);
        Volatile.Write(ref _registry, OpcodeOverrides.Merge(_defaultRegistry, overrides));
        Note("OpcodesReloaded", $"{reason}: {Describe(_registry)}" + (error is null ? "" : $" (file ignored: {error})"));
        OpcodesChanged?.Invoke();
        UpdateStatus();
        return error;
    }

    /// <summary>The shape-based proposal waiting for a click on Apply, or null.</summary>
    public OpcodeDetection? PendingDetection
    {
        get => _detection;
        private set
        {
            _detection = value;
            Raise();
            DetectionChanged?.Invoke();
        }
    }

    public string DetectionSummary => _detection is null ? "" : SummaryOf(_detection);

    private static string SummaryOf(OpcodeDetection detection) =>
        string.Join(", ", detection.Mapping().Where(kv => kv.Key != MessageNames.Heartbeat)
            .Select(kv => $"{kv.Key} {OpcodeOverrides.FormatList(kv.Value)}"));

    public void DismissDetection()
    {
        if (_detection is null)
            return;
        _dismissedDetection = DetectionSummary;
        Note("OpcodeDetectionDismissed", _dismissedDetection);
        PendingDetection = null;
    }

    /// <summary>Called on the UI thread every tick: when the status is "Unknown version", looks at the buffered frames by shape.</summary>
    private void MaybeDetect(DateTime now)
    {
        if (_detecting || _detection is not null || Status.Kind != TrackerStatusKind.UnknownVersion
            || now - _lastDetectAt < TimeSpan.FromSeconds(30))
            return;
        var frames = _buffer.Snapshot();
        if (frames.Count == 0)
            return;
        _detecting = true;
        _lastDetectAt = now;
        var catalog = Catalog;
        Task.Run(() =>
        {
            OpcodeDetection? found = null;
            try { found = new PetOpcodeDetector(catalog).Detect(frames); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                DiagLog.Warn("Detect", "opcode detection failed", ex);
            }
            _ui.BeginInvoke(() =>
            {
                _detecting = false;
                if (_disposed || found is null)
                    return;
                Note("OpcodeDetection", $"examined {found.FramesExamined} buffered frames; snapshot confidence {found.SnapshotConfidence:0.00}; " +
                                        string.Join("; ", found.Candidates.Select(c => $"{c.LogicalName} {c.OpcodeHex} {c.Confidence:0.00}")));
                if (!found.HasConfidentSnapshot)
                    return;
                if (SummaryOf(found) != _dismissedDetection)
                    PendingDetection = found;
            });
        });
    }

    /// <summary>
    /// The Apply click: merges the proposal into opcodes.json, reloads the registry and re-processes the buffered frames so the
    /// snapshot loads now. Frames the old map already handled are not applied twice, except that everything from the snapshot on is
    /// replayed when the snapshot itself was not understood before (the snapshot replaces the state, so what follows belongs on top).
    /// </summary>
    public void ApplyDetection()
    {
        if (_detection is not { } detection)
            return;
        var plan = OpcodeRepair.Plan(detection, _defaultRegistry, OpcodeOverridesOnDisk, _buffer.Snapshot());
        SaveOpcodeOverrides(plan.Overrides);
        Note("OpcodeDetectionApplied", $"user clicked Apply: {DetectionSummary}");

        var replayed = 0;
        foreach (var f in plan.FramesToReplay)
        {
            var messages = PetMessageParser.Extract(Registry, [f.Frame]).ToList();
            if (messages.Count == 0)
                continue;
            replayed++;
            ApplyMessages(messages, new DateTime(f.Ticks, DateTimeKind.Utc));
        }
        Note("OpcodeDetectionReplay", $"re-processed {replayed} buffered pet frame(s)");
        PendingDetection = null;
        _dismissedDetection = null;
    }

    private static string Describe(OpcodeRegistry registry) => string.Join(", ", registry.MessageNamesKnown.Order()
        .Select(n => $"{n}={OpcodeOverrides.FormatList(registry.OpcodesFor(n))}"));

    /// <summary>Appends a system line to events.jsonl.</summary>
    public void Note(string kind, string message)
    {
        DiagLog.Info("Note." + kind, message);
        try { _log.Append(new SystemNote(DateTime.UtcNow, kind, message)); }
        catch (IOException ex) { DiagLog.Warn("Persist", "events.jsonl could not be written", ex); }
        catch (UnauthorizedAccessException ex) { DiagLog.Warn("Persist", "events.jsonl could not be written", ex); }
    }

    /// <summary>A text block describing the tracker's current state, for the diagnostic report. Contains no identifying data.</summary>
    public string DiagnosticSnapshot()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"tracker status  : {Status.Kind} - {Status.Text}");
        sb.AppendLine($"capture problem : {CaptureProblem ?? "none"}; Npcap missing flag {_npcapMissing}");
        if (_pipeline?.Status is { } s)
        {
            sb.AppendLine($"pipeline phase  : {s.Phase}{(s.Error is null ? "" : "; error: " + s.Error)}");
            sb.AppendLine($"capture         : {s.Capture}");
            sb.AppendLine($"counters        : packets {s.Packets}, s2c bytes {s.BytesS2C}, frames {s.Frames} (inner {s.InnerFrames}, bundles {s.Bundles}), " +
                          $"heartbeats {s.Heartbeats}, desyncs {s.Desyncs}, gaps {s.Gaps}, flows rejected {s.FlowsRejected}");
            sb.AppendLine($"game connections: {s.Connections.Count}; active flow {s.ActiveFlowId?.ToString() ?? "none"}");
            sb.AppendLine($"relay-like flows: {s.Relay.Describe()}");
        }
        else
        {
            sb.AppendLine("pipeline        : not running");
        }
        sb.AppendLine($"collection      : {Collection.Pets.Count} pets known; snapshot at {(Collection.SnapshotAt is { } at ? at.ToString("u") : "never")}; pet event seen {_petEventSeen}");
        sb.AppendLine($"opcodes         : {Describe(_registry)}");
        sb.AppendLine($"catalog         : {Catalog.KnownIds.Count()} pets");
        sb.AppendLine($"settings        : adapter override {(Settings.AdapterOverride is null ? "not set" : "set")}, close to tray {Settings.CloseToTray}, " +
                      $"overlay visible {Settings.OverlayVisible}, click-through {Settings.ClickThrough}, scale {Settings.Scale}, opacity {Settings.Opacity}");
        return sb.ToString();
    }

    // --- framing drift --------------------------------------------------------------------------------------------

    /// <summary>Extra status-tooltip line when desyncs keep coming (the frame format may have changed), else null.</summary>
    /// <summary>Parallel heartbeat-only connections seen (a ping reducer or VPN), or the default value when none.</summary>
    public RelayInfo Relay => _pipeline?.Status.Relay ?? default;

    public string? FramingHint => _drift.Suspected ? FramingDriftMonitor.Hint : null;

    public string StatusTooltip => _drift.Suspected
        ? $"{Status.Text}\n{FramingDriftMonitor.Hint}"
        : Status.Text;

    private void OnFrames(DecodedFrames decoded)
    {
        // Pipeline thread: parse only, apply on the UI thread.
        var registry = Registry;
        if (decoded.FlowId != _lastFlowId)
        {
            _lastFlowId = decoded.FlowId; // a new connection: only its own traffic is worth keeping
            _buffer.Clear();
        }
        _buffer.Add(decoded.Ticks, decoded.Frames, f => registry.Is(MessageNames.Heartbeat, f.Opcode));

        var errors = new List<string>();
        var messages = PetMessageParser.Extract(registry, decoded.Frames, errors.Add).ToList();
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
            if (e is Inconsistency inconsistency)
                DiagLog.Warn("Domain", $"inconsistency (pet {inconsistency.PetId?.ToString() ?? "-"}, needs resync {inconsistency.NeedsResync}): {inconsistency.Message}");
            else if (e is SnapshotLoaded loaded)
                DiagLog.Info("Domain", $"snapshot loaded: {loaded.Count} pets ({loaded.TotalKnown} known in the catalog)");
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
        MaybeDetect(now);
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
            {
                _lastDesyncAt = now;
            }
            else if (s.Desyncs < _lastDesyncs && s.Relay.Healthy)
            {
                // Relay-like flows were dropped because the game data decodes cleanly elsewhere: their desyncs no longer count.
                _lastDesyncAt = DateTime.MinValue;
                var wasSuspected = _drift.Suspected;
                _drift.Rebase(s.Desyncs);
                DiagLog.Info("Status", $"desync count fell to {s.Desyncs} after dropping relay-like flows; desync warning cleared");
                if (wasSuspected)
                {
                    Raise(nameof(StatusTooltip));
                    Raise(nameof(FramingHint));
                }
            }
            _lastDesyncs = s.Desyncs;
            if (_drift.Observe(s.Desyncs, now))
            {
                Note("FramingDrift", $"{_drift.Threshold}+ desyncs within {_drift.Window.TotalSeconds:0} s: {FramingDriftMonitor.Hint}");
                Raise(nameof(StatusTooltip));
                Raise(nameof(FramingHint));
                StatusChanged?.Invoke();
            }
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
        catch (IOException ex) { DiagLog.Warn("Persist", "collection.json could not be saved", ex); }
        catch (UnauthorizedAccessException ex) { DiagLog.Warn("Persist", "collection.json could not be saved", ex); }
    }

    public void SaveSettings()
    {
        try { _settingsStore.Save(Settings); }
        catch (IOException ex) { DiagLog.Warn("Persist", "settings.json could not be saved", ex); }
        catch (UnauthorizedAccessException ex) { DiagLog.Warn("Persist", "settings.json could not be saved", ex); }
    }

    // --- import / restore ---------------------------------------------------------------------------------------

    /// <summary>Everything a backup file holds, as it is right now.</summary>
    public Backup CreateBackup() => BackupFile.Create(Collection.Pets, Catalog.Overrides, Settings);

    /// <summary>Writes backups\auto-yyyyMMdd-HHmmss.json before an import or a restore replaces anything. Returns the path.</summary>
    public string WriteSafetyBackup()
    {
        var path = Path.Combine(Paths.Backups, $"auto-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        BackupFile.Write(path, CreateBackup());
        return path;
    }

    /// <summary>Replaces the whole collection (PetSouls import, backup restore) and saves it at once.</summary>
    public void ReplaceCollection(IEnumerable<PetState> states, string reason)
    {
        Publish(Collection.Flush());
        Collection.Restore(states);
        SyncRows();
        foreach (var row in _rows.Values)
            row.RefreshSession();
        Save();
        Note("CollectionReplaced", reason);
    }

    /// <summary>Takes over a restored backup's settings. The overlay's position and size stay as they are on this PC.</summary>
    public void ApplySettings(AppSettings s)
    {
        Opacity = s.Opacity;
        Scale = s.Scale;
        ClickThrough = s.ClickThrough;
        AdapterOverride = s.AdapterOverride;
        Settings.Hotkeys = s.Hotkeys ?? new HotkeySettings();
        Settings.PetSouls = s.PetSouls;

        _pinned.Clear();
        _pinned.UnionWith(s.PinnedPets ?? []);
        Settings.PinnedPets = _pinned.Order().ToList();
        foreach (var row in _rows.Values)
            row.RefreshPin();
        PinsChanged?.Invoke();
        SaveSettings();
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
