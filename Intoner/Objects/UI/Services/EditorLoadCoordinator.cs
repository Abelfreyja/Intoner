using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Services.Loading;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.UI.Services;

internal sealed record EditorLoadData(
    ObjectCatalogData Catalog,
    IReadOnlyList<FurnitureStainOption> FurnitureStains);

/// <summary> coordinates the data required before the object editor can draw </summary>
internal sealed class EditorLoadCoordinator(
    IObjectCatalogService catalogService,
    IFurnitureStainService stainService)
{
    private static readonly LoadStatus ReadyStatus = new(LoadPhase.Ready, string.Empty, 1d);

    private readonly LoadGroup _loading = new(
        (catalogService, 98d),
        (stainService, 2d));

    private EditorLoadData? _data;

    public void EnsureLoaded()
        => _loading.EnsureLoaded();

    public bool TryGetData(
        [NotNullWhen(true)] out EditorLoadData? data,
        out LoadStatus status)
    {
        if (_data is not null)
        {
            data = _data;
            status = ReadyStatus;
            return true;
        }

        status = _loading.Status;
        bool hasCatalog = catalogService.TryGetCatalog(out ObjectCatalogData? catalog);
        bool hasStains = stainService.TryGetStains(out IReadOnlyList<FurnitureStainOption>? stains);

        if (!hasCatalog || catalog is null || !hasStains || stains is null)
        {
            data = null;
            return false;
        }

        _data = new EditorLoadData(catalog, stains);
        data = _data;
        status = ReadyStatus;
        return true;
    }
}
