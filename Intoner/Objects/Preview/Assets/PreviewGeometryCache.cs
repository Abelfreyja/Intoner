using Intoner.Objects.Assets;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Preview.Assets;

internal sealed class PreviewGeometryCache(PreviewMaterialResolver materialResolver) : IDisposable
{
    private const int MaxSharedGeometryCount = 24;
    private const long MaxSharedGeometryBytes = 192L * 1024 * 1024;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, PreviewAssetState.GeometryEntry> _entries = new(StringComparer.Ordinal);

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct Snapshot(int Count, long ByteCount, int ActiveUseCount);

    public bool TryGet(string cacheKey, out PreviewAssetState.GeometryEntry? entry)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(cacheKey, out entry);
        }
    }

    public PreviewAssetState.GeometryEntry GetOrCreate(
        string cacheKey,
        ModelPreviewGeometryReader.PreviewGeometry geometry)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(cacheKey, out PreviewAssetState.GeometryEntry? existingEntry))
            {
                return existingEntry;
            }

            PreviewAssetState.GeometryEntry entry = new(cacheKey, geometry);
            _entries.Add(cacheKey, entry);
            return entry;
        }
    }

    public static void MarkAccess(PreviewAssetState.GeometryEntry entry, PreviewRender.Mode mode, long now)
    {
        lock (entry.SyncRoot)
        {
            if (mode == PreviewRender.Mode.Thumbnail)
            {
                entry.LastThumbnailAccessAtMs = now;
            }
            else
            {
                entry.LastDetailAccessAtMs = now;
            }
        }
    }

    public static void SyncAccess(PreviewAssetState.GeometryEntry entry, long lastThumbnailAccessAtMs, long lastDetailAccessAtMs)
    {
        if (lastThumbnailAccessAtMs > 0)
        {
            MarkAccess(entry, PreviewRender.Mode.Thumbnail, lastThumbnailAccessAtMs);
        }

        if (lastDetailAccessAtMs > 0)
        {
            MarkAccess(entry, PreviewRender.Mode.Detail, lastDetailAccessAtMs);
        }
    }

    public void BeginUse(PreviewAssetState.GeometryEntry entry, PreviewRender.Mode mode, long now)
    {
        lock (entry.SyncRoot)
        {
            entry.ActiveUseCount++;
            if (mode == PreviewRender.Mode.Thumbnail)
            {
                entry.LastThumbnailAccessAtMs = now;
            }
            else
            {
                entry.LastDetailAccessAtMs = now;
            }

            materialResolver.EnsureTextures(entry.Geometry);
        }
    }

    public static void EndUse(PreviewAssetState.GeometryEntry entry)
    {
        lock (entry.SyncRoot)
        {
            if (entry.ActiveUseCount > 0)
            {
                entry.ActiveUseCount--;
            }
        }
    }

    public Snapshot GetSnapshot()
    {
        int count;
        long byteCount = 0;
        int activeUseCount = 0;
        lock (_lock)
        {
            count = _entries.Count;
            foreach (PreviewAssetState.GeometryEntry entry in _entries.Values)
            {
                lock (entry.SyncRoot)
                {
                    byteCount += entry.GeometryByteCount;
                    if (entry.ActiveUseCount > 0)
                    {
                        activeUseCount++;
                    }
                }
            }
        }

        return new Snapshot(count, byteCount, activeUseCount);
    }

    public void Trim(long now, List<PreviewAssetState.GeometryEntry> evictedEntries)
    {
        TrimMaterialTextures(now);
        TrimEntries(now, evictedEntries);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (PreviewAssetState.GeometryEntry entry in _entries.Values)
            {
                lock (entry.SyncRoot)
                {
                    PreviewMaterialResolver.ReleaseTextures(entry.Geometry);
                    entry.ActiveUseCount = 0;
                }
            }

            _entries.Clear();
        }
    }

    private void TrimMaterialTextures(long now)
    {
        List<PreviewAssetState.GeometryEntry> entries;
        lock (_lock)
        {
            entries = [.. _entries.Values];
        }

        foreach (PreviewAssetState.GeometryEntry entry in entries)
        {
            lock (entry.SyncRoot)
            {
                if (entry.ActiveUseCount > 0
                 || !IsExpired(entry.LastThumbnailAccessAtMs, now, PreviewCachePolicy.ThumbnailTextureRetention)
                 || !IsExpired(entry.LastDetailAccessAtMs, now, PreviewCachePolicy.DetailGeometryRetention))
                {
                    continue;
                }

                PreviewMaterialResolver.ReleaseTextures(entry.Geometry);
            }
        }
    }

    private void TrimEntries(long now, List<PreviewAssetState.GeometryEntry> evictedEntries)
    {
        List<PreviewAssetState.GeometryEntry> entries;
        long totalGeometryBytes = 0;
        lock (_lock)
        {
            entries = [.. _entries.Values];
            foreach (PreviewAssetState.GeometryEntry entry in entries)
            {
                totalGeometryBytes += entry.GeometryByteCount;
            }
        }

        if (entries.Count <= MaxSharedGeometryCount
         && totalGeometryBytes <= MaxSharedGeometryBytes)
        {
            return;
        }

        List<PreviewAssetState.GeometryEntry> candidates = GetTrimCandidates(entries, now);
        foreach (PreviewAssetState.GeometryEntry candidate in candidates)
        {
            lock (_lock)
            {
                if (_entries.Count <= MaxSharedGeometryCount
                 && totalGeometryBytes <= MaxSharedGeometryBytes)
                {
                    break;
                }

                if (!_entries.TryGetValue(candidate.CacheKey, out PreviewAssetState.GeometryEntry? cachedEntry)
                 || !ReferenceEquals(candidate, cachedEntry))
                {
                    continue;
                }

                lock (candidate.SyncRoot)
                {
                    if (!CanTrim(candidate, now))
                    {
                        continue;
                    }

                    PreviewMaterialResolver.ReleaseTextures(candidate.Geometry);
                }

                if (_entries.Remove(candidate.CacheKey))
                {
                    totalGeometryBytes -= candidate.GeometryByteCount;
                    evictedEntries.Add(candidate);
                }
            }
        }
    }

    private static List<PreviewAssetState.GeometryEntry> GetTrimCandidates(
        IReadOnlyList<PreviewAssetState.GeometryEntry> entries,
        long now)
    {
        List<TrimCandidate> candidates = [];
        foreach (PreviewAssetState.GeometryEntry entry in entries)
        {
            lock (entry.SyncRoot)
            {
                if (CanTrim(entry, now))
                {
                    candidates.Add(new TrimCandidate(
                        entry,
                        Math.Max(entry.LastThumbnailAccessAtMs, entry.LastDetailAccessAtMs)));
                }
            }
        }

        candidates.Sort(static (left, right) => left.LastAccessAtMs.CompareTo(right.LastAccessAtMs));
        List<PreviewAssetState.GeometryEntry> entriesToTrim = [];
        foreach (TrimCandidate candidate in candidates)
        {
            entriesToTrim.Add(candidate.Entry);
        }

        return entriesToTrim;
    }

    private static bool CanTrim(PreviewAssetState.GeometryEntry entry, long now)
        => entry.ActiveUseCount == 0
            && IsExpired(entry.LastThumbnailAccessAtMs, now, PreviewCachePolicy.ThumbnailWorkRetention)
            && IsExpired(entry.LastDetailAccessAtMs, now, PreviewCachePolicy.DetailGeometryRetention);

    private static bool IsExpired(long lastAccessAtMs, long now, TimeSpan retention)
    {
        if (lastAccessAtMs <= 0)
        {
            return true;
        }

        return now - lastAccessAtMs >= retention.TotalMilliseconds;
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct TrimCandidate(PreviewAssetState.GeometryEntry Entry, long LastAccessAtMs);
}
