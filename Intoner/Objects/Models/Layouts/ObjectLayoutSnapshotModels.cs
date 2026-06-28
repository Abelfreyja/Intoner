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
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<string> Folders { get; init; } = [];
    public IReadOnlyDictionary<string, string> FolderColors { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<ObjectSnapshot> Objects { get; init; } = [];
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

internal sealed record ObjectPersistentWorkspaceSnapshot
{
    public IReadOnlyList<ObjectSnapshot> Objects { get; init; } = [];
    public IReadOnlyList<string> Folders { get; init; } = [];
    public IReadOnlyDictionary<string, string> FolderColors { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public Guid? DefaultLayoutId { get; init; }
    public string Name { get; init; } = string.Empty;
    public long Revision { get; init; }
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
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

