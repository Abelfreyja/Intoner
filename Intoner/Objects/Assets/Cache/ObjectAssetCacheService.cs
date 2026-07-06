using Intoner.Objects.Filesystem.Storage;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Intoner.Objects.Assets.Cache;

/// <summary> loads and saves object asset cache snapshots owned by the objects subsystem </summary>
internal interface IObjectAssetCacheService
{
    /// <summary> loads the current cache manifest when one is available </summary>
    /// <returns>the loaded manifest, or null when no cache is available</returns>
    ObjectAssetCacheManifest? TryLoadManifest();

    /// <summary> loads selected object asset cache sections from the current snapshot </summary>
    /// <param name="manifest">the cache manifest to load from</param>
    /// <param name="sections">the requested cache sections</param>
    /// <returns>the loaded snapshot data and loaded section flags</returns>
    ObjectAssetCacheLoadResult Load(ObjectAssetCacheManifest manifest, ObjectAssetCacheSectionSet sections);

    /// <summary> saves selected object asset cache sections </summary>
    /// <param name="request">the save request to persist</param>
    void Save(ObjectAssetCacheSaveRequest request);
}

internal sealed class ObjectAssetCacheService : IObjectAssetCacheService
{
    internal const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ILogger<ObjectAssetCacheService> _logger;
    private readonly IObjectStoragePathService _pathService;
    private readonly IObjectFileSystem _fileSystem;
    private readonly ObjectAssetCacheSerializer _serializer;
    private readonly ObjectAssetCachePayloadReader _payloadReader;

    public ObjectAssetCacheService(
        ILogger<ObjectAssetCacheService> logger,
        IObjectStoragePathService pathService,
        IObjectFileSystem fileSystem,
        ObjectAssetCacheSerializer serializer,
        ObjectAssetCachePayloadReader payloadReader)
    {
        _logger = logger;
        _pathService = pathService;
        _fileSystem = fileSystem;
        _serializer = serializer;
        _payloadReader = payloadReader;
    }

    public ObjectAssetCacheManifest? TryLoadManifest()
    {
        if (!_fileSystem.FileExists(_pathService.AssetCacheManifestPath)
         || !_fileSystem.FileExists(_pathService.AssetCachePayloadPath))
        {
            return null;
        }

        try
        {
            ObjectAssetCacheManifest? manifest = JsonSerializer.Deserialize<ObjectAssetCacheManifest>(
                _fileSystem.ReadAllText(_pathService.AssetCacheManifestPath),
                JsonOptions);
            if (manifest is null)
            {
                return null;
            }

            if (!string.Equals(manifest.CacheFileName, ObjectAssetCacheSerializer.CacheFileName, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "ignoring object asset cache with unexpected cache file name {CacheFileName}",
                    manifest.CacheFileName);
                return null;
            }

            if (manifest.SchemaVersion != SchemaVersion)
            {
                _logger.LogInformation(
                    "ignoring object asset cache with schema version {SchemaVersion}",
                    manifest.SchemaVersion);
                return null;
            }

            if (manifest.FormatVersion != ObjectAssetCacheSerializer.FormatVersion)
            {
                _logger.LogInformation(
                    "ignoring object asset cache with format version {FormatVersion}",
                    manifest.FormatVersion);
                return null;
            }

            if (!manifest.TryBuildSectionMap(out _, out string? error))
            {
                _logger.LogInformation(
                    "ignoring object asset cache with invalid section metadata: {Error}",
                    error);
                return null;
            }

            return manifest;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load object asset cache manifest");
            return null;
        }
    }

    public ObjectAssetCacheLoadResult Load(ObjectAssetCacheManifest manifest, ObjectAssetCacheSectionSet sections)
    {
        if (sections == ObjectAssetCacheSectionSet.None
         || !_fileSystem.FileExists(_pathService.AssetCachePayloadPath))
        {
            return ObjectAssetCacheLoadResult.Empty;
        }

        try
        {
            if (manifest.PayloadLength > 0
             && _fileSystem.GetFileLength(_pathService.AssetCachePayloadPath) != manifest.PayloadLength)
            {
                _logger.LogInformation("ignoring object asset cache with unexpected payload length");
                return ObjectAssetCacheLoadResult.Empty;
            }

            if (!_payloadReader.TryReadSectionPayloads(manifest, sections, out Dictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload>? sectionPayloads))
            {
                return ObjectAssetCacheLoadResult.Empty;
            }

            Dictionary<ObjectAssetCacheSectionKind, ReadOnlyMemory<byte>> loadedSectionPayloads = sectionPayloads.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Payload);
            ObjectAssetCacheSectionSet loadedSections = ObjectAssetCacheSectionSet.None;
            foreach (ObjectAssetCacheSectionKind kind in sectionPayloads.Keys)
            {
                loadedSections |= kind.ToSectionSet();
            }

            if (loadedSections == ObjectAssetCacheSectionSet.None)
            {
                return ObjectAssetCacheLoadResult.Empty;
            }

            ObjectAssetCacheSnapshot snapshot = _serializer.Deserialize(manifest, loadedSectionPayloads);
            _logger.LogInformation(
                "loaded object asset cache sections {LoadedSections} with {StaticCollisionCount} static collision paths, {StaticBgObjectCount} static bg objects, {StaticVfxCount} static resolved vfx paths, {BgModelCount} bg models, {VfxCount} standalone vfx assets, and {TimelinePathCount} timeline referenced vfx paths",
                loadedSections,
                snapshot.StaticCollisionPaths.Count,
                snapshot.StaticGameDataBgObjects.Count,
                snapshot.StaticResolvedVfxEntries.Count,
                snapshot.BgModels.Count,
                snapshot.StandaloneVfxAssets.Count,
                snapshot.TimelineReferencedVfxEntries.Count);
            return new ObjectAssetCacheLoadResult(snapshot, loadedSections);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load object asset cache sections");
            return ObjectAssetCacheLoadResult.Empty;
        }
    }

    public void Save(ObjectAssetCacheSaveRequest request)
    {
        Dictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload> mergedSections = [];
        ObjectAssetCacheSectionSet reusableSections = ObjectAssetCacheSectionSet.All & ~request.Sections;
        if (reusableSections != ObjectAssetCacheSectionSet.None
         && TryLoadManifest() is { } existingManifest
         && existingManifest.MatchesBuildIdentity(request.GameVersion, request.SqpackIndexFingerprint)
         && _payloadReader.TryReadSectionPayloads(existingManifest, reusableSections, out Dictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload>? existingSections))
        {
            foreach ((ObjectAssetCacheSectionKind kind, ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload section) in existingSections)
            {
                mergedSections[kind] = section;
            }
        }

        foreach ((ObjectAssetCacheSectionKind kind, ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload section) in _serializer.SerializeSections(request))
        {
            mergedSections[kind] = section;
        }

        ObjectAssetCacheSerializer.ObjectAssetCacheSerializedData serializedData = _serializer.BuildSerializedData(mergedSections);
        ObjectAssetCacheManifest manifest = _serializer.BuildManifest(
            request.GameVersion,
            request.SqpackIndexFingerprint,
            serializedData);
        string manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);

        _fileSystem.WriteAllBytesAtomic(_pathService.AssetCachePayloadPath, serializedData.Payload);
        _fileSystem.WriteAllTextAtomic(_pathService.AssetCacheManifestPath, manifestJson);

        _ = manifest.TryBuildSectionMap(out IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheManifestSection>? manifestSections, out _);

        _logger.LogInformation(
            "saved object asset cache with {StaticCollisionCount} static collision paths, {StaticBgObjectCount} static bg objects, {StaticVfxCount} static resolved vfx paths, {BgModelCount} bg models, {VfxCount} standalone vfx assets, and {TimelinePathCount} timeline referenced vfx paths",
            GetSectionCount(manifestSections, ObjectAssetCacheSectionKind.StaticCollisionPaths),
            GetSectionCount(manifestSections, ObjectAssetCacheSectionKind.StaticBgObjects),
            GetSectionCount(manifestSections, ObjectAssetCacheSectionKind.StaticResolvedVfx),
            GetSectionCount(manifestSections, ObjectAssetCacheSectionKind.BgModels),
            GetSectionCount(manifestSections, ObjectAssetCacheSectionKind.StandaloneVfx),
            GetSectionCount(manifestSections, ObjectAssetCacheSectionKind.TimelineReferencedVfx));
    }

    private static int GetSectionCount(
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheManifestSection>? manifestSections,
        ObjectAssetCacheSectionKind kind)
        => manifestSections is not null && manifestSections.TryGetValue(kind, out ObjectAssetCacheManifestSection? section)
            ? section.Count
            : 0;
}

