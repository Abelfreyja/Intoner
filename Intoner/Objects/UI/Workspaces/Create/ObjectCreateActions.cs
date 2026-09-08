using Intoner.Objects.Library;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Services;

namespace Intoner.Objects.UI;

internal sealed class ObjectCreateActions
{
    private readonly IObjectSceneView         _sceneView;
    private readonly IHistoryCoordinator      _historyCoordinator;
    private readonly IObjectKindService       _objectKindService;
    private readonly IObjectHousingModePolicy _housingModePolicy;
    private readonly ObjectEditorCatalog      _catalogInfo;
    private readonly ObjectCreationDraft      _draft;

    public ObjectCreateActions(
        IObjectSceneView sceneView,
        IHistoryCoordinator historyCoordinator,
        IObjectKindService objectKindService,
        IObjectHousingModePolicy housingModePolicy,
        ObjectEditorCatalog catalogInfo,
        ObjectCreationDraft draft)
    {
        _sceneView          = sceneView;
        _historyCoordinator = historyCoordinator;
        _objectKindService  = objectKindService;
        _housingModePolicy  = housingModePolicy;
        _catalogInfo        = catalogInfo;
        _draft              = draft;
    }

    internal bool CanPlaceObjectPreset(ObjectLibraryPreset preset)
        => TryResolvePlaceablePreset(preset, out _);

    private bool TryResolvePlaceablePreset(ObjectLibraryPreset preset, out ObjectLibraryPreset resolved)
    {
        resolved = preset;
        if (!_objectKindService.CanCreate(preset.Kind)
         || !ObjectLibraryRules.TryNormalizePreset(preset, out ObjectLibraryPreset? normalized))
        {
            return false;
        }

        resolved = normalized;
        return CanUseLibraryPreset(resolved)
            && (resolved.Model is not FurnitureModel furniture
             || CanCreateFurnitureInHousingMode(furniture.SharedGroupPath));
    }

    internal bool TryPlaceObjectPreset(ObjectLibraryPreset preset)
    {
        if (!TryResolvePlaceablePreset(preset, out ObjectLibraryPreset resolved))
        {
            return false;
        }

        ObjectData model = resolved.Model switch
        {
            FurnitureModel furniture => _catalogInfo.ResolveFurnitureCatalogVariant(furniture),
            VfxModel vfx when _catalogInfo.ResolveVfxCatalogInfo(vfx.VfxPath)?.CanUseReplayLoop == false
                => vfx with { Loop = false },
            _ => resolved.Model,
        };
        string folderPath = _draft.ResolveCreatePlacementFolderPath(_sceneView.GetPlacedFolders());
        ObjectPlacementOverrides overrides = new()
        {
            Visible = resolved.Visible,
            FolderPath = folderPath,
            Scale = resolved.Scale,
            Model = model,
        };
        return _historyCoordinator.TryCreateObject(
            ObjectEditorCatalog.ResolveCreateObjectHistoryTitle(resolved.Kind),
            resolved.Kind,
            overrides);
    }

    internal bool CanUseLibraryPreset(ObjectLibraryPreset preset)
    {
        ObjectHousingModeState housing = _housingModePolicy.GetState();
        if (!housing.IsHousingMode)
        {
            return true;
        }

        return preset.Model is FurnitureModel furniture
            && _housingModePolicy.AllowsFurniturePath(furniture.SharedGroupPath);
    }

    internal void CreateObject(ObjectKind kind, ObjectPlacementOverrides? overrides)
        => _ = _historyCoordinator.TryCreateObject(ObjectEditorCatalog.ResolveCreateObjectHistoryTitle(kind), kind, overrides);

    internal bool CanCreateFurnitureInHousingMode(string sharedGroupPath)
    {
        ObjectHousingModeState state = _housingModePolicy.GetState();
        if (!state.IsHousingMode)
        {
            return true;
        }

        if (!_housingModePolicy.AllowsFurniturePath(sharedGroupPath))
        {
            return false;
        }

        int furnitureCount = HousingFurnitureCounter.Count(_sceneView.GetPlacedObjectSnapshots());
        return furnitureCount < state.FurnitureLimit;
    }
}
