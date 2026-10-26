using Intoner.Logging;
using Intoner.Objects.Filesystem.Layouts;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Owns persistent workspace changes and recovery.
/// </summary>
internal interface IObjectManager
{
    /// <summary>
    /// Saves current objects in a new enabled layout and selects it for new objects.
    /// </summary>
    /// <param name="name">The requested layout name.</param>
    /// <param name="expectedPersistentRevision">The persistent scene revision that must still be current, or null for a local mutation.</param>
    /// <returns>The save result and new layout id.</returns>
    PersistentMutationResult SaveCurrentObjectsAsLayout(string name, long? expectedPersistentRevision = null);

    /// <summary>
    /// Creates an empty enabled layout and selects it for new objects.
    /// </summary>
    /// <param name="name">The requested layout name.</param>
    /// <returns>The creation result and new layout id.</returns>
    PersistentMutationResult CreateEmptyLayout(string name);

    /// <summary>
    /// Changes persistent layout membership without changing the other enabled layouts.
    /// </summary>
    /// <remarks>Enabling the first layout also selects it for new objects.</remarks>
    /// <param name="layoutId">The saved layout id.</param>
    /// <param name="enabled">Whether the layout should contribute to the workspace.</param>
    /// <param name="expectedPersistentRevision">The persistent scene revision that must still be current, or null for a local mutation.</param>
    /// <param name="expectedLayoutRevision">The selected layout revision that must still be current, or null for a local mutation.</param>
    /// <returns>The selection and runtime reconciliation result.</returns>
    PersistentMutationResult SetLayoutEnabled(
        Guid layoutId,
        bool enabled,
        long? expectedPersistentRevision = null,
        long? expectedLayoutRevision = null);

    /// <summary>
    /// Changes the destination for new objects without unloading layouts.
    /// </summary>
    /// <param name="layoutId">An enabled layout id, or null for standalone storage.</param>
    /// <param name="expectedPersistentRevision">The expected workspace revision, or null for local edits.</param>
    /// <returns>The configuration update result.</returns>
    PersistentMutationResult SetCreationLayout(Guid? layoutId, long? expectedPersistentRevision = null);

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
    /// Reloads saved layouts and reconciles changes to enabled owners.
    /// </summary>
    void ReloadSavedLayouts();
}

internal sealed class ObjectManager : IObjectManager
{
    private readonly ILogger<ObjectManager>      _logger;
    private readonly Lock                        _stateLock;
    private readonly IObjectLayoutStore          _layoutStore;
    private readonly IObjectLayoutManager        _layoutManager;
    private readonly IObjectPersistenceState     _persistenceState;
    private readonly IObjectIdentityService      _objectIdentityService;
    private readonly IObjectFolderService        _objectFolderService;
    private readonly IObjectHousingModePolicy    _housingModePolicy;
    private readonly IObjectRevisionTracker      _revisionTracker;
    private readonly IObjectScene                _scene;

    public ObjectManager(
        ILogger<ObjectManager> logger,
        ObjectStateLock stateLock,
        IObjectLayoutStore layoutStore,
        IObjectLayoutManager layoutManager,
        IObjectPersistenceState persistenceState,
        IObjectIdentityService objectIdentityService,
        IObjectFolderService objectFolderService,
        IObjectHousingModePolicy housingModePolicy,
        IObjectRevisionTracker revisionTracker,
        IObjectScene scene)
    {
        _logger                = logger;
        _stateLock             = stateLock.Value;
        _layoutStore           = layoutStore;
        _layoutManager         = layoutManager;
        _persistenceState      = persistenceState;
        _objectIdentityService = objectIdentityService;
        _objectFolderService   = objectFolderService;
        _housingModePolicy     = housingModePolicy;
        _revisionTracker       = revisionTracker;
        _scene                 = scene;
    }

    public PersistentMutationResult SaveCurrentObjectsAsLayout(string name, long? expectedPersistentRevision = null)
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

            IReadOnlyList<ObjectSnapshot> persistedSnapshots = _persistenceState.GetPersistedSnapshots();
            IReadOnlyList<SceneGroupSnapshot> persistedGroups = _persistenceState.GetPersistedGroups();
            bool promoteStandalone = _layoutManager.GetEnabledLayouts().Count == 0;
            List<ObjectSnapshot> layoutSnapshots;
            List<SceneGroupSnapshot> layoutGroups;
            if (promoteStandalone)
            {
                layoutSnapshots = persistedSnapshots.ToList();
                layoutGroups = persistedGroups.ToList();
            }
            else if (!ObjectSnapshotUtility.TryCopyWithNewIds(persistedSnapshots, persistedGroups, out layoutSnapshots, out layoutGroups))
            {
                return PersistentMutationResult.Failed(PersistentMutationStatus.InvalidRequest, "current object identities are invalid");
            }

            IReadOnlyList<ObjectSnapshot> workspaceSnapshots = promoteStandalone
                ? layoutSnapshots : [.. persistedSnapshots, .. layoutSnapshots];
            if (!_housingModePolicy.TryValidateLayout(workspaceSnapshots, out _))
            {
                return PersistentMutationResult.Failed(
                    PersistentMutationStatus.InvalidRequest,
                    "current objects are not valid for the active housing mode");
            }

            IReadOnlyList<ObjectFolderSnapshot> layoutFolders = _objectFolderService.GetWorkspaceFolders();
            if (!_layoutManager.TryCreateLayout(
                    name,
                    layoutSnapshots,
                    layoutFolders,
                    out ObjectLayoutSnapshot layout,
                    layoutGroups))
            {
                return PersistentMutationResult.Failed(
                    PersistentMutationStatus.StorageFailed,
                    "layout could not be stored");
            }

            PersistentMutationResult selectionResult = EnableCreatedLayout(layout.Id);
            if (!selectionResult.IsAccepted)
            {
                return selectionResult;
            }

            if (promoteStandalone)
            {
                _persistenceState.ClearStandaloneSnapshots();
            }

            IncrementPersistentSceneRevision();
            committedResult = WithCurrentRevisions(PersistentMutationResult.Success(layout.Id));
        }

        return ReconcileCommittedSceneChange(
            committedResult,
            "layout was saved but runtime reconciliation is pending");
    }

    public PersistentMutationResult CreateEmptyLayout(string name)
    {
        PersistentMutationResult committedResult;
        lock (_stateLock)
        {
            if (!_layoutManager.TryCreateLayout(name, out ObjectLayoutSnapshot layout))
            {
                return PersistentMutationResult.Failed(
                    PersistentMutationStatus.StorageFailed,
                    "layout could not be stored");
            }

            PersistentMutationResult selectionResult = EnableCreatedLayout(layout.Id);
            if (!selectionResult.IsAccepted)
            {
                return selectionResult;
            }

            IncrementPersistentSceneRevision();
            committedResult = WithCurrentRevisions(PersistentMutationResult.Success(layout.Id));
        }

        return ReconcileCommittedSceneChange(
            committedResult,
            "layout was saved but runtime reconciliation is pending");
    }

    private PersistentMutationResult EnableCreatedLayout(Guid layoutId)
    {
        List<Guid> enabledIds = _layoutManager.GetEnabledLayouts().Select(static layout => layout.Id).ToList();
        enabledIds.Add(layoutId);
        if (_layoutManager.TrySetWorkspaceLayouts(enabledIds, layoutId))
        {
            return PersistentMutationResult.Success(layoutId);
        }

        bool removed = _layoutManager.DeleteLayout(layoutId) == PersistentMutationStatus.Success;
        return PersistentMutationResult.Failed(
            removed ? PersistentMutationStatus.StorageFailed : PersistentMutationStatus.RecoveryRequired,
            removed
                ? "layout selection could not be stored"
                : "layout selection failed and the new layout could not be removed");
    }

    public PersistentMutationResult SetLayoutEnabled(
        Guid layoutId,
        bool enabled,
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

            if (!_layoutManager.TryGetLayout(layoutId, out ObjectLayoutSnapshot layout))
            {
                return PersistentMutationResult.Failed(PersistentMutationStatus.NotFound, "layout was not found");
            }

            if (TryGetRevisionFailure(expectedLayoutRevision, layout.Revision, "layout revision", out validationFailure))
            {
                return validationFailure;
            }

            bool wasEnabled = _layoutManager.IsLayoutEnabled(layoutId);

            if (enabled)
            {
                List<ObjectSnapshot> workspaceSnapshots = _persistenceState.GetPersistedSnapshots().ToList();

                if (!wasEnabled)
                {
                    workspaceSnapshots.AddRange(layout.Objects);
                }

                if (!_housingModePolicy.TryValidateLayout(workspaceSnapshots, out string validationMessage))
                {
                    return PersistentMutationResult.Failed(PersistentMutationStatus.InvalidRequest, validationMessage);
                }

                if (!_objectIdentityService.TryValidateSavedLayoutSet(_layoutManager.GetLayouts(), out Guid conflictingId))
                {
                    return PersistentMutationResult.Failed(
                        PersistentMutationStatus.Conflict,
                        $"layout contains object id {conflictingId:D} already owned by another active scene source");
                }
            }

            if (wasEnabled == enabled)
            {
                return WithCurrentRevisions(PersistentMutationResult.Success(layoutId));
            }

            List<Guid> enabledIds = _layoutManager.GetEnabledLayouts().Select(static entry => entry.Id).ToList();
            if (enabled)
            {
                enabledIds.Add(layoutId);
            }
            else
            {
                enabledIds.Remove(layoutId);
            }

            Guid? creationId = _layoutManager.GetCreationLayoutId();
            if (enabled && enabledIds.Count == 1)
            {
                creationId = layoutId;
            }
            else if (!enabled && creationId == layoutId)
            {
                creationId = null;
            }

            if (!_layoutManager.TrySetWorkspaceLayouts(enabledIds, creationId))
            {
                return PersistentMutationResult.Failed(
                    PersistentMutationStatus.StorageFailed,
                    "enabled layouts could not be stored");
            }

            IncrementPersistentSceneRevision();
            committedResult = WithCurrentRevisions(PersistentMutationResult.Success(layoutId));
        }

        return ReconcileCommittedSceneChange(
            committedResult,
            "layout selection was stored but runtime reconciliation is pending");
    }

    public PersistentMutationResult SetCreationLayout(Guid? layoutId, long? expectedPersistentRevision = null)
    {
        lock (_stateLock)
        {
            if (TryGetRevisionFailure(expectedPersistentRevision, _revisionTracker.GetPersistentSceneRevision(),
                    "persistent scene revision", out PersistentMutationResult failure))
            {
                return failure;
            }

            if (layoutId.HasValue && !_layoutManager.IsLayoutEnabled(layoutId.Value))
            {
                return PersistentMutationResult.Failed(PersistentMutationStatus.InvalidRequest, "creation layout must be enabled");
            }

            if (_layoutManager.GetCreationLayoutId() == layoutId)
            {
                return WithCurrentRevisions(PersistentMutationResult.Success(layoutId));
            }

            List<Guid> enabledIds = _layoutManager.GetEnabledLayouts().Select(static layout => layout.Id).ToList();
            if (!_layoutManager.TrySetWorkspaceLayouts(enabledIds, layoutId))
            {
                return PersistentMutationResult.Failed(PersistentMutationStatus.StorageFailed, "creation layout could not be stored");
            }

            IncrementPersistentSceneRevision();
            return WithCurrentRevisions(PersistentMutationResult.Success(layoutId));
        }
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

            bool wasEnabled = _layoutManager.IsLayoutEnabled(layoutId);
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
                        "layout deletion failed and the previous saved state could not be restored")
                    : PersistentMutationResult.Failed(deleteStatus, "layout could not be deleted");
            }

            reloadScene = wasEnabled;
            if (reloadScene)
            {
                IncrementPersistentSceneRevision();
            }

            committedResult = WithCurrentRevisions(PersistentMutationResult.Success(layoutId));
        }

        return reloadScene
            ? ReconcileCommittedSceneChange(
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
            Dictionary<Guid, ObjectLayoutSnapshot> layoutsById = [];
            foreach (ObjectLayoutSnapshot layout in workspace.Layouts)
            {
                if (layout.Id == Guid.Empty || !layoutsById.TryAdd(layout.Id, layout)
                    || !SceneGroupUtility.TryValidateOwner(layout.Id, layout.Objects, layout.Groups, out _))
                {
                    message = "The autosave contains invalid or duplicate layout owners.";
                    return false;
                }
            }

            if (!SceneGroupUtility.TryValidateOwner(null, workspace.StandaloneObjects, workspace.StandaloneGroups, out _)
                || workspace.CreationLayoutId.HasValue && !layoutsById.ContainsKey(workspace.CreationLayoutId.Value))
            {
                message = "The autosave contains invalid or duplicate identities, group membership, or layout ownership.";
                return false;
            }

            IReadOnlyList<SceneGroupSnapshot> sourceGroups = [.. workspace.StandaloneGroups, .. workspace.Layouts.SelectMany(static layout => layout.Groups)];
            if (!ObjectSnapshotUtility.TryCopyWithNewIds(workspace.EnumerateObjects().ToArray(), sourceGroups, out List<ObjectSnapshot> copies, out List<SceneGroupSnapshot> copiedGroups))
            {
                message = "The autosave contains invalid or duplicate object ids.";
                return false;
            }

            ObjectPersistentSceneUpdate recovered = BuildRecoveredWorkspace(workspace, copies, copiedGroups);
            IReadOnlyList<ObjectSnapshot> recoveredObjects = recovered.StandaloneObjects
                .Concat(recovered.Layouts.SelectMany(static layout => layout.Objects))
                .ToList();
            if (!_housingModePolicy.TryValidateLayout(recoveredObjects, out message))
            {
                return false;
            }

            if (!_objectIdentityService.TryValidatePersistentSceneReplacement(recoveredObjects, new HashSet<Guid>(), out _,
                    [.. recovered.StandaloneGroups, .. recovered.Layouts.SelectMany(static layout => layout.Groups)]))
            {
                message = "an object id belongs to another scene source";
                return false;
            }

            List<ObjectLayoutSnapshot> recoveryLayouts = [];
            Dictionary<Guid, Guid> recoveredIds = [];
            foreach (ObjectLayoutContentUpdate content in recovered.Layouts)
            {
                ObjectLayoutSnapshot original = layoutsById[content.LayoutId];
                string name = $"Recovered {TextUtility.TrimOrFallback(original.Name, "layout")}";
                if (!_layoutManager.TryCreateLayout(
                        name,
                        content.Objects,
                        content.Folders,
                        out ObjectLayoutSnapshot recoveryLayout,
                        content.Groups))
                {
                    message = "The recovered layout copy could not be stored.";
                    CleanupRecoveryLayouts(recoveryLayouts, ref message);
                    return false;
                }

                recoveryLayouts.Add(recoveryLayout);
                recoveredIds.Add(original.Id, recoveryLayout.Id);
            }

            recovered = recovered with
            {
                CreationLayoutId = workspace.CreationLayoutId.HasValue
                    ? recoveredIds[workspace.CreationLayoutId.Value] : null,
                Layouts = recoveryLayouts.Select(static layout => new ObjectLayoutContentUpdate(layout.Id, layout.Objects, layout.Folders) { Groups = layout.Groups }).ToList(),
            };

            commitResult = CommitPersistentScene(recovered);
            if (!commitResult.IsAccepted)
            {
                message = commitResult.Message;
                CleanupRecoveryLayouts(recoveryLayouts, ref message);

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
            DiagnosticLog.Write(() => _logger.LogError(ex, "autosaved persistent scene was stored but runtime reconciliation failed"));
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

            HashSet<Guid> currentIds = _layoutManager.GetEnabledLayouts().Select(static layout => layout.Id).ToHashSet();
            HashSet<Guid> updateIds = update.Layouts.Select(static layout => layout.LayoutId).ToHashSet();
            if (updateIds.Count != update.Layouts.Count
                || !updateIds.IsSubsetOf(currentIds)
                || update.CreationLayoutId.HasValue && !updateIds.Contains(update.CreationLayoutId.Value))
            {
                return WithCurrentRevisions(PersistentMutationResult.Failed(
                    PersistentMutationStatus.Conflict,
                    "replacement layout owners must belong to the current workspace"));
            }

            IReadOnlyList<ObjectSnapshot> objects = update.StandaloneObjects
                .Concat(update.Layouts.SelectMany(static layout => layout.Objects))
                .ToList();
            if (!SceneGroupUtility.TryValidateOwner(null, update.StandaloneObjects, update.StandaloneGroups, out _)
                || update.Layouts.Any(layout => !SceneGroupUtility.TryValidateOwner(layout.LayoutId, layout.Objects, layout.Groups, out _)))
                return WithCurrentRevisions(PersistentMutationResult.Failed(PersistentMutationStatus.InvalidRequest,
                    "group membership or layout ownership is invalid"));
            if (!_housingModePolicy.TryValidateLayout(objects, out string validationMessage))
            {
                return WithCurrentRevisions(PersistentMutationResult.Failed(
                    PersistentMutationStatus.InvalidRequest,
                    validationMessage));
            }

            if (!_objectIdentityService.TryValidatePersistentSceneReplacement(objects, updateIds, out _,
                    [.. update.StandaloneGroups, .. update.Layouts.SelectMany(static layout => layout.Groups)]))
            {
                return WithCurrentRevisions(PersistentMutationResult.Failed(
                    PersistentMutationStatus.Conflict,
                    "an object id belongs to another scene source"));
            }

            commitResult = CommitPersistentScene(update);
        }

        if (!commitResult.IsAccepted)
        {
            return commitResult;
        }

        return ReconcileCommittedSceneChange(
            commitResult,
            "persistent state was accepted but runtime reconciliation is pending");
    }

    public void ReloadSavedLayouts()
    {
        ObjectLayoutReloadResult reloadResult;
        lock (_stateLock)
        {
            IReadOnlyList<ObjectLayoutSnapshot> loadedLayouts;
            try
            {
                loadedLayouts = _layoutStore.LoadLayouts();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "saved layout reload could not read all files; keeping the loaded layouts");
                return;
            }

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
            _logger.LogWarning("saved layouts were reloaded but removed layout owners could not be cleared from configuration");
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
            DiagnosticLog.Write(() => _logger.LogError(ex, "saved layouts were reloaded but runtime reconciliation failed"));
            _scene.MarkNeedsRefresh();
        }
    }

    private PersistentMutationResult ReconcileCommittedSceneChange(
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
            DiagnosticLog.Write(() => _logger.LogError(ex, "persistent state was stored but runtime reconciliation failed"));
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
        => _revisionTracker.Increment(persistentChanged: true);

    private PersistentMutationResult CommitPersistentScene(ObjectPersistentSceneUpdate update)
    {
        ObjectPersistentWorkspaceSnapshot previous = _persistenceState.CaptureWorkspace(DateTime.UtcNow);
        bool selectionStored = false;
        try
        {
            List<Guid> nextEnabledIds = update.Layouts.Select(static layout => layout.LayoutId).ToList();
            if (!_layoutManager.TrySetWorkspaceLayouts(nextEnabledIds, update.CreationLayoutId))
            {
                return WithCurrentRevisions(PersistentMutationResult.Failed(
                    PersistentMutationStatus.StorageFailed, "workspace layout selection could not be stored"));
            }

            selectionStored = true;
            _objectFolderService.ReplaceStandaloneState(update.StandaloneFolders);
            _persistenceState.ReplaceStandaloneContent(update.StandaloneObjects, update.StandaloneGroups);

            // publish layout owners last so a failed commit retains the complete previous owner set
            PersistentMutationStatus status = _layoutManager.ReplaceLayoutContents(update.Layouts);
            if (status != PersistentMutationStatus.Success)
            {
                bool restored = RestorePersistentScene(previous, selectionStored);
                return restored && status != PersistentMutationStatus.RecoveryRequired
                    ? WithCurrentRevisions(PersistentMutationResult.Failed(status, "layout content could not be stored"))
                    : CreatePersistentRecoveryFailure("persistent scene storage could not be restored");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(() => _logger.LogError(ex, "failed to replace persistent scene state"));
            bool restored = RestorePersistentScene(previous, selectionStored);
            return restored
                ? WithCurrentRevisions(PersistentMutationResult.Failed(
                    PersistentMutationStatus.StorageFailed,
                    "persistent scene could not be stored"))
                : CreatePersistentRecoveryFailure(
                    "persistent scene storage failed and previous state could not be restored");
        }

        IncrementPersistentSceneRevision();
        return WithCurrentRevisions(PersistentMutationResult.Success());
    }

    private PersistentMutationResult CreatePersistentRecoveryFailure(string message)
    {
        IncrementPersistentSceneRevision();
        return WithCurrentRevisions(PersistentMutationResult.Failed(
            PersistentMutationStatus.RecoveryRequired,
            message));
    }

    private bool RestorePersistentScene(ObjectPersistentWorkspaceSnapshot previous, bool restoreSelection)
    {
        bool restored = true;
        if (restoreSelection)
        {
            restored = TryRestorePersistentSceneStep(
                () => _layoutManager.TrySetWorkspaceLayouts(previous.Layouts.Select(static layout => layout.Id).ToArray(), previous.CreationLayoutId),
                "enabled layouts");
        }

        restored = TryRestorePersistentSceneStep(
            () =>
            {
                _persistenceState.ReplaceStandaloneContent(previous.StandaloneObjects, previous.StandaloneGroups);
                return true;
            },
            "standalone objects") && restored;
        restored = TryRestorePersistentSceneStep(
            () =>
            {
                _objectFolderService.ReplaceStandaloneState(previous.StandaloneFolders);
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

            DiagnosticLog.Write(() => _logger.LogCritical("failed to restore persistent scene {State} after an apply error", state));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(() => _logger.LogCritical(ex, "failed to restore persistent scene {State} after an apply error", state));
        }

        return false;
    }

    private void CleanupRecoveryLayouts(IReadOnlyList<ObjectLayoutSnapshot> layouts, ref string message)
    {
        bool keptActive = false;
        bool cleanupFailed = false;
        foreach (Guid id in layouts.Select(static layout => layout.Id))
        {
            if (_layoutManager.IsLayoutEnabled(id))
            {
                keptActive = true;
            }
            else if (!TryRestorePersistentSceneStep(
                         () => _layoutManager.DeleteLayout(id) == PersistentMutationStatus.Success,
                         "unused recovery layout"))
            {
                cleanupFailed = true;
            }
        }

        if (keptActive)
        {
            message += " Recovered layouts were kept because the previous selection could not be restored.";
        }

        if (cleanupFailed)
        {
            message += " Unused recovered layouts could not be removed.";
        }
    }

    private static ObjectPersistentSceneUpdate BuildRecoveredWorkspace(
        ObjectPersistentWorkspaceSnapshot workspace,
        IReadOnlyList<ObjectSnapshot> copies,
        IReadOnlyList<SceneGroupSnapshot> groupCopies)
    {
        ILookup<Guid?, ObjectSnapshot> objectsByOwner = copies.ToLookup(static snapshot => snapshot.LayoutId);
        ILookup<Guid?, SceneGroupSnapshot> groupsByOwner = groupCopies.ToLookup(static group => group.LayoutId);
        List<ObjectSnapshot> standaloneObjects = objectsByOwner[null].ToList();
        SceneGroupSnapshot[] standaloneGroups = groupsByOwner[null].ToArray();
        IReadOnlyList<ObjectFolderSnapshot> standaloneFolders = ObjectFolderUtility.ExpandFolderEntries(workspace.StandaloneFolders, standaloneObjects, standaloneGroups);
        List<ObjectLayoutContentUpdate> layouts = [];
        foreach (ObjectLayoutSnapshot layout in workspace.Layouts)
        {
            List<ObjectSnapshot> objects = objectsByOwner[layout.Id].ToList();
            SceneGroupSnapshot[] groups = groupsByOwner[layout.Id].ToArray();
            layouts.Add(new ObjectLayoutContentUpdate(layout.Id, objects, layout.Folders) { Groups = groups });
        }

        return new ObjectPersistentSceneUpdate
        {
            StandaloneObjects = standaloneObjects,
            StandaloneGroups = standaloneGroups,
            StandaloneFolders = standaloneFolders,
            CreationLayoutId = workspace.CreationLayoutId,
            Layouts = layouts,
        };
    }
}
