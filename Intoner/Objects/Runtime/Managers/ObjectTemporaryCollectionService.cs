using Intoner.Objects.Models;
using Intoner.Objects.Assets;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Runtime;

/// <summary> manages temporary object collections for session scoped object sources </summary>
internal interface IObjectTemporaryCollectionService
{
    /// <summary> replaces the full temporary collection set for one source </summary>
    /// <param name="sourceKey">the temporary source key</param>
    /// <param name="sessionId">the source session id</param>
    /// <param name="name">the temporary source display name</param>
    /// <param name="collections">the replacement temporary collections</param>
    /// <param name="revision">the source revision for this write</param>
    /// <returns>the result of the temporary collection mutation</returns>
    ObjectTemporaryMutationResult TryApplyTemporaryCollections(
        string sourceKey,
        Guid sessionId,
        string name,
        IReadOnlyList<ObjectTemporaryCollectionData> collections,
        long revision = 0);

    /// <summary> creates or updates one temporary collection for a source </summary>
    /// <param name="sourceKey">the temporary source key</param>
    /// <param name="sessionId">the source session id</param>
    /// <param name="name">the temporary source display name</param>
    /// <param name="collection">the collection payload to create or update</param>
    /// <param name="revision">the source revision for this write</param>
    /// <returns>the result of the temporary collection mutation</returns>
    ObjectTemporaryMutationResult TryUpsertTemporaryCollection(
        string sourceKey,
        Guid sessionId,
        string name,
        ObjectTemporaryCollectionData collection,
        long revision = 0);

    /// <summary> removes one or more temporary collections from a source </summary>
    /// <param name="sourceKey">the temporary source key</param>
    /// <param name="sessionId">the source session id</param>
    /// <param name="collectionIds">the collection ids to remove</param>
    /// <param name="revision">the source revision for this write</param>
    /// <returns>the result of the temporary collection removals</returns>
    ObjectTemporaryMutationResult TryRemoveTemporaryCollections(
        string sourceKey,
        Guid sessionId,
        IReadOnlyList<string> collectionIds,
        long revision = 0);

    /// <summary> clears all temporary collections for one source </summary>
    /// <param name="sourceKey">the temporary source key</param>
    /// <param name="sessionId">the source session id</param>
    /// <param name="revision">the source revision for this write</param>
    /// <returns>the result of the temporary collection source clear</returns>
    ObjectTemporaryMutationResult TryClearTemporaryCollectionSource(
        string sourceKey,
        Guid sessionId,
        long revision = 0);

    /// <summary> clears one source when a new session replaces the current one </summary>
    /// <param name="sourceKey">the temporary source key</param>
    /// <param name="sessionId">the incoming source session id</param>
    void ResetTemporarySourceSessionIfNeeded(string sourceKey, Guid sessionId);
}

internal sealed class ObjectTemporaryCollectionService : IObjectTemporaryCollectionService
{
    private readonly record struct TemporaryWriteContext(
        string SourceKey,
        ObjectTemporaryCollectionSourceSnapshot? ExistingSource,
        ObjectTemporarySourceState CurrentState,
        bool ResetSource);

    private enum RuntimeCollectionMutationKind
    {
        Register,
        Remove,
    }

    private readonly record struct RuntimeCollectionMutation(
        RuntimeCollectionMutationKind Kind,
        string CollectionId,
        IReadOnlyList<ObjectPathRedirection> Redirects);

    private readonly Lock _runtimeMutationLock = new();
    private readonly Lock _stateLock = new();
    private readonly ILogger<ObjectTemporaryCollectionService> _logger;
    private readonly IObjectResolvedCollectionStore _collectionStore;
    private readonly Func<IObjectMemoryResourceService> _memoryResourceServiceFactory;

    private readonly Dictionary<string, ObjectTemporaryCollectionSourceSnapshot> _sources = [];
    private readonly Dictionary<string, ObjectTemporarySourceState> _sourceStates = [];

    public ObjectTemporaryCollectionService(
        ILogger<ObjectTemporaryCollectionService> logger,
        IObjectResolvedCollectionStore collectionStore,
        Func<IObjectMemoryResourceService> memoryResourceServiceFactory)
    {
        _logger = logger;
        _collectionStore = collectionStore;
        _memoryResourceServiceFactory = memoryResourceServiceFactory;
    }

    public ObjectTemporaryMutationResult TryApplyTemporaryCollections(
        string sourceKey,
        Guid sessionId,
        string name,
        IReadOnlyList<ObjectTemporaryCollectionData> collections,
        long revision = 0)
    {
        IReadOnlyList<RuntimeCollectionMutation> runtimeMutations;
        ObjectTemporaryMutationResult result;
        lock (_runtimeMutationLock)
        {
            lock (_stateLock)
            {
                if (!TryPrepareTemporaryWrite(sourceKey, sessionId, revision, out TemporaryWriteContext context, out ObjectTemporaryMutationResult error))
                {
                    return error;
                }

                if (!TryRemapTemporaryCollections(
                        context.SourceKey,
                        collections,
                        out IReadOnlyList<ObjectTemporaryCollectionData> remappedCollections))
                {
                    return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.InvalidObject, context.CurrentState.Revision);
                }

                result = CommitTemporaryCollections(
                    context,
                    sessionId,
                    name,
                    remappedCollections,
                    revision,
                    out runtimeMutations);
            }

            return ApplyRuntimeCollectionMutations(result, runtimeMutations);
        }
    }

    public ObjectTemporaryMutationResult TryUpsertTemporaryCollection(
        string sourceKey,
        Guid sessionId,
        string name,
        ObjectTemporaryCollectionData collection,
        long revision = 0)
    {
        IReadOnlyList<RuntimeCollectionMutation> runtimeMutations;
        ObjectTemporaryMutationResult result;
        lock (_runtimeMutationLock)
        {
            lock (_stateLock)
            {
                if (!TryPrepareTemporaryWrite(sourceKey, sessionId, revision, out TemporaryWriteContext context, out ObjectTemporaryMutationResult error))
                {
                    return error;
                }

                if (!TryRemapTemporaryCollection(context.SourceKey, collection, out ObjectTemporaryCollectionData remappedCollection))
                {
                    return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.InvalidObject, context.CurrentState.Revision);
                }

                result = CommitTemporaryCollections(
                    context,
                    sessionId,
                    name,
                    ReplaceCollection(GetWritableExistingSource(context)?.Collections, remappedCollection),
                    revision,
                    out runtimeMutations);
            }

            return ApplyRuntimeCollectionMutations(result, runtimeMutations);
        }
    }

    public ObjectTemporaryMutationResult TryRemoveTemporaryCollections(
        string sourceKey,
        Guid sessionId,
        IReadOnlyList<string> collectionIds,
        long revision = 0)
    {
        IReadOnlyList<RuntimeCollectionMutation> runtimeMutations;
        ObjectTemporaryMutationResult result;
        lock (_runtimeMutationLock)
        {
            lock (_stateLock)
            {
                if (!TryPrepareTemporaryWrite(sourceKey, sessionId, revision, out TemporaryWriteContext context, out ObjectTemporaryMutationResult error))
                {
                    return error;
                }

                if (!TryCreateTemporaryCollectionIdSet(context.SourceKey, collectionIds, out HashSet<string> normalizedCollectionIds))
                {
                    return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.InvalidObject, context.CurrentState.Revision);
                }

                ObjectTemporaryCollectionSourceSnapshot? writableExistingSource = GetWritableExistingSource(context);
                List<ObjectTemporaryCollectionData> nextCollections = RemoveCollections(writableExistingSource?.Collections, normalizedCollectionIds, out int removedCount);
                if (removedCount != normalizedCollectionIds.Count && !context.ResetSource)
                {
                    return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.ObjectNotFound, context.CurrentState.Revision);
                }

                result = CommitTemporaryCollections(
                    context,
                    sessionId,
                    string.Empty,
                    nextCollections,
                    revision,
                    out runtimeMutations);
            }

            return ApplyRuntimeCollectionMutations(result, runtimeMutations);
        }
    }

    public ObjectTemporaryMutationResult TryClearTemporaryCollectionSource(
        string sourceKey,
        Guid sessionId,
        long revision = 0)
    {
        IReadOnlyList<RuntimeCollectionMutation> runtimeMutations;
        ObjectTemporaryMutationResult result;
        string memoryOwnerId;
        lock (_runtimeMutationLock)
        {
            lock (_stateLock)
            {
                if (!TryPrepareTemporaryWrite(sourceKey, sessionId, revision, out TemporaryWriteContext context, out ObjectTemporaryMutationResult error))
                {
                    return error;
                }

                if (context.ExistingSource is null && context.CurrentState.Revision == 0)
                {
                    return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.ObjectNotFound, 0);
                }

                runtimeMutations = ClearSourceLocked(context.SourceKey, context.ExistingSource);
                result = CommitTemporarySourceState(context, sessionId, revision);
                memoryOwnerId = CreateMemoryOwnerId(context.SourceKey);
            }

            ObjectTemporaryMutationResult appliedResult = ApplyRuntimeCollectionMutations(result, runtimeMutations);
            if (appliedResult.IsSuccess)
            {
                MemoryResourceService.ReleaseOwner(memoryOwnerId);
            }

            return appliedResult;
        }
    }

    public void ResetTemporarySourceSessionIfNeeded(string sourceKey, Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            return;
        }

        IReadOnlyList<RuntimeCollectionMutation> runtimeMutations;
        string memoryOwnerId;
        lock (_runtimeMutationLock)
        {
            lock (_stateLock)
            {
                string sanitizedSourceKey = ObjectTemporarySourceUtility.NormalizeSourceKey(sourceKey);
                if (sanitizedSourceKey.Length == 0)
                {
                    return;
                }

                _sources.TryGetValue(sanitizedSourceKey, out ObjectTemporaryCollectionSourceSnapshot? existingSource);
                ObjectTemporarySourceState currentState = ResolveCurrentTemporarySourceState(sanitizedSourceKey, existingSource);
                if (!ObjectTemporarySourceUtility.IsNewSession(currentState.SessionId, sessionId))
                {
                    return;
                }

                runtimeMutations = ClearSourceLocked(sanitizedSourceKey, existingSource);
                _sourceStates[sanitizedSourceKey] = new ObjectTemporarySourceState(sessionId, 0);
                memoryOwnerId = CreateMemoryOwnerId(sanitizedSourceKey);
            }

            ApplyRuntimeCollectionMutations(runtimeMutations);
            MemoryResourceService.ReleaseOwner(memoryOwnerId);
        }
    }

    private bool TryPrepareTemporaryWrite(
        string sourceKey,
        Guid sessionId,
        long revision,
        out TemporaryWriteContext context,
        out ObjectTemporaryMutationResult error)
    {
        string sanitizedSourceKey = ObjectTemporarySourceUtility.NormalizeSourceKey(sourceKey);
        if (sanitizedSourceKey.Length == 0)
        {
            context = default;
            error = new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.InvalidSource, 0);
            return false;
        }

        _sources.TryGetValue(sanitizedSourceKey, out ObjectTemporaryCollectionSourceSnapshot? existingSource);
        ObjectTemporarySourceState currentState = ResolveCurrentTemporarySourceState(sanitizedSourceKey, existingSource);
        bool resetSource = ObjectTemporarySourceUtility.IsNewSession(currentState.SessionId, sessionId);
        if (resetSource)
        {
            currentState = new ObjectTemporarySourceState(sessionId, 0);
        }

        if (ObjectTemporarySourceUtility.IsStaleRevision(currentState.Revision, revision))
        {
            context = default;
            error = new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.StaleRevision, currentState.Revision);
            return false;
        }

        context = new TemporaryWriteContext(
            sanitizedSourceKey,
            existingSource,
            currentState,
            resetSource);
        error = default;
        return true;
    }

    private ObjectTemporaryMutationResult CommitTemporaryCollections(
        TemporaryWriteContext context,
        Guid sessionId,
        string name,
        IReadOnlyList<ObjectTemporaryCollectionData> collections,
        long revision,
        out IReadOnlyList<RuntimeCollectionMutation> runtimeMutations)
    {
        runtimeMutations = [];
        Guid nextSessionId = ObjectTemporarySourceUtility.ResolveSessionId(context.CurrentState.SessionId, sessionId);
        long nextRevision = ObjectTemporarySourceUtility.ResolveRevision(context.CurrentState.Revision, revision);
        ObjectTemporaryCollectionSourceSnapshot? writableExistingSource = GetWritableExistingSource(context);
        string sourceName = ObjectTemporarySourceUtility.ResolveName(writableExistingSource?.Name, name, context.SourceKey);

        string memoryOwnerId = CreateMemoryOwnerId(context.SourceKey);
        ObjectTemporaryCollectionSourceSnapshot? rollbackSource = context.ResetSource ? context.ExistingSource : writableExistingSource;
        HashSet<string> reusableMemoryPaths = CollectMemoryPaths(writableExistingSource?.Collections);
        if (!TryNormalizeCollections(
                memoryOwnerId,
                collections,
                reusableMemoryPaths,
                out IReadOnlyList<ObjectTemporaryCollectionData> normalizedCollections,
                out Dictionary<string, IReadOnlyList<ObjectPathRedirection>> builtCollections,
                out HashSet<string> activeMemoryPaths))
        {
            MemoryResourceService.RetainOwnerResources(memoryOwnerId, CollectMemoryPaths(rollbackSource?.Collections));
            return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.InvalidObject, context.CurrentState.Revision);
        }

        runtimeMutations = BuildRuntimeCollectionMutations(context, writableExistingSource, builtCollections);

        _sources[context.SourceKey] = new ObjectTemporaryCollectionSourceSnapshot
        {
            SourceKey = context.SourceKey,
            SourceSessionId = nextSessionId,
            Name = sourceName,
            Revision = nextRevision,
            Collections = normalizedCollections,
        };
        _sourceStates[context.SourceKey] = new ObjectTemporarySourceState(nextSessionId, nextRevision);
        MemoryResourceService.RetainOwnerResources(memoryOwnerId, activeMemoryPaths);

        return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.Success, nextRevision);
    }

    private ObjectTemporaryMutationResult CommitTemporarySourceState(TemporaryWriteContext context, Guid sessionId, long revision)
    {
        Guid nextSessionId = ObjectTemporarySourceUtility.ResolveSessionId(context.CurrentState.SessionId, sessionId);
        long nextRevision = ObjectTemporarySourceUtility.ResolveRevision(context.CurrentState.Revision, revision);
        _sourceStates[context.SourceKey] = new ObjectTemporarySourceState(nextSessionId, nextRevision);
        return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.Success, nextRevision);
    }

    private bool TryNormalizeCollections(
        string memoryOwnerId,
        IReadOnlyList<ObjectTemporaryCollectionData> collections,
        IReadOnlySet<string> reusableMemoryPaths,
        out IReadOnlyList<ObjectTemporaryCollectionData> normalizedCollections,
        out Dictionary<string, IReadOnlyList<ObjectPathRedirection>> builtCollections,
        out HashSet<string> activeMemoryPaths)
    {
        List<ObjectTemporaryCollectionData> normalizedList = [];
        normalizedCollections = [];
        builtCollections = new Dictionary<string, IReadOnlyList<ObjectPathRedirection>>(StringComparer.OrdinalIgnoreCase);
        activeMemoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectTemporaryCollectionData? collection in collections)
        {
            if (!TryNormalizeCollection(
                    memoryOwnerId,
                    collection,
                    reusableMemoryPaths,
                    out ObjectTemporaryCollectionData normalizedCollection,
                    out IReadOnlyList<ObjectPathRedirection> redirectRules,
                    activeMemoryPaths))
            {
                return false;
            }

            if (!builtCollections.TryAdd(normalizedCollection.CollectionId, redirectRules))
            {
                return false;
            }

            normalizedList.Add(normalizedCollection);
        }

        normalizedCollections = OrderCollections(normalizedList);
        return true;
    }

    private bool TryNormalizeCollection(
        string memoryOwnerId,
        ObjectTemporaryCollectionData? collection,
        IReadOnlySet<string> reusableMemoryPaths,
        out ObjectTemporaryCollectionData normalizedCollection,
        out IReadOnlyList<ObjectPathRedirection> redirectRules,
        HashSet<string> activeMemoryPaths)
    {
        normalizedCollection = default!;
        redirectRules = [];
        if (collection?.Redirects is null)
        {
            return false;
        }

        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collection.CollectionId);
        if (normalizedCollectionId.Length == 0)
        {
            return false;
        }

        List<ObjectPathRedirection> rules = [];
        List<ObjectTemporaryCollectionRedirectData> normalizedRedirects = [];
        HashSet<string> seenRequestedPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (ObjectTemporaryCollectionRedirectData? redirect in collection.Redirects)
        {
            if (!TryNormalizeRedirect(
                    memoryOwnerId,
                    redirect,
                    seenRequestedPaths,
                    reusableMemoryPaths,
                    out ObjectTemporaryCollectionRedirectData normalizedRedirect,
                    out ObjectPathRedirection redirectRule,
                    activeMemoryPaths))
            {
                return false;
            }

            normalizedRedirects.Add(normalizedRedirect);
            rules.Add(redirectRule);
        }

        normalizedCollection = collection with
        {
            CollectionId = normalizedCollectionId,
            Name = ObjectStringUtility.TrimOrFallback(collection.Name, normalizedCollectionId),
            Redirects = normalizedRedirects,
        };
        redirectRules = rules;
        return true;
    }

    private bool TryNormalizeRedirect(
        string memoryOwnerId,
        ObjectTemporaryCollectionRedirectData? redirect,
        HashSet<string> seenRequestedPaths,
        IReadOnlySet<string> reusableMemoryPaths,
        out ObjectTemporaryCollectionRedirectData normalizedRedirect,
        out ObjectPathRedirection redirectRule,
        HashSet<string> activeMemoryPaths)
    {
        normalizedRedirect = default!;
        redirectRule = default;
        if (!ObjectResourcePathUtility.TryNormalizeGamePath(redirect?.RequestedPath ?? string.Empty, out string normalizedRequestedPath)
         || !seenRequestedPaths.Add(normalizedRequestedPath))
        {
            return false;
        }

        if (!TryNormalizeReplacement(
                memoryOwnerId,
                redirect?.Replacement,
                reusableMemoryPaths,
                out ObjectTemporaryCollectionReplacementData normalizedReplacement,
                out ObjectResolvedPath resolvedPath,
                activeMemoryPaths))
        {
            return false;
        }

        if (!ObjectPathRedirectionUtility.TryCreate(normalizedRequestedPath, resolvedPath, out redirectRule))
        {
            return false;
        }

        normalizedRedirect = new ObjectTemporaryCollectionRedirectData
        {
            RequestedPath = normalizedRequestedPath,
            Replacement = normalizedReplacement,
        };
        return true;
    }

    private bool TryNormalizeReplacement(
        string memoryOwnerId,
        ObjectTemporaryCollectionReplacementData? replacement,
        IReadOnlySet<string> reusableMemoryPaths,
        out ObjectTemporaryCollectionReplacementData normalizedReplacement,
        out ObjectResolvedPath resolvedPath,
        HashSet<string> activeMemoryPaths)
    {
        normalizedReplacement = default!;
        if (replacement is null)
        {
            resolvedPath = default;
            return false;
        }

        switch (replacement.Kind)
        {
            case ObjectTemporaryCollectionReplacementKind.GamePath:
                if (!ObjectResourcePathUtility.TryNormalizeGamePath(replacement.Path, out string normalizedGamePath))
                {
                    resolvedPath = default;
                    return false;
                }

                normalizedReplacement = new ObjectTemporaryCollectionReplacementData
                {
                    Kind = ObjectTemporaryCollectionReplacementKind.GamePath,
                    Path = normalizedGamePath,
                };
                resolvedPath = ObjectResolvedPath.FromGamePath(normalizedGamePath);
                return true;
            case ObjectTemporaryCollectionReplacementKind.LocalFile:
                if (!ObjectResourcePathUtility.TryNormalizeLocalFilePath(replacement.Path, out string normalizedLocalFilePath))
                {
                    resolvedPath = default;
                    return false;
                }

                normalizedReplacement = new ObjectTemporaryCollectionReplacementData
                {
                    Kind = ObjectTemporaryCollectionReplacementKind.LocalFile,
                    Path = normalizedLocalFilePath,
                };
                resolvedPath = ObjectResolvedPath.FromLocalFile(normalizedLocalFilePath);
                return true;
            case ObjectTemporaryCollectionReplacementKind.Memory:
                if (!TryNormalizeMemoryReplacement(
                        memoryOwnerId,
                        replacement,
                        reusableMemoryPaths,
                        out normalizedReplacement,
                        out resolvedPath))
                {
                    return false;
                }

                activeMemoryPaths.Add(resolvedPath.Path);
                return true;
            default:
                resolvedPath = default;
                return false;
        }
    }

    private bool TryNormalizeMemoryReplacement(
        string memoryOwnerId,
        ObjectTemporaryCollectionReplacementData replacement,
        IReadOnlySet<string> reusableMemoryPaths,
        out ObjectTemporaryCollectionReplacementData normalizedReplacement,
        out ObjectResolvedPath resolvedPath)
    {
        normalizedReplacement = default!;
        resolvedPath = default;
        if (replacement.Data.Length > 0)
        {
            if (!ObjectPathRules.TryNormalizeSupportedObjectResourcePath(replacement.Path, out string normalizedGamePath))
            {
                return false;
            }

            resolvedPath = MemoryResourceService.RegisterResource(memoryOwnerId, normalizedGamePath, replacement.Data);
        }
        else if (TryGetReusableMemoryReplacement(memoryOwnerId, replacement.Path, reusableMemoryPaths, out ObjectResolvedPath reusablePath))
        {
            resolvedPath = reusablePath;
        }
        else
        {
            return false;
        }

        normalizedReplacement = new ObjectTemporaryCollectionReplacementData
        {
            Kind = ObjectTemporaryCollectionReplacementKind.Memory,
            Path = resolvedPath.Path,
            Data = [],
        };
        return true;
    }

    private bool TryGetReusableMemoryReplacement(
        string memoryOwnerId,
        string path,
        IReadOnlySet<string> reusableMemoryPaths,
        out ObjectResolvedPath resolvedPath)
    {
        resolvedPath = default;
        if (!ObjectMemoryResourcePathUtility.TryParse(path, out ObjectMemoryResourcePath memoryPath)
         || !reusableMemoryPaths.Contains(memoryPath.Path)
         || !MemoryResourceService.TryGetResource(memoryPath.Path, out ObjectMemoryResource memoryResource)
         || !string.Equals(memoryResource.OwnerId, memoryOwnerId, StringComparison.OrdinalIgnoreCase)
         || !MemoryResourceService.CanLoadMemoryResource(memoryResource))
        {
            return false;
        }

        resolvedPath = ObjectResolvedPath.FromMemory(memoryResource.MemoryPath);
        return true;
    }

    private static IReadOnlyList<RuntimeCollectionMutation> BuildRuntimeCollectionMutations(
        TemporaryWriteContext context,
        ObjectTemporaryCollectionSourceSnapshot? writableExistingSource,
        IReadOnlyDictionary<string, IReadOnlyList<ObjectPathRedirection>> builtCollections)
    {
        List<RuntimeCollectionMutation> mutations = [];
        if (context.ResetSource && context.ExistingSource is not null)
        {
            foreach (ObjectTemporaryCollectionData collection in context.ExistingSource.Collections)
            {
                mutations.Add(new RuntimeCollectionMutation(
                    RuntimeCollectionMutationKind.Remove,
                    collection.CollectionId,
                    []));
            }
        }

        foreach ((string collectionId, IReadOnlyList<ObjectPathRedirection> redirects) in builtCollections)
        {
            mutations.Add(new RuntimeCollectionMutation(
                RuntimeCollectionMutationKind.Register,
                collectionId,
                redirects));
        }

        if (!context.ResetSource && writableExistingSource is not null)
        {
            HashSet<string> activeCollectionIds = new(builtCollections.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (ObjectTemporaryCollectionData collection in writableExistingSource.Collections)
            {
                if (activeCollectionIds.Contains(collection.CollectionId))
                {
                    continue;
                }

                mutations.Add(new RuntimeCollectionMutation(
                    RuntimeCollectionMutationKind.Remove,
                    collection.CollectionId,
                    []));
            }
        }

        return mutations;
    }

    private List<RuntimeCollectionMutation> ClearSourceLocked(string sourceKey, ObjectTemporaryCollectionSourceSnapshot? existingSource)
    {
        List<RuntimeCollectionMutation> mutations = [];
        if (existingSource is null)
        {
            return mutations;
        }

        foreach (ObjectTemporaryCollectionData collection in existingSource.Collections)
        {
            mutations.Add(new RuntimeCollectionMutation(
                RuntimeCollectionMutationKind.Remove,
                collection.CollectionId,
                []));
        }

        _sources.Remove(sourceKey);
        return mutations;
    }

    private static IReadOnlyList<ObjectTemporaryCollectionData> ReplaceCollection(
        IReadOnlyList<ObjectTemporaryCollectionData>? existingCollections,
        ObjectTemporaryCollectionData nextCollection)
    {
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(nextCollection.CollectionId);
        List<ObjectTemporaryCollectionData> nextCollections = [];
        if (existingCollections is not null)
        {
            foreach (ObjectTemporaryCollectionData existingCollection in existingCollections)
            {
                if (!string.Equals(existingCollection.CollectionId, normalizedCollectionId, StringComparison.OrdinalIgnoreCase))
                {
                    nextCollections.Add(existingCollection);
                }
            }
        }

        nextCollections.Add(nextCollection with { CollectionId = normalizedCollectionId });
        return OrderCollections(nextCollections);
    }

    private static bool TryCreateTemporaryCollectionIdSet(
        string sourceKey,
        IReadOnlyList<string> collectionIds,
        out HashSet<string> temporaryCollectionIds)
    {
        temporaryCollectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string collectionId in collectionIds)
        {
            string temporaryCollectionId = ObjectIdentityUtility.CreateTemporaryCollectionId(sourceKey, collectionId);
            if (temporaryCollectionId.Length == 0)
            {
                temporaryCollectionIds = [];
                return false;
            }

            temporaryCollectionIds.Add(temporaryCollectionId);
        }

        return temporaryCollectionIds.Count > 0;
    }

    private static List<ObjectTemporaryCollectionData> RemoveCollections(
        IReadOnlyList<ObjectTemporaryCollectionData>? existingCollections,
        IReadOnlySet<string> collectionIds,
        out int removedCount)
    {
        removedCount = 0;
        List<ObjectTemporaryCollectionData> nextCollections = [];
        if (existingCollections is not null)
        {
            foreach (ObjectTemporaryCollectionData collection in existingCollections)
            {
                if (collectionIds.Contains(collection.CollectionId))
                {
                    ++removedCount;
                    continue;
                }

                nextCollections.Add(collection);
            }
        }

        return OrderCollections(nextCollections);
    }

    private static List<ObjectTemporaryCollectionData> OrderCollections(IEnumerable<ObjectTemporaryCollectionData> collections)
        => collections
            .OrderBy(static collection => collection.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static collection => collection.CollectionId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool TryRemapTemporaryCollections(
        string sourceKey,
        IReadOnlyList<ObjectTemporaryCollectionData>? collections,
        out IReadOnlyList<ObjectTemporaryCollectionData> remappedCollections)
    {
        if (collections is null)
        {
            remappedCollections = [];
            return false;
        }

        List<ObjectTemporaryCollectionData> remappedList = [];
        foreach (ObjectTemporaryCollectionData? collection in collections)
        {
            if (!TryRemapTemporaryCollection(sourceKey, collection, out ObjectTemporaryCollectionData remappedCollection))
            {
                remappedCollections = [];
                return false;
            }

            remappedList.Add(remappedCollection);
        }

        remappedCollections = remappedList;
        return true;
    }

    private static bool TryRemapTemporaryCollection(
        string sourceKey,
        ObjectTemporaryCollectionData? collection,
        out ObjectTemporaryCollectionData remappedCollection)
    {
        remappedCollection = default!;
        if (collection is null)
        {
            return false;
        }

        string remappedCollectionId = ObjectIdentityUtility.CreateTemporaryCollectionId(sourceKey, collection.CollectionId);
        if (remappedCollectionId.Length == 0)
        {
            return false;
        }

        remappedCollection = collection with
        {
            CollectionId = remappedCollectionId,
        };
        return true;
    }

    private static ObjectTemporaryCollectionSourceSnapshot? GetWritableExistingSource(TemporaryWriteContext context)
        => context.ResetSource ? null : context.ExistingSource;

    private ObjectTemporaryMutationResult ApplyRuntimeCollectionMutations(
        ObjectTemporaryMutationResult result,
        IReadOnlyList<RuntimeCollectionMutation> runtimeMutations)
    {
        if (!result.IsSuccess)
        {
            return result;
        }

        try
        {
            ApplyRuntimeCollectionMutations(runtimeMutations);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to apply temporary object collection runtime data");
            return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.RuntimeApplyFailed, result.SourceRevision);
        }
    }

    private void ApplyRuntimeCollectionMutations(IReadOnlyList<RuntimeCollectionMutation> runtimeMutations)
    {
        foreach (RuntimeCollectionMutation mutation in runtimeMutations)
        {
            switch (mutation.Kind)
            {
                case RuntimeCollectionMutationKind.Register:
                    _collectionStore.RegisterCollection(mutation.CollectionId, mutation.Redirects);
                    break;
                case RuntimeCollectionMutationKind.Remove:
                    _collectionStore.RemoveCollection(mutation.CollectionId);
                    break;
            }
        }
    }

    private ObjectTemporarySourceState ResolveCurrentTemporarySourceState(
        string sourceKey,
        ObjectTemporaryCollectionSourceSnapshot? existingSource)
        => existingSource is not null
            ? new ObjectTemporarySourceState(existingSource.SourceSessionId, existingSource.Revision)
            : _sourceStates.GetValueOrDefault(sourceKey);

    private static string CreateMemoryOwnerId(string sourceKey)
        => $"temporary:{sourceKey}";

    private IObjectMemoryResourceService MemoryResourceService
        => _memoryResourceServiceFactory();

    private static HashSet<string> CollectMemoryPaths(IReadOnlyList<ObjectTemporaryCollectionData>? collections)
    {
        HashSet<string> memoryPaths = new(StringComparer.OrdinalIgnoreCase);
        if (collections is null)
        {
            return memoryPaths;
        }

        foreach (ObjectTemporaryCollectionData collection in collections)
        {
            foreach (ObjectTemporaryCollectionRedirectData redirect in collection.Redirects)
            {
                if (redirect.Replacement.Kind == ObjectTemporaryCollectionReplacementKind.Memory
                 && ObjectMemoryResourcePathUtility.TryParse(redirect.Replacement.Path, out ObjectMemoryResourcePath memoryPath))
                {
                    memoryPaths.Add(memoryPath.Path);
                }
            }
        }

        return memoryPaths;
    }
}

