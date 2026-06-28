using Intoner.Logging;

namespace Intoner.Objects.Filesystem.Configuration;

internal static class ObjectConfigurationNormalizer
{
    public static void Normalize(ObjectConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateVersion(configuration.Version);

        ValidateSection(configuration.AssetCapture, nameof(configuration.AssetCapture));
        ValidateSection(configuration.HousingCulling, nameof(configuration.HousingCulling));
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
    }

    private static void ValidateVersion(int version)
    {
        if (version != ObjectConfiguration.CurrentVersion)
        {
            throw new InvalidDataException($"unsupported object configuration version {version}");
        }
    }

    private static void ValidateSection<TSection>(TSection? section, string sectionName)
        where TSection : class
    {
        if (section is null)
        {
            throw new InvalidDataException($"object configuration section '{sectionName}' is missing");
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
    }

    private static void ValidateEnum<TEnum>(TEnum value, string propertyName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new InvalidDataException($"object configuration property '{propertyName}' has invalid value '{value}'");
        }
    }
}

