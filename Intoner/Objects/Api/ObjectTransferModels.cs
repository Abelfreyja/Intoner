using MessagePack;

namespace Intoner.Objects.Api;

internal enum ObjectTransferKind
{
    SceneObjects = 1,
    Transform = 2,
    ObjectLibrary = 3,
}

internal enum ObjectTransformPart
{
    Position = 1,
    Rotation = 2,
    Scale = 3,
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record ObjectTransferDocument(
    [property: Key(0)] ObjectTransferKind Kind,
    [property: Key(1)] SceneObjectTransfer? SceneObjects = null,
    [property: Key(2)] ObjectLibraryTransfer? ObjectLibrary = null,
    [property: Key(3)] ObjectTransformValue? Transform = null);

[MessagePackObject(AllowPrivate = true)]
internal sealed record SceneObjectTransfer(
    [property: Key(0)] PersistentObjectSet Content,
    [property: Key(1)] string? FolderName = null);

internal enum ObjectLibraryTransferKind
{
    Folder = 1,
    Prefab = 2,
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record ObjectLibraryTransfer(
    [property: Key(0)] Guid RootGroupId,
    [property: Key(1)] IReadOnlyList<ObjectLibraryTransferGroup> Groups,
    [property: Key(2)] IReadOnlyList<ObjectLibraryTransferEntry> Entries);

[MessagePackObject(AllowPrivate = true)]
internal sealed record ObjectLibraryTransferGroup(
    [property: Key(0)] Guid Id,
    [property: Key(1)] ObjectLibraryTransferKind Kind,
    [property: Key(2)] string Name,
    [property: Key(3)] Guid? ParentFolderId,
    [property: Key(4)] string Color,
    [property: Key(5)] ObjectLocationData? CapturedIn,
    [property: Key(6)] IReadOnlyList<Guid> EntryIds);

[MessagePackObject(AllowPrivate = true)]
internal sealed record ObjectLibraryTransferEntry(
    [property: Key(0)] Guid Id,
    [property: Key(1)] string Name,
    [property: Key(2)] WorldObjectKind Kind,
    [property: Key(3)] bool Visible,
    [property: Key(4)] ObjectVector3 Scale,
    [property: Key(5)] WorldObjectModelData Model,
    [property: Key(6)] WorldObjectTransform? WorldTransform,
    [property: Key(7)] Guid? AttachmentParentEntryId,
    [property: Key(8)] Guid? FolderId);

[MessagePackObject(AllowPrivate = true)]
internal sealed record ObjectTransformValue(
    [property: Key(0)] ObjectTransformPart Part,
    [property: Key(1)] ObjectVector3 Value);
