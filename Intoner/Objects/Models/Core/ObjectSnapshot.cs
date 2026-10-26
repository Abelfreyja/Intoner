namespace Intoner.Objects.Models;

internal sealed record ObjectSnapshot : ObjectSceneItemSnapshot
{
    public Guid? GroupId { get; init; }
    public string CollectionId { get; init; } = string.Empty;
    public ObjectKind Kind => Model switch
    {
        BgObjectModel => ObjectKind.BgObject,
        FurnitureModel => ObjectKind.Furniture,
        VfxModel => ObjectKind.Vfx,
        LightModel => ObjectKind.Light,
        _ => default,
    };
    public required ObjectData Model { get; init; }
}
