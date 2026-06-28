using MessagePack;

namespace Intoner.Objects.Api;

/// <summary> bgobject model data </summary>
/// <param name="ModelPath"> bgobject model path </param>
/// <param name="Transparency"> transparency multiplier </param>
/// <param name="DyeColor"> bgobject dye color in linear rgba </param>
/// <param name="IsCoveredFromRain"> whether the bgobject draw object is flagged as covered by rain </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record BgObjectModelData(
    string ModelPath,
    float Transparency,
    ObjectVector4 DyeColor,
    bool IsCoveredFromRain);

/// <summary> furniture color data </summary>
/// <param name="StainId"> stain id used when custom color is disabled </param>
/// <param name="UseCustomColor"> whether to use the custom rgba color instead of the stain id </param>
/// <param name="CustomColor"> custom rgba tint </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record FurnitureColorData(
    byte StainId,
    bool UseCustomColor,
    ObjectVector4 CustomColor);

/// <summary> furniture model data </summary>
/// <param name="SharedGroupPath"> furniture shared group path </param>
/// <param name="Color"> furniture color settings </param>
/// <param name="Transparency"> transparency multiplier </param>
/// <param name="OutlineColor"> draw object outline color applied to shared-group graphics </param>
/// <param name="HousingRowId"> resolved housing furniture row id when known </param>
/// <param name="ItemRowId"> resolved item row id when known </param>
/// <param name="AttachmentParentId"> imported MakePlace parent furniture id when this furniture is attached to another furniture item </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record FurnitureModelData(
    string SharedGroupPath,
    FurnitureColorData Color,
    float Transparency,
    ObjectOutlineColor OutlineColor,
    uint HousingRowId,
    uint ItemRowId,
    Guid? AttachmentParentId);

/// <summary> VFX model data </summary>
/// <param name="VfxPath"> VFX game path </param>
/// <param name="Color"> VFX tint in normalized rgba </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record VfxModelData(
    string VfxPath,
    ObjectVector4 Color);

/// <summary> light flags </summary>
/// <param name="EnableMaterialReflection"> whether the light affects material reflections </param>
/// <param name="EnableDynamicLighting"> whether the light enables dynamic lighting </param>
/// <param name="EnableCharacterShadow"> whether the light can cast character shadows </param>
/// <param name="EnableObjectShadow"> whether the light can cast object shadows </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record LightFlagsData(
    bool EnableMaterialReflection,
    bool EnableDynamicLighting,
    bool EnableCharacterShadow,
    bool EnableObjectShadow);

/// <summary> light shape data </summary>
/// <param name="Range"> light range </param>
/// <param name="Falloff"> light falloff </param>
/// <param name="LightAngle"> main light angle </param>
/// <param name="FalloffAngle"> falloff angle </param>
/// <param name="AngleDegrees"> secondary angle pair in degrees </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record LightShapeData(
    float Range,
    float Falloff,
    float LightAngle,
    float FalloffAngle,
    ObjectVector2 AngleDegrees);

/// <summary> light shadow data </summary>
/// <param name="CharacterShadowRange"> character shadow range </param>
/// <param name="ShadowPlaneNear"> near shadow plane distance </param>
/// <param name="ShadowPlaneFar"> far shadow plane distance </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record LightShadowData(
    float CharacterShadowRange,
    float ShadowPlaneNear,
    float ShadowPlaneFar);

/// <summary> light model data </summary>
/// <param name="Color"> rgb light color </param>
/// <param name="LightType"> light type </param>
/// <param name="FalloffType"> light falloff type </param>
/// <param name="Flags"> light runtime flags </param>
/// <param name="Intensity"> light intensity multiplier </param>
/// <param name="Shape"> light shape settings </param>
/// <param name="Shadow"> light shadow settings </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record LightModelData(
    ObjectVector3 Color,
    ObjectLightType LightType,
    ObjectLightFalloffType FalloffType,
    LightFlagsData Flags,
    float Intensity,
    LightShapeData Shape,
    LightShadowData Shadow);

/// <summary> object model payload </summary>
/// <param name="BgObject"> bgobject model data for 'BgObject' objects </param>
/// <param name="Furniture"> furniture model data for 'Furniture' objects </param>
/// <param name="Vfx"> VFX model data for 'VFX' objects </param>
/// <param name="Light"> light model data for 'Light' objects </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record WorldObjectModelData(
    BgObjectModelData? BgObject = null,
    FurnitureModelData? Furniture = null,
    VfxModelData? Vfx = null,
    LightModelData? Light = null);

/// <summary> bgobject model patch </summary>
/// <param name="ModelPath"> updated bgobject model path, or null to keep the current path </param>
/// <param name="Transparency"> updated transparency multiplier, or null to keep the current value </param>
/// <param name="DyeColor"> updated bgobject dye color, or null to keep the current value </param>
/// <param name="IsCoveredFromRain"> updated covered by rain flag, or null to keep the current value </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record BgObjectModelPatchData(
    string? ModelPath = null,
    float? Transparency = null,
    ObjectVector4? DyeColor = null,
    bool? IsCoveredFromRain = null);

/// <summary> furniture color patch </summary>
/// <param name="StainId"> updated stain id, or null to keep the current value </param>
/// <param name="UseCustomColor"> updated custom color check, or null to keep the current value </param>
/// <param name="CustomColor"> updated custom rgba tint, or null to keep the current value </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record FurnitureColorPatchData(
    byte? StainId = null,
    bool? UseCustomColor = null,
    ObjectVector4? CustomColor = null);

/// <summary> furniture model patch </summary>
/// <param name="SharedGroupPath"> updated furniture shared group path, or null to keep the current path </param>
/// <param name="Color"> updated furniture color settings, or null to keep the current values </param>
/// <param name="Transparency"> updated transparency multiplier, or null to keep the current value </param>
/// <param name="OutlineColor"> updated outline color, or null to keep the current value </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record FurnitureModelPatchData(
    string? SharedGroupPath = null,
    FurnitureColorPatchData? Color = null,
    float? Transparency = null,
    ObjectOutlineColor? OutlineColor = null);

/// <summary> VFX model patch </summary>
/// <param name="VfxPath"> updated VFX game path, or null to keep the current path </param>
/// <param name="Color"> updated VFX tint, or null to keep the current value </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record VfxModelPatchData(
    string? VfxPath = null,
    ObjectVector4? Color = null);

/// <summary> light flags patch </summary>
/// <param name="EnableMaterialReflection"> updated material reflection check, or null to keep the current value </param>
/// <param name="EnableDynamicLighting"> updated dynamic lighting check, or null to keep the current value </param>
/// <param name="EnableCharacterShadow"> updated character shadow check, or null to keep the current value </param>
/// <param name="EnableObjectShadow"> updated object shadow check, or null to keep the current value </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record LightFlagsPatchData(
    bool? EnableMaterialReflection = null,
    bool? EnableDynamicLighting = null,
    bool? EnableCharacterShadow = null,
    bool? EnableObjectShadow = null);

/// <summary> light shape patch </summary>
/// <param name="Range"> updated light range, or null to keep the current value </param>
/// <param name="Falloff"> updated light falloff, or null to keep the current value </param>
/// <param name="LightAngle"> updated main light angle, or null to keep the current value </param>
/// <param name="FalloffAngle"> updated falloff angle, or null to keep the current value </param>
/// <param name="AngleDegrees"> updated secondary angle pair, or null to keep the current value </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record LightShapePatchData(
    float? Range = null,
    float? Falloff = null,
    float? LightAngle = null,
    float? FalloffAngle = null,
    ObjectVector2? AngleDegrees = null);

/// <summary> light shadow patch </summary>
/// <param name="CharacterShadowRange"> updated character shadow range, or null to keep the current value </param>
/// <param name="ShadowPlaneNear"> updated near shadow plane distance, or null to keep the current value </param>
/// <param name="ShadowPlaneFar"> updated far shadow plane distance, or null to keep the current value </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record LightShadowPatchData(
    float? CharacterShadowRange = null,
    float? ShadowPlaneNear = null,
    float? ShadowPlaneFar = null);

/// <summary> light model patch </summary>
/// <param name="Color"> updated rgb light color, or null to keep the current value </param>
/// <param name="LightType"> updated light type, or null to keep the current value </param>
/// <param name="FalloffType"> updated light falloff type, or null to keep the current value </param>
/// <param name="Flags"> updated light flags, or null to keep the current values </param>
/// <param name="Intensity"> updated intensity multiplier, or null to keep the current value </param>
/// <param name="Shape"> updated shape settings, or null to keep the current values </param>
/// <param name="Shadow"> updated shadow settings, or null to keep the current values </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record LightModelPatchData(
    ObjectVector3? Color = null,
    ObjectLightType? LightType = null,
    ObjectLightFalloffType? FalloffType = null,
    LightFlagsPatchData? Flags = null,
    float? Intensity = null,
    LightShapePatchData? Shape = null,
    LightShadowPatchData? Shadow = null);

/// <summary> object model patch payload </summary>
/// <param name="BgObject"> bgobject model patch for 'BgObject' objects </param>
/// <param name="Furniture"> furniture model patch for 'Furniture' objects </param>
/// <param name="Vfx"> VFX model patch for 'VFX' objects </param>
/// <param name="Light"> light model patch for 'Light' objects </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record WorldObjectModelPatch(
    BgObjectModelPatchData? BgObject = null,
    FurnitureModelPatchData? Furniture = null,
    VfxModelPatchData? Vfx = null,
    LightModelPatchData? Light = null);

