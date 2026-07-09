using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Resources;

/// <summary> rewrites vfx resource bytes before native avfx parsing </summary>
internal interface IVfxResourceRewriteService : IDisposable
{
    /// <summary>
    /// Enters a short lived vfx resource rewrite scope for one created vfx root path.
    /// </summary>
    /// <param name="rootPath">the resolved vfx root path being passed to native creation</param>
    /// <returns>a disposable scope that removes the active rewrite paths</returns>
    IDisposable EnterRewriteScope(ObjectResolvedRootPath rootPath);
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
    private readonly ActiveRewritePathRegistry _activePaths = new();
    private readonly Hook<VfxResourceBufferLoadDelegate>? _vfxResourceBufferLoadHook;
    private readonly ObjectLockedOnce _enableOnce = new();
    private readonly ObjectDisposalState _disposeState = new();

    public VfxResourceRewriteService(
        ILogger<VfxResourceRewriteService> logger,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner)
    {
        _logger = logger;
        _vfxResourceBufferLoadHook = ObjectInteropHookUtility.CreateHook<VfxResourceBufferLoadDelegate>(
            _logger,
            gameInteropProvider,
            sigScanner,
            ObjectSignatures.AvfxResourceBufferLoadHook,
            VfxResourceBufferLoadDetour);
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        ObjectInteropHookUtility.DisposeHook(_vfxResourceBufferLoadHook);
        _activePaths.Clear();
    }

    public IDisposable EnterRewriteScope(ObjectResolvedRootPath rootPath)
    {
        if (_disposeState.IsDisposing || !TryEnableHook())
        {
            return default(VfxRewriteScopeToken);
        }

        string[] paths = BuildRewritePaths(rootPath);
        if (paths.Length == 0)
        {
            return default(VfxRewriteScopeToken);
        }

        AddActivePaths(paths);
        return new VfxRewriteScopeToken(this, paths);
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
        try
        {
            TryRewriteAvfx(filePath, avfxData, dataSize, resourceHandle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "avfx resource rewrite failed for {Path}", FormatResourcePaths(ResolveResourcePaths(filePath, resourceHandle)));
        }

        return _vfxResourceBufferLoadHook!.Original(job, unknown0, filePath, avfxData, dataSize, resourceHandle, outputSize);
    }

    private void TryRewriteAvfx(byte* filePath, byte* avfxData, uint dataSize, ResourceHandle* resourceHandle)
    {
        if (_disposeState.IsDisposing
         || avfxData == null
         || dataSize == 0
         || dataSize > MaximumRewritableAvfxBytes)
        {
            return;
        }

        if (!TryGetActivePath(filePath, resourceHandle, out string resourcePath))
        {
            return;
        }

        Span<byte> avfxBytes = new(avfxData, checked((int)dataSize));
        try
        {
            byte[] rewrittenAvfx = avfxBytes.ToArray();
            if (!AvfxStandaloneRewriter.HasRewritableTimelineBindPoints(rewrittenAvfx)
             || !VfxAssetAnalyzer.TryAnalyzeAvfx(resourcePath, rewrittenAvfx, out VfxAnalysis analysis)
             || !AvfxRewritePolicy.CanRewriteForStandaloneSpawn(analysis))
            {
                return;
            }

            AvfxStandaloneRewriter.RewriteResult rewriteResult = AvfxStandaloneRewriter.RewriteForStandaloneSpawn(rewrittenAvfx);
            if (rewriteResult.TotalCount > 0)
            {
                rewrittenAvfx.CopyTo(avfxBytes);
                _logger.LogDebug(
                    "rewrote avfx for standalone spawn: capabilities={RewriteCapabilities}, timelineBindPoints={TimelineBindPointCount}, timelineBinders={TimelineBinderCount}, binderProperties={BinderPropertyCount}, path={Path}",
                    rewriteResult.AppliedCapabilities,
                    rewriteResult.TimelineBindPointCount,
                    rewriteResult.TimelineBinderCount,
                    rewriteResult.BinderPropertyCount,
                    resourcePath);
            }
        }
        finally
        {
            _activePaths.CompleteLoad(resourcePath);
        }
    }

    private bool TryGetActivePath(byte* filePath, ResourceHandle* resourceHandle, out string activePath)
    {
        if (ObjectResourcePathEncoding.TryReadActualScopedHandlePath(resourceHandle, out string actualScopedPath)
         && TryGetActivePath(actualScopedPath, out activePath))
        {
            return true;
        }

        if (ObjectResourcePathEncoding.TryReadHandlePath(resourceHandle, out string handlePath)
         && TryGetActivePath(handlePath, out activePath))
        {
            return true;
        }

        if (ObjectResourcePathEncoding.TryReadNativePath(filePath, out string nativePath)
         && TryGetActivePath(nativePath, out activePath))
        {
            return true;
        }

        activePath = string.Empty;
        return false;
    }

    private bool TryGetActivePath(string path, out string activePath)
    {
        string normalizedPath = ObjectResourcePathUtility.NormalizeTrackedPath(path);
        if (IsActiveAvfxPath(normalizedPath, out activePath))
        {
            return true;
        }

        string gamePath = ObjectMemoryResourcePathUtility.GetGamePathOrSelf(normalizedPath);
        if (!string.Equals(gamePath, normalizedPath, StringComparison.OrdinalIgnoreCase)
         && IsActiveAvfxPath(gamePath, out activePath))
        {
            return true;
        }

        activePath = string.Empty;
        return false;
    }

    private bool IsActiveAvfxPath(string path, out string activePath)
    {
        activePath = ObjectStringUtility.TrimOrEmpty(path);
        return activePath.Length > 0
            && GameAssetPathRules.IsFileKind(activePath, GameAssetFileKind.Avfx)
            && IsActivePath(activePath);
    }

    private static string FormatResourcePaths(IReadOnlyList<string> paths)
        => paths.Count == 0 ? "<unknown>" : string.Join(", ", paths);

    private void AddActivePaths(IReadOnlyList<string> paths)
        => _activePaths.Add(paths);

    private void ReleaseActivePaths(IReadOnlyList<string> paths)
        => _activePaths.Release(paths, ReleasedPathGraceMilliseconds);

    private bool IsActivePath(string path)
        => !_disposeState.IsDisposing && _activePaths.Contains(path);

    private bool TryEnableHook()
        => _enableOnce.TryExecute(
            () =>
            {
                _vfxResourceBufferLoadHook?.Enable();
                return _vfxResourceBufferLoadHook != null;
            },
            () => !_disposeState.IsDisposing);

    private static string[] ResolveResourcePaths(byte* filePath, ResourceHandle* resourceHandle)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

        if (ObjectResourcePathEncoding.TryReadActualScopedHandlePath(resourceHandle, out string actualScopedPath))
        {
            AddResourcePathAliases(paths, actualScopedPath);
        }

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
        AddAvfxPath(paths, normalizedPath);
        AddAvfxPath(paths, ObjectMemoryResourcePathUtility.GetGamePathOrSelf(normalizedPath));
    }

    private static void AddAvfxPath(HashSet<string> paths, string path)
    {
        string normalizedPath = ObjectStringUtility.TrimOrEmpty(path);
        if (normalizedPath.Length == 0
         || !GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
        {
            return;
        }

        paths.Add(normalizedPath);
    }

    private readonly struct VfxRewriteScopeToken(VfxResourceRewriteService? owner, IReadOnlyList<string>? paths) : IDisposable
    {
        public void Dispose()
        {
            if (paths is not null)
            {
                owner?.ReleaseActivePaths(paths);
            }
        }
    }

    private sealed class ActiveRewritePathRegistry
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, ActiveRewritePath> _paths = new(StringComparer.OrdinalIgnoreCase);

        public void Add(IReadOnlyList<string> paths)
        {
            lock (_lock)
            {
                ClearExpiredPaths(Environment.TickCount64);
                ActiveRewritePath activePath = GetOrAddGroup(paths);
                activePath.ScopeCount++;
                activePath.PendingLoadCount++;
                activePath.ExpiresAtMilliseconds = 0;
            }
        }

        public void Release(IReadOnlyList<string> paths, long releasedPathLifetimeMilliseconds)
        {
            long expiresAtMilliseconds = Environment.TickCount64 + releasedPathLifetimeMilliseconds;
            lock (_lock)
            {
                ClearExpiredPaths(Environment.TickCount64);
                foreach (ActiveRewritePath activePath in ResolveGroups(paths))
                {
                    if (activePath.ScopeCount > 0)
                    {
                        activePath.ScopeCount--;
                    }

                    if (activePath.ScopeCount > 0)
                    {
                        continue;
                    }

                    if (activePath.PendingLoadCount <= 0)
                    {
                        RemoveGroup(activePath);
                        continue;
                    }

                    activePath.ExpiresAtMilliseconds = expiresAtMilliseconds;
                }
            }
        }

        public bool Contains(string path)
        {
            long nowMilliseconds = Environment.TickCount64;
            lock (_lock)
            {
                if (!_paths.TryGetValue(path, out ActiveRewritePath? activePath))
                {
                    return false;
                }

                if (activePath.IsActive(nowMilliseconds))
                {
                    return true;
                }

                RemoveGroup(activePath);
                return false;
            }
        }

        public void CompleteLoad(string path)
        {
            lock (_lock)
            {
                if (!_paths.TryGetValue(path, out ActiveRewritePath? activePath))
                {
                    return;
                }

                if (activePath.PendingLoadCount > 0)
                {
                    activePath.PendingLoadCount--;
                }

                if (activePath.ScopeCount == 0 && activePath.PendingLoadCount <= 0)
                {
                    RemoveGroup(activePath);
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

        private ActiveRewritePath GetOrAddGroup(IReadOnlyList<string> paths)
        {
            ActiveRewritePath? activePath = null;
            foreach (string path in paths)
            {
                if (!_paths.TryGetValue(path, out ActiveRewritePath? existingPath))
                {
                    continue;
                }

                if (activePath is null)
                {
                    activePath = existingPath;
                    continue;
                }

                if (!ReferenceEquals(activePath, existingPath))
                {
                    MergeGroups(activePath, existingPath);
                }
            }

            activePath ??= new ActiveRewritePath();
            foreach (string path in paths)
            {
                activePath.Paths.Add(path);
                _paths[path] = activePath;
            }

            return activePath;
        }

        private void ClearExpiredPaths(long nowMilliseconds)
        {
            HashSet<ActiveRewritePath> activePaths = [];
            foreach (ActiveRewritePath activePath in _paths.Values)
            {
                activePaths.Add(activePath);
            }

            foreach (ActiveRewritePath activePath in activePaths)
            {
                if (!activePath.IsActive(nowMilliseconds))
                {
                    RemoveGroup(activePath);
                }
            }
        }

        private HashSet<ActiveRewritePath> ResolveGroups(IReadOnlyList<string> paths)
        {
            HashSet<ActiveRewritePath> activePaths = [];
            foreach (string path in paths)
            {
                if (_paths.TryGetValue(path, out ActiveRewritePath? activePath))
                {
                    activePaths.Add(activePath);
                }
            }

            return activePaths;
        }

        private void MergeGroups(ActiveRewritePath target, ActiveRewritePath source)
        {
            target.ScopeCount += source.ScopeCount;
            target.PendingLoadCount += source.PendingLoadCount;
            target.ExpiresAtMilliseconds = Math.Max(target.ExpiresAtMilliseconds, source.ExpiresAtMilliseconds);
            foreach (string path in source.Paths)
            {
                target.Paths.Add(path);
                _paths[path] = target;
            }
        }

        private void RemoveGroup(ActiveRewritePath activePath)
        {
            foreach (string path in activePath.Paths)
            {
                _paths.Remove(path);
            }
        }
    }

    private sealed class ActiveRewritePath
    {
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ScopeCount { get; set; }
        public int PendingLoadCount { get; set; }
        public long ExpiresAtMilliseconds { get; set; }

        public bool IsActive(long nowMilliseconds)
            => ScopeCount > 0 || ExpiresAtMilliseconds > nowMilliseconds;
    }
}
