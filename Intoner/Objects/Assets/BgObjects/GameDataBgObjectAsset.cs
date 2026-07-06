namespace Intoner.Objects.Assets;

internal sealed record GameDataBgObjectAsset(
    string ModelPath,
    string Source,
    uint RowId,
    string SourcePath,
    IReadOnlyList<uint> TerritoryIds,
    IReadOnlyList<string> TerritoryNames,
    IReadOnlyList<string> SearchTerms);
