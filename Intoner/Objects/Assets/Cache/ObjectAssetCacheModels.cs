using Dalamud.Game;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Intoner.Objects.Assets.Cache;

[Flags]
internal enum ObjectAssetCacheSectionSet
{
    None                   = 0,
    StaticCollisionPaths   = 1 << 0,
    StaticBgObjects        = 1 << 1,
    StaticResolvedVfx      = 1 << 2,
    BgModels               = 1 << 3,
    RuntimeVfx             = 1 << 4,
    TimelineReferencedVfx  = 1 << 5,
    AllStatic              = StaticCollisionPaths | StaticBgObjects | StaticResolvedVfx,
    RuntimeOverlay         = BgModels | RuntimeVfx | TimelineReferencedVfx,
    All                    = AllStatic | RuntimeOverlay,
}

internal static class ObjectAssetCacheSectionSetExtensions
{
    private static readonly ObjectAssetCacheSectionDescriptor[] SectionDescriptors =
    [
        new(ObjectAssetCacheSectionKind.StaticCollisionPaths,  "staticCollisionPaths",  ObjectAssetCacheSectionSet.StaticCollisionPaths),
        new(ObjectAssetCacheSectionKind.StaticBgObjects,       "staticBgObjects",       ObjectAssetCacheSectionSet.StaticBgObjects),
        new(ObjectAssetCacheSectionKind.StaticResolvedVfx,     "staticResolvedVfx",     ObjectAssetCacheSectionSet.StaticResolvedVfx),
        new(ObjectAssetCacheSectionKind.BgModels,              "bgModels",              ObjectAssetCacheSectionSet.BgModels),
        new(ObjectAssetCacheSectionKind.RuntimeVfx,            "runtimeVfx",            ObjectAssetCacheSectionSet.RuntimeVfx),
        new(ObjectAssetCacheSectionKind.TimelineReferencedVfx, "timelineReferencedVfx", ObjectAssetCacheSectionSet.TimelineReferencedVfx),
    ];

    private static readonly IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSectionDescriptor> DescriptorByKind =
        SectionDescriptors.ToDictionary(static descriptor => descriptor.Kind);

    private static readonly IReadOnlyDictionary<string, ObjectAssetCacheSectionDescriptor> DescriptorByManifestName =
        SectionDescriptors.ToDictionary(static descriptor => descriptor.ManifestName, StringComparer.Ordinal);

    public static bool Contains(this ObjectAssetCacheSectionSet sections, ObjectAssetCacheSectionKind kind)
        => sections.HasAny(kind.ToSectionSet());

    public static bool HasAny(this ObjectAssetCacheSectionSet sections, ObjectAssetCacheSectionSet flags)
        => (sections & flags) != ObjectAssetCacheSectionSet.None;

    public static ObjectAssetCacheSectionSet ToSectionSet(this ObjectAssetCacheSectionKind kind)
        => DescriptorByKind.TryGetValue(kind, out ObjectAssetCacheSectionDescriptor descriptor)
            ? descriptor.SectionSet
            : ObjectAssetCacheSectionSet.None;

    public static IEnumerable<ObjectAssetCacheSectionDescriptor> EnumerateDescriptors(this ObjectAssetCacheSectionSet sections)
    {
        foreach (ObjectAssetCacheSectionDescriptor descriptor in SectionDescriptors)
        {
            if (sections.HasAny(descriptor.SectionSet))
            {
                yield return descriptor;
            }
        }
    }

    public static IEnumerable<ObjectAssetCacheSectionDescriptor> EnumerateDescriptors()
        => SectionDescriptors;

    public static bool TryParseSectionKind(string value, out ObjectAssetCacheSectionKind kind)
    {
        if (DescriptorByManifestName.TryGetValue(value, out ObjectAssetCacheSectionDescriptor descriptor))
        {
            kind = descriptor.Kind;
            return true;
        }

        kind = ObjectAssetCacheSectionKind.Unknown;
        return false;
    }

    public static string ToManifestName(this ObjectAssetCacheSectionKind kind)
        => DescriptorByKind.TryGetValue(kind, out ObjectAssetCacheSectionDescriptor descriptor)
            ? descriptor.ManifestName
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, null);

    public static bool TryBuildSectionMap(
        this ObjectAssetCacheManifest manifest,
        [NotNullWhen(true)] out IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheManifestSection>? sectionMap,
        [NotNullWhen(false)] out string? error)
    {
        if (!Enum.IsDefined(manifest.Language))
        {
            error = "invalid cache client language";
            sectionMap = null;
            return false;
        }

        if (manifest.PayloadLength < 0 || manifest.Sections is null)
        {
            error = "invalid cache payload metadata";
            sectionMap = null;
            return false;
        }

        if ((manifest.Sections.Count == 0) != (manifest.PayloadLength == 0))
        {
            error = "cache sections do not match the payload length";
            sectionMap = null;
            return false;
        }

        Dictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheManifestSection> map = [];
        foreach (ObjectAssetCacheManifestSection section in manifest.Sections)
        {
            if (section is null || string.IsNullOrWhiteSpace(section.Kind))
            {
                error = "missing cache section kind";
                sectionMap = null;
                return false;
            }

            if (!TryParseSectionKind(section.Kind, out ObjectAssetCacheSectionKind kind))
            {
                error = $"unknown cache section kind '{section.Kind}'";
                sectionMap = null;
                return false;
            }

            long sectionEnd = section.Offset + section.Length;
            if (section.Count < 0
             || section.Offset < 0
             || section.Length <= 0
             || sectionEnd < section.Offset
             || sectionEnd > manifest.PayloadLength
             || !IsSha256Hash(section.Hash))
            {
                error = $"invalid payload metadata for cache section '{section.Kind}'";
                sectionMap = null;
                return false;
            }

            if (!map.TryAdd(kind, section))
            {
                error = $"duplicate cache section kind '{section.Kind}'";
                sectionMap = null;
                return false;
            }
        }

        long previousEnd = 0;
        foreach (ObjectAssetCacheManifestSection section in map.Values.OrderBy(static section => section.Offset))
        {
            if (section.Offset != previousEnd)
            {
                error = $"non-contiguous payload range for cache section '{section.Kind}'";
                sectionMap = null;
                return false;
            }

            previousEnd = section.Offset + section.Length;
        }

        if (previousEnd != manifest.PayloadLength)
        {
            error = "cache section ranges do not cover the payload";
            sectionMap = null;
            return false;
        }

        error = null;
        sectionMap = map;
        return true;
    }

    public static bool MatchesBuildIdentity(
        this ObjectAssetCacheManifest manifest,
        string? gameVersion,
        string? sqpackIndexFingerprint,
        ClientLanguage language)
        => Enum.IsDefined(language)
        && manifest.Language == language
        && !string.IsNullOrWhiteSpace(gameVersion)
        && !string.IsNullOrWhiteSpace(sqpackIndexFingerprint)
        && string.Equals(manifest.GameVersion, gameVersion, StringComparison.OrdinalIgnoreCase)
        && string.Equals(manifest.SqpackIndexFingerprint, sqpackIndexFingerprint, StringComparison.Ordinal);

    public static bool MatchesSaveIdentity(
        this ObjectAssetCacheManifest manifest,
        string? gameVersion,
        string? sqpackIndexFingerprint,
        ClientLanguage language)
        => Enum.IsDefined(language)
        && manifest.Language == language
        && string.Equals(NormalizeIdentityValue(manifest.GameVersion), NormalizeIdentityValue(gameVersion), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeIdentityValue(manifest.SqpackIndexFingerprint), NormalizeIdentityValue(sqpackIndexFingerprint), StringComparison.Ordinal);

    private static string? NormalizeIdentityValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    public static ObjectAssetCacheSectionSet GetReusableSections(
        this ObjectAssetCacheManifest manifest,
        ObjectAssetCacheSectionSet requestedSections)
    {
        if (requestedSections == ObjectAssetCacheSectionSet.None
         || !manifest.TryBuildSectionMap(out IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheManifestSection>? sectionMap, out _))
        {
            return ObjectAssetCacheSectionSet.None;
        }

        ObjectAssetCacheSectionSet reusableSections = ObjectAssetCacheSectionSet.None;
        foreach (ObjectAssetCacheSectionDescriptor descriptor in requestedSections.EnumerateDescriptors())
        {
            if (!sectionMap.ContainsKey(descriptor.Kind))
            {
                continue;
            }

            reusableSections |= descriptor.SectionSet;
        }

        return reusableSections;
    }
    private static bool IsSha256Hash(string? hash)
        => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
}

internal readonly record struct ObjectAssetCacheSectionDescriptor(
    ObjectAssetCacheSectionKind Kind,
    string ManifestName,
    ObjectAssetCacheSectionSet SectionSet);

internal sealed record ObjectAssetCacheSnapshot(
    string? GameVersion,
    IReadOnlyList<string> StaticCollisionPaths,
    IReadOnlyList<BgObjectAsset> StaticGameDataBgObjects,
    IReadOnlyList<ResolvedVfxPath> StaticResolvedVfxEntries,
    IReadOnlyList<ObjectAssetCacheBgModel> BgModels,
    IReadOnlyList<ObjectAssetCacheRuntimeVfx> RuntimeVfxAssets,
    IReadOnlyList<ObjectAssetCacheTimelineReferencedVfxEntry> TimelineReferencedVfxEntries)
{
    public static ObjectAssetCacheSnapshot Empty { get; } = new(
        null,
        [],
        [],
        [],
        [],
        [],
        []);
}

internal sealed record ObjectAssetCacheLoadResult(
    ObjectAssetCacheSnapshot Snapshot,
    ObjectAssetCacheSectionSet LoadedSections)
{
    public static ObjectAssetCacheLoadResult Empty { get; } = new(
        ObjectAssetCacheSnapshot.Empty,
        ObjectAssetCacheSectionSet.None);
}

internal readonly record struct ObjectAssetCacheLoadRequest(
    string? GameVersion,
    string? SqpackIndexFingerprint,
    ClientLanguage Language,
    ObjectAssetCacheSectionSet Sections)
{
    public ObjectAssetCacheSectionSet GetReusableSections(ObjectAssetCacheManifest? manifest)
        => manifest is not null && manifest.MatchesBuildIdentity(GameVersion, SqpackIndexFingerprint, Language)
            ? manifest.GetReusableSections(Sections)
            : ObjectAssetCacheSectionSet.None;
}

internal sealed record ObjectAssetCacheSaveRequest(
    string? GameVersion,
    string? SqpackIndexFingerprint,
    ClientLanguage Language,
    ObjectAssetCacheSectionSet Sections,
    IReadOnlyList<string> StaticCollisionPaths,
    IReadOnlyList<BgObjectAsset> StaticGameDataBgObjects,
    IReadOnlyList<ResolvedVfxPath> StaticResolvedVfxEntries,
    IReadOnlyList<ObjectAssetCacheBgModel> BgModels,
    IReadOnlyList<ObjectAssetCacheRuntimeVfx> RuntimeVfxAssets,
    IReadOnlyList<ObjectAssetCacheTimelineReferencedVfxEntry> TimelineReferencedVfxEntries);

internal sealed record ObjectAssetCacheBgModel(
    string Path,
    IReadOnlyList<string> Sources,
    IReadOnlyList<uint> TerritoryIds,
    IReadOnlyList<string> TerritoryNames,
    IReadOnlyList<string> SearchTerms);

internal sealed record ObjectAssetCacheRuntimeVfx(
    string Path,
    RuntimeVfxEvidence Evidence,
    VfxAnalysis? Analysis);

internal sealed record ObjectAssetCacheTimelineReferencedVfxEntry(
    string Path,
    RuntimeVfxEvidence Evidence,
    VfxTimelineContext Context);

internal sealed record ObjectAssetCacheManifest(
    int SchemaVersion,
    int FormatVersion,
    string CacheFileName,
    string? GameVersion,
    string? SqpackIndexFingerprint,
    [property: JsonRequired] ClientLanguage Language,
    DateTime CreatedUtc,
    long PayloadLength,
    IReadOnlyList<ObjectAssetCacheManifestSection> Sections);

internal sealed record ObjectAssetCacheManifestSection(
    string Kind,
    int Count,
    long Offset,
    int Length,
    string Hash);

internal enum ObjectAssetCacheSectionKind : byte
{
    Unknown                = 0,
    StaticCollisionPaths   = 1,
    StaticBgObjects        = 2,
    StaticResolvedVfx      = 3,
    BgModels               = 4,
    RuntimeVfx             = 5,
    TimelineReferencedVfx  = 6,
}
