using Intoner.Services.Configuration;
using Intoner.UI;

namespace Intoner.Services.Input;

/// <summary> defines one discoverable shortcut and its default gesture </summary>
/// <param name="Id"> the stable id used for saved overrides </param>
/// <param name="Name"> the display name </param>
/// <param name="Description"> the shortcut's behavior </param>
/// <param name="DefaultGesture"> the binding used when no override is saved </param>
/// <param name="AllowAdditionalModifiers"> allow the shortcut to combine with other held modifiers </param>
internal sealed record ShortcutDefinition(
    string Id,
    string Name,
    string Description,
    KeyboardGesture DefaultGesture,
    bool AllowAdditionalModifiers = false);

/// <summary> contributes shortcut definitions owned by one plugin feature </summary>
internal interface IShortcutProvider
{
    /// <summary> gets the shortcuts owned by this feature </summary>
    IReadOnlyList<ShortcutDefinition> Shortcuts { get; }
}

/// <summary> the result of saving a shortcut binding </summary>
internal enum ShortcutBindingUpdateResult
{
    Updated,
    InvalidGesture,
    Conflict,
    SaveFailed,
}

/// <summary> resolves and evaluates registered command shortcuts </summary>
internal interface IShortcutService
{
    /// <summary> gets every registered shortcut in provider order </summary>
    IReadOnlyList<ShortcutDefinition> Shortcuts { get; }

    /// <summary> gets the gesture currently assigned to a shortcut </summary>
    /// <returns> the default binding unless a custom one is set; null if disabled or another binding overlaps its combination </returns>
    KeyboardGesture? GetGesture(ShortcutDefinition shortcut);

    /// <summary> changes the shortcut's key combination, or disables it when <paramref name="gesture"/> is null </summary>
    /// <remarks> rejects combinations that overlap another shortcut, including held shortcuts that allow extra modifiers </remarks>
    /// <param name="shortcut"> a registered shortcut, matched by its stable id </param>
    /// <param name="gesture"> the new key combination, or null to disable the shortcut </param>
    /// <param name="conflict"> the shortcut with an overlapping key combination, or null if none </param>
    /// <returns> <see cref="ShortcutBindingUpdateResult.Updated"/> if saved or already unchanged; otherwise, why the change was rejected </returns>
    ShortcutBindingUpdateResult SetGesture(ShortcutDefinition shortcut, KeyboardGesture? gesture, out ShortcutDefinition? conflict);

    /// <summary> removes the saved override and restores the shortcut's default key combination </summary>
    /// <param name="shortcut"> a registered shortcut, matched by its stable id </param>
    /// <param name="conflict"> the shortcut using the default key combination, or null if none </param>
    /// <returns> the same result as <see cref="SetGesture"/> </returns>
    ShortcutBindingUpdateResult ResetGesture(ShortcutDefinition shortcut, out ShortcutDefinition? conflict);

    /// <summary> checks whether a shortcut was pressed without repeating while held </summary>
    bool IsPressed(ShortcutDefinition shortcut);

    /// <summary> checks whether the effective binding is held </summary>
    bool IsHeld(ShortcutDefinition shortcut);

    /// <summary> enables or disables input capture for one shortcut in the current editor context </summary>
    /// <remarks> does not register disabled or conflicting bindings with the input service and has no effect after disposal </remarks>
    void SetActive(ShortcutDefinition shortcut, bool active);

    /// <summary> ends input capture for every shortcut in the current editor context </summary>
    void DeactivateAll();

    /// <summary> clears every queued shortcut transition without interrupting held bindings </summary>
    void ClearPending();
}

/// <summary> owns shortcut discovery, validation, and input evaluation </summary>
internal sealed class ShortcutService : IShortcutService, IDisposable
{
    private sealed class ShortcutBinding(ShortcutDefinition definition)
    {
        public ShortcutDefinition Definition { get; } = definition;
        public KeyboardGesture? Gesture { get; set; } = definition.DefaultGesture;
        public bool HasConflict { get; set; }
        public KeyboardGesture? EffectiveGesture => HasConflict ? null : Gesture;
        public IKeyboardInputLease? Lease { get; set; }
    }

    private readonly IReadOnlyDictionary<string, ShortcutBinding> _bindingsById;
    private readonly IKeyboardInputService _keyboardInput;
    private readonly IIntonerConfigurationService _configuration;
    private int _configurationRevision = 1;
    private int _appliedConfigurationRevision;
    private bool _disposed;

    public ShortcutService(
        IEnumerable<IShortcutProvider> providers,
        IKeyboardInputService keyboardInput,
        IIntonerConfigurationService configuration)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(keyboardInput);
        ArgumentNullException.ThrowIfNull(configuration);

        List<ShortcutDefinition> shortcuts = [];
        Dictionary<string, ShortcutBinding> byId = new(StringComparer.Ordinal);
        foreach (IShortcutProvider provider in providers)
        {
            foreach (ShortcutDefinition shortcut in provider.Shortcuts)
            {
                Validate(shortcut);

                if (!byId.TryAdd(shortcut.Id, new ShortcutBinding(shortcut)))
                {
                    throw new InvalidOperationException($"duplicate shortcut id '{shortcut.Id}'");
                }

                if (shortcuts.Any(other => Conflicts(shortcut, shortcut.DefaultGesture, other, other.DefaultGesture)))
                {
                    throw new InvalidOperationException($"conflicting default gesture for shortcut '{shortcut.Id}'");
                }

                shortcuts.Add(shortcut);
            }
        }

        Shortcuts = shortcuts.AsReadOnly();
        _bindingsById = byId;
        _keyboardInput = keyboardInput;
        _configuration = configuration;
        _configuration.ConfigurationChanged += HandleConfigurationChanged;
    }

    public IReadOnlyList<ShortcutDefinition> Shortcuts { get; }

    public KeyboardGesture? GetGesture(ShortcutDefinition shortcut)
    {
        ShortcutBinding binding = Resolve(shortcut);
        RefreshBindings();
        return binding.EffectiveGesture;
    }

    public ShortcutBindingUpdateResult SetGesture(
        ShortcutDefinition shortcut,
        KeyboardGesture? gesture,
        out ShortcutDefinition? conflict)
    {
        ShortcutBinding binding = Resolve(shortcut);
        conflict = null;

        if (gesture is { } value && !value.IsValid())
        {
            return ShortcutBindingUpdateResult.InvalidGesture;
        }

        ShortcutBindingUpdateResult result = ShortcutBindingUpdateResult.SaveFailed;
        ShortcutDefinition? conflictingShortcut = null;
        bool committed = _configuration.TryUpdateIf(configuration =>
        {
            Dictionary<string, KeyboardGesture?> overrides = configuration.Ui.ShortcutOverrides;

            if (gesture.HasValue)
            {
                ShortcutBinding? other = _bindingsById.Values.FirstOrDefault(other => other != binding
                    && ReadGesture(other, overrides) is { } assigned
                    && Conflicts(binding.Definition, gesture.Value, other.Definition, assigned));

                if (other is not null)
                {
                    conflictingShortcut = other.Definition;
                    result = ShortcutBindingUpdateResult.Conflict;
                    return false;
                }
            }

            bool useDefault = gesture == binding.Definition.DefaultGesture;
            bool hasOverride = overrides.TryGetValue(binding.Definition.Id, out KeyboardGesture? current);

            if (useDefault ? !hasOverride : hasOverride && current == gesture)
            {
                result = ShortcutBindingUpdateResult.Updated;
                return false;
            }

            if (useDefault)
            {
                overrides.Remove(binding.Definition.Id);
            }
            else
            {
                overrides[binding.Definition.Id] = gesture;
            }

            return true;
        });
        conflict = conflictingShortcut;

        if (committed)
        {
            result = ShortcutBindingUpdateResult.Updated;
        }

        RefreshBindings();
        return result;
    }

    public ShortcutBindingUpdateResult ResetGesture(ShortcutDefinition shortcut, out ShortcutDefinition? conflict)
        => SetGesture(shortcut, Resolve(shortcut).Definition.DefaultGesture, out conflict);

    public bool IsPressed(ShortcutDefinition shortcut)
    {
        ShortcutBinding binding = Resolve(shortcut);
        RefreshBindings();
        return binding.EffectiveGesture is { } gesture && binding.Lease is { } lease
            && lease.ConsumePressedCount(gesture) > 0;
    }

    public bool IsHeld(ShortcutDefinition shortcut)
    {
        ShortcutBinding binding = Resolve(shortcut);

        if (_disposed)
        {
            return false;
        }

        RefreshBindings();

        if (binding.EffectiveGesture is not { } gesture)
        {
            return false;
        }

        if (gesture.ImplicitModifier != KeyboardModifiers.None)
        {
            return UiKeyboard.AreModifiersDown(gesture.Modifiers | gesture.ImplicitModifier);
        }

        if (_keyboardInput.IsTextInputActive)
        {
            Deactivate(binding);
            return false;
        }

        return binding.Lease is { } lease && lease.IsHeld(gesture);
    }

    public void SetActive(ShortcutDefinition shortcut, bool active)
    {
        ShortcutBinding binding = Resolve(shortcut);

        if (_disposed)
        {
            return;
        }

        RefreshBindings();

        if (!active || binding.EffectiveGesture is not { } gesture || _keyboardInput.IsTextInputActive)
        {
            Deactivate(binding);
            return;
        }

        binding.Lease ??= _keyboardInput.BeginSuppression([gesture], binding.Definition.AllowAdditionalModifiers);
    }

    public void ClearPending()
    {
        foreach (ShortcutBinding binding in _bindingsById.Values)
        {
            binding.Lease?.ClearPending();
        }
    }

    public void DeactivateAll()
    {
        foreach (ShortcutBinding binding in _bindingsById.Values)
        {
            Deactivate(binding);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _configuration.ConfigurationChanged -= HandleConfigurationChanged;
        DeactivateAll();
    }

    private void HandleConfigurationChanged()
        => Interlocked.Increment(ref _configurationRevision);

    private void RefreshBindings()
    {
        int revision = Volatile.Read(ref _configurationRevision);

        if (revision == _appliedConfigurationRevision)
        {
            return;
        }

        Dictionary<string, KeyboardGesture?> overrides = _configuration.Current.Ui.ShortcutOverrides;
        List<(ShortcutBinding Binding, KeyboardGesture Gesture)> assigned = [];
        foreach (ShortcutBinding binding in _bindingsById.Values)
        {
            KeyboardGesture? gesture = ReadGesture(binding, overrides);

            if (binding.Gesture != gesture)
            {
                Deactivate(binding);
            }

            binding.Gesture = gesture;
            binding.HasConflict = false;

            if (gesture is not { } value)
            {
                continue;
            }

            foreach ((ShortcutBinding other, KeyboardGesture otherGesture) in assigned)
            {
                if (!Conflicts(binding.Definition, value, other.Definition, otherGesture))
                {
                    continue;
                }

                binding.HasConflict = other.HasConflict = true;
                Deactivate(binding);
                Deactivate(other);
            }

            assigned.Add((binding, value));
        }

        _appliedConfigurationRevision = revision;
    }

    private static KeyboardGesture? ReadGesture(ShortcutBinding binding, IReadOnlyDictionary<string, KeyboardGesture?> overrides)
        => overrides.TryGetValue(binding.Definition.Id, out KeyboardGesture? gesture) ? gesture : binding.Definition.DefaultGesture;

    private static bool Conflicts(ShortcutDefinition first, KeyboardGesture firstGesture,
        ShortcutDefinition second, KeyboardGesture secondGesture)
    {
        KeyboardModifiers combined = firstGesture.Modifiers | firstGesture.ImplicitModifier
            | secondGesture.Modifiers | secondGesture.ImplicitModifier;
        return firstGesture.Key == secondGesture.Key
            && firstGesture.MatchesModifiers(combined, first.AllowAdditionalModifiers || firstGesture.ImplicitModifier != KeyboardModifiers.None)
            && secondGesture.MatchesModifiers(combined, second.AllowAdditionalModifiers || secondGesture.ImplicitModifier != KeyboardModifiers.None);
    }

    private static void Validate(ShortcutDefinition shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);

        if (string.IsNullOrWhiteSpace(shortcut.Id)
            || !string.Equals(shortcut.Id, shortcut.Id.Trim(), StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(shortcut.Name)
            || string.IsNullOrWhiteSpace(shortcut.Description)
            || !shortcut.DefaultGesture.IsValid())
        {
            throw new InvalidOperationException("shortcut definitions require an id, human text, and a valid default gesture");
        }
    }

    private ShortcutBinding Resolve(ShortcutDefinition shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        return _bindingsById.TryGetValue(shortcut.Id, out ShortcutBinding? registered)
            ? registered
            : throw new ArgumentException($"shortcut '{shortcut.Id}' is not registered", nameof(shortcut));
    }

    private static void Deactivate(ShortcutBinding binding)
    {
        if (binding.Lease is not { } lease)
        {
            return;
        }

        binding.Lease = null;
        lease.ClearPending();
        lease.Dispose();
    }
}
