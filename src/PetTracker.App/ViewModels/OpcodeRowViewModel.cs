using PetTracker.App.Infrastructure;
using PetTracker.Core.Protocol;

namespace PetTracker.App.ViewModels;

/// <summary>One logical message in the opcode editor: its default opcodes, the user's override text and what is in effect.</summary>
public sealed class OpcodeRowViewModel(string name, IReadOnlyList<ushort> defaults, IReadOnlyList<ushort>? overridden) : ObservableObject
{
    private string _overrideText = overridden is null ? "" : OpcodeOverrides.FormatList(overridden);
    private string? _error;

    public string Name { get; } = name;
    public string DefaultText { get; } = OpcodeOverrides.FormatList(defaults);

    /// <summary>Hex opcodes, comma separated (for example "0990, 1490"). Empty means "use the default".</summary>
    public string OverrideText
    {
        get => _overrideText;
        set
        {
            if (Set(ref _overrideText, value))
            {
                Validate();
                Raise(nameof(EffectiveText));
            }
        }
    }

    public string EffectiveText => ParsedOverride() is { } list ? OpcodeOverrides.FormatList(list) : DefaultText;

    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    /// <summary>The parsed override, or null when the box is empty or invalid.</summary>
    public IReadOnlyList<ushort>? ParsedOverride()
    {
        if (string.IsNullOrWhiteSpace(_overrideText))
            return null;
        try
        {
            var list = OpcodeOverrides.ParseList(_overrideText);
            return list.Count == 0 ? null : list;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Checks the text; sets <see cref="Error"/> and returns whether it is fine.</summary>
    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(_overrideText))
        {
            Error = null;
            return true;
        }
        try
        {
            if (OpcodeOverrides.ParseList(_overrideText).Count == 0)
            {
                Error = "Enter at least one opcode, or clear the box to use the default.";
                return false;
            }
            Error = null;
            return true;
        }
        catch (FormatException ex)
        {
            Error = ex.Message;
            return false;
        }
    }

    public void SetError(string message) => Error = message;
}
