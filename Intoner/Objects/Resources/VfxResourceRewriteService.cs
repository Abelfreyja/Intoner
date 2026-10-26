using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using Intoner.Services.Interop;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using Penumbra.Api.Enums;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Resources;

/// <summary> rewrites vfx resource bytes before native avfx parsing </summary>
internal interface IVfxResourceRewriteService : IDisposable
{
    /// <summary>
    /// Enters a short lived vfx resource rewrite scope for one created vfx root path.
    /// </summary>
    /// <param name="rootPath">the resolved vfx root path being passed to native creation</param>
    /// <param name="isolatedResourceHash">the isolated cache identity for an unmodded root, if available</param>
    /// <returns>a disposable scope that releases the registration, or an empty scope when rewriting is unavailable or the service is shutting down</returns>
    IDisposable EnterRewriteScope(ObjectResolvedRootPath rootPath, uint? isolatedResourceHash);
}

internal sealed unsafe class VfxResourceRewriteService : IVfxResourceRewriteService
{
    private delegate nint VfxResourceBufferLoadDelegate(
        void* job,
        nint unknown0,
        byte* filePath,
        byte* avfxData,
        uint dataSize,
        ResourceHandle* resourceHandle,
        int* outputSize);

    private const uint MaximumRewritableAvfxBytes = 128u * 1024u * 1024u;
    private const long ReleasedPathGraceMilliseconds = 30_000;

    private readonly ILogger<VfxResourceRewriteService> _logger;
    private readonly ObjectResourceLoadScope _loadScope;
    private readonly ActiveRewritePathRegistry _activePaths = new();
    private readonly InteropHookCollection _hooks;
    private readonly Hook<VfxResourceBufferLoadDelegate>? _vfxResourceBufferLoadHook;
    private readonly DisposalState _disposeState = new();

    public VfxResourceRewriteService(
        ILogger<VfxResourceRewriteService> logger,
        ObjectResourceLoadScope loadScope,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner)
    {
        _logger = logger;
        _loadScope = loadScope;
        _hooks = new InteropHookCollection(logger);
        _vfxResourceBufferLoadHook = _hooks.Add(InteropHookUtility.CreateHook<VfxResourceBufferLoadDelegate>(
            _logger,
            gameInteropProvider,
            sigScanner,
            IntonerSignatures.AvfxResourceBufferLoadHook,
            VfxResourceBufferLoadDetour));
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose() && _hooks.IsDisposed)
        {
            return;
        }

        _hooks.Dispose();

        if (_hooks.IsDisposed)
        {
            _activePaths.Clear();
        }
    }

    public IDisposable EnterRewriteScope(ObjectResolvedRootPath rootPath, uint? isolatedResourceHash)
    {
        using InteropHookCollection.CallbackScope operation = _hooks.EnterOperation();

        if (!operation.IsActive || _disposeState.IsDisposing)
        {
            return VfxRewriteScopeToken.Empty;
        }

        VfxResourceIdentity? identity = ResolveResourceIdentity(rootPath, isolatedResourceHash);

        if (!identity.HasValue
            || !GameAssetPathRules.IsFileKind(rootPath.RequestedPath, GameAssetFileKind.Avfx)
            || !TryEnableHook())
        {
            return VfxRewriteScopeToken.Empty;
        }

        string[] paths = BuildRewritePaths(rootPath);

        if (paths.Length == 0)
        {
            return VfxRewriteScopeToken.Empty;
        }

        ActiveRewritePath activePath = _activePaths.Add(identity.Value, paths, rootPath.RequestedPath);
        return new VfxRewriteScopeToken(_activePaths, activePath);
    }

    private VfxResourceIdentity? ResolveResourceIdentity(ObjectResolvedRootPath rootPath, uint? isolatedResourceHash)
    {
        if (rootPath.ResourceCollectionId.Length == 0)
        {
            return isolatedResourceHash.HasValue ? new VfxResourceIdentity(isolatedResourceHash.Value, 0) : null;
        }

        if (!_loadScope.TryReadActiveCollection(out ObjectCollectionResolveData collection)
            || !string.Equals(collection.CollectionId, rootPath.ResourceCollectionId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string handlePath = ObjectScopedResourcePathUtility.Create(collection.ResourceScopeId, rootPath.ResolvedPath);
        return handlePath.Length > 0
            ? new VfxResourceIdentity(unchecked((uint)ObjectResourcePathEncoding.ComputeResourceHash(handlePath, null)), collection.ResourceScopeId)
            : null;
    }

    private nint VfxResourceBufferLoadDetour(
        void* job,
        nint unknown0,
        byte* filePath,
        byte* avfxData,
        uint dataSize,
        ResourceHandle* resourceHandle,
        int* outputSize)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        try
        {
            TryRewriteAvfx(filePath, avfxData, dataSize, resourceHandle);
        }
        catch (Exception ex)
        {
            string paths = FormatResourcePaths(ResolveResourcePaths(filePath, resourceHandle));
            ObjectResourceLog.Write(() => _logger.LogError(ex, "avfx resource rewrite failed for {Path}", paths));
        }

        return _vfxResourceBufferLoadHook!.OriginalDisposeSafe(job, unknown0, filePath, avfxData, dataSize, resourceHandle, outputSize);
    }

    private void TryRewriteAvfx(byte* filePath, byte* avfxData, uint dataSize, ResourceHandle* resourceHandle)
    {
        if (_disposeState.IsDisposing
         || avfxData == null
         || resourceHandle == null
         || dataSize == 0
         || dataSize > MaximumRewritableAvfxBytes)
        {
            return;
        }

        if (!TryGetActivePath(filePath, resourceHandle, out ActiveRewritePath? activePath))
        {
            return;
        }

        Span<byte> avfxBytes = new(avfxData, checked((int)dataSize));
        try
        {
            if (!AvfxStandaloneRewriter.HasRewritableTimelineBindPoints(avfxBytes))
            {
                return;
            }

            byte[] rewrittenAvfx = avfxBytes.ToArray();

            if (!VfxAssetAnalyzer.TryAnalyzeAvfx(activePath.GamePath, rewrittenAvfx, out VfxAnalysis analysis)
             || !AvfxRewritePolicy.CanRewriteForStandaloneSpawn(analysis))
            {
                return;
            }

            AvfxStandaloneRewriter.RewriteResult rewriteResult = AvfxStandaloneRewriter.RewriteForStandaloneSpawn(rewrittenAvfx);
            if (rewriteResult.TotalCount > 0)
            {
                rewrittenAvfx.CopyTo(avfxBytes);
                ObjectResourceLog.Write(() => _logger.LogDebug(
                    "rewrote avfx for standalone spawn: capabilities={RewriteCapabilities}, timelineBindPoints={TimelineBindPointCount}, timelineBinders={TimelineBinderCount}, binderProperties={BinderPropertyCount}, path={Path}",
                    rewriteResult.AppliedCapabilities,
                    rewriteResult.TimelineBindPointCount,
                    rewriteResult.TimelineBinderCount,
                    rewriteResult.BinderPropertyCount,
                    activePath.GamePath));
            }
        }
        finally
        {
            _activePaths.CompleteLoad(activePath);
        }
    }

    private bool TryGetActivePath(byte* filePath, ResourceHandle* resourceHandle, [NotNullWhen(true)] out ActiveRewritePath? activePath)
    {
        activePath = null;

        if (_disposeState.IsDisposing || resourceHandle == null || resourceHandle->FileType != (uint)ResourceType.Avfx)
        {
            return false;
        }

        uint resourceHash = resourceHandle->Id;
        bool hasHandlePath = ObjectResourcePathEncoding.TryReadHandlePath(resourceHandle, out string handlePath);
        bool hasFilePath = ObjectResourcePathEncoding.TryReadNativePath(filePath, out string nativePath);
        long? handleScopeId = null;
        long? fileScopeId = null;

        if (hasHandlePath && !TryNormalizeRewritePath(handlePath, out handlePath, out handleScopeId)
         || hasFilePath && !TryNormalizeRewritePath(nativePath, out nativePath, out fileScopeId)
         || handleScopeId.HasValue && fileScopeId.HasValue && handleScopeId != fileScopeId)
        {
            return false;
        }

        return _activePaths.TryGetPath(resourceHash, handleScopeId ?? fileScopeId, handlePath, nativePath, out activePath);
    }

    private static bool TryNormalizeRewritePath(string path, out string normalizedPath, out long? resourceScopeId)
    {
        normalizedPath = string.Empty;
        resourceScopeId = null;

        if (ObjectScopedResourcePathUtility.IsObjectScopedPath(path))
        {
            if (!ObjectScopedResourcePathUtility.TryParse(path, out ObjectScopedResourcePath scopedPath))
            {
                return false;
            }

            resourceScopeId = scopedPath.ResourceScopeId;
            path = scopedPath.Path;
        }

        if (!ObjectResolvedPath.TryCreate(path, out ObjectResolvedPath resolvedPath))
        {
            return false;
        }

        normalizedPath = resolvedPath.Path;
        return true;
    }

    private static string FormatResourcePaths(IReadOnlyList<string> paths)
        => paths.Count == 0 ? "<unknown>" : string.Join(", ", paths);

    private bool TryEnableHook()
        => !_disposeState.IsDisposing && _vfxResourceBufferLoadHook != null && _hooks.Enable();

    private static string[] ResolveResourcePaths(byte* filePath, ResourceHandle* resourceHandle)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

        if (ObjectResourcePathEncoding.TryReadHandlePath(resourceHandle, out string handlePath))
        {
            AddResourcePathAliases(paths, handlePath);
        }

        if (ObjectResourcePathEncoding.TryReadNativePath(filePath, out string nativePath))
        {
            AddResourcePathAliases(paths, nativePath);
        }

        return paths.ToArray();
    }

    private static string[] BuildRewritePaths(ObjectResolvedRootPath rootPath)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        AddResourcePathAliases(paths, rootPath.RequestedPath);
        AddResourcePathAliases(paths, rootPath.CreatePath);
        AddResourcePathAliases(paths, rootPath.ResolvedPath);
        return paths.ToArray();
    }

    private static void AddResourcePathAliases(HashSet<string> paths, string path)
    {
        string normalizedPath = ObjectResourcePathUtility.NormalizeTrackedPath(path);
        AddRewritePath(paths, normalizedPath);
        AddRewritePath(paths, ObjectMemoryResourcePathUtility.GetGamePathOrSelf(normalizedPath));
    }

    private static void AddRewritePath(HashSet<string> paths, string path)
    {
        string normalizedPath = TextUtility.TrimOrEmpty(path);

        if (normalizedPath.Length == 0)
        {
            return;
        }

        paths.Add(normalizedPath);
    }

    private sealed class VfxRewriteScopeToken(ActiveRewritePathRegistry? owner, ActiveRewritePath? activePath) : IDisposable
    {
        public static readonly IDisposable Empty = new VfxRewriteScopeToken(null, null);
        private ActiveRewritePathRegistry? _owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.Release(activePath!, ReleasedPathGraceMilliseconds);
    }

    private sealed class ActiveRewritePathRegistry
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<uint, List<ActiveRewritePath>> _paths = [];

        public ActiveRewritePath Add(VfxResourceIdentity identity, IReadOnlyList<string> paths, string gamePath)
        {
            lock (_lock)
            {
                ClearExpiredPaths(Environment.TickCount64);

                if (!_paths.TryGetValue(identity.ResourceHash, out List<ActiveRewritePath>? registrations))
                {
                    registrations = [];
                    _paths.Add(identity.ResourceHash, registrations);
                }

                ActiveRewritePath? activePath = null;

                foreach (ActiveRewritePath candidate in registrations)
                {
                    if (candidate.Identity == identity
                        && string.Equals(candidate.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)
                        && candidate.Paths.SetEquals(paths))
                    {
                        activePath = candidate;
                        break;
                    }
                }

                if (activePath == null)
                {
                    activePath = new ActiveRewritePath(identity, gamePath, paths);
                    registrations.Add(activePath);
                }

                activePath.ScopeCount++;
                activePath.PendingLoadCount++;
                activePath.ExpiresAtMilliseconds = 0;
                return activePath;
            }
        }

        public void Release(ActiveRewritePath activePath, long releasedPathLifetimeMilliseconds)
        {
            long expiresAtMilliseconds = Environment.TickCount64 + releasedPathLifetimeMilliseconds;

            lock (_lock)
            {
                ClearExpiredPaths(Environment.TickCount64);

                if (!IsCurrent(activePath))
                {
                    return;
                }

                if (activePath.ScopeCount > 0)
                {
                    activePath.ScopeCount--;
                }

                if (activePath.ScopeCount > 0)
                {
                    return;
                }

                if (activePath.PendingLoadCount <= 0)
                {
                    Remove(activePath);
                    return;
                }

                activePath.ExpiresAtMilliseconds = expiresAtMilliseconds;
            }
        }

        public bool TryGetPath(uint resourceHash, long? resourceScopeId, string handlePath, string filePath,
            [NotNullWhen(true)] out ActiveRewritePath? activePath)
        {
            activePath = null;

            if (handlePath.Length == 0 && filePath.Length == 0)
            {
                return false;
            }

            long nowMilliseconds = Environment.TickCount64;

            lock (_lock)
            {
                if (!_paths.TryGetValue(resourceHash, out List<ActiveRewritePath>? registrations))
                {
                    return false;
                }

                RemoveExpiredPaths(registrations, nowMilliseconds);

                if (registrations.Count == 0)
                {
                    _paths.Remove(resourceHash);
                    return false;
                }

                foreach (ActiveRewritePath candidate in registrations)
                {
                    if (resourceScopeId.HasValue && resourceScopeId.Value != candidate.Identity.ResourceScopeId
                        || handlePath.Length > 0 && !candidate.Paths.Contains(handlePath)
                        || filePath.Length > 0 && !candidate.Paths.Contains(filePath))
                    {
                        continue;
                    }

                    if (activePath != null)
                    {
                        activePath = null;
                        return false;
                    }

                    activePath = candidate;
                }

                return activePath != null;
            }
        }

        public void CompleteLoad(ActiveRewritePath activePath)
        {
            lock (_lock)
            {
                if (!IsCurrent(activePath))
                {
                    return;
                }

                if (activePath.PendingLoadCount > 0)
                {
                    activePath.PendingLoadCount--;
                }

                if (activePath.ScopeCount == 0 && activePath.PendingLoadCount <= 0)
                {
                    Remove(activePath);
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _paths.Clear();
            }
        }

        private void ClearExpiredPaths(long nowMilliseconds)
        {
            foreach ((uint resourceHash, List<ActiveRewritePath> registrations) in _paths)
            {
                RemoveExpiredPaths(registrations, nowMilliseconds);

                if (registrations.Count == 0)
                {
                    _paths.Remove(resourceHash);
                }
            }
        }

        private static void RemoveExpiredPaths(List<ActiveRewritePath> registrations, long nowMilliseconds)
        {
            for (int index = registrations.Count - 1; index >= 0; --index)
            {
                if (!registrations[index].IsActive(nowMilliseconds))
                {
                    registrations.RemoveAt(index);
                }
            }
        }

        private void Remove(ActiveRewritePath activePath)
        {
            if (_paths.TryGetValue(activePath.Identity.ResourceHash, out List<ActiveRewritePath>? registrations)
                && registrations.Remove(activePath) && registrations.Count == 0)
            {
                _paths.Remove(activePath.Identity.ResourceHash);
            }
        }

        private bool IsCurrent(ActiveRewritePath activePath)
            => _paths.TryGetValue(activePath.Identity.ResourceHash, out List<ActiveRewritePath>? registrations)
                && registrations.Contains(activePath);
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct VfxResourceIdentity(uint ResourceHash, long ResourceScopeId);

    internal sealed class ActiveRewritePath(VfxResourceIdentity identity, string gamePath, IReadOnlyList<string> paths)
    {
        public VfxResourceIdentity Identity { get; } = identity;
        public string GamePath { get; } = gamePath;
        public HashSet<string> Paths { get; } = new(paths, StringComparer.OrdinalIgnoreCase);
        public int ScopeCount { get; set; }
        public int PendingLoadCount { get; set; }
        public long ExpiresAtMilliseconds { get; set; }

        public bool IsActive(long nowMilliseconds)
            => ScopeCount > 0 || ExpiresAtMilliseconds > nowMilliseconds;
    }
}
