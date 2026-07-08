using PenumbraMdlFile = Penumbra.GameData.Files.MdlFile;
using PenumbraMtrlFile = Penumbra.GameData.Files.MtrlFile;

namespace Intoner.Objects.Assets;

internal static class ObjectMaterialPathUtility
{
    public static IReadOnlyList<string> CollectGameModelMaterialPaths(IObjectAssetGameData gameData, string modelPath)
    {
        string normalizedModelPath = GameAssetPathRules.NormalizeGamePath(modelPath);
        if (!GameAssetPathRules.IsFileKind(normalizedModelPath, GameAssetFileKind.Mdl))
        {
            return [];
        }

        PenumbraMdlFile? model = TryLoadModel(gameData.GetFile(normalizedModelPath)?.Data);
        return CollectModelMaterialPaths(
            normalizedModelPath,
            model,
            gameData.FileExists,
            includeUnresolvedCandidate: false);
    }

    public static IReadOnlyList<string> CollectLocalModelMaterialPaths(
        IObjectAssetGameData gameData,
        string requestedModelPath,
        string localModelPath)
    {
        string normalizedRequestedModelPath = GameAssetPathRules.NormalizeGamePath(requestedModelPath);
        if (!GameAssetPathRules.IsFileKind(normalizedRequestedModelPath, GameAssetFileKind.Mdl))
        {
            return [];
        }

        PenumbraMdlFile? model = TryLoadModel(ObjectAssetFileUtility.TryReadLocalFileBytes(localModelPath));
        return CollectModelMaterialPaths(
            normalizedRequestedModelPath,
            model,
            gameData.FileExists,
            includeUnresolvedCandidate: true);
    }

    public static IReadOnlyList<string> CollectMaterialDependencyPaths(IObjectAssetGameData gameData, string materialPath)
    {
        List<string> dependencyPaths = [];
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        CollectGameMaterialDependencyPaths(gameData, materialPath, dependencyPaths, seenPaths);
        return dependencyPaths;
    }

    private static void CollectGameMaterialDependencyPaths(
        IObjectAssetGameData gameData,
        string materialPath,
        ICollection<string> dependencyPaths,
        ISet<string> seenPaths)
    {
        string normalizedMaterialPath = GameAssetPathRules.NormalizeGamePath(materialPath);
        if (!GameAssetPathRules.IsFileKind(normalizedMaterialPath, GameAssetFileKind.Mtrl))
        {
            return;
        }

        try
        {
            var gameFile = gameData.GetFile(normalizedMaterialPath);
            if (gameFile is null)
            {
                return;
            }

            PenumbraMtrlFile material = new(gameFile.Data);
            if (!material.Valid)
            {
                return;
            }

            CollectMaterialDependencyPaths(material, dependencyPaths, seenPaths);
        }
        catch
        {
            // ignore invalid material files
        }
    }

    public static IReadOnlyList<string> CollectLocalMaterialDependencyPaths(string localMaterialPath)
    {
        List<string> dependencyPaths = [];
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        PenumbraMtrlFile? material = TryLoadMaterial(ObjectAssetFileUtility.TryReadLocalFileBytes(localMaterialPath));
        CollectMaterialDependencyPaths(material, dependencyPaths, seenPaths);
        return dependencyPaths;
    }

    private static IReadOnlyList<string> CollectModelMaterialPaths(
        string normalizedModelPath,
        PenumbraMdlFile? model,
        Func<string, bool> fileExists,
        bool includeUnresolvedCandidate)
    {
        if (model is null)
        {
            return [];
        }

        List<string> materialPaths = [];
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string materialName in model.Materials)
        {
            if (TryResolveMaterialPath(normalizedModelPath, materialName, fileExists, includeUnresolvedCandidate, out string materialPath))
            {
                AddCandidatePath(materialPaths, seenPaths, materialPath);
            }
        }

        return materialPaths;
    }

    private static bool TryResolveMaterialPath(
        string normalizedModelPath,
        string materialName,
        Func<string, bool> fileExists,
        bool includeUnresolvedCandidate,
        out string materialPath)
    {
        if (GameMaterialPathUtility.TryResolveExistingMaterialPath(fileExists, normalizedModelPath, materialName, out materialPath))
        {
            return true;
        }

        return includeUnresolvedCandidate
            && GameMaterialPathUtility.TryGetPrimaryMaterialPathCandidate(normalizedModelPath, materialName, out materialPath);
    }

    private static void CollectMaterialDependencyPaths(
        PenumbraMtrlFile? material,
        ICollection<string> dependencyPaths,
        ISet<string> seenPaths)
    {
        if (material is null)
        {
            return;
        }

        foreach (PenumbraMtrlFile.Texture texture in material.Textures)
        {
            AddCandidatePath(dependencyPaths, seenPaths, texture.Path);
        }

        AddCandidatePath(dependencyPaths, seenPaths, material.ShaderPackage.Name);
    }

    private static PenumbraMdlFile? TryLoadModel(byte[]? data)
    {
        if (data is null)
        {
            return null;
        }

        try
        {
            PenumbraMdlFile model = new(data);
            return model.Valid ? model : null;
        }
        catch
        {
            return null;
        }
    }

    private static PenumbraMtrlFile? TryLoadMaterial(byte[]? data)
    {
        if (data is null)
        {
            return null;
        }

        try
        {
            PenumbraMtrlFile material = new(data);
            return material.Valid ? material : null;
        }
        catch
        {
            return null;
        }
    }

    private static void AddCandidatePath(ICollection<string> candidates, ISet<string> seenPaths, string path)
        => _ = GameAssetPathCollectionUtility.AddGamePath(candidates, seenPaths, path);
}

