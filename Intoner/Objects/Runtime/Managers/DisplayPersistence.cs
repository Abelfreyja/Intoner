using Intoner.Services.Storage;
using Microsoft.Extensions.Logging;

namespace Intoner.Displays;

/// <summary> reads and writes the display section of the scene store </summary>
internal sealed class DisplayPersistence
{
    private const string StoreSection = "displays";

    private readonly ILogger<DisplayPersistence> _logger;
    private readonly ISceneStore _sceneStore;

    public DisplayPersistence(ILogger<DisplayPersistence> logger, ISceneStore sceneStore)
    {
        _logger = logger;
        _sceneStore = sceneStore;
    }

    public IReadOnlyList<DisplaySnapshot> Load()
    {
        if (!_sceneStore.TryRead(StoreSection, out DisplayDocument? document))
        {
            return [];
        }

        if (document.FormatVersion == DisplayDocument.CurrentFormatVersion)
        {
            return document.Displays;
        }

        _logger.LogWarning(
            "display scene data has unsupported format version {FormatVersion}",
            document.FormatVersion);
        return [];
    }

    public bool TrySave(IReadOnlyList<DisplaySnapshot> snapshots)
        => _sceneStore.TryWrite(
            StoreSection,
            new DisplayDocument
            {
                Displays = snapshots,
            });
}
