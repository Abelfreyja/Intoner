using Intoner.Objects.Api;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Services.Configuration;
using Intoner.Services.Storage;
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
    private readonly IIntonerConfigurationService         _configurationService;
    private readonly IPluginStoragePaths                  _pathService;
    private readonly IPluginFileSystem                    _fileSystem;
    private readonly IObjectPersistenceState              _persistenceState;
    private readonly IObjectLayoutRecoveryService         _recoveryService;

    private DateTime _nextSaveAtUtc = DateTime.MinValue;
    private long _lastSavedPersistentRevision;

    public ObjectLayoutAutoSaveService(
        ILogger<ObjectLayoutAutoSaveService> logger,
        IIntonerConfigurationService configurationService,
        IPluginStoragePaths pathService,
        IPluginFileSystem fileSystem,
        IObjectPersistenceState persistenceState,
        IObjectLayoutRecoveryService recoveryService)
    {
        _logger               = logger;
        _configurationService = configurationService;
        _pathService          = pathService;
        _fileSystem           = fileSystem;
        _persistenceState     = persistenceState;
        _recoveryService      = recoveryService;

        // only persistent edits should create a draft for this session
        _lastSavedPersistentRevision = persistenceState.Revision;
    }

    public void FrameworkUpdate()
        => FrameworkUpdate(DateTime.UtcNow);

    internal void FrameworkUpdate(DateTime now)
    {
        LayoutAutoSaveConfiguration configuration = _configurationService.Current.LayoutAutoSave;
        if (!configuration.Enabled)
        {
            return;
        }

        if (now < _nextSaveAtUtc)
        {
            return;
        }

        _nextSaveAtUtc = now + ResolveInterval(configuration);
        if (!_recoveryService.TryPrepareSession())
        {
            return;
        }

        if (_persistenceState.Revision == _lastSavedPersistentRevision)
        {
            return;
        }

        TrySaveWorkspace(now);
    }

    private void TrySaveWorkspace(DateTime savedAtUtc)
    {
        try
        {
            ObjectPersistentWorkspaceSnapshot workspace = _persistenceState.CaptureWorkspace(savedAtUtc);
            if (workspace.Objects.Count == 0 && workspace.StandaloneFolders.Count == 0 && workspace.DefaultLayoutFolders.Count == 0)
            {
                _fileSystem.DeleteFile(_pathService.ObjectAutosaveCurrentPath);
            }
            else
            {
                ObjectLayoutAutosaveDocument document = ObjectLayoutJsonSerializer.BuildAutosaveDocument(
                    workspace with { Name = $"{workspace.Name} autosave" });
                _fileSystem.WriteAllTextAtomic(
                    _pathService.ObjectAutosaveCurrentPath,
                    ObjectLayoutJsonSerializer.SerializeAutosave(document));
            }

            _lastSavedPersistentRevision = workspace.Revision;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to update object layout autosave draft");
        }
    }

    private static TimeSpan ResolveInterval(LayoutAutoSaveConfiguration configuration)
        => TimeSpan.FromSeconds(configuration.IntervalSeconds);
}

