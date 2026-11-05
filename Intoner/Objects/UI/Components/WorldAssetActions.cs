using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Catalog;
using Intoner.Objects.Library;
using Intoner.Objects.Models;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI.Components
{
    internal sealed class WorldAssetActions
    {
        private readonly ObjectEditorCatalog  _catalogInfo;
        private readonly ObjectCreationDraft  _draft;
        private readonly ObjectCatalogBrowser _catalog;
        private readonly ObjectLibraryBrowser _library;
        private readonly ObjectCreateActions  _createActions;
        private readonly EditorWorldPicker    _picker;
        private long _placementInstanceId;
        private string? _placementError;

        private ObjectData? _assetModel;
        private Vector3 _assetScale;
        private ObjectCatalogBgObjectInfo? _bgObjectInfo;
        private IReadOnlyList<ObjectCatalogFurnitureResult>? _furniture;
        private Choice[] _choices = [];

        public WorldAssetActions(ObjectEditorCatalog catalogInfo, ObjectCreationDraft draft,
            ObjectCatalogBrowser catalog, ObjectLibraryBrowser library, ObjectCreateActions createActions, EditorWorldPicker picker)
        {
            _catalogInfo   = catalogInfo;
            _draft         = draft;
            _catalog       = catalog;
            _library       = library;
            _createActions = createActions;
            _picker        = picker;
        }

        internal bool Draw(float edge, bool workspaceAvailable)
        {
            WorldAssetPickState state = _picker.State;
            WorldAsset? asset = state.IsPicking ? null : state.SelectedAsset;
            bool ready = _catalogInfo.IsReady;
            bool enabled = ready && workspaceAvailable && asset is not null;

            if (asset is not null)
            {
                RefreshChoices(asset);
            }

            DrawPlacement(state, edge, workspaceAvailable);
            ImGui.SameLine();

            bool canOpenCatalog = enabled && (_draft.CanSelectKind(asset!.Kind) || _choices.Length > 1);
            string tooltip = "Open in Catalog";

            if (!ready)
            {
                tooltip = "Catalog is not ready";
            }
            else if (!workspaceAvailable)
            {
                tooltip = "Close the current dialog first";
            }
            else if (asset is not null && !_draft.CanSelectKind(asset.Kind) && _choices.Length < 2)
            {
                tooltip = asset.Kind == ObjectKind.Vfx ? "VFX are unavailable in Housing Mode" : "BgObjects are unavailable in Housing Mode";
            }

            bool clicked = EditorIconButton.DrawToggle("catalog", FontAwesomeIcon.ExternalLinkAlt, new Vector2(edge), false,
                ThemeColors.AccentPrimary, ThemeColors.AccentOrange, tooltip, enabled: canOpenCatalog);
            bool opened = false;

            if (canOpenCatalog)
            {
                opened = _choices.Length == 1
                    ? clicked && OpenCatalog(_choices[0])
                    : DrawChoiceMenu("##modelCatalog", clicked, Destination.Catalog);
            }

            ImGui.SameLine();

            if (enabled && _choices.Length > 1)
            {
                clicked = EditorIconButton.DrawToggle("modelLibrary", FontAwesomeIcon.FolderPlus, new Vector2(edge), false,
                    ThemeColors.AccentPrimary, ThemeColors.AccentOrange, "Add to Library");
                opened |= DrawChoiceMenu("##modelLibrary", clicked, Destination.Library);
            }
            else
            {
                Choice? choice = enabled ? _choices[0] : null;
                opened |= _library.DrawPresetLibraryButton(choice?.Name ?? string.Empty, choice?.Preset, edge, "Add to Library");
            }

            return opened;
        }

        private void DrawPlacement(WorldAssetPickState state, float edge, bool workspaceAvailable)
        {
            WorldAsset? asset = state.IsPicking ? null : state.LiveSelection;
            long instanceId = asset?.InstanceId ?? 0;

            if (_placementInstanceId != instanceId)
            {
                _placementInstanceId = instanceId;
                _placementError = null;
            }

            ObjectLibraryPreset? preset = asset is not null ? _choices[0].Preset : null;
            bool enabled = workspaceAvailable && preset is not null && _createActions.CanPlaceObjectPreset(preset);
            string tooltip = _placementError ?? (asset, workspaceAvailable, enabled) switch
            {
                (null, _, _) => "Select an asset still present in this scene",
                (_, false, _) => "Close the current dialog first",
                (_, _, false) => "Placement is unavailable",
                _ => asset.Kind == ObjectKind.Vfx ? "Place VFX at picked position" : "Place BgObject at picked position",
            };

            if (EditorIconButton.DrawToggle("place", FontAwesomeIcon.Play, new Vector2(edge), false,
                    ThemeColors.AccentPrimary, ThemeColors.AccentGreen, tooltip, enabled: enabled))
            {
                _placementError = null;
                _picker.RefreshSelection();
                WorldAsset? current = _picker.State.LiveSelection;

                if (current is null || current.InstanceId != asset!.InstanceId)
                {
                    _placementError = "This asset is no longer present in the scene";
                }
                else if (!_createActions.TryPlaceObjectPreset(preset! with { Model = current.Model, Scale = current.Scale },
                             current.CreatePlacementTransform()))
                {
                    _placementError = "The object could not be placed";
                }
            }
        }

        private void RefreshChoices(WorldAsset asset)
        {
            string assetPath = asset.Path;
            bool ready = _catalogInfo.IsReady;
            IReadOnlyList<ObjectCatalogFurnitureResult> furniture = ready && asset.Model is BgObjectModel
                ? _catalogInfo.FindFurnitureByModel(assetPath) : [];
            ObjectCatalogBgObjectInfo? bgObjectInfo = ready && asset.Model is BgObjectModel
                ? _catalogInfo.ResolveBgObjectCatalogInfo(assetPath) : null;

            if (_assetModel == asset.Model && _assetScale == asset.Scale
             && ReferenceEquals(_furniture, furniture) && ReferenceEquals(_bgObjectInfo, bgObjectInfo))
            {
                return;
            }

            _placementError = null;
            _choices = new Choice[furniture.Count + 1];
            _choices[0] = new(Path.GetFileNameWithoutExtension(assetPath),
                ObjectEditorCatalog.CreateLibraryPreset(
                    ready && asset.Model is BgObjectModel bgObject ? _catalogInfo.ApplyBgObjectDefaults(bgObject) : asset.Model,
                    scale: asset.Scale));
            for (int index = 0; index < furniture.Count; ++index)
            {
                ObjectCatalogFurnitureResult match = furniture[index];
                _choices[index + 1] = new(match.Variant.Name, ObjectEditorCatalog.CreateCatalogLibraryPreset(match), match);
            }

            _assetModel = asset.Model;
            _assetScale = asset.Scale;
            _furniture = furniture;
            _bgObjectInfo = bgObjectInfo;
        }

        private bool DrawChoiceMenu(string id, bool open, Destination destination)
        {
            using EditorContextMenu.PopupScope menu = EditorContextMenu.BeginDropdownForLastItem(id, open);

            if (!menu)
            {
                return false;
            }

            bool opened = DrawChoice(_choices[0], "BgObject", destination);

            if (_choices.Length == 2)
            {
                opened |= DrawChoice(_choices[1], "Furniture", destination);
            }
            else
            {
                using EditorContextMenu.SubMenuScope furniture = EditorContextMenu.BeginSubMenu("furniture",
                    SceneItemPresentation.ResolveObjectKindIcon(ObjectKind.Furniture), "Furniture");

                if (furniture)
                {
                    for (int index = 1; index < _choices.Length; ++index)
                    {
                        opened |= DrawChoice(_choices[index], _choices[index].Name, destination);
                    }
                }
            }

            return opened;
        }

        private bool DrawChoice(Choice choice, string label, Destination destination)
        {
            using var id = ImRaii.PushId(choice.Id);

            if (destination == Destination.Library)
            {
                return _library.DrawPresetLibraryMenuItem(label, choice.Name, choice.Preset);
            }

            bool enabled = _draft.CanSelectKind(choice.Preset.Kind);
            return EditorContextMenu.DrawItem(SceneItemPresentation.ResolveObjectKindIcon(choice.Preset.Kind), label,
                    enabled: enabled, tooltip: enabled ? choice.Name : "BgObjects are unavailable in Housing Mode")
                && OpenCatalog(choice);
        }

        private bool OpenCatalog(Choice choice)
        {
            if (choice.Furniture is { } furniture)
            {
                _draft.SelectFurnitureCatalogEntry(furniture);
            }
            else if (!_draft.SelectAssetPath(choice.Preset.Kind, Intoner.Objects.Utils.ObjectSnapshotUtility.GetRootResourcePath(choice.Preset.Model)))
            {
                return false;
            }

            _catalog.OpenCurrentDraftInCatalog();
            return true;
        }

        private enum Destination { Catalog, Library }

        private sealed record Choice(string Name, ObjectLibraryPreset Preset, ObjectCatalogFurnitureResult? Furniture = null)
        {
            public string Id { get; } = Furniture is { } furniture
                ? $"{furniture.Entry.PlacementPath}:{furniture.Variant.HousingRowId}"
                : "asset";
        }
    }
}
