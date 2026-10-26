using Intoner.Services.Storage;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace Intoner.Objects.Assets.Cache;

/// <summary> loads and saves object asset cache snapshots owned by the objects subsystem </summary>
internal interface IObjectAssetCacheService
{
    /// <summary> loads reusable cache sections while holding the publication mutex </summary>
    /// <param name="request">the current build identity and requested sections</param>
    /// <returns>the loaded snapshot data and loaded section flags</returns>
    ObjectAssetCacheLoadResult Load(ObjectAssetCacheLoadRequest request);

    /// <summary> saves selected object asset cache sections </summary>
    /// <param name="request">the save request to persist</param>
    void Save(ObjectAssetCacheSaveRequest request);
}

internal sealed class ObjectAssetCacheService : IObjectAssetCacheService
{
    internal const int SchemaVersion = 2;
    private const string ReleasedPayloadFileName = "assets.cache";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ILogger<ObjectAssetCacheService> _logger;
    private readonly IPluginStoragePaths _pathService;
    private readonly IPluginFileSystem _fileSystem;
    private readonly ObjectAssetCachePayloadReader _payloadReader;
    private readonly string _publicationMutexName;

    public ObjectAssetCacheService(
        ILogger<ObjectAssetCacheService> logger,
        IPluginStoragePaths pathService,
        IPluginFileSystem fileSystem,
        ObjectAssetCachePayloadReader payloadReader)
    {
        _logger = logger;
        _pathService = pathService;
        _fileSystem = fileSystem;
        _payloadReader = payloadReader;
        _publicationMutexName = GetPublicationMutexName(pathService.AssetCacheRootPath);
    }

    private ObjectAssetCacheManifest? TryLoadManifestLocked()
    {
        if (!_fileSystem.FileExists(_pathService.AssetCacheManifestPath))
        {
            return null;
        }

        string json = _fileSystem.ReadAllText(_pathService.AssetCacheManifestPath);

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
             || !TryReadVersion(root, "schemaVersion", out int schemaVersion)
             || !TryReadVersion(root, "formatVersion", out int formatVersion))
            {
                return DiscardManifestLocked("missing or invalid version metadata");
            }

            if (schemaVersion != SchemaVersion)
            {
                return DiscardManifestLocked($"unsupported schema version {schemaVersion}");
            }

            if (formatVersion != ObjectAssetCacheSerializer.FormatVersion)
            {
                return DiscardManifestLocked($"unsupported format version {formatVersion}");
            }

            ObjectAssetCacheManifest? manifest = root.Deserialize<ObjectAssetCacheManifest>(JsonOptions);

            if (manifest is null || !IsPayloadFileName(manifest.CacheFileName))
            {
                return DiscardManifestLocked("invalid payload file name");
            }

            if (!manifest.TryBuildSectionMap(out _, out string? error))
            {
                return DiscardManifestLocked($"invalid section metadata: {error}");
            }

            if (!_fileSystem.FileExists(GetPayloadPath(manifest.CacheFileName)))
            {
                return DiscardManifestLocked("missing payload file");
            }

            return manifest;
        }
        catch (JsonException ex)
        {
            return DiscardManifestLocked("invalid JSON data", ex);
        }
    }

    private ObjectAssetCacheManifest? DiscardManifestLocked(string reason, Exception? exception = null)
    {
        _fileSystem.DeleteFile(_pathService.AssetCacheManifestPath);

        if (exception is null)
        {
            _logger.LogInformation("discarded object asset cache manifest: {Reason}", reason);
        }
        else
        {
            _logger.LogWarning(exception, "discarded invalid object asset cache manifest: {Reason}", reason);
        }

        return null;
    }

    private static bool TryReadVersion(JsonElement root, string name, out int version)
    {
        version = 0;
        return root.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out version);
    }

    public ObjectAssetCacheLoadResult Load(ObjectAssetCacheLoadRequest request)
    {
        if (request.Sections == ObjectAssetCacheSectionSet.None)
        {
            return ObjectAssetCacheLoadResult.Empty;
        }

        try
        {
            using PublicationMutex publicationMutex = new(_publicationMutexName, _logger);
            ObjectAssetCacheManifest? manifest = TryLoadManifestLocked();
            ObjectAssetCacheSectionSet sections = request.GetReusableSections(manifest);
            return manifest is not null && sections != ObjectAssetCacheSectionSet.None
                ? LoadSectionsLocked(manifest, sections)
                : ObjectAssetCacheLoadResult.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load object asset cache sections");
            return ObjectAssetCacheLoadResult.Empty;
        }
    }

    private ObjectAssetCacheLoadResult LoadSectionsLocked(ObjectAssetCacheManifest manifest, ObjectAssetCacheSectionSet sections)
    {
        string payloadPath = GetPayloadPath(manifest.CacheFileName);
        if (!_payloadReader.TryReadSectionPayloads(manifest, payloadPath, sections, out Dictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload>? sectionPayloads))
        {
            return ObjectAssetCacheLoadResult.Empty;
        }

        Dictionary<ObjectAssetCacheSectionKind, ReadOnlyMemory<byte>> loadedSectionPayloads = sectionPayloads.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Payload);
        ObjectAssetCacheSnapshot snapshot = ObjectAssetCacheSerializer.Deserialize(manifest, loadedSectionPayloads, out ObjectAssetCacheSectionSet loadedSections);
        foreach (ObjectAssetCacheSectionKind kind in sectionPayloads.Keys.Where(kind => !loadedSections.Contains(kind)))
        {
            _logger.LogInformation("ignoring object asset cache section {SectionKind} with invalid encoded data", kind);
        }

        if (loadedSections == ObjectAssetCacheSectionSet.None)
        {
            return ObjectAssetCacheLoadResult.Empty;
        }

        _logger.LogInformation(
            "loaded object asset cache sections {LoadedSections} with {StaticCollisionCount} static collision paths, {StaticBgObjectCount} static bg objects, {StaticVfxCount} static resolved vfx paths, {BgModelCount} bg models, {VfxCount} runtime vfx paths, and {TimelinePathCount} timeline referenced vfx paths",
            loadedSections,
            snapshot.StaticCollisionPaths.Count,
            snapshot.StaticGameDataBgObjects.Count,
            snapshot.StaticResolvedVfxEntries.Count,
            snapshot.BgModels.Count,
            snapshot.RuntimeVfxAssets.Count,
            snapshot.TimelineReferencedVfxEntries.Count);
        return new ObjectAssetCacheLoadResult(snapshot, loadedSections);
    }

    public void Save(ObjectAssetCacheSaveRequest request)
    {
        if (!Enum.IsDefined(request.Language))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Language, "invalid client language");
        }

        using PublicationMutex publicationMutex = new(_publicationMutexName, _logger);
        SaveLocked(request);
    }

    internal static string GetPublicationMutexName(string cacheRoot)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot)).ToUpperInvariant();
        return $"Local\\Intoner.AssetCache.{ObjectAssetHashUtility.ComputeSha256Hex(Encoding.UTF8.GetBytes(normalizedRoot))}";
    }

    private void SaveLocked(ObjectAssetCacheSaveRequest request)
    {
        Dictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload> mergedSections = [];
        ObjectAssetCacheSectionSet reusableSections = ObjectAssetCacheSectionSet.All & ~request.Sections;

        if (reusableSections != ObjectAssetCacheSectionSet.None
         && TryLoadManifestLocked() is { } existingManifest
         && existingManifest.MatchesSaveIdentity(request.GameVersion, request.SqpackIndexFingerprint, request.Language)
         && _payloadReader.TryReadSectionPayloads(
             existingManifest,
             GetPayloadPath(existingManifest.CacheFileName),
             existingManifest.GetReusableSections(reusableSections),
             out Dictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload>? existingSections))
        {
            foreach ((ObjectAssetCacheSectionKind kind, ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload section) in existingSections)
            {
                if (ObjectAssetCacheSerializer.TryValidateSectionPayload(kind, section.Count, section.Payload))
                {
                    mergedSections[kind] = section;
                }
                else
                {
                    _logger.LogInformation("ignoring retained object asset cache section {SectionKind} with invalid encoded data", kind);
                }
            }
        }

        foreach ((ObjectAssetCacheSectionKind kind, ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload section) in ObjectAssetCacheSerializer.SerializeSections(request))
        {
            mergedSections[kind] = section;
        }

        ObjectAssetCacheSerializer.ObjectAssetCacheSerializedData serializedData = ObjectAssetCacheSerializer.BuildSerializedData(mergedSections);
        string cacheFileName = $"assets.{Guid.NewGuid():N}.cache";
        ObjectAssetCacheManifest manifest = ObjectAssetCacheSerializer.BuildManifest(
            request.GameVersion,
            request.SqpackIndexFingerprint,
            request.Language,
            serializedData,
            cacheFileName);
        string manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);

        _fileSystem.WriteAllBytesAtomic(GetPayloadPath(manifest.CacheFileName), serializedData.Payload);
        _fileSystem.WriteAllTextAtomic(_pathService.AssetCacheManifestPath, manifestJson);
        DeleteUnusedPayloads(manifest.CacheFileName);

        _ = manifest.TryBuildSectionMap(out IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheManifestSection>? manifestSections, out _);

        _logger.LogInformation(
            "saved object asset cache with {StaticCollisionCount} static collision paths, {StaticBgObjectCount} static bg objects, {StaticVfxCount} static resolved vfx paths, {BgModelCount} bg models, {VfxCount} runtime vfx paths, and {TimelinePathCount} timeline referenced vfx paths",
            GetSectionCount(manifestSections, ObjectAssetCacheSectionKind.StaticCollisionPaths),
            GetSectionCount(manifestSections, ObjectAssetCacheSectionKind.StaticBgObjects),
            GetSectionCount(manifestSections, ObjectAssetCacheSectionKind.StaticResolvedVfx),
            GetSectionCount(manifestSections, ObjectAssetCacheSectionKind.BgModels),
            GetSectionCount(manifestSections, ObjectAssetCacheSectionKind.RuntimeVfx),
            GetSectionCount(manifestSections, ObjectAssetCacheSectionKind.TimelineReferencedVfx));
    }

    private string GetPayloadPath(string cacheFileName)
        => Path.Combine(_pathService.AssetCacheRootPath, cacheFileName);

    private static bool IsPayloadFileName(string? fileName)
        => fileName is { Length: 45 }
        && fileName.StartsWith("assets.", StringComparison.Ordinal)
        && fileName.EndsWith(".cache", StringComparison.Ordinal)
        && Guid.TryParseExact(fileName.AsSpan(7, 32), "N", out _);

    private void DeleteUnusedPayloads(string currentFileName)
    {
        try
        {
            foreach (string path in _fileSystem.EnumerateFiles(_pathService.AssetCacheRootPath, "assets.*.cache"))
            {
                string fileName = Path.GetFileName(path);

                if (IsPayloadFileName(fileName) && !string.Equals(fileName, currentFileName, StringComparison.OrdinalIgnoreCase))
                {
                    _fileSystem.DeleteFile(GetPayloadPath(fileName));
                }
            }

            _fileSystem.DeleteFile(GetPayloadPath(ReleasedPayloadFileName));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "failed to remove unused object asset cache payloads");
        }
    }

    private sealed class PublicationMutex : IDisposable
    {
        private readonly Mutex _mutex;

        public PublicationMutex(string name, ILogger logger)
        {
            _mutex = new Mutex(false, name);
            try
            {
                _ = _mutex.WaitOne();
            }
            catch (AbandonedMutexException ex)
            {
                try
                {
                    logger.LogWarning(ex, "previous object asset cache transaction was interrupted");
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }
            catch
            {
                _mutex.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            finally
            {
                _mutex.Dispose();
            }
        }
    }

    private static int GetSectionCount(
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheManifestSection>? manifestSections,
        ObjectAssetCacheSectionKind kind)
        => manifestSections is not null && manifestSections.TryGetValue(kind, out ObjectAssetCacheManifestSection? section)
            ? section.Count
            : 0;
}
