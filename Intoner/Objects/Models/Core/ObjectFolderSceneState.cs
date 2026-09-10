namespace Intoner.Objects.Models;

internal sealed record ObjectFolderSceneState
{
    public IReadOnlyList<ObjectFolderSnapshot> StandaloneFolders { get; init; } = [];
    public Guid? DefaultLayoutId { get; init; }
    public IReadOnlyList<ObjectFolderSnapshot> DefaultLayoutFolders { get; init; } = [];
}

