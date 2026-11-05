using Dalamud.Interface;
using Intoner.Objects.Library;

namespace Intoner.Objects.UI.Components;

internal static class CatalogLibraryTooltip
{
    private const int VisibleEntryLimit = 5;

    internal sealed record Data(int TotalCount, IReadOnlyList<Location> Locations);

    internal sealed record Location(string Name, LocationKind Kind, string GroupPath);

    internal enum LocationKind
    {
        Ungrouped,
        Folder,
        Prefab,
    }

    public static Data Build(
        ObjectLibraryHierarchy hierarchy,
        IReadOnlyList<ObjectLibraryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentNullException.ThrowIfNull(entries);

        int visibleCount = Math.Min(entries.Count, VisibleEntryLimit);
        List<Location> resolvedLocations = new(visibleCount);
        for (int index = 0; index < visibleCount; ++index)
        {
            ObjectLibraryEntry entry = entries[index];
            ObjectLibraryGroup? group = hierarchy.GetEntryGroup(entry);
            LocationKind kind = group switch
            {
                ObjectLibraryPrefab => LocationKind.Prefab,
                ObjectLibraryFolder => LocationKind.Folder,
                _ => LocationKind.Ungrouped,
            };
            resolvedLocations.Add(new Location(entry.Name, kind, group is null ? string.Empty : hierarchy.GetGroupPath(group)));
        }

        return new Data(entries.Count, resolvedLocations);
    }

    public static void Draw(Data data)
    {
        string summary = data.TotalCount == 1 ? "1 saved preset" : $"{data.TotalCount} saved presets";
        int remaining = data.TotalCount - data.Locations.Count;
        string remainingText = remaining == 1 ? "1 more saved preset" : $"{remaining} more saved presets";
        float contentWidth = IntonerTooltipContent.MeasureHeaderWidth(
            FontAwesomeIcon.Star,
            "Saved in Library",
            summary);
        foreach (Location location in data.Locations)
        {
            (FontAwesomeIcon icon, string detail) = ResolveLocationContent(location);
            contentWidth = MathF.Max(
                contentWidth,
                IntonerTooltipContent.MeasureItemWidth(icon, location.Name, detail));
        }

        if (remaining > 0)
        {
            contentWidth = MathF.Max(
                contentWidth,
                IntonerTooltipContent.MeasureNoticeWidth(FontAwesomeIcon.EllipsisH, remainingText));
        }

        IntonerTooltip.DrawContentSized(
            () =>
            {
                IntonerTooltipContent.Header(
                    FontAwesomeIcon.Star,
                    "Saved in Library",
                    summary,
                    ThemeColors.AccentYellow);
                foreach (Location location in data.Locations)
                {
                    DrawLocation(location);
                }

                if (remaining > 0)
                {
                    IntonerTooltipContent.Notice(
                        FontAwesomeIcon.EllipsisH,
                        remainingText,
                        ThemeColors.AccentYellow);
                }
            },
            contentWidth,
            new IntonerTooltipOptions
            {
                Accent = ThemeColors.AccentYellow,
                MaxWidth = 280f,
            });
    }

    private static void DrawLocation(Location location)
    {
        (FontAwesomeIcon icon, string detail) = ResolveLocationContent(location);
        IntonerTooltipContent.Item(icon, location.Name, detail, ThemeColors.AccentYellow);
    }

    private static (FontAwesomeIcon Icon, string Detail) ResolveLocationContent(Location location)
    {
        FontAwesomeIcon icon = location.Kind switch
        {
            LocationKind.Ungrouped => FontAwesomeIcon.LayerGroup,
            LocationKind.Folder    => FontAwesomeIcon.Folder,
            LocationKind.Prefab    => FontAwesomeIcon.Cubes,
            _                      => throw new ArgumentOutOfRangeException(nameof(location), location.Kind, null),
        };
        string detail = location.Kind switch
        {
            LocationKind.Ungrouped => "Ungrouped",
            LocationKind.Folder    => $"Folder: {location.GroupPath}",
            LocationKind.Prefab    => $"Prefab: {location.GroupPath}",
            _                      => throw new ArgumentOutOfRangeException(nameof(location), location.Kind, null),
        };

        return (icon, detail);
    }
}
