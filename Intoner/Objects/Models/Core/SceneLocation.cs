using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Intoner.Scene;

/// <summary> world and data center names resolved from the game sheets </summary>
internal sealed record SceneWorldInfo(ushort Id, string Name, uint DataCenterId, string DataCenterName);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneLocationScope(
    ushort WorldId,
    uint TerritoryId,
    uint DivisionId,
    uint WardId,
    uint HouseId,
    uint RoomId)
{
    public bool IsValid
        => WorldId != 0 && TerritoryId != 0;
}

internal sealed record SceneCreationContext
{
    public ushort WorldId { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public uint TerritoryId { get; init; }
    public string TerritoryName { get; init; } = string.Empty;
    public uint DivisionId { get; init; }
    public uint WardId { get; init; }
    public uint HouseId { get; init; }
    public uint RoomId { get; init; }

    [JsonIgnore]
    public SceneLocationScope Scope
        => new(WorldId, TerritoryId, DivisionId, WardId, HouseId, RoomId);

    [JsonIgnore]
    public bool IsValid
        => Scope.IsValid
           || !string.IsNullOrWhiteSpace(WorldName)
           || !string.IsNullOrWhiteSpace(TerritoryName);
}

