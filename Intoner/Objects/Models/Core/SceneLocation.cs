using Intoner.Objects.Api;
using Intoner.Objects.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Intoner.Scene;

/// <summary> world and data center names resolved from the game sheets </summary>
internal sealed record SceneWorldInfo(ushort Id, string Name, uint DataCenterId, string DataCenterName);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneLocationAddress(uint TerritoryId, uint DivisionId, uint WardId, uint HouseId, uint RoomId);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneLocationScope(
    ushort WorldId,
    uint TerritoryId,
    uint DivisionId,
    uint WardId,
    uint HouseId,
    uint RoomId)
{
    public bool IsValid => WorldId != 0 && TerritoryId != 0;
    public SceneLocationAddress Address => ObjectHousingAddress.Normalize(new(TerritoryId, DivisionId, WardId, HouseId, RoomId));
}

internal sealed record SceneCreationContext
{
    public ObjectWorldSelection? Worlds { get; init; }
    public uint TerritoryId { get; init; }
    public string TerritoryName { get; init; } = string.Empty;
    public uint DivisionId { get; init; }
    public uint WardId { get; init; }
    public uint HouseId { get; init; }
    public uint RoomId { get; init; }

    [JsonIgnore]
    public SceneLocationAddress Address => ObjectHousingAddress.Normalize(new(TerritoryId, DivisionId, WardId, HouseId, RoomId));

    [JsonIgnore]
    public bool IsAssigned => Worlds is not null && TerritoryId != 0;

    [JsonIgnore]
    public bool HasLocation => Worlds is not null || Address != default || !string.IsNullOrWhiteSpace(TerritoryName);

    public static SceneCreationContext FromScope(SceneLocationScope scope, string territoryName)
        => new()
        {
            Worlds = scope.WorldId == 0 ? null : new(false, worldIds: [scope.WorldId]),
            TerritoryId = scope.TerritoryId,
            TerritoryName = territoryName,
            DivisionId = scope.DivisionId,
            WardId = scope.WardId,
            HouseId = scope.HouseId,
            RoomId = scope.RoomId,
        };

    public bool HasSameLocation(SceneCreationContext other)
        => Address == other.Address && Worlds == other.Worlds;

    public bool MatchesLocation(SceneLocationScope current, uint dataCenterId)
        => !HasLocation || current.IsValid && Address == current.Address && Worlds?.Matches(current.WorldId, dataCenterId) == true;
}

internal sealed record SceneLocationChange(ObjectWorldSelection? Worlds = null, SceneCreationContext? HousingAddress = null)
{
    public SceneCreationContext Apply(SceneCreationContext original)
        => (HousingAddress ?? original) with
        {
            Worlds = Worlds ?? original.Worlds,
            TerritoryName = HousingAddress is null || HousingAddress.TerritoryId == original.TerritoryId
                ? original.TerritoryName : HousingAddress.TerritoryName,
        };
}
