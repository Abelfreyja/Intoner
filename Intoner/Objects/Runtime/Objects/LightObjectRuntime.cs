using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Microsoft.Extensions.Logging;
using System.Numerics;
using DrawObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject;
using RenderLight = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Light;
using RenderLightFalloffType = FFXIVClientStructs.FFXIV.Client.Graphics.Render.LightFalloffType;
using RenderLightFlags = FFXIVClientStructs.FFXIV.Client.Graphics.Render.LightFlags;
using RenderLightShape = FFXIVClientStructs.FFXIV.Client.Graphics.Render.LightShape;
using SceneLight = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Light;

namespace Intoner.Objects.Runtime;

internal sealed unsafe class LightObjectRuntime : DrawObjectRuntime
{
    private readonly ObjectResolvedRootPath _projectionResource;
    private SceneLight* _light;
    private TextureResourceHandle* _pendingProjectionTexture;

    public override ObjectKind Kind
        => ObjectKind.Light;

    public override bool NeedsFrameworkUpdates
        => true;

    public override nint Address
        => (nint)_light;

    protected override DrawObject* DrawObjectPointer
        => _light != null ? (DrawObject*)_light : null;

    internal LightObjectRuntime(
        IFramework framework,
        ILogger logger,
        ObjectSnapshot snapshot,
        SceneLight* light,
        ObjectResolvedRootPath projectionResource)
        : base(framework, logger, snapshot)
    {
        _light = light;
        _projectionResource = projectionResource;
    }

    internal override void Initialize()
    {
        if (((LightModel)Snapshot.Model).ProjectionTexturePath.Length == 0)
        {
            return;
        }

        _pendingProjectionTexture = (TextureResourceHandle*)ResourceHandle.GetAsync(_projectionResource.CreatePath);

        if (_pendingProjectionTexture == null)
        {
            throw new InvalidOperationException($"light projection texture request failed: {_projectionResource.RequestedPath}");
        }
    }

    protected override void FrameworkUpdateUnsafe()
    {
        if (_light == null)
        {
            return;
        }

        ApplyProjectionTexture();
        var drawObject = (DrawObject*)_light;
        drawObject->UpdateMaterials();
    }

    protected override bool CanResolveDrawObjectBounds(DrawObject* drawObject)
        => drawObject != null && _light != null && _light->RenderLight != null;

    public override void AppendSelectionDraws(SceneSelectionCollector collector)
    {
        if (_light == null || !Snapshot.Visible)
        {
            return;
        }

        var lightModel = (LightModel)Snapshot.Model;
        switch (lightModel.LightType)
        {
            case LightType.AreaLight:
                collector.AddPrimitive(
                    Snapshot,
                    SceneSelectionPrimitiveKind.Sphere,
                    CreateScaledWorldTransform(Snapshot.Transform, new Vector3(MathF.Max(0.25f, lightModel.Shape.Range))));
                break;
            case LightType.SpotLight:
                {
                    var range = MathF.Max(0.25f, lightModel.Shape.Range);
                    var radius = MathF.Max(0.1f, MathF.Tan(MathF.Max(1f, lightModel.Shape.LightAngle) * (MathF.PI / 360f)) * range);
                    collector.AddPrimitive(
                        Snapshot,
                        SceneSelectionPrimitiveKind.Cone,
                        CreateScaledWorldTransform(Snapshot.Transform, new Vector3(radius, radius, range)));
                    break;
                }
            case LightType.FlatLight:
                collector.AddPrimitive(
                    Snapshot,
                    SceneSelectionPrimitiveKind.Box,
                    ObjectShapeMath.CreateForwardSkewedBoxTransform(
                        Snapshot.Transform,
                        lightModel.Shape.Range,
                        lightModel.Shape.AngleDegrees));
                break;
            default:
                collector.AddPrimitive(
                    Snapshot,
                    SceneSelectionPrimitiveKind.Sphere,
                    CreateScaledWorldTransform(Snapshot.Transform, new Vector3(2f)));
                break;
        }
    }

    protected override ObjectRuntimeUpdateResult ValidateSnapshotUpdate(ObjectSnapshot snapshot)
    {
        LightModel model = (LightModel)snapshot.Model;
        LightModel previousModel = (LightModel)Snapshot.Model;

        if (_light == null
         || !string.Equals(model.ProjectionTexturePath, previousModel.ProjectionTexturePath, StringComparison.OrdinalIgnoreCase)
         || model.ProjectionTexturePath.Length > 0
         && (model.LightType != previousModel.LightType
             || !string.Equals(snapshot.CollectionId, Snapshot.CollectionId, StringComparison.OrdinalIgnoreCase)))
        {
            return ObjectRuntimeUpdateResult.RequiresRecreate;
        }

        return ObjectRuntimeUpdateResult.Applied;
    }

    protected override ObjectRuntimeUpdateResult ApplySnapshotUnsafe(ObjectSnapshot snapshot, ObjectSnapshot previousSnapshot)
        => ApplySnapshotStateUnsafe(snapshot);

    protected override ObjectRuntimeUpdateResult RefreshResourcesUnsafe(ObjectSnapshot snapshot)
        => ((LightModel)snapshot.Model).ProjectionTexturePath.Length > 0
            ? ObjectRuntimeUpdateResult.RequiresRecreate
            : ObjectRuntimeUpdateResult.Applied;

    protected override void DisposeUnsafe()
    {
        ReleasePendingProjectionTexture();

        if (_light == null)
        {
            return;
        }

        nint address = (nint)_light;
        ObjectResourceLog.Write(() => Logger.LogInformation("destroying light 0x{Address:X}", (ulong)address));
        DestroyNative((DrawObject*)_light);
        _light = null;
    }

    private void ApplyProjectionTexture()
    {
        if (_pendingProjectionTexture == null)
        {
            return;
        }

        byte readState = Volatile.Read(ref _pendingProjectionTexture->ReadState);
        byte loadState = Volatile.Read(ref _pendingProjectionTexture->LoadState);
        bool failed = readState == 3 || loadState > 7;

        if (!failed && (readState != 2 || loadState < 7))
        {
            return;
        }

        LightModel model = (LightModel)Snapshot.Model;
        TextureFlags expectedType = model.LightType == LightType.AreaLight
            ? TextureFlags.TextureTypeCube
            : TextureFlags.TextureType2D;

        if (failed
         || _pendingProjectionTexture->Texture == null
         || (_pendingProjectionTexture->Texture->Flags & TextureFlags.TextureTypeMask) != expectedType)
        {
            Logger.LogWarning("light projection texture rejected; object={ObjectId}; path={Path}; load={LoadState}; expected={TextureType}",
                Snapshot.Id, _projectionResource.RequestedPath, loadState, expectedType);
            ReleasePendingProjectionTexture();
            return;
        }

        _light->ProjectedTexture = _pendingProjectionTexture;
        _pendingProjectionTexture = null;
        ((DrawObject*)_light)->LoadState = 2;
        _light->UpdateRender();
    }

    private void ReleasePendingProjectionTexture()
    {
        TextureResourceHandle* pending = _pendingProjectionTexture;
        _pendingProjectionTexture = null;

        if (pending != null)
        {
            pending->DecRef();
        }
    }

    private ObjectRuntimeUpdateResult ApplySnapshotStateUnsafe(ObjectSnapshot snapshot)
    {
        if (_light == null || _light->RenderLight == null)
        {
            return ObjectRuntimeUpdateResult.RequiresRecreate;
        }

        var lightModel = (LightModel)snapshot.Model;
        var drawObject = (DrawObject*)_light;
        var renderLight = _light->RenderLight;

        _light->Position = snapshot.Transform.Position;
        _light->Rotation = CreateRotation(snapshot.Transform.RotationDegrees);
        _light->Scale = snapshot.Transform.Scale;

        renderLight->Transform = (Transform*)&_light->Position;
        renderLight->LightShape = ToRenderLightShape(lightModel.LightType);
        renderLight->LightFlags = ToRenderLightFlags(lightModel.Flags);
        renderLight->Color = lightModel.Color;
        renderLight->Intensity = lightModel.Intensity;
        renderLight->FalloffType = (RenderLightFalloffType)lightModel.FalloffType;
        renderLight->FalloffFactor = lightModel.Shape.Falloff;
        renderLight->SpotLightAngleDegrees = lightModel.Shape.LightAngle;
        renderLight->AngularFalloffDegrees = lightModel.Shape.FalloffAngle;
        renderLight->Range = lightModel.Shape.Range;
        renderLight->FlatLightSkewAngleDegrees = lightModel.Shape.AngleDegrees * (MathF.PI / 180f);
        renderLight->CharacterShadowRange = lightModel.Shadow.CharacterShadowRange;
        renderLight->ShadowPlaneNear = lightModel.Shadow.ShadowPlaneNear;
        renderLight->ShadowPlaneFar = lightModel.Shadow.ShadowPlaneFar;
        renderLight->MaxRange = RenderLight.UnlimitedMaxRange;

        drawObject->IsVisible = snapshot.Visible;
        drawObject->NotifyTransformChanged();
        drawObject->UpdateCulling();
        drawObject->UpdateMaterials();
        return ObjectRuntimeUpdateResult.Applied;
    }

    internal static RenderLightShape ToRenderLightShape(LightType lightType)
        => lightType switch
        {
            LightType.WorldLight => RenderLightShape.WorldLight,
            LightType.AreaLight => RenderLightShape.PointLight,
            LightType.SpotLight => RenderLightShape.SpotLight,
            LightType.FlatLight => RenderLightShape.FlatLight,
            _ => RenderLightShape.PointLight,
        };

    private static RenderLightFlags ToRenderLightFlags(LightFlags lightFlags)
    {
        RenderLightFlags flags = default;
        if (lightFlags.EnableMaterialReflection)
        {
            flags |= RenderLightFlags.SpecularHighlights;
        }

        if (lightFlags.EnableDynamicLighting)
        {
            flags |= RenderLightFlags.DynamicShadows;
        }

        if (lightFlags.EnableCharacterShadow)
        {
            flags |= RenderLightFlags.CharacterShadows;
        }

        if (lightFlags.EnableObjectShadow)
        {
            flags |= RenderLightFlags.ObjectShadows;
        }

        return flags;
    }

    private static Matrix4x4 CreateScaledWorldTransform(SceneTransform transform, Vector3 localScale)
        => SceneTransformMath.CreateWorldTransform(
            transform.Position,
            SceneTransformMath.CreateRotationQuaternion(transform.RotationDegrees),
            Vector3.Multiply(transform.Scale, localScale));
}
