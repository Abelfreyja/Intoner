using Dalamud.Plugin.Services;
using Intoner.Objects.Assets;
using System.Numerics;

namespace Intoner.Objects.Preview.Assets;

internal sealed class PreviewGeometryBuilder(
    IDataManager gameData,
    PreviewMaterialResolver materialResolver)
{
    public ModelPreviewGeometryReader.PreviewGeometry? BuildMergedGeometry(
        IReadOnlyList<PreviewModelInfo> previewModels,
        out string? error)
    {
        List<Vector3> mergedPositions = [];
        List<Vector2> mergedTexCoords = [];
        List<int> mergedIndices = [];
        List<int> mergedTriangleMaterialIndices = [];
        List<ModelPreviewGeometryReader.PreviewMaterial> mergedMaterials = [];
        Dictionary<string, ModelPreviewGeometryReader.PreviewGeometry> loadedGeometry = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ModelPreviewGeometryReader.PreviewMaterial[]> resolvedMaterialCache = new(StringComparer.OrdinalIgnoreCase);
        string? firstReason = null;

        for (var i = 0; i < previewModels.Count; i++)
        {
            PreviewModelInfo previewModel = previewModels[i];
            if (!TryAppendMergedPreviewModel(
                    previewModel,
                    mergedPositions,
                    mergedTexCoords,
                    mergedIndices,
                    mergedTriangleMaterialIndices,
                    mergedMaterials,
                    loadedGeometry,
                    resolvedMaterialCache,
                    out string? loadReason))
            {
                firstReason ??= loadReason ?? $"Model file could not be loaded: {previewModel.ModelPath}";
            }
        }

        if (mergedPositions.Count == 0 || mergedIndices.Count < 3)
        {
            error = firstReason ?? "Preview geometry could not be loaded.";
            return null;
        }

        error = null;
        return new ModelPreviewGeometryReader.PreviewGeometry(
            mergedPositions.ToArray(),
            mergedTexCoords.ToArray(),
            mergedIndices.ToArray(),
            mergedTriangleMaterialIndices.ToArray(),
            mergedMaterials.ToArray());
    }

    private bool TryAppendMergedPreviewModel(
        PreviewModelInfo previewModel,
        List<Vector3> mergedPositions,
        List<Vector2> mergedTexCoords,
        List<int> mergedIndices,
        List<int> mergedTriangleMaterialIndices,
        List<ModelPreviewGeometryReader.PreviewMaterial> mergedMaterials,
        IDictionary<string, ModelPreviewGeometryReader.PreviewGeometry> loadedGeometry,
        IDictionary<string, ModelPreviewGeometryReader.PreviewMaterial[]> resolvedMaterialCache,
        out string? reason)
    {
        reason = null;
        if (!TryLoadPreviewGeometry(previewModel.ModelPath, loadedGeometry, out ModelPreviewGeometryReader.PreviewGeometry modelGeometry, out reason))
        {
            return false;
        }

        if (!resolvedMaterialCache.TryGetValue(previewModel.ModelPath, out ModelPreviewGeometryReader.PreviewMaterial[]? resolvedMaterials))
        {
            resolvedMaterials = materialResolver.Resolve(modelGeometry.Materials, previewModel.ModelPath);
            resolvedMaterialCache[previewModel.ModelPath] = resolvedMaterials;
        }

        int baseVertexIndex = mergedPositions.Count;
        int baseMaterialIndex = mergedMaterials.Count;
        AppendPreviewPositions(mergedPositions, modelGeometry.Positions, previewModel.Transform);
        mergedTexCoords.AddRange(modelGeometry.TexCoords);

        for (var index = 0; index < modelGeometry.Indices.Length; index++)
        {
            mergedIndices.Add(baseVertexIndex + modelGeometry.Indices[index]);
        }

        for (var triangleIndex = 0; triangleIndex < modelGeometry.TriangleMaterialIndices.Length; triangleIndex++)
        {
            int materialIndex = modelGeometry.TriangleMaterialIndices[triangleIndex];
            mergedTriangleMaterialIndices.Add(materialIndex >= 0 ? baseMaterialIndex + materialIndex : -1);
        }

        mergedMaterials.AddRange(resolvedMaterials);
        return true;
    }

    private bool TryLoadPreviewGeometry(
        string modelPath,
        IDictionary<string, ModelPreviewGeometryReader.PreviewGeometry> loadedGeometry,
        out ModelPreviewGeometryReader.PreviewGeometry geometry,
        out string? reason)
    {
        reason = null;
        if (loadedGeometry.TryGetValue(modelPath, out geometry!))
        {
            return true;
        }

        var file = gameData.GetFile(modelPath);
        if (file is null)
        {
            geometry = null!;
            reason = $"Model file could not be loaded: {modelPath}";
            return false;
        }

        if (!ModelPreviewGeometryReader.TryLoadPreviewGeometry(file.Data, out geometry, out reason))
        {
            return false;
        }

        loadedGeometry[modelPath] = geometry;
        return true;
    }

    private static void AppendPreviewPositions(
        List<Vector3> mergedPositions,
        IReadOnlyList<Vector3> positions,
        Matrix4x4 transform)
    {
        if (transform == Matrix4x4.Identity)
        {
            mergedPositions.AddRange(positions);
            return;
        }

        for (var i = 0; i < positions.Count; i++)
        {
            mergedPositions.Add(Vector3.Transform(positions[i], transform));
        }
    }
}
