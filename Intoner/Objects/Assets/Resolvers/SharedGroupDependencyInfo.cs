namespace Intoner.Objects.Assets;

internal sealed class SharedGroupDependencyInfo(
    IReadOnlyList<string> modelPaths,
    IReadOnlyList<string> nestedSharedGroupPaths,
    IReadOnlyList<string> referencedVfxPaths)
{
    public IReadOnlyList<string> ModelPaths { get; } = modelPaths;
    public IReadOnlyList<string> NestedSharedGroupPaths { get; } = nestedSharedGroupPaths;
    public IReadOnlyList<string> ReferencedVfxPaths { get; } = referencedVfxPaths;
    public IReadOnlyList<string> DependencyPaths { get; } = ObjectPathCollectionUtility.MergeGamePaths(
        modelPaths,
        nestedSharedGroupPaths,
        referencedVfxPaths);
}
