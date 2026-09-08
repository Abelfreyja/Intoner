namespace Intoner.Objects.Models;

internal enum PersistentMutationStatus
{
    Success = 0,
    InvalidRequest = 1,
    NotFound = 2,
    Conflict = 3,
    StorageFailed = 4,
    RecoveryRequired = 5,
    RuntimeApplyFailed = 6,
}

internal readonly record struct PersistentMutationResult(
    PersistentMutationStatus Status,
    bool IsAccepted,
    Guid? EntityId = null,
    string Message = "",
    long SceneRevision = 0,
    long PersistentRevision = 0,
    long SavedLayoutsRevision = 0)
{
    public bool IsSuccess
        => Status == PersistentMutationStatus.Success;

    public static PersistentMutationResult Success(Guid? entityId = null)
        => new(PersistentMutationStatus.Success, true, entityId);

    public static PersistentMutationResult Failed(PersistentMutationStatus status, string message)
        => new(status, false, null, message);

    public static PersistentMutationResult AcceptedRuntimeFailure(Guid? entityId, string message)
        => new(PersistentMutationStatus.RuntimeApplyFailed, true, entityId, message);

    public PersistentMutationResult WithRevisions(
        long sceneRevision,
        long persistentRevision,
        long savedLayoutsRevision)
        => this with
        {
            SceneRevision = sceneRevision,
            PersistentRevision = persistentRevision,
            SavedLayoutsRevision = savedLayoutsRevision,
        };
}
