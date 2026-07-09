namespace Intoner.Objects.Assets;

internal sealed class RuntimeVfxAssetState(string path)
{
    public string Path { get; } = path;
    public string CatalogSource { get; set; } = "vfx";
    public RuntimeVfxEvidence Evidence { get; set; }
    public bool SeenFromRuntime { get; set; }
    public VfxAnalysis? Analysis { get; set; }
    public IReadOnlyList<string> SearchTerms { get; set; } = [];
    public AssetPathContract PathContracts { get; set; }
    public KnownVfxFamily FamilyHint { get; set; }
    public VfxStandaloneSupportClass SupportClass { get; set; }

    public CacheSaveCapture CaptureForSave()
        => new(
            Path,
            Evidence,
            SeenFromRuntime,
            SupportClass,
            Analysis);

    public readonly record struct CacheSaveCapture(
        string Path,
        RuntimeVfxEvidence Evidence,
        bool SeenFromRuntime,
        VfxStandaloneSupportClass SupportClass,
        VfxAnalysis? Analysis);
}
