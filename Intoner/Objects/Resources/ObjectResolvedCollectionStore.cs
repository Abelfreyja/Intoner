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
        if (ObjectResourcePathUtility.TryNormalizeGamePath(requestedPath, out string normalizedRequestedPath)
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
        if (ObjectResourcePathUtility.TryNormalizeGamePath(rootResourcePath, out string normalizedRootPath)
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

internal sealed class ObjectResolvedCollectionStore : IObjectResolvedCollectionStore
{
    private readonly ILogger<ObjectResolvedCollectionStore> _logger;
    private ImmutableDictionary<string, ObjectCollectionResolveData> _collections
        = ImmutableDictionary.Create<string, ObjectCollectionResolveData>(StringComparer.OrdinalIgnoreCase);
    private ImmutableDictionary<long, ObjectCollectionResolveData> _collectionsByResourceScopeId
        = ImmutableDictionary<long, ObjectCollectionResolveData>.Empty;
    private long _nextRuntimeRevision;

    public event Action<ObjectResolvedCollectionChangedInfo>? CollectionChanged;

    public ObjectResolvedCollectionStore(ILogger<ObjectResolvedCollectionStore> logger)
    {
        _logger = logger;
    }

    public ObjectCollectionResolveData RegisterCollection(
        string collectionId,
        IEnumerable<ObjectPathRedirection> redirects,
        bool forceRefresh = false,
        IReadOnlyList<CollectionResourceView>? resourceViews = null)
    {
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        if (normalizedCollectionId.Length == 0)
        {
            throw new ArgumentException("collection id must not be empty", nameof(collectionId));
        }

        ImmutableDictionary<string, ObjectCollectionResolveData> currentCollections = Volatile.Read(ref _collections);
        currentCollections.TryGetValue(normalizedCollectionId, out ObjectCollectionResolveData? existingSnapshot);
        ImmutableDictionary<string, ObjectResolvedPath>.Builder redirectBuilder
            = ImmutableDictionary.CreateBuilder<string, ObjectResolvedPath>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectPathRedirection redirect in redirects)
        {
            if (!ObjectResourcePathUtility.IsSupportedRedirection(redirect.RequestedPath, redirect.ResolvedPath))
            {
                continue;
            }

            redirectBuilder[redirect.RequestedPath] = redirect.ResolvedPath;
        }

        ImmutableDictionary<string, ObjectResolvedPath> redirectsSnapshot = redirectBuilder.ToImmutable();
        ImmutableDictionary<string, CollectionResourceRevision> resourceRevisions = BuildResourceRevisions(
            resourceViews,
            redirectsSnapshot,
            existingSnapshot?.ResourceRevisions,
            forceRefresh);
        long resourceScopeId = CreateResourceScopeId(normalizedCollectionId, redirectsSnapshot);
        if (!forceRefresh
         && existingSnapshot is not null
         && existingSnapshot.ResourceScopeId == resourceScopeId
         && RedirectsMatch(existingSnapshot.Redirects, redirectsSnapshot)
         && ResourceRevisionsMatch(existingSnapshot.ResourceRevisions, resourceRevisions))
        {
            return existingSnapshot;
        }

        ObjectCollectionResolveData snapshot = new()
        {
            CollectionId = normalizedCollectionId,
            Revision = Interlocked.Increment(ref _nextRuntimeRevision),
            ResourceScopeId = resourceScopeId,
            Redirects = redirectsSnapshot,
            ResourceRevisions = resourceRevisions,
        };

        ImmutableInterlocked.AddOrUpdate(
            ref _collections,
            normalizedCollectionId,
            snapshot,
            (_, _) => snapshot);
        ImmutableInterlocked.Update(
            ref _collectionsByResourceScopeId,
            static (collections, state) => RemoveResourceScopesForCollection(collections, state.CollectionId)
                .SetItem(state.ResourceScopeId, state),
            snapshot);

        RaiseCollectionChanged(new ObjectResolvedCollectionChangedInfo(snapshot.CollectionId, snapshot.Revision, Removed: false));
        return snapshot;
    }

    public IReadOnlyList<ObjectCollectionResolveData> GetCollections()
        => Volatile.Read(ref _collections).Values.ToList();

    public bool TryGetCollection(string collectionId, out ObjectCollectionResolveData snapshot)
    {
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        if (normalizedCollectionId.Length == 0)
        {
            snapshot = null!;
            return false;
        }

        if (Volatile.Read(ref _collections).TryGetValue(normalizedCollectionId, out ObjectCollectionResolveData? resolvedSnapshot))
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

        if (Volatile.Read(ref _collectionsByResourceScopeId).TryGetValue(resourceScopeId, out ObjectCollectionResolveData? resolvedSnapshot))
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

        if (!ImmutableInterlocked.TryRemove(ref _collections, normalizedCollectionId, out ObjectCollectionResolveData? removedSnapshot)
            || removedSnapshot == null)
        {
            return false;
        }

        RemoveCollectionRevisions(normalizedCollectionId);
        RaiseCollectionChanged(new ObjectResolvedCollectionChangedInfo(normalizedCollectionId, removedSnapshot.Revision, Removed: true));
        return true;
    }

    private void RemoveCollectionRevisions(string collectionId)
    {
        ImmutableInterlocked.Update(
            ref _collectionsByResourceScopeId,
            static (collections, state) => RemoveResourceScopesForCollection(collections, state),
            collectionId);
    }

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
            if (!ObjectResourcePathUtility.TryNormalizeGamePath(view.RootPath, out string rootPath))
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

    private static ImmutableDictionary<long, ObjectCollectionResolveData> RemoveResourceScopesForCollection(
        ImmutableDictionary<long, ObjectCollectionResolveData> collections,
        string collectionId)
        => collections.RemoveRange(collections
            .Where(pair => string.Equals(pair.Value.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
            .Select(static pair => pair.Key));

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
        try
        {
            CollectionChanged?.Invoke(info);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "object resource collection changed handler failed for {CollectionId}", info.CollectionId);
        }
    }
}


