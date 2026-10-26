using Intoner.Services.Storage;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.Assets.Cache;

internal sealed class ObjectAssetCachePayloadReader
{
    private readonly ILogger<ObjectAssetCachePayloadReader> _logger;
    private readonly IPluginFileSystem _fileSystem;

    public ObjectAssetCachePayloadReader(
        ILogger<ObjectAssetCachePayloadReader> logger,
        IPluginFileSystem fileSystem)
    {
        _logger = logger;
        _fileSystem = fileSystem;
    }

    public bool TryReadSectionPayloads(
        ObjectAssetCacheManifest manifest,
        string payloadPath,
        ObjectAssetCacheSectionSet sections,
        [NotNullWhen(true)] out Dictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload>? loadedSectionPayloads)
    {
        loadedSectionPayloads = [];
        if (sections == ObjectAssetCacheSectionSet.None)
        {
            return true;
        }

        if (!manifest.TryBuildSectionMap(out IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheManifestSection>? manifestSections, out string? error))
        {
            _logger.LogInformation("ignoring object asset cache with invalid section metadata: {Error}", error);
            loadedSectionPayloads = null;
            return false;
        }

        if (_fileSystem.GetFileLength(payloadPath) != manifest.PayloadLength)
        {
            _logger.LogInformation("ignoring object asset cache with unexpected payload length");
            loadedSectionPayloads = null;
            return false;
        }

        byte[]? payload = manifestSections.Keys.All(kind => sections.Contains(kind))
            ? _fileSystem.ReadAllBytes(payloadPath)
            : null;
        long payloadLength = payload?.LongLength ?? manifest.PayloadLength;

        foreach ((ObjectAssetCacheSectionKind kind, ObjectAssetCacheManifestSection section) in manifestSections)
        {
            if (!sections.Contains(kind))
            {
                continue;
            }

            if (!TryValidateSectionRange(payloadLength, section))
            {
                _logger.LogInformation("ignoring object asset cache section {SectionKind} with invalid payload range", section.Kind);
                continue;
            }

            ReadOnlyMemory<byte> sectionPayload = payload is not null
                ? payload.AsMemory((int)section.Offset, section.Length)
                : _fileSystem.ReadBytes(payloadPath, section.Offset, section.Length);

            if (!ValidateSectionHash(section, sectionPayload.Span))
            {
                _logger.LogInformation("ignoring object asset cache section {SectionKind} with hash mismatch", section.Kind);
                continue;
            }

            loadedSectionPayloads[kind] = new ObjectAssetCacheSerializer.ObjectAssetCacheSectionPayload(kind, section.Count, sectionPayload);
        }

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
        => !string.IsNullOrWhiteSpace(section.Hash)
        && string.Equals(
            ObjectAssetHashUtility.ComputeSha256Hex(payload),
            section.Hash,
            StringComparison.OrdinalIgnoreCase);
}
