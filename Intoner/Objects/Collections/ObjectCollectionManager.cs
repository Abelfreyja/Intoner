using Intoner.Logging;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using Intoner.Services.Serialization;
using Intoner.Services.Storage;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Intoner.Objects.Collections;

/// <summary> owns object collections, persistence, and runtime materialization </summary>
internal interface IObjectCollectionManager : IAsyncDisposable
{
    /// <summary> raised when collections or runtime state change </summary>
    event Action? CollectionsChanged;

    /// <summary> gets a snapshot of all object collections and their runtime state </summary>
    /// <returns>the current collection snapshots</returns>
    IReadOnlyList<ObjectCollectionSnapshot> GetCollections();

    /// <summary> tries to resolve one object collection snapshot </summary>
    /// <param name="collectionId">the collection id</param>
    /// <param name="snapshot">the collection snapshot when found</param>
    /// <returns>true when the collection exists</returns>
    bool TryGetCollection(string collectionId, out ObjectCollectionSnapshot snapshot);

    /// <summary> creates one new object collection </summary>
    /// <param name="name">the collection display name</param>
    /// <param name="snapshot">the created collection snapshot</param>
    /// <returns>true when the collection was created</returns>
    bool TryCreateCollection(string name, out ObjectCollectionSnapshot snapshot);

    /// <summary> replaces one object collection record and refreshes runtime materialization when already in use </summary>
    /// <param name="record">the complete collection record</param>
    /// <param name="snapshot">the updated collection snapshot</param>
    /// <returns>true when the collection exists and the replacement record is valid</returns>
    bool TryUpdateCollection(ObjectCollection record, out ObjectCollectionSnapshot snapshot);

    /// <summary> deletes one object collection and removes its resolved runtime data </summary>
    /// <param name="collectionId">the collection id</param>
    /// <returns>true when a collection was deleted</returns>
    /// <exception cref="IOException"> collection or layout storage could not be restored after a failed write </exception>
    bool TryDeleteCollection(string collectionId);

    /// <summary> ensures one object collection is materialized for current runtime usage </summary>
    /// <param name="collectionId">the collection id</param>
    /// <param name="additionalSnapshots">optional snapshots that should be included in the current usage set before persistence catches up</param>
    /// <param name="forceResolve">when true, resolves again even if the current materialization is still current</param>
    /// <returns>the current materialization state after requesting the collection</returns>
    ObjectCollectionMaterializationState EnsureCollectionMaterialized(string collectionId, IReadOnlyList<ObjectSnapshot>? additionalSnapshots = null, bool forceResolve = false);

    /// <summary> refreshes tracked runtime collection usage after one object mutation succeeds </summary>
    /// <param name="previousSnapshot">the previous active snapshot when one existed</param>
    /// <param name="nextSnapshot">the next active snapshot when one exists now</param>
    void RefreshCollectionUsage(ObjectSnapshot? previousSnapshot, ObjectSnapshot? nextSnapshot);
}

internal sealed class ObjectCollectionManager : IObjectCollectionManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private sealed class CollectionState
    {
        public required ObjectCollection Record { get; set; }
        public ObjectCollectionResolveState ResolveState { get; set; } = ObjectCollectionResolveState.Inactive;
        public string StatusText { get; set; } = string.Empty;
        public IReadOnlyList<string> Warnings { get; set; } = [];
        public bool KeepingLastGoodSnapshot { get; set; }
        public int RedirectCount { get; set; }
        public long MaterializationGeneration { get; set; } = 1;
        public Dictionary<Guid, ObjectSnapshot> TrackedSnapshots { get; } = [];
        public long InvalidationVersion { get; set; } = 1;
        public long UsageGeneration { get; set; } = 1;
        public CollectionMaterializationStamp CurrentMaterialization { get; set; } = CollectionMaterializationStamp.Empty;
        public CollectionMaterializationStamp PendingMaterialization { get; set; } = CollectionMaterializationStamp.Empty;
        public bool ForceNextRuntimeRefresh { get; set; }
        public CancellationTokenSource? ResolveCancellation { get; set; }
    }

    private readonly record struct CollectionMaterializationStamp(
        string UsageSignature,
        long MaterializationGeneration,
        long InvalidationVersion)
    {
        public static CollectionMaterializationStamp Empty { get; } = new(string.Empty, 0, 0);

        public bool MatchesUsage(string usageSignature, long materializationGeneration)
            => MaterializationGeneration == materializationGeneration
                && string.Equals(UsageSignature, usageSignature, StringComparison.Ordinal);
    }

    private const string IdleStatusText = "collection is idle until assigned to an object";

    private readonly IPluginStoragePaths _pathService;
    private readonly IPluginFileSystem _fileSystem;
    private readonly IObjectCollectionResolver _resolver;
    private readonly IObjectResolvedCollectionStore _resolvedCollectionStore;
    private readonly IObjectModDataSource _modDataSource;
    private readonly IObjectLayoutManager _layoutManager;
    private readonly IObjectPersistenceState _persistenceState;
    private readonly IObjectRevisionTracker _revisionTracker;
    private readonly ILogger<ObjectCollectionManager> _logger;
    private readonly Lock _stateLock;
    private readonly Dictionary<string, CollectionState> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CancellationTokenSource, Task> _resolveTasks = [];
    private readonly DisposalState _disposeState = new();
    private bool _canWriteCollections = true;
    private readonly record struct CollectionUsage(
        IReadOnlyList<ObjectSnapshot> Snapshots,
        string Signature,
        long UsageGeneration);

    public ObjectCollectionManager(
        ILogger<ObjectCollectionManager> logger,
        ObjectStateLock stateLock,
        IPluginStoragePaths pathService,
        IPluginFileSystem fileSystem,
        IObjectCollectionResolver resolver,
        IObjectResolvedCollectionStore resolvedCollectionStore,
        IObjectModDataSource modDataSource,
        IObjectLayoutManager layoutManager,
        IObjectPersistenceState persistenceState,
        IObjectRevisionTracker revisionTracker)
    {
        _logger = logger;
        _stateLock = stateLock.Value;
        _pathService = pathService;
        _fileSystem = fileSystem;
        _resolver = resolver;
        _resolvedCollectionStore = resolvedCollectionStore;
        _modDataSource = modDataSource;
        _layoutManager = layoutManager;
        _persistenceState = persistenceState;
        _revisionTracker = revisionTracker;

        LoadPersistedCollections();
        _modDataSource.StateChanged += HandleModDataChange;
    }

    public event Action? CollectionsChanged;

    public IReadOnlyList<ObjectCollectionSnapshot> GetCollections()
    {
        if (IsDisposing)
        {
            return [];
        }

        lock (_stateLock)
        {
            return _collections.Values
                .Select(CreateSnapshot)
                .OrderBy(static snapshot => snapshot.Record.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static snapshot => snapshot.Record.CollectionId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public bool TryGetCollection(string collectionId, out ObjectCollectionSnapshot snapshot)
    {
        if (IsDisposing)
        {
            snapshot = default!;
            return false;
        }

        lock (_stateLock)
        {
            if (_collections.TryGetValue(ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId), out CollectionState? state))
            {
                snapshot = CreateSnapshot(state);
                return true;
            }
        }

        snapshot = default!;
        return false;
    }

    public bool TryCreateCollection(string name, out ObjectCollectionSnapshot snapshot)
    {
        if (IsDisposing)
        {
            snapshot = default!;
            return false;
        }

        string normalizedName = TextUtility.TrimOrEmpty(name);
        if (normalizedName.Length == 0)
        {
            snapshot = default!;
            return false;
        }

        string collectionId = $"object-collection-{Guid.NewGuid():N}";
        lock (_stateLock)
        {
            ObjectCollection record = new()
            {
                CollectionId = collectionId,
                Name = normalizedName,
            };
            CollectionState state = new()
            {
                Record = record,
                ResolveState = ObjectCollectionResolveState.Inactive,
                StatusText = IdleStatusText,
            };
            if (!TryWriteCollections(_collections.Values
                    .Select(static current => current.Record)
                    .Append(record)))
            {
                snapshot = default!;
                return false;
            }

            _collections.Add(collectionId, state);
            snapshot = CreateSnapshot(state);
        }

        RaiseCollectionsChanged();
        return true;
    }

    public bool TryUpdateCollection(ObjectCollection record, out ObjectCollectionSnapshot snapshot)
    {
        if (IsDisposing)
        {
            snapshot = default!;
            return false;
        }

        string collectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(record.CollectionId);
        bool shouldMaterialize;
        lock (_stateLock)
        {
            if (!_collections.TryGetValue(collectionId, out CollectionState? state))
            {
                snapshot = default!;
                return false;
            }

            if (!TryNormalizeRecord(record, out ObjectCollection normalizedRecord))
            {
                snapshot = default!;
                return false;
            }

            bool materializationChanged = !AreEntriesEqual(state.Record.Entries, normalizedRecord.Entries);
            if (!materializationChanged
                && string.Equals(state.Record.Name, normalizedRecord.Name, StringComparison.Ordinal))
            {
                snapshot = CreateSnapshot(state);
                return true;
            }

            if (!TryWriteCollections(_collections.Values.Select(current => ReferenceEquals(current, state)
                    ? normalizedRecord
                    : current.Record)))
            {
                snapshot = default!;
                return false;
            }

            ObjectCollection previousRecord = state.Record;
            long previousMaterializationGeneration = state.MaterializationGeneration;
            state.Record = normalizedRecord;
            if (materializationChanged)
            {
                checked
                {
                    state.MaterializationGeneration++;
                }
            }

            PersistentMutationStatus revisionStatus = AdvanceDependentRevisions(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { collectionId });
            if (revisionStatus != PersistentMutationStatus.Success)
            {
                state.Record = previousRecord;
                state.MaterializationGeneration = previousMaterializationGeneration;
                bool restored = TryWriteCollections(_collections.Values.Select(static current => current.Record));
                if (!restored)
                {
                    state.Record = normalizedRecord;
                    state.MaterializationGeneration = materializationChanged
                        ? checked(previousMaterializationGeneration + 1)
                        : previousMaterializationGeneration;
                }

                if (!restored || revisionStatus == PersistentMutationStatus.RecoveryRequired)
                {
                    _logger.LogCritical(
                        "object collection {CollectionId} dependency revision update failed and storage recovery is required",
                        collectionId);
                }

                snapshot = default!;
                return false;
            }

            bool hasTrackedUsage = HasTrackedUsageLocked(state);
            shouldMaterialize = materializationChanged && hasTrackedUsage;
            if (!hasTrackedUsage)
            {
                ResetRuntimeStateLocked(state);

                if (materializationChanged)
                {
                    _resolvedCollectionStore.RemoveCollection(collectionId);
                }
            }

            snapshot = CreateSnapshot(state);
        }

        RaiseCollectionsChanged();
        if (shouldMaterialize)
        {
            EnsureCollectionMaterialized(collectionId);
        }

        return true;
    }

    public bool TryDeleteCollection(string collectionId)
    {
        if (IsDisposing)
        {
            return false;
        }

        CollectionState? removedState;
        lock (_stateLock)
        {
            string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
            if (!_collections.TryGetValue(normalizedCollectionId, out removedState)
                || !TryWriteCollections(_collections
                    .Where(pair => !string.Equals(pair.Key, normalizedCollectionId, StringComparison.OrdinalIgnoreCase))
                    .Select(static pair => pair.Value.Record)))
            {
                return false;
            }

            _ = _collections.Remove(normalizedCollectionId);
            PersistentMutationStatus revisionStatus = AdvanceDependentRevisions(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalizedCollectionId });
            if (revisionStatus != PersistentMutationStatus.Success)
            {
                _collections.Add(normalizedCollectionId, removedState);
                bool restored = TryWriteCollections(_collections.Values.Select(static current => current.Record));
                if (!restored)
                {
                    _ = _collections.Remove(normalizedCollectionId);
                }

                if (!restored || revisionStatus == PersistentMutationStatus.RecoveryRequired)
                {
                    throw new IOException($"collection {normalizedCollectionId} deletion failed and storage could not be restored");
                }

                return false;
            }

            removedState.ResolveCancellation?.Cancel();
            _resolvedCollectionStore.RemoveCollection(removedState.Record.CollectionId);
        }

        RaiseCollectionsChanged();
        return true;
    }

    public ObjectCollectionMaterializationState EnsureCollectionMaterialized(string collectionId, IReadOnlyList<ObjectSnapshot>? additionalSnapshots = null, bool forceResolve = false)
    {
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);

        if (IsDisposing || normalizedCollectionId.Length == 0)
        {
            return ObjectCollectionMaterializationState.Current;
        }

        bool stateChanged;
        ObjectCollectionMaterializationState materializationState;
        lock (_stateLock)
        {
            if (IsDisposing || !_collections.TryGetValue(normalizedCollectionId, out CollectionState? state))
            {
                return ObjectCollectionMaterializationState.Current;
            }

            CollectionUsage usage = ResolveCollectionUsageLocked(state, additionalSnapshots);

            if (usage.Snapshots.Count == 0)
            {
                stateChanged = ClearTrackedUsageLocked(state);
                stateChanged |= _resolvedCollectionStore.RemoveCollection(normalizedCollectionId);
                materializationState = ObjectCollectionMaterializationState.Current;
            }
            else
            {
                CollectionMaterializationStamp stamp = CreateMaterializationStampLocked(state, usage.Signature);
                bool isCurrent = IsMaterializedLocked(state, stamp);
                stateChanged = forceResolve || (!isCurrent && !HasPendingResolveLocked(state, stamp));
                materializationState = isCurrent && !forceResolve
                    ? ObjectCollectionMaterializationState.Current
                    : ObjectCollectionMaterializationState.Pending;

                if (stateChanged)
                {
                    state.StatusText = "waiting to resolve object collection";
                    state.ResolveState = ObjectCollectionResolveState.Resolving;
                    state.Warnings = [];
                    state.KeepingLastGoodSnapshot = false;
                    state.ForceNextRuntimeRefresh |= forceResolve;
                    state.ResolveCancellation?.Cancel();
                    CancellationTokenSource cancellation = new();
                    state.ResolveCancellation = cancellation;
                    state.PendingMaterialization = stamp;
                    _resolveTasks.Add(cancellation, Task.Run(() =>
                        ResolveCollectionAsync(normalizedCollectionId, usage, cancellation, stamp)));
                }
            }
        }

        if (stateChanged)
        {
            RaiseCollectionsChanged();
        }

        return materializationState;
    }

    public void RefreshCollectionUsage(ObjectSnapshot? previousSnapshot, ObjectSnapshot? nextSnapshot)
    {
        if (IsDisposing)
        {
            return;
        }

        List<string> affectedCollectionIds;
        lock (_stateLock)
        {
            affectedCollectionIds = UpdateTrackedUsageLocked(previousSnapshot, nextSnapshot);
        }

        foreach (string collectionId in affectedCollectionIds)
        {
            EnsureCollectionMaterialized(collectionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        _modDataSource.StateChanged -= HandleModDataChange;

        try
        {
            await StopResolveTasksAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_stateLock)
            {
                foreach (string collectionId in _collections.Keys)
                {
                    _resolvedCollectionStore.RemoveCollection(collectionId);
                }
            }
        }
    }

    private Task StopResolveTasksAsync()
    {
        lock (_stateLock)
        {
            Task completion = Task.WhenAll(_resolveTasks.Values);
            // cancellation and disposal share the same lock
            foreach (CancellationTokenSource cancellation in _resolveTasks.Keys.ToArray())
            {
                if (!_resolveTasks.ContainsKey(cancellation))
                {
                    continue;
                }

                try
                {
                    cancellation.Cancel();
                }
                catch (AggregateException ex)
                {
                    _logger.LogWarning(ex, "object collection resolve cancellation failed");
                }
            }

            return completion;
        }
    }

    private bool IsDisposing
        => _disposeState.IsDisposing;

    private void LoadPersistedCollections()
    {
        ObjectCollectionsFile document = LoadCollectionsDocument();
        lock (_stateLock)
        {
            _collections.Clear();
            foreach (ObjectCollection? record in document.Collections)
            {
                if (!TryNormalizeRecord(record, out ObjectCollection normalizedRecord))
                {
                    _canWriteCollections = false;
                    _logger.LogWarning(
                        "skipping invalid persisted object collection record with collection id '{CollectionId}'",
                        record?.CollectionId);
                    continue;
                }

                if (!_collections.TryAdd(normalizedRecord.CollectionId, new CollectionState
                    {
                        Record = normalizedRecord,
                        ResolveState = ObjectCollectionResolveState.Inactive,
                        StatusText = IdleStatusText,
                    }))
                {
                    _canWriteCollections = false;
                    _logger.LogWarning("duplicate persisted object collection {CollectionId}; collection writes are disabled", normalizedRecord.CollectionId);
                }
            }
        }
    }

    private ObjectCollectionsFile LoadCollectionsDocument()
    {
        if (!_fileSystem.FileExists(_pathService.ObjectCollectionsPath))
        {
            return new ObjectCollectionsFile();
        }

        _canWriteCollections = false;
        try
        {
            ObjectCollectionsFile? document = JsonSerializer.Deserialize<ObjectCollectionsFile>(
                JsonDocumentUtility.ReadFile(_fileSystem, _pathService.ObjectCollectionsPath),
                JsonOptions);
            if (document is null)
            {
                return new ObjectCollectionsFile();
            }

            if (document.Version != 1)
            {
                _logger.LogWarning("unsupported object collections version {Version}", document.Version);
                return new ObjectCollectionsFile();
            }

            if (document.Collections is null)
            {
                _logger.LogWarning("object collections file has no collection list");
                return new ObjectCollectionsFile();
            }

            _canWriteCollections = true;
            return document;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load object collections");
        }

        return new ObjectCollectionsFile();
    }

    private bool TryWriteCollections(IEnumerable<ObjectCollection> collections)
        => TryWriteDocument(new ObjectCollectionsFile
        {
            Collections = collections
                .OrderBy(static record => record.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static record => record.CollectionId, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        });

    private bool TryWriteDocument(ObjectCollectionsFile document)
    {
        if (!_canWriteCollections)
        {
            return false;
        }

        try
        {
            string json = JsonSerializer.Serialize(document, JsonOptions);
            JsonDocumentUtility.WriteFile(_fileSystem, _pathService.ObjectCollectionsPath, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to save object collections");
            return false;
        }
    }

    private static void ResetRuntimeStateLocked(CollectionState state)
    {
        state.ResolveState = ObjectCollectionResolveState.Inactive;
        state.StatusText = IdleStatusText;
        state.Warnings = [];
        state.KeepingLastGoodSnapshot = false;
        state.RedirectCount = 0;
        state.CurrentMaterialization = CollectionMaterializationStamp.Empty;
        state.PendingMaterialization = CollectionMaterializationStamp.Empty;
        state.ForceNextRuntimeRefresh = false;
    }

    private static bool ClearTrackedUsageLocked(CollectionState state)
    {
        bool hadTrackedUsage = HasTrackedUsageLocked(state);
        state.ResolveCancellation?.Cancel();
        state.ResolveCancellation = null;
        state.TrackedSnapshots.Clear();
        if (hadTrackedUsage)
        {
            checked
            {
                state.UsageGeneration++;
            }
        }

        ResetRuntimeStateLocked(state);
        return hadTrackedUsage;
    }

    private static bool HasTrackedUsageLocked(CollectionState state)
        => state.TrackedSnapshots.Count > 0;

    private static CollectionUsage ResolveCollectionUsageLocked(CollectionState state, IReadOnlyList<ObjectSnapshot>? additionalSnapshots)
    {
        Dictionary<Guid, ObjectSnapshot> usageById = new(state.TrackedSnapshots);

        if (additionalSnapshots is not null)
        {
            foreach (ObjectSnapshot snapshot in additionalSnapshots.Where(snapshot =>
                         string.Equals(snapshot.CollectionId, state.Record.CollectionId, StringComparison.OrdinalIgnoreCase)))
            {
                usageById[snapshot.Id] = snapshot;
            }
        }

        IReadOnlyList<ObjectSnapshot> snapshots = usageById.Values.OrderBy(static snapshot => snapshot.Id).ToList();
        return new CollectionUsage(snapshots, BuildUsageSignature(snapshots), state.UsageGeneration);
    }

    private List<string> UpdateTrackedUsageLocked(ObjectSnapshot? previousSnapshot, ObjectSnapshot? nextSnapshot)
    {
        HashSet<string> affectedCollectionIds = new(StringComparer.OrdinalIgnoreCase);
        UpdateTrackedUsageLocked(previousSnapshot, affectedCollectionIds, remove: true);
        UpdateTrackedUsageLocked(nextSnapshot, affectedCollectionIds, remove: false);
        return affectedCollectionIds.ToList();
    }

    private void UpdateTrackedUsageLocked(ObjectSnapshot? snapshot, ISet<string> affectedCollectionIds, bool remove)
    {
        if (snapshot is null)
        {
            return;
        }

        string collectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId);
        if (collectionId.Length == 0
         || !_collections.TryGetValue(collectionId, out CollectionState? state))
        {
            return;
        }

        affectedCollectionIds.Add(collectionId);
        if (remove)
        {
            if (state.TrackedSnapshots.Remove(snapshot.Id))
            {
                checked
                {
                    state.UsageGeneration++;
                }
            }

            return;
        }

        if (!state.TrackedSnapshots.TryGetValue(snapshot.Id, out ObjectSnapshot? previousSnapshot)
         || !Equals(previousSnapshot, snapshot))
        {
            checked
            {
                state.UsageGeneration++;
            }
        }

        state.TrackedSnapshots[snapshot.Id] = snapshot;
    }

    private static string BuildUsageSignature(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            snapshots
                .Select(static snapshot => $"{snapshot.Id:N}|{snapshot.Kind}|{ObjectResourceRoots.FromModel(snapshot.Model)}")
                .OrderBy(static signature => signature, StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildTrackedUsageSignatureLocked(CollectionState state)
        => BuildUsageSignature(state.TrackedSnapshots.Values
            .OrderBy(static snapshot => snapshot.Id)
            .ToList());

    private void HandleModDataChange(ObjectModDataChange invalidation)
    {
        if (IsDisposing)
        {
            return;
        }

        List<string> affectedCollectionIds = [];
        List<string> trackedCollectionIds = [];
        lock (_stateLock)
        {
            bool forceRuntimeRefresh = invalidation.Kind == ObjectModDataChangeKind.ModContentChanged;
            foreach ((string collectionId, CollectionState state) in _collections)
            {
                if (!InvalidationTouchesCollection(invalidation, state.Record))
                {
                    continue;
                }

                affectedCollectionIds.Add(collectionId);
                if (state.TrackedSnapshots.Count == 0)
                {
                    continue;
                }

                checked
                {
                    state.InvalidationVersion++;
                }

                if (forceRuntimeRefresh)
                {
                    state.ForceNextRuntimeRefresh = true;
                }

                trackedCollectionIds.Add(collectionId);
            }

            if (affectedCollectionIds.Count > 0)
            {
                PersistentMutationStatus revisionStatus = AdvanceDependentRevisions(
                    affectedCollectionIds.ToHashSet(StringComparer.OrdinalIgnoreCase));
                if (revisionStatus != PersistentMutationStatus.Success)
                {
                    _logger.LogError(
                        "could not advance layout revisions after {ChangeKind} object mod data change: {Status}",
                        invalidation.Kind,
                        revisionStatus);
                }
            }
        }

        foreach (string collectionId in trackedCollectionIds)
        {
            EnsureCollectionMaterialized(collectionId);
        }
    }

    private async Task ResolveCollectionAsync(
        string collectionId,
        CollectionUsage usage,
        CancellationTokenSource resolveCancellation,
        CollectionMaterializationStamp materializationStamp)
    {
        try
        {
            ObjectCollection record;
            lock (_stateLock)
            {
                if (IsDisposing
                    || !_collections.TryGetValue(collectionId, out CollectionState? state)
                    || !ReferenceEquals(state.ResolveCancellation, resolveCancellation))
                {
                    return;
                }

                record = CloneCollectionRecord(state.Record);
            }

            ObjectCollectionResolveResult result = await _resolver.ResolveAsync(
                record, usage.Snapshots, resolveCancellation.Token).ConfigureAwait(false);
            if (TryApplyResolveResult(collectionId, usage, resolveCancellation, materializationStamp, result))
            {
                DiagnosticLog.Write(() => _logger.LogInformation(
                    "object collection {CollectionId} resolve result {ResolveState} with {RedirectCount} redirects for {UsageCount} objects: {StatusText}",
                    collectionId, result.ResolveState, result.Redirects.Count, usage.Snapshots.Count, result.StatusText));
                RaiseCollectionsChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // ignore replaced resolve requests
        }
        catch (Exception ex)
        {
            ObjectCollectionResolveResult failure = new()
            {
                ResolveState = ObjectCollectionResolveState.ResolveFailed,
                StatusText = $"object collection resolve failed: {ex.Message}",
            };
            if (TryApplyResolveResult(collectionId, usage, resolveCancellation, materializationStamp, failure, forceRuntimeRefresh: true))
            {
                DiagnosticLog.Write(() => _logger.LogWarning(ex, "object collection {CollectionId} resolve task failed", collectionId));
                RaiseCollectionsChanged();
            }
        }
        finally
        {
            DisposeResolveCancellation(collectionId, resolveCancellation);
        }
    }

    private bool TryApplyResolveResult(
        string collectionId,
        CollectionUsage usage,
        CancellationTokenSource resolveCancellation,
        CollectionMaterializationStamp materializationStamp,
        ObjectCollectionResolveResult resolveResult,
        bool forceRuntimeRefresh = false)
    {
        lock (_stateLock)
        {
            if (IsDisposing
                || resolveCancellation.IsCancellationRequested
                || !_collections.TryGetValue(collectionId, out CollectionState? state)
                || !CanApplyResolveResultLocked(state, usage, resolveCancellation, materializationStamp))
            {
                return false;
            }

            ObjectCollectionResolveData? retainedSnapshot = null;
            bool keepLastGoodSnapshot = resolveResult.KeepLastGoodSnapshot
                && state.CurrentMaterialization.MatchesUsage(usage.Signature, state.MaterializationGeneration)
                && _resolvedCollectionStore.TryGetCollection(state.Record.CollectionId, out retainedSnapshot);

            state.CurrentMaterialization = materializationStamp;
            state.PendingMaterialization = CollectionMaterializationStamp.Empty;
            forceRuntimeRefresh |= state.ForceNextRuntimeRefresh;
            state.ForceNextRuntimeRefresh = false;
            state.ResolveState = resolveResult.ResolveState;
            state.StatusText = resolveResult.StatusText;
            state.Warnings = resolveResult.Warnings;
            state.KeepingLastGoodSnapshot = keepLastGoodSnapshot;
            state.RedirectCount = keepLastGoodSnapshot ? retainedSnapshot!.Redirects.Count : resolveResult.Redirects.Count;
            IReadOnlyList<ObjectPathRedirection> runtimeRedirects = resolveResult.ResolveState == ObjectCollectionResolveState.Ready
                ? resolveResult.Redirects
                : [];
            IReadOnlyList<CollectionResourceView> runtimeResourceViews = resolveResult.ResourceViews;

            if (keepLastGoodSnapshot && forceRuntimeRefresh)
            {
                runtimeRedirects = ObjectPathRedirectionUtility.CreateStableList(retainedSnapshot!.Redirects.Select(static pair =>
                    new ObjectPathRedirection(pair.Key, pair.Value)));
                List<CollectionResourceView> retainedViews = new(retainedSnapshot.ResourceRevisions.Count);

                foreach (CollectionResourceRevision view in retainedSnapshot.ResourceRevisions.Values)
                {
                    retainedViews.Add(new CollectionResourceView(view.RootPath,
                        ObjectPathRedirectionUtility.CreateStableList(view.Redirects.Select(static pair =>
                            new ObjectPathRedirection(pair.Key, pair.Value)))));
                }

                runtimeResourceViews = retainedViews;
            }

            if (!keepLastGoodSnapshot || forceRuntimeRefresh)
            {
                _resolvedCollectionStore.RegisterCollection(
                    state.Record.CollectionId,
                    runtimeRedirects,
                    forceRefresh: forceRuntimeRefresh,
                    resourceViews: runtimeResourceViews);
            }

            return true;
        }
    }

    private void DisposeResolveCancellation(string collectionId, CancellationTokenSource resolveCancellation)
    {
        lock (_stateLock)
        {
            if (_collections.TryGetValue(collectionId, out CollectionState? state)
             && ReferenceEquals(state.ResolveCancellation, resolveCancellation))
            {
                state.ResolveCancellation = null;
            }

            _resolveTasks.Remove(resolveCancellation);
            resolveCancellation.Dispose();
        }
    }

    private static bool CanApplyResolveResultLocked(
        CollectionState state,
        CollectionUsage usage,
        CancellationTokenSource resolveCancellation,
        CollectionMaterializationStamp materializationStamp)
    {
        if (!ReferenceEquals(state.ResolveCancellation, resolveCancellation)
         || state.MaterializationGeneration != materializationStamp.MaterializationGeneration
         || state.InvalidationVersion != materializationStamp.InvalidationVersion)
        {
            return false;
        }

        return state.UsageGeneration == usage.UsageGeneration
            || string.Equals(BuildTrackedUsageSignatureLocked(state), usage.Signature, StringComparison.Ordinal);
    }

    private static CollectionMaterializationStamp CreateMaterializationStampLocked(CollectionState state, string usageSignature)
        => new(usageSignature, state.MaterializationGeneration, state.InvalidationVersion);

    private static bool HasPendingResolveLocked(CollectionState state, CollectionMaterializationStamp materializationStamp)
        => state.ResolveCancellation is { IsCancellationRequested: false }
            && state.PendingMaterialization == materializationStamp;

    private static bool IsMaterializedLocked(CollectionState state, CollectionMaterializationStamp materializationStamp)
        => state.CurrentMaterialization == materializationStamp;

    private static bool TryNormalizeRecord(ObjectCollection? record, out ObjectCollection normalizedRecord)
    {
        if (record is null)
        {
            normalizedRecord = default!;
            return false;
        }

        string collectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(record.CollectionId);
        string normalizedName = TextUtility.TrimOrEmpty(record.Name);
        if (collectionId.Length == 0 || normalizedName.Length == 0 || record.Entries is null)
        {
            normalizedRecord = default!;
            return false;
        }

        List<ObjectCollectionModSettings> normalizedEntries = [];
        foreach (ObjectCollectionModSettings? entry in record.Entries)
        {
            if (!TryNormalizeEntry(entry, out ObjectCollectionModSettings normalizedEntry))
            {
                normalizedRecord = default!;
                return false;
            }

            if (normalizedEntry.ModDirectory.Length > 0)
            {
                normalizedEntries.Add(normalizedEntry);
            }
        }

        List<ObjectCollectionModSettings> entries = normalizedEntries
            .GroupBy(static entry => ObjectCollectionKeyUtility.NormalizeModDirectory(entry.ModDirectory), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Last())
            .OrderByDescending(static entry => entry.Priority)
            .ThenBy(static entry => entry.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.ModDirectory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        normalizedRecord = new ObjectCollection
        {
            CollectionId = collectionId,
            Name = normalizedName,
            Entries = entries,
        };
        return true;
    }

    private static bool TryNormalizeEntry(ObjectCollectionModSettings? entry, out ObjectCollectionModSettings normalizedEntry)
    {
        normalizedEntry = default!;
        if (entry?.Settings is null)
        {
            return false;
        }

        Dictionary<string, List<string>> settings = new(StringComparer.Ordinal);
        foreach ((string groupName, List<string>? optionNames) in entry.Settings)
        {
            string normalizedGroupName = CollectionModSettingsUtility.NormalizeGroupName(groupName);
            if (!CollectionModSettingsUtility.TryNormalizeOptionNames(optionNames, out List<string> normalizedOptions))
            {
                return false;
            }

            settings[normalizedGroupName] = normalizedOptions;
        }

        normalizedEntry = new ObjectCollectionModSettings
        {
            ModDirectory = TextUtility.TrimOrEmpty(entry.ModDirectory),
            ModName = TextUtility.TrimOrEmpty(entry.ModName),
            Enabled = entry.Enabled,
            Priority = entry.Priority,
            Settings = settings,
        };
        return true;
    }

    private static bool AreEntriesEqual(IReadOnlyList<ObjectCollectionModSettings> left, IReadOnlyList<ObjectCollectionModSettings> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; ++index)
        {
            if (!AreEntriesEqual(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreEntriesEqual(ObjectCollectionModSettings left, ObjectCollectionModSettings right)
    {
        if (!string.Equals(left.ModDirectory, right.ModDirectory, StringComparison.OrdinalIgnoreCase)
         || !string.Equals(left.ModName, right.ModName, StringComparison.OrdinalIgnoreCase)
         || left.Enabled != right.Enabled
         || left.Priority != right.Priority)
        {
            return false;
        }

        return CollectionModSettingsUtility.AreEqual(left.Settings, right.Settings);
    }

    private PersistentMutationStatus AdvanceDependentRevisions(IReadOnlySet<string> collectionIds)
    {
        bool standaloneAffected = _persistenceState.GetStandaloneSnapshots().Any(snapshot => collectionIds.Contains(
            ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId)));
        PersistentMutationStatus status = _layoutManager.AdvanceCollectionDependencyRevisions(
            collectionIds,
            out bool enabledLayoutAffected);
        if (status == PersistentMutationStatus.Success && (standaloneAffected || enabledLayoutAffected))
        {
            _revisionTracker.Increment(persistentChanged: true);
        }

        return status;
    }

    private static ObjectCollectionSnapshot CreateSnapshot(CollectionState state)
        => new()
        {
            Record = CloneCollectionRecord(state.Record),
            ResolveState = state.ResolveState,
            StatusText = state.StatusText,
            Warnings = state.Warnings.ToList(),
            KeepingLastGoodSnapshot = state.KeepingLastGoodSnapshot,
            RedirectCount = state.RedirectCount,
        };

    private static ObjectCollection CloneCollectionRecord(ObjectCollection record)
        => new()
        {
            CollectionId = record.CollectionId,
            Name = record.Name,
            Entries = record.Entries
                .Select(CloneCollectionEntry)
                .ToList(),
        };

    private static ObjectCollectionModSettings CloneCollectionEntry(ObjectCollectionModSettings entry)
        => new()
        {
            ModDirectory = entry.ModDirectory,
            ModName = entry.ModName,
            Enabled = entry.Enabled,
            Priority = entry.Priority,
            Settings = CollectionModSettingsUtility.CloneSettings(entry.Settings),
        };

    private void RaiseCollectionsChanged()
    {
        if (IsDisposing)
        {
            return;
        }

        try
        {
            CollectionsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "object collection change handler failed");
        }
    }

    private static bool InvalidationTouchesCollection(ObjectModDataChange invalidation, ObjectCollection record)
    {
        foreach (ObjectCollectionModSettings entry in record.Entries)
        {
            if (!entry.Enabled)
            {
                continue;
            }

            string normalizedModDirectory = ObjectCollectionKeyUtility.NormalizeModDirectory(entry.ModDirectory);
            if (normalizedModDirectory.Length > 0
             && (invalidation.AffectsAllCollections
                 || invalidation.AffectedModDirectories.Contains(normalizedModDirectory)))
            {
                return true;
            }
        }

        return false;
    }
}
