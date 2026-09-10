namespace Intoner.Objects.Models;

internal enum ObjectLoadedLayoutKind
{
    Default = 1,
    Temporary = 2,
}

internal sealed record ObjectLayoutSnapshot
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public long Revision { get; init; } = 1;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<ObjectFolderSnapshot> Folders { get; init; } = [];
    public IReadOnlyList<ObjectSnapshot> Objects { get; init; } = [];
}

internal sealed record ObjectPersistentWorkspaceSnapshot
{
    public IReadOnlyList<ObjectSnapshot> Objects { get; init; } = [];
    public IReadOnlyList<ObjectFolderSnapshot> StandaloneFolders { get; init; } = [];
    public IReadOnlyList<ObjectFolderSnapshot> DefaultLayoutFolders { get; init; } = [];
    public Guid? DefaultLayoutId { get; init; }
    public string Name { get; init; } = string.Empty;
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
    public IReadOnlyList<ObjectSnapshot> Objects { get; init; } = [];
}

internal sealed record ObjectPersistentSceneUpdate
{
    public long ExpectedRevision { get; init; }
    public IReadOnlyList<ObjectSnapshot> StandaloneObjects { get; init; } = [];
    public IReadOnlyList<ObjectFolderSnapshot> StandaloneFolders { get; init; } = [];
    public Guid? DefaultLayoutId { get; init; }
    public IReadOnlyList<ObjectSnapshot> DefaultLayoutObjects { get; init; } = [];
    public IReadOnlyList<ObjectFolderSnapshot> DefaultLayoutFolders { get; init; } = [];
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
    public IReadOnlyList<ObjectSnapshot> Objects { get; init; } = [];
}

