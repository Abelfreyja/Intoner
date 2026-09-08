using Intoner.Scene;
namespace Intoner.Objects.Models;

internal sealed record ObjectSnapshotPatch
{
    public string? Name { get; init; }
    public string? FolderPath { get; init; }
    public bool? Locked { get; init; }
    public bool? Visible { get; init; }
    public SceneTransform? Transform { get; init; }
    public ObjectKind? ModelKind { get; init; }
    public ObjectDataPatch? Model { get; init; }

    public bool HasChanges
        => Name is not null
           || FolderPath is not null
           || Locked.HasValue
           || Visible.HasValue
           || Transform is not null
           || Model?.HasChanges == true;
}

