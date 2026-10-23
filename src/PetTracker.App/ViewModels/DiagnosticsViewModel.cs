using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using PetTracker.App.Infrastructure;
using PetTracker.Core.Diagnostics;
using PetTracker.Core.Protocol;
using PetTracker.Core.Domain;

namespace PetTracker.App.ViewModels;

/// <summary>The Settings > Diagnostics tab: live status, saving or copying the support report, and the log preview.</summary>
public sealed class DiagnosticsViewModel : ObservableObject
{
    private const int PreviewLines = 200;

    private readonly TrackerModel _model;
    private string _message = "", _preview = "";
    private IReadOnlyList<StatusChip> _status = [];
    private bool _messageIsError;

    public DiagnosticsViewModel(TrackerModel model)
    {
        _model = model;
        SaveReportCommand = new RelayCommand(SaveReport);
        CopyReportCommand = new RelayCommand(CopyReport);
        OpenLogsFolderCommand = new RelayCommand(() =>
        {
            if (!ExternalLinks.Open(model.Paths.Logs, model.Paths.Folder, logsFolder: model.Paths.Logs))
                Report("Could not open the logs folder.", error: true);
        });
        RefreshCommand = new RelayCommand(Refresh);
        ClearLogsCommand = new RelayCommand(() =>
        {
            DiagLog.Clear();
            DiagLog.Info("Diag", "log files cleared by the user");
            Refresh();
            Report("Log files deleted. New activity is logged from now on.");
        });
        model.StatusChanged += RefreshStatusIfVisible;
        Refresh();
    }

    public ICommand SaveReportCommand { get; }
    public ICommand CopyReportCommand { get; }
    public ICommand OpenLogsFolderCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ClearLogsCommand { get; }

    public string LogsFolder => _model.Paths.Logs;
    public IReadOnlyList<StatusChip> Chips { get => _status; private set => Set(ref _status, value); }
    public string Preview { get => _preview; private set => Set(ref _preview, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public bool MessageIsError { get => _messageIsError; private set => Set(ref _messageIsError, value); }

    private void RefreshStatusIfVisible() => Chips = BuildStatus();

    /// <summary>Re-reads the live status and the last lines of the log (called when the tab opens and by the Refresh button).</summary>
    public void Refresh()
    {
        Chips = BuildStatus();
        Preview = DiagReport.Tail(LogsFolder, PreviewLines);
    }

    private IReadOnlyList<StatusChip> BuildStatus()
    {
        var capture = _model.CaptureStatus;
        var tracker = _model.Status;
        return
        [
            new("Tracker", tracker.Text, tracker.Kind switch
            {
                TrackerStatusKind.SnapshotLoaded => ChipLevel.Good,
                TrackerStatusKind.Connected => ChipLevel.Warn,
                TrackerStatusKind.WaitingForGame => ChipLevel.Neutral,
                _ => ChipLevel.Bad,
            }),
            _model.NpcapMissing ? new("Npcap", "Not found", ChipLevel.Bad) : new("Npcap", "Found", ChipLevel.Good),
            _model.CaptureProblem is { } problem ? new("Capture", "Problem", ChipLevel.Bad, Redacted(problem))
                : capture is null ? new("Capture", "Not running", ChipLevel.Warn)
                : new("Capture", "Running", ChipLevel.Good),
            capture is null ? new("Game", "Unknown", ChipLevel.Neutral)
                : capture.Connections.Count > 0 ? new("Game", "Connected", ChipLevel.Good)
                : new("Game", "Not detected", ChipLevel.Neutral),
            capture is null ? new("Game data", "None", ChipLevel.Neutral)
                : capture.Heartbeats > 0 ? new("Game data", "Receiving", ChipLevel.Good)
                : new("Game data", "None yet", ChipLevel.Neutral),
            RelayChip(_model.Relay),
        ];
    }

    private static StatusChip RelayChip(RelayInfo relay) =>
        !relay.Detected ? new("Network", "No relays seen", ChipLevel.Neutral)
        : relay.Healthy ? new("Network", "Relays ignored", ChipLevel.Info,
            $"{relay.Flows} extra heartbeat-only connections{(relay.Identical ? " with matching byte counts" : "")}: a ping reducer or VPN seems to be active. " +
            "The tracker ignores them and reads the game's own connection, so the Desync label does not apply to them.")
        : new("Network", "Relays seen", ChipLevel.Warn,
            $"{relay.Flows} extra heartbeat-only connections (a ping reducer or VPN seems to be active). Log in to a character so the tracker can find the game's own connection.");

    private static string Redacted(string text) => DiagLog.Sanitizer?.Sanitize(text) ?? text;

    private string BuildReport() =>
        DiagReport.Build(LogsFolder, _model.DiagnosticSnapshot(), DiagLog.Sanitizer ?? new LogSanitizer());

    private void SaveReport()
    {
        try
        {
            var path = Dialogs.SaveText("Save diagnostic report", $"pettracker-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt");
            if (path is null)
                return;
            File.WriteAllText(path, BuildReport(), new UTF8Encoding(false));
            DiagLog.Info("Diag", "diagnostic report saved by the user");
            Report("Saved. Attach that file to your support request.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagLog.Warn("Diag", "diagnostic report could not be saved", ex);
            Report("Could not save the report: " + ex.Message, error: true);
        }
    }

    private void CopyReport()
    {
        try
        {
            Clipboard.SetText(BuildReport());
            DiagLog.Info("Diag", "diagnostic report copied by the user");
            Report("Copied to the clipboard. Paste it into your support message or a text file.");
        }
        catch (ExternalException)
        {
            Report("Could not copy: the clipboard is busy. Try again.", error: true);
        }
    }

    private void Report(string text, bool error = false)
    {
        MessageIsError = error;
        Message = text;
    }
}

public enum ChipLevel { Neutral, Good, Info, Warn, Bad }

/// <summary>One row of the status card: a label and a colored chip. Level is a string so XAML triggers can match it.</summary>
public sealed record StatusChip(string Label, string Text, ChipLevel Level, string? Detail = null)
{
    public string LevelName => Level.ToString();
    public bool HasDetail => Detail is not null;
}
