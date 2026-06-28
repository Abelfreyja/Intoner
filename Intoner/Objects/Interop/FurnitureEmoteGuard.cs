using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Interop;

internal sealed unsafe class FurnitureEmoteGuard : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Size = 0x40)]
    private struct SnapResult
    {
        public Vector3 PrimaryPosition;
        public float PrimaryRotation;
        private fixed byte _padding0[12];
        public Vector3 FallbackPosition;
        public float FallbackRotation;
        private fixed byte _padding1[12];
        public nint LayoutInstance;
    }

    private delegate bool ResolveSnapDelegate(nint actor, SnapResult* result);
    private delegate bool ExecuteEmoteDelegate(nint emoteManager, ushort emoteId, EmoteController.PlayEmoteOption* playEmoteOption);

    private readonly ILogger<FurnitureEmoteGuard> _logger;
    private readonly IGameInteropProvider         _gameInteropProvider;
    private readonly ISigScanner                  _sigScanner;

    private readonly Lock _hookStateLock = new();
    private readonly Lock _trackedInstancesLock = new();
    private readonly Dictionary<nint, HashSet<nint>> _trackedInstanceGroups = [];
    private readonly HashSet<nint> _trackedInstances = [];

    private Hook<ExecuteEmoteDelegate>?  _executeEmoteHook;
    private Hook<ResolveSnapDelegate>?   _snapVariantZeroHook;
    private Hook<ResolveSnapDelegate>?   _snapVariantOneHook;
    private ExecuteEmoteDelegate?        _executeEmoteOriginal;
    private ResolveSnapDelegate?         _snapVariantZeroOriginal;
    private ResolveSnapDelegate?         _snapVariantOneOriginal;
    private readonly ObjectDisposalState _disposeState = new();
    private Task?                        _hookInitializationTask;
    private bool                         _hooksDisposed;

    public FurnitureEmoteGuard(ILogger<FurnitureEmoteGuard> logger, IGameInteropProvider gameInteropProvider, ISigScanner sigScanner)
    {
        _logger              = logger;
        _gameInteropProvider = gameInteropProvider;
        _sigScanner          = sigScanner;

        StartHookInitialization();
    }

    private void StartHookInitialization()
    {
        Task hookInitializationTask;
        lock (_hookStateLock)
        {
            if (_hookInitializationTask != null || _disposeState.IsDisposing)
            {
                return;
            }

            _hookInitializationTask = Task.Run(InitializeHooks);
            hookInitializationTask = _hookInitializationTask;
        }

        _ = hookInitializationTask.ContinueWith(
            _ => DisposeHooksIfRequested(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void InitializeHooks()
    {
        try
        {
            _snapVariantZeroHook = CreateSnapHook(ObjectSignatures.FurnitureSnapZero, ResolveSnapVariantZeroDetour);
            _snapVariantOneHook = CreateSnapHook(ObjectSignatures.FurnitureSnapOne, ResolveSnapVariantOneDetour);
            _executeEmoteHook = CreateExecuteEmoteHook();
            _executeEmoteOriginal = _executeEmoteHook?.Original;
            _snapVariantZeroOriginal = _snapVariantZeroHook?.Original;
            _snapVariantOneOriginal = _snapVariantOneHook?.Original;

            if (IsDisposeRequested())
            {
                DisposeInitializedHooks();
                return;
            }

            _executeEmoteHook?.Enable();
            _snapVariantZeroHook?.Enable();
            _snapVariantOneHook?.Enable();

            if (_executeEmoteHook == null || _snapVariantZeroHook == null || _snapVariantOneHook == null)
            {
                _logger.LogWarning("furniture emote guard did not resolve all emote hooks");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "furniture emote guard failed to initialize emote hooks");
            DisposeInitializedHooks();
        }
    }

    private Hook<ExecuteEmoteDelegate>? CreateExecuteEmoteHook()
    {
        if (ObjectSignatures.FurnitureExecuteEmote.Address == nint.Zero)
        {
            _logger.LogWarning("furniture emote guard could not resolve EmoteManager.ExecuteEmote from FFXIVClientStructs");
            return null;
        }

        // explicit layout targets bypass the wrapper snap helpers, so clear helper layouts here too
        return ObjectInteropHookUtility.CreateHookFromAddress<ExecuteEmoteDelegate>(
            _logger,
            _gameInteropProvider,
            ObjectSignatures.FurnitureExecuteEmote,
            ExecuteEmoteDetour);
    }

    private Hook<ResolveSnapDelegate>? CreateSnapHook(ObjectSignatures.JmpCallHookTarget target, ResolveSnapDelegate detour)
    {
        nint functionAddress = ObjectNativeAddressResolver.TryResolveJmpCallTarget(_logger, _sigScanner, target);
        return ObjectInteropHookUtility.CreateHookFromAddress(_logger, _gameInteropProvider, functionAddress, detour, target);
    }

    public void RegisterInstanceTree(SharedGroupLayoutInstance* instance)
    {
        if (instance == null)
        {
            return;
        }

        // snap targets can point at child layout instances, not only the shared group root
        var trackedAddresses = new HashSet<nint>();
        CollectTrackedInstances((ILayoutInstance*)instance, trackedAddresses);

        lock (_trackedInstancesLock)
        {
            ReplaceTrackedGroup((nint)instance, trackedAddresses);
        }
    }

    public void UnregisterInstanceTree(SharedGroupLayoutInstance* instance)
    {
        if (instance == null)
        {
            return;
        }

        lock (_trackedInstancesLock)
        {
            RemoveTrackedGroup((nint)instance);
        }
    }

    private bool ExecuteEmoteDetour(nint emoteManager, ushort emoteId, EmoteController.PlayEmoteOption* playEmoteOption)
    {
        var executeEmoteOriginal = _executeEmoteOriginal;
        if (executeEmoteOriginal == null)
        {
            return false;
        }

        if (playEmoteOption == null || !IsTrackedInstance((nint)playEmoteOption->Layout))
        {
            return executeEmoteOriginal(emoteManager, emoteId, playEmoteOption);
        }

        var sanitizedOption = *playEmoteOption;
        sanitizedOption.Layout = null;

        _logger.LogInformation(
            "cleared helper furniture layout from play emote option for emote 0x{EmoteId:X}",
            emoteId);

        return executeEmoteOriginal(emoteManager, emoteId, &sanitizedOption);
    }

    private bool ResolveSnapVariantZeroDetour(nint actor, SnapResult* result)
    {
        var snapVariantZeroOriginal = _snapVariantZeroOriginal;
        if (snapVariantZeroOriginal == null)
        {
            return false;
        }

        return ResolveSnapDetour(snapVariantZeroOriginal, actor, result);
    }

    private bool ResolveSnapVariantOneDetour(nint actor, SnapResult* result)
    {
        var snapVariantOneOriginal = _snapVariantOneOriginal;
        if (snapVariantOneOriginal == null)
        {
            return false;
        }

        return ResolveSnapDetour(snapVariantOneOriginal, actor, result);
    }

    private bool ResolveSnapDetour(ResolveSnapDelegate original, nint actor, SnapResult* result)
    {
        var resolved = original(actor, result);
        if (!resolved || result == null)
        {
            return resolved;
        }

        // wrapper based sit or doze snaps report the chosen layout instance here
        var layoutInstance = result->LayoutInstance;
        if (!IsTrackedInstance(layoutInstance))
        {
            return resolved;
        }

        *result = default;
        _logger.LogInformation(
            "blocked emote snap onto helper furniture instance 0x{Address:X}",
            (ulong)layoutInstance);
        return false;
    }

    private bool IsTrackedInstance(nint layoutInstance)
    {
        lock (_trackedInstancesLock)
        {
            return _trackedInstances.Contains(layoutInstance);
        }
    }

    private static void CollectTrackedInstances(ILayoutInstance* instance, HashSet<nint> trackedAddresses)
    {
        if (instance == null)
        {
            return;
        }

        trackedAddresses.Add((nint)instance);
        if (instance->Id.Type != InstanceType.SharedGroup)
        {
            return;
        }

        var sharedGroup = (SharedGroupLayoutInstance*)instance;
        foreach (var child in sharedGroup->Instances.Instances)
        {
            var node = child.Value;
            if (node == null || node->Instance == null)
            {
                continue;
            }

            CollectTrackedInstances(node->Instance, trackedAddresses);
        }
    }

    private void ReplaceTrackedGroup(nint rootInstance, HashSet<nint> trackedAddresses)
    {
        RemoveTrackedGroup(rootInstance);
        _trackedInstanceGroups[rootInstance] = trackedAddresses;

        foreach (var trackedAddress in trackedAddresses)
        {
            _trackedInstances.Add(trackedAddress);
        }
    }

    private void RemoveTrackedGroup(nint rootInstance)
    {
        if (!_trackedInstanceGroups.Remove(rootInstance, out var trackedAddresses))
        {
            return;
        }

        foreach (var trackedAddress in trackedAddresses)
        {
            _trackedInstances.Remove(trackedAddress);
        }
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        DisposeHooksIfRequested();
    }

    private bool IsDisposeRequested()
        => _disposeState.IsDisposing;

    private void DisposeHooksIfRequested()
    {
        lock (_hookStateLock)
        {
            if (!_disposeState.IsDisposing || _hookInitializationTask is { IsCompleted: false })
            {
                return;
            }
        }

        DisposeInitializedHooks();
    }

    private void DisposeInitializedHooks()
    {
        Hook<ExecuteEmoteDelegate>? executeEmoteHook;
        Hook<ResolveSnapDelegate>? snapVariantZeroHook;
        Hook<ResolveSnapDelegate>? snapVariantOneHook;

        lock (_hookStateLock)
        {
            if (_hooksDisposed)
            {
                return;
            }

            _hooksDisposed = true;

            executeEmoteHook    = _executeEmoteHook;
            snapVariantZeroHook = _snapVariantZeroHook;
            snapVariantOneHook  = _snapVariantOneHook;
        }

        ObjectInteropHookUtility.DisposeHook(executeEmoteHook);
        ObjectInteropHookUtility.DisposeHook(snapVariantZeroHook);
        ObjectInteropHookUtility.DisposeHook(snapVariantOneHook);

        lock (_trackedInstancesLock)
        {
            _trackedInstanceGroups.Clear();
            _trackedInstances.Clear();
        }
    }
}

