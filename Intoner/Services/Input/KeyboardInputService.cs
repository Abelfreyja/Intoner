using Intoner.Services.Interop;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.InteropServices;

namespace Intoner.Services.Input;

/// <summary> keyboard modifiers captured with a native key transition </summary>
[Flags]
internal enum KeyboardModifiers
{
    None = 0,
    Shift = 1 << 0,
    Control = 1 << 1,
    Alt = 1 << 2,
}

/// <summary> one native keyboard shortcut gesture </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct KeyboardGesture(
    SeVirtualKey Key,
    KeyboardModifiers Modifiers = KeyboardModifiers.None)
{
    private static readonly int KeyboardKeyCount = new KeyboardInputData().KeyState.Length;

    /// <summary> gets the modifier represented by the root key, if any </summary>
    internal KeyboardModifiers ImplicitModifier => Key switch
    {
        SeVirtualKey.MENU => KeyboardModifiers.Alt,
        SeVirtualKey.CONTROL => KeyboardModifiers.Control,
        SeVirtualKey.SHIFT => KeyboardModifiers.Shift,
        _ => KeyboardModifiers.None,
    };

    /// <summary> checks that the key and modifiers are valid for input </summary>
    public bool IsValid()
        => Key != SeVirtualKey.NO_KEY
        && (int)Key < KeyboardKeyCount
        && Enum.IsDefined(Key)
        && (Modifiers & ImplicitModifier) == KeyboardModifiers.None
        && (Modifiers & ~(KeyboardModifiers.Control | KeyboardModifiers.Shift | KeyboardModifiers.Alt)) == KeyboardModifiers.None;

    /// <summary> checks the required modifiers, optionally allowing other modifiers to be held as well </summary>
    internal bool MatchesModifiers(KeyboardModifiers modifiers, bool allowAdditionalModifiers)
    {
        KeyboardModifiers required = Modifiers | ImplicitModifier;
        return allowAdditionalModifiers ? (modifiers & required) == required : modifiers == required;
    }
}

/// <summary> one active keyboard input suppression and its queued key transitions </summary>
internal interface IKeyboardInputLease : IDisposable
{
    /// <summary> checks whether a registered gesture is down in the latest input snapshot </summary>
    bool IsHeld(KeyboardGesture gesture);

    /// <summary> returns and clears the number of matching pressed transitions </summary>
    int ConsumePressedCount(KeyboardGesture gesture);

    /// <summary> returns and clears every pressed transition for one key </summary>
    int ConsumePressedCount(SeVirtualKey key);

    /// <summary> clears all queued transitions without changing held state or suppression </summary>
    void ClearPending();
}

/// <summary> provides scoped native keyboard input suppression for plugin interactions </summary>
internal interface IKeyboardInputService
{
    /// <summary> installs capture once and awaits framework initialization </summary>
    Task StartAsync();

    /// <summary> gets whether the game is accepting keyboard text input </summary>
    bool IsTextInputActive { get; }

    /// <summary> begins observing gestures and suppressing exact keys while their required modifiers match </summary>
    /// <param name="gestures"> the gestures to observe </param>
    /// <param name="allowAdditionalModifiers"> allow extra modifiers </param>
    IKeyboardInputLease BeginSuppression(IEnumerable<KeyboardGesture> gestures, bool allowAdditionalModifiers = false);

    /// <summary> begins observing keys and suppressing exact keys regardless of held modifiers </summary>
    IKeyboardInputLease BeginSuppression(IEnumerable<SeVirtualKey> keys);
}

/// <summary> owns native keyboard observation and temporary game input suppression </summary>
internal sealed unsafe class KeyboardInputService : FrameworkNativeService, IKeyboardInputService
{
    private delegate nint UpdateInputDelegate(UIInputData* inputData, GamepadInputData* gamepad,
        CursorInputData* cursor, KeyboardInputData* keyboard, int cursorX, int cursorY, byte focusChanged, float deltaTime);

    private sealed class EmptyKeyboardInputLease : IKeyboardInputLease
    {
        public static EmptyKeyboardInputLease Instance { get; } = new EmptyKeyboardInputLease();

        public bool IsHeld(KeyboardGesture gesture)
            => false;

        public int ConsumePressedCount(KeyboardGesture gesture)
            => 0;

        public int ConsumePressedCount(SeVirtualKey key)
            => 0;

        public void ClearPending()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class KeyboardInputLease : IKeyboardInputLease
    {
        private readonly KeyboardInputService _owner;
        private readonly object _registrationKey = new();
        private int _disposed;

        public KeyboardInputLease(KeyboardInputService owner, KeyboardGesture[] gestures, bool matchModifiers,
            bool allowAdditionalModifiers = false)
        {
            _owner = owner;
            _owner.Register(_registrationKey, gestures, matchModifiers, allowAdditionalModifiers, this);
        }

        public bool IsHeld(KeyboardGesture gesture)
            => Volatile.Read(ref _disposed) == 0 && _owner.IsHeld(_registrationKey, gesture);

        public int ConsumePressedCount(KeyboardGesture gesture)
            => Volatile.Read(ref _disposed) != 0
                ? 0
                : _owner.ConsumePressedCount(_registrationKey, gesture);

        public int ConsumePressedCount(SeVirtualKey key)
            => Volatile.Read(ref _disposed) != 0
                ? 0
                : _owner.ConsumePressedCount(_registrationKey, key);

        public void ClearPending()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _owner.ClearPending(_registrationKey);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.Release(_registrationKey);
        }
    }

    private sealed class KeyboardInputRegistration
    {
        public required WeakReference<KeyboardInputLease> LeaseReference { get; init; }
        public required KeyboardGesture[] Gestures { get; init; }
        public required bool MatchModifiers { get; init; }
        public required bool AllowAdditionalModifiers { get; init; }
        public Dictionary<KeyboardGesture, int> PendingPressCounts { get; } = [];
        public HashSet<SeVirtualKey> HeldKeys { get; } = [];
        public KeyboardModifiers HeldModifiers { get; set; }
        public HashSet<SeVirtualKey> KeysAwaitingRelease { get; } = [];
        public bool ReleasePending { get; set; }
    }

    private readonly IFramework _framework;
    private readonly ILogger<KeyboardInputService> _logger;
    private readonly IGameInteropProvider? _gameInteropProvider;
    private readonly ISigScanner? _sigScanner;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<object, KeyboardInputRegistration> _registrationsByKey = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SeVirtualKey, int> _activeKeyRefCounts = [];
    private Hook<UpdateInputDelegate>? _updateInputHook;
    private int _disposed;

    public KeyboardInputService(
        ILogger<KeyboardInputService> logger,
        IFramework framework,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner)
        : base(framework, logger)
    {
        _framework = framework;
        _logger = logger;
        _gameInteropProvider = gameInteropProvider;
        _sigScanner = sigScanner;
    }

    internal KeyboardInputService(IFramework framework)
        : base(framework, NullLogger<KeyboardInputService>.Instance)
    {
        _framework = framework;
        _logger = NullLogger<KeyboardInputService>.Instance;
    }

    protected override void InitializeCore()
    {
        _updateInputHook = Hooks.Add(InteropHookUtility.CreateHook<UpdateInputDelegate>(_logger, _gameInteropProvider!, _sigScanner!,
            IntonerSignatures.NativeUIInputDataUpdate, UpdateInputDetour));
        Hooks.Enable();
        _framework.Update += HandleFrameworkUpdate;

        if (_updateInputHook is null)
        {
            _logger.LogWarning("native keyboard capture is unavailable");
        }
    }

    public bool IsTextInputActive
    {
        get
        {
            UIModule* uiModule = UIModule.Instance();
            if (uiModule is null)
            {
                return false;
            }

            RaptureAtkModule* atkModule = uiModule->GetRaptureAtkModule();
            return atkModule is not null && atkModule->IsTextInputActive();
        }
    }

    public IKeyboardInputLease BeginSuppression(IEnumerable<KeyboardGesture> gestures, bool allowAdditionalModifiers = false)
    {
        ArgumentNullException.ThrowIfNull(gestures);

        KeyboardGesture[] normalizedGestures = NormalizeGestures(gestures);
        return normalizedGestures.Length == 0
            ? EmptyKeyboardInputLease.Instance
            : new KeyboardInputLease(this, normalizedGestures, matchModifiers: true, allowAdditionalModifiers: allowAdditionalModifiers);
    }

    public IKeyboardInputLease BeginSuppression(IEnumerable<SeVirtualKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        KeyboardGesture[] gestures = NormalizeGestures(keys
            .Where(static key => key != SeVirtualKey.NO_KEY)
            .Select(static key => new KeyboardGesture(key)));
        return gestures.Length == 0
            ? EmptyKeyboardInputLease.Instance
            : new KeyboardInputLease(this, gestures, matchModifiers: false);
    }

    protected override void DisposeCore()
    {
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _framework.Update -= HandleFrameworkUpdate;
            }
        }
        finally
        {
            try
            {
                base.DisposeCore();
            }
            finally
            {
                lock (_stateLock)
                {
                    _registrationsByKey.Clear();
                    _activeKeyRefCounts.Clear();
                }
            }
        }
    }

    private static KeyboardGesture[] NormalizeGestures(IEnumerable<KeyboardGesture> gestures)
    {
        KeyboardGesture[] normalized = gestures.Distinct().ToArray();

        if (normalized.Any(static gesture => !gesture.IsValid()))
        {
            throw new ArgumentException("keyboard gestures require a valid key and modifiers", nameof(gestures));
        }

        return normalized;
    }

    private void HandleFrameworkUpdate(IFramework _)
    {
        if (IsDisposing || UIInputData.Instance() is not null)
        {
            return;
        }

        ProcessInput(null);
    }

    private nint UpdateInputDetour(UIInputData* inputData, GamepadInputData* gamepad,
        CursorInputData* cursor, KeyboardInputData* keyboard, int cursorX, int cursorY, byte focusChanged, float deltaTime)
    {
        using InteropHookCollection.CallbackScope callback = Hooks.EnterCallback();
        nint result = _updateInputHook!.OriginalDisposeSafe(inputData, gamepad, cursor, keyboard,
            cursorX, cursorY, focusChanged, deltaTime);

        if (!callback.IsActive || IsDisposing)
        {
            return result;
        }

        ProcessInput(inputData, IsTextInputActive);
        return result;
    }

    internal void ProcessInput(UIInputData* input, bool isTextInputActive = false)
    {
        if (IsDisposing)
        {
            return;
        }

        lock (_stateLock)
        {
            if (input == null)
            {
                foreach (KeyboardInputRegistration registration in _registrationsByKey.Values)
                {
                    registration.HeldKeys.Clear();
                }

                return;
            }

            PruneRegistrationsUnsafe(input);

            if (_registrationsByKey.Count == 0)
            {
                return;
            }

            KeyboardModifiers modifiers = ResolveModifiers((InputData*)input);
            foreach (KeyboardInputRegistration registration in _registrationsByKey.Values)
            {
                if (registration.ReleasePending || isTextInputActive)
                {
                    registration.HeldKeys.Clear();
                    registration.PendingPressCounts.Clear();
                    continue;
                }

                registration.HeldKeys.Clear();
                registration.HeldModifiers = modifiers;
                foreach (KeyboardGesture configuredGesture in registration.Gestures)
                {
                    KeyStateFlags state = input->GetKeyState(configuredGesture.Key);
                    KeyboardModifiers implicitModifier = configuredGesture.ImplicitModifier;

                    if (implicitModifier == KeyboardModifiers.None
                        ? IsKeyDownOrHeld(state)
                        : (modifiers & implicitModifier) != KeyboardModifiers.None)
                    {
                        registration.HeldKeys.Add(configuredGesture.Key);
                    }

                    if ((state & KeyStateFlags.Pressed) == KeyStateFlags.None
                        || (registration.MatchModifiers
                            && !configuredGesture.MatchesModifiers(modifiers, registration.AllowAdditionalModifiers)))
                    {
                        continue;
                    }

                    KeyboardGesture capturedGesture = registration.MatchModifiers
                        ? configuredGesture
                        : configuredGesture with { Modifiers = modifiers & ~implicitModifier };
                    int currentCount = registration.PendingPressCounts.GetValueOrDefault(capturedGesture);
                    registration.PendingPressCounts[capturedGesture] = currentCount == int.MaxValue
                        ? currentCount
                        : currentCount + 1;

                    if (implicitModifier == KeyboardModifiers.None)
                    {
                        registration.KeysAwaitingRelease.Add(configuredGesture.Key);
                    }
                }
            }

            if (_activeKeyRefCounts.Count > 0)
            {
                bool keyboardInputsChanged = false;
                foreach (SeVirtualKey key in _activeKeyRefCounts.Keys)
                {
                    if (input->GetKeyState(key) == KeyStateFlags.None
                        || !ShouldSuppressKeyUnsafe(key, modifiers, allowNewCapture: !isTextInputActive))
                    {
                        continue;
                    }

                    input->KeyboardInputs.KeyState[(int)key] = KeyStateFlags.None;
                    keyboardInputsChanged = true;
                }

                input->KeyboardInputsChanged |= keyboardInputsChanged;
            }
        }
    }

    private static bool IsKeyDownOrHeld(KeyStateFlags state)
        => (state & (KeyStateFlags.Down | KeyStateFlags.Held)) != KeyStateFlags.None;

    private static KeyboardModifiers ResolveModifiers(InputData* input)
        => (KeyboardModifiers)input->CurrentKeyModifier;

    private void Register(
        object registrationKey,
        KeyboardGesture[] gestures,
        bool matchModifiers,
        bool allowAdditionalModifiers,
        KeyboardInputLease lease)
    {
        if (IsDisposing)
        {
            return;
        }

        lock (_stateLock)
        {
            if (IsDisposing)
            {
                return;
            }

            PruneStaleRegistrationsUnsafe();
            KeyboardInputRegistration registration = new()
            {
                LeaseReference = new WeakReference<KeyboardInputLease>(lease),
                Gestures = gestures,
                MatchModifiers = matchModifiers,
                AllowAdditionalModifiers = allowAdditionalModifiers,
            };
            _registrationsByKey.Add(registrationKey, registration);

            foreach (KeyboardGesture gesture in gestures)
            {
                if (gesture.ImplicitModifier == KeyboardModifiers.None)
                {
                    IncrementActiveKeyUnsafe(gesture.Key);
                }
            }
        }
    }

    private void Release(object registrationKey)
    {
        if (IsDisposing)
        {
            return;
        }

        lock (_stateLock)
        {
            PruneStaleRegistrationsUnsafe();
            if (!_registrationsByKey.TryGetValue(registrationKey, out KeyboardInputRegistration? registration))
            {
                return;
            }

            registration.PendingPressCounts.Clear();
            registration.HeldKeys.Clear();

            if (registration.KeysAwaitingRelease.Count == 0)
            {
                RemoveRegistrationUnsafe(registrationKey);
                return;
            }

            registration.ReleasePending = true;
        }
    }

    private bool IsHeld(object registrationKey, KeyboardGesture gesture)
    {
        if (IsDisposing)
        {
            return false;
        }

        lock (_stateLock)
        {
            if (!_registrationsByKey.TryGetValue(registrationKey, out KeyboardInputRegistration? registration)
                || registration.ReleasePending
                || !registration.HeldKeys.Contains(gesture.Key)
                || (registration.MatchModifiers && !registration.Gestures.Contains(gesture)))
            {
                return false;
            }

            return gesture.MatchesModifiers(registration.HeldModifiers,
                registration.AllowAdditionalModifiers || gesture.ImplicitModifier != KeyboardModifiers.None);
        }
    }

    private int ConsumePressedCount(object registrationKey, KeyboardGesture gesture)
    {
        if (IsDisposing)
        {
            return 0;
        }

        lock (_stateLock)
        {
            PruneStaleRegistrationsUnsafe();
            return _registrationsByKey.TryGetValue(registrationKey, out KeyboardInputRegistration? registration)
                && registration.PendingPressCounts.Remove(gesture, out int count)
                    ? count
                    : 0;
        }
    }

    private int ConsumePressedCount(object registrationKey, SeVirtualKey key)
    {
        if (IsDisposing)
        {
            return 0;
        }

        lock (_stateLock)
        {
            PruneStaleRegistrationsUnsafe();
            if (!_registrationsByKey.TryGetValue(registrationKey, out KeyboardInputRegistration? registration))
            {
                return 0;
            }

            int count = 0;
            List<KeyboardGesture>? consumed = null;
            foreach ((KeyboardGesture gesture, int gestureCount) in registration.PendingPressCounts)
            {
                if (gesture.Key != key)
                {
                    continue;
                }

                consumed ??= [];
                consumed.Add(gesture);
                count = int.MaxValue - count < gestureCount
                    ? int.MaxValue
                    : count + gestureCount;
            }

            if (consumed is not null)
            {
                foreach (KeyboardGesture gesture in consumed)
                {
                    _ = registration.PendingPressCounts.Remove(gesture);
                }
            }

            return count;
        }
    }

    private void ClearPending(object registrationKey)
    {
        if (IsDisposing)
        {
            return;
        }

        lock (_stateLock)
        {
            PruneStaleRegistrationsUnsafe();
            if (_registrationsByKey.TryGetValue(registrationKey, out KeyboardInputRegistration? registration))
            {
                registration.PendingPressCounts.Clear();
            }
        }
    }

    private void RemoveRegistrationUnsafe(object registrationKey)
    {
        if (!_registrationsByKey.Remove(registrationKey, out KeyboardInputRegistration? registration))
        {
            return;
        }

        foreach (KeyboardGesture gesture in registration.Gestures)
        {
            if (gesture.ImplicitModifier == KeyboardModifiers.None)
            {
                DecrementActiveKeyUnsafe(gesture.Key);
            }
        }
    }

    private void PruneStaleRegistrationsUnsafe()
    {
        if (_registrationsByKey.Count == 0)
        {
            return;
        }

        List<object>? staleKeys = null;
        foreach (var pair in _registrationsByKey)
        {
            if (pair.Value.ReleasePending || pair.Value.LeaseReference.TryGetTarget(out _))
            {
                continue;
            }

            if (pair.Value.KeysAwaitingRelease.Count > 0)
            {
                pair.Value.PendingPressCounts.Clear();
                pair.Value.HeldKeys.Clear();
                pair.Value.ReleasePending = true;
                continue;
            }

            staleKeys ??= [];
            staleKeys.Add(pair.Key);
        }

        if (staleKeys is null)
        {
            return;
        }

        foreach (var staleKey in staleKeys)
        {
            RemoveRegistrationUnsafe(staleKey);
        }
    }

    private void PruneRegistrationsUnsafe(UIInputData* input)
    {
        PruneStaleRegistrationsUnsafe();

        List<object>? releasedRegistrations = null;
        foreach (var pair in _registrationsByKey)
        {
            KeyboardInputRegistration registration = pair.Value;
            List<SeVirtualKey>? releasedKeys = null;
            foreach (SeVirtualKey key in registration.KeysAwaitingRelease)
            {
                if (IsKeyDownOrHeld(input->GetKeyState(key)))
                {
                    continue;
                }

                releasedKeys ??= [];
                releasedKeys.Add(key);
            }

            if (releasedKeys is not null)
            {
                foreach (SeVirtualKey key in releasedKeys)
                {
                    registration.KeysAwaitingRelease.Remove(key);
                }
            }

            if (registration.ReleasePending && registration.KeysAwaitingRelease.Count == 0)
            {
                releasedRegistrations ??= [];
                releasedRegistrations.Add(pair.Key);
            }
        }

        if (releasedRegistrations is null)
        {
            return;
        }

        foreach (object registrationKey in releasedRegistrations)
        {
            RemoveRegistrationUnsafe(registrationKey);
        }
    }

    private void IncrementActiveKeyUnsafe(SeVirtualKey key)
    {
        _activeKeyRefCounts[key] = _activeKeyRefCounts.GetValueOrDefault(key) + 1;
    }

    private void DecrementActiveKeyUnsafe(SeVirtualKey key)
    {
        if (!_activeKeyRefCounts.TryGetValue(key, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            _activeKeyRefCounts.Remove(key);
            return;
        }

        _activeKeyRefCounts[key] = count - 1;
    }

    private bool ShouldSuppressKeyUnsafe(
        SeVirtualKey key,
        KeyboardModifiers modifiers,
        bool allowNewCapture)
    {
        if (key == SeVirtualKey.NO_KEY || !_activeKeyRefCounts.ContainsKey(key))
        {
            return false;
        }

        foreach (KeyboardInputRegistration registration in _registrationsByKey.Values)
        {
            if (registration.KeysAwaitingRelease.Contains(key))
            {
                return true;
            }

            if (registration.ReleasePending || !allowNewCapture)
            {
                continue;
            }

            foreach (KeyboardGesture gesture in registration.Gestures)
            {
                if (gesture.Key == key
                    && (!registration.MatchModifiers || gesture.MatchesModifiers(modifiers, registration.AllowAdditionalModifiers)))
                {
                    registration.KeysAwaitingRelease.Add(key);
                    return true;
                }
            }
        }

        return false;
    }
}
