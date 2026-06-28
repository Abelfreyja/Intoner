using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Intoner.Objects.Filesystem.Layouts;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Owns persisted layout changes, global object clearing, and persistent scene change notifications.
/// </summary>
internal interface IObjectManager
{
    /// <summary>
    /// Saves the current persisted local objects into a new layout.
    /// </summary>
    /// <param name="name">The requested layout name.</param>
    /// <param name="layoutId">The new layout id when saving succeeded.</param>
    /// <returns>true when the layout was created and populated.</returns>
    bool TrySaveCurrentObjectsAsLayout(string name, out Guid layoutId);

    /// <summary>
    /// Creates a new empty saved layout.
    /// </summary>
    /// <param name="name">The requested layout name.</param>
    /// <returns>The created layout id.</returns>
    Guid CreateEmptyLayout(string name);

    /// <summary>
    /// Selects the current default local layout.
    /// </summary>
    /// <param name="layoutId">The layout id to select, or null to clear the default layout.</param>
    /// <returns>true when the layout selection was valid.</returns>
    bool TrySelectLayout(Guid? layoutId);

    /// <summary>
    /// Deletes one saved layout.
    /// </summary>
    /// <param name="layoutId">The layout id to delete.</param>
    /// <returns>true when the layout existed and was deleted.</returns>
    bool TryDeleteLayout(Guid layoutId);

    /// <summary>
    /// Replaces the current persisted workspace with an autosaved workspace snapshot.
    /// </summary>
    /// <param name="workspace">the recovered workspace snapshot</param>
    /// <param name="message">the recovery result message</param>
    /// <returns>true when recovery was applied</returns>
    bool TryRecoverWorkspace(ObjectPersistentWorkspaceSnapshot workspace, out string message);

    /// <summary>
    /// Clears all object state, layouts, and runtime entries.
    /// </summary>
    void ClearAll();

    /// <summary>
    /// Raised when the persistent local object scene changes.
    /// </summary>
    event Action PersistentSceneChanged;
}

internal sealed class ObjectManager : IObjectManager, IDisposable
{
    private readonly record struct RecoveredWorkspace(
        Guid? DefaultLayoutId,
        IReadOnlyList<ObjectSnapshot> StandaloneObjects,
        IReadOnlyList<ObjectSnapshot> DefaultLayoutObjects);

    private readonly ILogger<ObjectManager> _logger;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IObjectLayoutManager _layoutManager;
    private readonly IObjectPersistenceState _persistenceState;
    private readonly IObjectFolderService _objectFolderService;
    private readonly IObjectHousingModePolicy _housingModePolicy;
    private readonly IObjectRevisionTracker _revisionTracker;
    private readonly IObjectMutationService _mutationService;
    private readonly IObjectScene _scene;
    private readonly IObjectResolvedCollectionStore _collectionStore;
    private readonly IObjectLayoutAutoSaveService _layoutAutoSaveService;

    private int _pendingSavedLayoutReload;
    private uint _lastZone;
    private bool _sentBetweenAreas;
    private bool _disposed;

    public ObjectManager(
        ILogger<ObjectManager> logger,
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IObjectLayoutManager layoutManager,
        IObjectPersistenceState persistenceState,
        IObjectFolderService objectFolderService,
        IObjectHousingModePolicy housingModePolicy,
        IObjectRevisionTracker revisionTracker,
        IObjectMutationService mutationService,
        IObjectScene scene,
        IObjectResolvedCollectionStore collectionStore,
        IObjectLayoutAutoSaveService layoutAutoSaveService)
    {
        _logger = logger;
        _framework = framework;
        _clientState = clientState;
        _condition = condition;
        _layoutManager = layoutManager;
        _persistenceState = persistenceState;
        _objectFolderService = objectFolderService;
        _housingModePolicy = housingModePolicy;
        _revisionTracker = revisionTracker;
        _mutationService = mutationService;
        _scene = scene;
        _collectionStore = collectionStore;
        _layoutAutoSaveService = layoutAutoSaveService;

        _framework.Update += HandleFrameworkUpdate;
        _clientState.Logout += HandleClientLogout;
        _layoutManager.SavedLayoutsReloaded += HandleSavedLayoutsReloaded;
        _collectionStore.CollectionChanged += HandleResolvedCollectionChanged;
    }

    public event Action PersistentSceneChanged
    {
        add => _revisionTracker.PersistentSceneChanged += value;
        remove => _revisionTracker.PersistentSceneChanged -= value;
    }

    public bool TrySaveCurrentObjectsAsLayout(string name, out Guid layoutId)
    {
        layoutId = Guid.Empty;

        var persistedSnapshots = _persistenceState.GetPersistedSnapshots();
        var shouldPromoteStandaloneObjects = !_layoutManager.GetDefaultLayoutId().HasValue
            && persistedSnapshots.Count > 0;
        var layoutSnapshots = persistedSnapshots
            .Select(snapshot => snapshot with
            {
                Id = shouldPromoteStandaloneObjects ? snapshot.Id : Guid.NewGuid(),
            })
            .ToList();
        if (!_housingModePolicy.TryValidateLayout(layoutSnapshots, out _))
        {
            return false;
        }

        var layoutFolderExport = _objectFolderService.BuildLayoutExport(layoutSnapshots);
        var layout = _layoutManager.CreateLayout(
            name,
            layoutSnapshots,
            layoutFolderExport.Folders,
            layoutFolderExport.FolderColors);

        if (shouldPromoteStandaloneObjects)
        {
            _persistenceState.ClearStandaloneSnapshots();
            _layoutManager.TrySetDefaultLayout(layout.Id);
            IncrementPersistentSceneRevision();
            _ = _scene.ReloadForCurrentLocation();
        }
        else
        {
            IncrementSceneRevision();
        }

        layoutId = layout.Id;
        return true;
    }

    public Guid CreateEmptyLayout(string name)
    {
        var layout = _layoutManager.CreateLayout(name);
        IncrementSceneRevision();
        return layout.Id;
    }

    public bool TrySelectLayout(Guid? layoutId)
    {
        if (!layoutId.HasValue)
        {
            if (!_layoutManager.GetDefaultLayoutId().HasValue)
            {
                return true;
            }

            if (!_layoutManager.TrySetDefaultLayout(null))
            {
                return false;
            }

            IncrementPersistentSceneRevision();
            IncrementSceneRevision();
            return _scene.ReloadForCurrentLocation().IsApplied;
        }

        var defaultLayoutId = _layoutManager.GetDefaultLayoutId();
        if (defaultLayoutId.HasValue)
        {
            return defaultLayoutId.Value == layoutId.Value;
        }

        if (!_layoutManager.TryGetLayout(layoutId.Value, out var layout))
        {
            return false;
        }

        if (!_housingModePolicy.TryValidateLayout(layout.Objects, out _))
        {
            return false;
        }

        if (!_layoutManager.TrySetDefaultLayout(layout.Id))
        {
            return false;
        }

        IncrementPersistentSceneRevision();
        IncrementSceneRevision();
        return _scene.ReloadForCurrentLocation().IsApplied;
    }

    public bool TryDeleteLayout(Guid layoutId)
    {
        var defaultLayoutId = _layoutManager.GetDefaultLayoutId();
        var deleted = _layoutManager.TryDeleteLayout(layoutId);
        if (!deleted)
        {
            return false;
        }

        if (defaultLayoutId == layoutId)
        {
            IncrementPersistentSceneRevision();
        }

        IncrementSceneRevision();
        return defaultLayoutId == layoutId
            ? _scene.ReloadForCurrentLocation().IsApplied
            : true;
    }

    public bool TryRecoverWorkspace(ObjectPersistentWorkspaceSnapshot workspace, out string message)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        RecoveredWorkspace recovered = BuildRecoveredWorkspace(workspace);
        IReadOnlyList<ObjectSnapshot> recoveredObjects = recovered.StandaloneObjects
            .Concat(recovered.DefaultLayoutObjects)
            .ToList();
        if (!_housingModePolicy.TryValidateLayout(recoveredObjects, out message))
        {
            return false;
        }

        if (!TryApplyRecoveredDefaultLayout(recovered, workspace, out message))
        {
            return false;
        }

        if (!_layoutManager.TrySetDefaultLayout(recovered.DefaultLayoutId))
        {
            message = "Failed to select the recovered default layout.";
            return false;
        }

        _mutationService.ClearAllActiveEntries(removePersistedState: false);
        _persistenceState.ClearStandaloneSnapshots();
        foreach (ObjectSnapshot snapshot in recovered.StandaloneObjects)
        {
            _persistenceState.UpsertPersistedSnapshot(snapshot);
        }

        _objectFolderService.TryApplySceneState(BuildRecoveredFolderState(workspace, recovered));
        IncrementPersistentSceneRevision();

        SceneReloadResult reloadResult = _scene.ReloadForCurrentLocation();
        message = reloadResult switch
        {
            { CanApply: false } => "Recovered the autosave. Objects will load when the current location can display them.",
            { NeedsRetry: true } => "Recovered the autosave. Some objects need another scene refresh to finish loading.",
            _ => "Recovered the last autosaved object workspace.",
        };
        return true;
    }

    public void ClearAll()
        => ClearAll(persistLayoutChanges: true);

    private void ClearAll(bool persistLayoutChanges)
    {
        var hadPersistentSceneState = _persistenceState.HasPersistentSceneState();
        _mutationService.ClearAllActiveEntries(removePersistedState: false);
        _persistenceState.ClearStandaloneSnapshots();
        _layoutManager.ClearAllLayoutObjects(persistLayoutChanges);
        IncrementSceneRevision(persistentChanged: hadPersistentSceneState);
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
        _layoutManager.SavedLayoutsReloaded -= HandleSavedLayoutsReloaded;
        _collectionStore.CollectionChanged -= HandleResolvedCollectionChanged;

        if (ObjectFrameworkUtility.IsGameFrameworkDestroying())
        {
            _logger.LogWarning("framework is unloading, skipping object manager clear during dispose");
            return;
        }

        ClearAll(persistLayoutChanges: false);
    }

    private void IncrementPersistentSceneRevision()
        => IncrementSceneRevision(persistentChanged: true);

    private void IncrementSceneRevision(bool persistentChanged = false)
        => _revisionTracker.Increment(persistentChanged);

    private RecoveredWorkspace BuildRecoveredWorkspace(ObjectPersistentWorkspaceSnapshot workspace)
    {
        Guid? defaultLayoutId = workspace.DefaultLayoutId.HasValue
            && _layoutManager.TryGetLayout(workspace.DefaultLayoutId.Value, out _)
                ? workspace.DefaultLayoutId.Value
                : null;

        if (!defaultLayoutId.HasValue)
        {
            return new RecoveredWorkspace(
                null,
                workspace.Objects.Select(static snapshot => snapshot with { LayoutId = null }).ToList(),
                []);
        }

        Guid layoutId = defaultLayoutId.Value;
        List<ObjectSnapshot> defaultLayoutObjects = [];
        List<ObjectSnapshot> standaloneObjects = [];
        foreach (ObjectSnapshot snapshot in workspace.Objects)
        {
            if (snapshot.LayoutId == layoutId)
            {
                defaultLayoutObjects.Add(snapshot with { LayoutId = layoutId });
            }
            else
            {
                standaloneObjects.Add(snapshot with { LayoutId = null });
            }
        }

        return new RecoveredWorkspace(defaultLayoutId, standaloneObjects, defaultLayoutObjects);
    }

    private bool TryApplyRecoveredDefaultLayout(
        RecoveredWorkspace recovered,
        ObjectPersistentWorkspaceSnapshot workspace,
        out string message)
    {
        if (!recovered.DefaultLayoutId.HasValue)
        {
            message = string.Empty;
            return true;
        }

        if (!_layoutManager.TryReplaceLayoutObjects(recovered.DefaultLayoutId.Value, recovered.DefaultLayoutObjects))
        {
            message = $"Failed to restore autosave objects into layout '{workspace.Name}'.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static ObjectFolderSceneState BuildRecoveredFolderState(ObjectPersistentWorkspaceSnapshot workspace, RecoveredWorkspace recovered)
    {
        IReadOnlyList<string> standaloneFolders = ResolveRecoveredFolders(workspace, recovered.StandaloneObjects);
        IReadOnlyList<string> defaultLayoutFolders = recovered.DefaultLayoutId.HasValue
            ? ResolveRecoveredFolders(workspace, recovered.DefaultLayoutObjects)
            : [];

        return new ObjectFolderSceneState
        {
            StandaloneFolders = standaloneFolders,
            StandaloneFolderColors = ObjectFolderUtility.OrderFolderColorMap(workspace.FolderColors, standaloneFolders),
            DefaultLayoutId = recovered.DefaultLayoutId,
            DefaultLayoutFolders = defaultLayoutFolders,
            DefaultLayoutFolderColors = ObjectFolderUtility.OrderFolderColorMap(workspace.FolderColors, defaultLayoutFolders),
        };
    }

    private static IReadOnlyList<string> ResolveRecoveredFolders(ObjectPersistentWorkspaceSnapshot workspace, IReadOnlyList<ObjectSnapshot> objects)
    {
        if (objects.Count == 0)
        {
            return [];
        }

        HashSet<string> objectFolders = new(objects.Select(static snapshot => ObjectFolderUtility.SanitizeFolderPath(snapshot.FolderPath)), StringComparer.OrdinalIgnoreCase);
        return ObjectFolderUtility.OrderFolders(workspace.Folders.Where(folder => IsRecoveredFolderUsed(folder, objectFolders)).Concat(objectFolders));
    }

    private static bool IsRecoveredFolderUsed(string folder, IReadOnlySet<string> objectFolders)
    {
        string sanitizedFolder = ObjectFolderUtility.SanitizeFolderPath(folder);
        return !string.IsNullOrWhiteSpace(sanitizedFolder)
            && objectFolders.Any(objectFolder =>
                string.Equals(objectFolder, sanitizedFolder, StringComparison.OrdinalIgnoreCase)
                || objectFolder.StartsWith($"{sanitizedFolder}/", StringComparison.OrdinalIgnoreCase));
    }

    private void HandleFrameworkUpdate(IFramework framework)
    {
        _ = framework;
        ProcessZoneTransitionState();
        ProcessPendingSavedLayoutReload();
        _layoutAutoSaveService.FrameworkUpdate();
        _scene.FrameworkUpdate();
    }

    private void HandleSavedLayoutsReloaded()
        => Interlocked.Exchange(ref _pendingSavedLayoutReload, 1);

    private void ProcessPendingSavedLayoutReload()
    {
        if (Interlocked.Exchange(ref _pendingSavedLayoutReload, 0) == 0)
        {
            return;
        }

        IncrementPersistentSceneRevision();
        _ = _scene.ReloadForCurrentLocation();
    }

    private void ProcessZoneTransitionState()
    {
        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51])
        {
            uint zone = _clientState.TerritoryType;
            if (_lastZone != zone)
            {
                _lastZone = zone;
                if (!_sentBetweenAreas)
                {
                    _sentBetweenAreas = true;
                    HandleZoneSwitchStart();
                }
            }

            return;
        }

        if (!_sentBetweenAreas)
        {
            return;
        }

        _sentBetweenAreas = false;
        HandleZoneSwitchEnd();
    }

    private void HandleClientLogout(int type, int code)
    {
        _ = type;
        _ = code;
        ObjectFrameworkUtility.RunOnFrameworkThread(_framework, HandleLogout);
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
