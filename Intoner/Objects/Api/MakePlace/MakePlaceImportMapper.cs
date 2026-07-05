using Intoner.Objects.Catalog;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Api;

internal sealed class MakePlaceImportMapper(
    IObjectKindService objectKindService,
    FurnitureCatalogResolver furnitureCatalog,
    MakePlaceColorMapper colorMapper)
{
    public bool TryBuildImportPayload(
        MakePlaceLayoutDocument document,
        string sourcePath,
        ObjectRuntimeLocationContext currentLocation,
        out ObjectLayoutImportPayload payload,
        out string errorMessage)
    {
        payload = null!;
        if (!LayoutTransferContext.TryResolve(currentLocation, "MakePlace", "import", out LayoutTransferContext areaContext, out errorMessage))
        {
            return false;
        }

        string documentHouseSize = ObjectHousingTerritoryUtility.NormalizeHouseSize(document.HouseSize);
        if (!string.IsNullOrWhiteSpace(documentHouseSize)
            && !string.Equals(documentHouseSize, areaContext.HouseSize, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = $"This MakePlace design is for a {documentHouseSize} {areaContext.AreaLabel} area, but the current housing area is {areaContext.ScopeLabel}.";
            return false;
        }

        IReadOnlyList<LayoutAttachmentNode<MakePlaceFurnitureDocument>> furniture = LayoutAttachmentTree.Flatten(SelectFurniture(document, areaContext), SelectAttachments);
        int ignoredFurnitureCount = LayoutAttachmentTree.Count(SelectOppositeFurniture(document, areaContext), SelectAttachments);
        int ignoredFixtureCount = CountFixtures(document);
        if (furniture.Count == 0)
        {
            errorMessage = ignoredFurnitureCount > 0
                ? $"This MakePlace design only contains {areaContext.OppositeAreaLabel} furniture. Move to the matching housing area to import it."
                : $"The selected MakePlace design does not contain any {areaContext.AreaLabel} furniture.";
            return false;
        }

        float layoutScale = SelectScale(document, areaContext);
        List<ObjectSnapshot> snapshots = BuildSnapshots(furniture, areaContext, layoutScale, currentLocation.CreationContext, DateTime.UtcNow, out int skippedFurnitureCount);
        if (snapshots.Count == 0)
        {
            errorMessage = $"The selected MakePlace design did not contain any {areaContext.AreaLabel} furniture that could be mapped to the Intoner's furniture catalog.";
            return false;
        }

        string importedName = ObjectLayoutFileUtility.ResolveImportedLayoutName(Path.GetFileNameWithoutExtension(sourcePath), sourcePath);
        payload = new ObjectLayoutImportPayload(
            importedName,
            snapshots,
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BuildImportStatus(importedName, snapshots.Count, skippedFurnitureCount, ignoredFurnitureCount, ignoredFixtureCount, areaContext));
        errorMessage = string.Empty;
        return true;
    }

    private List<ObjectSnapshot> BuildSnapshots(
        IReadOnlyList<LayoutAttachmentNode<MakePlaceFurnitureDocument>> furnitureEntries,
        LayoutTransferContext areaContext,
        float layoutScale,
        ObjectCreationContext currentContext,
        DateTime createdAtUtc,
        out int skippedFurnitureCount)
    {
        List<ObjectSnapshot> snapshots = new(furnitureEntries.Count);
        Dictionary<int, Guid> snapshotIdsByNodeIndex = [];
        skippedFurnitureCount = 0;

        for (int index = 0; index < furnitureEntries.Count; ++index)
        {
            LayoutAttachmentNode<MakePlaceFurnitureDocument> entry = furnitureEntries[index];
            Guid? attachmentParentId = entry.HasParent && snapshotIdsByNodeIndex.TryGetValue(entry.ParentIndex, out Guid parentId)
                ? parentId
                : null;

            if (TryCreateSnapshot(entry.Item, areaContext, layoutScale, currentContext, attachmentParentId, createdAtUtc, out ObjectSnapshot snapshot))
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
        MakePlaceFurnitureDocument furniture,
        LayoutTransferContext areaContext,
        float layoutScale,
        ObjectCreationContext currentContext,
        Guid? attachmentParentId,
        DateTime createdAtUtc,
        out ObjectSnapshot snapshot)
    {
        snapshot = null!;
        if (!furnitureCatalog.TryFind(furniture.ItemId, furniture.Name, areaContext.FurnitureArea, out FurnitureCatalogMatch? match)
            || !MakePlaceTransformMapper.TryToObjectTransform(furniture.Transform, layoutScale, areaContext.PlotBasis, out ObjectTransform transform))
        {
            return false;
        }

        byte stainId = colorMapper.TryResolveStainId(furniture, out byte resolvedStainId)
            ? resolvedStainId
            : (byte)0;

        ObjectSnapshot nextSnapshot = new()
        {
            Id = Guid.NewGuid(),
            Name = ObjectStringUtility.TrimOrFallback(furniture.Name, match.DisplayName),
            Kind = ObjectKind.Furniture,
            CreatedAtUtc = createdAtUtc,
            CreatedIn = currentContext,
            Transform = transform,
            Model = new FurnitureModel
            {
                SharedGroupPath = match.Entry.PlacementPath,
                HousingRowId = match.Variant.HousingRowId,
                ItemRowId = match.Variant.ItemRowId,
                AttachmentParentId = attachmentParentId,
                MaterialItem = MakePlaceMaterialMapper.TryResolveMaterial(furniture, out FurnitureMaterialItemModel material)
                    ? material
                    : null,
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

    private static string BuildImportStatus(
        string layoutName,
        int importedCount,
        int skippedFurnitureCount,
        int ignoredFurnitureCount,
        int ignoredFixtureCount,
        LayoutTransferContext areaContext)
    {
        string message = $"Imported MakePlace layout '{layoutName}' with {importedCount} {areaContext.AreaLabel} furniture for the current {areaContext.ScopeLabel} area.";
        if (skippedFurnitureCount > 0)
        {
            message += $" Skipped {skippedFurnitureCount} unsupported furniture entries.";
        }

        if (ignoredFurnitureCount > 0)
        {
            message += $" Ignored {ignoredFurnitureCount} {areaContext.OppositeAreaLabel} furniture entries.";
        }

        if (ignoredFixtureCount > 0)
        {
            message += $" Ignored {ignoredFixtureCount} fixture entries because Intoner does not edit fixtures yet.";
        }

        return message;
    }

    private static IReadOnlyList<MakePlaceFurnitureDocument> SelectFurniture(
        MakePlaceLayoutDocument document,
        LayoutTransferContext context)
        => context.Area == ObjectHousingArea.Outdoor
            ? document.ExteriorFurniture ?? []
            : document.InteriorFurniture ?? [];

    private static IReadOnlyList<MakePlaceFurnitureDocument> SelectOppositeFurniture(
        MakePlaceLayoutDocument document,
        LayoutTransferContext context)
        => context.Area == ObjectHousingArea.Outdoor
            ? document.InteriorFurniture ?? []
            : document.ExteriorFurniture ?? [];

    private static float SelectScale(MakePlaceLayoutDocument document, LayoutTransferContext context)
        => context.Area == ObjectHousingArea.Outdoor
            ? document.ExteriorScale
            : document.InteriorScale;

    private static IEnumerable<MakePlaceFurnitureDocument?> SelectAttachments(MakePlaceFurnitureDocument furniture)
        => furniture.Attachments ?? [];

    private static int CountFixtures(MakePlaceLayoutDocument document)
        => (document.InteriorFixture?.Count ?? 0) + (document.ExteriorFixture?.Count ?? 0);
}
