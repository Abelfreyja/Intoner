using Dalamud.Plugin.Services;
using Intoner.Objects.Filesystem.Layouts;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Utils;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Owns persisted layout changes, global object clearing, and persistent scene change notifications.
/// </summary>
internal interface IObjectManager
{
    /// <summary>
    /// Saves the current persisted local objects, optionally checking that the caller revision is still current.
    /// </summary>
    /// <param name="name">The requested layout name.</param>
    /// <param name="expectedPersistentRevision">The persistent scene revision that must still be current, or null for a local mutation.</param>
    /// <returns>The save result and new layout id.</returns>
    PersistentMutationResult SaveCurrentObjectsAsLayout(string name, long? expectedPersistentRevision = null);

    /// <summary>
    /// Creates a new empty saved layout.
    /// </summary>
    /// <param name="name">The requested layout name.</param>
    /// <returns>The creation result and new layout id.</returns>
    PersistentMutationResult CreateEmptyLayout(string name);

    /// <summary>
    /// Selects the current default local layout, optionally checking caller revisions.
    /// </summary>
    /// <param name="layoutId">The layout id to select, or null to clear the default layout.</param>
    /// <param name="expectedPersistentRevision">The persistent scene revision that must still be current, or null for a local mutation.</param>
    /// <param name="expectedLayoutRevision">The selected layout revision that must still be current, or null for a local mutation.</param>
    /// <returns>The selection and runtime reconciliation result.</returns>
    PersistentMutationResult SelectLayout(
        Guid? layoutId,
        long? expectedPersistentRevision = null,
        long? expectedLayoutRevision = null);

    /// <summary>
    /// Deletes one saved layout, optionally checking caller revisions.
    /// </summary>
    /// <param name="layoutId">The layout id to delete.</param>
    /// <param name="expectedLayoutRevision">The target layout revision that must still be current, or null for a local mutation.</param>
    /// <param name="expectedPersistentRevision">The persistent scene revision that must still be current, or null for a local mutation.</param>
    /// <returns>The deletion and runtime reconciliation result.</returns>
    PersistentMutationResult DeleteLayout(
        Guid layoutId,
        long? expectedLayoutRevision = null,
        long? expectedPersistentRevision = null);

    /// <summary>
    /// restores independent copies of an autosaved workspace without changing existing saved layouts
    /// </summary>
    /// <param name="workspace">the recovered workspace snapshot</param>
    /// <param name="message">the recovery result message</param>
    /// <returns>true when recovery was applied</returns>
    bool TryRecoverWorkspace(ObjectPersistentWorkspaceSnapshot workspace, out string message);

    /// <summary>
    /// Replaces the persistent scene when the caller revision is still current.
    /// </summary>
    /// <param name="update">The complete persistent scene replacement.</param>
    /// <returns>The persistent scene apply result.</returns>
    PersistentMutationResult TryApplyPersistentScene(ObjectPersistentSceneUpdate update);

    /// <summary>
    /// Clears all object state, layouts, and runtime entries.
    /// </summary>
    void ClearAll();

}

internal sealed class ObjectManager : IObjectManager, IDisposable
{
    private readonly ILogger<ObjectManager> _logger;
    private readonly Lock _stateLock;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly ISceneLocationService _locationService;
    private readonly IObjectLayoutStore _layoutStore;
    private readonly IObjectLayoutManager _layoutManager;
    private readonly IObjectPersistenceState _persistenceState;
    private readonly IObjectIdentityService _objectIdentityService;
    private readonly IObjectFolderService _objectFolderService;
    private readonly IObjectHousingModePolicy _housingModePolicy;
    private readonly IObjectRevisionTracker _revisionTracker;
    private readonly IObjectSceneMutationService _sceneMutations;
    private readonly IObjectScene _scene;
    private readonly IObjectResolvedCollectionStore _collectionStore;
    private readonly IObjectLayoutAutoSaveService _layoutAutoSaveService;

    private int _pendingSavedLayoutReload;
    private bool _isTransitioning;
    private bool _disposed;

    public ObjectManager(
        ILogger<ObjectManager> logger,
        ObjectStateLock stateLock,
        IFramework framework,
        IClientState clientState,
        ISceneLocationService locationService,
        IObjectLayoutStore layoutStore,
        IObjectLayoutManager layoutManager,
        IObjectPersistenceState persistenceState,
        IObjectIdentityService objectIdentityService,
        IObjectFolderService objectFolderService,
        IObjectHousingModePolicy housingModePolicy,
        IObjectRevisionTracker revisionTracker,
        IObjectSceneMutationService sceneMutations,
        IObjectScene scene,
        IObjectResolvedCollectionStore collectionStore,
        IObjectLayoutAutoSaveService layoutAutoSaveService)
    {
        _logger = logger;
        _stateLock = stateLock.Value;
        _framework = framework;
        _clientState = clientState;
        _locationService = locationService;
        _layoutStore = layoutStore;
        _layoutManager = layoutManager;
        _persistenceState = persistenceState;
        _objectIdentityService = objectIdentityService;
        _objectFolderService = objectFolderService;
        _housingModePolicy = housingModePolicy;
        _revisionTracker = revisionTracker;
        _sceneMutations = sceneMutations;
        _scene = scene;
        _collectionStore = collectionStore;
        _layoutAutoSaveService = layoutAutoSaveService;

        _isTransitioning = _locationService.IsTransitioning;

        _framework.Update += HandleFrameworkUpdate;
        _clientState.Logout += HandleClientLogout;
        _locationService.TransitionStarted += HandleLocationTransitionStarted;
        _locationService.LocationInvalidated += HandleLocationInvalidated;
        _layoutStore.LayoutFilesChanged += HandleSavedLayoutFilesChanged;
        _collectionStore.CollectionChanged += HandleResolvedCollectionChanged;

        if (_isTransitioning)
        {
            HandleZoneSwitchStart();
        }
    }

    public PersistentMutationResult SaveCurrentObjectsAsLayout(string name, long? expectedPersistentRevision = null)
    {
        PersistentMutationResult committedResult;
        bool reloadScene = false;
        lock (_stateLock)
        {
            if (TryGetRevisionFailure(
                    expectedPersistentRevision,
                    _revisionTracker.GetPersistentSceneRevision(),
                    "persistent scene revision",
                    out PersistentMutationResult validationFailure))
            {
                return validationFailure;
            }

            IReadOnlyList<ObjectSnapshot> persistedSnapshots = _persistenceState.GetPersistedSnapshots();
            bool shouldPromoteStandaloneObjects = !_layoutManager.GetDefaultLayoutId().HasValue
                && persistedSnapshots.Count > 0;
            List<ObjectSnapshot> layoutSnapshots = persistedSnapshots
                .Select(snapshot => snapshot with
                {
                    Id = shouldPromoteStandaloneObjects ? snapshot.Id : Guid.NewGuid(),
                })
                .ToList();
            if (!_housingModePolicy.TryValidateLayout(layoutSnapshots, out _))
            {
                return PersistentMutationResult.Failed(
                    PersistentMutationStatus.InvalidRequest,
                    "current objects are not valid for the active housing mode");
            }

            IReadOnlyList<ObjectFolderSnapshot> layoutFolders = _objectFolderService.BuildLayoutExport(layoutSnapshots);
            if (!_layoutManager.TryCreateLayout(
                    name,
                    layoutSnapshots,
                    layoutFolders,
                    out ObjectLayoutSnapshot layout))
            {
                return PersistentMutationResult.Failed(
                    PersistentMutationStatus.StorageFailed,
                    "layout could not be stored");
            }

            if (shouldPromoteStandaloneObjects)
            {
                if (!_layoutManager.TrySetDefaultLayout(layout.Id))
                {
                    PersistentMutationStatus cleanupStatus = _layoutManager.DeleteLayout(layout.Id);
                    bool removed = cleanupStatus == PersistentMutationStatus.Success;
                    return PersistentMutationResult.Failed(
                        removed ? PersistentMutationStatus.StorageFailed : PersistentMutationStatus.RecoveryRequired,
                        removed
                            ? "layout selection could not be stored"
                            : "layout selection failed and the new layout could not be removed");
                }

                _persistenceState.ClearStandaloneSnapshots();
                IncrementPersistentSceneRevision();
                reloadScene = true;
            }

            committedResult = WithCurrentRevisions(PersistentMutationResult.Success(layout.Id));
        }

        return reloadScene
            ? ReconcileCommittedLayoutChange(
                committedResult,
                "layout was saved but runtime reconciliation is pending")
            : committedResult;
    }

    public PersistentMutationResult CreateEmptyLayout(string name)
    {
        lock (_stateLock)
        {
            return _layoutManager.TryCreateLayout(name, out ObjectLayoutSnapshot layout)
                ? WithCurrentRevisions(PersistentMutationResult.Success(layout.Id))
                : PersistentMutationResult.Failed(
                    PersistentMutationStatus.StorageFailed,
                    "layout could not be stored");
        }
    }

    public PersistentMutationResult SelectLayout(
        Guid? layoutId,
        long? expectedPersistentRevision = null,
        long? expectedLayoutRevision = null)
    {
        PersistentMutationResult committedResult;
        lock (_stateLock)
        {
            if (TryGetRevisionFailure(
                    expectedPersistentRevision,
                    _revisionTracker.GetPersistentSceneRevision(),
                    "persistent scene revision",
                    out PersistentMutationResult validationFailure))
            {
                return validationFailure;
            }

            if (layoutId.HasValue)
            {
                if (!_layoutManager.TryGetLayout(layoutId.Value, out ObjectLayoutSnapshot layout))
                {
                    return PersistentMutationResult.Failed(PersistentMutationStatus.NotFound, "layout was not found");
                }

                if (TryGetRevisionFailure(
                        expectedLayoutRevision,
                        layout.Revision,
                        "layout revision",
                        out validationFailure))
                {
                    return validationFailure;
                }

                if (!_housingModePolicy.TryValidateLayout(layout.Objects, out string validationMessage))
                {
                    return PersistentMutationResult.Failed(PersistentMutationStatus.InvalidRequest, validationMessage);
                }

                if (!_objectIdentityService.TryValidatePersistentSceneSelection(layout.Objects, out Guid conflictingId))
                {
                    return PersistentMutationResult.Failed(
                        PersistentMutationStatus.Conflict,
                        $"layout contains object id {conflictingId:D} already owned by another active scene source");
                }
            }

            Guid? previousLayoutId = _layoutManager.GetDefaultLayoutId();
            if (previousLayoutId == layoutId)
            {
                return WithCurrentRevisions(PersistentMutationResult.Success(layoutId));
            }

            if (!_layoutManager.TrySetDefaultLayout(layoutId))
            {
                return PersistentMutationResult.Failed(
                    PersistentMutationStatus.StorageFailed,
                    "default layout selection could not be stored");
            }

            IncrementPersistentSceneRevision();
            committedResult = WithCurrentRevisions(PersistentMutationResult.Success(layoutId));
        }

        return ReconcileCommittedLayoutChange(
            committedResult,
            "layout selection was stored but runtime reconciliation is pending");
    }

    public PersistentMutationResult DeleteLayout(
        Guid layoutId,
        long? expectedLayoutRevision = null,
        long? expectedPersistentRevision = null)
    {
        PersistentMutationResult committedResult;
        bool reloadScene;
        lock (_stateLock)
        {
            if (TryGetRevisionFailure(
                    expectedPersistentRevision,
                    _revisionTracker.GetPersistentSceneRevision(),
                    "persistent scene revision",
                    out PersistentMutationResult validationFailure))
            {
                return validationFailure;
            }

            Guid? defaultLayoutId = _layoutManager.GetDefaultLayoutId();
            if (!_layoutManager.TryGetLayout(layoutId, out ObjectLayoutSnapshot layout))
            {
                return PersistentMutationResult.Failed(PersistentMutationStatus.NotFound, "layout was not found");
            }

            if (TryGetRevisionFailure(
                    expectedLayoutRevision,
                    layout.Revision,
                    "layout revision",
                    out validationFailure))
            {
                return validationFailure;
            }

            PersistentMutationStatus deleteStatus = _layoutManager.DeleteLayout(layoutId);
            if (deleteStatus != PersistentMutationStatus.Success)
            {
                return deleteStatus == PersistentMutationStatus.RecoveryRequired
                    ? CreatePersistentRecoveryFailure(
                        "layout deletion failed and the previous default selection could not be restored")
                    : PersistentMutationResult.Failed(deleteStatus, "layout could not be deleted");
            }

            reloadScene = defaultLayoutId == layoutId;
            if (reloadScene)
            {
                IncrementPersistentSceneRevision();
            }

            committedResult = WithCurrentRevisions(PersistentMutationResult.Success(layoutId));
        }

        return reloadScene
            ? ReconcileCommittedLayoutChange(
                committedResult,
                "layout was deleted but runtime reconciliation is pending")
            : committedResult;
    }

    public bool TryRecoverWorkspace(ObjectPersistentWorkspaceSnapshot workspace, out string message)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        PersistentMutationResult commitResult;
        lock (_stateLock)
        {
            if (!ObjectSnapshotUtility.TryCopyWithNewIds(workspace.Objects, out List<ObjectSnapshot> copies))
            {
                message = "The autosave contains invalid or duplicate object ids.";
                return false;
            }

            ObjectPersistentSceneUpdate recovered = BuildRecoveredWorkspace(workspace, copies);
            IReadOnlyList<ObjectSnapshot> recoveredObjects = recovered.StandaloneObjects
                .Concat(recovered.DefaultLayoutObjects)
                .ToList();
            if (!_housingModePolicy.TryValidateLayout(recoveredObjects, out message))
            {
                return false;
            }

            if (!_objectIdentityService.TryValidatePersistentSceneReplacement(recoveredObjects, null, out _))
            {
                message = "an object id belongs to another scene source";
                return false;
            }

            ObjectLayoutSnapshot? recoveryLayout = null;
            if (recovered.DefaultLayoutId.HasValue)
            {
                string name = $"Recovered {TextUtility.TrimOrFallback(workspace.Name, "object workspace")}";
                if (!_layoutManager.TryCreateLayout(
                        name,
                        recovered.DefaultLayoutObjects,
                        recovered.DefaultLayoutFolders,
                        out recoveryLayout))
                {
                    message = "The recovered layout copy could not be stored.";
                    return false;
                }

                recovered = recovered with
                {
                    DefaultLayoutId = recoveryLayout.Id,
                    DefaultLayoutObjects = recoveryLayout.Objects,
                    DefaultLayoutFolders = recoveryLayout.Folders,
                };
            }

            commitResult = CommitPersistentScene(recovered);
            if (!commitResult.IsAccepted)
            {
                message = commitResult.Message;
                if (recoveryLayout is not null)
                {
                    if (_layoutManager.GetDefaultLayoutId() == recoveryLayout.Id)
                    {
                        message += " The recovered layout copy was kept because the previous selection could not be restored.";
                    }
                    else if (!TryRestorePersistentSceneStep(
                                 () => _layoutManager.DeleteLayout(recoveryLayout.Id) == PersistentMutationStatus.Success,
                                 "unused recovery layout"))
                    {
                        message += " The unused recovered layout copy could not be removed.";
                    }
                }

                if (commitResult.Status == PersistentMutationStatus.RecoveryRequired)
                {
                    _scene.MarkNeedsRefresh();
                }

                return false;
            }
        }

        try
        {
            SceneReloadResult reloadResult = _scene.ReloadForCurrentLocation();
            message = reloadResult switch
            {
                { CanApply: false } => "Recovered the autosave. Objects will load when the current location can display them.",
                { NeedsRetry: true } => "Recovered the autosave. Some objects need another scene refresh to finish loading.",
                _ => "Recovered the last autosaved object workspace.",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "autosaved persistent scene was stored but runtime reconciliation failed");
            _scene.MarkNeedsRefresh();
            message = "Recovered the autosave. Objects will load after the scene refreshes.";
        }

        return true;
    }

    public PersistentMutationResult TryApplyPersistentScene(ObjectPersistentSceneUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        PersistentMutationResult commitResult;

        lock (_stateLock)
        {
            long persistentRevision = _revisionTracker.GetPersistentSceneRevision();
            if (update.ExpectedRevision <= 0)
            {
                return WithCurrentRevisions(PersistentMutationResult.Failed(
                    PersistentMutationStatus.InvalidRequest,
                    "expected revision must be positive"));
            }

            if (update.ExpectedRevision != persistentRevision)
            {
                return WithCurrentRevisions(PersistentMutationResult.Failed(
                    PersistentMutationStatus.Conflict,
                    "persistent scene revision has changed"));
            }

            Guid? previousDefaultLayoutId = _layoutManager.GetDefaultLayoutId();
            if (update.DefaultLayoutId.HasValue && update.DefaultLayoutId != previousDefaultLayoutId)
            {
                return WithCurrentRevisions(PersistentMutationResult.Failed(
                    PersistentMutationStatus.Conflict,
                    "default layout must match the current persistent scene"));
            }

            if (update.DefaultLayoutId.HasValue
                && !_layoutManager.TryGetLayout(update.DefaultLayoutId.Value, out _))
            {
                return WithCurrentRevisions(PersistentMutationResult.Failed(
                    PersistentMutationStatus.NotFound,
                    "default layout was not found"));
            }

            IReadOnlyList<ObjectSnapshot> objects = update.StandaloneObjects
                .Concat(update.DefaultLayoutObjects)
                .ToList();
            if (!_housingModePolicy.TryValidateLayout(objects, out string validationMessage))
            {
                return WithCurrentRevisions(PersistentMutationResult.Failed(
                    PersistentMutationStatus.InvalidRequest,
                    validationMessage));
            }

            if (!_objectIdentityService.TryValidatePersistentSceneReplacement(objects, update.DefaultLayoutId, out _))
            {
                return WithCurrentRevisions(PersistentMutationResult.Failed(
                    PersistentMutationStatus.Conflict,
                    "an object id belongs to another scene source"));
            }

            commitResult = CommitPersistentScene(update);
        }

        try
        {
            SceneReloadResult reloadResult = _scene.ReloadForCurrentLocation();
            return reloadResult.IsApplied
                ? commitResult
                : commitResult with
                {
                    Status = PersistentMutationStatus.RuntimeApplyFailed,
                    Message = "persistent state was accepted but runtime reconciliation is pending",
                };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "persistent scene was stored but runtime reconciliation failed");
            _scene.MarkNeedsRefresh();
            return commitResult with
            {
                Status = PersistentMutationStatus.RuntimeApplyFailed,
                Message = "persistent state was accepted but runtime reconciliation failed",
            };
        }
    }

    public void ClearAll()
        => ClearAll(persistLayoutChanges: true);

    private void ClearAll(bool persistLayoutChanges)
    {
        lock (_stateLock)
        {
            bool hadPersistentSceneState = _persistenceState.HasPersistentSceneState();
            PersistentMutationStatus clearStatus = _layoutManager.ClearAllLayoutObjects(persistLayoutChanges);
            if (clearStatus != PersistentMutationStatus.Success)
            {
                _logger.LogError(
                    "could not clear saved layout state ({Status}); active object state was left unchanged",
                    clearStatus);
                return;
            }

            _persistenceState.ClearStandaloneSnapshots();
            IncrementSceneRevision(persistentChanged: hadPersistentSceneState);
        }

        _sceneMutations.ClearAllActiveEntries(removePersistedState: false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _framework.Update -= HandleFrameworkUpdate;
        _clientState.Logout -= HandleClientLogout;
        _locationService.TransitionStarted -= HandleLocationTransitionStarted;
        _locationService.LocationInvalidated -= HandleLocationInvalidated;
        _layoutStore.LayoutFilesChanged -= HandleSavedLayoutFilesChanged;
        _collectionStore.CollectionChanged -= HandleResolvedCollectionChanged;

        if (FrameworkUnloadUtility.IsGameFrameworkDestroying())
        {
            _logger.LogWarning("framework is unloading, skipping object manager clear during dispose");
            return;
        }

        ClearAll(persistLayoutChanges: false);
    }

    private PersistentMutationResult ReconcileCommittedLayoutChange(
        PersistentMutationResult committedResult,
        string pendingMessage)
    {
        try
        {
            SceneReloadResult reloadResult = _scene.ReloadForCurrentLocation();
            if (reloadResult.IsApplied)
            {
                return committedResult;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "layout state was stored but runtime reconciliation failed");
        }

        _scene.MarkNeedsRefresh();
        return committedResult with
        {
            Status = PersistentMutationStatus.RuntimeApplyFailed,
            Message = pendingMessage,
        };
    }

    private PersistentMutationResult WithCurrentRevisions(PersistentMutationResult result)
    {
        ObjectRevisionSnapshot revisions = _revisionTracker.GetSnapshot();
        return result.WithRevisions(
            revisions.SceneRevision,
            revisions.PersistentSceneRevision,
            revisions.SavedLayoutsRevision);
    }

    private static bool TryGetRevisionFailure(
        long? expectedRevision,
        long currentRevision,
        string revisionName,
        out PersistentMutationResult failure)
    {
        if (!expectedRevision.HasValue)
        {
            failure = default;
            return false;
        }

        if (expectedRevision.Value <= 0)
        {
            failure = PersistentMutationResult.Failed(
                PersistentMutationStatus.InvalidRequest,
                $"expected {revisionName} must be positive");
            return true;
        }

        if (expectedRevision.Value != currentRevision)
        {
            failure = PersistentMutationResult.Failed(
                PersistentMutationStatus.Conflict,
                $"{revisionName} has changed");
            return true;
        }

        failure = default;
        return false;
    }

    private void IncrementPersistentSceneRevision()
        => IncrementSceneRevision(persistentChanged: true);

    private void IncrementSceneRevision(bool persistentChanged = false)
        => _revisionTracker.Increment(persistentChanged);

    private PersistentMutationResult CommitPersistentScene(ObjectPersistentSceneUpdate update)
    {
        Guid? previousDefaultLayoutId = _layoutManager.GetDefaultLayoutId();
        ObjectLayoutSnapshot? previousTargetLayout = null;
        if (update.DefaultLayoutId.HasValue
            && !_layoutManager.TryGetLayout(update.DefaultLayoutId.Value, out previousTargetLayout!))
        {
            return WithCurrentRevisions(PersistentMutationResult.Failed(
                PersistentMutationStatus.NotFound,
                "default layout was not found"));
        }

        IReadOnlyList<ObjectSnapshot> previousStandaloneObjects = _persistenceState.GetStandaloneSnapshots();
        ObjectFolderSceneState previousFolderState = _objectFolderService.CaptureSceneState();
        bool layoutUpdateStarted = false;
        bool defaultLayoutChanged = false;
        try
        {
            if (update.DefaultLayoutId.HasValue)
            {
                layoutUpdateStarted = true;
                bool layoutReplaced = _layoutManager.TryReplaceLayoutContent(
                    update.DefaultLayoutId.Value,
                    update.DefaultLayoutObjects,
                    update.DefaultLayoutFolders);
                if (!layoutReplaced)
                {
                    return WithCurrentRevisions(PersistentMutationResult.Failed(
                        PersistentMutationStatus.StorageFailed,
                        "default layout could not be stored"));
                }
            }

            defaultLayoutChanged = previousDefaultLayoutId != update.DefaultLayoutId;
            if (defaultLayoutChanged && !_layoutManager.TrySetDefaultLayout(update.DefaultLayoutId))
            {
                bool restored = RestorePersistentScene(
                    previousDefaultLayoutId,
                    previousTargetLayout,
                    previousStandaloneObjects,
                    previousFolderState,
                    layoutUpdateStarted);
                return restored
                    ? WithCurrentRevisions(PersistentMutationResult.Failed(
                        PersistentMutationStatus.StorageFailed,
                        "default layout selection could not be stored"))
                    : CreatePersistentRecoveryFailure("persistent scene could not be restored");
            }

            _persistenceState.ReplaceStandaloneSnapshots(update.StandaloneObjects);
            _objectFolderService.ReplaceStandaloneState(update.StandaloneFolders);
            IncrementPersistentSceneRevision();
            return WithCurrentRevisions(PersistentMutationResult.Success());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to replace persistent scene state");
            bool restored = RestorePersistentScene(
                previousDefaultLayoutId,
                previousTargetLayout,
                previousStandaloneObjects,
                previousFolderState,
                layoutUpdateStarted,
                defaultLayoutChanged);
            return restored
                ? WithCurrentRevisions(PersistentMutationResult.Failed(
                    PersistentMutationStatus.StorageFailed,
                    "persistent scene could not be stored"))
                : CreatePersistentRecoveryFailure(
                    "persistent scene storage failed and previous state could not be restored");
        }
    }

    private PersistentMutationResult CreatePersistentRecoveryFailure(string message)
    {
        IncrementPersistentSceneRevision();
        return WithCurrentRevisions(PersistentMutationResult.Failed(
            PersistentMutationStatus.RecoveryRequired,
            message));
    }

    private bool RestorePersistentScene(
        Guid? previousDefaultLayoutId,
        ObjectLayoutSnapshot? previousTargetLayout,
        IReadOnlyList<ObjectSnapshot> previousStandaloneObjects,
        ObjectFolderSceneState previousFolderState,
        bool restoreTargetLayout,
        bool defaultLayoutChanged = false)
    {
        bool restored = true;
        if (defaultLayoutChanged)
        {
            restored = TryRestorePersistentSceneStep(
                () => _layoutManager.TrySetDefaultLayout(previousDefaultLayoutId),
                "default layout");
        }

        if (restoreTargetLayout && previousTargetLayout is not null)
        {
            restored = TryRestorePersistentSceneStep(
                () => _layoutManager.TryRestoreLayout(previousTargetLayout),
                "layout content") && restored;
        }

        restored = TryRestorePersistentSceneStep(
            () =>
            {
                _persistenceState.ReplaceStandaloneSnapshots(previousStandaloneObjects);
                return true;
            },
            "standalone objects") && restored;
        restored = TryRestorePersistentSceneStep(
            () =>
            {
                _objectFolderService.ReplaceStandaloneState(previousFolderState.StandaloneFolders);
                return true;
            },
            "standalone folders") && restored;
        return restored;
    }

    private bool TryRestorePersistentSceneStep(Func<bool> restore, string state)
    {
        try
        {
            if (restore())
            {
                return true;
            }

            _logger.LogCritical("failed to restore persistent scene {State} after an apply error", state);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "failed to restore persistent scene {State} after an apply error", state);
        }

        return false;
    }

    private static ObjectPersistentSceneUpdate BuildRecoveredWorkspace(
        ObjectPersistentWorkspaceSnapshot workspace,
        IReadOnlyList<ObjectSnapshot> copies)
    {
        List<ObjectSnapshot> defaultLayoutObjects = [];
        List<ObjectSnapshot> standaloneObjects = [];
        foreach (ObjectSnapshot snapshot in copies)
        {
            if (workspace.DefaultLayoutId.HasValue && snapshot.LayoutId == workspace.DefaultLayoutId)
            {
                defaultLayoutObjects.Add(snapshot);
            }
            else
            {
                standaloneObjects.Add(snapshot with { LayoutId = null });
            }
        }

        IReadOnlyList<ObjectFolderSnapshot> standaloneFolders = ResolveRecoveredFolders(workspace.StandaloneFolders, standaloneObjects);
        IReadOnlyList<ObjectFolderSnapshot> defaultLayoutFolders = workspace.DefaultLayoutId.HasValue
            ? ResolveRecoveredFolders(workspace.DefaultLayoutFolders, defaultLayoutObjects)
            : [];

        return new ObjectPersistentSceneUpdate
        {
            StandaloneObjects = standaloneObjects,
            StandaloneFolders = standaloneFolders,
            DefaultLayoutId = workspace.DefaultLayoutId,
            DefaultLayoutObjects = defaultLayoutObjects,
            DefaultLayoutFolders = defaultLayoutFolders,
        };
    }

    private static IReadOnlyList<ObjectFolderSnapshot> ResolveRecoveredFolders(
        IEnumerable<ObjectFolderSnapshot> folders,
        IReadOnlyList<ObjectSnapshot> objects)
        => ObjectFolderUtility.ExpandFolderEntries(
            folders.Concat(objects.Select(static snapshot => new ObjectFolderSnapshot(snapshot.FolderPath))));

    private void HandleFrameworkUpdate(IFramework framework)
    {
        _ = framework;
        ProcessPendingSavedLayoutReload();
        _layoutAutoSaveService.FrameworkUpdate();
        _scene.FrameworkUpdate();
    }

    private void HandleSavedLayoutFilesChanged()
        => Interlocked.Exchange(ref _pendingSavedLayoutReload, 1);

    private void ProcessPendingSavedLayoutReload()
    {
        if (Interlocked.Exchange(ref _pendingSavedLayoutReload, 0) == 0)
        {
            return;
        }

        ObjectLayoutReloadResult reloadResult;
        lock (_stateLock)
        {
            IReadOnlyList<ObjectLayoutSnapshot> loadedLayouts = _layoutStore.LoadLayouts();
            if (!_objectIdentityService.TryValidateSavedLayoutSet(loadedLayouts, out Guid conflictingId))
            {
                _logger.LogWarning(
                    "saved layout reload rejected conflicting object id {ObjectId}",
                    conflictingId);
                return;
            }

            reloadResult = _layoutManager.ApplySavedLayoutReload(loadedLayouts);
        }

        if (!reloadResult.IsApplied)
        {
            _logger.LogWarning(
                "saved layout reload rejected conflicting identity {Identity}",
                reloadResult.ConflictingId);
            return;
        }

        if (reloadResult.Status == ObjectLayoutReloadStatus.ConfigurationWriteFailed)
        {
            _logger.LogWarning("saved layouts were reloaded but the removed default layout could not be cleared from configuration");
        }

        if (!reloadResult.ActiveLayoutChanged)
        {
            return;
        }

        try
        {
            _ = _scene.ReloadForCurrentLocation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "saved layouts were reloaded but runtime reconciliation failed");
            _scene.MarkNeedsRefresh();
        }
    }

    private void HandleLocationTransitionStarted()
    {
        if (_isTransitioning)
        {
            return;
        }

        _isTransitioning = true;
        HandleZoneSwitchStart();
    }

    private void HandleLocationInvalidated()
    {
        if (!_isTransitioning)
        {
            return;
        }

        _isTransitioning = false;
        HandleZoneSwitchEnd();
    }

    private void HandleClientLogout(int type, int code)
    {
        _ = type;
        _ = code;
        FrameworkThreadUtility.Run(_framework, HandleLogout);
    }

    private void HandleZoneSwitchStart()
    {
        _scene.HandleZoneSwitchStart();
        IncrementSceneRevision();
    }

    private void HandleZoneSwitchEnd()
    {
        _scene.HandleZoneSwitchEnd();
        IncrementSceneRevision();
    }

    private void HandleLogout()
    {
        _scene.HandleLogout();
        IncrementSceneRevision();
    }

    private void HandleResolvedCollectionChanged(ObjectResolvedCollectionChangedInfo _)
    {
        _scene.MarkNeedsRefresh();
        IncrementSceneRevision();
    }
}
