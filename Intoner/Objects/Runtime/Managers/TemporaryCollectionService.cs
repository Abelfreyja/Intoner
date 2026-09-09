using Intoner.Objects.Assets;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Runtime;

internal sealed record PreparedTemporaryCollections(
    IReadOnlyList<ObjectTemporaryCollectionData> SourceCollections,
    IReadOnlyList<ObjectTemporaryCollectionData> RuntimeCollections,
    IReadOnlyDictionary<string, IReadOnlyList<ObjectPathRedirection>> Redirects,
    string MemoryOwnerId,
    bool HasUncommittedMemoryLease);

/// <summary> prepares and materializes temporary source collections without owning source state </summary>
internal interface ITemporaryCollectionService
{
    /// <summary> validates and prepares a complete collection set before a source commit </summary>
    ObjectTemporaryMutationResult TryPrepare(
        string sourceKey,
        Guid sessionId,
        long revision,
        IReadOnlyList<ObjectTemporaryCollectionData> collections,
        out PreparedTemporaryCollections prepared);

    /// <summary> rebuilds runtime redirects from a committed source without creating resources </summary>
    ObjectTemporaryMutationResult TryPrepareCommitted(
        ObjectTemporarySourceSnapshot source,
        out PreparedTemporaryCollections prepared);

    /// <summary> atomically publishes the prepared runtime collection set </summary>
    void Publish(string sourceKey, PreparedTemporaryCollections prepared);

    /// <summary> removes all runtime collections for a removed source </summary>
    void Remove(string sourceKey);

    /// <summary> releases memory generations no longer used after scene reconciliation </summary>
    void ReleaseRetiredMemory(IEnumerable<string> retiredOwnerIds, string activeOwnerId);

    /// <summary> releases memory created by a preparation that was not committed </summary>
    void Discard(PreparedTemporaryCollections prepared);
}

internal sealed class TemporaryCollectionService : ITemporaryCollectionService
{
    private readonly IObjectResolvedCollectionStore _collectionStore;
    private readonly IObjectMemoryResourceService _memoryResourceService;

    private readonly Lock _publicationLock = new();
    private readonly Dictionary<string, IReadOnlySet<string>> _publishedCollectionIds = new(StringComparer.OrdinalIgnoreCase);

    public TemporaryCollectionService(
        IObjectResolvedCollectionStore collectionStore,
        IObjectMemoryResourceService memoryResourceService)
    {
        _collectionStore = collectionStore;
        _memoryResourceService = memoryResourceService;
    }

    public ObjectTemporaryMutationResult TryPrepare(
        string sourceKey,
        Guid sessionId,
        long revision,
        IReadOnlyList<ObjectTemporaryCollectionData> collections,
        out PreparedTemporaryCollections prepared)
    {
        prepared = new PreparedTemporaryCollections(
            [],
            [],
            new Dictionary<string, IReadOnlyList<ObjectPathRedirection>>(StringComparer.OrdinalIgnoreCase),
            string.Empty,
            false);
        string normalizedSourceKey = TemporarySourceUtility.NormalizeKey(sourceKey);
        if (normalizedSourceKey.Length == 0 || sessionId == Guid.Empty || revision <= 0 || collections is null)
        {
            return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.InvalidSource, 0);
        }

        string memoryOwnerId = CreateMemoryOwnerId(normalizedSourceKey, sessionId, revision);
        List<ObjectTemporaryCollectionData> sourceCollections = [];
        List<ObjectTemporaryCollectionData> runtimeCollections = [];
        Dictionary<string, IReadOnlyList<ObjectPathRedirection>> redirectsByCollection = new(StringComparer.OrdinalIgnoreCase);
        bool retainedMemory = false;

        foreach (ObjectTemporaryCollectionData? collection in collections)
        {
            if (!TryPrepareCollection(
                    normalizedSourceKey,
                    memoryOwnerId,
                    collection,
                    ref retainedMemory,
                    out ObjectTemporaryCollectionData sourceCollection,
                    out ObjectTemporaryCollectionData runtimeCollection,
                    out IReadOnlyList<ObjectPathRedirection> redirects)
                || !redirectsByCollection.TryAdd(runtimeCollection.CollectionId, redirects))
            {
                _memoryResourceService.ReleaseOwner(memoryOwnerId);
                return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.InvalidCollection, 0);
            }

            sourceCollections.Add(sourceCollection);
            runtimeCollections.Add(runtimeCollection);
        }

        if (!retainedMemory)
        {
            _memoryResourceService.ReleaseOwner(memoryOwnerId);
        }

        prepared = new PreparedTemporaryCollections(
            OrderCollections(sourceCollections),
            OrderCollections(runtimeCollections),
            redirectsByCollection,
            retainedMemory ? memoryOwnerId : string.Empty,
            retainedMemory);
        return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.Success, revision);
    }

    public ObjectTemporaryMutationResult TryPrepareCommitted(
        ObjectTemporarySourceSnapshot source,
        out PreparedTemporaryCollections prepared)
    {
        Dictionary<string, IReadOnlyList<ObjectPathRedirection>> redirectsByCollection = new(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectTemporaryCollectionData collection in source.RuntimeCollections)
        {
            if (!TryResolveCommittedRedirects(collection, out IReadOnlyList<ObjectPathRedirection> redirects)
                || !redirectsByCollection.TryAdd(collection.CollectionId, redirects))
            {
                prepared = default!;
                return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.InvalidCollection, source.Revision);
            }
        }

        prepared = new PreparedTemporaryCollections(
            source.Collections,
            source.RuntimeCollections,
            redirectsByCollection,
            source.MemoryOwnerId,
            false);
        return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.Success, source.Revision);
    }

    public void Publish(string sourceKey, PreparedTemporaryCollections prepared)
    {
        string normalizedSourceKey = TemporarySourceUtility.NormalizeKey(sourceKey);
        lock (_publicationLock)
        {
            IReadOnlySet<string> previousIds = _publishedCollectionIds.GetValueOrDefault(normalizedSourceKey)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> nextIds = new(prepared.Redirects.Keys, StringComparer.OrdinalIgnoreCase);
            List<string> removedIds = previousIds.Where(id => !nextIds.Contains(id)).ToList();
            _collectionStore.ReplaceCollections(prepared.Redirects, removedIds);
            _publishedCollectionIds[normalizedSourceKey] = nextIds;
        }
    }

    public void Remove(string sourceKey)
    {
        string normalizedSourceKey = TemporarySourceUtility.NormalizeKey(sourceKey);
        lock (_publicationLock)
        {
            if (!_publishedCollectionIds.TryGetValue(normalizedSourceKey, out IReadOnlySet<string>? publishedIds))
            {
                return;
            }

            _collectionStore.ReplaceCollections(
                new Dictionary<string, IReadOnlyList<ObjectPathRedirection>>(StringComparer.OrdinalIgnoreCase),
                publishedIds.ToList());
            _publishedCollectionIds.Remove(normalizedSourceKey);
        }
    }

    public void ReleaseRetiredMemory(IEnumerable<string> retiredOwnerIds, string activeOwnerId)
    {
        foreach (string ownerId in retiredOwnerIds.Where(ownerId => !string.Equals(ownerId, activeOwnerId, StringComparison.OrdinalIgnoreCase)))
        {
            _memoryResourceService.ReleaseOwner(ownerId);
        }
    }

    public void Discard(PreparedTemporaryCollections prepared)
    {
        if (prepared.HasUncommittedMemoryLease && prepared.MemoryOwnerId.Length > 0)
        {
            _memoryResourceService.ReleaseOwner(prepared.MemoryOwnerId);
        }
    }

    private bool TryPrepareCollection(
        string sourceKey,
        string memoryOwnerId,
        ObjectTemporaryCollectionData? collection,
        ref bool retainedMemory,
        out ObjectTemporaryCollectionData sourceCollection,
        out ObjectTemporaryCollectionData runtimeCollection,
        out IReadOnlyList<ObjectPathRedirection> redirects)
    {
        sourceCollection = default!;
        runtimeCollection = default!;
        redirects = [];
        if (collection?.Redirects is null)
        {
            return false;
        }

        string sourceCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collection.CollectionId);
        string runtimeCollectionId = ObjectIdentityUtility.CreateTemporaryCollectionId(sourceKey, sourceCollectionId);
        if (runtimeCollectionId.Length == 0)
        {
            return false;
        }

        HashSet<string> requestedPaths = new(StringComparer.OrdinalIgnoreCase);
        List<ObjectTemporaryCollectionRedirectData> normalizedRedirects = [];
        List<ObjectPathRedirection> redirectRules = [];
        foreach (ObjectTemporaryCollectionRedirectData? redirect in collection.Redirects)
        {
            if (!TryPrepareRedirect(
                    memoryOwnerId,
                    redirect,
                    requestedPaths,
                    ref retainedMemory,
                    out ObjectTemporaryCollectionRedirectData normalizedRedirect,
                    out ObjectPathRedirection redirectRule))
            {
                return false;
            }

            normalizedRedirects.Add(normalizedRedirect);
            redirectRules.Add(redirectRule);
        }

        sourceCollection = collection with
        {
            CollectionId = sourceCollectionId,
            Name = TextUtility.TrimOrFallback(collection.Name, sourceCollectionId),
            Redirects = normalizedRedirects,
        };
        runtimeCollection = sourceCollection with { CollectionId = runtimeCollectionId };
        redirects = ObjectPathRedirectionUtility.CreateStableList(redirectRules);
        return true;
    }

    private bool TryPrepareRedirect(
        string memoryOwnerId,
        ObjectTemporaryCollectionRedirectData? redirect,
        HashSet<string> requestedPaths,
        ref bool retainedMemory,
        out ObjectTemporaryCollectionRedirectData normalizedRedirect,
        out ObjectPathRedirection redirectRule)
    {
        normalizedRedirect = default!;
        redirectRule = default;
        if (!GameAssetPathRules.TryNormalizeGamePath(redirect?.RequestedPath ?? string.Empty, out string requestedPath)
            || !requestedPaths.Add(requestedPath)
            || !TryPrepareReplacement(
                memoryOwnerId,
                redirect?.Replacement,
                ref retainedMemory,
                out ObjectTemporaryCollectionReplacementData replacement,
                out ObjectResolvedPath resolvedPath)
            || !ObjectPathRedirectionUtility.TryCreate(requestedPath, resolvedPath, out redirectRule))
        {
            return false;
        }

        normalizedRedirect = new ObjectTemporaryCollectionRedirectData
        {
            RequestedPath = requestedPath,
            Replacement = replacement,
        };
        return true;
    }

    private bool TryPrepareReplacement(
        string memoryOwnerId,
        ObjectTemporaryCollectionReplacementData? replacement,
        ref bool retainedMemory,
        out ObjectTemporaryCollectionReplacementData normalizedReplacement,
        out ObjectResolvedPath resolvedPath)
    {
        normalizedReplacement = default!;
        resolvedPath = default;
        if (replacement is null)
        {
            return false;
        }

        switch (replacement.Kind)
        {
            case ObjectTemporaryCollectionReplacementKind.GamePath:
                if (!GameAssetPathRules.TryNormalizeGamePath(replacement.Path, out string gamePath))
                {
                    return false;
                }

                normalizedReplacement = replacement with { Path = gamePath, Data = [] };
                resolvedPath = ObjectResolvedPath.FromGamePath(gamePath);
                return true;
            case ObjectTemporaryCollectionReplacementKind.LocalFile:
                if (!ObjectLocalFilePathUtility.TryNormalizeLocalFilePath(replacement.Path, out string localPath))
                {
                    return false;
                }

                normalizedReplacement = replacement with { Path = localPath, Data = [] };
                resolvedPath = ObjectResolvedPath.FromLocalFile(localPath);
                return true;
            case ObjectTemporaryCollectionReplacementKind.Memory:
                return TryPrepareMemoryReplacement(
                    memoryOwnerId,
                    replacement,
                    ref retainedMemory,
                    out normalizedReplacement,
                    out resolvedPath);
            default:
                return false;
        }
    }

    private bool TryPrepareMemoryReplacement(
        string memoryOwnerId,
        ObjectTemporaryCollectionReplacementData replacement,
        ref bool retainedMemory,
        out ObjectTemporaryCollectionReplacementData normalizedReplacement,
        out ObjectResolvedPath resolvedPath)
    {
        normalizedReplacement = default!;
        resolvedPath = default;
        if (replacement.Data.Length > 0)
        {
            if (!ObjectAssetPathRules.TryNormalizeSupportedResourcePath(replacement.Path, out string gamePath))
            {
                return false;
            }

            resolvedPath = _memoryResourceService.RegisterResource(memoryOwnerId, gamePath, replacement.Data);
            retainedMemory = true;
        }
        else if (ObjectMemoryResourcePathUtility.TryParse(replacement.Path, out ObjectMemoryResourcePath memoryPath)
            && _memoryResourceService.TryAcquireResource(memoryOwnerId, memoryPath.Path, out ObjectMemoryResource resource)
            && _memoryResourceService.CanLoadMemoryResource(resource))
        {
            resolvedPath = ObjectResolvedPath.FromMemory(resource.MemoryPath);
            retainedMemory = true;
        }
        else
        {
            return false;
        }

        normalizedReplacement = replacement with { Path = resolvedPath.Path, Data = [] };
        return true;
    }

    private static IReadOnlyList<ObjectTemporaryCollectionData> OrderCollections(IEnumerable<ObjectTemporaryCollectionData> collections)
        => collections
            .OrderBy(static collection => collection.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static collection => collection.CollectionId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private bool TryResolveCommittedRedirects(
        ObjectTemporaryCollectionData collection,
        out IReadOnlyList<ObjectPathRedirection> redirects)
    {
        HashSet<string> requestedPaths = new(StringComparer.OrdinalIgnoreCase);
        List<ObjectPathRedirection> result = [];
        foreach (ObjectTemporaryCollectionRedirectData redirect in collection.Redirects)
        {
            if (!GameAssetPathRules.TryNormalizeGamePath(redirect.RequestedPath, out string requestedPath)
                || !requestedPaths.Add(requestedPath)
                || !TryResolveCommittedReplacement(redirect.Replacement, out ObjectResolvedPath resolvedPath)
                || !ObjectPathRedirectionUtility.TryCreate(requestedPath, resolvedPath, out ObjectPathRedirection rule))
            {
                redirects = [];
                return false;
            }

            result.Add(rule);
        }

        redirects = ObjectPathRedirectionUtility.CreateStableList(result);
        return true;
    }

    private bool TryResolveCommittedReplacement(
        ObjectTemporaryCollectionReplacementData replacement,
        out ObjectResolvedPath resolvedPath)
    {
        switch (replacement.Kind)
        {
            case ObjectTemporaryCollectionReplacementKind.GamePath
                when GameAssetPathRules.TryNormalizeGamePath(replacement.Path, out string gamePath):
                resolvedPath = ObjectResolvedPath.FromGamePath(gamePath);
                return true;
            case ObjectTemporaryCollectionReplacementKind.LocalFile
                when ObjectLocalFilePathUtility.TryNormalizeLocalFilePath(replacement.Path, out string localPath):
                resolvedPath = ObjectResolvedPath.FromLocalFile(localPath);
                return true;
            case ObjectTemporaryCollectionReplacementKind.Memory
                when ObjectMemoryResourcePathUtility.TryParse(replacement.Path, out ObjectMemoryResourcePath memoryPath)
                && _memoryResourceService.TryGetResource(memoryPath.Path, out ObjectMemoryResource resource)
                && _memoryResourceService.CanLoadMemoryResource(resource):
                resolvedPath = ObjectResolvedPath.FromMemory(resource.MemoryPath);
                return true;
            default:
                resolvedPath = default;
                return false;
        }
    }

    private static string CreateMemoryOwnerId(string sourceKey, Guid sessionId, long revision)
        => $"temporary:{sourceKey}:{sessionId:D}:{revision}:{Guid.NewGuid():N}";
}
