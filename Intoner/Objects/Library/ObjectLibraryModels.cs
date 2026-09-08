using Intoner.Objects.Models;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.Library;

internal sealed record ObjectLibraryPreset
{
    public required ObjectKind Kind { get; init; }
    public required bool Visible { get; init; }
    public Vector3 Scale { get; init; } = Vector3.One;
    public required ObjectData Model { get; init; }
}

internal sealed record ObjectLibraryEntry
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public Guid? FolderId { get; init; }
    public Guid? AttachmentParentEntryId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required ObjectLibraryPreset Preset { get; init; }
    public SceneTransform? WorldTransform { get; init; }
}

internal abstract record ObjectLibraryGroup
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public Guid? ParentFolderId { get; init; }
    public string Color { get; init; } = string.Empty;
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

internal sealed record ObjectLibraryFolder : ObjectLibraryGroup;

internal sealed record ObjectLibraryPrefab : ObjectLibraryGroup
{
    public required SceneCreationContext CapturedIn { get; init; }
    public required IReadOnlyList<Guid> EntryIds { get; init; }
}

internal sealed record ObjectLibraryImport(ObjectLibraryContent Content, Guid RootGroupId);

internal readonly record struct ObjectLibraryContent(
    ObjectLibraryEntry[] Entries,
    ObjectLibraryFolder[] Folders,
    ObjectLibraryPrefab[] Prefabs);

internal sealed record ObjectLibrarySnapshot(
    long Revision,
    IReadOnlyList<ObjectLibraryEntry> Entries,
    IReadOnlyList<ObjectLibraryFolder> Folders,
    IReadOnlyList<ObjectLibraryPrefab> Prefabs)
{
    public static ObjectLibrarySnapshot Empty { get; } = Create(0, new ObjectLibraryContent([], [], []));

    public static ObjectLibrarySnapshot Create(long revision, ObjectLibraryContent content)
        => new(
            revision,
            Array.AsReadOnly(content.Entries),
            Array.AsReadOnly(content.Folders),
            Array.AsReadOnly(content.Prefabs
                .Select(prefab => prefab with { EntryIds = Array.AsReadOnly(prefab.EntryIds.ToArray()) })
                .ToArray()));
}
