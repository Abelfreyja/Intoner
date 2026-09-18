using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Numerics;
using System.Runtime.InteropServices;
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
        bgObject->Rotation = SceneTransformMath.CreateRotationQuaternion(snapshot.Transform.RotationDegrees);
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
        if (!ObjectSceneInterop.TryApplyTransparency(drawObject, model.Transparency))
        {
            return false;
        }

        ApplyDyeColor(bgObject, model.DyeColor);
        return true;
    }

    public static bool IsModelLoaded(SceneBgObject* bgObject)
        => ObjectSceneInterop.IsDrawObjectLoaded((DrawObject*)bgObject)
            && bgObject->ModelResourceHandle != null
            && bgObject->ModelResourceHandle->ReadState == 2
            && bgObject->ModelResourceHandle->LoadState == ModelResourceLoadedState;

    public static ModelLoadState GetModelLoadState(SceneBgObject* bgObject)
    {
        var handle = bgObject->ModelResourceHandle;
        
        return new ModelLoadState((nint)handle, bgObject->LoadState,
            handle != null ? handle->ReadState : byte.MaxValue,
            handle != null ? handle->LoadState : byte.MaxValue,
            handle != null ? *(uint*)((byte*)handle + 0x6C) : uint.MaxValue,
            handle != null ? *((byte*)handle + 0x298) : (byte)0);
    }

    public static byte GetCurrentLod(SceneBgObject* bgObject)
        => *((byte*)bgObject + 0xCC);

    [StructLayout(LayoutKind.Auto)]
    public readonly record struct ModelLoadState(nint Handle, byte DrawState, byte ReadState,
        byte LoadState, uint ResourceLodBytes, byte LodCount);

    private static void ApplyDyeColor(SceneBgObject* bgObject, Vector4 dyeColor)
    {
        var srgbColor = new Vector4(
            MathF.Sqrt(Math.Clamp(dyeColor.X, 0f, 1f)),
            MathF.Sqrt(Math.Clamp(dyeColor.Y, 0f, 1f)),
            MathF.Sqrt(Math.Clamp(dyeColor.Z, 0f, 1f)),
            Math.Clamp(dyeColor.W, 0f, 1f));

        bgObject->TrySetStainColor(ColorUtility.ToByteColor(srgbColor));
    }
}


