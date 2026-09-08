using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Catalog;
using Intoner.Objects.UI.Services;
using System.Globalization;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class TerritoryUsageTooltip
{
    internal const ImGuiKey ExpandKey = ImGuiKey.LeftAlt;

    private const int PreviewLimit = 4;
    private const int GridColumns = 5;
    private const float ArtworkWidth = 112f;
    private const float ArtworkHeight = 68f;
    private const float GridRowHeight = 21f;

    internal sealed record Data(
        int TotalCount,
        IReadOnlyList<ArtworkGroup> ArtworkGroups,
        IReadOnlyList<TerritoryArtwork> BadgeArtwork,
        IReadOnlyList<string> FallbackNames);

    internal sealed record ArtworkGroup(
        TerritoryArtwork Artwork,
        IReadOnlyList<string> TerritoryIdLabels,
        string Title,
        string Subtitle,
        string PreviewText);

    public static Data Build(ObjectCatalogBgObjectInfo info, TerritoryArtworkService artworkService)
    {
        if (info.TerritoryIds.Count == 0)
        {
            return new Data(info.TerritoryNames.Count, [], [], info.TerritoryNames);
        }

        Dictionary<(string TexturePath, string Name), List<uint>> territoryIdsByArtwork = [];
        Dictionary<(string TexturePath, string Name), TerritoryArtwork> artworkByKey = [];
        List<(string TexturePath, string Name)> keyOrder = [];
        List<string> fallbackNames = [];
        foreach (uint territoryId in info.TerritoryIds)
        {
            if (!artworkService.TryGet(territoryId, out TerritoryArtwork? artwork))
            {
                fallbackNames.Add($"Territory {territoryId.ToString(CultureInfo.InvariantCulture)}");
                continue;
            }

            (string TexturePath, string Name) key = (artwork.TexturePath, artwork.Name);
            if (!territoryIdsByArtwork.TryGetValue(key, out List<uint>? territoryIds))
            {
                territoryIds = [];
                territoryIdsByArtwork.Add(key, territoryIds);
                artworkByKey.Add(key, artwork);
                keyOrder.Add(key);
            }

            territoryIds.Add(territoryId);
        }

        List<ArtworkGroup> groups = new(keyOrder.Count);
        foreach ((string TexturePath, string Name) key in keyOrder)
        {
            TerritoryArtwork artwork = artworkByKey[key];
            List<uint> territoryIds = territoryIdsByArtwork[key];
            (string title, string subtitle) = SplitArtworkName(artwork.Name);
            groups.Add(new ArtworkGroup(
                artwork,
                territoryIds.Select(static id => $"#{id.ToString(CultureInfo.InvariantCulture)}").ToArray(),
                title,
                subtitle,
                BuildIdPreview(territoryIds)));
        }

        List<TerritoryArtwork> badgeArtwork = groups
            .Where(static group => group.Artwork.Texture is not null)
            .Select(static group => group.Artwork)
            .DistinctBy(static artwork => artwork.TexturePath, StringComparer.OrdinalIgnoreCase)
            .Take(PreviewLimit)
            .ToList();
        return new Data(info.TerritoryIds.Count, groups, badgeArtwork, fallbackNames);
    }

    public static IDalamudTextureWrap? GetBadgeTexture(Data data, int index)
    {
        if ((uint)index >= (uint)data.BadgeArtwork.Count)
        {
            return null;
        }

        return TryGetTexture(data.BadgeArtwork[index]);
    }

    public static void Draw(Data data)
    {
        bool expanded = data.TotalCount > PreviewLimit && ImGui.IsKeyDown(ExpandKey);
        IntonerTooltip.Draw(
            () =>
            {
                IntonerTooltipContent.Heading(
                    FontAwesomeIcon.MapMarkerAlt,
                    "Found In",
                    separatorTopSpacing: 3f);
                using var spacing = ImRaii.PushStyle(
                    ImGuiStyleVar.ItemSpacing,
                    new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
                DrawTerritories(data, expanded);
            },
            new IntonerTooltipOptions
            {
                Accent = ThemeColors.AccentPrimary,
                Width = 400f,
            });
    }

    private static void DrawTerritories(Data data, bool expanded)
    {
        if (expanded)
        {
            DrawExpanded(data);
            return;
        }

        int visibleGroupCount = Math.Min(data.ArtworkGroups.Count, PreviewLimit);
        for (int index = 0; index < visibleGroupCount; ++index)
        {
            DrawArtworkGroup(data.ArtworkGroups[index], false);
        }

        int visibleFallbackCount = Math.Min(data.FallbackNames.Count, PreviewLimit - visibleGroupCount);
        DrawFallbackNames(data.FallbackNames, visibleFallbackCount);
        if (data.TotalCount > PreviewLimit)
        {
            string suffix = data.TotalCount == 1
                ? "to show the territory"
                : $"to show all {data.TotalCount.ToString(CultureInfo.InvariantCulture)} territories";
            IntonerTooltipContent.KeyHint("Hold", ExpandKey, suffix, topSpacing: 5f);
        }
    }

    private static void DrawExpanded(Data data)
    {
        float contentHeight = MeasureExpandedHeight(data);
        float maxHeight = MathF.Max(
            ArtworkHeight * ImGuiHelpers.GlobalScale,
            ImGui.GetMainViewport().WorkSize.Y * 0.58f);
        IntonerTooltipScroll.Draw(
            "##territoryUsage",
            contentHeight,
            () =>
            {
                foreach (ArtworkGroup group in data.ArtworkGroups)
                {
                    DrawArtworkGroup(group, true);
                }

                DrawFallbackNames(data.FallbackNames, data.FallbackNames.Count);
            },
            new IntonerTooltipScrollOptions
            {
                Accent = ThemeColors.AccentPrimary,
                MaxHeight = maxHeight / ImGuiHelpers.GlobalScale,
                ModifierKey = ExpandKey,
            });
    }

    private static float MeasureExpandedHeight(Data data)
    {
        Vector2 mediaSize = new(ArtworkWidth, ArtworkHeight);
        float height = 0f;
        foreach (ArtworkGroup group in data.ArtworkGroups)
        {
            height += IntonerTooltipMedia.MeasureRowHeight(mediaSize);
            height += IntonerTooltipGrid.MeasureHeight(
                group.TerritoryIdLabels.Count,
                CreateGridOptions());
        }

        height += data.FallbackNames.Count * ImGui.GetTextLineHeight();
        return MathF.Max(1f, height);
    }

    private static void DrawArtworkGroup(ArtworkGroup group, bool expanded)
    {
        IDalamudTextureWrap? texture = TryGetTexture(group.Artwork);
        string territoryCount = group.TerritoryIdLabels.Count == 1
            ? "1 territory"
            : $"{group.TerritoryIdLabels.Count.ToString(CultureInfo.InvariantCulture)} territories";
        IntonerTooltipMedia.DrawRow(new IntonerTooltipMediaRow
        {
            MediaSize = new Vector2(ArtworkWidth, ArtworkHeight),
            DrawMedia = (drawList, min, max) => DrawArtwork(drawList, texture, min, max),
            Title = group.Title,
            Subtitle = group.Subtitle,
            Metadata = expanded ? string.Empty : group.PreviewText,
            Trailing = territoryCount,
        });
        if (expanded)
        {
            IntonerTooltipGrid.Draw(group.TerritoryIdLabels, CreateGridOptions());
        }
    }

    private static void DrawArtwork(
        ImDrawListPtr drawList,
        IDalamudTextureWrap? texture,
        Vector2 min,
        Vector2 max)
    {
        if (texture is not null)
        {
            EditorImage.DrawCover(drawList, texture, min, max, drawBorder: false);
            return;
        }

        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(ThemeColors.WindowBg with { W = 0.72f }));
        EditorIcon.DrawCentered(
            drawList,
            FontAwesomeIcon.MapMarkerAlt,
            min,
            max,
            ThemeColors.AccentPrimary with { W = 0.72f });
    }

    private static void DrawFallbackNames(IReadOnlyList<string> names, int count)
    {
        if (count <= 0)
        {
            return;
        }

        using var textColor = ImRaii.PushColor(ImGuiCol.Text, ThemeColors.TextDisabled);
        for (int index = 0; index < count; ++index)
        {
            ImGui.TextUnformatted(names[index]);
        }
    }

    private static string BuildIdPreview(IReadOnlyList<uint> territoryIds)
    {
        int visibleCount = Math.Min(territoryIds.Count, PreviewLimit);
        string visibleIds = string.Join(
            "  ·  ",
            territoryIds.Take(visibleCount).Select(static id => id.ToString(CultureInfo.InvariantCulture)));
        int remaining = territoryIds.Count - visibleCount;
        return remaining > 0
            ? $"{visibleIds}  ·  +{remaining.ToString(CultureInfo.InvariantCulture)}"
            : visibleIds;
    }

    private static (string Title, string Subtitle) SplitArtworkName(string name)
    {
        int separator = name.IndexOf(" - ", StringComparison.Ordinal);
        return separator > 0 && separator + 3 < name.Length
            ? (name[(separator + 3)..], name[..separator])
            : (name, string.Empty);
    }

    private static IDalamudTextureWrap? TryGetTexture(TerritoryArtwork artwork)
        => artwork.Texture is not null
        && artwork.Texture.TryGetWrap(out IDalamudTextureWrap? texture, out _)
            ? texture
            : null;

    private static IntonerTooltipGridOptions CreateGridOptions()
        => new()
        {
            Columns = GridColumns,
            RowHeight = GridRowHeight,
        };
}
