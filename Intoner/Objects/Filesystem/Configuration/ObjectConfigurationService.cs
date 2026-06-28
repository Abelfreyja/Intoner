using Intoner.Logging;
using Intoner.Objects.Filesystem.Storage;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Intoner.Objects.Filesystem.Configuration;

/// <summary> loads and saves config </summary>
internal interface IObjectConfigurationService
{
    /// <summary> gets the current config </summary>
    ObjectConfiguration Current { get; }

    /// <summary> saves the current config </summary>
    void Save();

    /// <summary> updates and saves the current config under the service lock </summary>
    /// <param name="update"> mutation to apply to the current config </param>
    void Update(Action<ObjectConfiguration> update);
}

internal sealed class ObjectConfigurationService : IObjectConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = ObjectJsonSerializerOptionsUtility.CreateStrictIndented(JsonNamingPolicy.CamelCase);

    private readonly ILogger<ObjectConfigurationService> _logger;
    private readonly IObjectStoragePathService           _pathService;
    private readonly IObjectFileSystem                   _fileSystem;
    private readonly IIntonerLogLevelService             _logLevelService;
    private readonly Lock                                _lock = new();

    private ObjectConfiguration _current;

    public ObjectConfigurationService(
        ILogger<ObjectConfigurationService> logger,
        IObjectStoragePathService pathService,
        IObjectFileSystem fileSystem,
        IIntonerLogLevelService logLevelService)
    {
        _logger          = logger;
        _pathService     = pathService;
        _fileSystem      = fileSystem;
        _logLevelService = logLevelService;
        _current         = LoadOrCreate();
        ApplyRuntimeSettings(_current);
    }

    public ObjectConfiguration Current
    {
        get
        {
            lock (_lock)
            {
                return _current.Copy();
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            SaveCurrentLocked();
        }
    }

    public void Update(Action<ObjectConfiguration> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_lock)
        {
            ObjectConfiguration next = _current.Copy();
            update(next);
            ObjectConfigurationNormalizer.Normalize(next);
            _current = next;
            ApplyRuntimeSettings(_current);
            TryWriteConfiguration(_current);
        }
    }

    private ObjectConfiguration LoadOrCreate()
    {
        if (TryLoadConfiguration(out ObjectConfiguration configuration))
        {
            return configuration;
        }

        ObjectConfiguration fallback = ObjectConfiguration.CreateDefault();
        TryWriteConfiguration(fallback);
        return fallback;
    }

    private bool TryLoadConfiguration(out ObjectConfiguration configuration)
    {
        configuration = null!;
        if (!_fileSystem.FileExists(_pathService.ObjectConfigurationPath))
        {
            return false;
        }

        try
        {
            ObjectConfiguration? loaded = JsonSerializer.Deserialize<ObjectConfiguration>(
                _fileSystem.ReadAllText(_pathService.ObjectConfigurationPath),
                JsonOptions);
            if (loaded is null)
            {
                _logger.LogWarning("failed to load object config: file was empty");
                return false;
            }

            ObjectConfigurationNormalizer.Normalize(loaded);
            configuration = loaded;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load object config");
            return false;
        }
    }

    private void SaveCurrentLocked()
    {
        ObjectConfigurationNormalizer.Normalize(_current);
        ApplyRuntimeSettings(_current);
        TryWriteConfiguration(_current);
    }

    private void ApplyRuntimeSettings(ObjectConfiguration configuration)
        => _logLevelService.SetDalamudMinimumLevel(configuration.Logging.DalamudMinimumLevel);

    private void TryWriteConfiguration(ObjectConfiguration configuration)
    {
        try
        {
            string json = JsonSerializer.Serialize(configuration, JsonOptions);
            _fileSystem.WriteAllTextAtomic(_pathService.ObjectConfigurationPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to save object config");
        }
    }
}

