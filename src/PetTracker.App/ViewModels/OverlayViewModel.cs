using System.Collections.ObjectModel;
using System.Windows.Media;
using PetTracker.App.Infrastructure;
using PetTracker.Core.Domain;

namespace PetTracker.App.ViewModels;

public sealed class OverlayViewModel : ObservableObject
{
    public OverlayViewModel(TrackerModel model)
    {
        Model = model;
        model.StatusChanged += () => { Raise(nameof(StatusText)); Raise(nameof(StatusBrush)); };
        model.SessionChanged += () => { Raise(nameof(SessionText)); Raise(nameof(SoulsText)); Raise(nameof(RateText)); };
        model.PinsChanged += RebuildPinned;
        model.Catalog.Changed += RebuildPinned;
        RebuildPinned();
    }

    public TrackerModel Model { get; }
    public ObservableCollection<FeedItem> Feed => Model.Feed;
    public ObservableCollection<PetRowViewModel> Pinned { get; } = [];

    public string StatusText => Model.Status.Text;

    public Brush StatusBrush => Model.Status.Kind switch
    {
        TrackerStatusKind.SnapshotLoaded => Frozen(0x2E, 0xA0, 0x5A),
        TrackerStatusKind.Connected => Frozen(0x3B, 0x82, 0xC4),
        TrackerStatusKind.WaitingForGame => Frozen(0x6B, 0x72, 0x80),
        TrackerStatusKind.UnknownVersion => Frozen(0xC9, 0x8A, 0x1B),
        _ => Frozen(0xC0, 0x3A, 0x3A),
    };

    public string SoulsText => $"Souls {Model.Stats.SoulsThisSession}";
    public string RateText => $"{Model.Stats.SoulsPerHour(DateTime.UtcNow):0.#}/h";
    public string SessionText => $"{SoulsText}  |  {RateText}";
    public bool HasPinned => Pinned.Count > 0;
    public bool HasFeed => Feed.Count > 0;

    private void RebuildPinned()
    {
        Pinned.Clear();
        foreach (var row in Model.PinnedRows)
            Pinned.Add(row);
        Raise(nameof(HasPinned));
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
