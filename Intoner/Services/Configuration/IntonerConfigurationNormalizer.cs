using Intoner.Logging;

namespace Intoner.Services.Configuration;

internal static class IntonerConfigurationNormalizer
{
    public static void Normalize(IntonerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateVersion(configuration.Version);

        ValidateSection(configuration.AssetCapture, nameof(configuration.AssetCapture));
        ValidateSection(configuration.HousingMode, nameof(configuration.HousingMode));
        ValidateSection(configuration.Layouts, nameof(configuration.Layouts));
        ValidateSection(configuration.LayoutAutoSave, nameof(configuration.LayoutAutoSave));
        ValidateSection(configuration.Logging, nameof(configuration.Logging));
        ValidateSection(configuration.Rendering, nameof(configuration.Rendering));
        ValidateSection(configuration.Ui, nameof(configuration.Ui));

        NormalizeHousingMode(configuration.HousingMode);
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

    private static void ValidateSection<TSection>(TSection? section, string sectionName)
        where TSection : class
    {
        if (section is null)
        {
            throw new InvalidDataException($"Intoner configuration section '{sectionName}' is missing");
        }
    }

    private static void NormalizeHousingMode(HousingModeConfiguration configuration)
    {
        ValidateEnum(configuration.Mode, nameof(configuration.Mode));
        ValidateEnum(configuration.Size, nameof(configuration.Size));
        ValidateEnum(configuration.Area, nameof(configuration.Area));
        if (configuration.Size == ObjectHousingSize.Apartment)
        {
            configuration.Area = ObjectHousingArea.Indoor;
        }
    }

    private static void NormalizeLayoutAutoSave(LayoutAutoSaveConfiguration configuration)
        => configuration.IntervalSeconds = LayoutAutoSaveConfiguration.ClampIntervalSeconds(configuration.IntervalSeconds);

    private static void NormalizeLogging(LoggingConfiguration configuration)
    {
        ValidateEnum(configuration.DalamudMinimumLevel, nameof(configuration.DalamudMinimumLevel));
        configuration.DalamudMinimumLevel = IntonerLogLevels.NormalizeDalamudMinimumLevel(configuration.DalamudMinimumLevel);
    }

    private static void NormalizeRendering(RenderingConfiguration configuration)
    {
        ValidateEnum(configuration.DrawMode, nameof(configuration.DrawMode));
        ValidateEnum(configuration.DepthMode, nameof(configuration.DepthMode));
        configuration.AntiAliasing = RenderingConfiguration.ClampAntiAliasing(configuration.AntiAliasing);
        ValidateSection(configuration.GizmoAppearance, nameof(configuration.GizmoAppearance));
        ValidateEnum(configuration.GizmoAppearance.Preset, nameof(configuration.GizmoAppearance.Preset));
        configuration.GizmoAppearance.Opacity = Math.Clamp(configuration.GizmoAppearance.Opacity,
            GizmoAppearanceConfiguration.MinimumOpacity, GizmoAppearanceConfiguration.MaximumOpacity);
    }

    private static void NormalizeUi(UiConfiguration configuration)
    {
        ValidateSection(configuration.Theme, nameof(configuration.Theme));
        ValidateSection(configuration.Theme.PrimaryColor, nameof(configuration.Theme.PrimaryColor));
        ValidateEnum(configuration.Theme.PrimaryColor.Source, nameof(configuration.Theme.PrimaryColor.Source));
        ValidateEnum(configuration.Theme.PrimaryColor.Preset, nameof(configuration.Theme.PrimaryColor.Preset));
        ValidateEnum(configuration.PlacedListRowSize, nameof(configuration.PlacedListRowSize));
        ValidateEnum(configuration.ToolbarPosition, nameof(configuration.ToolbarPosition));
        configuration.WorkspaceSplits = configuration.WorkspaceSplits.Clamp();
    }

    private static void ValidateEnum<TEnum>(TEnum value, string propertyName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new InvalidDataException($"Intoner configuration property '{propertyName}' has invalid value '{value}'");
        }
    }
}

