using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using InteropGenerator.Runtime;
using Intoner.Objects.Resources;
using Intoner.Services.Interop;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using SceneVfxObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject;

namespace Intoner.Objects.Assets;

internal enum ObjectAssetObservationKind
{
    ResourceLoad,
    StaticVfxCreate,
    ActorVfxCreate,
}

internal readonly record struct ObjectAssetObservation(ObjectAssetObservationKind Kind, string Path);

internal sealed unsafe class ObjectAssetObserver : IDisposable
{
    private delegate nint ActorVfxCreateDelegate(byte* path, nint a2, nint a3, float a4, byte a5, ushort a6, byte a7);

    private readonly ILogger<ObjectAssetObserver> _logger;
    private readonly ConcurrentQueue<ObjectAssetObservation> _queue = [];
    private readonly Action<IReadOnlyList<ObjectAssetObservation>> _observeBatch;

    private readonly InteropHookCollection _hooks;

    private readonly Hook<ObjectResourceHooks.GetResourceSyncDelegate>? _getResourceSyncHook;
    private readonly Hook<ObjectResourceHooks.GetResourceAsyncDelegate>? _getResourceAsyncHook;
    private readonly Hook<SceneVfxObject.Delegates.Create>? _staticVfxCreateHook;
    private readonly Hook<ActorVfxCreateDelegate>? _actorVfxCreateHook;

    private readonly DisposalState _disposeState = new();
    private int _drainScheduled;

    public ObjectAssetObserver(
        ILogger<ObjectAssetObserver> logger,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner,
        Action<IReadOnlyList<ObjectAssetObservation>> observeBatch)
    {
        _logger = logger;
        _observeBatch = observeBatch;

        _hooks = new InteropHookCollection(logger);
        try
        {
            _getResourceSyncHook = _hooks.Add(InteropHookUtility.CreateHookFromAddress<ObjectResourceHooks.GetResourceSyncDelegate>(
                _logger,
                gameInteropProvider,
                IntonerSignatures.ResourceSync,
                GetResourceSyncDetour));
            _getResourceAsyncHook = _hooks.Add(InteropHookUtility.CreateHookFromAddress<ObjectResourceHooks.GetResourceAsyncDelegate>(
                _logger,
                gameInteropProvider,
                IntonerSignatures.ResourceAsync,
                GetResourceAsyncDetour));
            _staticVfxCreateHook = _hooks.Add(InteropHookUtility.CreateHookFromAddress<SceneVfxObject.Delegates.Create>(
                _logger,
                gameInteropProvider,
                IntonerSignatures.AssetStaticVfxCreate,
                StaticVfxCreateDetour));
            _actorVfxCreateHook = _hooks.Add(InteropHookUtility.CreateHook<ActorVfxCreateDelegate>(
                _logger,
                gameInteropProvider,
                sigScanner,
                IntonerSignatures.AssetActorVfxCreate,
                ActorVfxCreateDetour));
            _hooks.Enable();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose() && _hooks.IsDisposed)
        {
            return;
        }

        _hooks.Dispose();
    }

    private ResourceHandle* GetResourceSyncDetour(ResourceManager* resourceManager, ResourceHandleType* handleType, uint* type, uint* hash, byte* pathPointer, ObjectGetResourceParameters* getResourceParameters, byte* file, uint line)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        if (!callback.IsActive || IsDisposing)
        {
            return _getResourceSyncHook!.OriginalDisposeSafe(resourceManager, handleType, type, hash, pathPointer, getResourceParameters, file, line);
        }

        var path = ReadNativePath(pathPointer);
        var result = _getResourceSyncHook!.OriginalDisposeSafe(resourceManager, handleType, type, hash, pathPointer, getResourceParameters, file, line);
        Enqueue(ObjectAssetObservationKind.ResourceLoad, path);
        return result;
    }

    private ResourceHandle* GetResourceAsyncDetour(ResourceManager* resourceManager, ResourceHandleType* handleType, uint* type, uint* hash, byte* pathPointer, ObjectGetResourceParameters* getResourceParameters, byte hasHandleLock, byte* file, uint line)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        if (!callback.IsActive || IsDisposing)
        {
            return _getResourceAsyncHook!.OriginalDisposeSafe(resourceManager, handleType, type, hash, pathPointer, getResourceParameters, hasHandleLock, file, line);
        }

        var path = ReadNativePath(pathPointer);
        var result = _getResourceAsyncHook!.OriginalDisposeSafe(resourceManager, handleType, type, hash, pathPointer, getResourceParameters, hasHandleLock, file, line);
        Enqueue(ObjectAssetObservationKind.ResourceLoad, path);
        return result;
    }

    private SceneVfxObject* StaticVfxCreateDetour(CStringPointer path, CStringPointer pool)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        if (!callback.IsActive || IsDisposing)
        {
            return _staticVfxCreateHook!.OriginalDisposeSafe(path, pool);
        }

        var normalizedPath = ReadNativePath(path);
        var result = _staticVfxCreateHook!.OriginalDisposeSafe(path, pool);
        ObserveCreatedVfx((nint)result, normalizedPath, ObjectAssetObservationKind.StaticVfxCreate);
        return result;
    }

    private nint ActorVfxCreateDetour(byte* path, nint a2, nint a3, float a4, byte a5, ushort a6, byte a7)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        if (!callback.IsActive || IsDisposing)
        {
            return _actorVfxCreateHook!.OriginalDisposeSafe(path, a2, a3, a4, a5, a6, a7);
        }

        var normalizedPath = ReadNativePath(path);
        var result = _actorVfxCreateHook!.OriginalDisposeSafe(path, a2, a3, a4, a5, a6, a7);
        ObserveCreatedVfx(result, normalizedPath, ObjectAssetObservationKind.ActorVfxCreate);
        return result;
    }

    private void ObserveCreatedVfx(nint vfx, string path, ObjectAssetObservationKind kind)
    {
        if (vfx == nint.Zero)
        {
            return;
        }

        Enqueue(kind, path);
    }

    private void Enqueue(ObjectAssetObservationKind kind, string rawPath)
    {
        if (IsDisposing)
        {
            return;
        }

        string path = NormalizeObservedPath(rawPath);
        if (string.IsNullOrWhiteSpace(path)
         || (kind == ObjectAssetObservationKind.ResourceLoad
          && !ObjectAssetPathRules.IsRelevantRuntimeCapturePath(path)))
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
                var seenObservations = new HashSet<ObjectAssetObservation>(ObjectAssetObservationComparer.Instance);
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

    private static string NormalizeObservedPath(string path)
    {
        string normalizedPath = ObjectResourcePathUtility.NormalizeTrackedPath(path);
        return ObjectMemoryResourcePathUtility.GetGamePathOrSelf(normalizedPath);
    }

    private static string ReadNativePath(byte* pathPointer)
        => ObjectResourcePathEncoding.TryReadNativePath(pathPointer, out string path) ? path : string.Empty;

    private sealed class ObjectAssetObservationComparer : IEqualityComparer<ObjectAssetObservation>
    {
        public static ObjectAssetObservationComparer Instance { get; } = new();

        public bool Equals(ObjectAssetObservation left, ObjectAssetObservation right)
            => left.Kind == right.Kind
            && string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ObjectAssetObservation observation)
            => HashCode.Combine(
                observation.Kind,
                StringComparer.OrdinalIgnoreCase.GetHashCode(observation.Path));
    }
}
