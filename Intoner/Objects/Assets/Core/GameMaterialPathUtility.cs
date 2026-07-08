namespace Intoner.Objects.Assets;

internal static class GameMaterialPathUtility
{
    public static bool TryResolveExistingMaterialPath(
        Func<string, bool> fileExists,
        string modelPath,
        string materialName,
        out string materialPath)
    {
        foreach (string candidate in CollectMaterialPathCandidates(modelPath, materialName))
        {
            if (!fileExists(candidate))
            {
                continue;
            }

            materialPath = candidate;
            return true;
        }

        materialPath = string.Empty;
        return false;
    }

    public static bool TryGetPrimaryMaterialPathCandidate(string modelPath, string materialName, out string materialPath)
    {
        IReadOnlyList<string> candidates = CollectMaterialPathCandidates(modelPath, materialName);
        foreach (string candidate in candidates)
        {
            if (IsQualifiedMaterialPath(candidate))
            {
                materialPath = candidate;
                return true;
            }
        }

        if (candidates.Count == 0)
        {
            materialPath = string.Empty;
            return false;
        }

        materialPath = candidates[0];
        return true;
    }

    private static IReadOnlyList<string> CollectMaterialPathCandidates(string modelPath, string materialName)
    {
        string normalizedMaterialName = GameAssetPathRules.NormalizeGamePath(materialName);
        if (string.IsNullOrWhiteSpace(normalizedMaterialName))
        {
            return [];
        }

        if (!GameAssetPathRules.IsFileKind(normalizedMaterialName, GameAssetFileKind.Mtrl))
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

        return candidates;
    }

    private static void AddMaterialPathCandidate(ICollection<string> candidates, ISet<string> seenCandidates, string path)
        => _ = GameAssetPathCollectionUtility.AddGamePath(candidates, seenCandidates, path);

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
