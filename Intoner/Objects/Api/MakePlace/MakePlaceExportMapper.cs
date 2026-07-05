using Intoner.Objects.Catalog;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using System.Text.Json;

namespace Intoner.Objects.Api;

internal sealed class MakePlaceExportMapper(
    FurnitureCatalogResolver furnitureCatalog,
    MakePlaceColorMapper colorMapper)
{
    public bool TryBuildExportDocument(
        ObjectLayoutSnapshot layout,
        ObjectRuntimeLocationContext currentLocation,
        out MakePlaceLayoutDocument document,
        out string successMessage,
        out string errorMessage)
    {
        document = null!;
        successMessage = string.Empty;
        if (!LayoutTransferContext.TryResolve(currentLocation, "MakePlace", "export", out LayoutTransferContext areaContext, out errorMessage))
        {
            return false;
        }

        List<MakePlaceFurnitureDocument> furniture = BuildFurnitureTree(layout.Objects, areaContext, out int exportedFurnitureCount, out int skippedFurnitureCount);
        if (furniture.Count == 0)
        {
            errorMessage = $"The selected layout does not contain any {areaContext.AreaLabel} furniture that can be exported to MakePlace.";
            return false;
        }

        document = new MakePlaceLayoutDocument
        {
            HouseSize = areaContext.HouseSize,
            InteriorScale = 1f,
            InteriorFurniture = areaContext.Area == ObjectHousingArea.Indoor ? furniture : [],
            ExteriorScale = 1f,
            ExteriorFurniture = areaContext.Area == ObjectHousingArea.Outdoor ? furniture : [],
        };
        successMessage = BuildExportStatus(layout.Name, exportedFurnitureCount, skippedFurnitureCount, areaContext);
        errorMessage = string.Empty;
        return true;
    }

    private List<MakePlaceFurnitureDocument> BuildFurnitureTree(
        IReadOnlyList<ObjectSnapshot> snapshots,
        LayoutTransferContext areaContext,
        out int exportedFurnitureCount,
        out int skippedFurnitureCount)
    {
        Dictionary<Guid, ExportedFurnitureNode> exportedById = [];
        skippedFurnitureCount = 0;
        foreach (ObjectSnapshot snapshot in snapshots)
        {
            if (TryCreateFurnitureDocument(snapshot, areaContext, out FurnitureModel? furnitureModel, out MakePlaceFurnitureDocument document))
            {
                exportedById[snapshot.Id] = new ExportedFurnitureNode(furnitureModel, document);
                continue;
            }

            if (snapshot.Model is FurnitureModel)
            {
                skippedFurnitureCount++;
            }
        }

        exportedFurnitureCount = exportedById.Count;
        List<MakePlaceFurnitureDocument> roots = [];
        foreach (ObjectSnapshot snapshot in snapshots)
        {
            if (!exportedById.TryGetValue(snapshot.Id, out ExportedFurnitureNode? node))
            {
                continue;
            }

            if (node.Furniture.AttachmentParentId is { } parentId
                && exportedById.TryGetValue(parentId, out ExportedFurnitureNode? parentNode))
            {
                parentNode.Document.Attachments.Add(node.Document);
                continue;
            }

            roots.Add(node.Document);
        }

        return roots;
    }

    private bool TryCreateFurnitureDocument(
        ObjectSnapshot snapshot,
        LayoutTransferContext areaContext,
        out FurnitureModel furnitureModel,
        out MakePlaceFurnitureDocument document)
    {
        document = null!;
        if (!furnitureCatalog.TryResolve(snapshot, areaContext.FurnitureArea, out FurnitureModel? resolvedFurnitureModel, out FurnitureCatalogMatch? match))
        {
            furnitureModel = null!;
            return false;
        }

        furnitureModel = resolvedFurnitureModel;
        document = new MakePlaceFurnitureDocument
        {
            Name = match.DisplayName,
            ItemId = furnitureModel.ItemRowId,
            Transform = MakePlaceTransformMapper.ToMakePlaceTransform(snapshot.Transform, 1f, areaContext.PlotBasis),
            Properties = BuildProperties(furnitureModel),
        };
        return true;
    }

    private Dictionary<string, JsonElement> BuildProperties(FurnitureModel furniture)
    {
        Dictionary<string, JsonElement> properties = new(StringComparer.OrdinalIgnoreCase);
        if (colorMapper.TryResolveColorHex(furniture.Color, out string colorHex))
        {
            properties["color"] = JsonSerializer.SerializeToElement(colorHex, MakePlaceJsonSerializer.JsonOptions);
        }

        MakePlaceMaterialMapper.AddMaterialProperty(properties, furniture.MaterialItem);
        return properties;
    }

    private static string BuildExportStatus(
        string layoutName,
        int exportedCount,
        int skippedFurnitureCount,
        LayoutTransferContext areaContext)
    {
        string message = $"Exported MakePlace layout '{layoutName}' with {exportedCount} {areaContext.AreaLabel} furniture for the current {areaContext.ScopeLabel} area.";
        if (skippedFurnitureCount > 0)
        {
            message += $" Skipped {skippedFurnitureCount} unsupported or non-current-area furniture entries.";
        }

        return message;
    }

    private sealed record ExportedFurnitureNode(FurnitureModel Furniture, MakePlaceFurnitureDocument Document);
}
