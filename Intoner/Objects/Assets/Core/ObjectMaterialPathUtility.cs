using Dalamud.Plugin.Services;
using PenumbraMdlFile = Penumbra.GameData.Files.MdlFile;
using PenumbraMtrlFile = Penumbra.GameData.Files.MtrlFile;

namespace Intoner.Objects.Assets;

internal static class ObjectMaterialPathUtility
{
    public static bool TryResolveMaterialPath(IDataManager gameData, string modelPath, string materialName, out string materialPath)
        => TryResolveMaterialPath(gameData.FileExists, modelPath, materialName, allowMissingCandidate: false, out materialPath);

    public static bool TryResolveMaterialPath(IObjectAssetGameData gameData, string modelPath, string materialName, out string materialPath)
        => TryResolveMaterialPath(gameData.FileExists, modelPath, materialName, allowMissingCandidate: false, out materialPath);

    public static IReadOnlyList<string> CollectGameModelMaterialPaths(IObjectAssetGameData gameData, string modelPath)
    {
        List<string> materialPaths = [];
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        string normalizedModelPath = ObjectPathRules.NormalizeGamePath(modelPath);
        if (!ObjectPathRules.IsModelPath(normalizedModelPath))
        {
            return materialPaths;
        }

        PenumbraMdlFile? model = TryLoadModel(gameData.GetFile(normalizedModelPath)?.Data);
        if (model is null)
        {
            return materialPaths;
        }

        foreach (string materialName in model.Materials)
        {
            if (!TryResolveMaterialPath(gameData, normalizedModelPath, materialName, out string materialPath))
            {
                continue;
            }

            AddCandidatePath(materialPaths, seenPaths, materialPath);
        }

        return materialPaths;
    }

    public static IReadOnlyList<string> CollectLocalModelMaterialPaths(string requestedModelPath, string localModelPath)
    {
        List<string> materialPaths = [];
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        string normalizedRequestedModelPath = ObjectPathRules.NormalizeGamePath(requestedModelPath);
        if (!ObjectPathRules.IsModelPath(normalizedRequestedModelPath))
        {
            return materialPaths;
        }

        PenumbraMdlFile? model = TryLoadModel(ObjectAssetFileUtility.TryReadLocalFileBytes(localModelPath));
        if (model is null)
        {
            return materialPaths;
        }

        foreach (string materialName in model.Materials)
        {
            if (!TryResolveMaterialPath(
                    fileExists: null,
                    normalizedRequestedModelPath,
                    materialName,
                    allowMissingCandidate: true,
                    out string materialPath))
            {
                continue;
            }

            AddCandidatePath(materialPaths, seenPaths, materialPath);
        }

        return materialPaths;
    }

    public static IReadOnlyList<string> CollectModelDependencyPaths(IObjectAssetGameData gameData, string modelPath)
    {
        List<string> dependencyPaths = [];
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string materialPath in CollectGameModelMaterialPaths(gameData, modelPath))
        {
            AddCandidatePath(dependencyPaths, seenPaths, materialPath);
            CollectMaterialDependencyPaths(gameData, materialPath, dependencyPaths, seenPaths);
        }

        return dependencyPaths;
    }

    public static IReadOnlyList<string> CollectMaterialDependencyPaths(IObjectAssetGameData gameData, string materialPath)
    {
        List<string> dependencyPaths = [];
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        CollectMaterialDependencyPaths(gameData, materialPath, dependencyPaths, seenPaths);
        return dependencyPaths;
    }

    public static IReadOnlyList<string> CollectLocalMaterialDependencyPaths(string localMaterialPath)
    {
        List<string> dependencyPaths = [];
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        PenumbraMtrlFile? material = TryLoadMaterial(ObjectAssetFileUtility.TryReadLocalFileBytes(localMaterialPath));
        if (material is null)
        {
            return dependencyPaths;
        }

        foreach (PenumbraMtrlFile.Texture texture in material.Textures)
        {
            AddCandidatePath(dependencyPaths, seenPaths, texture.Path);
        }

        AddCandidatePath(dependencyPaths, seenPaths, material.ShaderPackage.Name);
        return dependencyPaths;
    }

    public static void CollectMaterialDependencyPaths(
        IObjectAssetGameData gameData,
        string materialPath,
        ICollection<string> dependencyPaths,
        ISet<string> seenPaths)
    {
        string normalizedMaterialPath = ObjectPathRules.NormalizeGamePath(materialPath);
        if (!ObjectPathRules.IsMaterialPath(normalizedMaterialPath))
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

            foreach (PenumbraMtrlFile.Texture texture in material.Textures)
            {
                AddCandidatePath(dependencyPaths, seenPaths, texture.Path);
            }

            AddCandidatePath(dependencyPaths, seenPaths, material.ShaderPackage.Name);
        }
        catch
        {
            // ignore invalid material files
        }
    }

    private static bool TryResolveMaterialPath(
        Func<string, bool>? fileExists,
        string modelPath,
        string materialName,
        bool allowMissingCandidate,
        out string materialPath)
    {
        materialPath = string.Empty;

        string normalizedMaterialName = ObjectPathRules.NormalizeGamePath(materialName);
        if (string.IsNullOrWhiteSpace(normalizedMaterialName))
        {
            return false;
        }

        if (!ObjectPathRules.IsMaterialPath(normalizedMaterialName))
        {
            normalizedMaterialName += ".mtrl";
        }

        List<string> candidates = [];
        HashSet<string> seenCandidates = new(StringComparer.OrdinalIgnoreCase);
        AddMaterialPathCandidate(candidates, seenCandidates, normalizedMaterialName);

        string fileName = Path.GetFileName(normalizedMaterialName);
        string modelDirectory = GetGameDirectory(modelPath);
        string modelParentDirectory = GetGameDirectory(modelDirectory);
        string materialDirectory = CombineGamePath(modelParentDirectory, "material");
        AddMaterialPathCandidatesForDirectory(candidates, seenCandidates, modelDirectory, normalizedMaterialName, fileName);
        AddMaterialPathCandidatesForDirectory(candidates, seenCandidates, modelParentDirectory, normalizedMaterialName, fileName);
        AddMaterialPathCandidatesForDirectory(candidates, seenCandidates, materialDirectory, normalizedMaterialName, fileName);

        foreach (string candidate in candidates)
        {
            if (fileExists is not null && !fileExists(candidate))
            {
                continue;
            }

            materialPath = candidate;
            return true;
        }

        if (allowMissingCandidate && candidates.Count > 0)
        {
            materialPath = candidates[0];
            return true;
        }

        return false;
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

    private static void AddMaterialPathCandidate(ICollection<string> candidates, ISet<string> seenCandidates, string path)
        => _ = ObjectPathCollectionUtility.AddGamePath(candidates, seenCandidates, path);

    private static void AddMaterialPathCandidatesForDirectory(
        ICollection<string> candidates,
        ISet<string> seenCandidates,
        string directory,
        string normalizedMaterialName,
        string fileName)
    {
        AddMaterialPathCandidate(candidates, seenCandidates, CombineGamePath(directory, normalizedMaterialName));
        AddMaterialPathCandidate(candidates, seenCandidates, CombineGamePath(directory, fileName));
    }

    private static string GetGameDirectory(string gamePath)
    {
        string normalizedPath = ObjectPathRules.NormalizeGamePath(gamePath);
        int lastSlashIndex = normalizedPath.LastIndexOf('/');
        return lastSlashIndex < 0 ? string.Empty : normalizedPath[..lastSlashIndex];
    }

    private static string CombineGamePath(params string[] segments)
        => ObjectPathRules.NormalizeGamePath(string.Join("/", segments.Where(static segment => !string.IsNullOrWhiteSpace(segment))));

    private static void AddCandidatePath(ICollection<string> candidates, ISet<string> seenPaths, string path)
        => _ = ObjectPathCollectionUtility.AddGamePath(candidates, seenPaths, path);
}

