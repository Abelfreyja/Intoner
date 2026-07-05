using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Text.Json;

namespace Intoner.Objects.Api;

internal static class MakePlaceMaterialMapper
{
    private const string MaterialPropertyName = "material";

    public static bool TryResolveMaterial(MakePlaceFurnitureDocument furniture, out FurnitureMaterialItemModel material)
    {
        material = null!;
        if (furniture.Properties is null
            || !furniture.Properties.TryGetValue(MaterialPropertyName, out JsonElement materialElement)
            || materialElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        MakePlaceBasicItemDocument? materialItem;
        try
        {
            materialItem = materialElement.Deserialize<MakePlaceBasicItemDocument>(MakePlaceJsonSerializer.JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (materialItem is null || materialItem.ItemId == 0)
        {
            return false;
        }

        material = new FurnitureMaterialItemModel
        {
            Name = ObjectStringUtility.TrimOrEmpty(materialItem.Name),
            ItemId = materialItem.ItemId,
        };
        return true;
    }

    public static void AddMaterialProperty(Dictionary<string, JsonElement> properties, FurnitureMaterialItemModel? material)
    {
        if (material is not { ItemId: not 0 })
        {
            return;
        }

        properties[MaterialPropertyName] = JsonSerializer.SerializeToElement(
            new MakePlaceBasicItemDocument
            {
                Name = ObjectStringUtility.TrimOrEmpty(material.Name),
                ItemId = material.ItemId,
            },
            MakePlaceJsonSerializer.JsonOptions);
    }
}
