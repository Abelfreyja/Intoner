using Intoner.Objects.Api;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Services;

namespace Intoner.Objects.UI;

internal sealed class DebugIpcSamples
{
    private readonly IObjectSceneView    _sceneView;
    private readonly EditorInteraction   _interaction;
    private readonly ObjectCreationDraft _draft;

    public DebugIpcSamples(
        IObjectSceneView sceneView,
        EditorInteraction interaction,
        ObjectCreationDraft draft)
    {
        _sceneView   = sceneView;
        _interaction = interaction;
        _draft       = draft;
    }

    internal WorldObject BuildObjectIpcWorldObjectSample(ObjectSnapshot? snapshot = null)
    {
        if (snapshot is not null || TryResolveReferenceObjectIpcSnapshot(out snapshot))
        {
            return ObjectApiMapper.ToWorldObject(snapshot);
        }

        return BuildFallbackObjectIpcWorldObjectSample();
    }

    internal PersistentObject BuildObjectIpcMutationSample(ObjectSnapshot? snapshot = null)
    {
        if (snapshot is not null || TryResolveReferencePersistedObjectIpcSnapshot(out snapshot))
        {
            return ObjectApiMapper.ToPersistentObject(snapshot);
        }

        return new PersistentObject(BuildFallbackObjectIpcWorldObjectSample(), string.Empty, false);
    }

    private WorldObject BuildFallbackObjectIpcWorldObjectSample()
    {
        var createdIn = BuildObjectIpcCreationData();
        var vfxModel = _draft.Vfx.Model;
        if (!string.IsNullOrWhiteSpace(vfxModel.VfxPath))
        {
            return new WorldObject(
                Guid.Empty,
                "IPC Tester VFX",
                WorldObjectKind.Vfx,
                _draft.Vfx.Visible,
                new WorldObjectTransform(
                    new ObjectVector3(0f, 0f, 0f),
                    new ObjectVector3(0f, 0f, 0f),
                    new ObjectVector3(_draft.Vfx.Scale.X, _draft.Vfx.Scale.Y, _draft.Vfx.Scale.Z)),
                default,
                createdIn,
                string.Empty,
                new WorldObjectModelData(
                    Vfx: new VfxModelData(
                        vfxModel.VfxPath,
                        new ObjectVector4(
                            vfxModel.Color.X,
                            vfxModel.Color.Y,
                            vfxModel.Color.Z,
                            vfxModel.Color.W),
                        vfxModel.Speed,
                        vfxModel.Paused,
                        vfxModel.FadeInSeconds,
                        vfxModel.ReplayOnTransform,
                        vfxModel.Loop,
                        vfxModel.LoopIntervalSeconds)));
        }

        if (!string.IsNullOrWhiteSpace(_draft.Furniture.Model.SharedGroupPath))
        {
            return new WorldObject(
                Guid.Empty,
                "IPC Tester Furniture",
                WorldObjectKind.Furniture,
                _draft.Furniture.Visible,
                new WorldObjectTransform(
                    new ObjectVector3(0f, 0f, 0f),
                    new ObjectVector3(0f, 0f, 0f),
                    new ObjectVector3(_draft.Furniture.Scale.X, _draft.Furniture.Scale.Y, _draft.Furniture.Scale.Z)),
                default,
                createdIn,
                string.Empty,
                new WorldObjectModelData(
                    Furniture: new FurnitureModelData(
                        _draft.Furniture.Model.SharedGroupPath,
                        new FurnitureColorData(
                            _draft.Furniture.Model.Color.StainId,
                            _draft.Furniture.Model.Color.UseCustomColor,
                            new ObjectVector4(
                                _draft.Furniture.Model.Color.CustomColor.X,
                                _draft.Furniture.Model.Color.CustomColor.Y,
                                _draft.Furniture.Model.Color.CustomColor.Z,
                                _draft.Furniture.Model.Color.CustomColor.W)),
                        _draft.Furniture.Model.Transparency,
                        (Api.ObjectOutlineColor)_draft.Furniture.Model.OutlineColor,
                        _draft.Furniture.Model.HousingRowId,
                        _draft.Furniture.Model.ItemRowId,
                        null)));
        }

        if (!string.IsNullOrWhiteSpace(_draft.BgObject.Model.ModelPath))
        {
            return new WorldObject(
                Guid.Empty,
                "IPC Tester BgObject",
                WorldObjectKind.BgObject,
                _draft.BgObject.Visible,
                new WorldObjectTransform(
                    new ObjectVector3(0f, 0f, 0f),
                    new ObjectVector3(0f, 0f, 0f),
                    new ObjectVector3(_draft.BgObject.Scale.X, _draft.BgObject.Scale.Y, _draft.BgObject.Scale.Z)),
                default,
                createdIn,
                string.Empty,
                new WorldObjectModelData(
                    BgObject: new BgObjectModelData(
                        _draft.BgObject.Model.ModelPath,
                        _draft.BgObject.Model.Transparency,
                        new ObjectVector4(
                            _draft.BgObject.Model.DyeColor.X,
                            _draft.BgObject.Model.DyeColor.Y,
                            _draft.BgObject.Model.DyeColor.Z,
                            _draft.BgObject.Model.DyeColor.W),
                        _draft.BgObject.Model.IsCoveredFromRain)));
        }

        return new WorldObject(
            Guid.Empty,
            "IPC Tester Light",
            WorldObjectKind.Light,
            _draft.Light.Visible,
            new WorldObjectTransform(
                new ObjectVector3(0f, 0f, 0f),
                new ObjectVector3(0f, 0f, 0f),
                new ObjectVector3(1f, 1f, 1f)),
            default,
            createdIn,
            string.Empty,
            new WorldObjectModelData(
                Light: new LightModelData(
                    new ObjectVector3(1f, 1f, 1f),
                    Api.ObjectLightType.AreaLight,
                    Api.ObjectLightFalloffType.Quadratic,
                    new LightFlagsData(true, true, false, false),
                    1f,
                    new LightShapeData(4f, 1f, 45f, 60f, new ObjectVector2(45f, 60f)),
                    new LightShadowData(0f, 0.01f, 4f))));
    }

    internal PersistentObjectPatchRequest BuildObjectIpcPatchSample(PersistentObject persistentObject)
    {
        WorldObject worldObject = persistentObject.Object;
        var position = worldObject.Transform.Position;
        return new PersistentObjectPatchRequest(
            _sceneView.GetPersistentSceneRevision(),
            worldObject.Id,
            new PersistentObjectPatch(
                new WorldObjectPatch(
                    Name: $"{worldObject.Name} [IPC]",
                    Transform: worldObject.Transform with
                    {
                        Position = position with { X = position.X + 0.5f },
                    })));
    }

    private ObjectLocationData BuildObjectIpcCreationData()
    {
        var context = _sceneView.GetCurrentLocationContext();
        return new ObjectLocationData(
            context.WorldId,
            context.WorldName,
            context.TerritoryId,
            context.TerritoryName,
            context.DivisionId,
            context.WardId,
            context.HouseId,
            context.RoomId);
    }

    internal bool TryResolveSelectedObjectIpcSnapshot(out ObjectSnapshot snapshot)
        => TryResolveSelectedIpcSnapshot(persistedOnly: false, out snapshot);

    internal bool TryResolveReferenceObjectIpcSnapshot(out ObjectSnapshot snapshot)
        => TryResolveReferenceIpcSnapshot(persistedOnly: false, out snapshot);

    internal bool TryResolveSelectedPersistedObjectIpcSnapshot(out ObjectSnapshot snapshot)
        => TryResolveSelectedIpcSnapshot(persistedOnly: true, out snapshot);

    private bool TryResolveReferencePersistedObjectIpcSnapshot(out ObjectSnapshot snapshot)
        => TryResolveReferenceIpcSnapshot(persistedOnly: true, out snapshot);

    private bool TryResolveSelectedIpcSnapshot(bool persistedOnly, out ObjectSnapshot snapshot)
    {
        var selectedObjectId = _interaction.Selection.PrimaryItemId;
        if (!selectedObjectId.HasValue)
        {
            snapshot = null!;
            return false;
        }

        var id = selectedObjectId.Value;
        if (!persistedOnly && _sceneView.TryGetSceneObjectSnapshot(id, out snapshot))
        {
            return true;
        }

        if (_sceneView.TryGetPersistedObjectSnapshot(id, out snapshot))
        {
            return true;
        }

        snapshot = null!;
        return false;
    }

    private bool TryResolveReferenceIpcSnapshot(bool persistedOnly, out ObjectSnapshot snapshot)
    {
        if (TryResolveSelectedIpcSnapshot(persistedOnly, out snapshot))
        {
            return true;
        }

        var placedSnapshots = _sceneView.GetPlacedObjectSnapshots();
        if (placedSnapshots.Count > 0)
        {
            snapshot = placedSnapshots[0];
            return true;
        }

        snapshot = null!;
        return false;
    }
}
