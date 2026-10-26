using Intoner.Services.Serialization;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intoner.Services.Storage;

internal sealed record SceneStoreDocument
{
    public const int CurrentFormatVersion = 1;

    [JsonRequired]
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    [JsonRequired]
    public Dictionary<string, JsonElement> Sections { get; init; } = new(StringComparer.Ordinal);
}

/// <summary> persists independently versioned scene domain sections in one document </summary>
internal interface ISceneStore
{
    /// <summary> true when the scene document could not be loaded safely </summary>
    bool IsReadOnly { get; }

    /// <summary> reads one scene domain section for its owner to deserialize </summary>
    bool TryRead(string section, out JsonElement value);

    /// <summary> atomically replaces one scene domain section while preserving all others </summary>
    bool TryWrite<T>(string section, T value);
}

internal sealed class SceneStore : ISceneStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly ILogger<SceneStore> _logger;
    private readonly IPluginFileSystem _fileSystem;
    private readonly string _path;
    private readonly Lock _stateLock = new();

    private IReadOnlyDictionary<string, JsonElement> _sections;
    private bool _canWrite = true;

    public SceneStore(
        ILogger<SceneStore> logger,
        IPluginFileSystem fileSystem,
        IPluginStoragePaths storagePaths)
    {
        _logger = logger;
        _fileSystem = fileSystem;
        _path = storagePaths.SceneStorePath;
        _sections = LoadSections();
    }

    public bool IsReadOnly => !_canWrite;

    public bool TryRead(string section, out JsonElement value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        lock (_stateLock)
        {
            return _sections.TryGetValue(section, out value);
        }
    }

    public bool TryWrite<T>(string section, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            JsonElement element = JsonSerializer.SerializeToElement(value, JsonOptions);
            lock (_stateLock)
            {
                if (!_canWrite)
                {
                    return false;
                }

                var sections = new Dictionary<string, JsonElement>(_sections, StringComparer.Ordinal)
                {
                    [section] = element,
                };
                string json = JsonSerializer.Serialize(
                    new SceneStoreDocument
                    {
                        Sections = sections,
                    },
                    JsonOptions);
                JsonDocumentUtility.WriteFile(_fileSystem, _path, json);
                _sections = sections;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to write scene store section {Section}", section);
            return false;
        }
    }

    private IReadOnlyDictionary<string, JsonElement> LoadSections()
    {
        if (!_fileSystem.FileExists(_path))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        _canWrite = false;
        try
        {
            SceneStoreDocument? document = JsonSerializer.Deserialize<SceneStoreDocument>(
                JsonDocumentUtility.ReadFile(_fileSystem, _path),
                JsonOptions);
            if (document is null)
            {
                _logger.LogWarning("scene store document was empty");
                return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            }

            if (document.FormatVersion != SceneStoreDocument.CurrentFormatVersion)
            {
                _logger.LogWarning(
                    "scene store has unsupported format version {FormatVersion}",
                    document.FormatVersion);
                return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            }

            Dictionary<string, JsonElement> sections = new(document.Sections, StringComparer.Ordinal);
            _canWrite = true;
            return sections;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load scene store");
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options =
            JsonSerializerOptionsUtility.CreateStrictIndented(JsonNamingPolicy.CamelCase);
        options.IncludeFields = true;
        return options;
    }
}
