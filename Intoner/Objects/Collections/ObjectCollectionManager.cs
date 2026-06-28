using Intoner.Objects.Filesystem.Storage;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Intoner.Objects.Collections;

/// <summary> owns object collections, persistence, and runtime materialization </summary>
internal interface IObjectCollectionManager : IDisposable
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

    private readonly IObjectStoragePathService _pathService;
    private readonly IObjectFileSystem _fileSystem;
    private readonly IObjectCollectionResolver _resolver;
    private readonly IObjectResolvedCollectionStore _resolvedCollectionStore;
    private readonly IObjectModDataSource _modDataSource;
    private readonly ILogger<ObjectCollectionManager> _logger;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<string, CollectionState> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObjectDisposalState _disposeState = new();
    private readonly record struct CollectionUsage(
        IReadOnlyList<ObjectSnapshot> Snapshots,
        string Signature,
        long UsageGeneration);

    public ObjectCollectionManager(
        ILogger<ObjectCollectionManager> logger,
        IObjectStoragePathService pathService,
        IObjectFileSystem fileSystem,
        IObjectCollectionResolver resolver,
        IObjectResolvedCollectionStore resolvedCollectionStore,
        IObjectModDataSource modDataSource)
    {
        _logger = logger;
        _pathService = pathService;
        _fileSystem = fileSystem;
        _resolver = resolver;
        _resolvedCollectionStore = resolvedCollectionStore;
        _modDataSource = modDataSource;

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

        string normalizedName = ObjectStringUtility.TrimOrEmpty(name);
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
            _collections.Add(collectionId, state);
            TryWriteCollections();
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
        bool shouldRemoveRuntimeCollection;
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
            state.Record = normalizedRecord;
            if (materializationChanged)
            {
                checked
                {
                    state.MaterializationGeneration++;
                }
            }

            bool hasTrackedUsage = HasTrackedUsageLocked(state);
            shouldMaterialize = materializationChanged && hasTrackedUsage;
            shouldRemoveRuntimeCollection = materializationChanged && !hasTrackedUsage;
            if (!hasTrackedUsage)
            {
                ResetRuntimeStateLocked(state);
            }
            TryWriteCollections();
            snapshot = CreateSnapshot(state);
        }

        if (shouldRemoveRuntimeCollection)
        {
            _resolvedCollectionStore.RemoveCollection(collectionId);
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
            if (!_collections.Remove(normalizedCollectionId, out removedState))
            {
                return false;
            }

            removedState.ResolveCancellation?.Cancel();
            TryWriteCollections();
        }

        _resolvedCollectionStore.RemoveCollection(removedState.Record.CollectionId);
        RaiseCollectionsChanged();
        return true;
    }

    public ObjectCollectionMaterializationState EnsureCollectionMaterialized(string collectionId, IReadOnlyList<ObjectSnapshot>? additionalSnapshots = null, bool forceResolve = false)
    {
        if (IsDisposing)
        {
            return ObjectCollectionMaterializationState.Current;
        }

        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        if (normalizedCollectionId.Length == 0)
        {
            return ObjectCollectionMaterializationState.Current;
        }

        CollectionUsage usage = ResolveCollectionUsage(normalizedCollectionId, additionalSnapshots);
        if (usage.Snapshots.Count == 0)
        {
            bool stateChanged;
            lock (_stateLock)
            {
                if (!_collections.TryGetValue(normalizedCollectionId, out CollectionState? state))
                {
                    return ObjectCollectionMaterializationState.Current;
                }

                stateChanged = ClearTrackedUsageLocked(state);
            }

            bool removedRuntimeCollection = _resolvedCollectionStore.RemoveCollection(normalizedCollectionId);
            if (stateChanged || removedRuntimeCollection)
            {
                RaiseCollectionsChanged();
            }

            return ObjectCollectionMaterializationState.Current;
        }

        bool shouldResolve;
        ObjectCollectionMaterializationState materializationState;
        CollectionMaterializationStamp materializationStamp;
        lock (_stateLock)
        {
            if (!_collections.TryGetValue(normalizedCollectionId, out CollectionState? state))
            {
                return ObjectCollectionMaterializationState.Current;
            }

            materializationStamp = CreateMaterializationStampLocked(state, usage.Signature);
            bool isCurrent = IsMaterializedLocked(state, materializationStamp);
            bool hasPendingResolve = HasPendingResolveLocked(state, materializationStamp);
            shouldResolve = forceResolve || (!isCurrent && !hasPendingResolve);
            materializationState = isCurrent && !forceResolve
                ? ObjectCollectionMaterializationState.Current
                : ObjectCollectionMaterializationState.Pending;
            if (shouldResolve)
            {
                state.StatusText = "waiting to resolve object collection";
                state.ResolveState = ObjectCollectionResolveState.Resolving;
                state.Warnings = [];
                state.KeepingLastGoodSnapshot = false;
                if (forceResolve)
                {
                    state.ForceNextRuntimeRefresh = true;
                }
            }
        }

        if (shouldResolve)
        {
            RaiseCollectionsChanged();
            ScheduleResolve(normalizedCollectionId, usage, materializationStamp);
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

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        _modDataSource.StateChanged -= HandleModDataChange;

        List<CancellationTokenSource> cancellations;
        lock (_stateLock)
        {
            cancellations = _collections.Values
                .Select(static state => state.ResolveCancellation)
                .Where(static cancellation => cancellation is not null)
                .Cast<CancellationTokenSource>()
                .ToList();
        }

        foreach (CancellationTokenSource cancellation in cancellations)
        {
            cancellation.Cancel();
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
            foreach (ObjectCollection record in document.Collections)
            {
                if (!TryNormalizeRecord(record, out ObjectCollection normalizedRecord))
                {
                    _logger.LogWarning(
                        "skipping invalid persisted object collection record with collection id '{CollectionId}'",
                        record.CollectionId);
                    continue;
                }

                _collections[normalizedRecord.CollectionId] = new CollectionState
                {
                    Record = normalizedRecord,
                    ResolveState = ObjectCollectionResolveState.Inactive,
                    StatusText = IdleStatusText,
                };
            }
        }
    }

    private ObjectCollectionsFile LoadCollectionsDocument()
    {
        if (!_fileSystem.FileExists(_pathService.ObjectCollectionsPath))
        {
            return new ObjectCollectionsFile();
        }

        try
        {
            ObjectCollectionsFile? document = JsonSerializer.Deserialize<ObjectCollectionsFile>(
                _fileSystem.ReadAllText(_pathService.ObjectCollectionsPath),
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

            return document;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load object collections");
        }

        return new ObjectCollectionsFile();
    }

    private void TryWriteCollections()
        => TryWriteDocument(new ObjectCollectionsFile
        {
            Collections = _collections.Values
                .Select(static state => state.Record)
                .OrderBy(static record => record.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static record => record.CollectionId, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        });

    private void TryWriteDocument(ObjectCollectionsFile document)
    {
        try
        {
            string json = JsonSerializer.Serialize(document, JsonOptions);
            _fileSystem.WriteAllTextAtomic(_pathService.ObjectCollectionsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to save object collections");
        }
    }

    private void ResetRuntimeStateLocked(CollectionState state)
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

    private bool ClearTrackedUsageLocked(CollectionState state)
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

    private CollectionUsage ResolveCollectionUsage(string collectionId, IReadOnlyList<ObjectSnapshot>? additionalSnapshots)
    {
        IReadOnlyList<ObjectSnapshot> usageSnapshots;
        lock (_stateLock)
        {
            if (!_collections.TryGetValue(collectionId, out CollectionState? state))
            {
                return new CollectionUsage([], string.Empty, 0);
            }

            Dictionary<Guid, ObjectSnapshot> usageById = new(state.TrackedSnapshots);
            if (additionalSnapshots is not null)
            {
                foreach (ObjectSnapshot snapshot in additionalSnapshots)
                {
                    if (!string.Equals(snapshot.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    usageById[snapshot.Id] = snapshot;
                }
            }

            usageSnapshots = usageById.Values
                .OrderBy(static snapshot => snapshot.Id)
                .ToList();
            return new CollectionUsage(usageSnapshots, BuildUsageSignature(usageSnapshots), state.UsageGeneration);
        }
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
                .Select(static snapshot => $"{snapshot.Id:N}|{snapshot.Kind}|{ObjectSnapshotUtility.GetRootResourcePath(snapshot)}")
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

        List<string> trackedCollectionIds = [];
        lock (_stateLock)
        {
            bool forceRuntimeRefresh = invalidation.Kind == ObjectModDataChangeKind.ModContentChanged;
            foreach ((string collectionId, CollectionState state) in _collections)
            {
                if (state.TrackedSnapshots.Count == 0 || !InvalidationTouchesCollection(invalidation, state.Record))
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
        }

        foreach (string collectionId in trackedCollectionIds)
        {
            EnsureCollectionMaterialized(collectionId);
        }
    }

    private void ScheduleResolve(
        string collectionId,
        CollectionUsage usage,
        CollectionMaterializationStamp materializationStamp)
    {
        if (IsDisposing)
        {
            return;
        }

        CancellationTokenSource cancellation;
        CancellationToken cancellationToken;
        lock (_stateLock)
        {
            if (IsDisposing || !_collections.TryGetValue(collectionId, out CollectionState? state))
            {
                return;
            }

            state.ResolveCancellation?.Cancel();
            cancellation = new CancellationTokenSource();
            cancellationToken = cancellation.Token;
            state.ResolveCancellation = cancellation;
            state.PendingMaterialization = materializationStamp;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ResolveCollectionAsync(
                    collectionId,
                    usage,
                    cancellation,
                    cancellationToken,
                    materializationStamp).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore replaced resolve requests
            }
            catch (Exception ex)
            {
                HandleResolveFailure(collectionId, usage, cancellation, materializationStamp, ex);
            }
            finally
            {
                DisposeResolveCancellation(collectionId, cancellation);
            }
        });
    }

    private async Task ResolveCollectionAsync(
        string collectionId,
        CollectionUsage usage,
        CancellationTokenSource resolveCancellation,
        CancellationToken cancellationToken,
        CollectionMaterializationStamp materializationStamp)
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

        ObjectCollectionResolveResult resolveResult = await _resolver.ResolveAsync(record, usage.Snapshots, cancellationToken).ConfigureAwait(false);
        if (IsDisposing || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        bool shouldRegisterRuntimeCollection;
        string runtimeCollectionId = string.Empty;
        IReadOnlyList<ObjectPathRedirection> runtimeRedirects = [];
        bool forceRuntimeRefresh = false;
        lock (_stateLock)
        {
            if (IsDisposing
                || !_collections.TryGetValue(collectionId, out CollectionState? state)
                || !CanApplyResolveResultLocked(state, usage, resolveCancellation, materializationStamp))
            {
                return;
            }

            bool keepLastGoodSnapshot = resolveResult.KeepLastGoodSnapshot
                && state.CurrentMaterialization.MatchesUsage(usage.Signature, state.MaterializationGeneration)
                && _resolvedCollectionStore.TryGetCollection(state.Record.CollectionId, out _);

            AcceptMaterializationLocked(state, materializationStamp);
            forceRuntimeRefresh = state.ForceNextRuntimeRefresh;
            state.ForceNextRuntimeRefresh = false;
            state.ResolveState = resolveResult.ResolveState;
            state.StatusText = resolveResult.StatusText;
            state.Warnings = resolveResult.Warnings;
            state.KeepingLastGoodSnapshot = keepLastGoodSnapshot;
            state.RedirectCount = resolveResult.Redirects.Count;
            runtimeCollectionId = state.Record.CollectionId;

            if (resolveResult.ResolveState == ObjectCollectionResolveState.Ready)
            {
                runtimeRedirects = resolveResult.Redirects;
            }

            shouldRegisterRuntimeCollection = !keepLastGoodSnapshot;
        }

        if (!shouldRegisterRuntimeCollection
            && forceRuntimeRefresh
            && _resolvedCollectionStore.TryGetCollection(runtimeCollectionId, out ObjectCollectionResolveData existingRuntimeCollection))
        {
            shouldRegisterRuntimeCollection = true;
            runtimeRedirects = ObjectPathRedirectionUtility.CreateStableList(
                existingRuntimeCollection.Redirects.Select(static pair =>
                    new ObjectPathRedirection(pair.Key, pair.Value)));
        }

        if (shouldRegisterRuntimeCollection)
        {
            _resolvedCollectionStore.RegisterCollection(
                runtimeCollectionId,
                runtimeRedirects,
                forceRefresh: forceRuntimeRefresh,
                resourceViews: resolveResult.ResourceViews);
        }

        _logger.LogInformation(
            "object collection {CollectionId} resolve result {ResolveState} with {RedirectCount} redirects for {UsageCount} objects: {StatusText}",
            collectionId,
            resolveResult.ResolveState,
            resolveResult.Redirects.Count,
            usage.Snapshots.Count,
            resolveResult.StatusText);

        RaiseCollectionsChanged();
    }

    private void HandleResolveFailure(
        string collectionId,
        CollectionUsage usage,
        CancellationTokenSource resolveCancellation,
        CollectionMaterializationStamp materializationStamp,
        Exception ex)
    {
        string runtimeCollectionId = string.Empty;
        lock (_stateLock)
        {
            if (IsDisposing
                || !_collections.TryGetValue(collectionId, out CollectionState? state)
                || !CanApplyResolveResultLocked(state, usage, resolveCancellation, materializationStamp))
            {
                return;
            }

            AcceptMaterializationLocked(state, materializationStamp);
            state.ForceNextRuntimeRefresh = false;
            state.ResolveState = ObjectCollectionResolveState.ResolveFailed;
            state.StatusText = $"object collection resolve failed: {ex.Message}";
            state.Warnings = [];
            state.KeepingLastGoodSnapshot = false;
            state.RedirectCount = 0;
            runtimeCollectionId = state.Record.CollectionId;
        }

        _logger.LogWarning(ex, "object collection {CollectionId} resolve task failed", collectionId);
        _resolvedCollectionStore.RegisterCollection(runtimeCollectionId, [], forceRefresh: true);
        RaiseCollectionsChanged();
    }

    private void DisposeResolveCancellation(string collectionId, CancellationTokenSource resolveCancellation)
    {
        lock (_stateLock)
        {
            if (!IsDisposing
             && _collections.TryGetValue(collectionId, out CollectionState? state)
             && ReferenceEquals(state.ResolveCancellation, resolveCancellation))
            {
                state.ResolveCancellation = null;
            }
        }

        resolveCancellation.Dispose();
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

    private static void AcceptMaterializationLocked(CollectionState state, CollectionMaterializationStamp materializationStamp)
    {
        state.CurrentMaterialization = materializationStamp;
        state.PendingMaterialization = CollectionMaterializationStamp.Empty;
    }

    private static bool HasPendingResolveLocked(CollectionState state, CollectionMaterializationStamp materializationStamp)
        => state.ResolveCancellation is { IsCancellationRequested: false }
            && state.PendingMaterialization == materializationStamp;

    private static bool IsMaterializedLocked(CollectionState state, CollectionMaterializationStamp materializationStamp)
        => state.CurrentMaterialization == materializationStamp;

    private static bool TryNormalizeRecord(ObjectCollection record, out ObjectCollection normalizedRecord)
    {
        string collectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(record.CollectionId);
        string normalizedName = ObjectStringUtility.TrimOrEmpty(record.Name);
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
            if (normalizedGroupName.Length == 0)
            {
                continue;
            }

            if (!CollectionModSettingsUtility.TryNormalizeOptionNames(optionNames, out List<string> normalizedOptions))
            {
                return false;
            }

            settings[normalizedGroupName] = normalizedOptions;
        }

        normalizedEntry = new ObjectCollectionModSettings
        {
            ModDirectory = ObjectStringUtility.TrimOrEmpty(entry.ModDirectory),
            ModName = ObjectStringUtility.TrimOrEmpty(entry.ModName),
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
         || left.Priority != right.Priority
         || left.Settings.Count != right.Settings.Count)
        {
            return false;
        }

        foreach ((string groupName, List<string> optionNames) in left.Settings)
        {
            if (!right.Settings.TryGetValue(groupName, out List<string>? rightOptionNames)
             || optionNames.Count != rightOptionNames.Count)
            {
                return false;
            }

            for (int index = 0; index < optionNames.Count; ++index)
            {
                if (!string.Equals(optionNames[index], rightOptionNames[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
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
            Settings = entry.Settings.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToList(),
                StringComparer.Ordinal),
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
        if (invalidation.AffectsAllCollections || invalidation.AffectedModDirectories.Count == 0)
        {
            return true;
        }

        foreach (ObjectCollectionModSettings entry in record.Entries)
        {
            if (!entry.Enabled)
            {
                continue;
            }

            string normalizedModDirectory = ObjectCollectionKeyUtility.NormalizeModDirectory(entry.ModDirectory);
            if (normalizedModDirectory.Length > 0
             && invalidation.AffectedModDirectories.Contains(normalizedModDirectory))
            {
                return true;
            }
        }

        return false;
    }
}
