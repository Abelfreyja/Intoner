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

        if (!catalogService.TryResolveFurnitureVariant(
                resolvedFurnitureModel.SharedGroupPath,
                resolvedFurnitureModel.HousingRowId,
                resolvedFurnitureModel.ItemRowId,
                out _,
                out ObjectCatalogFurnitureVariant? variant))
        {
            return false;
        }

        furnitureModel = resolvedFurnitureModel;
        metadata = variant.HousingMetadata;
        return true;
    }
}

