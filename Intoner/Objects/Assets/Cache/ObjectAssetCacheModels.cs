using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Intoner.Objects.Assets.Cache;

[Flags]
internal enum ObjectAssetCacheSectionSet
{
    None                   = 0,
    StaticCollisionPaths   = 1 << 0,
    StaticBgObjects        = 1 << 1,
    StaticResolvedVfx      = 1 << 2,
    BgModels               = 1 << 3,
    StandaloneVfx          = 1 << 4,
    TimelineReferencedVfx  = 1 << 5,
    AllStatic              = StaticCollisionPaths | StaticBgObjects | StaticResolvedVfx,
    RuntimeOverlay         = BgModels | StandaloneVfx | TimelineReferencedVfx,
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
        new(ObjectAssetCacheSectionKind.StandaloneVfx,         "standaloneVfx",         ObjectAssetCacheSectionSet.StandaloneVfx),
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

    public static int CountSections(this ObjectAssetCacheSectionSet sections)
        => BitOperations.PopCount((uint)(sections & ObjectAssetCacheSectionSet.All));

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

        kind = 0;
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
        Dictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheManifestSection> map = [];
        foreach (ObjectAssetCacheManifestSection section in manifest.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Kind))
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

            if (!map.TryAdd(kind, section))
            {
                error = $"duplicate cache section kind '{section.Kind}'";
                sectionMap = null;
                return false;
            }
        }

        error = null;
        sectionMap = map;
        return true;
    }

    public static bool MatchesBuildIdentity(
        this ObjectAssetCacheManifest manifest,
        string? gameVersion,
        string? sqpackIndexFingerprint)
        => !string.IsNullOrWhiteSpace(gameVersion)
        && !string.IsNullOrWhiteSpace(sqpackIndexFingerprint)
        && string.Equals(manifest.GameVersion, gameVersion, StringComparison.OrdinalIgnoreCase)
        && string.Equals(manifest.SqpackIndexFingerprint, sqpackIndexFingerprint, StringComparison.Ordinal);

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
            if (!sectionMap.TryGetValue(descriptor.Kind, out ObjectAssetCacheManifestSection? section)
             || !section.HasReusablePayload())
            {
                continue;
            }

            reusableSections |= descriptor.SectionSet;
        }

        return reusableSections;
    }

    private static bool HasReusablePayload(this ObjectAssetCacheManifestSection section)
        => section.Length > 0 && !string.IsNullOrWhiteSpace(section.Hash);
}

internal readonly record struct ObjectAssetCacheSectionDescriptor(
    ObjectAssetCacheSectionKind Kind,
    string ManifestName,
    ObjectAssetCacheSectionSet SectionSet);

internal sealed record ObjectAssetCacheSnapshot(
    string? GameVersion,
    IReadOnlyList<string> StaticCollisionPaths,
    IReadOnlyList<ObjectAssetCacheStaticBgObject> StaticGameDataBgObjects,
    IReadOnlyList<ObjectAssetCacheResolvedVfxEntry> StaticResolvedVfxEntries,
    IReadOnlyList<ObjectAssetCacheBgModel> BgModels,
    IReadOnlyList<ObjectAssetCacheStandaloneVfx> StandaloneVfxAssets,
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

internal sealed record ObjectAssetCacheSaveRequest(
    string? GameVersion,
    string? SqpackIndexFingerprint,
    ObjectAssetCacheSectionSet Sections,
    IReadOnlyList<string> StaticCollisionPaths,
    IReadOnlyList<ObjectAssetCacheStaticBgObject> StaticGameDataBgObjects,
    IReadOnlyList<ObjectAssetCacheResolvedVfxEntry> StaticResolvedVfxEntries,
    IReadOnlyList<ObjectAssetCacheBgModel> BgModels,
    IReadOnlyList<ObjectAssetCacheStandaloneVfx> StandaloneVfxAssets,
    IReadOnlyList<ObjectAssetCacheTimelineReferencedVfxEntry> TimelineReferencedVfxEntries);

internal sealed record ObjectAssetCacheStaticBgObject(
    string ModelPath,
    string Source,
    uint RowId,
    string SourcePath,
    IReadOnlyList<uint> TerritoryIds,
    IReadOnlyList<string> TerritoryNames,
    IReadOnlyList<string> SearchTerms);

internal sealed record ObjectAssetCacheResolvedVfxEntry(
    string Path,
    KnownVfxFamily Family,
    RuntimeVfxEvidence Evidence,
    AssetPathSource Sources,
    AssetPathContract Contracts,
    IReadOnlyList<string> SearchTerms,
    VfxAnalysis? Analysis);

internal sealed record ObjectAssetCacheBgModel(
    string Path,
    IReadOnlyList<string> Sources,
    IReadOnlyList<uint> TerritoryIds,
    IReadOnlyList<string> TerritoryNames);

internal sealed record ObjectAssetCacheStandaloneVfx(
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
    DateTime CreatedUtc,
    long PayloadLength,
    string PayloadHash,
    IReadOnlyList<ObjectAssetCacheManifestSection> Sections);

internal sealed record ObjectAssetCacheManifestSection(
    string Kind,
    int Count,
    long Offset,
    int Length,
    string Hash);

internal enum ObjectAssetCacheSectionKind : byte
{
    StaticCollisionPaths   = 1,
    StaticBgObjects        = 2,
    StaticResolvedVfx      = 3,
    BgModels               = 4,
    StandaloneVfx          = 5,
    TimelineReferencedVfx  = 6,
}

