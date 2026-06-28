using ClientFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Intoner.Objects.Assets.Cache;

/// <summary> resolves the current game version used to invalidate asset cache snapshots </summary>
internal interface IObjectAssetGameVersionService
{
    /// <summary> gets the current game version when it can be resolved </summary>
    /// <returns>the current game version, or an empty string when unavailable</returns>
    string GetCurrentGameVersion();
}

internal sealed class ObjectAssetGameVersionService : IObjectAssetGameVersionService
{
    private readonly ILogger<ObjectAssetGameVersionService> _logger;
    private readonly Lock _lock = new();
    private string? _gameVersion;

    public ObjectAssetGameVersionService(ILogger<ObjectAssetGameVersionService> logger)
    {
        _logger = logger;
    }

    public string GetCurrentGameVersion()
    {
        lock (_lock)
        {
            if (_gameVersion is not null)
            {
                return _gameVersion;
            }

            try
            {
                string frameworkGameVersion = ResolveFrameworkGameVersion();
                if (!string.IsNullOrWhiteSpace(frameworkGameVersion))
                {
                    _gameVersion = frameworkGameVersion;
                    return _gameVersion;
                }

                string? executablePath = Process.GetCurrentProcess().MainModule?.FileName;
                string gameDirectory = executablePath is null
                    ? string.Empty
                    : Path.GetDirectoryName(executablePath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gameDirectory))
                {
                    _gameVersion = string.Empty;
                    return _gameVersion;
                }

                string versionPath = Path.Combine(gameDirectory, "ffxivgame.ver");
                _gameVersion = File.Exists(versionPath)
                    ? File.ReadAllText(versionPath).Trim()
                    : string.Empty;
                return _gameVersion;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "failed to resolve current object asset game version");
                _gameVersion = string.Empty;
                return _gameVersion;
            }
        }
    }

    private static unsafe string ResolveFrameworkGameVersion()
    {
        ClientFramework* framework = ClientFramework.Instance();
        return framework == null
            ? string.Empty
            : framework->GameVersionString.Trim();
    }
}

