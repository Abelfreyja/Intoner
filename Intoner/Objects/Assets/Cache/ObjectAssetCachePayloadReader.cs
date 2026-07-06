using Intoner.Objects.Filesystem.Storage;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.Assets.Cache;

internal sealed class ObjectAssetCachePayloadReader
{
    private readonly ILogger<ObjectAssetCachePayloadReader> _logger;
    private readonly IObjectStoragePathService _pathService;
    private readonly IObjectFileSystem _fileSystem;

    public ObjectAssetCachePayloadReader(
        ILogger<ObjectAssetCachePayloadReader> logger,
        IObjectStoragePathService pathService,
        IObjectFileSystem fileSystem)
    {
        _logger = logger;
        _pathService = pathService;
        _fileSystem = fileSystem;
    }

    public bool TryReadSectionPayloads(
        ObjectAssetCacheManifest manifest,
        ObjectAssetCacheSectionSet sections,
        [NotNullWhen(true)] out Dictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload>? loadedSectionPayloads)
    {
        loadedSectionPayloads = [];
        if (sections == ObjectAssetCacheSectionSet.None)
        {
            return true;
        }

        long payloadLength = manifest.PayloadLength > 0
            ? manifest.PayloadLength
            : _fileSystem.GetFileLength(_pathService.AssetCachePayloadPath);
        if (!manifest.TryBuildSectionMap(out IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheManifestSection>? manifestSections, out string? error))
        {
            _logger.LogInformation("ignoring object asset cache with invalid section metadata: {Error}", error);
            loadedSectionPayloads = null;
            return false;
        }

        if (sections.CountSections() > 1)
        {
            return TryReadFromWholePayload(manifest, sections, manifestSections, out loadedSectionPayloads);
        }

        ReadRequestedSections(sections, manifestSections, payloadLength, loadedSectionPayloads);
        return true;
    }

    private bool TryReadFromWholePayload(
        ObjectAssetCacheManifest manifest,
        ObjectAssetCacheSectionSet sections,
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheManifestSection> manifestSections,
        [NotNullWhen(true)] out Dictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload>? loadedSectionPayloads)
    {
        loadedSectionPayloads = [];
        byte[] payload = _fileSystem.ReadAllBytes(_pathService.AssetCachePayloadPath);
        if (!string.IsNullOrWhiteSpace(manifest.PayloadHash)
         && !string.Equals(
             ObjectAssetHashUtility.ComputeSha256Hex(payload),
             manifest.PayloadHash,
             StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("ignoring object asset cache with payload hash mismatch");
            loadedSectionPayloads = null;
            return false;
        }

        foreach ((ObjectAssetCacheSectionKind kind, ObjectAssetCacheManifestSection section) in manifestSections)
        {
            if (!sections.Contains(kind))
            {
                continue;
            }

            if (!TrySliceSectionPayload(payload, section, out ReadOnlyMemory<byte> sectionPayload))
            {
                _logger.LogInformation("ignoring object asset cache section {SectionKind} with invalid payload range", section.Kind);
                continue;
            }

            if (!ValidateSectionHash(section, sectionPayload.Span))
            {
                _logger.LogInformation("ignoring object asset cache section {SectionKind} with hash mismatch", section.Kind);
                continue;
            }

            loadedSectionPayloads[kind] = new ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload(kind, section.Count, sectionPayload);
        }

        return true;
    }

    private void ReadRequestedSections(
        ObjectAssetCacheSectionSet requestedSections,
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheManifestSection> manifestSections,
        long payloadLength,
        Dictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload> loadedSectionPayloads)
    {
        foreach ((ObjectAssetCacheSectionKind kind, ObjectAssetCacheManifestSection section) in manifestSections)
        {
            if (!requestedSections.Contains(kind))
            {
                continue;
            }

            if (!TryValidateSectionRange(payloadLength, section))
            {
                _logger.LogInformation("ignoring object asset cache section {SectionKind} with invalid payload range", section.Kind);
                continue;
            }

            byte[] sectionPayload = _fileSystem.ReadBytes(
                _pathService.AssetCachePayloadPath,
                section.Offset,
                section.Length);
            if (!ValidateSectionHash(section, sectionPayload))
            {
                _logger.LogInformation("ignoring object asset cache section {SectionKind} with hash mismatch", section.Kind);
                continue;
            }

            loadedSectionPayloads[kind] = new ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload(kind, section.Count, sectionPayload);
        }
    }

    private static bool TrySliceSectionPayload(
        ReadOnlyMemory<byte> payload,
        ObjectAssetCacheManifestSection section,
        out ReadOnlyMemory<byte> sectionPayload)
    {
        sectionPayload = ReadOnlyMemory<byte>.Empty;
        if (!TryValidateSectionRange(payload.Length, section))
        {
            return false;
        }

        sectionPayload = payload.Slice((int)section.Offset, section.Length);
        return true;
    }

    private static bool TryValidateSectionRange(long payloadLength, ObjectAssetCacheManifestSection section)
    {
        long sectionEnd = section.Offset + section.Length;
        return section.Offset >= 0
            && section.Length >= 0
            && sectionEnd >= section.Offset
            && sectionEnd <= payloadLength;
    }

    private static bool ValidateSectionHash(ObjectAssetCacheManifestSection section, ReadOnlySpan<byte> payload)
        => string.IsNullOrWhiteSpace(section.Hash)
        || string.Equals(
            ObjectAssetHashUtility.ComputeSha256Hex(payload),
            section.Hash,
            StringComparison.OrdinalIgnoreCase);
}
