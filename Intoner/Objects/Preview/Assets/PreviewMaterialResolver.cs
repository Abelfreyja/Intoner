using Dalamud.Plugin.Services;
using Intoner.Objects.Assets;
using Microsoft.Extensions.Logging;
using Penumbra.GameData.Files.Materials;
using System.Runtime.InteropServices;
using PenumbraMtrlFile = Penumbra.GameData.Files.Materials.MtrlFile;
using ShaderNames = Penumbra.GameData.Files.ShaderStructs.Names;

namespace Intoner.Objects.Preview.Assets;

internal sealed class PreviewMaterialResolver(
    ILogger logger,
    IDataManager gameData,
    PreviewTextureCache textureCache) : IDisposable
{
    private readonly Lock _materialPathCacheLock = new();
    private readonly Dictionary<string, string> _materialPathCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct Resolution(
        ModelPreviewGeometryReader.PreviewMaterial[] Materials,
        IReadOnlyList<PreviewAssetState.MaterialRequest> MissingMaterials);

    public Resolution Resolve(
        IReadOnlyList<ModelPreviewGeometryReader.PreviewMaterial> materials,
        string modelPath)
    {
        Dictionary<string, PenumbraMtrlFile> parsedMaterials = new(StringComparer.OrdinalIgnoreCase);
        ModelPreviewGeometryReader.PreviewMaterial[] resolvedMaterials = new ModelPreviewGeometryReader.PreviewMaterial[materials.Count];
        List<PreviewAssetState.MaterialRequest> missingMaterials = [];

        for (int index = 0; index < materials.Count; ++index)
        {
            string materialName = materials[index].Name;

            if (GameMaterialPathUtility.TryNormalizeMaterialRequest(modelPath, materialName, out string normalizedModelPath, out string normalizedMaterialName))
            {
                PreviewAssetState.MaterialRequest request = new(index, normalizedModelPath, normalizedMaterialName);

                if (TryResolveMaterial(request, parsedMaterials, out ModelPreviewGeometryReader.PreviewMaterial material))
                {
                    resolvedMaterials[index] = material;
                    continue;
                }

                missingMaterials.Add(request);
            }

            resolvedMaterials[index] = new(materialName, null, null, false, false, 1f);
        }

        return new Resolution(resolvedMaterials, missingMaterials);
    }

    public void RefreshMaterials(
        ModelPreviewGeometryReader.PreviewGeometry geometry,
        List<PreviewAssetState.MaterialRequest> missingMaterials)
    {
        Dictionary<string, PenumbraMtrlFile> parsedMaterials = new(StringComparer.OrdinalIgnoreCase);

        for (int index = missingMaterials.Count - 1; index >= 0; --index)
        {
            PreviewAssetState.MaterialRequest request = missingMaterials[index];

            if (!TryResolveMaterial(request, parsedMaterials, out ModelPreviewGeometryReader.PreviewMaterial material))
            {
                continue;
            }

            geometry.Materials[request.Index] = material;
            missingMaterials.RemoveAt(index);
        }
    }

    private bool TryResolveMaterial(
        PreviewAssetState.MaterialRequest request,
        Dictionary<string, PenumbraMtrlFile> parsedMaterials,
        out ModelPreviewGeometryReader.PreviewMaterial material)
    {
        material = null!;

        if (!TryResolveMaterialPath(request.ModelPath, request.Name, out string materialPath)
         || !TryLoadMaterial(materialPath, parsedMaterials, out PenumbraMtrlFile parsedMaterial))
        {
            return false;
        }

        string normalizedTexturePath = TextureMapKindResolver.TryGetTexturePath(parsedMaterial, TextureMapKind.Diffuse, out string texturePath)
            ? texturePath
            : string.Empty;
        ModelPreviewGeometryReader.PreviewTexture? diffuseTexture = normalizedTexturePath.Length > 0
            ? textureCache.GetOrLoad(normalizedTexturePath)
            : null;
        material = new ModelPreviewGeometryReader.PreviewMaterial(
            materialPath,
            normalizedTexturePath.Length > 0 ? normalizedTexturePath : null,
            diffuseTexture,
            ShouldApplyAlphaClip(parsedMaterial),
            IsTransparent(parsedMaterial),
            GetTransparency(parsedMaterial));
        return true;
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

    public long GetNextTextureRetryAtMs(ModelPreviewGeometryReader.PreviewGeometry geometry, long now)
    {
        long nextRetryAtMs = 0;

        foreach (ModelPreviewGeometryReader.PreviewMaterial material in geometry.Materials)
        {
            if (material.DiffuseTexture is not null || string.IsNullOrWhiteSpace(material.DiffuseTexturePath))
            {
                continue;
            }

            long retryAtMs = textureCache.GetNextRetryAtMs(material.DiffuseTexturePath, now);

            if (retryAtMs > 0 && (nextRetryAtMs == 0 || retryAtMs < nextRetryAtMs))
            {
                nextRetryAtMs = retryAtMs;
            }
        }

        return nextRetryAtMs;
    }

    public static bool ReleaseTextures(ModelPreviewGeometryReader.PreviewGeometry geometry)
    {
        bool released = false;

        foreach (ModelPreviewGeometryReader.PreviewMaterial material in geometry.Materials)
        {
            released |= material.DiffuseTexture is not null;
            material.DiffuseTexture = null;
        }

        return released;
    }

    public void Dispose()
    {
        lock (_materialPathCacheLock)
        {
            _disposed = true;
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

    private bool TryLoadMaterial(
        string materialPath,
        Dictionary<string, PenumbraMtrlFile> parsedMaterials,
        out PenumbraMtrlFile material)
    {
        if (parsedMaterials.TryGetValue(materialPath, out material!))
        {
            return true;
        }

        material = null!;

        try
        {
            var gameFile = gameData.GetFile(materialPath);
            if (gameFile is null)
            {
                return false;
            }

            material = new PenumbraMtrlFile(gameFile.Data);
            parsedMaterials.Add(materialPath, material);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
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

        string cacheKey = $"{modelPath}|{materialName}";
        lock (_materialPathCacheLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_materialPathCache.TryGetValue(cacheKey, out string? cachedPath))
            {
                materialPath = cachedPath;
                return true;
            }
        }

        if (GameMaterialPathUtility.TryResolveMaterialPath(gameData.FileExists, modelPath, materialName, out string resolvedMaterialPath))
        {
            materialPath = resolvedMaterialPath;
            lock (_materialPathCacheLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _materialPathCache[cacheKey] = resolvedMaterialPath;
            }

            return true;
        }

        return false;
    }
}
