using Intoner.Objects.Models;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Owns explicit folder state for standalone objects and the current default layout
/// </summary>
internal interface IObjectFolderService
{
    /// <summary>
    /// Captures the explicit folder state for standalone objects and the current default layout
    /// </summary>
    /// <returns>The captured explicit folder state.</returns>
    ObjectFolderSceneState CaptureSceneState();

    /// <summary>
    /// Applies explicit folder state for standalone objects and one saved layout
    /// </summary>
    /// <param name="state">The folder state to apply.</param>
    /// <returns>true when any explicit folder state changed.</returns>
    bool TryApplySceneState(ObjectFolderSceneState state);

    /// <summary>
    /// Gets the explicit folder color map for the current persisted scene
    /// </summary>
    /// <returns>The ordered folder color map for standalone objects and the current default layout.</returns>
    IReadOnlyDictionary<string, string> GetSceneFolderColors();

    /// <summary>
    /// Gets all explicit and object implied folders for the current persisted scene
    /// </summary>
    /// <param name="snapshots">The persisted object snapshots to include.</param>
    /// <returns>The ordered folder paths for the current persisted scene.</returns>
    IReadOnlyList<string> GetSceneFolders(IReadOnlyList<ObjectSnapshot> snapshots);

    /// <summary>
    /// Builds layout folder data from the current explicit scene state and the provided layout objects
    /// </summary>
    /// <param name="snapshots">The layout objects to include.</param>
    /// <returns>The ordered folder export for the saved layout.</returns>
    IReadOnlyList<ObjectFolderSnapshot> BuildLayoutExport(IReadOnlyList<ObjectSnapshot> snapshots);

    /// <summary>
    /// Checks whether any standalone explicit folder state exists
    /// </summary>
    /// <returns>true when any standalone folders exist.</returns>
    bool HasStandaloneState();

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
            Guid? defaultLayoutId = _layoutManager.GetDefaultLayoutId();
            IReadOnlyList<ObjectFolderSnapshot> defaultLayoutFolders = [];
            if (defaultLayoutId.HasValue
                && _layoutManager.TryGetLayout(defaultLayoutId.Value, out ObjectLayoutSnapshot defaultLayout))
            {
                defaultLayoutFolders = defaultLayout.Folders;
            }

            return new ObjectFolderSceneState
            {
                StandaloneFolders = ObjectFolderUtility.OrderFolderEntries(_standaloneFolders),
                DefaultLayoutId = defaultLayoutId,
                DefaultLayoutFolders = ObjectFolderUtility.OrderFolderEntries(defaultLayoutFolders),
            };
        }
    }

    public bool TryApplySceneState(ObjectFolderSceneState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_stateLock)
        {
            IReadOnlyList<ObjectFolderSnapshot> nextStandaloneFolders = ObjectFolderUtility.OrderFolderEntries(state.StandaloneFolders);
            IReadOnlyList<ObjectFolderSnapshot> nextDefaultLayoutFolders = ObjectFolderUtility.OrderFolderEntries(state.DefaultLayoutFolders);
            ObjectLayoutSnapshot layout = null!;
            if (state.DefaultLayoutId.HasValue
                && !_layoutManager.TryGetLayout(state.DefaultLayoutId.Value, out layout!))
            {
                return false;
            }

            bool changed = false;
            if (state.DefaultLayoutId.HasValue
                && !ObjectFolderUtility.FolderEntriesMatch(layout.Folders, nextDefaultLayoutFolders))
            {
                if (!_layoutManager.TryReplaceLayoutFolders(
                        state.DefaultLayoutId.Value,
                        nextDefaultLayoutFolders))
                {
                    return false;
                }

                changed = true;
            }

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

    public IReadOnlyDictionary<string, string> GetSceneFolderColors()
    {
        var sceneState = CaptureSceneState();
        return ObjectFolderUtility.ToFolderColorMap(ObjectFolderUtility.OrderFolderEntries(
            sceneState.DefaultLayoutFolders.Concat(sceneState.StandaloneFolders)));
    }

    public IReadOnlyList<string> GetSceneFolders(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        var sceneState = CaptureSceneState();
        return ObjectFolderUtility.ExpandFolders(
            sceneState.StandaloneFolders
                .Concat(sceneState.DefaultLayoutFolders)
                .Select(static folder => folder.Path)
                .Concat(snapshots.Select(static snapshot => snapshot.FolderPath)));
    }

    public IReadOnlyList<ObjectFolderSnapshot> BuildLayoutExport(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        var sceneState = CaptureSceneState();
        return ObjectFolderUtility.OrderFolderEntries(
            sceneState.DefaultLayoutFolders
                .Concat(sceneState.StandaloneFolders)
                .Concat(snapshots.Select(static snapshot => new ObjectFolderSnapshot(snapshot.FolderPath))));
    }

    public bool HasStandaloneState()
    {
        lock (_stateLock)
        {
            return _standaloneFolders.Count > 0;
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

