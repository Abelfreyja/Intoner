using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using System.Numerics;
using System.Text;
using DrawObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;

namespace Intoner.Objects.Interop;

/// <summary> raw scene bgobject helpers for placed runtime objects </summary>
internal static unsafe class BgObjectSceneInterop
{
    private const byte DestroyFlagsFree = 1;
    private const byte ModelResourceLoadedState = 7;

    private static ReadOnlySpan<byte> PoolName
        => "Intoner.BgObject\0"u8;

    public static SceneBgObject* Create(string modelPath)
    {
        Span<byte> pathBytes = stackalloc byte[Encoding.UTF8.GetByteCount(modelPath) + 1];
        Encoding.UTF8.GetBytes(modelPath, pathBytes);
        pathBytes[^1] = 0;

        fixed (byte* pathPtr = pathBytes)
        fixed (byte* poolPtr = PoolName)
        {
            return SceneBgObject.Create(pathPtr, poolPtr);
        }
    }

    public static bool SetModel(SceneBgObject* bgObject, ResourceCategory resourceCategory, string modelPath)
    {
        if (bgObject == null)
        {
            return false;
        }

        Span<byte> pathBytes = stackalloc byte[Encoding.UTF8.GetByteCount(modelPath) + 1];
        Encoding.UTF8.GetBytes(modelPath, pathBytes);
        pathBytes[^1] = 0;

        fixed (byte* pathPtr = pathBytes)
        {
            return bgObject->SetModel(&resourceCategory, pathPtr);
        }
    }

    public static void Destroy(SceneBgObject* bgObject)
    {
        if (bgObject == null)
        {
            return;
        }

        bgObject->CleanupRender();
        bgObject->Dtor(DestroyFlagsFree);
    }

    public static void ApplyRuntimeState(SceneBgObject* bgObject, ObjectSnapshot snapshot)
    {
        if (bgObject == null)
        {
            return;
        }

        bgObject->Position = snapshot.Transform.Position;
        bgObject->Rotation = ObjectTransformMath.CreateRotationQuaternion(snapshot.Transform.RotationDegrees);
        bgObject->Scale = snapshot.Transform.Scale;

        var drawObject = (DrawObject*)bgObject;
        drawObject->IsVisible = snapshot.Visible;
        drawObject->NotifyTransformChanged();

        if (IsModelLoaded(bgObject))
        {
            drawObject->UpdateTransforms(false);
        }
    }

    public static bool TryApplyVisualState(SceneBgObject* bgObject, BgObjectModel model)
    {
        if (bgObject == null)
        {
            return false;
        }

        var drawObject = (DrawObject*)bgObject;
        drawObject->IsCoveredFromRain = model.IsCoveredFromRain;
        if (!IsModelLoaded(bgObject))
        {
            return false;
        }

        ObjectSceneInterop.ApplyTransparency(drawObject, model.Transparency);
        ApplyDyeColor(bgObject, model.DyeColor);
        return true;
    }

    public static bool IsModelLoaded(SceneBgObject* bgObject)
        => bgObject != null
            && bgObject->ModelResourceHandle != null
            && bgObject->ModelResourceHandle->LoadState >= ModelResourceLoadedState;

    public static string GetCurrentModelPath(SceneBgObject* bgObject)
    {
        if (bgObject == null
            || bgObject->ModelResourceHandle == null
            || !ObjectResourcePathEncoding.TryReadHandlePath((ResourceHandle*)bgObject->ModelResourceHandle, out string handlePath))
        {
            return string.Empty;
        }

        return ObjectResourcePathUtility.NormalizeTrackedPath(handlePath);
    }

    private static void ApplyDyeColor(SceneBgObject* bgObject, Vector4 dyeColor)
    {
        var srgbColor = new Vector4(
            MathF.Sqrt(Math.Clamp(dyeColor.X, 0f, 1f)),
            MathF.Sqrt(Math.Clamp(dyeColor.Y, 0f, 1f)),
            MathF.Sqrt(Math.Clamp(dyeColor.Z, 0f, 1f)),
            Math.Clamp(dyeColor.W, 0f, 1f));

        bgObject->TrySetStainColor(ObjectColorUtility.ToByteColor(srgbColor));
    }
}


