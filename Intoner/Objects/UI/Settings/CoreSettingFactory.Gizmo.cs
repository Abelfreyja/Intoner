using Intoner.Objects.UI.Settings.Components;
using Intoner.Services.Configuration;

namespace Intoner.Objects.UI.Settings;

internal sealed partial class CoreSettingFactory
{
    private static readonly ChoiceOption<GizmoColorPreset>[] GizmoColorOptions =
    [
        new(GizmoColorPreset.Default, "Default", "default"),
        new(GizmoColorPreset.ImGuizmo, "ImGuizmo", "imguizmo"),
        new(GizmoColorPreset.Custom, "Custom", "custom colors"),
    ];

    public ISettingEntry CreateGizmoColorPreset()
        => new ChoiceSettingEntry<GizmoColorPreset>(
            new SettingDefinition("gizmoColorPreset", "Color Preset", "Choose a preset or custom gizmo colors.", "gizmo color preset imguizmo custom"),
            GizmoColorOptions,
            () => _configuration.Current.Rendering.GizmoAppearance.Preset,
            value => _configuration.TryUpdate(configuration => configuration.Rendering.GizmoAppearance.Preset = value),
            style: ChoiceRowStyle.Segmented,
            layout: new SettingRowLayout(390f));

    public ISettingEntry CreateGizmoOpacity()
        => new IntegerSettingEntry(
            new SettingDefinition("gizmoOpacity", "Opacity", "Lower values make the gizmo more transparent.", "gizmo opacity transparency alpha"),
            new IntegerSettingRange(GizmoAppearanceConfiguration.MinimumOpacity, GizmoAppearanceConfiguration.MaximumOpacity, 5),
            () => _configuration.Current.Rendering.GizmoAppearance.Opacity,
            value => _configuration.TryUpdate(configuration => configuration.Rendering.GizmoAppearance.Opacity = value),
            static value => $"{value}%",
            static value => $"{value}%");

    public ISettingEntry CreateGizmoColor(string id, string label, string description,
        Func<GizmoAppearanceConfiguration, RgbColor> readColor, Action<GizmoAppearanceConfiguration, RgbColor> writeColor)
        => new ColorSettingEntry(
            new SettingDefinition(id, label, description, $"gizmo custom color {label}"),
            () => readColor(_configuration.Current.Rendering.GizmoAppearance).ToNormalizedVector3(),
            value => _configuration.TryUpdate(configuration =>
                writeColor(configuration.Rendering.GizmoAppearance, RgbColor.FromNormalizedVector3(value))))
            .VisibleWhen(() => _configuration.Current.Rendering.GizmoAppearance.Preset == GizmoColorPreset.Custom);
}
