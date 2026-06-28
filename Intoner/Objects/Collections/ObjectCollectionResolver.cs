using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using System.Collections.Immutable;

namespace Intoner.Objects.Collections;

internal sealed record ObjectCollectionResolveResult
{
    public ObjectCollectionResolveState ResolveState { get; init; } = ObjectCollectionResolveState.Inactive;
    public string StatusText { get; init; } = string.Empty;
    public bool KeepLastGoodSnapshot { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<ObjectPathRedirection> Redirects { get; init; } = [];
    public IReadOnlyList<CollectionResourceView> ResourceViews { get; init; } = [];
}

/// <summary> resolves authored object collections into runtime resource snapshots </summary>
internal interface IObjectCollectionResolver
{
    /// <summary> resolves one authored object collection into runtime redirect rules </summary>
    /// <param name="collection">the authored collection to resolve</param>
    /// <param name="usageSnapshots">the current object snapshots using that collection</param>
    /// <param name="cancellationToken">the cancellation token for the operation</param>
    /// <returns>the resolve result for that collection</returns>
    Task<ObjectCollectionResolveResult> ResolveAsync(
        ObjectCollection collection,
        IReadOnlyList<ObjectSnapshot> usageSnapshots,
        CancellationToken cancellationToken);
}

internal sealed class ObjectCollectionResolver : IObjectCollectionResolver
{
    private readonly IObjectModDataSource _modDataSource;
    private readonly IObjectAssetIndex _assetIndex;

    public ObjectCollectionResolver(IObjectModDataSource modDataSource, IObjectAssetIndex assetIndex)
    {
        _modDataSource = modDataSource;
        _assetIndex = assetIndex;
    }

    public async Task<ObjectCollectionResolveResult> ResolveAsync(
        ObjectCollection collection,
        IReadOnlyList<ObjectSnapshot> usageSnapshots,
        CancellationToken cancellationToken)
    {
        if (usageSnapshots.Count == 0)
        {
            return new ObjectCollectionResolveResult
            {
                ResolveState = ObjectCollectionResolveState.Inactive,
                StatusText = "collection has no active object usage",
            };
        }

        Dictionary<string, ObjectResolvedPath> resolvedPaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> reachablePaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> probedPaths = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> pendingPaths = new();
        List<string> warnings = [];

        AddUsageRootPaths(usageSnapshots, reachablePaths, pendingPaths);
        if (reachablePaths.Count == 0)
        {
            return new ObjectCollectionResolveResult
            {
                ResolveState = ObjectCollectionResolveState.Inactive,
                StatusText = "collection has no object resource paths in active usage",
            };
        }

        while (pendingPaths.Count > 0)
        {
            HashSet<string> requestedPaths = DrainPendingPaths(pendingPaths, probedPaths);
            if (requestedPaths.Count == 0)
            {
                continue;
            }

            ObjectModResolveResult resolveResult = await _modDataSource.ResolvePathsAsync(
                collection,
                requestedPaths,
                cancellationToken).ConfigureAwait(false);
            warnings.AddRange(resolveResult.Warnings);
            if (resolveResult.ResolveState != ObjectCollectionResolveState.Ready)
            {
                return new ObjectCollectionResolveResult
                {
                    ResolveState = resolveResult.ResolveState,
                    StatusText = resolveResult.StatusText,
                    Warnings = ObjectCollectionDiagnosticUtility.NormalizeWarnings(warnings),
                    KeepLastGoodSnapshot = resolveResult.KeepLastGoodSnapshot,
                };
            }

            foreach (string requestedPath in requestedPaths)
            {
                probedPaths.Add(requestedPath);
            }

            foreach ((string requestedPath, ObjectResolvedPath resolvedPath) in resolveResult.ResolvedPaths)
            {
                if (reachablePaths.Contains(requestedPath))
                {
                    resolvedPaths[requestedPath] = resolvedPath;
                }
            }

            foreach (string requestedPath in requestedPaths)
            {
                ObjectResolvedPath effectivePath = resolvedPaths.TryGetValue(requestedPath, out ObjectResolvedPath resolvedPath)
                    ? resolvedPath
                    : ObjectResolvedPath.FromGamePath(requestedPath);
                foreach (string dependencyPath in _assetIndex.GetCollectionPathDependencies(requestedPath, effectivePath))
                {
                    AddReachablePath(reachablePaths, pendingPaths, dependencyPath);
                }
            }
        }

        if (resolvedPaths.Count == 0)
        {
            return new ObjectCollectionResolveResult
            {
                ResolveState = ObjectCollectionResolveState.Inactive,
                StatusText = "assigned Penumbra mods expose no redirects used by the active objects in this collection",
                Warnings = ObjectCollectionDiagnosticUtility.NormalizeWarnings(warnings),
                ResourceViews = BuildResourceViews(usageSnapshots, resolvedPaths),
            };
        }

        ImmutableArray<ObjectPathRedirection>.Builder redirectBuilder = ImmutableArray.CreateBuilder<ObjectPathRedirection>(resolvedPaths.Count);
        foreach ((string requestedPath, ObjectResolvedPath resolvedPath) in resolvedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reachablePaths.Contains(requestedPath))
            {
                continue;
            }

            if (ObjectPathRedirectionUtility.TryCreate(requestedPath, resolvedPath, out ObjectPathRedirection redirect))
            {
                redirectBuilder.Add(redirect);
            }
        }

        return new ObjectCollectionResolveResult
        {
            ResolveState = redirectBuilder.Count == 0
                ? ObjectCollectionResolveState.Inactive
                : ObjectCollectionResolveState.Ready,
            StatusText = redirectBuilder.Count == 0
                ? "assigned Penumbra mods expose no redirects used by the active objects in this collection"
                : $"resolved {redirectBuilder.Count} redirected object resource paths used by {usageSnapshots.Count} objects",
            Warnings = ObjectCollectionDiagnosticUtility.NormalizeWarnings(warnings),
            Redirects = redirectBuilder.ToImmutable(),
            ResourceViews = BuildResourceViews(usageSnapshots, resolvedPaths),
        };
    }

    private static void AddUsageRootPaths(
        IReadOnlyList<ObjectSnapshot> usageSnapshots,
        HashSet<string> reachablePaths,
        Queue<string> pendingPaths)
    {
        foreach (ObjectSnapshot snapshot in usageSnapshots)
        {
            AddReachablePath(reachablePaths, pendingPaths, ObjectSnapshotUtility.GetRootResourcePath(snapshot));
        }
    }

    private static HashSet<string> DrainPendingPaths(Queue<string> pendingPaths, ISet<string> probedPaths)
    {
        HashSet<string> requestedPaths = new(StringComparer.OrdinalIgnoreCase);
        while (pendingPaths.Count > 0)
        {
            string requestedPath = pendingPaths.Dequeue();
            if (!probedPaths.Contains(requestedPath))
            {
                requestedPaths.Add(requestedPath);
            }
        }

        return requestedPaths;
    }

    private static void AddReachablePath(
        ISet<string> reachablePaths,
        Queue<string> pendingPaths,
        string path)
    {
        if (!ObjectPathRules.TryNormalizeSupportedObjectResourcePath(path, out string normalizedPath)
            || !reachablePaths.Add(normalizedPath))
        {
            return;
        }

        pendingPaths.Enqueue(normalizedPath);
    }

    private IReadOnlyList<CollectionResourceView> BuildResourceViews(
        IReadOnlyList<ObjectSnapshot> usageSnapshots,
        IReadOnlyDictionary<string, ObjectResolvedPath> resolvedPaths)
    {
        Dictionary<string, CollectionResourceView> viewsByRoot = new(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectSnapshot snapshot in usageSnapshots)
        {
            if (!ObjectPathRules.TryNormalizeGamePath(ObjectSnapshotUtility.GetRootResourcePath(snapshot), out string rootPath)
             || viewsByRoot.ContainsKey(rootPath))
            {
                continue;
            }

            viewsByRoot[rootPath] = new CollectionResourceView(
                rootPath,
                ResolveResourceViewRedirects(rootPath, resolvedPaths));
        }

        return viewsByRoot.Values
            .OrderBy(static view => view.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private IReadOnlyList<ObjectPathRedirection> ResolveResourceViewRedirects(
        string rootPath,
        IReadOnlyDictionary<string, ObjectResolvedPath> resolvedPaths)
    {
        HashSet<string> reachablePaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> probedPaths = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> pendingPaths = new();
        Dictionary<string, ObjectResolvedPath> viewRedirects = new(StringComparer.OrdinalIgnoreCase);

        AddReachablePath(reachablePaths, pendingPaths, rootPath);
        while (pendingPaths.Count > 0)
        {
            HashSet<string> requestedPaths = DrainPendingPaths(pendingPaths, probedPaths);
            foreach (string requestedPath in requestedPaths)
            {
                probedPaths.Add(requestedPath);
                ObjectResolvedPath effectivePath = resolvedPaths.TryGetValue(requestedPath, out ObjectResolvedPath resolvedPath)
                    ? resolvedPath
                    : ObjectResolvedPath.FromGamePath(requestedPath);
                if (resolvedPaths.TryGetValue(requestedPath, out ObjectResolvedPath redirectedPath))
                {
                    viewRedirects[requestedPath] = redirectedPath;
                }

                foreach (string dependencyPath in _assetIndex.GetCollectionPathDependencies(requestedPath, effectivePath))
                {
                    AddReachablePath(reachablePaths, pendingPaths, dependencyPath);
                }
            }
        }

        return ObjectPathRedirectionUtility.CreateStableList(
            viewRedirects.Select(static pair =>
                new ObjectPathRedirection(pair.Key, pair.Value)));
    }
}

