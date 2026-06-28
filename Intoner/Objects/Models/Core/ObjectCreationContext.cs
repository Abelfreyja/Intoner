using System.Text.Json.Serialization;

namespace Intoner.Objects.Models;

internal sealed record ObjectCreationContext
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
    public ObjectLocationScope Scope
        => new(WorldId, TerritoryId, DivisionId, WardId, HouseId, RoomId);

    [JsonIgnore]
    public bool IsValid
        => Scope.IsValid
           || !string.IsNullOrWhiteSpace(WorldName)
           || !string.IsNullOrWhiteSpace(TerritoryName);
}

