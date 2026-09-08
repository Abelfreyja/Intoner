using Dalamud.Interface;
using Intoner.Objects.Catalog;
using Intoner.Objects.Library;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services;
using System.Globalization;

namespace Intoner.Objects.UI;

internal sealed class CatalogLibraryStatus
{
    private readonly IObjectLibrary          _objectLibrary;
    private readonly TerritoryArtworkService _territoryArtwork;
    private CatalogBadgeCache? _catalogBadgeCache;

    public CatalogLibraryStatus(IObjectLibrary objectLibrary, TerritoryArtworkService territoryArtwork)
    {
        _objectLibrary    = objectLibrary;
        _territoryArtwork = territoryArtwork;
    }

    internal IReadOnlyList<EditorBadge> ResolveCatalogBadges(
        ObjectLibraryPreset preset,
        ObjectCatalogBgObjectInfo? bgObjectInfo = null)
    {
        ObjectLibrarySnapshot library = _objectLibrary.Current;
        CatalogBadgeCache badgeCache;
        if (_catalogBadgeCache is not { } cached || cached.Revision != library.Revision)
        {
            badgeCache = new CatalogBadgeCache(
                library.Revision,
                CatalogLibraryTooltip.LocationIndex.Create(library));
            _catalogBadgeCache = badgeCache;
        }
        else
        {
            badgeCache = cached;
        }

        if (badgeCache.Entries.TryGetValue(preset, out CatalogBadgeEntry? cachedEntry)
         && ReferenceEquals(cachedEntry.BgObjectInfo, bgObjectInfo))
        {
            return cachedEntry.Badges;
        }

        IReadOnlyList<ObjectLibraryEntry> entries = ObjectLibraryQuery.FindEntriesBySource(library, preset);
        List<EditorBadge> badges = new(2);
        if (CreateTerritoryUsageBadge(bgObjectInfo) is { } territoryBadge)
        {
            badges.Add(territoryBadge);
        }

        if (entries.Count > 0)
        {
            CatalogLibraryTooltip.Data tooltip = CatalogLibraryTooltip.Build(badgeCache.Locations, entries);
            badges.Add(EditorBadge.Count(
                FontAwesomeIcon.Star,
                entries.Count,
                () => CatalogLibraryTooltip.Draw(tooltip),
                ThemeColors.AccentYellow));
        }

        badgeCache.Entries[preset] = new CatalogBadgeEntry(bgObjectInfo, badges);
        return badges;
    }

    internal EditorBadge? CreateTerritoryUsageBadge(ObjectCatalogBgObjectInfo? info)
    {
        if (info is null)
        {
            return null;
        }

        TerritoryUsageTooltip.Data usage = TerritoryUsageTooltip.Build(info, _territoryArtwork);
        if (usage.TotalCount == 0)
        {
            return null;
        }

        string text = usage.TotalCount > 1
            ? usage.TotalCount.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        return usage.BadgeArtwork.Count > 0
            ? EditorBadge.ThumbnailStrip(
                FontAwesomeIcon.MapMarkerAlt,
                text,
                usage.BadgeArtwork.Count,
                index => TerritoryUsageTooltip.GetBadgeTexture(usage, index),
                () => TerritoryUsageTooltip.Draw(usage),
                ThemeColors.AccentPrimary)
            : EditorBadge.Count(
                FontAwesomeIcon.MapMarkerAlt,
                text,
                () => TerritoryUsageTooltip.Draw(usage),
                ThemeColors.AccentPrimary);
    }

    internal sealed record CatalogBadgeEntry(
        ObjectCatalogBgObjectInfo? BgObjectInfo,
        IReadOnlyList<EditorBadge> Badges);

    internal sealed record CatalogBadgeCache(
        long Revision,
        CatalogLibraryTooltip.LocationIndex Locations)
    {
        public Dictionary<ObjectLibraryPreset, CatalogBadgeEntry> Entries { get; } = [];
    }
}
