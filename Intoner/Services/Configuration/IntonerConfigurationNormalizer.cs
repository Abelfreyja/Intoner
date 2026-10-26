using Intoner.Logging;

namespace Intoner.Services.Configuration;

internal static class IntonerConfigurationNormalizer
{
    public static void Normalize(IntonerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateVersion(configuration.Version);

        configuration.AssetCapture ??= AssetCaptureConfiguration.CreateDefault();
        configuration.HousingMode ??= HousingModeConfiguration.CreateDefault();
        configuration.Layouts ??= LayoutConfiguration.CreateDefault();
        configuration.LayoutAutoSave ??= LayoutAutoSaveConfiguration.CreateDefault();
        configuration.Logging ??= LoggingConfiguration.CreateDefault();
        configuration.Rendering ??= RenderingConfiguration.CreateDefault();
        configuration.Ui ??= UiConfiguration.CreateDefault();

        NormalizeHousingMode(configuration.HousingMode);
        NormalizeLayouts(configuration.Layouts);
        NormalizeLayoutAutoSave(configuration.LayoutAutoSave);
        NormalizeLogging(configuration.Logging);
        NormalizeRendering(configuration.Rendering);
        NormalizeUi(configuration.Ui);
    }

    private static void ValidateVersion(int version)
    {
        if (version != IntonerConfiguration.CurrentVersion)
        {
            throw new InvalidDataException($"unsupported Intoner configuration version {version}");
        }
    }

    private static void NormalizeLayouts(LayoutConfiguration configuration)
    {
        configuration.EnabledLayoutIds ??= [];
        HashSet<Guid> seen = [];
        configuration.EnabledLayoutIds.RemoveAll(id => id == Guid.Empty || !seen.Add(id));
        if (configuration.CreationLayoutId.HasValue && !seen.Contains(configuration.CreationLayoutId.Value))
        {
            configuration.CreationLayoutId = null;
        }
    }

    private static void NormalizeHousingMode(HousingModeConfiguration configuration)
    {
        HousingModeConfiguration defaults = HousingModeConfiguration.CreateDefault();
        configuration.Mode = NormalizeEnum(configuration.Mode, defaults.Mode);
        configuration.Size = NormalizeEnum(configuration.Size, defaults.Size);
        configuration.Area = NormalizeEnum(configuration.Area, defaults.Area);
        if (configuration.Size == ObjectHousingSize.Apartment)
        {
            configuration.Area = ObjectHousingArea.Indoor;
        }
    }

    private static void NormalizeLayoutAutoSave(LayoutAutoSaveConfiguration configuration)
        => configuration.IntervalSeconds = LayoutAutoSaveConfiguration.ClampIntervalSeconds(configuration.IntervalSeconds);

    private static void NormalizeLogging(LoggingConfiguration configuration)
        => configuration.DalamudMinimumLevel = IntonerLogLevels.NormalizeDalamudMinimumLevel(configuration.DalamudMinimumLevel);

    private static void NormalizeRendering(RenderingConfiguration configuration)
    {
        RenderingConfiguration defaults = RenderingConfiguration.CreateDefault();
        configuration.DrawMode = NormalizeEnum(configuration.DrawMode, defaults.DrawMode);
        configuration.DepthMode = NormalizeEnum(configuration.DepthMode, defaults.DepthMode);
        configuration.AntiAliasing = RenderingConfiguration.ClampAntiAliasing(configuration.AntiAliasing);
        configuration.GizmoAppearance ??= new GizmoAppearanceConfiguration();
        configuration.GizmoAppearance.Preset = NormalizeEnum(configuration.GizmoAppearance.Preset, defaults.GizmoAppearance.Preset);
        configuration.GizmoAppearance.Opacity = Math.Clamp(configuration.GizmoAppearance.Opacity,
            GizmoAppearanceConfiguration.MinimumOpacity, GizmoAppearanceConfiguration.MaximumOpacity);
    }

    private static void NormalizeUi(UiConfiguration configuration)
    {
        UiConfiguration defaults = UiConfiguration.CreateDefault();
        configuration.Theme ??= UiThemeConfiguration.CreateDefault();
        configuration.Theme.PrimaryColor ??= UiThemeColorConfiguration.CreateDefault();
        configuration.Theme.PrimaryColor.Source = NormalizeEnum(configuration.Theme.PrimaryColor.Source, defaults.Theme.PrimaryColor.Source);
        configuration.Theme.PrimaryColor.Preset = NormalizeEnum(configuration.Theme.PrimaryColor.Preset, defaults.Theme.PrimaryColor.Preset);
        configuration.PlacedListRowSize = NormalizeEnum(configuration.PlacedListRowSize, defaults.PlacedListRowSize);
        configuration.PlacedLayoutView = NormalizeEnum(configuration.PlacedLayoutView, defaults.PlacedLayoutView);
        configuration.ToolbarPosition = NormalizeEnum(configuration.ToolbarPosition, defaults.ToolbarPosition);
        configuration.WorkspaceSplits = configuration.WorkspaceSplits.Clamp();
        configuration.ShortcutOverrides ??= new(StringComparer.Ordinal);
        foreach (string id in configuration.ShortcutOverrides.Keys.ToArray())
        {
            if (string.IsNullOrWhiteSpace(id) || !string.Equals(id, id.Trim(), StringComparison.Ordinal))
            {
                configuration.ShortcutOverrides.Remove(id);
            }
            else if (configuration.ShortcutOverrides[id] is { } gesture && !gesture.IsValid())
            {
                configuration.ShortcutOverrides[id] = null;
            }
        }
    }

    private static TEnum NormalizeEnum<TEnum>(TEnum value, TEnum fallback)
        where TEnum : struct, Enum
        => Enum.IsDefined(value) ? value : fallback;
}
