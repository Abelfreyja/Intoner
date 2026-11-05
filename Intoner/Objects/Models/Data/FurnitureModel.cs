using System.Numerics;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Models;

internal sealed record FurnitureColorModel
{
    public byte StainId { get; init; }
    public bool UseCustomColor { get; init; }
    public Vector4 CustomColor { get; init; } = Vector4.One;

    public bool HasManualColor
        => UseCustomColor || StainId != 0;
}

internal sealed record FurnitureModel : ObjectData
{
    internal override ObjectKind Kind => ObjectKind.Furniture;

    public string SharedGroupPath { get; init; } = string.Empty;
    public bool CameraCollision { get; init; } = true;
    public bool SoundEnabled { get; init; } = true;
    public FurnitureColorModel Color { get; init; } = new();
    public float Transparency { get; init; }
    public ObjectOutlineColor OutlineColor { get; init; }
    public uint HousingRowId { get; init; }
    public uint ItemRowId { get; init; }
    public Guid? AttachmentParentId { get; init; }
    public FurnitureMaterialItemModel? MaterialItem { get; init; }

    public bool NeedsVisualState(FurnitureModel? previousModel)
    {
        if (previousModel is null)
        {
            return MaterialItem is not null || Color.HasManualColor
                || !NumericsUtility.IsNearlyZero(Transparency) || OutlineColor != ObjectOutlineColor.None;
        }

        return MaterialItem?.ItemId != previousModel.MaterialItem?.ItemId
            || Color.UseCustomColor != previousModel.Color.UseCustomColor
            || (Color.UseCustomColor
                ? !NumericsUtility.IsNearlyEqual(Color.CustomColor, previousModel.Color.CustomColor)
                : Color.StainId != previousModel.Color.StainId)
            || !NumericsUtility.IsNearlyEqual(previousModel.Transparency, Transparency)
            || previousModel.OutlineColor != OutlineColor;
    }

    public bool RequiresRecreateToClearColor(FurnitureModel previousModel)
        => previousModel.Color.HasManualColor && !Color.HasManualColor;
}
