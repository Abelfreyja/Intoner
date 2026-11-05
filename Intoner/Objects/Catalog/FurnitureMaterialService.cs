using Intoner.Objects.Assets;
using Intoner.Objects.Models;
using Lumina.Excel.Sheets;

namespace Intoner.Objects.Catalog;

internal sealed record FurnitureMaterialOption(uint ItemId, string Name, ushort Variant, uint IconId)
{
    public FurnitureMaterialItemModel ToModel()
        => new() { ItemId = ItemId, Name = Name };
}

/// <summary> resolves wallpaper items to the game's furniture material variants </summary>
internal sealed class FurnitureMaterialService(IObjectAssetGameData gameData)
{
    private readonly Lazy<IReadOnlyList<FurnitureMaterialOption>> _wallpapers = new(() => BuildWallpapers(gameData));

    private readonly Lazy<HashSet<string>> _wallpaperGroups = new(() => BuildWallpaperGroups(gameData));

    public IReadOnlyList<FurnitureMaterialOption> Wallpapers => _wallpapers.Value;

    public bool IsWallpaperFurniture(FurnitureModel model)
        => _wallpaperGroups.Value.Contains(model.SharedGroupPath);

    private static HashSet<string> BuildWallpaperGroups(IObjectAssetGameData gameData)
    {
        var sheet = gameData.GetExcelSheet<HousingFurniture>()
            ?? throw new InvalidOperationException("Furniture material data is unavailable.");
        return sheet.Where(static row => row.UsageType == 12 && row.UsageParameter == 3)
            .Select(static row => GameDataAssetPathUtility.BuildIndoorHousingSharedGroupPath(row.ModelKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool TryResolve(FurnitureModel model, out FurnitureMaterialOption? material)
    {
        material = null;
        if (model.MaterialItem is not { } selection || !IsWallpaperFurniture(model))
        {
            return true;
        }

        bool resolved = TryResolve(selection.ItemId, out FurnitureMaterialOption option);
        material = resolved ? option : null;
        return resolved;
    }

    public bool TryResolve(uint itemId, out FurnitureMaterialOption material)
    {
        foreach (FurnitureMaterialOption candidate in Wallpapers)
        {
            if (candidate.ItemId == itemId)
            {
                material = candidate;
                return true;
            }
        }

        material = null!;
        return false;
    }

    private static IReadOnlyList<FurnitureMaterialOption> BuildWallpapers(IObjectAssetGameData gameData)
    {
        var sheet = gameData.GetCurrentLanguageExcelSheet<VaseFlower>()
            ?? throw new InvalidOperationException("Wallpaper material data is unavailable.");
        List<FurnitureMaterialOption> wallpapers = [];
        foreach (VaseFlower row in sheet)
        {
            if (row.RowId <= 2000 || row.RowId >= 3000 || row.Unknown0 is 0 or > 9999
                || row.Item.ValueNullable is not { } item)
            {
                continue;
            }

            wallpapers.Add(new FurnitureMaterialOption(item.RowId, item.Name.ExtractText(), row.Unknown0, item.Icon));
        }

        return wallpapers.OrderBy(static option => option.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
