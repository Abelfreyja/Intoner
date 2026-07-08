using Dalamud.Plugin.Services;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Intoner.Objects.Assets;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal sealed class ObjectSelectionGeometry
{
    public ObjectSelectionGeometry(Vector3[] positions, int[] indices)
    {
        Positions = positions;
        Indices = indices;
        BoundsCenter = ComputeBoundsCenter(positions);
        BoundsRadius = ComputeBoundsRadius(positions, BoundsCenter);
    }

    public Vector3[] Positions { get; }
    public int[] Indices { get; }
    public Vector3 BoundsCenter { get; }
    public float BoundsRadius { get; }

    private static Vector3 ComputeBoundsCenter(Vector3[] positions)
    {
        if (positions.Length == 0)
        {
            return Vector3.Zero;
        }

        var min = positions[0];
        var max = positions[0];
        for (var index = 1; index < positions.Length; index++)
        {
            min = Vector3.Min(min, positions[index]);
            max = Vector3.Max(max, positions[index]);
        }

        return (min + max) * 0.5f;
    }

    private static float ComputeBoundsRadius(Vector3[] positions, Vector3 center)
    {
        var radiusSquared = 0f;
        foreach (var position in positions)
        {
            radiusSquared = MathF.Max(radiusSquared, Vector3.DistanceSquared(center, position));
        }

        return MathF.Sqrt(radiusSquared);
    }
}

internal sealed class ObjectSelectionGeometryCache
{
    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(5);

    private sealed class CacheEntry
    {
        public ObjectSelectionGeometry? Geometry { get; init; }
        public DateTime LastTouchedUtc { get; set; }
    }

    private readonly ILogger<ObjectSelectionGeometryCache> _logger;
    private readonly IDataManager _gameData;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ObjectSelectionGeometryCache(
        ILogger<ObjectSelectionGeometryCache> logger,
        IDataManager gameData)
    {
        _logger = logger;
        _gameData = gameData;
    }

    public bool TryGetGeometry(string modelPath, out ObjectSelectionGeometry geometry)
    {
        geometry = null!;
        if (!ObjectResourcePathUtility.TryNormalizeTrackedPath(modelPath, out var normalizedPath))
        {
            return false;
        }

        if (TryGetCachedEntry(normalizedPath, out var cachedEntry))
        {
            geometry = cachedEntry.Geometry!;
            return geometry != null;
        }

        var loadedEntry = LoadGeometryEntry(normalizedPath, DateTime.UtcNow);
        StoreEntry(normalizedPath, loadedEntry);
        geometry = loadedEntry.Geometry!;
        return geometry != null;
    }

    public void Touch(string modelPath)
    {
        if (!ObjectResourcePathUtility.TryNormalizeTrackedPath(modelPath, out var normalizedPath))
        {
            return;
        }

        lock (_cacheLock)
        {
            var now = DateTime.UtcNow;
            TrimExpiredUnsafe(now);
            if (_cache.TryGetValue(normalizedPath, out var entry))
            {
                entry.LastTouchedUtc = now;
            }
        }
    }

    private bool TryGetCachedEntry(string normalizedPath, out CacheEntry cacheEntry)
    {
        var now = DateTime.UtcNow;
        lock (_cacheLock)
        {
            TrimExpiredUnsafe(now);
            if (_cache.TryGetValue(normalizedPath, out var cachedEntry))
            {
                cacheEntry = cachedEntry;
                return true;
            }
        }

        cacheEntry = null!;
        return false;
    }

    private void StoreEntry(string normalizedPath, CacheEntry cacheEntry)
    {
        lock (_cacheLock)
        {
            _cache[normalizedPath] = cacheEntry;
        }
    }

    private CacheEntry LoadGeometryEntry(string normalizedPath, DateTime touchedUtc)
    {
        try
        {
            if (ObjectLocalFilePathUtility.IsLocalFilePath(normalizedPath))
            {
                return LoadLocalGeometryEntry(normalizedPath, touchedUtc);
            }

            var file = _gameData.GetFile(normalizedPath);
            if (file is null)
            {
                _logger.LogDebug("object selection geometry missing file {ModelPath}", normalizedPath);
                return new CacheEntry
                {
                    LastTouchedUtc = touchedUtc,
                };
            }

            if (!ModelPreviewGeometryReader.TryLoad(file.Data, out var previewGeometry, out var reason))
            {
                return CreateDecodeFailedEntry(normalizedPath, reason, touchedUtc);
            }

            return CreateGeometryEntry(previewGeometry, touchedUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "object selection geometry load failed for {ModelPath}", normalizedPath);
            return new CacheEntry
            {
                LastTouchedUtc = touchedUtc,
            };
        }
    }

    private CacheEntry LoadLocalGeometryEntry(string normalizedPath, DateTime touchedUtc)
    {
        string fileSystemPath = ObjectLocalFilePathUtility.ToFileSystemPath(normalizedPath);
        if (!File.Exists(fileSystemPath))
        {
            _logger.LogDebug("object selection geometry missing local file {ModelPath}", normalizedPath);
            return new CacheEntry
            {
                LastTouchedUtc = touchedUtc,
            };
        }

        var data = File.ReadAllBytes(fileSystemPath);
        return ModelPreviewGeometryReader.TryLoad(data, out var previewGeometry, out var reason)
            ? CreateGeometryEntry(previewGeometry, touchedUtc)
            : CreateDecodeFailedEntry(normalizedPath, reason, touchedUtc);
    }

    private CacheEntry CreateDecodeFailedEntry(string normalizedPath, string? reason, DateTime touchedUtc)
    {
        _logger.LogDebug(
            "object selection geometry decode failed for {ModelPath}: {Reason}",
            normalizedPath,
            reason ?? "unknown reason");
        return new CacheEntry
        {
            LastTouchedUtc = touchedUtc,
        };
    }

    private static CacheEntry CreateGeometryEntry(ModelPreviewGeometryReader.PreviewGeometry previewGeometry, DateTime touchedUtc)
        => new()
        {
            Geometry = new ObjectSelectionGeometry(
                previewGeometry.Positions,
                previewGeometry.Indices),
            LastTouchedUtc = touchedUtc,
        };

    private void TrimExpiredUnsafe(DateTime nowUtc)
    {
        if (_cache.Count == 0)
        {
            return;
        }

        foreach (var (path, entry) in _cache.ToArray())
        {
            if (nowUtc - entry.LastTouchedUtc >= EntryTtl)
            {
                _cache.Remove(path);
            }
        }
    }
}
