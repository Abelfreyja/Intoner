using Intoner.Objects.Resources;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

internal static class VfxResolvedPathUtility
{
    public static void MergeSharedGroupPaths(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        IObjectAssetGameData gameData,
        SharedGroupAssetInfo sharedGroupAssets,
        IReadOnlyList<string> searchTerms)
    {
        MergePaths(sharedGroupAssets.ReferencedVfxPaths, RuntimeVfxEvidence.LayoutInstance, "shared group vfx");
        MergePaths(sharedGroupAssets.AutoplayVfxPaths, RuntimeVfxEvidence.LayoutAutoplay, "shared group autoplay");

        void MergePaths(IReadOnlyList<string> paths, RuntimeVfxEvidence evidence, string label)
        {
            foreach (string path in paths)
            {
                IReadOnlyList<string> pathTerms = SearchTermUtility.BuildStableTerms(
                    [label, "shared group"], searchTerms, sharedGroupAssets.Sources[path]);
                _ = TryMergeResolvedPath(
                    resolvedPaths,
                    gameData,
                    path,
                    sqpackIndexSnapshot: null,
                    KnownVfxFamily.None,
                    evidence,
                    AssetPathSource.SharedGroup,
                    AssetPathContract.ParsedFileReference,
                    pathTerms);
            }
        }
    }

    public static bool MergeTimelinePaths(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        IObjectAssetGameData gameData,
        TmbVfxReferenceCollection collection,
        RuntimeVfxEvidence evidence,
        IReadOnlyList<string> searchTerms,
        SqpackIndexSnapshot? sqpackIndexSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        foreach (TmbVfxReference reference in collection.References)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TryMergeResolvedPath(
                resolvedPaths,
                gameData,
                reference.Path,
                sqpackIndexSnapshot,
                KnownVfxFamily.None,
                evidence | reference.Evidence,
                AssetPathSource.GameData,
                AssetPathContract.ParsedFileReference,
                SearchTermUtility.MergeTerms(searchTerms, reference.SearchTerms),
                timelineContext: reference.ContextFlags);
        }

        return collection.IsComplete;
    }

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
        VfxAnalysis? analysis = null,
        VfxTimelineContext timelineContext = VfxTimelineContext.None)
    {
        string normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;

        if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx)
         || (!resolvedPaths.ContainsKey(normalizedPath)
          && !(sqpackIndexSnapshot?.ContainsPath(normalizedPath) ?? false)
          && !gameData.FileExists(normalizedPath)))
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
            analysis,
            timelineContext);
        return true;
    }

    public static bool TryMergeResolvedPath(
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        IObjectAssetGameData gameData,
        ResolvedVfxPath resolvedPath,
        SqpackIndexSnapshot? sqpackIndexSnapshot = null,
        IReadOnlyList<string>? extraSearchTerms = null)
    {
        IReadOnlyList<string> searchTerms = extraSearchTerms is null
            ? resolvedPath.SearchTerms
            : SearchTermUtility.MergeTerms(resolvedPath.SearchTerms, extraSearchTerms);

        return TryMergeResolvedPath(
            resolvedPaths,
            gameData,
            resolvedPath.Path,
            sqpackIndexSnapshot,
            resolvedPath.Family,
            resolvedPath.Evidence,
            resolvedPath.Sources,
            resolvedPath.Contracts,
            searchTerms,
            resolvedPath.Analysis,
            resolvedPath.TimelineContext);
    }
}
