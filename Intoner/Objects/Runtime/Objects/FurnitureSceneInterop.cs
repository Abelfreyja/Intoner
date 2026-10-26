using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Node;
using InteropGenerator.Runtime;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Microsoft.Extensions.Logging;
using GraphicsSceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using ObjectHighlightColor = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectHighlightColor;

namespace Intoner.Objects.Runtime;

internal static unsafe class FurnitureSceneInterop
{
    public static void AppendSelectionDraws(SharedGroupLayoutInstance* instance, ObjectSnapshot snapshot, SceneSelectionCollector collector)
    {
        if (instance == null)
        {
            return;
        }

        AppendSelectionDraws(&instance->Instances, snapshot, collector);
    }

    public static bool ApplyRuntimeState(FurnitureNativeInstance native, ObjectSnapshot snapshot)
    {
        SharedGroupLayoutInstance* instance = native.Instance;
        ObjectLayoutInterop.ApplyTransform((ILayoutInstance*)instance, snapshot.Transform);
        native.SetParticipation(snapshot.Visible, ((FurnitureModel)snapshot.Model).CameraCollision);

        return RefreshVisualState(native);
    }

    public static bool RefreshVisualState(FurnitureNativeInstance native)
    {
        SharedGroupLayoutInstance* instance = native.Instance;
        if (instance == null)
        {
            return false;
        }

        bool ready = ObjectLayoutInterop.TryRefreshSharedGroupTransforms(instance);
        if (!ready)
        {
            instance->Instances.ApplyTransforms();
        }

        ObjectLayoutInterop.ApplySharedGroupVisibility(instance, native.Visible);

        return ready;
    }

    public static bool TryApplyVisualState(ILogger logger, SharedGroupLayoutInstance* instance, FurnitureModel furnitureModel)
    {
        if (!ObjectLayoutInterop.IsPrimaryLoaded(instance) || instance->Instances.Instances.Count == 0)
        {
            return false;
        }

        if (!TryApplyChildTransparency(&instance->Instances, furnitureModel.Transparency))
        {
            return false;
        }

        ApplyOutlineColor(&instance->Instances, furnitureModel.OutlineColor);

        if (furnitureModel.Color.UseCustomColor)
        {
            ByteColor stainColor = ColorUtility.ToOpaqueByteColor(furnitureModel.Color.CustomColor);
            ApplyCustomStainColor(&instance->Instances, &stainColor);
            return true;
        }

        return TryApplyNativeStainColor(logger, instance, furnitureModel.Color.StainId);
    }

    private static bool TryApplyNativeStainColor(ILogger logger, SharedGroupLayoutInstance* instance, byte chosenStainId)
    {
        var stainInfo = instance->StainInfo;

        if (chosenStainId == 0 && (stainInfo == null || stainInfo->DefaultStainIndex is 0 or byte.MaxValue))
        {
            return true;
        }

        int sharedGroupChildCount = instance->Instances.Instances.Count;
        if (stainInfo == null)
        {
            logger.LogInformation(
                "furniture dye is unavailable for shared group 0x{Address:X}; stain info is null; primary path {PrimaryPath}; shared group children {ChildCount}",
                (ulong)(nint)instance,
                GetInstancePath(instance->GetPrimaryPath()),
                sharedGroupChildCount);
        }
        else if (instance->TryApplyStain(chosenStainId))
        {
            logger.LogDebug(
                "applied furniture dye through shared group state for 0x{Address:X}; stain info 0x{StainInfo:X}; shared group children {ChildCount}",
                (ulong)(nint)instance,
                (ulong)(nint)stainInfo,
                sharedGroupChildCount);
            return true;
        }
        else
        {
            logger.LogInformation(
                "furniture dye did not apply for shared group 0x{Address:X}; stain info 0x{StainInfo:X}; default stain {DefaultStain}; chosen stain {ChosenStain}; flags 0x{Flags:X}; primary path {PrimaryPath}; shared group children {ChildCount}",
                (ulong)(nint)instance,
                (ulong)(nint)stainInfo,
                stainInfo->DefaultStainIndex,
                stainInfo->ChosenStainIndex,
                (uint)stainInfo->Flags,
                GetInstancePath(instance->GetPrimaryPath()),
                sharedGroupChildCount);
        }

        if (!TryResolveNativeStainColor(instance, chosenStainId, out var stainColor))
        {
            return false;
        }

        logger.LogInformation(
            "falling back to manual furniture dye apply for shared group 0x{Address:X}; shared group children {ChildCount}",
            (ulong)(nint)instance,
            sharedGroupChildCount);
        ApplyCustomStainColor(&instance->Instances, &stainColor);
        return true;
    }

    private static bool TryResolveNativeStainColor(
        SharedGroupLayoutInstance* instance,
        byte chosenStainId,
        out ByteColor stainColor)
    {
        stainColor = default;

        var stainId = chosenStainId;
        if (stainId == 0)
        {
            var stainInfo = instance->StainInfo;
            if (stainInfo == null || stainInfo->DefaultStainIndex == 0)
            {
                return false;
            }

            stainId = stainInfo->DefaultStainIndex;
        }

        var nativeColor = SharedGroupLayoutInstance.GetObjectStainColorByIndex(stainId);
        if (nativeColor == null)
        {
            return false;
        }

        stainColor = *nativeColor;
        return true;
    }

    private static void ApplyCustomStainColor(ChildNodeContainer* container, ByteColor* stainColor)
    {
        foreach (var child in container->Instances.AsSpan())
        {
            var node = child.Value;
            if (node == null || node->Instance == null)
            {
                continue;
            }

            node->Instance->ApplyStain(stainColor);

            if (node->Instance->Id.Type == InstanceType.SharedGroup)
            {
                var childGroup = (SharedGroupLayoutInstance*)node->Instance;
                ApplyCustomStainColor(&childGroup->Instances, stainColor);
            }
        }
    }

    private static void AppendSelectionDraws(ChildNodeContainer* container, ObjectSnapshot snapshot, SceneSelectionCollector collector)
    {
        foreach (var child in container->Instances.AsSpan())
        {
            var node = child.Value;
            if (node == null || node->Instance == null)
            {
                continue;
            }

            var instance = node->Instance;
            if (ObjectLayoutInterop.TryGetWorldTransform(instance, out var worldTransform))
            {
                var primaryGraphics = instance->GetGraphics();
                if (primaryGraphics != null)
                {
                    collector.AddModel(snapshot, GetInstancePath(instance->GetPrimaryPath()), worldTransform);
                }

                var secondaryGraphics = instance->GetGraphics2();
                if (secondaryGraphics != null && secondaryGraphics != primaryGraphics)
                {
                    collector.AddModel(snapshot, GetInstancePath(instance->GetSecondaryPath()), worldTransform);
                }
            }

            if (instance->Id.Type == InstanceType.SharedGroup)
            {
                var childGroup = (SharedGroupLayoutInstance*)instance;
                AppendSelectionDraws(&childGroup->Instances, snapshot, collector);
            }
        }
    }

    private static bool TryApplyChildTransparency(ChildNodeContainer* container, float transparency)
    {
        bool applied = true;
        foreach (var child in container->Instances.AsSpan())
        {
            var node = child.Value;
            if (node == null || node->Instance == null)
            {
                continue;
            }

            var primaryGraphics = node->Instance->GetGraphics();
            applied &= TryApplyChildTransparency(primaryGraphics, transparency);

            var secondaryGraphics = node->Instance->GetGraphics2();
            if (secondaryGraphics != null && secondaryGraphics != primaryGraphics)
            {
                applied &= TryApplyChildTransparency(secondaryGraphics, transparency);
            }

            if (node->Instance->Id.Type == InstanceType.SharedGroup)
            {
                var childGroup = (SharedGroupLayoutInstance*)node->Instance;
                applied &= TryApplyChildTransparency(&childGroup->Instances, transparency);
            }
        }

        return applied;
    }

    private static bool TryApplyChildTransparency(GraphicsSceneObject* graphics, float transparency)
    {
        if (!ObjectSceneInterop.TryGetDrawObject(graphics, out var drawObject))
        {
            return true;
        }

        return drawObject->LoadState == 4
            || ObjectSceneInterop.TryApplyTransparency(drawObject, transparency);
    }

    private static void ApplyOutlineColor(ChildNodeContainer* container, ObjectOutlineColor outlineColor)
    {
        foreach (var child in container->Instances.AsSpan())
        {
            var node = child.Value;
            if (node == null || node->Instance == null)
            {
                continue;
            }

            var primaryGraphics = node->Instance->GetGraphics();
            ObjectSceneInterop.ApplyOutlineColor(primaryGraphics, (ObjectHighlightColor)outlineColor);

            var secondaryGraphics = node->Instance->GetGraphics2();
            if (secondaryGraphics != null && secondaryGraphics != primaryGraphics)
            {
                ObjectSceneInterop.ApplyOutlineColor(secondaryGraphics, (ObjectHighlightColor)outlineColor);
            }

            if (node->Instance->Id.Type == InstanceType.SharedGroup)
            {
                var childGroup = (SharedGroupLayoutInstance*)node->Instance;
                ApplyOutlineColor(&childGroup->Instances, outlineColor);
            }
        }
    }

    private static string GetInstancePath(CStringPointer path)
        => path.HasValue ? path.ToString() : string.Empty;
}
