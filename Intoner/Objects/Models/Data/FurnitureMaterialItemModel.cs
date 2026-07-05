namespace Intoner.Objects.Models;

internal sealed record FurnitureMaterialItemModel
{
    public string Name { get; init; } = string.Empty;
    public uint ItemId { get; init; }
}
