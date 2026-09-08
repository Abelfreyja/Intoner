using System.Runtime.InteropServices;

namespace Intoner.Services.Configuration;

internal enum UiColorSource
{
    Preset,
    Custom,
}

internal enum UiColorPreset
{
    Purple,
    Pink,
}

internal sealed class UiThemeColorConfiguration
{
    public UiColorSource Source { get; set; } = UiColorSource.Preset;
    public UiColorPreset Preset { get; set; } = UiColorPreset.Purple;
    public RgbColor CustomColor { get; set; } = new(217, 140, 184);

    public static UiThemeColorConfiguration CreateDefault()
        => new();

    public UiThemeColorConfiguration Copy()
        => new()
        {
            Source = Source,
            Preset = Preset,
            CustomColor = CustomColor,
        };
}

internal sealed class UiThemeConfiguration
{
    public required UiThemeColorConfiguration PrimaryColor { get; set; }

    public static UiThemeConfiguration CreateDefault()
        => new()
        {
            PrimaryColor = UiThemeColorConfiguration.CreateDefault(),
        };

    public UiThemeConfiguration Copy()
        => new()
        {
            PrimaryColor = PrimaryColor.Copy(),
        };
}

internal enum SceneListRowSize
{
    Large,
    Compact,
}

internal enum ToolbarDockPosition
{
    Top,
    Right,
    Bottom,
    Left,
}

internal sealed class UiConfiguration
{
    [StructLayout(LayoutKind.Auto)]
    public readonly record struct SplitRatios(float CatalogCreate, float PlacedInspector, float LayoutManager)
    {
        public const float Minimum = 0.20f;
        public const float Maximum = 0.80f;
        public const float DefaultRatio = 0.50f;

        public static SplitRatios Default { get; } = new(DefaultRatio, DefaultRatio, DefaultRatio);

        public SplitRatios Clamp()
            => new(
                  ClampRatio(CatalogCreate),
                  ClampRatio(PlacedInspector),
                  ClampRatio(LayoutManager));

        public static float ClampRatio(float value)
            => float.IsFinite(value)
                ? Math.Clamp(value, Minimum, Maximum)
                : DefaultRatio;
    }

    public required bool ShowSplashScreenOnStartup { get; set; }
    public bool HideWithGameUi { get; set; } = true;
    public bool HideInCutscenes { get; set; } = true;
    public bool HideInGpose { get; set; } = false;
    public bool ShowSceneToolsWithoutEditor { get; set; }
    public bool EnableShortcutsWithoutEditor { get; set; }
    public bool ShowDependencyStatusInTitleBar { get; set; } = true;
    public bool ShowDebugTab { get; set; }
    public bool ShowDebugInformation { get; set; }
    public required UiThemeConfiguration Theme { get; set; }
    public SceneListRowSize PlacedListRowSize { get; set; } = SceneListRowSize.Large;
    public ToolbarDockPosition ToolbarPosition { get; set; } = ToolbarDockPosition.Top;
    public SplitRatios WorkspaceSplits { get; set; } = SplitRatios.Default;

    public static UiConfiguration CreateDefault()
        => new()
        {
            ShowSplashScreenOnStartup = true,
            HideWithGameUi = true,
            HideInCutscenes = true,
            HideInGpose = false,
            ShowSceneToolsWithoutEditor = false,
            EnableShortcutsWithoutEditor = false,
            ShowDependencyStatusInTitleBar = true,
            ShowDebugTab = false,
            ShowDebugInformation = false,
            Theme = UiThemeConfiguration.CreateDefault(),
            PlacedListRowSize = SceneListRowSize.Large,
            ToolbarPosition = ToolbarDockPosition.Top,
            WorkspaceSplits = SplitRatios.Default,
        };

    public UiConfiguration Copy()
        => new()
        {
            ShowSplashScreenOnStartup = ShowSplashScreenOnStartup,
            HideWithGameUi = HideWithGameUi,
            HideInCutscenes = HideInCutscenes,
            HideInGpose = HideInGpose,
            ShowSceneToolsWithoutEditor = ShowSceneToolsWithoutEditor,
            EnableShortcutsWithoutEditor = EnableShortcutsWithoutEditor,
            ShowDependencyStatusInTitleBar = ShowDependencyStatusInTitleBar,
            ShowDebugTab = ShowDebugTab,
            ShowDebugInformation = ShowDebugInformation,
            Theme = Theme.Copy(),
            PlacedListRowSize = PlacedListRowSize,
            ToolbarPosition = ToolbarPosition,
            WorkspaceSplits = WorkspaceSplits,
        };
}
