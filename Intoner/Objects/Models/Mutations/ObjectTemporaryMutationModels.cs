namespace Intoner.Objects.Models;

internal enum ObjectTemporaryMutationStatus
{
    Success = 0,
    InvalidSource = 1,
    InvalidObject = 2,
    StaleRevision = 3,
    ObjectNotFound = 4,
    SourceMismatch = 5,
    RuntimeApplyFailed = 6,
}

internal readonly record struct ObjectTemporaryMutationResult(
    ObjectTemporaryMutationStatus Status,
    long SourceRevision)
{
    public bool IsSuccess
        => Status == ObjectTemporaryMutationStatus.Success;
}

internal readonly record struct ObjectTemporarySourceState(
    Guid SessionId,
    long Revision);

internal enum ObjectTemporaryChangeKind
{
    Upsert = 1,
    Remove = 2,
    Patch = 3,
}

internal readonly record struct ObjectTemporaryChange(
    ObjectTemporaryChangeKind Kind,
    ObjectSnapshot? Snapshot,
    Guid ObjectId,
    ObjectSnapshotPatch? Patch = null);

