using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

/// <summary> edits object models in a property table supplied by the caller </summary>
/// <remarks> callbacks receive the edit id, history title, updated model, and whether to record immediately </remarks>
internal static class ObjectModelEditor
{
    public static void DrawBgObjectRows(
        string id,
        ref BgObjectModel model,
        Action<string, string, BgObjectModel, bool>? onChanged = null)
    {
        float transparency = model.Transparency;
        if (EditorPropertyTable.SliderFloat($"BgObjectTransparency_{id}", "Transparency", ref transparency, 0f, 1f, "%.3f"))
        {
            model = model with { Transparency = transparency };
            onChanged?.Invoke("BgObjectTransparency", "Change BgObject Transparency", model, false);
        }

        bool coveredFromRain = model.IsCoveredFromRain;
        if (EditorPropertyTable.Checkbox($"BgObjectCoveredFromRain_{id}", "Covered From Rain", ref coveredFromRain))
        {
            model = model with { IsCoveredFromRain = coveredFromRain };
            onChanged?.Invoke("BgObjectCoveredFromRain", "Change Rain Coverage", model, true);
        }

        EditorPropertyTable.NextRow("Dye Color");
        Vector4 dyeColor = model.DyeColor;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.ColorEdit4($"##BgObjectDyeColor_{id}", ref dyeColor, ImGuiColorEditFlags.Float))
        {
            model = model with { DyeColor = dyeColor };
            onChanged?.Invoke("BgObjectDyeColor", "Change BgObject Dye Color", model, false);
        }
    }

    public static void DrawFurnitureRows(
        string id,
        ref FurnitureModel model,
        IReadOnlyList<FurnitureStainOption> stains,
        FurnitureStainSelector stainSelector,
        Action<string, string, FurnitureModel, bool>? onChanged = null)
    {
        float transparency = model.Transparency;
        if (EditorPropertyTable.SliderFloat($"FurnitureTransparency_{id}", "Transparency", ref transparency, 0f, 1f, "%.3f"))
        {
            model = model with { Transparency = transparency };
            onChanged?.Invoke("FurnitureTransparency", "Change Furniture Transparency", model, false);
        }

        ObjectOutlineColor outlineColor = model.OutlineColor;
        if (EditorPropertyTable.EnumRow($"FurnitureOutlineColor_{id}", "Outline Color", outlineColor, static value => value.ToString(), ref outlineColor))
        {
            model = model with { OutlineColor = outlineColor };
            onChanged?.Invoke("FurnitureOutlineColor", "Change Furniture Outline Color", model, true);
        }

        bool useCustomColor = model.Color.UseCustomColor;
        if (EditorPropertyTable.Checkbox($"{id}_furnitureCustomColor", "Custom Color", ref useCustomColor))
        {
            model = model with { Color = model.Color with { UseCustomColor = useCustomColor } };
            onChanged?.Invoke("FurnitureCustomColor", "Toggle Furniture Custom Color", model, true);
        }

        if (useCustomColor)
        {
            Vector4 customColor = model.Color.CustomColor;
            Vector3 rgbColor = new(customColor.X, customColor.Y, customColor.Z);
            EditorPropertyTable.NextRow("Color");
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.ColorEdit3($"##Color_{id}", ref rgbColor))
            {
                model = model with { Color = model.Color with { CustomColor = new Vector4(rgbColor, 1f) } };
                onChanged?.Invoke("FurnitureColor", "Change Furniture Color", model, false);
            }
        }
        else
        {
            byte stainId = model.Color.StainId;
            if (stainSelector.Draw("Color", id, stains, ref stainId))
            {
                model = model with { Color = model.Color with { StainId = stainId } };
                onChanged?.Invoke("FurnitureStain", "Change Furniture Stain", model, true);
            }
        }
    }

    public static void DrawVfxRows(
        string id,
        ref VfxModel model,
        bool canUseReplayLoop,
        Action<string, string, VfxModel, bool>? onChanged = null)
    {
        string vfxPath = model.VfxPath;
        EditorPropertyTable.NextRow("VFX Path");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText($"##VfxPath_{id}", ref vfxPath, 512))
        {
            model = model with { VfxPath = vfxPath };
            onChanged?.Invoke("VfxPath", "Change VFX Path", model, false);
        }

        EditorPropertyTable.NextRow("Tint");
        Vector4 color = model.Color;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.ColorEdit4($"##VfxColor_{id}", ref color, ImGuiColorEditFlags.Float))
        {
            model = model with { Color = color };
            onChanged?.Invoke("VfxColor", "Change VFX Tint", model, false);
        }

        bool paused = model.Paused;
        if (EditorPropertyTable.Checkbox($"VfxPaused_{id}", "Paused", ref paused))
        {
            model = model with { Paused = paused };
            onChanged?.Invoke("VfxPaused", paused ? "Pause VFX" : "Resume VFX", model, true);
        }

        float speed = model.Speed;
        if (EditorPropertyTable.DragFloat($"VfxSpeed_{id}", "Speed", ref speed, 0.01f, VfxModel.MinSpeed, VfxModel.MaxSpeed, "%.2fx"))
        {
            model = model with { Speed = VfxModel.ClampSpeed(speed) };
            onChanged?.Invoke("VfxSpeed", "Change VFX Speed", model, false);
        }

        float fadeInSeconds = model.FadeInSeconds;
        if (EditorPropertyTable.DragFloat(
                $"VfxFadeIn_{id}",
                "Fade In",
                ref fadeInSeconds,
                0.05f,
                VfxModel.MinFadeInSeconds,
                VfxModel.MaxFadeInSeconds,
                "%.2f seconds"))
        {
            model = model with { FadeInSeconds = VfxModel.ClampFadeInSeconds(fadeInSeconds) };
            onChanged?.Invoke("VfxFadeIn", "Change VFX Fade In", model, false);
        }

        bool replayOnTransform = model.ReplayOnTransform;
        if (EditorPropertyTable.Checkbox($"VfxReplayOnTransform_{id}", "Replay on Transform", ref replayOnTransform))
        {
            model = model with { ReplayOnTransform = replayOnTransform };
            onChanged?.Invoke(
                "VfxReplayOnTransform",
                replayOnTransform ? "Enable VFX Transform Replay" : "Disable VFX Transform Replay",
                model,
                true);
        }

        if (!canUseReplayLoop)
        {
            return;
        }

        bool loop = model.Loop;
        if (EditorPropertyTable.Checkbox($"VfxLoop_{id}", "Loop", ref loop))
        {
            model = model with { Loop = loop };
            onChanged?.Invoke("VfxLoop", loop ? "Enable VFX Loop" : "Disable VFX Loop", model, true);
        }

        using (ImRaii.Disabled(!loop))
        {
            EditorPropertyTable.NextRow("Loop Interval");
            int loopIntervalSeconds = model.LoopIntervalSeconds;
            if (ImGui.DragInt(
                    $"##VfxLoopInterval_{id}",
                    ref loopIntervalSeconds,
                    0.1f,
                    VfxModel.MinLoopIntervalSeconds,
                    VfxModel.MaxLoopIntervalSeconds,
                    "%d seconds"))
            {
                model = model with { LoopIntervalSeconds = VfxModel.ClampLoopIntervalSeconds(loopIntervalSeconds) };
                onChanged?.Invoke("VfxLoopInterval", "Change VFX Loop Interval", model, false);
            }
        }
    }

    public static void DrawLightRows(
        string id,
        ref LightModel model,
        bool includeLightType = true,
        Action<string, string, LightModel, bool>? onChanged = null)
    {
        LightFlags lightFlags = model.Flags;
        LightShape lightShape = model.Shape;
        LightShadow lightShadow = model.Shadow;

        LightType lightType = model.LightType;
        if (includeLightType && EditorPropertyTable.EnumRow($"LightType_{id}", "Light Type", lightType, static value => value.ToString(), ref lightType))
        {
            model = model with { LightType = lightType };
            onChanged?.Invoke("LightType", "Change Light Type", model, true);
        }

        LightFalloffType falloffType = model.FalloffType;
        if (EditorPropertyTable.EnumRow($"FalloffType_{id}", "Falloff Type", falloffType, static value => value.ToString(), ref falloffType))
        {
            model = model with { FalloffType = falloffType };
            onChanged?.Invoke("LightFalloffType", "Change Light Falloff Type", model, true);
        }

        bool reflection = lightFlags.EnableMaterialReflection;
        if (EditorPropertyTable.Checkbox($"MaterialReflection_{id}", "Material Reflection", ref reflection))
        {
            lightFlags = lightFlags with { EnableMaterialReflection = reflection };
            model = model with { Flags = lightFlags };
            onChanged?.Invoke("LightMaterialReflection", "Toggle Light Material Reflection", model, true);
        }

        bool dynamicLighting = lightFlags.EnableDynamicLighting;
        if (EditorPropertyTable.Checkbox($"DynamicLighting_{id}", "Dynamic Lighting", ref dynamicLighting))
        {
            lightFlags = lightFlags with { EnableDynamicLighting = dynamicLighting };
            model = model with { Flags = lightFlags };
            onChanged?.Invoke("LightDynamicLighting", "Toggle Light Dynamic Lighting", model, true);
        }

        bool charaShadow = lightFlags.EnableCharacterShadow;
        if (EditorPropertyTable.Checkbox($"CharacterShadow_{id}", "Character Shadow", ref charaShadow))
        {
            lightFlags = lightFlags with { EnableCharacterShadow = charaShadow };
            model = model with { Flags = lightFlags };
            onChanged?.Invoke("LightCharacterShadow", "Toggle Light Character Shadow", model, true);
        }

        bool objectShadow = lightFlags.EnableObjectShadow;
        if (EditorPropertyTable.Checkbox($"ObjectShadow_{id}", "Object Shadow", ref objectShadow))
        {
            lightFlags = lightFlags with { EnableObjectShadow = objectShadow };
            model = model with { Flags = lightFlags };
            onChanged?.Invoke("LightObjectShadow", "Toggle Light Object Shadow", model, true);
        }

        EditorPropertyTable.NextRow("Color");
        Vector3 rawColor = model.Color;
        Vector3 lightColor = Vector3.SquareRoot(rawColor / 6f);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.ColorEdit3($"##Color_{id}", ref lightColor, ImGuiColorEditFlags.Hdr | ImGuiColorEditFlags.Float))
        {
            rawColor = lightColor * lightColor * 6f;
            model = model with { Color = rawColor };
            onChanged?.Invoke("LightColor", "Change Light Color", model, false);
        }

        float intensity = model.Intensity;
        if (EditorPropertyTable.DragFloat($"Intensity_{id}", "Intensity", ref intensity, 0.05f, 0f, 100f, "%.3f"))
        {
            model = model with { Intensity = intensity };
            onChanged?.Invoke("LightIntensity", "Change Light Intensity", model, false);
        }

        float range = lightShape.Range;
        if (EditorPropertyTable.DragFloat($"Range_{id}", "Range", ref range, 0.1f, 0.01f, 900f, "%.3f"))
        {
            lightShape = lightShape with { Range = range };
            model = model with { Shape = lightShape };
            onChanged?.Invoke("LightRange", "Change Light Range", model, false);
        }

        float falloff = lightShape.Falloff;
        if (EditorPropertyTable.DragFloat($"Falloff_{id}", "Falloff", ref falloff, 0.01f, 0f, 1000f, "%.3f"))
        {
            lightShape = lightShape with { Falloff = falloff };
            model = model with { Shape = lightShape };
            onChanged?.Invoke("LightFalloff", "Change Light Falloff", model, false);
        }

        if (lightType == LightType.SpotLight)
        {
            float lightAngle = lightShape.LightAngle;
            if (EditorPropertyTable.SliderFloat($"LightAngle_{id}", "Light Angle", ref lightAngle, 0f, 180f, "%.0f Degrees"))
            {
                lightShape = lightShape with { LightAngle = lightAngle };
                model = model with { Shape = lightShape };
                onChanged?.Invoke("LightAngle", "Change Light Angle", model, false);
            }
        }

        if (lightType == LightType.SpotLight || lightType == LightType.FlatLight)
        {
            float falloffAngle = lightShape.FalloffAngle;
            if (EditorPropertyTable.SliderFloat($"FalloffAngle_{id}", "Falloff Angle", ref falloffAngle, 0f, 180f, "%.0f Degrees"))
            {
                lightShape = lightShape with { FalloffAngle = falloffAngle };
                model = model with { Shape = lightShape };
                onChanged?.Invoke("LightFalloffAngle", "Change Light Falloff Angle", model, false);
            }
        }

        if (lightType == LightType.FlatLight)
        {
            Vector2 flatAngles = lightShape.AngleDegrees;
            EditorPropertyTable.NextRow("Flat Angle");
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.SliderFloat2($"##FlatAngle_{id}", ref flatAngles, -90f, 90f, "%.0f Degrees"))
            {
                lightShape = lightShape with { AngleDegrees = flatAngles };
                model = model with { Shape = lightShape };
                onChanged?.Invoke("LightFlatAngle", "Change Light Flat Angle", model, false);
            }
        }

        float shadowRange = lightShadow.CharacterShadowRange;
        if (EditorPropertyTable.DragFloat($"ShadowRange_{id}", "Character Shadow Range", ref shadowRange, 0.25f, 0f, 1000f, "%.3f"))
        {
            lightShadow = lightShadow with { CharacterShadowRange = shadowRange };
            model = model with { Shadow = lightShadow };
            onChanged?.Invoke("LightShadowRange", "Change Light Character Shadow Range", model, false);
        }

        float shadowNear = lightShadow.ShadowPlaneNear;
        if (EditorPropertyTable.DragFloat($"ShadowNear_{id}", "Shadow Near", ref shadowNear, 0.001f, 0.001f, 100f, "%.3f"))
        {
            lightShadow = lightShadow with { ShadowPlaneNear = shadowNear };
            model = model with { Shadow = lightShadow };
            onChanged?.Invoke("LightShadowNear", "Change Light Shadow Near", model, false);
        }

        float shadowFar = lightShadow.ShadowPlaneFar;
        if (EditorPropertyTable.DragFloat($"ShadowFar_{id}", "Shadow Far", ref shadowFar, 0.05f, 0.01f, 1000f, "%.3f"))
        {
            lightShadow = lightShadow with { ShadowPlaneFar = shadowFar };
            model = model with { Shadow = lightShadow };
            onChanged?.Invoke("LightShadowFar", "Change Light Shadow Far", model, false);
        }
    }
}
