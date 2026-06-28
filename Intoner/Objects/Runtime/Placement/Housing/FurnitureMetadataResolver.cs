using Intoner.Objects.Catalog;
using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal sealed class FurnitureMetadataResolver(IObjectCatalogService catalogService)
{
    public bool TryResolve(ObjectSnapshot snapshot, out HousingFurnitureMetadata metadata)
    {
        if (TryResolve(snapshot, out _, out metadata))
        {
            return true;
        }

        metadata = default!;
        return false;
    }

    public bool TryResolve(ObjectSnapshot snapshot, out FurnitureModel furnitureModel, out HousingFurnitureMetadata metadata)
    {
        furnitureModel = default!;
        metadata = default!;
        if (snapshot.Model is not FurnitureModel resolvedFurnitureModel)
        {
            return false;
        }

        if (!catalogService.TryResolveFurnitureMetadata(
                resolvedFurnitureModel.SharedGroupPath,
                resolvedFurnitureModel.HousingRowId,
                resolvedFurnitureModel.ItemRowId,
                out HousingFurnitureMetadata? resolvedMetadata)
            || resolvedMetadata is null)
        {
            return false;
        }

        furnitureModel = resolvedFurnitureModel;
        metadata = resolvedMetadata;
        return true;
    }
}

