using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using System.Collections.Immutable;

namespace Intoner.Objects.Collections;

internal sealed record ObjectCollectionResolveResult
{
    public bool IsComplete { get; init; } = true;
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
    private readonly record struct DependencySource(
        GameAssetFileKind FileKind,
        ObjectResolvedPathKind PathKind,
        string Path,
        string ModelPath);

    private readonly IObjectModDataSource _modDataSource;
    private readonly ObjectAssetDependencyResolver _dependencyResolver;

    public ObjectCollectionResolver(IObjectModDataSource modDataSource, ObjectAssetDependencyResolver dependencyResolver)
    {
        _modDataSource = modDataSource;
        _dependencyResolver = dependencyResolver;
    }

    public async Task<ObjectCollectionResolveResult> ResolveAsync(
        ObjectCollection collection,
        IReadOnlyList<ObjectSnapshot> usageSnapshots,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (usageSnapshots.Count == 0)
        {
            return new ObjectCollectionResolveResult
            {
                ResolveState = ObjectCollectionResolveState.Inactive,
                StatusText = "collection has no active object usage",
            };
        }

        Dictionary<string, ObjectPathRedirection> redirectsByPath = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IReadOnlyList<string>> dependenciesByPath = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<DependencySource, AssetDependencyPaths> dependenciesBySource = [];
        HashSet<string> rootPaths = CreateUsageRootPaths(usageSnapshots, cancellationToken);
        HashSet<string> reachablePaths = new(rootPaths, StringComparer.OrdinalIgnoreCase);
        Queue<string> pendingPaths = new(rootPaths);

        if (reachablePaths.Count == 0)
        {
            return new ObjectCollectionResolveResult
            {
                ResolveState = ObjectCollectionResolveState.Inactive,
                StatusText = "collection has no object resource paths in active usage",
            };
        }

        ObjectModResolveResult modResult = await _modDataSource.PrepareCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (modResult.ResolveState != ObjectCollectionResolveState.Ready)
        {
            return new ObjectCollectionResolveResult
            {
                IsComplete = modResult.IsComplete,
                ResolveState = modResult.ResolveState,
                StatusText = modResult.StatusText,
                Warnings = modResult.Warnings,
                KeepLastGoodSnapshot = modResult.KeepLastGoodSnapshot,
                ResourceViews = modResult.IsComplete && modResult.ResolveState == ObjectCollectionResolveState.Inactive
                    ? BuildResourceViews(rootPaths, redirectsByPath, dependenciesByPath, cancellationToken) : [],
            };
        }

        while (pendingPaths.Count > 0)
        {
            string requestedPath = pendingPaths.Dequeue();
            ObjectResolvedPath effectivePath;

            if (modResult.Redirects.TryResolvePath(requestedPath, out ObjectResolvedPath resolvedPath, cancellationToken))
            {
                effectivePath = resolvedPath;

                if (ObjectPathRedirectionUtility.TryCreate(requestedPath, resolvedPath, out ObjectPathRedirection redirect))
                {
                    redirectsByPath[requestedPath] = redirect;
                }
            }
            else
            {
                effectivePath = ObjectResolvedPath.FromGamePath(requestedPath);
            }

            GameAssetFileKind fileKind = GameAssetPathRules.ClassifyNormalizedFilePath(requestedPath);
            DependencySource source = new(
                fileKind,
                effectivePath.Kind,
                effectivePath.Path.ToLowerInvariant(),
                fileKind == GameAssetFileKind.Mdl && effectivePath.IsLocalFile
                    ? requestedPath.ToLowerInvariant()
                    : string.Empty);

            if (!dependenciesBySource.TryGetValue(source, out AssetDependencyPaths dependencies))
            {
                dependencies = _dependencyResolver.GetCollectionPathDependencies(requestedPath, effectivePath, cancellationToken);
                dependenciesBySource.Add(source, dependencies);
            }

            if (!dependencies.IsComplete)
            {
                string message = $"could not read dependencies for {requestedPath}";
                return new ObjectCollectionResolveResult
                {
                    IsComplete = false,
                    ResolveState = ObjectCollectionResolveState.ResolveFailed,
                    StatusText = message,
                    KeepLastGoodSnapshot = true,
                    Warnings = [.. modResult.Warnings, message],
                };
            }

            dependenciesByPath[requestedPath] = dependencies.Paths;

            foreach (string dependencyPath in dependencies.Paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddReachablePath(reachablePaths, pendingPaths, dependencyPath);
            }
        }

        IReadOnlyList<CollectionResourceView> resourceViews = BuildResourceViews(rootPaths, redirectsByPath, dependenciesByPath, cancellationToken);
        modResult.Redirects.ThrowIfChanged(cancellationToken);

        ImmutableArray<ObjectPathRedirection>.Builder redirectBuilder = ImmutableArray.CreateBuilder<ObjectPathRedirection>(redirectsByPath.Count);
        foreach (ObjectPathRedirection redirect in redirectsByPath.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            redirectBuilder.Add(redirect);
        }

        modResult.Redirects.ThrowIfChanged(cancellationToken);
        return new ObjectCollectionResolveResult
        {
            IsComplete = modResult.IsComplete,
            ResolveState = redirectBuilder.Count == 0
                ? ObjectCollectionResolveState.Inactive
                : ObjectCollectionResolveState.Ready,
            StatusText = redirectBuilder.Count == 0
                ? "assigned Penumbra mods expose no redirects used by the active objects in this collection"
                : $"resolved {redirectBuilder.Count} redirected object resource paths used by {usageSnapshots.Count} objects",
            Warnings = modResult.Warnings,
            Redirects = redirectBuilder.ToImmutable(),
            ResourceViews = resourceViews,
        };
    }

    private static HashSet<string> CreateUsageRootPaths(
        IReadOnlyList<ObjectSnapshot> usageSnapshots,
        CancellationToken cancellationToken)
    {
        HashSet<string> rootPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectSnapshot snapshot in usageSnapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectResourceRoots roots = ObjectResourceRoots.FromModel(snapshot.Model);

            if (ObjectAssetPathRules.IsSupportedResourcePath(roots.Primary))
            {
                rootPaths.Add(roots.Primary);
            }

            if (ObjectAssetPathRules.IsSupportedResourcePath(roots.Collision))
            {
                rootPaths.Add(roots.Collision);
            }
        }

        return rootPaths;
    }

    private static void AddReachablePath(
        ISet<string> reachablePaths,
        Queue<string> pendingPaths,
        string path)
    {
        if (!ObjectResourcePathUtility.TryNormalizeSupportedGamePath(path, out string normalizedPath)
            || !reachablePaths.Add(normalizedPath))
        {
            return;
        }

        pendingPaths.Enqueue(normalizedPath);
    }

    private static IReadOnlyList<CollectionResourceView> BuildResourceViews(
        IReadOnlySet<string> rootPaths,
        IReadOnlyDictionary<string, ObjectPathRedirection> redirectsByPath,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependenciesByPath,
        CancellationToken cancellationToken)
    {
        List<CollectionResourceView> views = new(rootPaths.Count);
        foreach (string rootPath in rootPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            views.Add(new CollectionResourceView(
                rootPath,
                redirectsByPath.Count == 0
                    ? []
                    : ResolveResourceViewRedirects(rootPath, redirectsByPath, dependenciesByPath, cancellationToken)));
        }

        return views
            .OrderBy(static view => view.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static IReadOnlyList<ObjectPathRedirection> ResolveResourceViewRedirects(
        string rootPath,
        IReadOnlyDictionary<string, ObjectPathRedirection> redirectsByPath,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependenciesByPath,
        CancellationToken cancellationToken)
    {
        HashSet<string> reachablePaths = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> pendingPaths = new();
        List<ObjectPathRedirection> viewRedirects = [];

        AddReachablePath(reachablePaths, pendingPaths, rootPath);
        while (pendingPaths.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string requestedPath = pendingPaths.Dequeue();

            if (redirectsByPath.TryGetValue(requestedPath, out ObjectPathRedirection redirect))
            {
                viewRedirects.Add(redirect);
            }

            if (dependenciesByPath.TryGetValue(requestedPath, out IReadOnlyList<string>? dependencies))
            {
                foreach (string dependencyPath in dependencies)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddReachablePath(reachablePaths, pendingPaths, dependencyPath);
                }
            }
        }

        return viewRedirects
            .OrderBy(static redirect => redirect.RequestedPath, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }
}
