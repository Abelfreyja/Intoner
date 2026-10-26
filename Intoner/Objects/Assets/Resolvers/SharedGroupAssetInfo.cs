using Intoner.Objects.Models;

namespace Intoner.Objects.Assets;

internal sealed class SharedGroupAssetInfo(
    IReadOnlyList<PreviewModelInfo> previewModels,
    IReadOnlyList<string> bgObjectModelPaths,
    IReadOnlyList<string> nestedSharedGroupPaths,
    IReadOnlyList<string> autoplayVfxPaths,
    IReadOnlyList<string> referencedVfxPaths,
    AssetReferenceSources sources,
    bool isComplete = true,
    bool exists = true)
{
    public static SharedGroupAssetInfo Empty { get; } = new([], [], [], [], [], AssetReferenceSources.Empty);
    public static SharedGroupAssetInfo Missing { get; } = new([], [], [], [], [], AssetReferenceSources.Empty, exists: false);

    public IReadOnlyList<PreviewModelInfo> PreviewModels { get; } = previewModels;
    public IReadOnlyList<string> BgObjectModelPaths { get; } = bgObjectModelPaths;
    public IReadOnlyList<string> NestedSharedGroupPaths { get; } = nestedSharedGroupPaths;
    public IReadOnlyList<string> AutoplayVfxPaths { get; } = autoplayVfxPaths;
    public IReadOnlyList<string> ReferencedVfxPaths { get; } = referencedVfxPaths;
    public AssetReferenceSources Sources { get; } = sources;
    public bool IsComplete { get; } = isComplete;
    public bool Exists { get; } = exists;
    public IReadOnlyDictionary<string, IReadOnlyList<BgColliderDefinition>> ColliderDefinitions { get; init; } = new Dictionary<string, IReadOnlyList<BgColliderDefinition>>(StringComparer.OrdinalIgnoreCase);
}
