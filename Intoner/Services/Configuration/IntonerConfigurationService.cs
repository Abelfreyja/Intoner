using Intoner.Logging;
using Intoner.Services.Serialization;
using Intoner.Services.Storage;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Intoner.Services.Configuration;

/// <summary> loads and saves config </summary>
internal interface IIntonerConfigurationService
{
    /// <summary> gets the current config </summary>
    IntonerConfiguration Current { get; }

    /// <summary> raised after an update commits the current config </summary>
    /// <remarks> invoked outside the service lock on the update caller's thread </remarks>
    event Action? ConfigurationChanged;

    /// <summary> saves the current config </summary>
    /// <returns>true when the config was written</returns>
    bool TrySave();

    /// <summary> updates and saves the current config under the service lock </summary>
    /// <param name="update"> mutation to apply to the current config </param>
    /// <returns>true when the updated config was written and committed</returns>
    bool TryUpdate(Action<IntonerConfiguration> update);
}

internal sealed class IntonerConfigurationService : IIntonerConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptionsUtility.CreateLenientIndented(JsonNamingPolicy.CamelCase);

    private readonly ILogger<IntonerConfigurationService> _logger;
    private readonly IPluginStoragePaths                  _pathService;
    private readonly IPluginFileSystem                    _fileSystem;
    private readonly IIntonerLogLevelService             _logLevelService;
    private readonly Lock                                _lock = new();

    private IntonerConfiguration _current;

    public IntonerConfigurationService(
        ILogger<IntonerConfigurationService> logger,
        IPluginStoragePaths pathService,
        IPluginFileSystem fileSystem,
        IIntonerLogLevelService logLevelService)
    {
        _logger          = logger;
        _pathService     = pathService;
        _fileSystem      = fileSystem;
        _logLevelService = logLevelService;
        _current         = LoadOrCreate();
        ApplyRuntimeSettings(_current);
    }

    public IntonerConfiguration Current
    {
        get
        {
            lock (_lock)
            {
                return _current.Copy();
            }
        }
    }

    public event Action? ConfigurationChanged;

    public bool TrySave()
    {
        lock (_lock)
        {
            IntonerConfiguration next = _current.Copy();
            IntonerConfigurationNormalizer.Normalize(next);
            if (!TryWriteConfiguration(next))
            {
                return false;
            }

            _current = next;
            ApplyRuntimeSettings(_current);
            return true;
        }
    }

    public bool TryUpdate(Action<IntonerConfiguration> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_lock)
        {
            IntonerConfiguration next = _current.Copy();
            update(next);
            IntonerConfigurationNormalizer.Normalize(next);
            if (!TryWriteConfiguration(next))
            {
                return false;
            }

            _current = next;
            ApplyRuntimeSettings(_current);
        }

        ConfigurationChanged?.Invoke();
        return true;
    }

    private IntonerConfiguration LoadOrCreate()
    {
        if (TryLoadConfiguration(out IntonerConfiguration configuration))
        {
            return configuration;
        }

        IntonerConfiguration fallback = IntonerConfiguration.CreateDefault();
        _ = TryWriteConfiguration(fallback);
        return fallback;
    }

    private bool TryLoadConfiguration(out IntonerConfiguration configuration)
    {
        configuration = null!;
        if (!_fileSystem.FileExists(_pathService.ConfigurationPath))
        {
            return false;
        }

        try
        {
            IntonerConfiguration? loaded = JsonSerializer.Deserialize<IntonerConfiguration>(
                _fileSystem.ReadAllText(_pathService.ConfigurationPath),
                JsonOptions);
            if (loaded is null)
            {
                _logger.LogWarning("failed to load Intoner config: file was empty");
                return false;
            }

            IntonerConfigurationNormalizer.Normalize(loaded);
            configuration = loaded;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load Intoner config");
            return false;
        }
    }

    private void ApplyRuntimeSettings(IntonerConfiguration configuration)
        => _logLevelService.SetDalamudMinimumLevel(configuration.Logging.DalamudMinimumLevel);

    private bool TryWriteConfiguration(IntonerConfiguration configuration)
    {
        try
        {
            string json = JsonSerializer.Serialize(configuration, JsonOptions);
            _fileSystem.WriteAllTextAtomic(_pathService.ConfigurationPath, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to save Intoner config");
            return false;
        }
    }
}
