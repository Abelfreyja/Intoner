using Intoner.Objects.Assets;
using Intoner.Objects.Preview;
using Intoner.Objects.Resources;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Intoner.Objects.Catalog;

internal static class CatalogPreviewAssetFactory
{
    public static PreviewAsset Create(ObjectCatalogEntry entry)
    {
        string normalizedPath = ObjectResolvedPath.FromGamePath(entry.PlacementPath).Path;
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
        string normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;
        if (!IsSupportedPath(kind, normalizedPath))
        {
            return false;
        }

        asset = Create(kind, normalizedPath, objectCatalog.ResolvePreviewModels(kind, normalizedPath));
        return true;
    }

    public static bool IsSupportedPath(ObjectCatalogKind kind, string normalizedPath)
        => normalizedPath.Length > 0 && (kind != ObjectCatalogKind.BgObject || ObjectAssetPathRules.IsCatalogModelPath(normalizedPath));

    public static PreviewAsset Create(ObjectCatalogKind kind, string path, IReadOnlyList<PreviewModelInfo> models, ushort? materialVariant = null)
    {
        if (materialVariant is > 0)
        {
            models = models.Select(info => info.MaterialVariant.HasValue ? info with { MaterialVariant = materialVariant } : info).ToArray();
        }

        return new PreviewAsset(new PreviewAsset.Key(GetSource(kind), path), path, models, ResolveUntexturedDiffuseColor(kind));
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
