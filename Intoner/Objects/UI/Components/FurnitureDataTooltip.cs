using Dalamud.Interface;
using Intoner.Objects.Catalog;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Components;

internal static class FurnitureDataTooltip
{
    private const float MaxWidth = 320f;

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PropertyRow(string Label, string Value);

    public static EditorBadge CreateBadge(ObjectCatalogFurnitureVariant variant)
        => new(
            $"#{variant.HousingRowId.ToString(CultureInfo.InvariantCulture)}",
            EditorBadgeStyle.Muted,
            DrawTooltip: () => Draw(variant));

    private static void Draw(ObjectCatalogFurnitureVariant variant)
    {
        string title = variant.Name;
        string subtitle = BuildPlacementSummary(variant);
        IReadOnlyList<PropertyRow> properties = BuildProperties(variant);
        float contentWidth = IntonerTooltipContent.MeasureHeaderWidth(
            FontAwesomeIcon.Home,
            title,
            subtitle);
        foreach (PropertyRow property in properties)
        {
            contentWidth = MathF.Max(
                contentWidth,
                IntonerTooltipContent.MeasurePropertyWidth(property.Label, property.Value));
        }

        IntonerTooltip.DrawContentSized(
            () =>
            {
                IntonerTooltipContent.Heading(
                    FontAwesomeIcon.Home,
                    title,
                    subtitle,
                    ThemeColors.AccentPrimary);
                foreach (PropertyRow property in properties)
                {
                    IntonerTooltipContent.Property(property.Label, property.Value);
                }
            },
            contentWidth,
            new IntonerTooltipOptions
            {
                Accent = ThemeColors.AccentPrimary,
                MaxWidth = MaxWidth,
            });
    }

    private static IReadOnlyList<PropertyRow> BuildProperties(ObjectCatalogFurnitureVariant variant)
    {
        List<PropertyRow> properties = new(4);
        if (!string.IsNullOrWhiteSpace(variant.Category))
        {
            properties.Add(new PropertyRow("Category", variant.Category));
        }

        if (variant.HousingRowId != 0)
        {
            properties.Add(new PropertyRow(
                "Housing row",
                variant.HousingRowId.ToString(CultureInfo.InvariantCulture)));
        }

        if (variant.ItemRowId != 0)
        {
            properties.Add(new PropertyRow(
                "Item ID",
                variant.ItemRowId.ToString(CultureInfo.InvariantCulture)));
        }

        if (variant.HousingMetadata.AquariumTier != 0)
        {
            properties.Add(new PropertyRow(
                "Aquarium",
                $"Tier {variant.HousingMetadata.AquariumTier.ToString(CultureInfo.InvariantCulture)}"));
        }

        return properties;
    }

    private static string BuildPlacementSummary(ObjectCatalogFurnitureVariant variant)
        => $"{variant.HousingMetadata.Area} · {variant.HousingMetadata.Surface}";
}
