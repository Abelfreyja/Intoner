using Intoner.Logging;
using Intoner.Services.Serialization;
using Intoner.Services.Storage;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary> applies and saves a config change only when the callback returns true </summary>
    /// <param name="update"> the change to apply; return false to leave the config and file unchanged, without notifying listeners </param>
    /// <returns> true if the change was saved and committed; false if the callback rejects it or saving fails </returns>
    bool TryUpdateIf(Func<IntonerConfiguration, bool> update);
}

internal sealed class IntonerConfigurationService : IIntonerConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptionsUtility.CreateLenientIndented(JsonNamingPolicy.CamelCase);

    private readonly ILogger<IntonerConfigurationService> _logger;
    private readonly IPluginStoragePaths                  _pathService;
    private readonly IPluginFileSystem                    _fileSystem;
    private readonly IIntonerLogLevelService              _logLevelService;
    private readonly Lock                                 _lock = new();

    private IntonerConfiguration _current;
    private JsonObject? _sourceDocument;
    private bool _canWriteConfiguration = true;
    private (string Path, string Contents)? _pendingBackup;

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
        return TryUpdateIf(configuration =>
        {
            update(configuration);
            return true;
        });
    }

    public bool TryUpdateIf(Func<IntonerConfiguration, bool> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_lock)
        {
            IntonerConfiguration next = _current.Copy();

            if (!update(next))
            {
                return false;
            }

            IntonerConfigurationNormalizer.Normalize(next);
            next = next.Copy();

            if (!TryWriteConfiguration(next))
            {
                return false;
            }

            _current = next;
            ApplyRuntimeSettings(_current);
        }

        CommittedChangeNotification.Publish(ConfigurationChanged, _logger, "configuration change");
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

        string? original = null;
        try
        {
            original = JsonDocumentUtility.ReadFile(_fileSystem, _pathService.ConfigurationPath);
            JsonObject root = IntonerConfigurationReader.Parse(original, JsonOptions);
            bool migrated = UpgradeConfiguration(root);
            IntonerConfiguration loaded = IntonerConfigurationReader.Read(root, JsonOptions, out bool repaired);
            _sourceDocument = root;
            if (migrated || repaired)
            {
                string backupPath = _pathService.ConfigurationPath + (migrated ? ".v2.bak" : ".repair.bak");
                if (!migrated || !_fileSystem.FileExists(backupPath))
                {
                    _pendingBackup = (backupPath, original);
                }

                if (repaired)
                {
                    DiagnosticLog.Write(() => _logger.LogWarning("repaired invalid or missing Intoner config values; valid settings were preserved"));
                }
                _ = TryWriteConfiguration(loaded);
            }

            configuration = loaded;
            return true;
        }
        catch (JsonException ex) when (original is not null)
        {
            _pendingBackup = (_pathService.ConfigurationPath + ".invalid.bak", original);
            DiagnosticLog.Write(() => _logger.LogWarning(ex, "Intoner config is not readable JSON; restoring defaults after backing up the original"));
            return false;
        }
        catch (Exception ex)
        {
            _canWriteConfiguration = false;
            DiagnosticLog.Write(() => _logger.LogWarning(ex, "failed to load Intoner config; the original is unchanged and configuration writes are disabled"));
            return false;
        }
    }

    // only version 2 has the singular default layout contract
    private static bool UpgradeConfiguration(JsonObject root)
    {
        if (root["version"] is not JsonValue versionNode || !versionNode.TryGetValue<int>(out int version))
        {
            return false;
        }

        if (version != 2 && version != IntonerConfiguration.CurrentVersion)
        {
            throw new InvalidDataException("unsupported Intoner configuration version");
        }

        if (version != 2)
        {
            return false;
        }

        if (root["layouts"] is not JsonObject layouts)
        {
            layouts = new JsonObject();
            root["layouts"] = layouts;
        }

        JsonElement releasedLayouts = JsonSerializer.SerializeToElement(layouts);
        Guid? id = releasedLayouts.TryGetPropertyIgnoreCase("defaultLayoutId", out JsonElement value)
            && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out Guid parsed) && parsed != Guid.Empty ? parsed : null;
        layouts["enabledLayoutIds"] = id.HasValue ? new JsonArray(JsonValue.Create(id.Value)) : new JsonArray();
        layouts["creationLayoutId"] = id.HasValue ? JsonValue.Create(id.Value) : null;
        string? legacyKey = layouts.FirstOrDefault(static entry => entry.Key.Equals("defaultLayoutId", StringComparison.OrdinalIgnoreCase)).Key;

        if (legacyKey is not null)
        {
            layouts.Remove(legacyKey);
        }

        root["version"] = IntonerConfiguration.CurrentVersion;
        return true;
    }

    private void ApplyRuntimeSettings(IntonerConfiguration configuration)
        => _logLevelService.SetDalamudMinimumLevel(configuration.Logging.DalamudMinimumLevel);

    private bool TryWriteConfiguration(IntonerConfiguration configuration)
    {
        if (!_canWriteConfiguration)
        {
            return false;
        }

        try
        {
            if (_pendingBackup is { } backup)
            {
                _fileSystem.WriteAllTextAtomic(backup.Path, backup.Contents);
                _pendingBackup = null;
            }

            JsonObject document = IntonerConfigurationReader.CreateDocument(configuration, _sourceDocument, JsonOptions);
            JsonDocumentUtility.WriteFile(_fileSystem, _pathService.ConfigurationPath, document.ToJsonString(JsonOptions));
            _sourceDocument = document;
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(() => _logger.LogWarning(ex, "failed to save Intoner config"));
            return false;
        }
    }
}
