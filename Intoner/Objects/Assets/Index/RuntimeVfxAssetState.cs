namespace Intoner.Objects.Assets;

internal sealed class RuntimeVfxAssetState(string path)
{
    public string Path { get; } = path;
    public RuntimeVfxEvidence SourceEvidence { get; set; }
    public bool SeenFromRuntime { get; set; }
    public VfxAnalysis? Analysis { get; set; }
    public VfxStandaloneReport? Report { get; set; }

    public RuntimeVfxEvidence Evidence
        => SourceEvidence | (Report?.CombinedEvidence ?? RuntimeVfxEvidence.None);
}
