using Dalamud.Plugin.Services;
using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using System.Text.Json;

namespace Intoner.Objects.Api;

internal sealed class ObjectMakePlaceImportMapper(
    IObjectCatalogService catalogService,
    IObjectKindService objectKindService,
    IFramework framework)
{
    private IndoorFurnitureLookup? _indoorFurnitureLookup;
    private IReadOnlyList<StainSwatch>? _stainSwatches;

    public bool TryBuildImportPayload(
        ObjectMakePlaceLayoutDocument document,
        string sourcePath,
        ObjectCreationContext currentContext,
        string currentHouseSize,
        out ObjectLayoutImportPayload payload,
        out string errorMessage)
    {
        payload = null!;

        var documentHouseSize = ObjectHousingTerritoryUtility.NormalizeHouseSize(document.HouseSize);
        if (!string.IsNullOrWhiteSpace(documentHouseSize)
            && !string.Equals(documentHouseSize, currentHouseSize, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = $"This MakePlace design is for a {documentHouseSize} interior, but the current house is {currentHouseSize}.";
            return false;
        }

        var interiorFurniture = ObjectMakePlaceLayoutUtility.FlattenFurnitureTree(document.InteriorFurniture ?? []);
        var ignoredExteriorCount = ObjectMakePlaceLayoutUtility.CountFurnitureTree(document.ExteriorFurniture ?? []);
        if (interiorFurniture.Count == 0)
        {
            errorMessage = ignoredExteriorCount > 0
                ? "This MakePlace design only contains exterior furniture, which is not supported yet."
                : "The selected MakePlace design does not contain any interior furniture.";
            return false;
        }

        var snapshots = BuildSnapshots(interiorFurniture, document.InteriorScale, currentContext, out var skippedFurnitureCount);
        if (snapshots.Count == 0)
        {
            errorMessage = "The selected MakePlace design did not contain any furniture that could be mapped to the Intoner furniture catalog.";
            return false;
        }

        var importedName = ObjectLayoutFileUtility.ResolveImportedLayoutName(Path.GetFileNameWithoutExtension(sourcePath), sourcePath);
        payload = new ObjectLayoutImportPayload(
            importedName,
            snapshots,
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BuildImportStatus(importedName, snapshots.Count, skippedFurnitureCount, ignoredExteriorCount, currentHouseSize));
        errorMessage = string.Empty;
        return true;
    }

    private List<ObjectSnapshot> BuildSnapshots(
        IReadOnlyList<ObjectMakePlaceFurnitureNode> furnitureEntries,
        float interiorScale,
        ObjectCreationContext currentContext,
        out int skippedFurnitureCount)
    {
        var snapshots = new List<ObjectSnapshot>(furnitureEntries.Count);
        Dictionary<int, Guid> snapshotIdsByNodeIndex = [];
        skippedFurnitureCount = 0;

        for (var index = 0; index < furnitureEntries.Count; ++index)
        {
            var entry = furnitureEntries[index];
            Guid? attachmentParentId = entry.HasParent && snapshotIdsByNodeIndex.TryGetValue(entry.ParentIndex, out Guid parentId)
                ? parentId
                : null;

            if (TryCreateSnapshot(entry.Furniture, interiorScale, currentContext, attachmentParentId, out var snapshot))
            {
                snapshots.Add(snapshot);
                snapshotIdsByNodeIndex[index] = snapshot.Id;
                continue;
            }

            skippedFurnitureCount++;
        }

        return snapshots;
    }

    private bool TryCreateSnapshot(
        ObjectMakePlaceFurnitureDocument furniture,
        float interiorScale,
        ObjectCreationContext currentContext,
        Guid? attachmentParentId,
        out ObjectSnapshot snapshot)
    {
        snapshot = null!;
        if (!TryResolveIndoorFurnitureMatch(furniture, out var match))
        {
            return false;
        }

        if (!ObjectMakePlaceUtility.TryConvertTransform(furniture.Transform, interiorScale, out var transform))
        {
            return false;
        }

        var stainId = TryResolveStainId(furniture, out var resolvedStainId)
            ? resolvedStainId
            : (byte)0;

        var nextSnapshot = new ObjectSnapshot
        {
            Id = Guid.NewGuid(),
            Name = ObjectStringUtility.TrimOrFallback(furniture.Name, match.DisplayName),
            Kind = ObjectKind.Furniture,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedIn = currentContext,
            Transform = transform,
            Model = new FurnitureModel
            {
                SharedGroupPath = match.Entry.PlacementPath,
                HousingRowId = match.Variant.HousingRowId,
                ItemRowId = match.Variant.ItemRowId,
                AttachmentParentId = attachmentParentId,
                Color = new FurnitureColorModel
                {
                    StainId = stainId,
                },
            },
        };

        if (!objectKindService.TrySanitizeSnapshot(nextSnapshot, out snapshot))
        {
            snapshot = null!;
            return false;
        }

        return true;
    }

    private bool TryResolveIndoorFurnitureMatch(ObjectMakePlaceFurnitureDocument furniture, out IndoorFurnitureMatch match)
    {
        var lookup = GetIndoorFurnitureLookup();

        match = null!;
        if (furniture.ItemId != 0 && lookup.ByItemId.TryGetValue(furniture.ItemId, out match!))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(furniture.Name)
            && lookup.ByName.TryGetValue(ObjectStringUtility.TrimOrEmpty(furniture.Name), out match!))
        {
            return true;
        }

        return false;
    }

    private IndoorFurnitureLookup GetIndoorFurnitureLookup()
    {
        if (_indoorFurnitureLookup is not null)
        {
            return _indoorFurnitureLookup;
        }

        var byItemId = new Dictionary<uint, IndoorFurnitureMatch>();
        var byName = new Dictionary<string, IndoorFurnitureMatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalogService.GetCatalog().Furniture.Entries)
        {
            if (entry.FurnitureInfo is null
                || string.IsNullOrWhiteSpace(entry.PlacementPath)
                || !entry.PlacementPath.StartsWith("bgcommon/hou/indoor/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (ObjectCatalogFurnitureVariant variant in entry.FurnitureInfo.Variants)
            {
                IndoorFurnitureMatch match = new(entry, variant);
                if (variant.ItemRowId != 0)
                {
                    byItemId.TryAdd(variant.ItemRowId, match);
                }

                if (!string.IsNullOrWhiteSpace(variant.Name))
                {
                    byName.TryAdd(ObjectStringUtility.TrimOrEmpty(variant.Name), match);
                }
            }

            if (!string.IsNullOrWhiteSpace(entry.Name))
            {
                IndoorFurnitureMatch primaryMatch = new(entry, entry.FurnitureInfo.PrimaryVariant);
                byName.TryAdd(ObjectStringUtility.TrimOrEmpty(entry.Name), primaryMatch);
            }
        }

        _indoorFurnitureLookup = new IndoorFurnitureLookup(byItemId, byName);
        return _indoorFurnitureLookup;
    }

    private IReadOnlyList<StainSwatch> GetStainSwatches()
    {
        if (_stainSwatches is not null)
        {
            return _stainSwatches;
        }

        IReadOnlyList<FurnitureStainColor> nativeStainColors = FurnitureStainColorUtility.CaptureNativeColors(framework);
        List<StainSwatch> swatches = new(nativeStainColors.Count);
        foreach (FurnitureStainColor nativeStainColor in nativeStainColors)
        {
            swatches.Add(new StainSwatch(
                nativeStainColor.StainId,
                nativeStainColor.Color.R,
                nativeStainColor.Color.G,
                nativeStainColor.Color.B));
        }

        _stainSwatches = swatches;
        return _stainSwatches;
    }

    private bool TryResolveStainId(ObjectMakePlaceFurnitureDocument furniture, out byte stainId)
    {
        stainId = 0;
        if (!TryGetImportedFurnitureColor(furniture, out var red, out var green, out var blue))
        {
            return false;
        }

        var bestDistance = int.MaxValue;
        foreach (var swatch in GetStainSwatches())
        {
            var distance = ObjectColorUtility.ComputeRgbDistanceSquared(red, green, blue, swatch.Red, swatch.Green, swatch.Blue);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            stainId = swatch.StainId;
        }

        return stainId != 0;
    }

    private static bool TryGetImportedFurnitureColor(ObjectMakePlaceFurnitureDocument furniture, out byte red, out byte green, out byte blue)
    {
        red = 0;
        green = 0;
        blue = 0;
        return furniture.Properties is not null
               && furniture.Properties.TryGetValue("color", out var colorElement)
               && colorElement.ValueKind == JsonValueKind.String
               && ObjectColorUtility.TryParseHexBytes(colorElement.GetString(), out red, out green, out blue, out _);
    }

    private static string BuildImportStatus(string layoutName, int importedCount, int skippedFurnitureCount, int ignoredExteriorCount, string houseSize)
    {
        var message = $"Imported MakePlace layout '{layoutName}' with {importedCount} interior furniture for the current {houseSize} house.";
        if (skippedFurnitureCount > 0)
        {
            message += $" Skipped {skippedFurnitureCount} unsupported furniture entries.";
        }

        if (ignoredExteriorCount > 0)
        {
            message += $" Ignored {ignoredExteriorCount} exterior furniture entries.";
        }

        return message;
    }

    private sealed record IndoorFurnitureLookup(
        IReadOnlyDictionary<uint, IndoorFurnitureMatch> ByItemId,
        IReadOnlyDictionary<string, IndoorFurnitureMatch> ByName);

    private sealed record IndoorFurnitureMatch(ObjectCatalogEntry Entry, ObjectCatalogFurnitureVariant Variant)
    {
        public string DisplayName
            => ObjectStringUtility.TrimOrFallback(Variant.Name, Entry.Name);
    }

    private readonly record struct StainSwatch(byte StainId, byte Red, byte Green, byte Blue);
}

