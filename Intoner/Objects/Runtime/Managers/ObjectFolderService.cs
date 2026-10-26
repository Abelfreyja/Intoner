using Intoner.Objects.Models;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Owns explicit folder state for standalone objects and enabled layouts
/// </summary>
internal interface IObjectFolderService
{
    /// <summary>
    /// Captures the explicit folder state for standalone objects and enabled layouts
    /// </summary>
    /// <returns>The captured explicit folder state.</returns>
    ObjectFolderSceneState CaptureSceneState();

    /// <summary>
    /// Captures folders for storage and all saved owners
    /// </summary>
    ObjectFolderSceneState CaptureStoredState();

    /// <summary>
    /// Applies explicit folder state to each owner without merging their folders
    /// </summary>
    /// <param name="state">The folder state to apply.</param>
    /// <returns>true when any explicit folder state changed.</returns>
    /// <exception cref="IOException"> saved layout files could not be restored after a failed write.</exception>
    bool TryApplySceneState(ObjectFolderSceneState state);

    /// <summary>
    /// Gets the explicit folder color map for one owner
    /// </summary>
    /// <param name="layoutId">Saved layout owner, or null for standalone folders.</param>
    /// <returns>The selected owner's folder color map.</returns>
    IReadOnlyDictionary<string, string> GetSceneFolderColors(Guid? layoutId);

    /// <summary>
    /// Gets explicit folders and folders used by objects or groups for one owner
    /// </summary>
    /// <param name="snapshots">The persisted objects and groups to include.</param>
    /// <param name="layoutId">Saved layout owner, or null for standalone folders.</param>
    /// <returns>The ordered folder paths for the selected owner.</returns>
    IReadOnlyList<string> GetSceneFolders(IEnumerable<ObjectSceneItemSnapshot> snapshots, Guid? layoutId);

    /// <summary>
    /// Gets explicit folders from enabled layouts and standalone storage
    /// </summary>
    /// <returns>The folder metadata in layout order, followed by standalone folders.</returns>
    IReadOnlyList<ObjectFolderSnapshot> GetWorkspaceFolders();

    /// <summary>
    /// Clears standalone explicit folder state
    /// </summary>
    void ClearStandaloneState();

    /// <summary>
    /// Replaces standalone explicit folder state without publishing a revision.
    /// </summary>
    /// <param name="folders">The replacement explicit folders.</param>
    void ReplaceStandaloneState(IReadOnlyList<ObjectFolderSnapshot> folders);
}

internal sealed class ObjectFolderService : IObjectFolderService
{
    private readonly Lock                   _stateLock;
    private readonly IObjectLayoutManager   _layoutManager;
    private readonly IObjectRevisionTracker _revisionTracker;

    private IReadOnlyList<ObjectFolderSnapshot> _standaloneFolders = [];

    public ObjectFolderService(
        ObjectStateLock stateLock,
        IObjectLayoutManager layoutManager,
        IObjectRevisionTracker revisionTracker)
    {
        _stateLock = stateLock.Value;
        _layoutManager = layoutManager;
        _revisionTracker = revisionTracker;
    }

    public ObjectFolderSceneState CaptureSceneState()
    {
        lock (_stateLock)
        {
            return new ObjectFolderSceneState
            {
                StandaloneFolders = ObjectFolderUtility.OrderFolderEntries(_standaloneFolders),
                LayoutFolders = _layoutManager.GetEnabledLayouts().ToDictionary(
                    static layout => layout.Id,
                    static layout => ObjectFolderUtility.OrderFolderEntries(layout.Folders)),
            };
        }
    }

    public ObjectFolderSceneState CaptureStoredState()
    {
        lock (_stateLock)
        {
            return new ObjectFolderSceneState
            {
                StandaloneFolders = _standaloneFolders,
                LayoutFolders = _layoutManager.GetLayouts().ToDictionary(static layout => layout.Id, static layout => layout.Folders),
            };
        }
    }

    public bool TryApplySceneState(ObjectFolderSceneState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_stateLock)
        {
            IReadOnlyList<ObjectFolderSnapshot> nextStandaloneFolders = ObjectFolderUtility.OrderFolderEntries(state.StandaloneFolders);
            List<ObjectLayoutContentUpdate> updates = [];
            foreach ((Guid id, IReadOnlyList<ObjectFolderSnapshot> folders) in state.LayoutFolders)
            {
                if (!_layoutManager.TryGetLayout(id, out ObjectLayoutSnapshot layout))
                {
                    return false;
                }

                if (!ObjectFolderUtility.FolderEntriesMatch(layout.Folders, folders))
                {
                    updates.Add(new ObjectLayoutContentUpdate(id, layout.Objects, folders) { Groups = layout.Groups });
                }
            }

            PersistentMutationStatus status = _layoutManager.ReplaceLayoutContents(updates);

            if (status == PersistentMutationStatus.RecoveryRequired)
            {
                throw new IOException("folder changes failed and saved layouts could not be restored");
            }

            if (status != PersistentMutationStatus.Success)
            {
                return false;
            }

            bool changed = updates.Count > 0;
            if (!ObjectFolderUtility.FolderEntriesMatch(_standaloneFolders, nextStandaloneFolders))
            {
                _standaloneFolders = nextStandaloneFolders;
                changed = true;
            }

            if (changed)
            {
                _revisionTracker.Increment(persistentChanged: true);
            }

            return changed;
        }
    }

    public IReadOnlyDictionary<string, string> GetSceneFolderColors(Guid? layoutId)
        => ObjectFolderUtility.ToFolderColorMap(ObjectFolderUtility.OrderFolderEntries(GetExplicitFolders(layoutId)));

    public IReadOnlyList<string> GetSceneFolders(IEnumerable<ObjectSceneItemSnapshot> snapshots, Guid? layoutId)
    {
        return ObjectFolderUtility.ExpandFolders(
            GetExplicitFolders(layoutId)
                .Select(static folder => folder.Path)
                .Concat(snapshots.Where(snapshot => snapshot.LayoutId == layoutId).Select(static snapshot => snapshot.FolderPath)));
    }

    private IReadOnlyList<ObjectFolderSnapshot> GetExplicitFolders(Guid? layoutId)
    {
        lock (_stateLock)
        {
            if (!layoutId.HasValue)
            {
                return _standaloneFolders;
            }

            return _layoutManager.IsLayoutEnabled(layoutId.Value)
                && _layoutManager.TryGetLayout(layoutId.Value, out ObjectLayoutSnapshot layout)
                    ? layout.Folders
                    : [];
        }
    }

    public IReadOnlyList<ObjectFolderSnapshot> GetWorkspaceFolders()
    {
        lock (_stateLock)
        {
            return _layoutManager.GetEnabledLayouts().SelectMany(static layout => layout.Folders)
                .Concat(_standaloneFolders).ToArray();
        }
    }

    public void ClearStandaloneState()
        => ReplaceStandaloneState([]);

    public void ReplaceStandaloneState(IReadOnlyList<ObjectFolderSnapshot> folders)
    {
        IReadOnlyList<ObjectFolderSnapshot> orderedFolders = ObjectFolderUtility.OrderFolderEntries(folders);
        lock (_stateLock)
        {
            _standaloneFolders = orderedFolders;
        }
    }
}
