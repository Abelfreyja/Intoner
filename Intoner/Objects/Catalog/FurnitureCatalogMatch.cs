using Intoner.Objects.Utils;

namespace Intoner.Objects.Catalog;

internal sealed record FurnitureCatalogMatch(ObjectCatalogEntry Entry, ObjectCatalogFurnitureVariant Variant)
{
    public string DisplayName
        => ObjectStringUtility.TrimOrFallback(Variant.Name, Entry.Name);
}
