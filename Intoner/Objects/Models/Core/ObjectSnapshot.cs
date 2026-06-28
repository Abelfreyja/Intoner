namespace Intoner.Objects.Models;

internal sealed record ObjectSnapshot
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string FolderPath { get; init; } = string.Empty;
    public string CollectionId { get; init; } = string.Empty;
    public ObjectKind Kind { get; init; }
    public Guid? LayoutId { get; init; }
    public bool Locked { get; init; }
    public bool Visible { get; init; } = true;
    public ObjectTransform Transform { get; init; } = new();
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public ObjectCreationContext CreatedIn { get; init; } = new();
    public required ObjectData Model { get; init; }
}

