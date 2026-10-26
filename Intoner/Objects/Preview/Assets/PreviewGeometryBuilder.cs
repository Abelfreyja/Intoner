using Dalamud.Plugin.Services;
using Intoner.Objects.Assets;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Preview.Assets;

internal sealed class PreviewGeometryBuilder(
    IDataManager gameData,
    PreviewMaterialResolver materialResolver)
{
    public ModelPreviewGeometryReader.PreviewGeometry? BuildMergedGeometry(
        IReadOnlyList<PreviewModelInfo> previewModels,
        out IReadOnlyList<PreviewAssetState.MaterialRequest> missingMaterials,
        out IReadOnlySet<int> loadedModelIndices,
        out string? error)
    {
        HashSet<int> loadedIndices = [];
        loadedModelIndices = loadedIndices;
        List<Vector3> mergedPositions = [];
        List<Vector2> mergedTexCoords = [];
        List<int> mergedIndices = [];
        List<int> mergedTriangleMaterialIndices = [];
        List<ModelPreviewGeometryReader.PreviewMaterial> mergedMaterials = [];
        List<PreviewAssetState.MaterialRequest> mergedMissingMaterials = [];
        Dictionary<string, ModelPreviewGeometryReader.PreviewGeometry> loadedGeometry = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PreviewMaterialResolver.Resolution> resolvedMaterialCache = new(StringComparer.OrdinalIgnoreCase);
        string? firstReason = null;

        for (int modelIndex = 0; modelIndex < previewModels.Count; ++modelIndex)
        {
            PreviewModelInfo previewModel = previewModels[modelIndex];
            string modelPath = ObjectResolvedPath.FromGamePath(previewModel.ModelPath).Path;

            if (!GameAssetPathRules.IsFileKind(modelPath, GameAssetFileKind.Mdl))
            {
                firstReason ??= $"Invalid preview model path: {previewModel.ModelPath}";
                continue;
            }

            if (!TryLoadPreviewGeometry(modelPath, loadedGeometry, out ModelPreviewGeometryReader.PreviewGeometry modelGeometry, out string? loadReason))
            {
                firstReason ??= loadReason ?? $"Model file could not be loaded: {previewModel.ModelPath}";
                continue;
            }

            if (!resolvedMaterialCache.TryGetValue(modelPath, out PreviewMaterialResolver.Resolution resolution))
            {
                resolution = materialResolver.Resolve(modelGeometry.Materials, modelPath);
                resolvedMaterialCache[modelPath] = resolution;
            }

            int baseVertexIndex = mergedPositions.Count;
            int baseMaterialIndex = mergedMaterials.Count;
            AppendPreviewPositions(mergedPositions, modelGeometry.Positions, previewModel.Transform);
            mergedTexCoords.AddRange(modelGeometry.TexCoords);

            for (int index = 0; index < modelGeometry.Indices.Length; ++index)
            {
                mergedIndices.Add(baseVertexIndex + modelGeometry.Indices[index]);
            }

            for (int triangleIndex = 0; triangleIndex < modelGeometry.TriangleMaterialIndices.Length; ++triangleIndex)
            {
                int materialIndex = modelGeometry.TriangleMaterialIndices[triangleIndex];
                mergedTriangleMaterialIndices.Add(materialIndex >= 0 ? baseMaterialIndex + materialIndex : -1);
            }

            mergedMaterials.AddRange(resolution.Materials);

            foreach (PreviewAssetState.MaterialRequest request in resolution.MissingMaterials)
            {
                mergedMissingMaterials.Add(request with { Index = baseMaterialIndex + request.Index });
            }

            loadedIndices.Add(modelIndex);
        }

        if (mergedPositions.Count == 0 || mergedIndices.Count < 3)
        {
            missingMaterials = [];
            error = firstReason ?? "Preview geometry could not be loaded.";
            return null;
        }

        ModelPreviewGeometryReader.PreviewGeometry geometry = new(
            mergedPositions.ToArray(),
            mergedTexCoords.ToArray(),
            mergedIndices.ToArray(),
            mergedTriangleMaterialIndices.ToArray(),
            mergedMaterials.ToArray());

        if (!NumericsUtility.IsFinite(geometry.Bounds.Center) || !float.IsFinite(geometry.Bounds.Radius))
        {
            missingMaterials = [];
            error = "Preview model transforms produce invalid bounds.";
            return null;
        }

        missingMaterials = mergedMissingMaterials;
        error = null;
        return geometry;
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

        try
        {
            var file = gameData.GetFile(modelPath);

            if (file is null)
            {
                geometry = null!;
                reason = $"Model file could not be loaded: {modelPath}";
                return false;
            }

            if (!ModelPreviewGeometryReader.TryLoad(file.Data, out geometry, out reason))
            {
                return false;
            }
        }
        catch (Exception ex) when (ObjectAssetFileUtility.IsReadFailure(ex))
        {
            geometry = null!;
            reason = $"Model file could not be loaded: {modelPath}";
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
