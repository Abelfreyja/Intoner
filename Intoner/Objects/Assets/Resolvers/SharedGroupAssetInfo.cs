namespace Intoner.Objects.Assets;

internal sealed class SharedGroupAssetInfo(
    IReadOnlyList<PreviewModelInfo> previewModels,
    IReadOnlyList<string> bgObjectModelPaths,
    IReadOnlyList<string> nestedSharedGroupPaths,
    IReadOnlyList<string> standaloneVfxPaths,
    IReadOnlyList<string> referencedVfxPaths)
{
    public IReadOnlyList<PreviewModelInfo> PreviewModels { get; } = previewModels;
    public IReadOnlyList<string> PreviewModelPaths { get; } = previewModels
        .Select(static model => model.ModelPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public IReadOnlyList<string> BgObjectModelPaths { get; } = bgObjectModelPaths;
    public IReadOnlyList<string> NestedSharedGroupPaths { get; } = nestedSharedGroupPaths;
    public IReadOnlyList<string> StandaloneVfxPaths { get; } = standaloneVfxPaths;
    public IReadOnlyList<string> ReferencedVfxPaths { get; } = referencedVfxPaths;
    public IReadOnlyList<string> CollectionDependencyPaths { get; } = GameAssetPathCollectionUtility.MergeGamePaths(
        bgObjectModelPaths,
        nestedSharedGroupPaths,
        referencedVfxPaths);
}
