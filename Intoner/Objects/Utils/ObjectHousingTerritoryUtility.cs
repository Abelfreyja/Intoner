using Dalamud.Plugin.Services;
using Intoner.Objects.Filesystem.Configuration;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Intoner.Objects.Utils;

internal static class ObjectHousingTerritoryUtility
{
    public static string NormalizeHouseSize(string? houseSize)
        => string.IsNullOrWhiteSpace(houseSize)
            ? string.Empty
            : houseSize.Trim() switch
            {
                "Small" => "Small",
                "Medium" => "Medium",
                "Large" => "Large",
                "Apartment" => "Apartment",
                _ => string.Empty,
            };

    public static bool TryResolveIndoorHousingSize(IDataManager gameData, uint territoryId, out ObjectHousingSize houseSize)
    {
        houseSize = default;
        if (territoryId == 0)
        {
            return false;
        }

        ExcelSheet<TerritoryType>? sheet = gameData.GetExcelSheet<TerritoryType>(gameData.Language);
        if (sheet == null || !sheet.TryGetRow(territoryId, out TerritoryType row))
        {
            return false;
        }

        string placeName = row.Name.ToString();
        if (string.IsNullOrWhiteSpace(placeName) || placeName.Length < 4)
        {
            return false;
        }

        string sizeToken = placeName.Substring(1, 3);
        return TryResolveIndoorSizeToken(sizeToken, out houseSize);
    }

    private static bool TryResolveIndoorSizeToken(string sizeToken, out ObjectHousingSize houseSize)
    {
        (ObjectHousingSize Size, bool Success) result = sizeToken switch
        {
            "1i1" => (ObjectHousingSize.Small, true),
            "1i2" => (ObjectHousingSize.Medium, true),
            "1i3" => (ObjectHousingSize.Large, true),
            "1i4" => (ObjectHousingSize.Apartment, true),
            _     => (default, false),
        };
        houseSize = result.Size;
        return result.Success;
    }
}

