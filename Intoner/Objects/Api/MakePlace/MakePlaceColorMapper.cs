using FFXIVClientStructs.FFXIV.Client.Graphics;
using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Text.Json;

namespace Intoner.Objects.Api;

internal sealed class MakePlaceColorMapper(IFurnitureStainService stainService)
{
    public bool TryResolveStainId(MakePlaceFurnitureDocument furniture, out byte stainId)
    {
        stainId = 0;
        if (!TryGetImportedFurnitureColor(furniture, out byte red, out byte green, out byte blue))
        {
            return false;
        }

        return stainService.TryFindNearestStain(red, green, blue, out stainId);
    }

    public bool TryResolveColorHex(FurnitureColorModel color, out string colorHex)
    {
        colorHex = string.Empty;
        if (color.UseCustomColor)
        {
            colorHex = FormatColor(ObjectColorUtility.ToByteColor(color.CustomColor));
            return true;
        }

        if (color.StainId == 0)
        {
            return false;
        }

        if (stainService.TryResolveStainColor(color.StainId, out ByteColor stainColor))
        {
            colorHex = FormatColor(stainColor);
            return true;
        }

        return false;
    }

    private static bool TryGetImportedFurnitureColor(MakePlaceFurnitureDocument furniture, out byte red, out byte green, out byte blue)
    {
        red = 0;
        green = 0;
        blue = 0;
        return furniture.Properties is not null
               && furniture.Properties.TryGetValue("color", out JsonElement colorElement)
               && colorElement.ValueKind == JsonValueKind.String
               && ObjectColorUtility.TryParseHexBytes(colorElement.GetString(), out red, out green, out blue, out _);
    }

    private static string FormatColor(ByteColor color)
        => $"{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";
}
