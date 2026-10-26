using System.Numerics;

namespace Intoner.Objects.Models;

internal sealed record ObjectPlacementOverrides
{
    public string? Name { get; init; }
    public bool? Visible { get; init; }
    public string? FolderPath { get; init; }
    public Vector3? Scale { get; init; }
    public ObjectData? Model { get; init; }
}
