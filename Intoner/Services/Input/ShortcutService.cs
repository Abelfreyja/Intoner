using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace Intoner.Services.Input;

/// <summary> defines one discoverable command shortcut and its default gesture </summary>
internal sealed record ShortcutDefinition(
    string Id,
    string Name,
    string Description,
    KeyboardGesture DefaultGesture);

/// <summary> contributes shortcut definitions owned by one plugin feature </summary>
internal interface IShortcutProvider
{
    /// <summary> gets the shortcuts owned by this feature </summary>
    IReadOnlyList<ShortcutDefinition> Shortcuts { get; }
}

/// <summary> resolves and evaluates registered command shortcuts </summary>
internal interface IShortcutService
{
    /// <summary> gets every registered shortcut in provider order </summary>
    IReadOnlyList<ShortcutDefinition> Shortcuts { get; }

    /// <summary> gets the gesture currently assigned to a shortcut </summary>
    KeyboardGesture GetGesture(ShortcutDefinition shortcut);

    /// <summary> checks whether a shortcut was pressed without repeating while held </summary>
    bool IsPressed(ShortcutDefinition shortcut);

    /// <summary> enables or disables one shortcut for the current editor context </summary>
    void SetActive(ShortcutDefinition shortcut, bool active);

    /// <summary> disables every shortcut in the current editor context </summary>
    void DeactivateAll();

    /// <summary> clears every queued shortcut transition </summary>
    void ClearPending();
}

/// <summary> owns shortcut discovery, validation, and input evaluation </summary>
internal sealed class ShortcutService : IShortcutService, IDisposable
{
    private const KeyboardModifiers AllModifiers = KeyboardModifiers.Control
        | KeyboardModifiers.Shift
        | KeyboardModifiers.Alt;

    private readonly IReadOnlyDictionary<string, ShortcutDefinition> _shortcutsById;
    private readonly IKeyboardInputService _keyboardInput;
    private readonly Dictionary<string, IKeyboardInputLease> _activeShortcuts = new(StringComparer.Ordinal);

    public ShortcutService(IEnumerable<IShortcutProvider> providers, IKeyboardInputService keyboardInput)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(keyboardInput);

        List<ShortcutDefinition> shortcuts = [];
        Dictionary<string, ShortcutDefinition> byId = new(StringComparer.Ordinal);
        foreach (IShortcutProvider provider in providers)
        {
            foreach (ShortcutDefinition shortcut in provider.Shortcuts)
            {
                Validate(shortcut);
                if (!byId.TryAdd(shortcut.Id, shortcut))
                {
                    throw new InvalidOperationException($"duplicate shortcut id '{shortcut.Id}'");
                }

                shortcuts.Add(shortcut);
            }
        }

        Shortcuts = shortcuts.AsReadOnly();
        _shortcutsById = byId;
        _keyboardInput = keyboardInput;
    }

    public IReadOnlyList<ShortcutDefinition> Shortcuts { get; }

    public KeyboardGesture GetGesture(ShortcutDefinition shortcut)
        => Resolve(shortcut).DefaultGesture;

    public bool IsPressed(ShortcutDefinition shortcut)
    {
        ShortcutDefinition registered = Resolve(shortcut);
        return _activeShortcuts.TryGetValue(registered.Id, out IKeyboardInputLease? lease)
            && lease.ConsumePressedCount(registered.DefaultGesture) > 0;
    }

    public void SetActive(ShortcutDefinition shortcut, bool active)
    {
        ShortcutDefinition registered = Resolve(shortcut);
        bool shouldSuppress = active && !_keyboardInput.IsTextInputActive;
        if (shouldSuppress == _activeShortcuts.ContainsKey(registered.Id))
        {
            return;
        }

        if (shouldSuppress)
        {
            _activeShortcuts.Add(
                registered.Id,
                _keyboardInput.BeginSuppression([registered.DefaultGesture]));
            return;
        }

        Deactivate(registered.Id);
    }

    public void ClearPending()
    {
        foreach (IKeyboardInputLease lease in _activeShortcuts.Values)
        {
            lease.ClearPending();
        }
    }

    public void DeactivateAll()
    {
        foreach (IKeyboardInputLease lease in _activeShortcuts.Values)
        {
            lease.ClearPending();
            lease.Dispose();
        }

        _activeShortcuts.Clear();
    }

    public void Dispose()
        => DeactivateAll();

    private static void Validate(ShortcutDefinition shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        if (string.IsNullOrWhiteSpace(shortcut.Id)
            || !string.Equals(shortcut.Id, shortcut.Id.Trim(), StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(shortcut.Name)
            || string.IsNullOrWhiteSpace(shortcut.Description)
            || shortcut.DefaultGesture.Key == SeVirtualKey.NO_KEY
            || !Enum.IsDefined(shortcut.DefaultGesture.Key)
            || (shortcut.DefaultGesture.Modifiers & ~AllModifiers) != KeyboardModifiers.None)
        {
            throw new InvalidOperationException("shortcut definitions require an id, human text, and a valid default gesture");
        }
    }

    private ShortcutDefinition Resolve(ShortcutDefinition shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        return _shortcutsById.TryGetValue(shortcut.Id, out ShortcutDefinition? registered)
            ? registered
            : throw new ArgumentException($"shortcut '{shortcut.Id}' is not registered", nameof(shortcut));
    }

    private void Deactivate(string shortcutId)
    {
        if (!_activeShortcuts.Remove(shortcutId, out IKeyboardInputLease? lease))
        {
            return;
        }

        lease.ClearPending();
        lease.Dispose();
    }
}
