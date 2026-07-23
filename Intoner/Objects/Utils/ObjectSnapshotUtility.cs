using Intoner.Objects.Assets;
using Intoner.Objects.Models;

namespace Intoner.Objects.Utils;

internal static class ObjectSnapshotUtility
{
    public static bool MatchesLocation(ObjectSnapshot snapshot, ObjectLocationScope currentLocation)
    {
        if (!snapshot.CreatedIn.IsValid)
        {
            return true;
        }

        return snapshot.CreatedIn.Scope == currentLocation;
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

        var nextSnapshot = snapshot;
        if (patch.Name is not null)
        {
            nextSnapshot = nextSnapshot with { Name = patch.Name };
        }

        if (patch.FolderPath is not null)
        {
            nextSnapshot = nextSnapshot with { FolderPath = patch.FolderPath };
        }

        if (patch.Locked.HasValue)
        {
            nextSnapshot = nextSnapshot with { Locked = patch.Locked.Value };
        }

        if (patch.Visible.HasValue)
        {
            nextSnapshot = nextSnapshot with { Visible = patch.Visible.Value };
        }

        if (patch.Transform is not null)
        {
            nextSnapshot = nextSnapshot with { Transform = patch.Transform };
        }

        if (patch.Model is not null)
        {
            nextSnapshot = nextSnapshot with { Model = ApplyModelPatch(nextSnapshot.Model, patch.Model) };
        }

        return nextSnapshot;
    }

    public static string GetAssetName(ObjectSnapshot snapshot)
        => snapshot.Model switch
        {
            BgObjectModel bgObjectModel   => Path.GetFileName(bgObjectModel.ModelPath),
            FurnitureModel furnitureModel => Path.GetFileName(furnitureModel.SharedGroupPath),
            VfxModel vfxModel             => Path.GetFileName(vfxModel.VfxPath),
            LightModel                 _  => "light",
            _                             => snapshot.Kind.ToString(),
        };

    public static string GetRootResourcePath(ObjectSnapshot snapshot)
        => snapshot.Model switch
        {
            BgObjectModel bgObjectModel   => GameAssetPathRules.NormalizeGamePath(bgObjectModel.ModelPath),
            FurnitureModel furnitureModel => GameAssetPathRules.NormalizeGamePath(furnitureModel.SharedGroupPath),
            VfxModel vfxModel             => GameAssetPathRules.NormalizeGamePath(vfxModel.VfxPath),
            _                             => string.Empty,
        };

    private static ObjectData ApplyModelPatch(ObjectData model, ObjectDataPatch patch)
        => (model, patch) switch
        {
            (BgObjectModel bgObjectModel, BgObjectModelPatch bgObjectPatch)        => ApplyBgObjectModelPatch(bgObjectModel, bgObjectPatch),
            (FurnitureModel furnitureModel, FurnitureModelPatch furniturePatch)    => ApplyFurnitureModelPatch(furnitureModel, furniturePatch),
            (VfxModel vfxModel, VfxModelPatch vfxPatch)                            => ApplyVfxModelPatch(vfxModel, vfxPatch),
            (LightModel lightModel, LightModelPatch lightPatch)                    => ApplyLightModelPatch(lightModel, lightPatch),
            _                                                                      => model,
        };

    private static BgObjectModel ApplyBgObjectModelPatch(BgObjectModel model, BgObjectModelPatch patch)
        => model with
        {
            ModelPath = patch.ModelPath ?? model.ModelPath,
            Transparency = patch.Transparency ?? model.Transparency,
            DyeColor = patch.DyeColor ?? model.DyeColor,
            IsCoveredFromRain = patch.IsCoveredFromRain ?? model.IsCoveredFromRain,
        };

    private static FurnitureModel ApplyFurnitureModelPatch(FurnitureModel model, FurnitureModelPatch patch)
        => model with
        {
            SharedGroupPath = patch.SharedGroupPath ?? model.SharedGroupPath,
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

