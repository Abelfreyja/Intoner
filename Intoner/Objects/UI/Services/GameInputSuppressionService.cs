using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.UI.Services;

/// <summary>
/// Represents one active keyboard suppression registration and its queued pressed transitions.
/// </summary>
internal interface IGameInputSuppressionLease : IDisposable
{
    /// <summary>
    /// Returns and clears the number of pressed transitions recorded for the suppressed key since the last consume.
    /// </summary>
    int ConsumePressedCount(SeVirtualKey key);
}

/// <summary>
/// Begins keyboard suppression that blocks matching game input ids while still queueing pressed transitions for the
/// tool that requested the suppression.
/// </summary>
internal interface IGameInputSuppressionService
{
    /// <summary>
    /// Begins suppressing the given keys until the returned lease is disposed.
    /// </summary>
    IGameInputSuppressionLease BeginKeyboardSuppression(IEnumerable<SeVirtualKey> keys);
}

/// <summary>
/// Owns the low level input id detours and framework tick raw key filtering used by temporary UI interactions that must
/// block game actions without losing the local shortcut press that triggered them.
/// </summary>
internal sealed unsafe class GameInputSuppressionService : IGameInputSuppressionService, IDisposable
{
    private unsafe delegate byte InputIdStateDelegate(InputData* inputData, InputId inputId);

    private sealed class NoOpSuppressionLease : IGameInputSuppressionLease
    {
        public static IGameInputSuppressionLease Instance { get; } = new NoOpSuppressionLease();

        public int ConsumePressedCount(SeVirtualKey key)
            => 0;

        public void Dispose()
        {
        }
    }

    private sealed class SuppressionLease : IGameInputSuppressionLease
    {
        private readonly GameInputSuppressionService _service;
        private readonly object _registrationKey = new();
        private int _disposed;

        public SuppressionLease(GameInputSuppressionService service, IReadOnlyCollection<SeVirtualKey> keys)
        {
            _service = service;
            _service.RegisterSuppressedKeys(_registrationKey, keys, this);
        }

        public int ConsumePressedCount(SeVirtualKey key)
            => Volatile.Read(ref _disposed) != 0
                ? 0
                : _service.ConsumePressedCount(_registrationKey, key);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _service.ReleaseSuppressedKeys(_registrationKey);
        }
    }

    private sealed class SuppressionRegistration
    {
        public required WeakReference<SuppressionLease> LeaseReference { get; init; }
        public HashSet<SeVirtualKey> Keys { get; } = [];
        public Dictionary<SeVirtualKey, int> PendingPressCounts { get; } = [];
    }

    private readonly ILogger<GameInputSuppressionService> _logger;
    private readonly IFramework _framework;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<object, SuppressionRegistration> _registrationsByKey = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SeVirtualKey, int> _activeKeyRefCounts = [];
    private readonly Hook<InputIdStateDelegate>? _inputPressedHook;
    private readonly Hook<InputIdStateDelegate>? _inputDownHook;
    private readonly Hook<InputIdStateDelegate>? _inputHeldHook;
    private int _disposed;

    public GameInputSuppressionService(
        ILogger<GameInputSuppressionService> logger,
        IFramework framework,
        IGameInteropProvider gameInteropProvider)
    {
        _logger = logger;
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
            _logger.LogWarning("game input suppression service did not resolve all input detours");
        }
    }

    public IGameInputSuppressionLease BeginKeyboardSuppression(IEnumerable<SeVirtualKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var normalizedKeys = keys
            .Where(static key => key != SeVirtualKey.NO_KEY)
            .Distinct()
            .ToArray();
        return normalizedKeys.Length == 0
            ? NoOpSuppressionLease.Instance
            : new SuppressionLease(this, normalizedKeys);
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

    private void HandleFrameworkUpdate(IFramework _)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var input = UIInputData.Instance();
        if (input == null)
        {
            return;
        }

        lock (_stateLock)
        {
            PruneStaleRegistrationsUnsafe();
            if (_activeKeyRefCounts.Count == 0)
            {
                return;
            }

            foreach (var registration in _registrationsByKey.Values)
            {
                foreach (var key in registration.Keys)
                {
                    if (input->IsKeyPressed(key))
                    {
                        registration.PendingPressCounts[key] = registration.PendingPressCounts.GetValueOrDefault(key) + 1;
                    }
                }
            }

            foreach (var key in _activeKeyRefCounts.Keys)
            {
                input->KeyboardInputs.KeyState[(int)key] = KeyStateFlags.None;
            }

            input->KeyboardInputsChanged = true;
        }
    }

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
            return IsSuppressedKeyUnsafe(keySettings[0].Key)
                || IsSuppressedKeyUnsafe(keySettings[1].Key);
        }
    }

    private void RegisterSuppressedKeys(object registrationKey, IReadOnlyCollection<SeVirtualKey> keys, SuppressionLease lease)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_stateLock)
        {
            PruneStaleRegistrationsUnsafe();
            if (_registrationsByKey.TryGetValue(registrationKey, out var existingRegistration))
            {
                ReplaceSuppressedKeysUnsafe(existingRegistration, keys);
                return;
            }

            var registration = new SuppressionRegistration
            {
                LeaseReference = new WeakReference<SuppressionLease>(lease),
            };
            _registrationsByKey.Add(registrationKey, registration);
            ReplaceSuppressedKeysUnsafe(registration, keys);
        }
    }

    private void ReleaseSuppressedKeys(object registrationKey)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_stateLock)
        {
            PruneStaleRegistrationsUnsafe();
            ClearSuppressedKeysUnsafe(registrationKey);
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
            if (!_registrationsByKey.TryGetValue(registrationKey, out var registration)
                || !registration.PendingPressCounts.Remove(key, out var count))
            {
                return 0;
            }

            return count;
        }
    }

    private void ReplaceSuppressedKeysUnsafe(SuppressionRegistration registration, IReadOnlyCollection<SeVirtualKey> keys)
    {
        foreach (var key in registration.Keys)
        {
            DecrementActiveKeyUnsafe(key);
        }

        registration.Keys.Clear();
        foreach (var key in keys)
        {
            registration.Keys.Add(key);
            registration.PendingPressCounts.TryAdd(key, 0);
            IncrementActiveKeyUnsafe(key);
        }

        foreach (var staleKey in registration.PendingPressCounts.Keys.Except(registration.Keys).ToArray())
        {
            registration.PendingPressCounts.Remove(staleKey);
        }
    }

    private void ClearSuppressedKeysUnsafe(object registrationKey)
    {
        if (!_registrationsByKey.Remove(registrationKey, out var registration))
        {
            return;
        }

        foreach (var key in registration.Keys)
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
            if (pair.Value.LeaseReference.TryGetTarget(out _))
            {
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
            ClearSuppressedKeysUnsafe(staleKey);
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

    private bool IsSuppressedKeyUnsafe(SeVirtualKey key)
        => key != SeVirtualKey.NO_KEY && _activeKeyRefCounts.ContainsKey(key);
}


