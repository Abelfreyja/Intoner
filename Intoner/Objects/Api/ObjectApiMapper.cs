using Intoner.Objects.Models;
using System.Numerics;
using ObjectOutlineColorApi = Intoner.Objects.Api.ObjectOutlineColor;
using ObjectOutlineColorModel = Intoner.Objects.Models.ObjectOutlineColor;
using RuntimeObjectStateKindDto = Intoner.Objects.Api.RuntimeObjectStateKind;
using RuntimeObjectStateKindModel = Intoner.Objects.Models.ObjectRuntimeStateKind;

namespace Intoner.Objects.Api;

internal static class ObjectApiMapper
{
    public static WorldObject ToDto(ObjectSnapshot snapshot)
        => new(
            snapshot.Id,
            snapshot.Name,
            ToDto(snapshot.Kind),
            snapshot.Visible,
            ToDto(snapshot.Transform),
            snapshot.CreatedAtUtc,
            ToCreationDataDto(snapshot.CreatedIn),
            snapshot.CollectionId,
            ToDto(snapshot.Kind, snapshot.Model));

    public static SavedObjectLayout ToDto(ObjectLayoutSnapshot layout)
        => new(
            layout.Id,
            layout.Name,
            layout.CreatedAtUtc,
            layout.UpdatedAtUtc,
            layout.Objects.Select(ToDto).ToList());

    public static LoadedObjectLayout ToDto(ObjectLoadedLayoutSnapshot layout)
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
            layout.Objects.Select(ToDto).ToList());

    public static ObjectLocationData ToLocationDto(ObjectCreationContext context)
        => new(
            context.WorldId,
            context.TerritoryId,
            context.WorldName,
            context.TerritoryName,
            context.DivisionId,
            context.WardId,
            context.HouseId,
            context.RoomId);

    public static RuntimeObjectState ToDto(ObjectRuntimeStateSnapshot snapshot)
        => new(
            snapshot.Id,
            ToDto(snapshot.State),
            snapshot.FailureCode);

    public static TemporarySourceMutationResult ToDto(ObjectTemporaryMutationResult result)
        => new(
            ToDto(result.Status),
            result.SourceRevision);

    public static TemporaryObjectCollection ToDto(ObjectTemporaryCollectionData collection)
        => new(
            collection.CollectionId,
            collection.Name,
            collection.Redirects.Select(ToDto).ToList());

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
            || !TryToObjectKind(dto.Kind, out var kind)
            || !TryToObjectData(kind, dto.Model, out var model))
        {
            snapshot = null!;
            return false;
        }

        snapshot = new ObjectSnapshot
        {
            Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            Name = dto.Name,
            Kind = kind,
            Visible = dto.Visible,
            Transform = ToTransform(dto.Transform),
            CreatedAtUtc = dto.CreatedAtUtc == default ? DateTime.UtcNow : dto.CreatedAtUtc,
            CreatedIn = ToCreationContext(dto.CreatedIn),
            CollectionId = dto.CollectionId,
            LayoutId = null,
            Model = model,
        };
        return true;
    }

    public static bool TryToDetachedSnapshot(WorldObject? dto, out ObjectSnapshot snapshot)
    {
        if (!TryToSnapshot(dto, out snapshot))
        {
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

    public static bool TryToPatch(WorldObjectPatch dto, ObjectKind kind, out ObjectSnapshotPatch patch)
    {
        ObjectDataPatch? model = null;
        if (dto.Model is not null
            && !TryToObjectDataPatch(kind, dto.Model, out model))
        {
            patch = null!;
            return false;
        }

        patch = new ObjectSnapshotPatch
        {
            Name = dto.Name,
            Visible = dto.Visible,
            Transform = dto.Transform is not null
                ? ToTransform(dto.Transform)
                : null,
            Model = model,
        };
        return true;
    }

    private static WorldObjectKind ToDto(ObjectKind kind)
        => kind switch
        {
            ObjectKind.Light => WorldObjectKind.Light,
            ObjectKind.BgObject => WorldObjectKind.BgObject,
            ObjectKind.Furniture => WorldObjectKind.Furniture,
            ObjectKind.Vfx => WorldObjectKind.Vfx,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static bool TryToObjectKind(WorldObjectKind kind, out ObjectKind objectKind)
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

    private static WorldObjectTransform ToDto(ObjectTransform transform)
        => new(
            ToDto(transform.Position),
            ToDto(transform.RotationDegrees),
            ToDto(transform.Scale));

    private static ObjectTransform ToTransform(WorldObjectTransform transform)
        => new()
        {
            Position = ToVector3(transform.Position),
            RotationDegrees = ToVector3(transform.RotationDegrees),
            Scale = ToVector3(transform.Scale),
        };

    private static ObjectCreationData ToCreationDataDto(ObjectCreationContext context)
        => new(
            context.WorldId,
            context.WorldName,
            context.TerritoryId,
            context.TerritoryName,
            context.DivisionId,
            context.WardId,
            context.HouseId,
            context.RoomId);

    private static ObjectCreationContext ToCreationContext(ObjectCreationData context)
        => new()
        {
            WorldId = context.WorldId,
            WorldName = context.WorldName,
            TerritoryId = context.TerritoryId,
            TerritoryName = context.TerritoryName,
            DivisionId = context.DivisionId,
            WardId = context.WardId,
            HouseId = context.HouseId,
            RoomId = context.RoomId,
        };

    private static WorldObjectModelData ToDto(ObjectKind kind, ObjectData model)
        => kind switch
        {
            ObjectKind.BgObject when model is BgObjectModel bgObject => ToDto(bgObject),
            ObjectKind.Furniture when model is FurnitureModel furniture => ToDto(furniture),
            ObjectKind.Vfx when model is VfxModel vfx => ToDto(vfx),
            ObjectKind.Light when model is LightModel light => ToDto(light),
            _ => throw new InvalidOperationException($"unsupported object model mapping for {kind}"),
        };

    private static bool TryToObjectData(ObjectKind kind, WorldObjectModelData? model, out ObjectData objectData)
    {
        if (model is null)
        {
            objectData = null!;
            return false;
        }

        switch (kind)
        {
            case ObjectKind.BgObject when model.BgObject is not null:
                objectData = ToModel(model.BgObject);
                return true;
            case ObjectKind.Furniture when model.Furniture is not null:
                objectData = ToModel(model.Furniture);
                return true;
            case ObjectKind.Vfx when model.Vfx is not null:
                objectData = ToModel(model.Vfx);
                return true;
            case ObjectKind.Light when model.Light is not null:
                objectData = ToModel(model.Light);
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

    private static TemporaryObjectCollectionRedirect ToDto(ObjectTemporaryCollectionRedirectData redirect)
        => new(
            redirect.RequestedPath,
            ToDto(redirect.Replacement));

    private static TemporaryCollectionReplacement ToDto(ObjectTemporaryCollectionReplacementData replacement)
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
            Data = replacement.Data,
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
                    Data = dto.Data,
                };
                return true;
            default:
                replacement = null!;
                return false;
        }
    }

    private static RuntimeObjectStateKindDto ToDto(RuntimeObjectStateKindModel state)
        => state switch
        {
            RuntimeObjectStateKindModel.Active => RuntimeObjectStateKindDto.Active,
            RuntimeObjectStateKindModel.Inactive => RuntimeObjectStateKindDto.Inactive,
            RuntimeObjectStateKindModel.LocationMismatch => RuntimeObjectStateKindDto.LocationMismatch,
            RuntimeObjectStateKindModel.LoadFailed => RuntimeObjectStateKindDto.LoadFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };

    private static TemporarySourceMutationStatus ToDto(ObjectTemporaryMutationStatus status)
        => status switch
        {
            ObjectTemporaryMutationStatus.Success => TemporarySourceMutationStatus.Success,
            ObjectTemporaryMutationStatus.InvalidSource => TemporarySourceMutationStatus.InvalidSource,
            ObjectTemporaryMutationStatus.InvalidObject => TemporarySourceMutationStatus.InvalidObject,
            ObjectTemporaryMutationStatus.StaleRevision => TemporarySourceMutationStatus.StaleRevision,
            ObjectTemporaryMutationStatus.ObjectNotFound => TemporarySourceMutationStatus.ObjectNotFound,
            ObjectTemporaryMutationStatus.SourceMismatch => TemporarySourceMutationStatus.SourceMismatch,
            ObjectTemporaryMutationStatus.RuntimeApplyFailed => TemporarySourceMutationStatus.RuntimeApplyFailed,
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

    private static WorldObjectModelData ToDto(BgObjectModel model)
        => new(BgObject: new BgObjectModelData(
            model.ModelPath,
            model.Transparency,
            ToDto(model.DyeColor),
            model.IsCoveredFromRain));

    private static WorldObjectModelData ToDto(FurnitureModel model)
        => new(Furniture: new FurnitureModelData(
            model.SharedGroupPath,
            ToDto(model.Color),
            model.Transparency,
            ToDto(model.OutlineColor),
            model.HousingRowId,
            model.ItemRowId,
            model.AttachmentParentId,
            ToFurnitureMaterialItemDto(model.MaterialItem)));

    private static WorldObjectModelData ToDto(VfxModel model)
        => new(Vfx: new VfxModelData(
            model.VfxPath,
            ToDto(model.Color),
            model.Loop,
            model.LoopIntervalSeconds));

    private static WorldObjectModelData ToDto(LightModel model)
        => new(Light: new LightModelData(
            ToDto(model.Color),
            (ObjectLightType)model.LightType,
            (ObjectLightFalloffType)model.FalloffType,
            ToDto(model.Flags),
            model.Intensity,
            ToDto(model.Shape),
            ToDto(model.Shadow)));

    private static FurnitureColorData ToDto(FurnitureColorModel color)
        => new(
            color.StainId,
            color.UseCustomColor,
            ToDto(color.CustomColor));

    private static LightFlagsData ToDto(LightFlags flags)
        => new(
            flags.EnableMaterialReflection,
            flags.EnableDynamicLighting,
            flags.EnableCharacterShadow,
            flags.EnableObjectShadow);

    private static LightShapeData ToDto(LightShape shape)
        => new(
            shape.Range,
            shape.Falloff,
            shape.LightAngle,
            shape.FalloffAngle,
            ToDto(shape.AngleDegrees));

    private static LightShadowData ToDto(LightShadow shadow)
        => new(
            shadow.CharacterShadowRange,
            shadow.ShadowPlaneNear,
            shadow.ShadowPlaneFar);

    private static BgObjectModel ToModel(BgObjectModelData model)
        => new()
        {
            ModelPath = model.ModelPath,
            Transparency = model.Transparency,
            DyeColor = ToVector4(model.DyeColor),
            IsCoveredFromRain = model.IsCoveredFromRain,
        };

    private static FurnitureModel ToModel(FurnitureModelData model)
        => new()
        {
            SharedGroupPath = model.SharedGroupPath,
            Color = ToModel(model.Color),
            Transparency = model.Transparency,
            OutlineColor = ToOutlineColor(model.OutlineColor),
            HousingRowId = model.HousingRowId,
            ItemRowId = model.ItemRowId,
            AttachmentParentId = model.AttachmentParentId,
            MaterialItem = ToFurnitureMaterialItemModel(model.MaterialItem),
        };

    private static FurnitureColorModel ToModel(FurnitureColorData color)
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
                Name = material.Name,
                ItemId = material.ItemId,
            };

    private static VfxModel ToModel(VfxModelData model)
        => new()
        {
            VfxPath = model.VfxPath,
            Color = ToVector4(model.Color),
            Loop = model.Loop,
            LoopIntervalSeconds = model.LoopIntervalSeconds,
        };

    private static LightModel ToModel(LightModelData model)
        => new()
        {
            Color = ToVector3(model.Color),
            LightType = (LightType)model.LightType,
            FalloffType = (LightFalloffType)model.FalloffType,
            Flags = ToModel(model.Flags),
            Intensity = model.Intensity,
            Shape = ToModel(model.Shape),
            Shadow = ToModel(model.Shadow),
        };

    private static LightFlags ToModel(LightFlagsData flags)
        => new()
        {
            EnableMaterialReflection = flags.EnableMaterialReflection,
            EnableDynamicLighting = flags.EnableDynamicLighting,
            EnableCharacterShadow = flags.EnableCharacterShadow,
            EnableObjectShadow = flags.EnableObjectShadow,
        };

    private static LightShape ToModel(LightShapeData shape)
        => new()
        {
            Range = shape.Range,
            Falloff = shape.Falloff,
            LightAngle = shape.LightAngle,
            FalloffAngle = shape.FalloffAngle,
            AngleDegrees = ToVector2(shape.AngleDegrees),
        };

    private static LightShadow ToModel(LightShadowData shadow)
        => new()
        {
            CharacterShadowRange = shadow.CharacterShadowRange,
            ShadowPlaneNear = shadow.ShadowPlaneNear,
            ShadowPlaneFar = shadow.ShadowPlaneFar,
        };

    private static ObjectVector2 ToDto(Vector2 value)
        => new(value.X, value.Y);

    private static ObjectVector3 ToDto(Vector3 value)
        => new(value.X, value.Y, value.Z);

    private static ObjectVector4 ToDto(Vector4 value)
        => new(value.X, value.Y, value.Z, value.W);

    private static ObjectOutlineColorApi ToDto(ObjectOutlineColorModel value)
        => (ObjectOutlineColorApi)value;

    private static Vector2 ToVector2(ObjectVector2 value)
        => new(value.X, value.Y);

    private static Vector2? ToVector2(ObjectVector2? value)
        => value.HasValue
            ? ToVector2(value.Value)
            : null;

    private static Vector3 ToVector3(ObjectVector3 value)
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

