using Intoner.Scene;

namespace Intoner.Objects.Models;

internal abstract record ObjectSceneItemSnapshot : SceneItemSnapshot
{
    public Guid? LayoutId { get; init; }
    public string FolderPath { get; init; } = string.Empty;
}
