using Intoner.Scene;

namespace Intoner.Objects.Models;

internal sealed record ObjectSnapshot : SceneItemSnapshot
{
    public string FolderPath { get; init; } = string.Empty;
    public Guid? LayoutId { get; init; }
    public string CollectionId { get; init; } = string.Empty;
    public ObjectKind Kind { get; init; }
    public required ObjectData Model { get; init; }
}

