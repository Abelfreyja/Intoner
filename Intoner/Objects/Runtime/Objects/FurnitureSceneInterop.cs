using FFXIVClientStructs.FFXIV.Client.Graphics;
using ObjectHighlightColor = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectHighlightColor;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Node;
using InteropGenerator.Runtime;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Numerics;
using GraphicsSceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace Intoner.Objects.Runtime;

internal static unsafe class FurnitureSceneInterop
{
    public static void AppendSelectionDraws(SharedGroupLayoutInstance* instance, ObjectSnapshot snapshot, ObjectSelectionCollector collector)
    {
        if (instance == null)
        {
            return;
        }

        AppendSelectionDraws(&instance->Instances, snapshot, collector);
    }

    public static void ApplyRuntimeState(SharedGroupLayoutInstance* instance, ObjectSnapshot snapshot)
    {
        ObjectLayoutInterop.ApplyTransform(instance, snapshot.Transform);
        ObjectLayoutInterop.ApplyVisibilityState((ILayoutInstance*)instance, snapshot.Visible);
        _ = ObjectLayoutInterop.TryRefreshVisualSharedGroupState(instance);
    }

    public static bool TryApplyVisualState(ILogger logger, SharedGroupLayoutInstance* instance, FurnitureModel furnitureModel)
    {
        if (!ObjectLayoutInterop.IsSharedGroupReady(instance))
        {
            return false;
        }

        if (!TryApplyTransparency(instance, furnitureModel.Transparency))
        {
            return false;
        }

        if (!TryApplyOutlineColor(instance, furnitureModel.OutlineColor))
        {
            return false;
        }

        if (furnitureModel.Color.UseCustomColor)
        {
            if (!HasChildInstances(instance))
            {
                return false;
            }

            ApplyCustomStainColor(instance, furnitureModel.Color.CustomColor);
            return true;
        }

        return TryApplyNativeStainColor(logger, instance, furnitureModel.Color.StainId);
    }

    private static bool HasChildInstances(SharedGroupLayoutInstance* instance)
        => (nint)instance->Instances.Instances.First != (nint)instance->Instances.Instances.Last;

    private static bool TryApplyTransparency(SharedGroupLayoutInstance* instance, float transparency)
    {
        if (!HasChildInstances(instance))
        {
            return false;
        }

        ApplyTransparency(&instance->Instances, transparency);
        return true;
    }

    private static bool TryApplyOutlineColor(SharedGroupLayoutInstance* instance, ObjectOutlineColor outlineColor)
    {
        if (!HasChildInstances(instance))
        {
            return false;
        }

        ApplyOutlineColor(&instance->Instances, outlineColor);
        return true;
    }

    private static void ApplyCustomStainColor(SharedGroupLayoutInstance* instance, Vector4 customColor)
    {
        var stainColor = ObjectColorUtility.ToOpaqueByteColor(customColor);
        ApplyCustomStainColor(&instance->Instances, &stainColor);
    }

    private static bool TryApplyNativeStainColor(ILogger logger, SharedGroupLayoutInstance* instance, byte chosenStainId)
    {
        var sharedGroupChildCount = GetSharedGroupChildCount(instance);
        var stainInfo = instance->StainInfo;
        if (stainInfo == null)
        {
            logger.LogInformation(
                "furniture dye is unavailable for shared group 0x{Address:X}; stain info is null; primary path {PrimaryPath}; shared group children {ChildCount}",
                (ulong)(nint)instance,
                GetPrimaryPath(instance),
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
                GetPrimaryPath(instance),
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

    private static int GetSharedGroupChildCount(SharedGroupLayoutInstance* instance)
        => GetChildCount(&instance->Instances);

    private static int GetChildCount(ChildNodeContainer* container)
    {
        var first = (nint)container->Instances.First;
        var last = (nint)container->Instances.Last;
        return first == 0 || last < first
            ? 0
            : (int)((last - first) / sizeof(nint));
    }

    private static string GetPrimaryPath(SharedGroupLayoutInstance* instance)
    {
        if (instance == null)
        {
            return string.Empty;
        }

        var primaryPath = instance->GetPrimaryPath();
        return primaryPath.HasValue ? primaryPath.ToString() : string.Empty;
    }

    private static void ApplyCustomStainColor(ChildNodeContainer* container, ByteColor* stainColor)
    {
        foreach (var child in container->Instances)
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

    private static void AppendSelectionDraws(ChildNodeContainer* container, ObjectSnapshot snapshot, ObjectSelectionCollector collector)
    {
        foreach (var child in container->Instances)
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

    private static void ApplyTransparency(ChildNodeContainer* container, float transparency)
    {
        foreach (var child in container->Instances)
        {
            var node = child.Value;
            if (node == null || node->Instance == null)
            {
                continue;
            }

            var primaryGraphics = node->Instance->GetGraphics();
            ApplyTransparency(primaryGraphics, transparency);

            var secondaryGraphics = node->Instance->GetGraphics2();
            if (secondaryGraphics != null && secondaryGraphics != primaryGraphics)
            {
                ApplyTransparency(secondaryGraphics, transparency);
            }

            if (node->Instance->Id.Type == InstanceType.SharedGroup)
            {
                var childGroup = (SharedGroupLayoutInstance*)node->Instance;
                ApplyTransparency(&childGroup->Instances, transparency);
            }
        }
    }

    private static void ApplyTransparency(GraphicsSceneObject* graphics, float transparency)
    {
        if (!ObjectSceneInterop.TryGetDrawObject(graphics, out var drawObject))
        {
            return;
        }

        ObjectSceneInterop.ApplyTransparency(drawObject, transparency);
    }

    private static void ApplyOutlineColor(ChildNodeContainer* container, ObjectOutlineColor outlineColor)
    {
        foreach (var child in container->Instances)
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

