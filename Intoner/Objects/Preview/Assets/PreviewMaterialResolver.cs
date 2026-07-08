using Dalamud.Plugin.Services;
using Intoner.Objects.Assets;
using Microsoft.Extensions.Logging;
using Penumbra.GameData.Files.MaterialStructs;
using ShaderNames = Penumbra.GameData.Files.ShaderStructs.Names;
using PenumbraMtrlFile = Penumbra.GameData.Files.MtrlFile;

namespace Intoner.Objects.Preview.Assets;

internal sealed class PreviewMaterialResolver(
    ILogger logger,
    IDataManager gameData,
    PreviewTextureCache textureCache)
{
    private readonly Lock _materialPathCacheLock = new();
    private readonly Dictionary<string, string?> _materialPathCache = new(StringComparer.OrdinalIgnoreCase);

    public ModelPreviewGeometryReader.PreviewMaterial[] Resolve(
        IReadOnlyList<ModelPreviewGeometryReader.PreviewMaterial> materials,
        string modelPath)
    {
        ModelPreviewGeometryReader.PreviewMaterial[] resolvedMaterials = new ModelPreviewGeometryReader.PreviewMaterial[materials.Count];
        for (var i = 0; i < materials.Count; i++)
        {
            string materialName = materials[i].Name;
            if (!TryResolveMaterialPath(modelPath, materialName, out string materialPath)
             || !TryLoadMaterial(materialPath, out PenumbraMtrlFile material)
             || !TextureMapKindResolver.TryGetTexturePath(material, TextureMapKind.Diffuse, out string texturePath))
            {
                resolvedMaterials[i] = new ModelPreviewGeometryReader.PreviewMaterial(materialName, null, null, false, false, 1f);
                continue;
            }

            string normalizedTexturePath = GameAssetPathRules.NormalizeGamePath(texturePath);
            ModelPreviewGeometryReader.PreviewTexture? diffuseTexture = textureCache.GetOrLoad(normalizedTexturePath);
            resolvedMaterials[i] = new ModelPreviewGeometryReader.PreviewMaterial(
                materialPath,
                string.IsNullOrWhiteSpace(normalizedTexturePath) ? null : normalizedTexturePath,
                diffuseTexture,
                ShouldApplyAlphaClip(material),
                IsTransparent(material),
                GetTransparency(material));
        }

        return resolvedMaterials;
    }

    public void EnsureTextures(ModelPreviewGeometryReader.PreviewGeometry geometry)
    {
        foreach (ModelPreviewGeometryReader.PreviewMaterial material in geometry.Materials)
        {
            if (material.DiffuseTexture is not null || string.IsNullOrWhiteSpace(material.DiffuseTexturePath))
            {
                continue;
            }

            material.DiffuseTexture = textureCache.GetOrLoad(material.DiffuseTexturePath);
        }
    }

    public static void ReleaseTextures(ModelPreviewGeometryReader.PreviewGeometry geometry)
    {
        foreach (ModelPreviewGeometryReader.PreviewMaterial material in geometry.Materials)
        {
            material.DiffuseTexture = null;
        }
    }

    public void Clear()
    {
        lock (_materialPathCacheLock)
        {
            _materialPathCache.Clear();
        }
    }

    private static bool IsTransparent(PenumbraMtrlFile material)
        => new ShaderFlags(material.ShaderPackage.Flags).EnableTransparency;

    private static bool ShouldApplyAlphaClip(PenumbraMtrlFile material)
    {
        const float AlphaThresholdEpsilon = 0.001f;

        foreach (var constant in material.ShaderPackage.Constants)
        {
            var constantName = ShaderNames.TryResolve(ShaderNames.KnownNames, constant.Id);
            if (constantName != "g_AlphaThreshold" && constantName != "g_ShadowAlphaThreshold")
            {
                continue;
            }

            ReadOnlySpan<float> values = material.GetConstantValue<float>(constant);
            if (values.IsEmpty)
            {
                continue;
            }

            for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
            {
                if (values[valueIndex] > AlphaThresholdEpsilon)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static float GetTransparency(PenumbraMtrlFile material)
    {
        const float DefaultTransparency = 1f;
        const float TransparencyEpsilon = 0.001f;

        foreach (var constant in material.ShaderPackage.Constants)
        {
            var constantName = ShaderNames.TryResolve(ShaderNames.KnownNames, constant.Id);
            if (constantName != "g_Transparency")
            {
                continue;
            }

            ReadOnlySpan<float> values = material.GetConstantValue<float>(constant);
            if (values.IsEmpty)
            {
                continue;
            }

            float transparency = values[0];
            if (transparency > TransparencyEpsilon)
            {
                return Math.Clamp(transparency, 0f, 1f);
            }
        }

        return DefaultTransparency;
    }

    private bool TryLoadMaterial(string materialPath, out PenumbraMtrlFile material)
    {
        material = null!;

        try
        {
            var gameFile = gameData.GetFile(materialPath);
            if (gameFile is null)
            {
                return false;
            }

            material = new PenumbraMtrlFile(gameFile.Data);
            return material.Valid;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load preview material {MaterialPath}", materialPath);
            return false;
        }
    }

    private bool TryResolveMaterialPath(string modelPath, string materialName, out string materialPath)
    {
        materialPath = string.Empty;

        string normalizedMaterialName = GameAssetPathRules.NormalizeGamePath(materialName);
        if (string.IsNullOrWhiteSpace(normalizedMaterialName))
        {
            return false;
        }

        if (!GameAssetPathRules.IsFileKind(normalizedMaterialName, GameAssetFileKind.Mtrl))
        {
            normalizedMaterialName += ".mtrl";
        }

        string cacheKey = $"{GameAssetPathRules.NormalizeGamePath(modelPath)}|{normalizedMaterialName}";
        lock (_materialPathCacheLock)
        {
            if (_materialPathCache.TryGetValue(cacheKey, out string? cachedPath))
            {
                materialPath = cachedPath ?? string.Empty;
                return cachedPath is not null;
            }
        }

        if (GameMaterialPathUtility.TryResolveExistingMaterialPath(gameData.FileExists, modelPath, normalizedMaterialName, out string resolvedMaterialPath))
        {
            materialPath = resolvedMaterialPath;
            lock (_materialPathCacheLock)
            {
                _materialPathCache[cacheKey] = resolvedMaterialPath;
            }

            return true;
        }

        lock (_materialPathCacheLock)
        {
            _materialPathCache[cacheKey] = null;
        }

        return false;
    }
}
