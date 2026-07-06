using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

internal sealed class RuntimeVfxAssetState(string path)
{
    private IReadOnlyList<string>? _searchTerms;

    public string Path { get; } = path;
    public string CatalogSource { get; set; } = "vfx";
    public RuntimeVfxEvidence Evidence { get; set; }
    public bool SeenFromRuntime { get; set; }
    public VfxAnalysis? Analysis { get; set; }
    public AssetPathContract PathContracts { get; set; }
    public KnownVfxFamily FamilyHint { get; set; }
    public VfxStandaloneSupportClass SupportClass { get; set; }
    public VfxStandaloneUnsupportedReason UnsupportedReasons { get; set; }
    public VfxStandaloneContextClue ContextClues { get; set; }
    public VfxStandaloneUnknownReason UnknownReasons { get; set; }

    public string GetPrimarySource()
        => CatalogSource;

    public void InvalidateSearchTerms()
        => _searchTerms = null;

    public IReadOnlyList<string> BuildSearchTerms()
    {
        if (_searchTerms is not null)
        {
            return _searchTerms;
        }

        HashSet<string> searchTerms = ObjectSearchTermUtility.CreateSet();
        if (FamilyHint.TryGetSearchLabel() is { } familySearchTerm)
        {
            _ = ObjectSearchTermUtility.AddTerm(searchTerms, familySearchTerm);
        }

        _searchTerms = ObjectSearchTermUtility.BuildStableTerms(searchTerms);
        return _searchTerms;
    }

    public CacheSaveCapture CaptureForSave()
        => new(
            Path,
            Evidence,
            SeenFromRuntime,
            SupportClass);

    public readonly record struct CacheSaveCapture(
        string Path,
        RuntimeVfxEvidence Evidence,
        bool SeenFromRuntime,
        VfxStandaloneSupportClass SupportClass);
}
