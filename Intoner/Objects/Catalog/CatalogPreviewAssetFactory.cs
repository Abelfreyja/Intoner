using Intoner.Objects.Assets;
using Intoner.Objects.Preview;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Intoner.Objects.Catalog;

internal static class CatalogPreviewAssetFactory
{
    public static PreviewAsset Create(ObjectCatalogEntry entry)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(entry.PlacementPath);
        return new PreviewAsset(
            new PreviewAsset.Key(GetSource(entry.Kind), normalizedPath),
            entry.DisplayPath,
            entry.PreviewModels,
            ResolveUntexturedDiffuseColor(entry.Kind));
    }

    public static bool TryCreate(
        IObjectCatalogService objectCatalog,
        ObjectCatalogKind kind,
        string path,
        [NotNullWhen(true)] out PreviewAsset? asset)
    {
        asset = null;
        if (!GameAssetPathRules.TryNormalizeGamePath(path, out string normalizedPath))
        {
            return false;
        }

        asset = new PreviewAsset(
            new PreviewAsset.Key(GetSource(kind), normalizedPath),
            normalizedPath,
            objectCatalog.ResolvePreviewModels(kind, normalizedPath),
            ResolveUntexturedDiffuseColor(kind));
        return true;
    }

    private static string GetSource(ObjectCatalogKind kind)
        => kind switch
        {
            ObjectCatalogKind.BgObject  => "object-catalog-bgobject",
            ObjectCatalogKind.Furniture => "object-catalog-furniture",
            ObjectCatalogKind.Vfx       => "object-catalog-vfx",
            _                           => "object-catalog",
        };

    private static Vector3 ResolveUntexturedDiffuseColor(ObjectCatalogKind kind)
        => kind switch
        {
            ObjectCatalogKind.BgObject  => new Vector3(0.83f, 0.71f, 0.58f),
            ObjectCatalogKind.Furniture => new Vector3(0.58f, 0.70f, 0.84f),
            ObjectCatalogKind.Vfx       => new Vector3(0.74f, 0.74f, 0.74f),
            _                           => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unsupported object catalog preview kind"),
        };
}
