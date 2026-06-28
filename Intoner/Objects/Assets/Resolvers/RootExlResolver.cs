using Lumina.Data.Files.Excel;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Assets;

internal sealed class RootExlDatasetIndex
{
    private readonly IReadOnlyDictionary<string, uint> _datasetIds;

    public RootExlDatasetIndex(IReadOnlyDictionary<string, uint> datasetIds)
    {
        _datasetIds = datasetIds;
    }

    public int DatasetCount
        => _datasetIds.Count;

    public bool TryGetDatasetId(string datasetName, out uint datasetId)
        => _datasetIds.TryGetValue(datasetName, out datasetId);
}

internal sealed class RootExlResolver
{
    private const string RootExlPath = "exd/root.exl";

    private readonly ILogger<RootExlResolver> _logger;
    private readonly IObjectAssetGameData _gameData;
    private readonly Lock _loadLock = new();

    private RootExlDatasetIndex? _datasetIndex;

    public RootExlResolver(ILogger<RootExlResolver> logger, IObjectAssetGameData gameData)
    {
        _logger = logger;
        _gameData = gameData;
    }

    public RootExlDatasetIndex? Load()
    {
        lock (_loadLock)
        {
            if (_datasetIndex is not null)
            {
                return _datasetIndex;
            }

            ExcelListFile? rootExl = _gameData.GetFile<ExcelListFile>(RootExlPath);
            if (rootExl is null)
            {
                _logger.LogWarning("failed to load {RootExlPath}", RootExlPath);
                return null;
            }

            Dictionary<string, uint> datasetIds = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string name, int id) in rootExl.ExdMap)
            {
                if (string.IsNullOrWhiteSpace(name) || id < 0)
                {
                    continue;
                }

                datasetIds[name] = (uint)id;
            }

            _datasetIndex = new RootExlDatasetIndex(datasetIds);

            _logger.LogInformation("loaded root.exl dataset map with {DatasetCount} entries", _datasetIndex.DatasetCount);
            return _datasetIndex;
        }
    }
}

