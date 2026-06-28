using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

internal static class VfxResolvedPathUtility
{
    public static bool TryMergeResolvedPath(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        IObjectAssetGameData gameData,
        string path,
        SqpackIndexSnapshot? sqpackIndexSnapshot,
        KnownVfxFamily family,
        RuntimeVfxEvidence evidence,
        AssetPathSource sources,
        AssetPathContract contracts,
        IEnumerable<string> searchTerms,
        VfxAnalysis? analysis = null)
    {
        string normalizedPath = ObjectPathRules.NormalizeGamePath(path);
        return TryMergeResolvedPathCore(
            resolvedPaths,
            gameData,
            normalizedPath,
            sqpackIndexSnapshot,
            family,
            evidence,
            sources,
            contracts,
            searchTerms,
            analysis);
    }

    public static bool TryMergeResolvedPath(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        IObjectAssetGameData gameData,
        ResolvedVfxPath resolvedPath,
        SqpackIndexSnapshot? sqpackIndexSnapshot = null,
        IReadOnlyList<string>? extraSearchTerms = null)
    {
        string normalizedPath = ObjectPathRules.NormalizeGamePath(resolvedPath.Path);
        IReadOnlyList<string> searchTerms = extraSearchTerms is null
            ? resolvedPath.SearchTerms
            : ObjectSearchTermUtility.MergeTerms(resolvedPath.SearchTerms, extraSearchTerms);

        return TryMergeResolvedPathCore(
            resolvedPaths,
            gameData,
            normalizedPath,
            sqpackIndexSnapshot,
            resolvedPath.Family,
            resolvedPath.Evidence,
            resolvedPath.Sources,
            resolvedPath.Contracts,
            searchTerms,
            resolvedPath.Analysis);
    }

    private static bool TryMergeResolvedPathCore(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        IObjectAssetGameData gameData,
        string normalizedPath,
        SqpackIndexSnapshot? sqpackIndexSnapshot,
        KnownVfxFamily family,
        RuntimeVfxEvidence evidence,
        AssetPathSource sources,
        AssetPathContract contracts,
        IEnumerable<string> searchTerms,
        VfxAnalysis? analysis)
    {
        if (!IsExistingVfxPath(gameData, normalizedPath, sqpackIndexSnapshot))
        {
            return false;
        }

        KnownVfxFamily resolvedFamily = family != KnownVfxFamily.None
            ? family
            : KnownVfxFamilyExtensions.InferFamilyHintFromPath(normalizedPath);

        ResolvedVfxPathAccumulator.MergeInto(
            resolvedPaths,
            normalizedPath,
            resolvedFamily,
            evidence,
            sources,
            contracts,
            searchTerms,
            analysis);
        return true;
    }

    private static bool IsExistingVfxPath(
        IObjectAssetGameData gameData,
        string normalizedPath,
        SqpackIndexSnapshot? sqpackIndexSnapshot)
        => ObjectPathRules.IsVfxPath(normalizedPath)
         && ((sqpackIndexSnapshot?.ContainsPath(normalizedPath) ?? false)
          || gameData.FileExists(normalizedPath));
}

