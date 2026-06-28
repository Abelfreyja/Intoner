using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using Intoner.Objects.Models;
using Microsoft.Extensions.Logging;
using System.Numerics;
using DrawObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject;
using RenderLight = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Light;
using RenderLightFalloffType = FFXIVClientStructs.FFXIV.Client.Graphics.Render.LightFalloffType;
using RenderLightFlags = FFXIVClientStructs.FFXIV.Client.Graphics.Render.LightFlags;
using RenderLightShape = FFXIVClientStructs.FFXIV.Client.Graphics.Render.LightShape;
using SceneLight = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Light;

namespace Intoner.Objects.Runtime;

internal sealed unsafe class LightSceneObject : DrawSceneObject
{
    private SceneLight* _light;

    public override ObjectKind Kind
        => ObjectKind.Light;

    public override bool NeedsFrameworkUpdates
        => true;

    public override nint Address
        => (nint)_light;

    protected override DrawObject* DrawObjectPointer
        => _light != null ? (DrawObject*)_light : null;

    internal LightSceneObject(
        IFramework framework,
        ILogger logger,
        ObjectSnapshot snapshot,
        SceneLight* light)
        : base(framework, logger, snapshot)
    {
        _light = light;
    }

    protected override void FrameworkUpdateUnsafe()
    {
        if (_light == null)
        {
            return;
        }

        var drawObject = (DrawObject*)_light;
        drawObject->UpdateMaterials();
    }

    protected override bool CanResolveDrawObjectBounds(DrawObject* drawObject)
        => drawObject != null && _light != null && _light->RenderLight != null;

    public override void AppendSelectionDraws(ObjectSelectionCollector collector)
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
                    ObjectSelectionPrimitiveKind.Sphere,
                    CreateScaledWorldTransform(Snapshot.Transform, new Vector3(MathF.Max(0.25f, lightModel.Shape.Range))));
                break;
            case LightType.SpotLight:
            {
                var range = MathF.Max(0.25f, lightModel.Shape.Range);
                var radius = MathF.Max(0.1f, MathF.Tan(MathF.Max(1f, lightModel.Shape.LightAngle) * (MathF.PI / 360f)) * range);
                collector.AddPrimitive(
                    Snapshot,
                    ObjectSelectionPrimitiveKind.Cone,
                    CreateScaledWorldTransform(Snapshot.Transform, new Vector3(radius, radius, range)));
                break;
            }
            case LightType.FlatLight:
            {
                var range = MathF.Max(0.25f, lightModel.Shape.Range);
                var width = MathF.Max(0.1f, MathF.Tan(MathF.Max(1f, lightModel.Shape.LightAngle) * (MathF.PI / 360f)) * range);
                var height = MathF.Max(0.1f, MathF.Tan(MathF.Max(1f, lightModel.Shape.FalloffAngle) * (MathF.PI / 360f)) * range);
                collector.AddPrimitive(
                    Snapshot,
                    ObjectSelectionPrimitiveKind.Pyramid,
                    CreateScaledWorldTransform(Snapshot.Transform, new Vector3(width, height, range)));
                break;
            }
            default:
                collector.AddPrimitive(
                    Snapshot,
                    ObjectSelectionPrimitiveKind.Sphere,
                    CreateScaledWorldTransform(Snapshot.Transform, new Vector3(2f)));
                break;
        }
    }

    protected override SceneObjectUpdateResult ValidateSnapshotUpdate(ObjectSnapshot snapshot)
        => _light != null
            ? SceneObjectUpdateResult.Applied
            : SceneObjectUpdateResult.RequiresRecreate;

    protected override SceneObjectUpdateResult ApplySnapshotUnsafe(ObjectSnapshot snapshot, ObjectSnapshot previousSnapshot)
        => ApplySnapshotStateUnsafe(snapshot);

    protected override SceneObjectUpdateResult RefreshResourcesUnsafe(ObjectSnapshot snapshot)
        => SceneObjectUpdateResult.Applied;

    protected override void DisposeUnsafe()
    {
        if (_light == null)
        {
            return;
        }

        Logger.LogInformation("destroying light 0x{Address:X}", (ulong)(nint)_light);
        _light->CleanupRender();
        _light->Dtor(DestroyFlagsFree);
        _light = null;
    }

    private SceneObjectUpdateResult ApplySnapshotStateUnsafe(ObjectSnapshot snapshot)
    {
        if (_light == null || _light->RenderLight == null)
        {
            return SceneObjectUpdateResult.RequiresRecreate;
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
        renderLight->FlatLightSkewAngleDegrees = lightModel.Shape.AngleDegrees;
        renderLight->CharacterShadowRange = lightModel.Shadow.CharacterShadowRange;
        renderLight->ShadowPlaneNear = lightModel.Shadow.ShadowPlaneNear;
        renderLight->ShadowPlaneFar = lightModel.Shadow.ShadowPlaneFar;
        renderLight->MaxRange = RenderLight.UnlimitedMaxRange;

        drawObject->IsVisible = snapshot.Visible;
        drawObject->NotifyTransformChanged();
        drawObject->UpdateCulling();
        drawObject->UpdateMaterials();
        return SceneObjectUpdateResult.Applied;
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
        RenderLightFlags flags = 0;
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

    private static Matrix4x4 CreateScaledWorldTransform(ObjectTransform transform, Vector3 localScale)
        => ObjectSelectionCollector.CreateWorldTransform(
            transform.Position,
            ObjectSelectionCollector.CreateRotation(transform.RotationDegrees),
            Vector3.Multiply(transform.Scale, localScale));
}

