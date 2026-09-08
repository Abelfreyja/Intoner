using Intoner.Services.Serialization;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Intoner.Services.Storage;

internal sealed record SceneStoreDocument
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public Dictionary<string, JsonElement> Sections { get; init; } = new(StringComparer.Ordinal);
}

/// <summary> persists independently versioned scene domain sections in one document </summary>
internal interface ISceneStore
{
    /// <summary> reads and deserializes one scene domain section </summary>
    bool TryRead<T>(string section, [NotNullWhen(true)] out T? value);

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

    public bool TryRead<T>(string section, [NotNullWhen(true)] out T? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);

        JsonElement element;
        lock (_stateLock)
        {
            if (!_sections.TryGetValue(section, out element))
            {
                value = default;
                return false;
            }
        }

        try
        {
            value = element.Deserialize<T>(JsonOptions);
            return value is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to read scene store section {Section}", section);
            value = default;
            return false;
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
                _fileSystem.WriteAllTextAtomic(_path, json);
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

        try
        {
            SceneStoreDocument? document = JsonSerializer.Deserialize<SceneStoreDocument>(
                _fileSystem.ReadAllText(_path),
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

            return new Dictionary<string, JsonElement>(document.Sections, StringComparer.Ordinal);
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
