using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Scene;

namespace Intoner.Objects.Utils;

internal static class ObjectSnapshotUtility
{
    /// <summary> copies snapshots with fresh ids, remapping internal attachments and clearing external attachments </summary>
    public static bool TryCopyWithNewIds(IReadOnlyList<ObjectSnapshot> snapshots, out List<ObjectSnapshot> copies)
        => TryCopyWithNewIds(snapshots, [], out copies, out _);

    /// <summary> copies objects and groups together, detaching membership and attachments outside the copied set </summary>
    public static bool TryCopyWithNewIds(
        IReadOnlyList<ObjectSnapshot> snapshots,
        IReadOnlyList<SceneGroupSnapshot> groups,
        out List<ObjectSnapshot> copies,
        out List<SceneGroupSnapshot> groupCopies)
    {
        copies = [];
        groupCopies = [];
        Dictionary<Guid, Guid> groupIds = new(groups.Count);
        foreach (Guid id in groups.Select(static group => group.Id))
        {
            if (id == Guid.Empty || !groupIds.TryAdd(id, Guid.NewGuid()))
                return false;
        }
        Dictionary<Guid, Guid> copiedIds = new(snapshots.Count);
        foreach (Guid id in snapshots.Select(static snapshot => snapshot.Id))
        {
            if (id == Guid.Empty || groupIds.ContainsKey(id) || !copiedIds.TryAdd(id, Guid.NewGuid()))
            {
                return false;
            }
        }

        copies = new List<ObjectSnapshot>(snapshots.Count);
        foreach (ObjectSnapshot snapshot in snapshots)
        {
            ObjectData model = snapshot.Model;

            if (model is FurnitureModel { AttachmentParentId: { } parentId } furniture)
            {
                model = furniture with
                {
                    AttachmentParentId = copiedIds.TryGetValue(parentId, out Guid copiedParentId)
                        ? copiedParentId
                        : null,
                };
            }

            copies.Add(snapshot with
            {
                Id = copiedIds[snapshot.Id], Model = model,
                GroupId = snapshot.GroupId is Guid groupId && groupIds.TryGetValue(groupId, out Guid copiedGroupId) ? copiedGroupId : null,
            });
        }

        groupCopies = groups.Select(group => group with { Id = groupIds[group.Id] }).ToList();

        return true;
    }

    public static bool IsCollectionOnlyChange(ObjectSnapshot previousSnapshot, ObjectSnapshot nextSnapshot)
        => !string.Equals(previousSnapshot.CollectionId, nextSnapshot.CollectionId, StringComparison.OrdinalIgnoreCase)
            && previousSnapshot with { CollectionId = nextSnapshot.CollectionId } == nextSnapshot;

    public static ObjectSnapshot ApplyPatch(ObjectSnapshot snapshot, ObjectSnapshotPatch patch)
    {
        if (!patch.HasChanges)
        {
            return snapshot;
        }

        return snapshot with
        {
            Name = patch.Name ?? snapshot.Name,
            FolderPath = patch.FolderPath ?? snapshot.FolderPath,
            Locked = patch.Locked ?? snapshot.Locked,
            Visible = patch.Visible ?? snapshot.Visible,
            Transform = patch.Transform ?? snapshot.Transform,
            Model = patch.Model is null ? snapshot.Model : ApplyModelPatch(snapshot.Model, patch.Model),
        };
    }

    public static string GetAssetName(ObjectSnapshot snapshot)
        => snapshot.Model switch
        {
            BgObjectModel bgObjectModel => Path.GetFileName(bgObjectModel.ModelPath),
            FurnitureModel furnitureModel => Path.GetFileName(furnitureModel.SharedGroupPath),
            VfxModel vfxModel => Path.GetFileName(vfxModel.VfxPath),
            LightModel _ => "light",
            _ => snapshot.Kind.ToString(),
        };

    public static string GetRootResourcePath(ObjectSnapshot snapshot)
        => GetRootResourcePath(snapshot.Model);

    public static string GetRootResourcePath(ObjectData model)
        => ObjectResolvedPath.FromGamePath(GetDeclaredRootResourcePath(model)).Path;

    public static bool TryNormalizeRootResourcePath(ObjectData model, out ObjectData normalizedModel)
    {
        string path = GetDeclaredRootResourcePath(model);
        string normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;

        if (!string.IsNullOrWhiteSpace(path) && normalizedPath.Length == 0)
        {
            normalizedModel = null!;
            return false;
        }

        if (string.Equals(path, normalizedPath, StringComparison.Ordinal))
        {
            normalizedModel = model;
            return true;
        }

        normalizedModel = model switch
        {
            BgObjectModel bgObject => bgObject with { ModelPath = normalizedPath },
            FurnitureModel furniture => furniture with { SharedGroupPath = normalizedPath },
            VfxModel vfx => vfx with { VfxPath = normalizedPath },
            LightModel light => light with { ProjectionTexturePath = normalizedPath },
            _ => model,
        };
        return true;
    }

    private static string GetDeclaredRootResourcePath(ObjectData model)
        => model switch
        {
            BgObjectModel bgObjectModel => bgObjectModel.ModelPath,
            FurnitureModel furnitureModel => furnitureModel.SharedGroupPath,
            VfxModel vfxModel => vfxModel.VfxPath,
            LightModel lightModel => lightModel.ProjectionTexturePath,
            _ => string.Empty,
        };

    private static ObjectData ApplyModelPatch(ObjectData model, ObjectDataPatch patch)
        => (model, patch) switch
        {
            (BgObjectModel bgObjectModel, BgObjectModelPatch bgObjectPatch) => ApplyBgObjectModelPatch(bgObjectModel, bgObjectPatch),
            (FurnitureModel furnitureModel, FurnitureModelPatch furniturePatch) => ApplyFurnitureModelPatch(furnitureModel, furniturePatch),
            (VfxModel vfxModel, VfxModelPatch vfxPatch) => ApplyVfxModelPatch(vfxModel, vfxPatch),
            (LightModel lightModel, LightModelPatch lightPatch) => ApplyLightModelPatch(lightModel, lightPatch),
            _ => model,
        };

    private static BgObjectModel ApplyBgObjectModelPatch(BgObjectModel model, BgObjectModelPatch patch)
        => model with
        {
            ModelPath = patch.ModelPath ?? model.ModelPath,
            CameraCollision = patch.CameraCollision ?? model.CameraCollision,
            ColliderDefinition = patch.ClearColliderDefinition ? null : patch.ColliderDefinition
                ?? (patch.ModelPath is not null && !string.Equals(
                    patch.ModelPath, model.ModelPath, StringComparison.OrdinalIgnoreCase) ? null : model.ColliderDefinition),
            Transparency = patch.Transparency ?? model.Transparency,
            DyeColor = patch.DyeColor ?? model.DyeColor,
            IsCoveredFromRain = patch.IsCoveredFromRain ?? model.IsCoveredFromRain,
        };

    private static FurnitureModel ApplyFurnitureModelPatch(FurnitureModel model, FurnitureModelPatch patch)
        => model with
        {
            SharedGroupPath = patch.SharedGroupPath ?? model.SharedGroupPath,
            CameraCollision = patch.CameraCollision ?? model.CameraCollision,
            Color = patch.Color is not null
                ? ApplyFurnitureColorPatch(model.Color, patch.Color)
                : model.Color,
            Transparency = patch.Transparency ?? model.Transparency,
            OutlineColor = patch.OutlineColor ?? model.OutlineColor,
        };

    private static FurnitureColorModel ApplyFurnitureColorPatch(FurnitureColorModel model, FurnitureColorPatch patch)
        => model with
        {
            StainId = patch.StainId ?? model.StainId,
            UseCustomColor = patch.UseCustomColor ?? model.UseCustomColor,
            CustomColor = patch.CustomColor ?? model.CustomColor,
        };

    private static VfxModel ApplyVfxModelPatch(VfxModel model, VfxModelPatch patch)
        => model with
        {
            VfxPath = patch.VfxPath ?? model.VfxPath,
            Color = patch.Color ?? model.Color,
            Speed = patch.Speed.HasValue ? VfxModel.ClampSpeed(patch.Speed.Value) : model.Speed,
            Paused = patch.Paused ?? model.Paused,
            FadeInSeconds = patch.FadeInSeconds.HasValue
                ? VfxModel.ClampFadeInSeconds(patch.FadeInSeconds.Value)
                : model.FadeInSeconds,
            ReplayOnTransform = patch.ReplayOnTransform ?? model.ReplayOnTransform,
            Loop = patch.Loop ?? model.Loop,
            LoopIntervalSeconds = patch.LoopIntervalSeconds.HasValue
                ? VfxModel.ClampLoopIntervalSeconds(patch.LoopIntervalSeconds.Value)
                : model.LoopIntervalSeconds,
        };

    private static LightModel ApplyLightModelPatch(LightModel model, LightModelPatch patch)
        => model with
        {
            Color = patch.Color ?? model.Color,
            LightType = patch.LightType ?? model.LightType,
            FalloffType = patch.FalloffType ?? model.FalloffType,
            Flags = patch.Flags is not null
                ? ApplyLightFlagsPatch(model.Flags, patch.Flags)
                : model.Flags,
            Intensity = patch.Intensity ?? model.Intensity,
            Shape = patch.Shape is not null
                ? ApplyLightShapePatch(model.Shape, patch.Shape)
                : model.Shape,
            Shadow = patch.Shadow is not null
                ? ApplyLightShadowPatch(model.Shadow, patch.Shadow)
                : model.Shadow,
            ProjectionTexturePath = patch.ProjectionTexturePath ?? model.ProjectionTexturePath,
        };

    private static LightFlags ApplyLightFlagsPatch(LightFlags model, LightFlagsPatch patch)
        => model with
        {
            EnableMaterialReflection = patch.EnableMaterialReflection ?? model.EnableMaterialReflection,
            EnableDynamicLighting = patch.EnableDynamicLighting ?? model.EnableDynamicLighting,
            EnableCharacterShadow = patch.EnableCharacterShadow ?? model.EnableCharacterShadow,
            EnableObjectShadow = patch.EnableObjectShadow ?? model.EnableObjectShadow,
        };

    private static LightShape ApplyLightShapePatch(LightShape model, LightShapePatch patch)
        => model with
        {
            Range = patch.Range ?? model.Range,
            Falloff = patch.Falloff ?? model.Falloff,
            LightAngle = patch.LightAngle ?? model.LightAngle,
            FalloffAngle = patch.FalloffAngle ?? model.FalloffAngle,
            AngleDegrees = patch.AngleDegrees ?? model.AngleDegrees,
        };

    private static LightShadow ApplyLightShadowPatch(LightShadow model, LightShadowPatch patch)
        => model with
        {
            CharacterShadowRange = patch.CharacterShadowRange ?? model.CharacterShadowRange,
            ShadowPlaneNear = patch.ShadowPlaneNear ?? model.ShadowPlaneNear,
            ShadowPlaneFar = patch.ShadowPlaneFar ?? model.ShadowPlaneFar,
        };
}
