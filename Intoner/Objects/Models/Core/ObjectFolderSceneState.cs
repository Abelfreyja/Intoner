namespace Intoner.Objects.Models;

internal sealed record ObjectFolderSceneState
{
    public IReadOnlyList<string> StandaloneFolders { get; init; } = [];
    public IReadOnlyDictionary<string, string> StandaloneFolderColors { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public Guid? DefaultLayoutId { get; init; }
    public IReadOnlyList<string> DefaultLayoutFolders { get; init; } = [];
    public IReadOnlyDictionary<string, string> DefaultLayoutFolderColors { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

