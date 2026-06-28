using Intoner.Objects.Models;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Runtime;

internal sealed record ObjectLayoutFolderExport
{
    public IReadOnlyList<string> Folders { get; init; } = [];
    public IReadOnlyDictionary<string, string> FolderColors { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

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
    ObjectLayoutFolderExport BuildLayoutExport(IReadOnlyList<ObjectSnapshot> snapshots);

    /// <summary>
    /// Checks whether any standalone explicit folder state exists
    /// </summary>
    /// <returns>true when any standalone folders or folder colors exist.</returns>
    bool HasStandaloneState();

    /// <summary>
    /// Clears standalone explicit folder state
    /// </summary>
    void ClearStandaloneState();
}

internal sealed class ObjectFolderService : IObjectFolderService
{
    private readonly Lock                   _stateLock;
    private readonly IObjectLayoutManager   _layoutManager;
    private readonly IObjectRevisionTracker _revisionTracker;

    private List<string>              _standaloneFolders = [];
    private Dictionary<string, string> _standaloneFolderColors = new(StringComparer.OrdinalIgnoreCase);

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
        List<string> standaloneFolders;
        Dictionary<string, string> standaloneFolderColors;
        lock (_stateLock)
        {
            standaloneFolders = [.. _standaloneFolders];
            standaloneFolderColors = new Dictionary<string, string>(_standaloneFolderColors, StringComparer.OrdinalIgnoreCase);
        }

        var defaultLayoutId = _layoutManager.GetDefaultLayoutId();
        IReadOnlyList<string> defaultLayoutFolders = [];
        IReadOnlyDictionary<string, string> defaultLayoutFolderColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (defaultLayoutId.HasValue
            && _layoutManager.TryGetLayout(defaultLayoutId.Value, out var defaultLayout))
        {
            defaultLayoutFolders = defaultLayout.Folders;
            defaultLayoutFolderColors = defaultLayout.FolderColors;
        }

        return new ObjectFolderSceneState
        {
            StandaloneFolders = ObjectFolderUtility.OrderFolders(standaloneFolders),
            StandaloneFolderColors = ObjectFolderUtility.OrderFolderColorMap(standaloneFolderColors, standaloneFolders),
            DefaultLayoutId = defaultLayoutId,
            DefaultLayoutFolders = ObjectFolderUtility.OrderFolders(defaultLayoutFolders),
            DefaultLayoutFolderColors = ObjectFolderUtility.OrderFolderColorMap(defaultLayoutFolderColors, defaultLayoutFolders),
        };
    }

    public bool TryApplySceneState(ObjectFolderSceneState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var nextStandaloneFolders = ObjectFolderUtility.OrderFolders(state.StandaloneFolders);
        var nextStandaloneFolderColors = ObjectFolderUtility.OrderFolderColorMap(state.StandaloneFolderColors, nextStandaloneFolders);
        var nextDefaultLayoutFolders = ObjectFolderUtility.OrderFolders(state.DefaultLayoutFolders);
        var nextDefaultLayoutFolderColors = ObjectFolderUtility.OrderFolderColorMap(state.DefaultLayoutFolderColors, nextDefaultLayoutFolders);
        ObjectLayoutSnapshot layout = null!;
        if (state.DefaultLayoutId.HasValue
            && !_layoutManager.TryGetLayout(state.DefaultLayoutId.Value, out layout!))
        {
            return false;
        }

        var changed = false;

        if (state.DefaultLayoutId.HasValue)
        {
            var layoutId = state.DefaultLayoutId.Value;
            if (!ObjectFolderUtility.FolderListsMatch(layout.Folders, nextDefaultLayoutFolders)
                || !ObjectFolderUtility.FolderColorMapsMatch(layout.FolderColors, nextDefaultLayoutFolderColors))
            {
                if (!_layoutManager.TryReplaceLayoutFolderState(layoutId, nextDefaultLayoutFolders, nextDefaultLayoutFolderColors))
                {
                    return false;
                }

                changed = true;
            }
        }

        var standaloneFoldersChanged = false;
        lock (_stateLock)
        {
            if (!ObjectFolderUtility.FolderListsMatch(_standaloneFolders, nextStandaloneFolders))
            {
                _standaloneFolders = [.. nextStandaloneFolders];
                standaloneFoldersChanged = true;
                changed = true;
            }

            if (standaloneFoldersChanged
                || !ObjectFolderUtility.FolderColorMapsMatch(_standaloneFolderColors, nextStandaloneFolderColors))
            {
                _standaloneFolderColors = new Dictionary<string, string>(nextStandaloneFolderColors, StringComparer.OrdinalIgnoreCase);
                changed = true;
            }
        }

        if (changed)
        {
            _revisionTracker.Increment(persistentChanged: true);
        }

        return changed;
    }

    public IReadOnlyDictionary<string, string> GetSceneFolderColors()
    {
        var sceneState = CaptureSceneState();
        return ObjectFolderUtility.OrderFolderColorMap(
            sceneState.StandaloneFolderColors
                .Concat(sceneState.DefaultLayoutFolderColors),
            sceneState.StandaloneFolders
                .Concat(sceneState.DefaultLayoutFolders));
    }

    public IReadOnlyList<string> GetSceneFolders(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        var sceneState = CaptureSceneState();
        return ObjectFolderUtility.OrderFolders(
            sceneState.StandaloneFolders
                .Concat(sceneState.DefaultLayoutFolders)
                .Concat(snapshots.Select(static snapshot => snapshot.FolderPath)));
    }

    public ObjectLayoutFolderExport BuildLayoutExport(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        var sceneState = CaptureSceneState();
        var folders = ObjectFolderUtility.OrderFolders(
            sceneState.StandaloneFolders
                .Concat(sceneState.DefaultLayoutFolders)
                .Concat(snapshots.Select(static snapshot => snapshot.FolderPath)));
        var folderColors = ObjectFolderUtility.OrderFolderColorMap(
            sceneState.StandaloneFolderColors
                .Concat(sceneState.DefaultLayoutFolderColors),
            folders);
        return new ObjectLayoutFolderExport
        {
            Folders = folders,
            FolderColors = folderColors,
        };
    }

    public bool HasStandaloneState()
    {
        lock (_stateLock)
        {
            return _standaloneFolders.Count > 0
                || _standaloneFolderColors.Count > 0;
        }
    }

    public void ClearStandaloneState()
    {
        lock (_stateLock)
        {
            _standaloneFolders = [];
            _standaloneFolderColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

