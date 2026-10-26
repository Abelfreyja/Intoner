using Intoner.Objects.Api;
using Intoner.Objects.Filesystem.Watching;
using Intoner.Objects.Models;
using Intoner.Services.Serialization;
using Intoner.Services.Storage;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Filesystem.Layouts;

/// <summary> protects and reads the previous sessions autosaved layout draft </summary>
internal interface IObjectLayoutRecoveryService
{
    /// <summary> archives the previous sessions draft before current autosaves may change it </summary>
    /// <returns>true when the current autosave path can be written or cleared</returns>
    bool TryPrepareSession();

    /// <summary> checks whether a previous sessions autosave draft exists </summary>
    /// <returns>true when a recovery draft exists on disk</returns>
    bool HasCurrentRecovery();

    /// <summary> reads the previous sessions autosave draft </summary>
    /// <param name="workspace">the recovered layout snapshot when loading succeeds</param>
    /// <param name="errorMessage">the failure reason when loading fails</param>
    /// <returns>true when the autosave draft was loaded</returns>
    bool TryLoadCurrentRecovery(out ObjectPersistentWorkspaceSnapshot workspace, out string errorMessage);
}

internal sealed class ObjectLayoutRecoveryService : IObjectLayoutRecoveryService, IDisposable
{
    private readonly ILogger<ObjectLayoutRecoveryService> _logger;
    private readonly IPluginStoragePaths                  _pathService;
    private readonly IPluginFileSystem                    _fileSystem;
    private readonly IObjectFileWatchSubscription         _watchSubscription;

    private readonly Lock _lock = new();
    private bool _sessionPrepared;
    private bool _hasCurrentRecovery;

    public ObjectLayoutRecoveryService(
        ILogger<ObjectLayoutRecoveryService> logger,
        IPluginStoragePaths pathService,
        IPluginFileSystem fileSystem,
        IObjectFileWatcherService fileWatcherService)
    {
        _logger            = logger;
        _pathService       = pathService;
        _fileSystem        = fileSystem;
        _watchSubscription = fileWatcherService.Watch(
            new ObjectFileWatchOptions(_pathService.ObjectAutosaveRootPath, "*.autosave.json",
                IncludeSubdirectories: false, DebounceDelay: TimeSpan.FromMilliseconds(650)),
            _ => RefreshRecoveryAvailability());
        RefreshRecoveryAvailability();
    }

    public bool TryPrepareSession()
    {
        lock (_lock)
        {
            if (_sessionPrepared)
            {
                return true;
            }

            try
            {
                if (_fileSystem.FileExists(_pathService.ObjectAutosaveCurrentPath))
                {
                    string json = JsonDocumentUtility.ReadFile(_fileSystem, _pathService.ObjectAutosaveCurrentPath);
                    bool valid = ObjectLayoutJsonSerializer.TryDeserializeAutosave(json, out _, out string errorMessage);
                    string archivePath = valid ? _pathService.ObjectAutosaveRecoveryPath : _pathService.ObjectAutosaveRejectedPath;
                    JsonDocumentUtility.WriteFile(_fileSystem, archivePath, json);
                    _fileSystem.DeleteFile(_pathService.ObjectAutosaveCurrentPath);

                    if (!valid)
                    {
                        _logger.LogWarning("preserved rejected object layout autosave draft: {Reason}", errorMessage);
                    }
                }

                _sessionPrepared = true;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "failed to preserve the previous object layout autosave draft");
                return false;
            }
            finally
            {
                RefreshRecoveryAvailability();
            }
        }
    }

    public bool HasCurrentRecovery()
        => Volatile.Read(ref _hasCurrentRecovery);

    public void Dispose()
        => _watchSubscription.Dispose();

    private void RefreshRecoveryAvailability()
    {
        lock (_lock)
        {
            Volatile.Write(ref _hasCurrentRecovery, _fileSystem.FileExists(GetRecoveryPath()));
        }
    }

    public bool TryLoadCurrentRecovery(out ObjectPersistentWorkspaceSnapshot workspace, out string errorMessage)
    {
        string recoveryPath = GetRecoveryPath();
        if (TryLoadRecovery(recoveryPath, out workspace, out errorMessage))
        {
            return true;
        }

        // a pending archive must not hide the backup when its source cannot be read
        return string.Equals(recoveryPath, _pathService.ObjectAutosaveCurrentPath, StringComparison.Ordinal)
            && _fileSystem.FileExists(_pathService.ObjectAutosaveRecoveryPath)
            && TryLoadRecovery(_pathService.ObjectAutosaveRecoveryPath, out workspace, out errorMessage);
    }

    private bool TryLoadRecovery(string path, out ObjectPersistentWorkspaceSnapshot workspace, out string errorMessage)
    {
        workspace = null!;
        if (!_fileSystem.FileExists(path))
        {
            errorMessage = "No autosave draft exists.";
            return false;
        }

        try
        {
            string json = JsonDocumentUtility.ReadFile(_fileSystem, path);
            if (ObjectLayoutJsonSerializer.TryDeserializeAutosave(json, out workspace, out errorMessage))
            {
                return true;
            }

            _logger.LogWarning("failed to parse layout autosave draft: {Reason}", errorMessage);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load layout autosave draft");
            errorMessage = "Failed to load the autosave draft.";
            return false;
        }
    }

    // an archive failure leaves the previous sessions draft at the current path
    private string GetRecoveryPath()
    {
        lock (_lock)
        {
            return !_sessionPrepared && _fileSystem.FileExists(_pathService.ObjectAutosaveCurrentPath)
                ? _pathService.ObjectAutosaveCurrentPath
                : _pathService.ObjectAutosaveRecoveryPath;
        }
    }
}
