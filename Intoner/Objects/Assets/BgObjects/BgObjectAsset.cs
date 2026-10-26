using Intoner.Objects.Models;

namespace Intoner.Objects.Assets;

internal sealed record BgObjectAsset(
    string ModelPath,
    string Source,
    uint RowId,
    string SourcePath,
    IReadOnlyList<uint> TerritoryIds,
    IReadOnlyList<string> TerritoryNames,
    IReadOnlyList<string> SearchTerms)
{
    public IReadOnlyList<BgColliderDefinition> ColliderDefinitions { get; init; } = [];
    public bool ColliderDefinitionsComplete { get; init; }
}
