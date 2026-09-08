using Intoner.Scene;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;
using ObjectOutlineColorApi = Intoner.Objects.Api.ObjectOutlineColor;
using ObjectOutlineColorModel = Intoner.Objects.Models.ObjectOutlineColor;
using RuntimeObjectStateKindDto = Intoner.Objects.Api.RuntimeObjectStateKind;
using RuntimeObjectStateKindModel = Intoner.Objects.Models.ObjectRuntimeStateKind;

namespace Intoner.Objects.Api;

internal static class ObjectApiMapper
{
    public static WorldObject ToWorldObject(ObjectSnapshot snapshot)
        => new(
            snapshot.Id,
            snapshot.Name,
            ToWorldObjectKind(snapshot.Kind),
            snapshot.Visible,
            ToWorldTransform(snapshot.Transform),
            snapshot.CreatedAtUtc,
            ToLocation(snapshot.CreatedIn),
            snapshot.CollectionId,
            ToWorldModel(snapshot.Kind, snapshot.Model));

    public static PersistentObject ToPersistentObject(ObjectSnapshot snapshot)
        => new(
            ToWorldObject(snapshot),
            snapshot.FolderPath,
            snapshot.Locked);

    public static SavedObjectLayoutInfo ToSavedLayoutInfo(ObjectLayoutSnapshot layout)
        => new(
            layout.Id,
            layout.Name,
            layout.Revision,
            layout.CreatedAtUtc,
            layout.UpdatedAtUtc);

    public static SavedObjectLayout ToSavedLayout(ObjectLayoutSnapshot layout)
        => new(
            layout.Id,
            layout.Name,
            layout.Revision,
            layout.CreatedAtUtc,
            layout.UpdatedAtUtc,
            ToPersistentSet(layout.Objects, layout.Folders, layout.FolderColors));

    public static PersistentObjectSet ToPersistentSet(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, string> folderColors)
        => new(
            objects.Select(ToPersistentObject).ToList(),
            folders.ToList(),
            new Dictionary<string, string>(folderColors, StringComparer.OrdinalIgnoreCase));

    public static LoadedObjectLayout ToLoadedLayout(ObjectLoadedLayoutSnapshot layout)
        => new(
            layout.Kind == ObjectLoadedLayoutKind.Default
                ? LoadedObjectLayoutType.Default
                : LoadedObjectLayoutType.Temporary,
            layout.LayoutId,
            layout.SourceKey,
            layout.SourceSessionId,
            layout.Name,
            layout.Revision,
            layout.UpdatedAtUtc,
            layout.Objects.Select(ToWorldObject).ToList());

    public static ObjectLocationData ToLocation(SceneCreationContext context)
        => new(
            context.WorldId,
            context.WorldName,
            context.TerritoryId,
            context.TerritoryName,
            context.DivisionId,
            context.WardId,
            context.HouseId,
            context.RoomId);

    public static RuntimeObjectState ToRuntimeState(ObjectRuntimeStateSnapshot snapshot)
        => new(
            snapshot.Id,
            ToRuntimeStateKind(snapshot.State),
            snapshot.FailureCode);

    public static TemporarySourceInfo ToTemporarySource(ObjectTemporarySourceSnapshot source, string sourceId)
        => new(
            sourceId,
            source.SessionId,
            source.Name,
            source.Revision,
            source.UpdatedAtUtc,
            source.Objects.Select(ToWorldObject).ToList(),
            source.Collections.Select(ToTemporaryCollection).ToList());

    public static TemporaryObjectCollection ToTemporaryCollection(ObjectTemporaryCollectionData collection)
        => new(
            collection.CollectionId,
            collection.Name,
            collection.Redirects.Select(ToTemporaryRedirect).ToList());

    public static bool TryToTemporaryCollection(
        TemporaryObjectCollection? dto,
        out ObjectTemporaryCollectionData collection)
    {
        if (dto?.Redirects is null)
        {
            collection = null!;
            return false;
        }

        List<ObjectTemporaryCollectionRedirectData> redirects = new(dto.Redirects.Count);
        foreach (TemporaryObjectCollectionRedirect? redirectDto in dto.Redirects)
        {
            if (!TryToTemporaryCollectionRedirect(redirectDto, out ObjectTemporaryCollectionRedirectData redirect))
            {
                collection = null!;
                return false;
            }

            redirects.Add(redirect);
        }

        collection = new ObjectTemporaryCollectionData
        {
            CollectionId = dto.CollectionId,
            Name = dto.Name,
            Redirects = redirects,
        };
        return true;
    }

    public static bool TryToTemporaryCollections(
        IReadOnlyList<TemporaryObjectCollection>? dtos,
        out List<ObjectTemporaryCollectionData> collections)
    {
        if (dtos is null)
        {
            collections = [];
            return false;
        }

        collections = new List<ObjectTemporaryCollectionData>(dtos.Count);
        foreach (TemporaryObjectCollection? dto in dtos)
        {
            if (!TryToTemporaryCollection(dto, out ObjectTemporaryCollectionData collection))
            {
                collections = [];
                return false;
            }

            collections.Add(collection);
        }

        return true;
    }

    public static bool TryToSnapshot(WorldObject? dto, out ObjectSnapshot snapshot)
    {
        if (dto is null
            || dto.Transform is null
            || dto.CreatedIn is null
            || !TryToObjectKind(dto.Kind, out ObjectKind kind)
            || !TryToObjectData(kind, dto.Model, out ObjectData model))
        {
            snapshot = null!;
            return false;
        }

        snapshot = new ObjectSnapshot
        {
            Id = dto.Id,
            Name = dto.Name ?? string.Empty,
            Kind = kind,
            Visible = dto.Visible,
            Transform = ToTransform(dto.Transform),
            CreatedAtUtc = dto.CreatedAtUtc,
            CreatedIn = ToCreationContext(dto.CreatedIn),
            CollectionId = dto.CollectionId ?? string.Empty,
            LayoutId = null,
            Model = model,
        };
        return true;
    }

    public static bool TryToPersistentSnapshot(PersistentObject? dto, out ObjectSnapshot snapshot)
    {
        if (dto is null || !TryToSnapshot(dto.Object, out snapshot))
        {
            snapshot = null!;
            return false;
        }

        snapshot = snapshot with
        {
            FolderPath = ObjectFolderUtility.SanitizeFolderPath(dto.FolderPath),
            Locked = dto.Locked,
        };
        return true;
    }

    public static bool TryToPersistentSceneUpdate(
        PersistentObjectSceneApplyRequest? dto,
        out ObjectPersistentSceneUpdate update)
    {
        if (dto is null
            || dto.ExpectedRevision <= 0
            || !TryToPersistentSet(dto.Standalone, null, out List<ObjectSnapshot> standaloneObjects, out IReadOnlyList<string> standaloneFolders, out IReadOnlyDictionary<string, string> standaloneFolderColors))
        {
            update = null!;
            return false;
        }

        Guid? defaultLayoutId = null;
        List<ObjectSnapshot> defaultLayoutObjects = [];
        IReadOnlyList<string> defaultLayoutFolders = [];
        IReadOnlyDictionary<string, string> defaultLayoutFolderColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dto.DefaultLayout is not null)
        {
            defaultLayoutId = dto.DefaultLayout.LayoutId;
            if (defaultLayoutId == Guid.Empty
                || !TryToPersistentSet(
                    dto.DefaultLayout.Content,
                    defaultLayoutId,
                    out defaultLayoutObjects,
                    out defaultLayoutFolders,
                    out defaultLayoutFolderColors))
            {
                update = null!;
                return false;
            }
        }

        HashSet<Guid> objectIds = new(standaloneObjects.Select(static snapshot => snapshot.Id));
        if (objectIds.Count != standaloneObjects.Count
            || defaultLayoutObjects.Any(snapshot => !objectIds.Add(snapshot.Id)))
        {
            update = null!;
            return false;
        }

        update = new ObjectPersistentSceneUpdate
        {
            ExpectedRevision = dto.ExpectedRevision,
            StandaloneObjects = standaloneObjects,
            StandaloneFolders = standaloneFolders,
            StandaloneFolderColors = standaloneFolderColors,
            DefaultLayoutId = defaultLayoutId,
            DefaultLayoutObjects = defaultLayoutObjects,
            DefaultLayoutFolders = defaultLayoutFolders,
            DefaultLayoutFolderColors = defaultLayoutFolderColors,
        };
        return true;
    }

    public static bool TryToDetachedSnapshot(WorldObject? dto, out ObjectSnapshot snapshot)
    {
        if (!TryToSnapshot(dto, out snapshot)
            || snapshot.Id == Guid.Empty
            || snapshot.CreatedAtUtc == default)
        {
            snapshot = null!;
            return false;
        }

        snapshot = snapshot with { LayoutId = null };
        return true;
    }

    public static bool TryToDetachedSnapshots(IReadOnlyList<WorldObject>? dtos, out List<ObjectSnapshot> snapshots)
    {
        if (dtos is null)
        {
            snapshots = [];
            return false;
        }

        snapshots = new List<ObjectSnapshot>(dtos.Count);
        foreach (WorldObject? dto in dtos)
        {
            if (!TryToDetachedSnapshot(dto, out var snapshot))
            {
                snapshots = [];
                return false;
            }

            snapshots.Add(snapshot);
        }

        return true;
    }

    public static bool TryToPatch(WorldObjectPatch? dto, ObjectKind kind, out ObjectSnapshotPatch patch)
        => TryToPatch(dto, out patch)
            && (!patch.ModelKind.HasValue || patch.ModelKind.Value == kind);

    public static bool TryToPatch(WorldObjectPatch? dto, out ObjectSnapshotPatch patch)
    {
        if (dto is null)
        {
            patch = null!;
            return false;
        }

        ObjectDataPatch? model = null;
        ObjectKind? modelKind = null;
        if (dto.Model is not null)
        {
            if (!TryGetModelPatchKind(dto.Model, out ObjectKind resolvedKind)
                || !TryToObjectDataPatch(resolvedKind, dto.Model, out model))
            {
                patch = null!;
                return false;
            }

            modelKind = resolvedKind;
        }

        patch = new ObjectSnapshotPatch
        {
            Name = dto.Name,
            Visible = dto.Visible,
            Transform = dto.Transform is not null
                ? ToTransform(dto.Transform)
                : null,
            ModelKind = modelKind,
            Model = model,
        };
        return true;
    }

    public static WorldObjectKind ToWorldObjectKind(ObjectKind kind)
        => kind switch
        {
            ObjectKind.Light => WorldObjectKind.Light,
            ObjectKind.BgObject => WorldObjectKind.BgObject,
            ObjectKind.Furniture => WorldObjectKind.Furniture,
            ObjectKind.Vfx => WorldObjectKind.Vfx,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    public static bool TryToObjectKind(WorldObjectKind kind, out ObjectKind objectKind)
    {
        switch (kind)
        {
            case WorldObjectKind.Light:
                objectKind = ObjectKind.Light;
                return true;
            case WorldObjectKind.BgObject:
                objectKind = ObjectKind.BgObject;
                return true;
            case WorldObjectKind.Furniture:
                objectKind = ObjectKind.Furniture;
                return true;
            case WorldObjectKind.Vfx:
                objectKind = ObjectKind.Vfx;
                return true;
            default:
                objectKind = default;
                return false;
        }
    }

    private static bool TryToObjectDataPatch(ObjectKind kind, WorldObjectModelPatch model, out ObjectDataPatch objectDataPatch)
    {
        switch (kind)
        {
            case ObjectKind.BgObject when model.BgObject is not null:
                objectDataPatch = ToPatch(model.BgObject);
                return true;
            case ObjectKind.Furniture when model.Furniture is not null:
                objectDataPatch = ToPatch(model.Furniture);
                return true;
            case ObjectKind.Vfx when model.Vfx is not null:
                objectDataPatch = ToPatch(model.Vfx);
                return true;
            case ObjectKind.Light when model.Light is not null:
                objectDataPatch = ToPatch(model.Light);
                return true;
            default:
                objectDataPatch = null!;
                return false;
        }
    }

    private static bool HasSingleModel(WorldObjectModelData model, ObjectKind expectedKind)
        => TryGetSingleModelKind(
            model.BgObject is not null,
            model.Furniture is not null,
            model.Vfx is not null,
            model.Light is not null,
            out ObjectKind kind)
        && kind == expectedKind;

    private static bool TryGetModelPatchKind(WorldObjectModelPatch model, out ObjectKind kind)
        => TryGetSingleModelKind(
            model.BgObject is not null,
            model.Furniture is not null,
            model.Vfx is not null,
            model.Light is not null,
            out kind);

    private static bool TryGetSingleModelKind(
        bool hasBgObject,
        bool hasFurniture,
        bool hasVfx,
        bool hasLight,
        out ObjectKind kind)
    {
        if ((hasBgObject ? 1 : 0)
            + (hasFurniture ? 1 : 0)
            + (hasVfx ? 1 : 0)
            + (hasLight ? 1 : 0) != 1)
        {
            kind = default;
            return false;
        }

        kind = (hasBgObject, hasFurniture, hasVfx, hasLight) switch
        {
            (true, false, false, false) => ObjectKind.BgObject,
            (false, true, false, false) => ObjectKind.Furniture,
            (false, false, true, false) => ObjectKind.Vfx,
            (false, false, false, true) => ObjectKind.Light,
            _ => default,
        };
        return true;
    }

    private static bool TryToPersistentSet(
        PersistentObjectSet? dto,
        Guid? layoutId,
        out List<ObjectSnapshot> objects,
        out IReadOnlyList<string> folders,
        out IReadOnlyDictionary<string, string> folderColors)
    {
        if (dto?.Objects is null || dto.Folders is null || dto.FolderColors is null)
        {
            objects = [];
            folders = [];
            folderColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }

        objects = new List<ObjectSnapshot>(dto.Objects.Count);
        foreach (PersistentObject? objectDto in dto.Objects)
        {
            if (!TryToPersistentSnapshot(objectDto, out ObjectSnapshot snapshot)
                || snapshot.Id == Guid.Empty
                || snapshot.CreatedAtUtc == default)
            {
                objects = [];
                folders = [];
                folderColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return false;
            }

            objects.Add(snapshot with { LayoutId = layoutId });
        }

        folders = ObjectFolderUtility.OrderFolders(dto.Folders);
        folderColors = ObjectFolderUtility.OrderFolderColorMap(dto.FolderColors, folders);
        return true;
    }

    public static bool TryToPersistentPatch(
        PersistentObjectPatch? dto,
        ObjectKind kind,
        out ObjectSnapshotPatch patch)
    {
        if (dto is null)
        {
            patch = null!;
            return false;
        }

        if (dto.Object is null)
        {
            patch = new ObjectSnapshotPatch();
        }
        else if (!TryToPatch(dto.Object, kind, out patch))
        {
            return false;
        }

        patch = patch with
        {
            FolderPath = dto.FolderPath is not null
                ? ObjectFolderUtility.SanitizeFolderPath(dto.FolderPath)
                : null,
            Locked = dto.Locked,
        };
        return true;
    }

    public static WorldObjectTransform ToWorldTransform(SceneTransform transform)
        => new(
            ToObjectVector3(transform.Position),
            ToObjectVector3(transform.RotationDegrees),
            ToObjectVector3(transform.Scale));

    public static SceneTransform ToTransform(WorldObjectTransform transform)
        => new()
        {
            Position = ToVector3(transform.Position),
            RotationDegrees = ToVector3(transform.RotationDegrees),
            Scale = ToVector3(transform.Scale),
        };

    public static SceneCreationContext ToCreationContext(ObjectLocationData context)
        => new()
        {
            WorldId = context.WorldId,
            WorldName = context.WorldName ?? string.Empty,
            TerritoryId = context.TerritoryId,
            TerritoryName = context.TerritoryName ?? string.Empty,
            DivisionId = context.DivisionId,
            WardId = context.WardId,
            HouseId = context.HouseId,
            RoomId = context.RoomId,
        };

    public static WorldObjectModelData ToWorldModel(ObjectKind kind, ObjectData model)
        => kind switch
        {
            ObjectKind.BgObject when model is BgObjectModel bgObject => ToBgObjectData(bgObject),
            ObjectKind.Furniture when model is FurnitureModel furniture => ToFurnitureData(furniture),
            ObjectKind.Vfx when model is VfxModel vfx => ToVfxData(vfx),
            ObjectKind.Light when model is LightModel light => ToLightData(light),
            _ => throw new InvalidOperationException($"unsupported object model mapping for {kind}"),
        };

    public static bool TryToObjectData(ObjectKind kind, WorldObjectModelData? model, out ObjectData objectData)
    {
        if (model is null || !HasSingleModel(model, kind))
        {
            objectData = null!;
            return false;
        }

        switch (kind)
        {
            case ObjectKind.BgObject when model.BgObject is not null:
                objectData = ToBgObjectModel(model.BgObject);
                return true;
            case ObjectKind.Furniture when model.Furniture is { Color: not null }:
                objectData = ToFurnitureModel(model.Furniture);
                return true;
            case ObjectKind.Vfx when model.Vfx is not null:
                objectData = ToVfxModel(model.Vfx);
                return true;
            case ObjectKind.Light when model.Light is { Flags: not null, Shape: not null, Shadow: not null }:
                objectData = ToLightModel(model.Light);
                return true;
            default:
                objectData = null!;
                return false;
        }
    }

    private static bool TryToTemporaryCollectionRedirect(
        TemporaryObjectCollectionRedirect? dto,
        out ObjectTemporaryCollectionRedirectData redirect)
    {
        if (dto is null || !TryToTemporaryCollectionReplacement(dto.Replacement, out ObjectTemporaryCollectionReplacementData replacement))
        {
            redirect = null!;
            return false;
        }

        redirect = new ObjectTemporaryCollectionRedirectData
        {
            RequestedPath = dto.RequestedPath,
            Replacement = replacement,
        };
        return true;
    }

    private static TemporaryObjectCollectionRedirect ToTemporaryRedirect(ObjectTemporaryCollectionRedirectData redirect)
        => new(
            redirect.RequestedPath,
            ToTemporaryReplacement(redirect.Replacement));

    private static TemporaryCollectionReplacement ToTemporaryReplacement(ObjectTemporaryCollectionReplacementData replacement)
        => new()
        {
            Kind = replacement.Kind switch
            {
                ObjectTemporaryCollectionReplacementKind.GamePath => TemporaryCollectionReplacementKind.GamePath,
                ObjectTemporaryCollectionReplacementKind.LocalFile => TemporaryCollectionReplacementKind.LocalFile,
                ObjectTemporaryCollectionReplacementKind.Memory => TemporaryCollectionReplacementKind.Memory,
                _ => throw new ArgumentOutOfRangeException(nameof(replacement), replacement.Kind, null),
            },
            Path = replacement.Path,
            Data = [.. replacement.Data],
        };

    private static bool TryToTemporaryCollectionReplacement(
        TemporaryCollectionReplacement? dto,
        out ObjectTemporaryCollectionReplacementData replacement)
    {
        if (dto is null)
        {
            replacement = null!;
            return false;
        }

        switch (dto.Kind)
        {
            case TemporaryCollectionReplacementKind.GamePath:
                replacement = new ObjectTemporaryCollectionReplacementData
                {
                    Kind = ObjectTemporaryCollectionReplacementKind.GamePath,
                    Path = dto.Path,
                };
                return true;
            case TemporaryCollectionReplacementKind.LocalFile:
                replacement = new ObjectTemporaryCollectionReplacementData
                {
                    Kind = ObjectTemporaryCollectionReplacementKind.LocalFile,
                    Path = dto.Path,
                };
                return true;
            case TemporaryCollectionReplacementKind.Memory:
                if (dto.Data is null)
                {
                    replacement = null!;
                    return false;
                }

                replacement = new ObjectTemporaryCollectionReplacementData
                {
                    Kind = ObjectTemporaryCollectionReplacementKind.Memory,
                    Path = dto.Path,
                    Data = [.. dto.Data],
                };
                return true;
            default:
                replacement = null!;
                return false;
        }
    }

    private static RuntimeObjectStateKindDto ToRuntimeStateKind(RuntimeObjectStateKindModel state)
        => state switch
        {
            RuntimeObjectStateKindModel.Active => RuntimeObjectStateKindDto.Active,
            RuntimeObjectStateKindModel.Inactive => RuntimeObjectStateKindDto.Inactive,
            RuntimeObjectStateKindModel.LocationMismatch => RuntimeObjectStateKindDto.LocationMismatch,
            RuntimeObjectStateKindModel.LoadFailed => RuntimeObjectStateKindDto.LoadFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };

    public static TemporarySourceMutationStatus ToTemporaryMutationStatus(ObjectTemporaryMutationStatus status)
        => status switch
        {
            ObjectTemporaryMutationStatus.Success => TemporarySourceMutationStatus.Success,
            ObjectTemporaryMutationStatus.InvalidSource => TemporarySourceMutationStatus.InvalidSource,
            ObjectTemporaryMutationStatus.InvalidObject => TemporarySourceMutationStatus.InvalidObject,
            ObjectTemporaryMutationStatus.StaleRevision => TemporarySourceMutationStatus.StaleRevision,
            ObjectTemporaryMutationStatus.ObjectNotFound => TemporarySourceMutationStatus.ObjectNotFound,
            ObjectTemporaryMutationStatus.SourceMismatch => TemporarySourceMutationStatus.SourceMismatch,
            ObjectTemporaryMutationStatus.RuntimeApplyFailed => TemporarySourceMutationStatus.RuntimeApplyFailed,
            ObjectTemporaryMutationStatus.AlreadyApplied => TemporarySourceMutationStatus.AlreadyApplied,
            ObjectTemporaryMutationStatus.InvalidCollection => TemporarySourceMutationStatus.InvalidCollection,
            ObjectTemporaryMutationStatus.IdentityConflict => TemporarySourceMutationStatus.IdentityConflict,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    private static BgObjectModelPatch ToPatch(BgObjectModelPatchData model)
        => new()
        {
            ModelPath = model.ModelPath,
            Transparency = model.Transparency,
            DyeColor = ToVector4(model.DyeColor),
            IsCoveredFromRain = model.IsCoveredFromRain,
        };

    private static FurnitureModelPatch ToPatch(FurnitureModelPatchData model)
        => new()
        {
            SharedGroupPath = model.SharedGroupPath,
            Color = ToPatch(model.Color),
            Transparency = model.Transparency,
            OutlineColor = ToOutlineColor(model.OutlineColor),
        };

    private static FurnitureColorPatch? ToPatch(FurnitureColorPatchData? color)
        => color is null
            ? null
            : new FurnitureColorPatch
            {
                StainId = color.StainId,
                UseCustomColor = color.UseCustomColor,
                CustomColor = ToVector4(color.CustomColor),
            };

    private static VfxModelPatch ToPatch(VfxModelPatchData model)
        => new()
        {
            VfxPath = model.VfxPath,
            Color = ToVector4(model.Color),
            Speed = model.Speed,
            Paused = model.Paused,
            FadeInSeconds = model.FadeInSeconds,
            ReplayOnTransform = model.ReplayOnTransform,
            Loop = model.Loop,
            LoopIntervalSeconds = model.LoopIntervalSeconds,
        };

    private static LightModelPatch ToPatch(LightModelPatchData model)
        => new()
        {
            Color = ToVector3(model.Color),
            LightType = model.LightType.HasValue
                ? (LightType)model.LightType.Value
                : null,
            FalloffType = model.FalloffType.HasValue
                ? (LightFalloffType)model.FalloffType.Value
                : null,
            Flags = ToPatch(model.Flags),
            Intensity = model.Intensity,
            Shape = ToPatch(model.Shape),
            Shadow = ToPatch(model.Shadow),
        };

    private static LightFlagsPatch? ToPatch(LightFlagsPatchData? flags)
        => flags is null
            ? null
            : new LightFlagsPatch
            {
                EnableMaterialReflection = flags.EnableMaterialReflection,
                EnableDynamicLighting = flags.EnableDynamicLighting,
                EnableCharacterShadow = flags.EnableCharacterShadow,
                EnableObjectShadow = flags.EnableObjectShadow,
            };

    private static LightShapePatch? ToPatch(LightShapePatchData? shape)
        => shape is null
            ? null
            : new LightShapePatch
            {
                Range = shape.Range,
                Falloff = shape.Falloff,
                LightAngle = shape.LightAngle,
                FalloffAngle = shape.FalloffAngle,
                AngleDegrees = ToVector2(shape.AngleDegrees),
            };

    private static LightShadowPatch? ToPatch(LightShadowPatchData? shadow)
        => shadow is null
            ? null
            : new LightShadowPatch
            {
                CharacterShadowRange = shadow.CharacterShadowRange,
                ShadowPlaneNear = shadow.ShadowPlaneNear,
                ShadowPlaneFar = shadow.ShadowPlaneFar,
            };

    private static WorldObjectModelData ToBgObjectData(BgObjectModel model)
        => new(BgObject: new BgObjectModelData(
            model.ModelPath,
            model.Transparency,
            ToObjectVector4(model.DyeColor),
            model.IsCoveredFromRain));

    private static WorldObjectModelData ToFurnitureData(FurnitureModel model)
        => new(Furniture: new FurnitureModelData(
            model.SharedGroupPath,
            ToFurnitureColorData(model.Color),
            model.Transparency,
            ToApiOutlineColor(model.OutlineColor),
            model.HousingRowId,
            model.ItemRowId,
            model.AttachmentParentId,
            ToFurnitureMaterialItemDto(model.MaterialItem)));

    private static WorldObjectModelData ToVfxData(VfxModel model)
        => new(Vfx: new VfxModelData(
            model.VfxPath,
            ToObjectVector4(model.Color),
            model.Speed,
            model.Paused,
            model.FadeInSeconds,
            model.ReplayOnTransform,
            model.Loop,
            model.LoopIntervalSeconds));

    private static WorldObjectModelData ToLightData(LightModel model)
        => new(Light: new LightModelData(
            ToObjectVector3(model.Color),
            (ObjectLightType)model.LightType,
            (ObjectLightFalloffType)model.FalloffType,
            ToLightFlagsData(model.Flags),
            model.Intensity,
            ToLightShapeData(model.Shape),
            ToLightShadowData(model.Shadow)));

    private static FurnitureColorData ToFurnitureColorData(FurnitureColorModel color)
        => new(
            color.StainId,
            color.UseCustomColor,
            ToObjectVector4(color.CustomColor));

    private static LightFlagsData ToLightFlagsData(LightFlags flags)
        => new(
            flags.EnableMaterialReflection,
            flags.EnableDynamicLighting,
            flags.EnableCharacterShadow,
            flags.EnableObjectShadow);

    private static LightShapeData ToLightShapeData(LightShape shape)
        => new(
            shape.Range,
            shape.Falloff,
            shape.LightAngle,
            shape.FalloffAngle,
            ToObjectVector2(shape.AngleDegrees));

    private static LightShadowData ToLightShadowData(LightShadow shadow)
        => new(
            shadow.CharacterShadowRange,
            shadow.ShadowPlaneNear,
            shadow.ShadowPlaneFar);

    private static BgObjectModel ToBgObjectModel(BgObjectModelData model)
        => new()
        {
            ModelPath = model.ModelPath ?? string.Empty,
            Transparency = model.Transparency,
            DyeColor = ToVector4(model.DyeColor),
            IsCoveredFromRain = model.IsCoveredFromRain,
        };

    private static FurnitureModel ToFurnitureModel(FurnitureModelData model)
        => new()
        {
            SharedGroupPath = model.SharedGroupPath ?? string.Empty,
            Color = ToFurnitureColorModel(model.Color),
            Transparency = model.Transparency,
            OutlineColor = ToOutlineColor(model.OutlineColor),
            HousingRowId = model.HousingRowId,
            ItemRowId = model.ItemRowId,
            AttachmentParentId = model.AttachmentParentId,
            MaterialItem = ToFurnitureMaterialItemModel(model.MaterialItem),
        };

    private static FurnitureColorModel ToFurnitureColorModel(FurnitureColorData color)
        => new()
        {
            StainId = color.StainId,
            UseCustomColor = color.UseCustomColor,
            CustomColor = ToVector4(color.CustomColor),
        };

    private static FurnitureMaterialItemData? ToFurnitureMaterialItemDto(FurnitureMaterialItemModel? material)
        => material is null
            ? null
            : new FurnitureMaterialItemData(material.Name, material.ItemId);

    private static FurnitureMaterialItemModel? ToFurnitureMaterialItemModel(FurnitureMaterialItemData? material)
        => material is null
            ? null
            : new FurnitureMaterialItemModel
            {
                Name = material.Name ?? string.Empty,
                ItemId = material.ItemId,
            };

    private static VfxModel ToVfxModel(VfxModelData model)
        => new()
        {
            VfxPath = model.VfxPath ?? string.Empty,
            Color = ToVector4(model.Color),
            Speed = model.Speed,
            Paused = model.Paused,
            FadeInSeconds = model.FadeInSeconds,
            ReplayOnTransform = model.ReplayOnTransform,
            Loop = model.Loop,
            LoopIntervalSeconds = model.LoopIntervalSeconds,
        };

    private static LightModel ToLightModel(LightModelData model)
        => new()
        {
            Color = ToVector3(model.Color),
            LightType = (LightType)model.LightType,
            FalloffType = (LightFalloffType)model.FalloffType,
            Flags = ToLightFlagsModel(model.Flags),
            Intensity = model.Intensity,
            Shape = ToLightShapeModel(model.Shape),
            Shadow = ToLightShadowModel(model.Shadow),
        };

    private static LightFlags ToLightFlagsModel(LightFlagsData flags)
        => new()
        {
            EnableMaterialReflection = flags.EnableMaterialReflection,
            EnableDynamicLighting = flags.EnableDynamicLighting,
            EnableCharacterShadow = flags.EnableCharacterShadow,
            EnableObjectShadow = flags.EnableObjectShadow,
        };

    private static LightShape ToLightShapeModel(LightShapeData shape)
        => new()
        {
            Range = shape.Range,
            Falloff = shape.Falloff,
            LightAngle = shape.LightAngle,
            FalloffAngle = shape.FalloffAngle,
            AngleDegrees = ToVector2(shape.AngleDegrees),
        };

    private static LightShadow ToLightShadowModel(LightShadowData shadow)
        => new()
        {
            CharacterShadowRange = shadow.CharacterShadowRange,
            ShadowPlaneNear = shadow.ShadowPlaneNear,
            ShadowPlaneFar = shadow.ShadowPlaneFar,
        };

    private static ObjectVector2 ToObjectVector2(Vector2 value)
        => new(value.X, value.Y);

    public static ObjectVector3 ToObjectVector3(Vector3 value)
        => new(value.X, value.Y, value.Z);

    private static ObjectVector4 ToObjectVector4(Vector4 value)
        => new(value.X, value.Y, value.Z, value.W);

    private static ObjectOutlineColorApi ToApiOutlineColor(ObjectOutlineColorModel value)
        => (ObjectOutlineColorApi)value;

    private static Vector2 ToVector2(ObjectVector2 value)
        => new(value.X, value.Y);

    private static Vector2? ToVector2(ObjectVector2? value)
        => value.HasValue
            ? ToVector2(value.Value)
            : null;

    public static Vector3 ToVector3(ObjectVector3 value)
        => new(value.X, value.Y, value.Z);

    private static Vector3? ToVector3(ObjectVector3? value)
        => value.HasValue
            ? ToVector3(value.Value)
            : null;

    private static Vector4 ToVector4(ObjectVector4 value)
        => new(value.X, value.Y, value.Z, value.W);

    private static Vector4? ToVector4(ObjectVector4? value)
        => value.HasValue
            ? ToVector4(value.Value)
            : null;

    private static ObjectOutlineColorModel ToOutlineColor(ObjectOutlineColorApi value)
        => (ObjectOutlineColorModel)value;

    private static ObjectOutlineColorModel? ToOutlineColor(ObjectOutlineColorApi? value)
        => value.HasValue
            ? ToOutlineColor(value.Value)
            : null;
}
