namespace Intoner.Objects.Catalog;

internal sealed record FurnitureCatalogMatch(ObjectCatalogEntry Entry, ObjectCatalogFurnitureVariant Variant)
{
    public string DisplayName
        => Variant.Name;
}
