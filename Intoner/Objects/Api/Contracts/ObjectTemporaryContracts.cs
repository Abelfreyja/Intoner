using MessagePack;

namespace Intoner.Objects.Api;

/// <summary> full temporary layout replacement </summary>
/// <param name="SourceKey"> temporary source key </param>
/// <param name="SourceSessionId"> source session id; use `Guid.Empty` to reuse the current session for this source </param>
/// <param name="Name"> temporary source display name, or empty to keep the current name </param>
/// <param name="Revision"> source revision, or '0' to advance it locally </param>
/// <param name="Objects"> full replacement object list for the source </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporaryLayoutApplyRequest(
    string SourceKey,
    Guid SourceSessionId,
    string Name,
    long Revision,
    IReadOnlyList<WorldObject> Objects);

/// <summary> temporary object upsert </summary>
/// <param name="SourceKey"> temporary source key </param>
/// <param name="SourceSessionId"> source session id; use `Guid.Empty` to reuse the current session for this source </param>
/// <param name="Name"> temporary source display name, or empty to keep the current name </param>
/// <param name="Revision"> source revision, or '0' to advance it locally </param>
/// <param name="Object"> object payload to create or update in the source </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporaryObjectUpsert(
    string SourceKey,
    Guid SourceSessionId,
    string Name,
    long Revision,
    WorldObject Object);

/// <summary> temporary object patch </summary>
/// <param name="SourceKey"> temporary source key </param>
/// <param name="SourceSessionId"> source session id; use `Guid.Empty` to reuse the current session for this source </param>
/// <param name="Name"> temporary source display name, or empty to keep the current name </param>
/// <param name="Revision"> source revision, or '0' to advance it locally </param>
/// <param name="ObjectId"> object id from the temporary source </param>
/// <param name="Patch"> partial update payload; null fields keep the current value </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporaryObjectPatch(
    string SourceKey,
    Guid SourceSessionId,
    string Name,
    long Revision,
    Guid ObjectId,
    WorldObjectPatch Patch);

/// <summary> one temporary object change </summary>
/// <param name="Kind"> change kind </param>
/// <param name="Object"> object payload for upsert changes </param>
/// <param name="ObjectId"> object id from the temporary source for remove and patch changes </param>
/// <param name="Patch"> partial update payload for patch changes; null fields keep the current value </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporaryObjectChange(
    TemporaryObjectChangeKind Kind,
    WorldObject? Object = null,
    Guid ObjectId = default,
    WorldObjectPatch? Patch = null);

/// <summary> batched temporary object changes for one source </summary>
/// <param name="SourceKey"> temporary source key </param>
/// <param name="SourceSessionId"> source session id; use `Guid.Empty` to reuse the current session for this source </param>
/// <param name="Name"> temporary source display name, or empty to keep the current name </param>
/// <param name="Revision"> source revision, or '0' to advance it locally </param>
/// <param name="Changes"> ordered source changes to apply as one batch </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporaryObjectChangeSet(
    string SourceKey,
    Guid SourceSessionId,
    string Name,
    long Revision,
    IReadOnlyList<TemporaryObjectChange> Changes);

/// <summary> temporary object removal </summary>
/// <param name="SourceKey"> temporary source key </param>
/// <param name="SourceSessionId"> source session id; use `Guid.Empty` to reuse the current session for this source </param>
/// <param name="ObjectId"> original source object id before runtime remaps it for temporary use </param>
/// <param name="Revision"> source revision, or '0' to advance it locally </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporaryObjectRemoval(
    string SourceKey,
    Guid SourceSessionId,
    Guid ObjectId,
    long Revision);

/// <summary> full temporary layout removal </summary>
/// <param name="SourceKey"> temporary source key to remove </param>
/// <param name="SourceSessionId"> source session id; use `Guid.Empty` to reuse the current session for this source </param>
/// <param name="Revision"> source revision, or '0' to advance it locally </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporaryLayoutRemoval(
    string SourceKey,
    Guid SourceSessionId,
    long Revision);

/// <summary> temporary source mutation result </summary>
/// <param name="Status"> typed mutation status </param>
/// <param name="SourceRevision"> source revision after the request was checked or applied </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporarySourceMutationResult(
    TemporarySourceMutationStatus Status,
    long SourceRevision);

