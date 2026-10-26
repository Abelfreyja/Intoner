using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Intoner.Objects.Resources;

/// <summary> immutable object owned resource collection snapshot </summary>
internal sealed record ObjectCollectionResolveData
{
    public string CollectionId { get; init; } = string.Empty;
    public long Revision { get; init; }
    public long ContentSignature { get; init; }
    public long ResourceScopeId { get; init; }
    public bool UsesResourceViews { get; init; }
    public IReadOnlyDictionary<string, ObjectResolvedPath> Redirects { get; init; }
        = ImmutableDictionary<string, ObjectResolvedPath>.Empty;
    public IReadOnlyDictionary<string, CollectionResourceRevision> ResourceRevisions { get; init; }
        = ImmutableDictionary<string, CollectionResourceRevision>.Empty;

    public bool TryResolvePath(string requestedPath, out ObjectResolvedPath resolvedPath)
    {
        string normalizedRequestedPath = ObjectResolvedPath.FromGamePath(requestedPath).Path;

        if (normalizedRequestedPath.Length > 0
         && Redirects.TryGetValue(normalizedRequestedPath, out ObjectResolvedPath resolvedSource))
        {
            resolvedPath = resolvedSource;
            return true;
        }

        resolvedPath = default;
        return false;
    }

    public ObjectResourceRevision? GetResourceRevision(ObjectResourceRoots roots)
        => GetResourceRevision(roots.Primary) is { } primary && GetResourceRevision(roots.Collision) is { } collision
            ? new(primary, collision) : null;

    public long? GetResourceRevision(string rootPath)
    {
        if (rootPath.Length == 0)
        {
            return 0;
        }

        if (TryGetResourceRevision(rootPath, out long revision))
        {
            return revision;
        }

        return UsesResourceViews ? null : Revision;
    }

    public bool TryGetResourceRevision(string rootResourcePath, out long revision)
    {
        string normalizedRootPath = ObjectResolvedPath.FromGamePath(rootResourcePath).Path;

        if (normalizedRootPath.Length > 0
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
    /// <param name="forceRefresh">when true, registers new runtime revisions and a native generation even when redirects did not change</param>
    /// <param name="resourceViews">optional per root resource redirect views</param>
    /// <returns>the normalized registered snapshot</returns>
    ObjectCollectionResolveData RegisterCollection(
        string collectionId,
        IEnumerable<ObjectPathRedirection> redirects,
        bool forceRefresh = false,
        IReadOnlyList<CollectionResourceView>? resourceViews = null);

    /// <summary>
    /// Replaces and removes a collection set through one immutable store swap.
    /// </summary>
    /// <remarks>the last replacement for each normalized collection id wins</remarks>
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
    /// <param name="snapshot">the current registered snapshot when found, or null when unavailable</param>
    /// <returns>true when the collection exists</returns>
    bool TryGetCollection(string collectionId, [MaybeNullWhen(false)] out ObjectCollectionResolveData snapshot);

    /// <summary>
    /// Tries to resolve one registered object resource collection by resource scope id.
    /// </summary>
    /// <param name="resourceScopeId">the stable resource scope id encoded in a scoped resource path</param>
    /// <param name="snapshot">the registered snapshot when found, or null when unavailable</param>
    /// <returns>true when the resource scope id exists</returns>
    bool TryGetCollectionByResourceScopeId(long resourceScopeId, [MaybeNullWhen(false)] out ObjectCollectionResolveData snapshot);

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
    /// Gets the current resource revision for one object root path inside a registered runtime collection.
    /// </summary>
    /// <param name="collectionId">the collection id to inspect</param>
    /// <param name="rootResourcePath">the object root resource path</param>
    /// <returns>the root resource revision, the collection revision when no root revision exists, or 0 when that collection is not registered</returns>
    long GetCollectionRevision(string collectionId, string rootResourcePath);

    /// <summary>
    /// Reads both root revisions from one snapshot, null means a required root is not ready.
    /// </summary>
    ObjectResourceRevision? GetCollectionRevision(string collectionId, ObjectResourceRoots roots);

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
        ImmutableDictionary<long, ObjectCollectionResolveData> CollectionsByResourceScopeId);

    private readonly ILogger<ObjectResolvedCollectionStore> _logger;
    private readonly ObjectMemoryResourceRegistry _memoryResources;
    private readonly Lock _mutationLock = new();
    private readonly Dictionary<long, int> _scopeLeaseCounts = [];
    private readonly string _memoryOwnerPrefix = $"resource-scope:{Guid.NewGuid():N}:";
    private StoreState _state = new(
        ImmutableDictionary.Create<string, ObjectCollectionResolveData>(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary<long, ObjectCollectionResolveData>.Empty);
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

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        // finish caller enumeration before reading the state that will be replaced
        ImmutableDictionary<string, ObjectResolvedPath> redirectSnapshot = CreateRedirectSnapshot(redirects);
        CollectionResourceView[]? viewSnapshot = resourceViews?.Select(static view
            => new CollectionResourceView(view.RootPath, view.Redirects.ToArray())).ToArray();

        ObjectCollectionResolveData snapshot;
        bool changed;
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferWrites();
        lock (_mutationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StoreState current = Volatile.Read(ref _state);
            current.Collections.TryGetValue(normalizedCollectionId, out ObjectCollectionResolveData? existingSnapshot);
            snapshot = BuildCollectionSnapshot(
                normalizedCollectionId,
                redirectSnapshot,
                existingSnapshot,
                forceRefresh,
                viewSnapshot);
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        string[] removals = removedCollectionIds.Select(static id => ObjectCollectionKeyUtility.NormalizeCollectionId(id)).ToArray();
        Dictionary<string, ImmutableDictionary<string, ObjectResolvedPath>> replacementSnapshots = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string collectionId, IReadOnlyList<ObjectPathRedirection> redirects) in replacements)
        {
            string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);

            if (normalizedCollectionId.Length == 0)
            {
                throw new ArgumentException("collection id must not be empty", nameof(replacements));
            }

            replacementSnapshots[normalizedCollectionId] = CreateRedirectSnapshot(redirects);
        }

        List<ObjectResolvedCollectionChangedInfo> changes = [];
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferWrites();
        lock (_mutationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ImmutableDictionary<string, ObjectCollectionResolveData>.Builder next = Volatile.Read(ref _state).Collections.ToBuilder();
            foreach (string normalizedCollectionId in removals)
            {
                if (next.Remove(normalizedCollectionId, out ObjectCollectionResolveData? removed))
                {
                    changes.Add(new ObjectResolvedCollectionChangedInfo(normalizedCollectionId, removed.Revision, Removed: true));
                }
            }

            foreach ((string normalizedCollectionId, ImmutableDictionary<string, ObjectResolvedPath> redirects) in replacementSnapshots)
            {
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

    public bool TryGetCollection(string collectionId, [MaybeNullWhen(false)] out ObjectCollectionResolveData snapshot)
    {
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        if (normalizedCollectionId.Length == 0)
        {
            snapshot = null;
            return false;
        }

        if (Volatile.Read(ref _state).Collections.TryGetValue(normalizedCollectionId, out ObjectCollectionResolveData? resolvedSnapshot))
        {
            snapshot = resolvedSnapshot;
            return true;
        }

        snapshot = null;
        return false;
    }

    public bool TryGetCollectionByResourceScopeId(long resourceScopeId, [MaybeNullWhen(false)] out ObjectCollectionResolveData snapshot)
    {
        if (resourceScopeId <= 0)
        {
            snapshot = null;
            return false;
        }

        if (Volatile.Read(ref _state).CollectionsByResourceScopeId.TryGetValue(resourceScopeId, out ObjectCollectionResolveData? resolvedSnapshot))
        {
            snapshot = resolvedSnapshot;
            return true;
        }

        snapshot = null;
        return false;
    }

    public long GetCollectionRevision(string collectionId, string rootResourcePath)
    {
        if (!TryGetCollection(collectionId, out ObjectCollectionResolveData? snapshot))
        {
            return 0;
        }

        return snapshot.GetResourceRevision(rootResourcePath) ?? snapshot.Revision;
    }

    public ObjectResourceRevision? GetCollectionRevision(string collectionId, ObjectResourceRoots roots)
    {
        if (collectionId.Length == 0 || roots.Primary.Length == 0 && roots.Collision.Length == 0)
        {
            return default(ObjectResourceRevision);
        }

        return TryGetCollection(collectionId, out ObjectCollectionResolveData? snapshot)
            ? snapshot.GetResourceRevision(roots) : null;
    }

    public bool TryGetCollectionResourceRevision(string collectionId, string rootResourcePath, out long revision)
    {
        if (!TryGetCollection(collectionId, out ObjectCollectionResolveData? snapshot))
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
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferWrites();
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
            return !_disposed && TryGetCollection(collectionId, out ObjectCollectionResolveData? snapshot)
                ? AcquireSnapshot(snapshot)
                : null;
        }
    }

    public ObjectResourceCollectionLease? AcquireResourceScope(long resourceScopeId)
    {
        lock (_mutationLock)
        {
            return !_disposed && TryGetCollectionByResourceScopeId(resourceScopeId, out ObjectCollectionResolveData? snapshot)
                ? AcquireSnapshot(snapshot)
                : null;
        }
    }

    private ObjectResourceCollectionLease AcquireSnapshot(ObjectCollectionResolveData snapshot)
    {
        _scopeLeaseCounts[snapshot.ResourceScopeId] = _scopeLeaseCounts.GetValueOrDefault(snapshot.ResourceScopeId) + 1;
        return new ObjectResourceCollectionLease(this, snapshot);
    }

    internal void ReleaseResourceScope(long resourceScopeId)
    {
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferWrites();
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

            Volatile.Write(ref _state, current with
            {
                CollectionsByResourceScopeId = current.CollectionsByResourceScopeId.Remove(resourceScopeId),
            });
            _memoryResources.ReleaseOwner(GetMemoryOwner(resourceScopeId));
        }
    }

    public void Dispose()
    {
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferWrites();
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
        ImmutableDictionary<string, ObjectResolvedPath> redirectSnapshot,
        ObjectCollectionResolveData? existingSnapshot,
        bool forceRefresh,
        IReadOnlyList<CollectionResourceView>? resourceViews)
    {
        Dictionary<string, string> fileStamps = new(StringComparer.OrdinalIgnoreCase);
        ImmutableDictionary<string, CollectionResourceRevision> resourceRevisions = BuildResourceRevisions(
            resourceViews,
            redirectSnapshot,
            existingSnapshot?.ResourceRevisions,
            forceRefresh,
            fileStamps);
        long contentSignature = CreateResourceRedirectHash(collectionId, redirectSnapshot, fileStamps);

        if (!forceRefresh
            && existingSnapshot is not null
            && existingSnapshot.UsesResourceViews == (resourceViews is not null)
            && existingSnapshot.ContentSignature == contentSignature
            && RedirectsMatch(existingSnapshot.Redirects, redirectSnapshot)
            && ResourceRevisionsMatch(existingSnapshot.ResourceRevisions, resourceRevisions))
        {
            return existingSnapshot;
        }

        return new ObjectCollectionResolveData
        {
            CollectionId = collectionId,
            Revision = Interlocked.Increment(ref _nextRuntimeRevision),
            ContentSignature = contentSignature,
            ResourceScopeId = ObjectResourceIdentity.Next(),
            Redirects = redirectSnapshot,
            ResourceRevisions = resourceRevisions,
            UsesResourceViews = resourceViews is not null,
        };
    }

    private static ImmutableDictionary<string, ObjectResolvedPath> CreateRedirectSnapshot(IEnumerable<ObjectPathRedirection> redirects)
    {
        ImmutableDictionary<string, ObjectResolvedPath>.Builder builder
            = ImmutableDictionary.CreateBuilder<string, ObjectResolvedPath>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectPathRedirection redirect in redirects.Where(static redirect
                     => ObjectResourcePathUtility.IsSupportedRedirection(redirect.RequestedPath, redirect.ResolvedPath)))
        {
            builder[redirect.RequestedPath] = redirect.ResolvedPath;
        }

        return builder.ToImmutable();
    }

    private void PublishCollections(ImmutableDictionary<string, ObjectCollectionResolveData> collections)
    {
        StoreState current = Volatile.Read(ref _state);
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

            string memoryOwner = GetMemoryOwner(collection.ResourceScopeId);
            foreach (ObjectResolvedPath path in collection.Redirects.Values.Where(static path => path.IsMemory))
            {
                _memoryResources.TryAcquireResource(memoryOwner, path.Path, out _);
            }
        }

        List<long> retiredScopeIds = current.CollectionsByResourceScopeId.Keys.Where(scopeId => !byResourceScopeId.ContainsKey(scopeId)).ToList();

        Volatile.Write(ref _state, new StoreState(collections, byResourceScopeId.ToImmutable()));
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
        bool forceRefresh,
        Dictionary<string, string> fileStamps)
    {
        if (resourceViews is null || resourceViews.Count == 0)
        {
            return ImmutableDictionary<string, CollectionResourceRevision>.Empty;
        }

        ImmutableDictionary<string, CollectionResourceRevision>.Builder revisions
            = ImmutableDictionary.CreateBuilder<string, CollectionResourceRevision>(StringComparer.OrdinalIgnoreCase);
        foreach (CollectionResourceView view in resourceViews)
        {
            string rootPath = ObjectResolvedPath.FromGamePath(view.RootPath).Path;

            if (rootPath.Length == 0)
            {
                continue;
            }

            ImmutableDictionary<string, ObjectResolvedPath> redirects = NormalizeResourceViewRedirects(
                view.Redirects,
                collectionRedirects);
            long viewSignature = CreateResourceRedirectHash(rootPath, redirects, fileStamps);
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

    private static long CreateResourceRedirectHash(
        string key,
        IReadOnlyDictionary<string, ObjectResolvedPath> redirects,
        Dictionary<string, string> fileStamps)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendHashValue(hash, key.ToUpperInvariant());
        foreach ((string requestedPath, ObjectResolvedPath resolvedPath) in redirects.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            AppendHashValue(hash, requestedPath.ToUpperInvariant());
            AppendHashValue(hash, ((int)resolvedPath.Kind).ToString(CultureInfo.InvariantCulture));
            AppendHashValue(hash, resolvedPath.Path);
            AppendHashValue(hash, CreateResolvedPathStamp(resolvedPath, fileStamps));
        }

        Span<byte> bytes = stackalloc byte[32];

        if (!hash.TryGetHashAndReset(bytes, out int written) || written < sizeof(long))
        {
            throw new InvalidOperationException("could not build object resource content signature");
        }

        long signature = BinaryPrimitives.ReadInt64LittleEndian(bytes[..sizeof(long)]) & long.MaxValue;
        return signature == 0 ? 1 : signature;
    }

    private static void AppendHashValue(IncrementalHash hash, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        byte[]? rented = byteCount > 256 ? ArrayPool<byte>.Shared.Rent(byteCount) : null;
        Span<byte> bytes = rented is null
            ? stackalloc byte[byteCount]
            : rented.AsSpan(0, byteCount);

        try
        {
            int written = Encoding.UTF8.GetBytes(value, bytes);
            hash.AppendData(bytes[..written]);
            hash.AppendData([0]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static string CreateResolvedPathStamp(ObjectResolvedPath resolvedPath, Dictionary<string, string> fileStamps)
    {
        if (!resolvedPath.IsLocalFile)
        {
            return string.Empty;
        }

        if (fileStamps.TryGetValue(resolvedPath.Path, out string? stamp))
        {
            return stamp;
        }

        try
        {
            FileInfo fileInfo = new(resolvedPath.Path);
            stamp = !fileInfo.Exists
                ? "missing"
                : string.Concat(
                    fileInfo.Length.ToString(CultureInfo.InvariantCulture),
                    ":",
                    fileInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            stamp = string.Empty;
        }

        fileStamps.Add(resolvedPath.Path, stamp);
        return stamp;
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
                ObjectResourceLog.Write(() => _logger.LogWarning(ex, "object resource collection changed handler failed for {CollectionId}", info.CollectionId));
            }
        }
    }
}
