using Intoner.Objects.Api;
using Intoner.Objects.Filesystem.Storage;
using Intoner.Objects.Models;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Filesystem.Layouts;

/// <summary> reads the latest autosaved layout draft </summary>
internal interface IObjectLayoutRecoveryService
{
    /// <summary> checks whether a current autosave draft exists </summary>
    /// <returns>true when a autosave draft exists on disk</returns>
    bool HasCurrentRecovery();

    /// <summary> reads the current autosave draft </summary>
    /// <param name="workspace">the recovered layout snapshot when loading succeeds</param>
    /// <param name="errorMessage">the failure reason when loading fails</param>
    /// <returns>true when the autosave draft was loaded</returns>
    bool TryLoadCurrentRecovery(out ObjectPersistentWorkspaceSnapshot workspace, out string errorMessage);
}

internal sealed class ObjectLayoutRecoveryService(
    ILogger<ObjectLayoutRecoveryService> logger,
    IObjectStoragePathService pathService,
    IObjectFileSystem fileSystem) : IObjectLayoutRecoveryService
{
    public bool HasCurrentRecovery()
        => fileSystem.FileExists(pathService.ObjectAutosaveCurrentPath);

    public bool TryLoadCurrentRecovery(out ObjectPersistentWorkspaceSnapshot workspace, out string errorMessage)
    {
        workspace = null!;
        if (!HasCurrentRecovery())
        {
            errorMessage = "No autosave draft exists.";
            return false;
        }

        try
        {
            string json = fileSystem.ReadAllText(pathService.ObjectAutosaveCurrentPath);
            if (ObjectLayoutJsonSerializer.TryDeserializeAutosave(json, out workspace, out errorMessage))
            {
                return true;
            }

            logger.LogWarning("failed to parse layout autosave draft: {Reason}", errorMessage);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to load layout autosave draft");
            errorMessage = "Failed to load the autosave draft.";
            return false;
        }
    }
}

