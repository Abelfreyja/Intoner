using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Intoner.Objects.Resources;

/// <summary> immutable object owned resource collection snapshot </summary>
internal sealed record ObjectCollectionResolveData
{
    public string CollectionId { get; init; } = string.Empty;
    public long Revision { get; init; }
    public long ResourceScopeId { get; init; }
    public IReadOnlyDictionary<string, ObjectResolvedPath> Redirects { get; init; }
        = ImmutableDictionary<string, ObjectResolvedPath>.Empty;
    public IReadOnlyDictionary<string, CollectionResourceRevision> ResourceRevisions { get; init; }
        = ImmutableDictionary<string, CollectionResourceRevision>.Empty;

    public bool TryResolvePath(string requestedPath, out ObjectResolvedPath resolvedPath)
    {
        if (GameAssetPathRules.TryNormalizeGamePath(requestedPath, out string normalizedRequestedPath)
         && Redirects.TryGetValue(normalizedRequestedPath, out ObjectResolvedPath resolvedSource))
        {
            resolvedPath = resolvedSource;
            return true;
        }

        resolvedPath = default;
        return false;
    }

    public bool TryGetResourceRevision(string rootResourcePath, out long revision)
    {
        if (GameAssetPathRules.TryNormalizeGamePath(rootResourcePath, out string normalizedRootPath)
         && ResourceRevisions.TryGetValue(normalizedRootPath, out CollectionResourceRevision? resourceRevision)
         && resourceRevision is not null)
        {
            revision = resourceRevision.Revision;
            return true;
        }

        revision = 0;
        return false;
    }
}

/// <summary> resolved redirect view for one object root resource </summary>
internal sealed record CollectionResourceView(string RootPath, IReadOnlyList<ObjectPathRedirection> Redirects);

/// <summary> runtime revision for one object root resource inside a collection </summary>
internal sealed record CollectionResourceRevision
{
    public string RootPath { get; init; } = string.Empty;
    public long Revision { get; init; }
    public long ViewSignature { get; init; }
    public IReadOnlyDictionary<string, ObjectResolvedPath> Redirects { get; init; }
        = ImmutableDictionary<string, ObjectResolvedPath>.Empty;
}

/// <summary> describes one runtime object resource collection change </summary>
internal readonly record struct ObjectResolvedCollectionChangedInfo(
    string CollectionId,
    long Revision,
    bool Removed);

/// <summary> retains one object resource collection snapshot and it's memory resources </summary>
internal sealed class ObjectResourceCollectionLease(ObjectResolvedCollectionStore owner, ObjectCollectionResolveData snapshot) : IDisposable
{
    private ObjectResolvedCollectionStore? _owner = owner;

    public ObjectCollectionResolveData Snapshot { get; } = snapshot;

    public void Dispose()
        => Interlocked.Exchange(ref _owner, null)?.ReleaseResourceScope(Snapshot.ResourceScopeId);
}

/// <summary>
/// Stores immutable object owned resource collection snapshots.
/// </summary>
internal interface IObjectResolvedCollectionStore
{
    /// <summary>
    /// Raised when one registered runtime object resource collection changes.
    /// </summary>
    event Action<ObjectResolvedCollectionChangedInfo> CollectionChanged;

    /// <summary>
    /// Registers or replaces one object owned resource collection snapshot.
    /// </summary>
    /// <param name="collectionId">the collection id</param>
    /// <param name="redirects">the object resource redirect rules for this collection</param>
    /// <param name="forceRefresh">when true, registers new runtime revisions even when redirects did not change</param>
    /// <param name="resourceViews">optional per root resource redirect views</param>
    /// <returns>the normalized registered snapshot</returns>
    ObjectCollectionResolveData RegisterCollection(
        string collectionId,
        IEnumerable<ObjectPathRedirection> redirects,
        bool forceRefresh = false,
        IReadOnlyList<CollectionResourceView>? resourceViews = null);

    /// <summary>replaces and removes a collection set through one immutable store swap</summary>
    /// <param name="replacements">complete redirect sets keyed by collection id</param>
    /// <param name="removedCollectionIds">collection ids to remove</param>
    void ReplaceCollections(
        IReadOnlyDictionary<string, IReadOnlyList<ObjectPathRedirection>> replacements,
        IReadOnlyList<string> removedCollectionIds);

    /// <summary>
    /// Gets the current registered object resource collection snapshots.
    /// </summary>
    /// <returns>the current registered snapshots</returns>
    IReadOnlyList<ObjectCollectionResolveData> GetCollections();

    /// <summary>
    /// Tries to resolve one registered object resource collection.
    /// </summary>
    /// <param name="collectionId">the collection id to resolve</param>
    /// <param name="snapshot">the current registered snapshot when found</param>
    /// <returns>true when the collection exists</returns>
    bool TryGetCollection(string collectionId, out ObjectCollectionResolveData snapshot);

    /// <summary>
    /// Tries to resolve one registered object resource collection by resource scope id.
    /// </summary>
    /// <param name="resourceScopeId">the stable resource scope id encoded in a scoped resource path</param>
    /// <param name="snapshot">the registered snapshot when found</param>
    /// <returns>true when the resource scope id exists</returns>
    bool TryGetCollectionByResourceScopeId(long resourceScopeId, out ObjectCollectionResolveData snapshot);

    /// <summary>
    /// Acquires a lease for the current registered object resource collection snapshot.
    /// </summary>
    /// <param name="collectionId">the collection id to resolve</param>
    /// <returns>a lease that must be disposed, or null when the collection is unavailable</returns>
    ObjectResourceCollectionLease? AcquireCollection(string collectionId);

    /// <summary>
    /// Acquires a lease for one current or retained object resource collection snapshot.
    /// </summary>
    /// <param name="resourceScopeId">the stable resource scope id encoded in a scoped resource path</param>
    /// <returns>a lease that must be disposed, or null when the generation is unavailable</returns>
    ObjectResourceCollectionLease? AcquireResourceScope(long resourceScopeId);

    /// <summary>
    /// Checks whether a local file path is used by a current or retained object resource collection.
    /// </summary>
    /// <param name="normalizedLocalFilePath">the normalized local file path</param>
    /// <returns>true when a current or retained collection uses the path</returns>
    bool ContainsLocalFilePath(string normalizedLocalFilePath);

    /// <summary>
    /// Gets the current resource revision for one object root path inside a registered runtime collection.
    /// </summary>
    /// <param name="collectionId">the collection id to inspect</param>
    /// <param name="rootResourcePath">the object root resource path</param>
    /// <returns>the root resource revision, the collection revision when no root revision exists, or 0 when that collection is not registered</returns>
    long GetCollectionRevision(string collectionId, string rootResourcePath);

    /// <summary>
    /// Tries to resolve a root resource revision for one registered runtime collection.
    /// </summary>
    /// <param name="collectionId">the collection id to inspect</param>
    /// <param name="rootResourcePath">the object root resource path</param>
    /// <param name="revision">the current root resource revision when found</param>
    /// <returns>true when a revision exists for that root resource</returns>
    bool TryGetCollectionResourceRevision(string collectionId, string rootResourcePath, out long revision);

    /// <summary>
    /// Removes one registered object resource collection.
    /// </summary>
    /// <param name="collectionId">the collection id to remove</param>
    /// <returns>true when a collection was removed</returns>
    bool RemoveCollection(string collectionId);
}

internal sealed class ObjectResolvedCollectionStore : IObjectResolvedCollectionStore, IDisposable
{
    private sealed record StoreState(
        ImmutableDictionary<string, ObjectCollectionResolveData> Collections,
        ImmutableDictionary<long, ObjectCollectionResolveData> CollectionsByResourceScopeId,
        ImmutableDictionary<string, int> LocalFilePathCounts);

    private readonly ILogger<ObjectResolvedCollectionStore> _logger;
    private readonly ObjectMemoryResourceRegistry _memoryResources;
    private readonly Lock _mutationLock = new();
    private readonly Dictionary<long, int> _scopeLeaseCounts = [];
    private readonly string _memoryOwnerPrefix = $"resource-scope:{Guid.NewGuid():N}:";
    private StoreState _state = new(
        ImmutableDictionary.Create<string, ObjectCollectionResolveData>(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary<long, ObjectCollectionResolveData>.Empty,
        ImmutableDictionary.Create<string, int>(StringComparer.OrdinalIgnoreCase));
    private long _nextRuntimeRevision;
    private bool _disposed;

    public event Action<ObjectResolvedCollectionChangedInfo>? CollectionChanged;

    public ObjectResolvedCollectionStore(ILogger<ObjectResolvedCollectionStore> logger, ObjectMemoryResourceRegistry memoryResources)
    {
        _logger = logger;
        _memoryResources = memoryResources;
    }

    public ObjectCollectionResolveData RegisterCollection(
        string collectionId,
        IEnumerable<ObjectPathRedirection> redirects,
        bool forceRefresh = false,
        IReadOnlyList<CollectionResourceView>? resourceViews = null)
    {
        ArgumentNullException.ThrowIfNull(redirects);
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        if (normalizedCollectionId.Length == 0)
        {
            throw new ArgumentException("collection id must not be empty", nameof(collectionId));
        }

        ObjectCollectionResolveData snapshot;
        bool changed;
        lock (_mutationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StoreState current = Volatile.Read(ref _state);
            current.Collections.TryGetValue(normalizedCollectionId, out ObjectCollectionResolveData? existingSnapshot);
            snapshot = BuildCollectionSnapshot(
                normalizedCollectionId,
                redirects,
                existingSnapshot,
                forceRefresh,
                resourceViews);
            changed = !ReferenceEquals(snapshot, existingSnapshot);
            if (changed)
            {
                PublishCollections(current.Collections.SetItem(snapshot.CollectionId, snapshot));
            }
        }

        if (changed)
        {
            RaiseCollectionChanged(new ObjectResolvedCollectionChangedInfo(snapshot.CollectionId, snapshot.Revision, Removed: false));
        }

        return snapshot;
    }

    public void ReplaceCollections(
        IReadOnlyDictionary<string, IReadOnlyList<ObjectPathRedirection>> replacements,
        IReadOnlyList<string> removedCollectionIds)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        ArgumentNullException.ThrowIfNull(removedCollectionIds);

        List<ObjectResolvedCollectionChangedInfo> changes = [];
        lock (_mutationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ImmutableDictionary<string, ObjectCollectionResolveData>.Builder next = Volatile.Read(ref _state).Collections.ToBuilder();
            foreach (string collectionId in removedCollectionIds)
            {
                string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
                if (next.Remove(normalizedCollectionId, out ObjectCollectionResolveData? removed))
                {
                    changes.Add(new ObjectResolvedCollectionChangedInfo(normalizedCollectionId, removed.Revision, Removed: true));
                }
            }

            foreach ((string collectionId, IReadOnlyList<ObjectPathRedirection> redirects) in replacements)
            {
                string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
                if (normalizedCollectionId.Length == 0)
                {
                    throw new ArgumentException("collection id must not be empty", nameof(replacements));
                }

                next.TryGetValue(normalizedCollectionId, out ObjectCollectionResolveData? existing);
                ObjectCollectionResolveData replacement = BuildCollectionSnapshot(
                    normalizedCollectionId,
                    redirects,
                    existing,
                    forceRefresh: false,
                    resourceViews: null);
                if (ReferenceEquals(replacement, existing))
                {
                    continue;
                }

                next[normalizedCollectionId] = replacement;
                changes.Add(new ObjectResolvedCollectionChangedInfo(normalizedCollectionId, replacement.Revision, Removed: false));
            }

            if (changes.Count > 0)
            {
                PublishCollections(next.ToImmutable());
            }
        }

        foreach (ObjectResolvedCollectionChangedInfo change in changes)
        {
            RaiseCollectionChanged(change);
        }
    }

    public IReadOnlyList<ObjectCollectionResolveData> GetCollections()
        => Volatile.Read(ref _state).Collections.Values
            .OrderBy(static collection => collection.CollectionId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool TryGetCollection(string collectionId, out ObjectCollectionResolveData snapshot)
    {
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        if (normalizedCollectionId.Length == 0)
        {
            snapshot = null!;
            return false;
        }

        if (Volatile.Read(ref _state).Collections.TryGetValue(normalizedCollectionId, out ObjectCollectionResolveData? resolvedSnapshot))
        {
            snapshot = resolvedSnapshot;
            return true;
        }

        snapshot = null!;
        return false;
    }

    public bool TryGetCollectionByResourceScopeId(long resourceScopeId, out ObjectCollectionResolveData snapshot)
    {
        if (resourceScopeId <= 0)
        {
            snapshot = null!;
            return false;
        }

        if (Volatile.Read(ref _state).CollectionsByResourceScopeId.TryGetValue(resourceScopeId, out ObjectCollectionResolveData? resolvedSnapshot))
        {
            snapshot = resolvedSnapshot;
            return true;
        }

        snapshot = null!;
        return false;
    }

    public long GetCollectionRevision(string collectionId, string rootResourcePath)
    {
        if (!TryGetCollection(collectionId, out ObjectCollectionResolveData snapshot))
        {
            return 0;
        }

        return snapshot.TryGetResourceRevision(rootResourcePath, out long revision)
            ? revision
            : snapshot.Revision;
    }

    public bool TryGetCollectionResourceRevision(string collectionId, string rootResourcePath, out long revision)
    {
        if (!TryGetCollection(collectionId, out ObjectCollectionResolveData snapshot))
        {
            revision = 0;
            return false;
        }

        return snapshot.TryGetResourceRevision(rootResourcePath, out revision);
    }

    public bool RemoveCollection(string collectionId)
    {
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        if (normalizedCollectionId.Length == 0)
        {
            return false;
        }

        ObjectCollectionResolveData? removedSnapshot;
        lock (_mutationLock)
        {
            StoreState current = Volatile.Read(ref _state);
            if (!current.Collections.TryGetValue(normalizedCollectionId, out removedSnapshot))
            {
                return false;
            }

            PublishCollections(current.Collections.Remove(normalizedCollectionId));
        }

        RaiseCollectionChanged(new ObjectResolvedCollectionChangedInfo(normalizedCollectionId, removedSnapshot.Revision, Removed: true));
        return true;
    }

    public ObjectResourceCollectionLease? AcquireCollection(string collectionId)
    {
        lock (_mutationLock)
        {
            return !_disposed && TryGetCollection(collectionId, out ObjectCollectionResolveData snapshot)
                ? AcquireSnapshot(snapshot)
                : null;
        }
    }

    public ObjectResourceCollectionLease? AcquireResourceScope(long resourceScopeId)
    {
        lock (_mutationLock)
        {
            return !_disposed && TryGetCollectionByResourceScopeId(resourceScopeId, out ObjectCollectionResolveData snapshot)
                ? AcquireSnapshot(snapshot)
                : null;
        }
    }

    public bool ContainsLocalFilePath(string normalizedLocalFilePath)
        => Volatile.Read(ref _state).LocalFilePathCounts.ContainsKey(normalizedLocalFilePath);

    private ObjectResourceCollectionLease AcquireSnapshot(ObjectCollectionResolveData snapshot)
    {
        _scopeLeaseCounts[snapshot.ResourceScopeId] = _scopeLeaseCounts.GetValueOrDefault(snapshot.ResourceScopeId) + 1;
        return new ObjectResourceCollectionLease(this, snapshot);
    }

    internal void ReleaseResourceScope(long resourceScopeId)
    {
        lock (_mutationLock)
        {
            if (!_scopeLeaseCounts.TryGetValue(resourceScopeId, out int count))
            {
                return;
            }

            if (count > 1)
            {
                _scopeLeaseCounts[resourceScopeId] = count - 1;
                return;
            }

            _scopeLeaseCounts.Remove(resourceScopeId);
            StoreState current = Volatile.Read(ref _state);
            ObjectCollectionResolveData snapshot = current.CollectionsByResourceScopeId[resourceScopeId];
            if (current.Collections.TryGetValue(snapshot.CollectionId, out ObjectCollectionResolveData? active)
             && active.ResourceScopeId == resourceScopeId)
            {
                return;
            }

            ImmutableDictionary<string, int>.Builder localPaths = current.LocalFilePathCounts.ToBuilder();
            UpdateLocalFilePaths(localPaths, snapshot, -1);
            Volatile.Write(ref _state, current with
            {
                CollectionsByResourceScopeId = current.CollectionsByResourceScopeId.Remove(resourceScopeId),
                LocalFilePathCounts = localPaths.ToImmutable(),
            });
            _memoryResources.ReleaseOwner(GetMemoryOwner(resourceScopeId));
        }
    }

    public void Dispose()
    {
        lock (_mutationLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _scopeLeaseCounts.Clear();
            PublishCollections(ImmutableDictionary.Create<string, ObjectCollectionResolveData>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private ObjectCollectionResolveData BuildCollectionSnapshot(
        string collectionId,
        IEnumerable<ObjectPathRedirection> redirects,
        ObjectCollectionResolveData? existingSnapshot,
        bool forceRefresh,
        IReadOnlyList<CollectionResourceView>? resourceViews)
    {
        ImmutableDictionary<string, ObjectResolvedPath>.Builder redirectBuilder
            = ImmutableDictionary.CreateBuilder<string, ObjectResolvedPath>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectPathRedirection redirect in redirects.Where(static redirect
                     => ObjectResourcePathUtility.IsSupportedRedirection(redirect.RequestedPath, redirect.ResolvedPath)))
        {
            redirectBuilder[redirect.RequestedPath] = redirect.ResolvedPath;
        }

        ImmutableDictionary<string, ObjectResolvedPath> redirectSnapshot = redirectBuilder.ToImmutable();
        ImmutableDictionary<string, CollectionResourceRevision> resourceRevisions = BuildResourceRevisions(
            resourceViews,
            redirectSnapshot,
            existingSnapshot?.ResourceRevisions,
            forceRefresh);
        long resourceScopeId = CreateResourceScopeId(collectionId, redirectSnapshot);
        if (!forceRefresh
            && existingSnapshot is not null
            && existingSnapshot.ResourceScopeId == resourceScopeId
            && RedirectsMatch(existingSnapshot.Redirects, redirectSnapshot)
            && ResourceRevisionsMatch(existingSnapshot.ResourceRevisions, resourceRevisions))
        {
            return existingSnapshot;
        }

        return new ObjectCollectionResolveData
        {
            CollectionId = collectionId,
            Revision = Interlocked.Increment(ref _nextRuntimeRevision),
            ResourceScopeId = resourceScopeId,
            Redirects = redirectSnapshot,
            ResourceRevisions = resourceRevisions,
        };
    }

    private static void UpdateLocalFilePaths(
        ImmutableDictionary<string, int>.Builder localPaths,
        ObjectCollectionResolveData collection,
        int change)
    {
        foreach (string path in collection.Redirects.Values.Where(static path => path.IsLocalFile).Select(static path => path.Path))
        {
            int count = localPaths.GetValueOrDefault(path) + change;
            if (count == 0)
            {
                localPaths.Remove(path);
            }
            else
            {
                localPaths[path] = count;
            }
        }
    }

    private void PublishCollections(ImmutableDictionary<string, ObjectCollectionResolveData> collections)
    {
        StoreState current = Volatile.Read(ref _state);
        ImmutableDictionary<string, int>.Builder localPaths = current.LocalFilePathCounts.ToBuilder();
        ImmutableDictionary<long, ObjectCollectionResolveData>.Builder byResourceScopeId
            = ImmutableDictionary.CreateBuilder<long, ObjectCollectionResolveData>();
        foreach (long scopeId in _scopeLeaseCounts.Keys)
        {
            byResourceScopeId[scopeId] = current.CollectionsByResourceScopeId[scopeId];
        }

        foreach (ObjectCollectionResolveData collection in collections.Values)
        {
            byResourceScopeId[collection.ResourceScopeId] = collection;
            if (current.CollectionsByResourceScopeId.ContainsKey(collection.ResourceScopeId))
            {
                continue;
            }

            UpdateLocalFilePaths(localPaths, collection, 1);
            string memoryOwner = GetMemoryOwner(collection.ResourceScopeId);
            foreach (ObjectResolvedPath path in collection.Redirects.Values.Where(static path => path.IsMemory))
            {
                _memoryResources.TryAcquireResource(memoryOwner, path.Path, out _);
            }
        }

        List<long> retiredScopeIds = [];
        foreach ((long scopeId, ObjectCollectionResolveData collection) in current.CollectionsByResourceScopeId)
        {
            if (!byResourceScopeId.ContainsKey(scopeId))
            {
                UpdateLocalFilePaths(localPaths, collection, -1);
                retiredScopeIds.Add(scopeId);
            }
        }

        Volatile.Write(ref _state, new StoreState(collections, byResourceScopeId.ToImmutable(), localPaths.ToImmutable()));
        foreach (long scopeId in retiredScopeIds)
        {
            _memoryResources.ReleaseOwner(GetMemoryOwner(scopeId));
        }
    }

    private string GetMemoryOwner(long resourceScopeId)
        => string.Concat(_memoryOwnerPrefix, resourceScopeId.ToString(CultureInfo.InvariantCulture));

    private static bool RedirectsMatch(
        IReadOnlyDictionary<string, ObjectResolvedPath> left,
        IReadOnlyDictionary<string, ObjectResolvedPath> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach ((string requestedPath, ObjectResolvedPath resolvedPath) in left)
        {
            if (!right.TryGetValue(requestedPath, out ObjectResolvedPath rightResolvedPath)
             || rightResolvedPath != resolvedPath)
            {
                return false;
            }
        }

        return true;
    }

    private ImmutableDictionary<string, CollectionResourceRevision> BuildResourceRevisions(
        IReadOnlyList<CollectionResourceView>? resourceViews,
        IReadOnlyDictionary<string, ObjectResolvedPath> collectionRedirects,
        IReadOnlyDictionary<string, CollectionResourceRevision>? existingRevisions,
        bool forceRefresh)
    {
        if (resourceViews is null || resourceViews.Count == 0)
        {
            return ImmutableDictionary<string, CollectionResourceRevision>.Empty;
        }

        ImmutableDictionary<string, CollectionResourceRevision>.Builder revisions
            = ImmutableDictionary.CreateBuilder<string, CollectionResourceRevision>(StringComparer.OrdinalIgnoreCase);
        foreach (CollectionResourceView view in resourceViews)
        {
            if (!GameAssetPathRules.TryNormalizeGamePath(view.RootPath, out string rootPath))
            {
                continue;
            }

            ImmutableDictionary<string, ObjectResolvedPath> redirects = NormalizeResourceViewRedirects(
                view.Redirects,
                collectionRedirects);
            long viewSignature = CreateResourceViewSignature(rootPath, redirects);
            if (!forceRefresh
             && existingRevisions is not null
             && existingRevisions.TryGetValue(rootPath, out CollectionResourceRevision? existingRevision)
             && existingRevision.ViewSignature == viewSignature
             && RedirectsMatch(existingRevision.Redirects, redirects))
            {
                revisions[rootPath] = existingRevision;
                continue;
            }

            revisions[rootPath] = new CollectionResourceRevision
            {
                RootPath = rootPath,
                Revision = redirects.Count == 0
                    ? 0
                    : Interlocked.Increment(ref _nextRuntimeRevision),
                ViewSignature = viewSignature,
                Redirects = redirects,
            };
        }

        return revisions.ToImmutable();
    }

    private static ImmutableDictionary<string, ObjectResolvedPath> NormalizeResourceViewRedirects(
        IEnumerable<ObjectPathRedirection> redirects,
        IReadOnlyDictionary<string, ObjectResolvedPath> collectionRedirects)
    {
        ImmutableDictionary<string, ObjectResolvedPath>.Builder builder
            = ImmutableDictionary.CreateBuilder<string, ObjectResolvedPath>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectPathRedirection redirect in redirects)
        {
            if (!ObjectResourcePathUtility.IsSupportedRedirection(redirect.RequestedPath, redirect.ResolvedPath)
             || !collectionRedirects.TryGetValue(redirect.RequestedPath, out ObjectResolvedPath collectionResolvedPath)
             || collectionResolvedPath != redirect.ResolvedPath)
            {
                continue;
            }

            builder[redirect.RequestedPath] = redirect.ResolvedPath;
        }

        return builder.ToImmutable();
    }

    private static bool ResourceRevisionsMatch(
        IReadOnlyDictionary<string, CollectionResourceRevision> left,
        IReadOnlyDictionary<string, CollectionResourceRevision> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach ((string rootPath, CollectionResourceRevision leftRevision) in left)
        {
            if (!right.TryGetValue(rootPath, out CollectionResourceRevision? rightRevision)
             || leftRevision.ViewSignature != rightRevision.ViewSignature
             || !RedirectsMatch(leftRevision.Redirects, rightRevision.Redirects))
            {
                return false;
            }
        }

        return true;
    }

    private static long CreateResourceScopeId(string collectionId, IReadOnlyDictionary<string, ObjectResolvedPath> redirects)
        => CreateResourceRedirectHash(collectionId, redirects);

    private static long CreateResourceViewSignature(string rootPath, IReadOnlyDictionary<string, ObjectResolvedPath> redirects)
        => CreateResourceRedirectHash(rootPath, redirects);

    private static long CreateResourceRedirectHash(string key, IReadOnlyDictionary<string, ObjectResolvedPath> redirects)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashValue(hash, key);
        foreach ((string requestedPath, ObjectResolvedPath resolvedPath) in redirects.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            AppendHashValue(hash, requestedPath);
            AppendHashValue(hash, ((int)resolvedPath.Kind).ToString(CultureInfo.InvariantCulture));
            AppendHashValue(hash, resolvedPath.Path);
            AppendHashValue(hash, CreateResolvedPathStamp(resolvedPath));
        }

        Span<byte> bytes = stackalloc byte[32];
        if (!hash.TryGetHashAndReset(bytes, out int written) || written < sizeof(long))
        {
            throw new InvalidOperationException("could not build object collection resource scope id");
        }

        long scopeId = BinaryPrimitives.ReadInt64LittleEndian(bytes[..sizeof(long)]) & long.MaxValue;
        return scopeId == 0 ? 1 : scopeId;
    }

    private static void AppendHashValue(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
        hash.AppendData([0]);
    }

    private static string CreateResolvedPathStamp(ObjectResolvedPath resolvedPath)
    {
        if (!resolvedPath.IsLocalFile)
        {
            return string.Empty;
        }

        try
        {
            FileInfo fileInfo = new(resolvedPath.Path);
            return !fileInfo.Exists
                ? "missing"
                : string.Concat(
                    fileInfo.Length.ToString(CultureInfo.InvariantCulture),
                    ":",
                    fileInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            return string.Empty;
        }
    }

    private void RaiseCollectionChanged(ObjectResolvedCollectionChangedInfo info)
    {
        Action<ObjectResolvedCollectionChangedInfo>? handlers = CollectionChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<ObjectResolvedCollectionChangedInfo> handler in handlers.GetInvocationList()
                     .Cast<Action<ObjectResolvedCollectionChangedInfo>>())
        {
            try
            {
                handler(info);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "object resource collection changed handler failed for {CollectionId}", info.CollectionId);
            }
        }
    }
}


