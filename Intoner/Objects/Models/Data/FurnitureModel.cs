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
    public string SharedGroupPath { get; init; } = string.Empty;
    public FurnitureColorModel Color { get; init; } = new();
    public float Transparency { get; init; }
    public ObjectOutlineColor OutlineColor { get; init; }
    public uint HousingRowId { get; init; }
    public uint ItemRowId { get; init; }
    public Guid? AttachmentParentId { get; init; }
    public FurnitureMaterialItemModel? MaterialItem { get; init; }

    public bool NeedsVisualState(FurnitureModel? previousModel)
    {
        if (Color.HasManualColor || !ObjectMathUtility.IsNearlyZero(Transparency) || OutlineColor != ObjectOutlineColor.None)
        {
            return true;
        }

        return previousModel is not null
            && (previousModel.Color.HasManualColor
                || !ObjectMathUtility.IsNearlyEqual(previousModel.Transparency, Transparency)
                || previousModel.OutlineColor != OutlineColor);
    }

    public bool RequiresRecreateToClearColor(FurnitureModel previousModel)
        => previousModel.Color.HasManualColor && !Color.HasManualColor;
}

