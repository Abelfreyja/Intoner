using System.Collections.Immutable;

namespace Intoner.Objects.Models;

internal enum ObjectLoadedLayoutKind
{
    Persistent = 1,
    Temporary = 2,
}

internal sealed record ObjectLayoutSnapshot
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public long Revision { get; init; } = 1;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<ObjectFolderSnapshot> Folders
    {
        get;
        init => field = value.ToImmutableArray();
    } = ImmutableArray<ObjectFolderSnapshot>.Empty;
    public IReadOnlyList<SceneGroupSnapshot> Groups
    {
        get;
        init => field = value.ToImmutableArray();
    } = ImmutableArray<SceneGroupSnapshot>.Empty;
    public IReadOnlyList<ObjectSnapshot> Objects
    {
        get;
        init => field = value.ToImmutableArray();
    } = ImmutableArray<ObjectSnapshot>.Empty;
}

internal sealed record ObjectPersistentWorkspaceSnapshot
{
    public IReadOnlyList<ObjectSnapshot> StandaloneObjects
    {
        get;
        init => field = value.ToImmutableArray();
    } = ImmutableArray<ObjectSnapshot>.Empty;
    public IReadOnlyList<SceneGroupSnapshot> StandaloneGroups
    {
        get;
        init => field = value.ToImmutableArray();
    } = ImmutableArray<SceneGroupSnapshot>.Empty;
    public IReadOnlyList<ObjectFolderSnapshot> StandaloneFolders
    {
        get;
        init => field = value.ToImmutableArray();
    } = ImmutableArray<ObjectFolderSnapshot>.Empty;
    public IReadOnlyList<ObjectLayoutSnapshot> Layouts
    {
        get;
        init => field = value.ToImmutableArray();
    } = ImmutableArray<ObjectLayoutSnapshot>.Empty;
    public Guid? CreationLayoutId { get; init; }
    public IEnumerable<ObjectSnapshot> EnumerateObjects()
        => StandaloneObjects.Concat(Layouts.SelectMany(static layout => layout.Objects));
    public long Revision { get; init; }
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
}

internal sealed record ObjectTemporaryLayoutSnapshot
{
    public string SourceKey { get; init; } = string.Empty;
    public Guid SourceSessionId { get; init; }
    public string Name { get; init; } = string.Empty;
    public long Revision { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<ObjectSnapshot> Objects
    {
        get;
        init => field = value.ToImmutableArray();
    } = ImmutableArray<ObjectSnapshot>.Empty;
}

internal sealed record ObjectPersistentSceneUpdate
{
    public long ExpectedRevision { get; init; }
    public IReadOnlyList<ObjectSnapshot> StandaloneObjects { get; init; } = [];
    public IReadOnlyList<SceneGroupSnapshot> StandaloneGroups { get; init; } = [];
    public IReadOnlyList<ObjectFolderSnapshot> StandaloneFolders { get; init; } = [];
    public IReadOnlyList<ObjectLayoutContentUpdate> Layouts { get; init; } = [];
    public Guid? CreationLayoutId { get; init; }
}

internal sealed record ObjectLayoutContentUpdate(
    Guid LayoutId,
    IReadOnlyList<ObjectSnapshot> Objects,
    IReadOnlyList<ObjectFolderSnapshot> Folders)
{
    public required IReadOnlyList<SceneGroupSnapshot> Groups { get; init; }
}

internal sealed record ObjectLoadedLayoutSnapshot
{
    public ObjectLoadedLayoutKind Kind { get; init; }
    public Guid? LayoutId { get; init; }
    public string SourceKey { get; init; } = string.Empty;
    public Guid SourceSessionId { get; init; }
    public string Name { get; init; } = string.Empty;
    public long Revision { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<ObjectSnapshot> Objects
    {
        get;
        init => field = value.ToImmutableArray();
    } = ImmutableArray<ObjectSnapshot>.Empty;
}
