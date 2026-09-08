using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using Microsoft.Extensions.Logging;
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
    KeyboardModifiers Modifiers = KeyboardModifiers.None);

/// <summary> one active keyboard input suppression and its queued key transitions </summary>
internal interface IKeyboardInputLease : IDisposable
{
    /// <summary> returns and clears the number of matching pressed transitions </summary>
    int ConsumePressedCount(KeyboardGesture gesture);

    /// <summary> returns and clears every pressed transition for one key </summary>
    int ConsumePressedCount(SeVirtualKey key);

    /// <summary> clears all queued transitions </summary>
    void ClearPending();
}

/// <summary> provides scoped native keyboard input suppression for plugin interactions </summary>
internal interface IKeyboardInputService
{
    /// <summary> gets whether the game is accepting keyboard text input </summary>
    bool IsTextInputActive { get; }

    /// <summary> begins suppressing exact gestures while their modifiers match </summary>
    IKeyboardInputLease BeginSuppression(IEnumerable<KeyboardGesture> gestures);

    /// <summary> begins suppressing keys regardless of held modifiers </summary>
    IKeyboardInputLease BeginSuppression(IEnumerable<SeVirtualKey> keys);
}

/// <summary> owns native keyboard observation and temporary game input suppression </summary>
internal sealed unsafe class KeyboardInputService : IKeyboardInputService, IDisposable
{
    private const KeyboardModifiers AllModifiers = KeyboardModifiers.Control
        | KeyboardModifiers.Shift
        | KeyboardModifiers.Alt;

    private unsafe delegate byte InputIdStateDelegate(InputData* inputData, InputId inputId);

    private sealed class EmptyKeyboardInputLease : IKeyboardInputLease
    {
        public static EmptyKeyboardInputLease Instance { get; } = new EmptyKeyboardInputLease();

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

        public KeyboardInputLease(KeyboardInputService owner, KeyboardGesture[] gestures, bool matchModifiers)
        {
            _owner = owner;
            _owner.Register(_registrationKey, gestures, matchModifiers, this);
        }

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
        public Dictionary<KeyboardGesture, int> PendingPressCounts { get; } = [];
        public HashSet<SeVirtualKey> KeysAwaitingRelease { get; } = [];
        public bool ReleasePending { get; set; }
    }

    private readonly IFramework _framework;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<object, KeyboardInputRegistration> _registrationsByKey = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SeVirtualKey, int> _activeKeyRefCounts = [];
    private readonly Hook<InputIdStateDelegate>? _inputPressedHook;
    private readonly Hook<InputIdStateDelegate>? _inputDownHook;
    private readonly Hook<InputIdStateDelegate>? _inputHeldHook;
    private int _disposed;

    public KeyboardInputService(
        ILogger<KeyboardInputService> logger,
        IFramework framework,
        IGameInteropProvider gameInteropProvider)
    {
        _framework = framework;
        _inputPressedHook = CreateInputHook(gameInteropProvider, (nint)InputData.MemberFunctionPointers.IsInputIdPressed, InputPressedDetour);
        _inputDownHook = CreateInputHook(gameInteropProvider, (nint)InputData.MemberFunctionPointers.IsInputIdDown, InputDownDetour);
        _inputHeldHook = CreateInputHook(gameInteropProvider, (nint)InputData.MemberFunctionPointers.IsInputIdHeld, InputHeldDetour);

        _inputPressedHook?.Enable();
        _inputDownHook?.Enable();
        _inputHeldHook?.Enable();
        _framework.Update += HandleFrameworkUpdate;

        if (_inputPressedHook == null || _inputDownHook == null || _inputHeldHook == null)
        {
            logger.LogWarning("keyboard input service did not resolve all input detours");
        }
    }

    internal KeyboardInputService(IFramework framework)
    {
        _framework = framework;
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

    public IKeyboardInputLease BeginSuppression(IEnumerable<KeyboardGesture> gestures)
    {
        ArgumentNullException.ThrowIfNull(gestures);

        KeyboardGesture[] normalizedGestures = NormalizeGestures(gestures);
        return normalizedGestures.Length == 0
            ? EmptyKeyboardInputLease.Instance
            : new KeyboardInputLease(this, normalizedGestures, matchModifiers: true);
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _framework.Update -= HandleFrameworkUpdate;
        _inputPressedHook?.Dispose();
        _inputDownHook?.Dispose();
        _inputHeldHook?.Dispose();

        lock (_stateLock)
        {
            _registrationsByKey.Clear();
            _activeKeyRefCounts.Clear();
        }
    }

    private static Hook<InputIdStateDelegate>? CreateInputHook(
        IGameInteropProvider gameInteropProvider,
        nint address,
        InputIdStateDelegate detour)
        => address == nint.Zero
            ? null
            : gameInteropProvider.HookFromAddress<InputIdStateDelegate>(address, detour);

    private static KeyboardGesture[] NormalizeGestures(IEnumerable<KeyboardGesture> gestures)
    {
        KeyboardGesture[] normalized = gestures.Distinct().ToArray();
        if (normalized.Any(static gesture => gesture.Key == SeVirtualKey.NO_KEY
            || !Enum.IsDefined(gesture.Key)
            || (gesture.Modifiers & ~AllModifiers) != KeyboardModifiers.None))
        {
            throw new ArgumentException("keyboard gestures require a valid key and modifiers", nameof(gestures));
        }

        return normalized;
    }

    private void HandleFrameworkUpdate(IFramework _)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        ProcessInput(UIInputData.Instance());
    }

    internal void ProcessInput(UIInputData* input)
    {
        if (input == null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_stateLock)
        {
            PruneRegistrationsUnsafe(input);
            if (_registrationsByKey.Count == 0)
            {
                return;
            }

            KeyboardModifiers modifiers = ResolveModifiers((InputData*)input);
            foreach (KeyboardInputRegistration registration in _registrationsByKey.Values)
            {
                if (registration.ReleasePending)
                {
                    continue;
                }

                foreach (KeyboardGesture configuredGesture in registration.Gestures)
                {
                    if (!input->IsKeyPressed(configuredGesture.Key)
                        || (registration.MatchModifiers && configuredGesture.Modifiers != modifiers))
                    {
                        continue;
                    }

                    KeyboardGesture capturedGesture = registration.MatchModifiers
                        ? configuredGesture
                        : configuredGesture with { Modifiers = modifiers };
                    int currentCount = registration.PendingPressCounts.GetValueOrDefault(capturedGesture);
                    registration.PendingPressCounts[capturedGesture] = currentCount == int.MaxValue
                        ? currentCount
                        : currentCount + 1;
                    registration.KeysAwaitingRelease.Add(configuredGesture.Key);
                }
            }

            if (_activeKeyRefCounts.Count > 0)
            {
                bool keyboardInputsChanged = false;
                foreach (SeVirtualKey key in _activeKeyRefCounts.Keys)
                {
                    if (input->GetKeyState(key) == KeyStateFlags.None
                        || !ShouldSuppressKeyUnsafe(key, modifiers, rememberUntilRelease: true))
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

    private static KeyboardModifiers ResolveModifiers(InputData* input)
        => (KeyboardModifiers)input->CurrentKeyModifier;

    private byte InputPressedDetour(InputData* inputData, InputId inputId)
        => ShouldSuppressInput(inputData, inputId)
            ? (byte)0
            : _inputPressedHook!.Original(inputData, inputId);

    private byte InputDownDetour(InputData* inputData, InputId inputId)
        => ShouldSuppressInput(inputData, inputId)
            ? (byte)0
            : _inputDownHook!.Original(inputData, inputId);

    private byte InputHeldDetour(InputData* inputData, InputId inputId)
        => ShouldSuppressInput(inputData, inputId)
            ? (byte)0
            : _inputHeldHook!.Original(inputData, inputId);

    private bool ShouldSuppressInput(InputData* inputData, InputId inputId)
    {
        if (inputData == null)
        {
            return false;
        }

        lock (_stateLock)
        {
            PruneStaleRegistrationsUnsafe();
            if (_activeKeyRefCounts.Count == 0)
            {
                return false;
            }

            var keybind = inputData->GetKeybind(inputId);
            if (keybind == null)
            {
                return false;
            }

            var keySettings = (KeySetting*)keybind;
            KeyboardModifiers modifiers = ResolveModifiers(inputData);
            return ShouldSuppressKeyUnsafe(keySettings[0].Key, modifiers)
                || ShouldSuppressKeyUnsafe(keySettings[1].Key, modifiers);
        }
    }

    private void Register(
        object registrationKey,
        KeyboardGesture[] gestures,
        bool matchModifiers,
        KeyboardInputLease lease)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_stateLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            PruneStaleRegistrationsUnsafe();
            KeyboardInputRegistration registration = new()
            {
                LeaseReference = new WeakReference<KeyboardInputLease>(lease),
                Gestures = gestures,
                MatchModifiers = matchModifiers,
            };
            _registrationsByKey.Add(registrationKey, registration);

            foreach (SeVirtualKey key in gestures.Select(static gesture => gesture.Key))
            {
                IncrementActiveKeyUnsafe(key);
            }
        }
    }

    private void Release(object registrationKey)
    {
        if (Volatile.Read(ref _disposed) != 0)
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
            if (registration.KeysAwaitingRelease.Count == 0)
            {
                RemoveRegistrationUnsafe(registrationKey);
                return;
            }

            registration.ReleasePending = true;
        }
    }

    private int ConsumePressedCount(object registrationKey, KeyboardGesture gesture)
    {
        if (Volatile.Read(ref _disposed) != 0)
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
        if (Volatile.Read(ref _disposed) != 0)
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
        if (Volatile.Read(ref _disposed) != 0)
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

        foreach (SeVirtualKey key in registration.Gestures.Select(static gesture => gesture.Key))
        {
            DecrementActiveKeyUnsafe(key);
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
                if (input->IsKeyDown(key))
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
        bool rememberUntilRelease = false)
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

            if (registration.ReleasePending)
            {
                continue;
            }

            foreach (KeyboardGesture gesture in registration.Gestures)
            {
                if (gesture.Key == key
                    && (!registration.MatchModifiers || gesture.Modifiers == modifiers))
                {
                    if (rememberUntilRelease)
                    {
                        registration.KeysAwaitingRelease.Add(key);
                    }

                    return true;
                }
            }
        }

        return false;
    }
}
