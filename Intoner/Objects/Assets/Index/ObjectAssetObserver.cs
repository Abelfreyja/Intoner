using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Intoner.Objects.Interop;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SceneVfxObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject;

namespace Intoner.Objects.Assets;

internal enum ObjectAssetObservationKind
{
    ResourceLoad,
    StaticVfxCreate,
    ActorVfxCreate,
    TriggerUse,
}

internal readonly record struct ObjectAssetObservation(ObjectAssetObservationKind Kind, string Path);

internal sealed unsafe class ObjectAssetObserver : IDisposable
{
    private delegate ResourceHandle* GetResourceSyncDelegate(ResourceManager* resourceManager, ResourceCategory* category, uint* type, uint* hash, byte* path, ObjectGetResourceParameters* getResourceParameters, byte* file, uint line);
    private delegate ResourceHandle* GetResourceAsyncDelegate(ResourceManager* resourceManager, ResourceCategory* category, uint* type, uint* hash, byte* path, ObjectGetResourceParameters* getResourceParameters, byte isUnknown, byte* file, uint line);
    private delegate SceneVfxObject* StaticVfxCreateDelegate(byte* path, byte* pool);
    private delegate void StaticVfxRemoveDelegate(nint vfx);
    private delegate nint ActorVfxCreateDelegate(byte* path, nint a2, nint a3, float a4, byte a5, ushort a6, byte a7);
    private delegate nint ActorVfxRemoveDelegate(nint vfx, uint freeFlags);
    private delegate void VfxUseTriggerDelegate(nint vfx, uint triggerId);

    private readonly ILogger<ObjectAssetObserver> _logger;
    private readonly ConcurrentQueue<ObjectAssetObservation> _queue = [];
    private readonly Action<IReadOnlyList<ObjectAssetObservation>> _observeBatch;
    private readonly Lock _activeVfxLock = new();
    private readonly Dictionary<nint, string> _activeVfxPaths = [];

    private readonly Hook<GetResourceSyncDelegate>? _getResourceSyncHook;
    private readonly Hook<GetResourceAsyncDelegate>? _getResourceAsyncHook;
    private readonly Hook<StaticVfxCreateDelegate>? _staticVfxCreateHook;
    private readonly Hook<StaticVfxRemoveDelegate>? _staticVfxRemoveHook;
    private readonly Hook<ActorVfxCreateDelegate>? _actorVfxCreateHook;
    private readonly Hook<ActorVfxRemoveDelegate>? _actorVfxRemoveHook;
    private readonly Hook<VfxUseTriggerDelegate>? _vfxUseTriggerHook;

    private readonly ObjectDisposalState _disposeState = new();
    private int _drainScheduled;

    public ObjectAssetObserver(
        ILogger<ObjectAssetObserver> logger,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner,
        Action<IReadOnlyList<ObjectAssetObservation>> observeBatch)
    {
        _logger = logger;
        _observeBatch = observeBatch;

        _getResourceSyncHook = ObjectInteropHookUtility.CreateHookFromAddress<GetResourceSyncDelegate>(
            _logger,
            gameInteropProvider,
            ObjectSignatures.AssetResourceSync,
            GetResourceSyncDetour);
        _getResourceAsyncHook = ObjectInteropHookUtility.CreateHookFromAddress<GetResourceAsyncDelegate>(
            _logger,
            gameInteropProvider,
            ObjectSignatures.AssetResourceAsync,
            GetResourceAsyncDetour);
        _staticVfxCreateHook = ObjectInteropHookUtility.CreateHookFromAddress<StaticVfxCreateDelegate>(
            _logger,
            gameInteropProvider,
            ObjectSignatures.AssetStaticVfxCreate,
            StaticVfxCreateDetour);
        _staticVfxRemoveHook = ObjectInteropHookUtility.CreateHook<StaticVfxRemoveDelegate>(
            _logger,
            gameInteropProvider,
            sigScanner,
            ObjectSignatures.AssetStaticVfxRemove,
            StaticVfxRemoveDetour);
        _actorVfxCreateHook = ObjectInteropHookUtility.CreateHook<ActorVfxCreateDelegate>(
            _logger,
            gameInteropProvider,
            sigScanner,
            ObjectSignatures.AssetActorVfxCreate,
            ActorVfxCreateDetour);
        _actorVfxRemoveHook = ObjectInteropHookUtility.CreateHookFromAddress<ActorVfxRemoveDelegate>(
            _logger,
            gameInteropProvider,
            ObjectNativeAddressResolver.TryResolveRipRelativePointerTarget(_logger, sigScanner, ObjectSignatures.AssetActorVfxRemove),
            ActorVfxRemoveDetour,
            ObjectSignatures.AssetActorVfxRemove);
        _vfxUseTriggerHook = ObjectInteropHookUtility.CreateHookFromAddress<VfxUseTriggerDelegate>(
            _logger,
            gameInteropProvider,
            ObjectNativeAddressResolver.TryResolveJmpCallTarget(_logger, sigScanner, ObjectSignatures.AssetVfxTrigger),
            VfxUseTriggerDetour,
            ObjectSignatures.AssetVfxTrigger);

        _getResourceSyncHook?.Enable();
        _getResourceAsyncHook?.Enable();
        _staticVfxCreateHook?.Enable();
        _staticVfxRemoveHook?.Enable();
        _actorVfxCreateHook?.Enable();
        _actorVfxRemoveHook?.Enable();
        _vfxUseTriggerHook?.Enable();
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        ObjectInteropHookUtility.DisposeHook(_getResourceSyncHook);
        ObjectInteropHookUtility.DisposeHook(_getResourceAsyncHook);
        ObjectInteropHookUtility.DisposeHook(_staticVfxCreateHook);
        ObjectInteropHookUtility.DisposeHook(_staticVfxRemoveHook);
        ObjectInteropHookUtility.DisposeHook(_actorVfxCreateHook);
        ObjectInteropHookUtility.DisposeHook(_actorVfxRemoveHook);
        ObjectInteropHookUtility.DisposeHook(_vfxUseTriggerHook);

        lock (_activeVfxLock)
        {
            _activeVfxPaths.Clear();
        }
    }

    private ResourceHandle* GetResourceSyncDetour(ResourceManager* resourceManager, ResourceCategory* category, uint* type, uint* hash, byte* pathPointer, ObjectGetResourceParameters* getResourceParameters, byte* file, uint line)
    {
        var path = ReadAnsiPath(pathPointer);
        var result = _getResourceSyncHook!.Original(resourceManager, category, type, hash, pathPointer, getResourceParameters, file, line);
        Enqueue(ObjectAssetObservationKind.ResourceLoad, path);
        return result;
    }

    private ResourceHandle* GetResourceAsyncDetour(ResourceManager* resourceManager, ResourceCategory* category, uint* type, uint* hash, byte* pathPointer, ObjectGetResourceParameters* getResourceParameters, byte isUnknown, byte* file, uint line)
    {
        var path = ReadAnsiPath(pathPointer);
        var result = _getResourceAsyncHook!.Original(resourceManager, category, type, hash, pathPointer, getResourceParameters, isUnknown, file, line);
        Enqueue(ObjectAssetObservationKind.ResourceLoad, path);
        return result;
    }

    private SceneVfxObject* StaticVfxCreateDetour(byte* path, byte* pool)
    {
        var result = _staticVfxCreateHook!.Original(path, pool);
        var normalizedPath = ReadAnsiPath(path);
        TrackActiveVfx((nint)result, normalizedPath);
        Enqueue(ObjectAssetObservationKind.StaticVfxCreate, normalizedPath);
        return result;
    }

    private void StaticVfxRemoveDetour(nint vfx)
    {
        RemoveTrackedVfx(vfx);
        _staticVfxRemoveHook!.Original(vfx);
    }

    private nint ActorVfxCreateDetour(byte* path, nint a2, nint a3, float a4, byte a5, ushort a6, byte a7)
    {
        var result = _actorVfxCreateHook!.Original(path, a2, a3, a4, a5, a6, a7);
        var normalizedPath = ReadAnsiPath(path);
        TrackActiveVfx(result, normalizedPath);
        Enqueue(ObjectAssetObservationKind.ActorVfxCreate, normalizedPath);
        return result;
    }

    private nint ActorVfxRemoveDetour(nint vfx, uint freeFlags)
    {
        RemoveTrackedVfx(vfx);
        return _actorVfxRemoveHook!.Original(vfx, freeFlags);
    }

    private void VfxUseTriggerDetour(nint vfx, uint triggerId)
    {
        _vfxUseTriggerHook!.Original(vfx, triggerId);
        if (TryGetTrackedVfxPath(vfx, out var path))
        {
            Enqueue(ObjectAssetObservationKind.TriggerUse, path);
        }
    }

    private void TrackActiveVfx(nint vfx, string path)
    {
        var normalizedPath = ObjectPathRules.NormalizeGamePath(path);
        if (vfx == nint.Zero || string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        lock (_activeVfxLock)
        {
            _activeVfxPaths[vfx] = normalizedPath;
        }
    }

    private void RemoveTrackedVfx(nint vfx)
    {
        if (vfx == nint.Zero)
        {
            return;
        }

        lock (_activeVfxLock)
        {
            _activeVfxPaths.Remove(vfx);
        }
    }

    private bool TryGetTrackedVfxPath(nint vfx, out string path)
    {
        lock (_activeVfxLock)
        {
            return _activeVfxPaths.TryGetValue(vfx, out path!);
        }
    }

    private void Enqueue(ObjectAssetObservationKind kind, string rawPath)
    {
        if (IsDisposing)
        {
            return;
        }

        var path = ObjectPathRules.NormalizeGamePath(rawPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _queue.Enqueue(new ObjectAssetObservation(kind, path));
        ScheduleDrain();
    }

    private void ScheduleDrain()
    {
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(DrainQueue);
    }

    private void DrainQueue()
    {
        try
        {
            while (!IsDisposing)
            {
                var batch = new List<ObjectAssetObservation>();
                var seenObservations = new HashSet<ObjectAssetObservation>();
                while (_queue.TryDequeue(out var observation))
                {
                    if (seenObservations.Add(observation))
                    {
                        batch.Add(observation);
                    }
                }

                if (batch.Count == 0)
                {
                    break;
                }

                _observeBatch(batch);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "object catalog asset observer failed while processing observations");
        }
        finally
        {
            Interlocked.Exchange(ref _drainScheduled, 0);
            if (!_queue.IsEmpty && !IsDisposing)
            {
                ScheduleDrain();
            }
        }
    }

    private bool IsDisposing
        => _disposeState.IsDisposing;

    private static string ReadAnsiPath(byte* pathPointer)
    {
        if (pathPointer == null)
        {
            return string.Empty;
        }

        var path = Marshal.PtrToStringAnsi((nint)pathPointer);
        if (string.IsNullOrWhiteSpace(path) || !IsAscii(path))
        {
            return string.Empty;
        }

        return path;
    }

    private static bool IsAscii(string value)
        => value.All(static c => c <= 0x7F);
}

