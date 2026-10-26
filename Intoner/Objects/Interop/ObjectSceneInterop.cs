using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Intoner.Objects.Utils;
using AxisAlignedBounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using ObjectHighlightColor = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectHighlightColor;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace Intoner.Objects.Interop;

internal static unsafe class ObjectSceneInterop
{
    private const byte DrawObjectLoadedState = 3;

    public static bool IsDrawObjectLoaded(DrawObject* drawObject)
        => drawObject != null && drawObject->LoadState == DrawObjectLoadedState;

    public static void UpdateCulling(DrawObject* drawObject)
        => drawObject->UpdateCulling();

    public static void UpdateMaterials(DrawObject* drawObject)
        => drawObject->UpdateMaterials();

    public static bool TryApplyTransparency(DrawObject* drawObject, float transparency)
    {
        if (!IsDrawObjectLoaded(drawObject)
            || (drawObject->GetObjectType() == ObjectType.BgObject
                && !BgObjectSceneInterop.IsModelLoaded((BgObject*)drawObject)))
        {
            return false;
        }

        drawObject->SetTransparency(transparency);
        drawObject->UpdateMaterials();
        drawObject->UpdateCulling();
        return true;
    }

    public static void ApplyOutlineColor(DrawObject* drawObject, ObjectHighlightColor outlineColor)
        => drawObject->OutlineColor = outlineColor;

    public static void ApplyOutlineColor(SceneObject* graphics, ObjectHighlightColor outlineColor)
    {
        if (!TryGetDrawObject(graphics, out var drawObject))
        {
            return;
        }

        ApplyOutlineColor(drawObject, outlineColor);
    }

    public static bool TryGetDrawObject(SceneObject* graphics, out DrawObject* drawObject)
    {
        drawObject = null;
        if (graphics == null)
        {
            return false;
        }

        switch (graphics->GetObjectType())
        {
            case ObjectType.Terrain:
            case ObjectType.BgObject:
            case ObjectType.CharacterBase:
            case ObjectType.VfxObject:
            case ObjectType.Light:
            case ObjectType.EnvSpace:
            case ObjectType.EnvLocation:
                drawObject = (DrawObject*)graphics;
                return true;
            default:
                return false;
        }
    }

    public static bool TryGetDrawObjectOrientedBounds(DrawObject* drawObject, out OrientedBounds bounds)
    {
        bounds = default;

        if (!IsDrawObjectLoaded(drawObject))
        {
            return false;
        }

        OrientedBounds result = default;

        if (drawObject->ComputeOrientedBounds(&result) == null || !ObjectShapeMath.IsValidBounds(result))
        {
            return false;
        }

        bounds = result;
        return true;
    }

    public static bool TryGetDrawObjectBounds(DrawObject* drawObject, out AxisAlignedBounds bounds)
    {
        bounds = default;

        if (!IsDrawObjectLoaded(drawObject))
        {
            return false;
        }

        AxisAlignedBounds result = default;

        if (drawObject->ComputeAxisAlignedBounds(&result) == null || !ObjectShapeMath.IsValidBounds(result))
        {
            return false;
        }

        bounds = result;
        return true;
    }
}
