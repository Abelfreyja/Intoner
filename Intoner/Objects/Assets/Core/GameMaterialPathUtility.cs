using Intoner.Objects.Resources;

namespace Intoner.Objects.Assets;

internal static class GameMaterialPathUtility
{
    public static bool TryResolveMaterialPath(
        Func<string, bool> fileExists,
        string modelPath,
        string materialName,
        out string materialPath,
        bool allowMissing = false)
    {
        IReadOnlyList<string> candidates = CollectMaterialPathCandidates(modelPath, materialName);
        string? primaryCandidate = null;

        foreach (string candidate in candidates)
        {
            if (fileExists(candidate))
            {
                materialPath = candidate;
                return true;
            }

            if (primaryCandidate is null && IsQualifiedMaterialPath(candidate))
            {
                primaryCandidate = candidate;
            }
        }

        materialPath = allowMissing && candidates.Count > 0
            ? primaryCandidate ?? candidates[0]
            : string.Empty;
        return materialPath.Length > 0;
    }

    public static bool TryNormalizeMaterialRequest(
        string modelPath,
        string materialName,
        out string normalizedModelPath,
        out string normalizedMaterialName)
    {
        normalizedModelPath = ObjectResolvedPath.FromGamePath(modelPath).Path;
        normalizedMaterialName = ObjectResolvedPath.FromGamePath(materialName).Path;

        if (!GameAssetPathRules.IsFileKind(normalizedModelPath, GameAssetFileKind.Mdl)
         || string.IsNullOrWhiteSpace(normalizedMaterialName))
        {
            return false;
        }

        if (!GameAssetPathRules.IsFileKind(normalizedMaterialName, GameAssetFileKind.Mtrl))
        {
            normalizedMaterialName = ObjectResolvedPath.FromGamePath(normalizedMaterialName + ".mtrl").Path;
        }

        return normalizedMaterialName.Length > 0;
    }

    private static IReadOnlyList<string> CollectMaterialPathCandidates(string modelPath, string materialName)
    {
        if (!TryNormalizeMaterialRequest(modelPath, materialName, out string normalizedModelPath, out string normalizedMaterialName))
        {
            return [];
        }

        if (IsQualifiedMaterialPath(normalizedMaterialName))
        {
            return [normalizedMaterialName];
        }

        List<string> candidates = [];
        HashSet<string> seenCandidates = new(StringComparer.OrdinalIgnoreCase);
        AddMaterialPathCandidate(candidates, seenCandidates, normalizedMaterialName);

        string fileName = Path.GetFileName(normalizedMaterialName);
        string modelDirectory = GetGameDirectory(normalizedModelPath);
        string modelParentDirectory = GetGameDirectory(modelDirectory);
        string materialDirectory = CombineGamePath(modelParentDirectory, "material");
        AddMaterialPathCandidatesForDirectory(candidates, seenCandidates, modelDirectory, normalizedMaterialName, fileName);
        AddMaterialPathCandidatesForDirectory(candidates, seenCandidates, modelParentDirectory, normalizedMaterialName, fileName);
        AddMaterialPathCandidatesForDirectory(candidates, seenCandidates, materialDirectory, normalizedMaterialName, fileName);

        return candidates;
    }

    private static void AddMaterialPathCandidate(ICollection<string> candidates, ISet<string> seenCandidates, string path)
        => _ = GameAssetPathCollectionUtility.AddGamePath(candidates, seenCandidates, ObjectResolvedPath.FromGamePath(path).Path);

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
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(gamePath);
        int lastSlashIndex = normalizedPath.LastIndexOf('/');
        return lastSlashIndex < 0 ? string.Empty : normalizedPath[..lastSlashIndex];
    }

    private static string CombineGamePath(params string[] segments)
        => GameAssetPathRules.NormalizeGamePath(string.Join("/", segments.Where(static segment => !string.IsNullOrWhiteSpace(segment))));

    private static bool IsQualifiedMaterialPath(string path)
        => path.Contains('/', StringComparison.Ordinal)
            && GameAssetPathRules.IsFileKind(path, GameAssetFileKind.Mtrl);
}
