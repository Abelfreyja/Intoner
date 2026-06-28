using MessagePack;

namespace Intoner.Objects.Api;

/// <summary> object kind </summary>
public enum WorldObjectKind
{
    Light = 1,
    BgObject = 2,
    Furniture = 4,
    Vfx = 8,
}

/// <summary> light type </summary>
public enum ObjectLightType : uint
{
    WorldLight = 1,
    AreaLight = 2,
    SpotLight = 3,
    FlatLight = 4,
}

/// <summary> light falloff type </summary>
public enum ObjectLightFalloffType : uint
{
    Linear = 0,
    Quadratic = 1,
    Cubic = 2,
}

/// <summary> draw object outline color </summary>
public enum ObjectOutlineColor : byte
{
    None = 0,
    Red = 1,
    Green = 2,
    Blue = 3,
    Yellow = 4,
    Orange = 5,
    Magenta = 6,
    Black = 7,
}

/// <summary> loaded layout type </summary>
public enum LoadedObjectLayoutType
{
    Default = 1,
    Temporary = 2,
}

/// <summary> runtime object state </summary>
public enum RuntimeObjectStateKind
{
    Active = 1,
    Inactive = 2,
    LocationMismatch = 3,
    LoadFailed = 4,
}

/// <summary> temporary source mutation status </summary>
public enum TemporarySourceMutationStatus
{
    Success = 0,
    InvalidSource = 1,
    InvalidObject = 2,
    StaleRevision = 3,
    ObjectNotFound = 4,
    SourceMismatch = 5,
    RuntimeApplyFailed = 6,
}

/// <summary> temporary object change kind </summary>
public enum TemporaryObjectChangeKind
{
    Upsert = 1,
    Remove = 2,
    Patch = 3,
}

/// <summary> object api version </summary>
/// <param name="Breaking"> breaking version for incompatible changes </param>
/// <param name="Feature"> feature version for non breaking changes </param>
[MessagePackObject(keyAsPropertyName: true)]
public readonly record struct ObjectApiVersion(int Breaking, int Feature);

/// <summary> 2d float vector </summary>
/// <param name="X"> x component </param>
/// <param name="Y"> y component </param>
[MessagePackObject(keyAsPropertyName: true)]
public readonly record struct ObjectVector2(float X, float Y);

/// <summary> 3d float vector </summary>
/// <param name="X"> x component </param>
/// <param name="Y"> y component </param>
/// <param name="Z"> z component </param>
[MessagePackObject(keyAsPropertyName: true)]
public readonly record struct ObjectVector3(float X, float Y, float Z);

/// <summary> 4d float vector </summary>
/// <param name="X"> x component </param>
/// <param name="Y"> y component </param>
/// <param name="Z"> z component </param>
/// <param name="W"> w component </param>
[MessagePackObject(keyAsPropertyName: true)]
public readonly record struct ObjectVector4(float X, float Y, float Z, float W);

/// <summary> saved object location context </summary>
/// <param name="WorldId"> world id, or '0' to refresh from the current location on local create or import </param>
/// <param name="WorldName"> world display name </param>
/// <param name="TerritoryId"> territory id, or '0' to refresh from the current location on local create or import </param>
/// <param name="TerritoryName"> territory display name </param>
/// <param name="DivisionId"> housing division id for the saved location </param>
/// <param name="WardId"> housing ward id for the saved location </param>
/// <param name="HouseId"> housing plot or apartment if id is 100 (thank me later) </param>
/// <param name="RoomId"> apartment or housing room id for the saved location </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ObjectCreationData(
    ushort WorldId,
    string WorldName,
    uint TerritoryId,
    string TerritoryName,
    uint DivisionId,
    uint WardId,
    uint HouseId,
    uint RoomId);

/// <summary> object transform </summary>
/// <param name="Position"> world position </param>
/// <param name="RotationDegrees"> euler rotation in degrees </param>
/// <param name="Scale"> local scale multiplier </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record WorldObjectTransform(
    ObjectVector3 Position,
    ObjectVector3 RotationDegrees,
    ObjectVector3 Scale);

/// <summary> current local object location context </summary>
/// <param name="WorldId"> current local world id </param>
/// <param name="TerritoryId"> current local territory id </param>
/// <param name="WorldName"> current local world display name </param>
/// <param name="TerritoryName"> current local territory display name </param>
/// <param name="DivisionId"> current local housing division id </param>
/// <param name="WardId"> current local housing ward id </param>
/// <param name="HouseId"> current local housing plot or apartment if id is 100 (thank me later) </param>
/// <param name="RoomId"> current local apartment or housing room id </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ObjectLocationData(
    ushort WorldId,
    uint TerritoryId,
    string WorldName,
    string TerritoryName,
    uint DivisionId,
    uint WardId,
    uint HouseId,
    uint RoomId);

