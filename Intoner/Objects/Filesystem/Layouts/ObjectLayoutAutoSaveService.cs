using Intoner.Objects.Api;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Filesystem.Storage;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Filesystem.Layouts;

/// <summary> writes separate autosave drafts for the current object workspace </summary>
internal interface IObjectLayoutAutoSaveService
{
    /// <summary> runs one autosave interval check from the framework update loop </summary>
    void FrameworkUpdate();
}

internal sealed class ObjectLayoutAutoSaveService : IObjectLayoutAutoSaveService
{
    private readonly ILogger<ObjectLayoutAutoSaveService> _logger;
    private readonly IObjectConfigurationService          _configurationService;
    private readonly IObjectStoragePathService            _pathService;
    private readonly IObjectFileSystem                    _fileSystem;
    private readonly IObjectPersistenceState              _persistenceState;
    private readonly IObjectFolderService                 _folderService;
    private readonly IObjectLayoutManager                 _layoutManager;
    private readonly IObjectRevisionTracker               _revisionTracker;

    private DateTime _nextSaveAtUtc = DateTime.MinValue;
    private long _lastSavedPersistentRevision;
    private bool _autosaveFileDeletedForCurrentRevision;

    public ObjectLayoutAutoSaveService(
        ILogger<ObjectLayoutAutoSaveService> logger,
        IObjectConfigurationService configurationService,
        IObjectStoragePathService pathService,
        IObjectFileSystem fileSystem,
        IObjectPersistenceState persistenceState,
        IObjectFolderService folderService,
        IObjectLayoutManager layoutManager,
        IObjectRevisionTracker revisionTracker)
    {
        _logger               = logger;
        _configurationService = configurationService;
        _pathService          = pathService;
        _fileSystem           = fileSystem;
        _persistenceState     = persistenceState;
        _folderService        = folderService;
        _layoutManager        = layoutManager;
        _revisionTracker      = revisionTracker;
    }

    public void FrameworkUpdate()
    {
        LayoutAutoSaveConfiguration configuration = _configurationService.Current.LayoutAutoSave;
        if (!configuration.Enabled)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (now < _nextSaveAtUtc)
        {
            return;
        }

        _nextSaveAtUtc = now + ResolveInterval(configuration);
        long persistentRevision = _revisionTracker.GetPersistentSceneRevision();
        if (!_persistenceState.HasPersistentSceneState())
        {
            DeleteAutosaveForEmptyWorkspace(persistentRevision);
            return;
        }

        if (persistentRevision == _lastSavedPersistentRevision)
        {
            return;
        }

        TryWriteAutosave(persistentRevision, now);
    }

    private void TryWriteAutosave(long persistentRevision, DateTime savedAtUtc)
    {
        try
        {
            ObjectPersistentWorkspaceSnapshot workspace = CapturePersistentWorkspace(persistentRevision, savedAtUtc);
            ObjectLayoutAutosaveDocument document = ObjectLayoutJsonSerializer.BuildAutosaveDocument(workspace);

            _fileSystem.WriteAllTextAtomic(
                _pathService.ObjectAutosaveCurrentPath,
                ObjectLayoutJsonSerializer.SerializeAutosave(document));
            _lastSavedPersistentRevision = persistentRevision;
            _autosaveFileDeletedForCurrentRevision = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to write object layout autosave draft");
        }
    }

    private ObjectPersistentWorkspaceSnapshot CapturePersistentWorkspace(long persistentRevision, DateTime savedAtUtc)
    {
        IReadOnlyList<ObjectSnapshot> snapshots = _persistenceState.GetPersistedSnapshots();
        return new ObjectPersistentWorkspaceSnapshot
        {
            Objects = snapshots,
            Folders = _folderService.GetSceneFolders(snapshots),
            FolderColors = _folderService.GetSceneFolderColors(),
            DefaultLayoutId = _layoutManager.GetDefaultLayoutId(),
            Name = ResolveAutosaveName(),
            Revision = persistentRevision,
            CapturedAtUtc = savedAtUtc,
        };
    }

    private void DeleteAutosaveForEmptyWorkspace(long persistentRevision)
    {
        if (_autosaveFileDeletedForCurrentRevision)
        {
            _lastSavedPersistentRevision = persistentRevision;
            return;
        }

        try
        {
            _fileSystem.DeleteFile(_pathService.ObjectAutosaveCurrentPath);
            _lastSavedPersistentRevision = persistentRevision;
            _autosaveFileDeletedForCurrentRevision = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to remove empty object layout autosave draft");
        }
    }

    private string ResolveAutosaveName()
    {
        Guid? defaultLayoutId = _layoutManager.GetDefaultLayoutId();
        if (defaultLayoutId.HasValue && _layoutManager.TryGetLayout(defaultLayoutId.Value, out ObjectLayoutSnapshot layout))
        {
            return $"{layout.Name} autosave";
        }

        return "Standalone objects autosave";
    }

    private static TimeSpan ResolveInterval(LayoutAutoSaveConfiguration configuration)
        => TimeSpan.FromSeconds(configuration.IntervalSeconds);
}

