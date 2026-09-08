using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Intoner.Objects.Filesystem.Layouts;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services;
using Intoner.Objects.UI.Services.Backdrop;
using Intoner.Objects.UI.Services.EdgeGlow;
using Intoner.Services;
using Intoner.Services.Configuration;
using Intoner.Services.Loading;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorStartupView : IDisposable
{
    private readonly IObjectManager               _objectManager;
    private readonly IObjectLayoutManager         _layoutManager;
    private readonly IObjectLayoutRecoveryService _objectLayoutRecoveryService;
    private readonly IIntonerConfigurationService _configurationService;
    private readonly EditorInteraction            _interaction;
    private readonly LayoutWorkspace              _layouts;
    private readonly EditorOverlayLayer           _editorOverlayLayer;
    private readonly EdgeGlowRenderer             _edgeGlowRenderer;
    private readonly BackdropRenderer             _windowBackdropRenderer;
    private readonly UiSharedService              _uiSharedService;
    private string _splashScreenStatusMessage = string.Empty;
    private bool _splashScreenStatusIsError;
    private bool _splashScreenLayoutPickerOpen;

    public EditorStartupView(
        IntonerBuildInfoService buildInfo,
        IObjectManager objectManager,
        IObjectLayoutManager layoutManager,
        IObjectLayoutRecoveryService objectLayoutRecoveryService,
        IIntonerConfigurationService configurationService,
        EditorInteraction interaction,
        LayoutWorkspace layouts,
        EditorOverlayLayer editorOverlayLayer,
        EdgeGlowRenderer edgeGlowRenderer,
        BackdropRenderer windowBackdropRenderer,
        UiSharedService uiSharedService)
    {
        _splashScreenVersionLabel    = buildInfo.SplashScreenVersion;
        _objectManager               = objectManager;
        _layoutManager               = layoutManager;
        _objectLayoutRecoveryService = objectLayoutRecoveryService;
        _configurationService        = configurationService;
        _interaction                 = interaction;
        _layouts                     = layouts;
        _editorOverlayLayer          = editorOverlayLayer;
        _edgeGlowRenderer            = edgeGlowRenderer;
        _windowBackdropRenderer      = windowBackdropRenderer;
        _uiSharedService             = uiSharedService;
        _interaction.DialogOpening += DismissSplashScreen;
    }

    public void Dispose()
        => _interaction.DialogOpening -= DismissSplashScreen;

    private static string ResolveLoadingStatusText(string statusText, string fallbackStatusText)
        => string.IsNullOrWhiteSpace(statusText) ? fallbackStatusText : statusText;

    internal void DrawEditorLoadingScreen(LoadStatus loadStatus)
    {
        bool hasFailed = loadStatus.HasFailed;
        if (EditorLayout.TryGetWindowBodyArea(out EditorOverlayArea area))
        {
            _edgeGlowRenderer.DrawRect(
                area.Min,
                area.Max,
                area.Rounding,
                new EdgeGlowStyle
                {
                    Mode = EdgeGlowMode.Line,
                    ColorVariant = hasFailed ? EdgeGlowColorVariant.Sunset : EdgeGlowColorVariant.Colorful,
                    Theme = EdgeGlowTheme.Dark,
                    BorderInset = 0f,
                    BorderWidth = 1f,
                    Duration = 2.4f,
                    Strength = hasFailed ? 0.86f : 1f,
                    Brightness = 1.3f,
                    Saturation = 1.2f,
                    HueRange = 13f,
                    StrokeOpacity = 0.72f,
                    InnerOpacity = 0.70f,
                    BloomOpacity = 0.64f,
                    InnerShadowAlpha = 0.10f,
                    RenderScale = 0.18f,
                    HorizontalFootprintScale = 2.5f,
                    CornerFlags = area.RoundingFlags,
                    ClipToRect = true,
                    ClipPadding = 1f,
                });
        }

        var status = ResolveLoadingStatusText(
            loadStatus.Message,
            hasFailed ? "Intoner loading failed" : "preparing Intoner");
        var headline = hasFailed ? "Intoner Unavailable" : "Preparing Intoner";
        var contentMin = ImGui.GetWindowPos() + ImGui.GetWindowContentRegionMin();
        var contentMax = ImGui.GetWindowPos() + ImGui.GetWindowContentRegionMax();
        var contentSize = contentMax - contentMin;
        if (contentSize.X <= 1f || contentSize.Y <= 1f)
        {
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        ImFontPtr headlineFont;
        float headlineFontSize;
        Vector2 headlineSize;
        using (_uiSharedService.UidFont.Push())
        {
            headlineFont = ImGui.GetFont();
            headlineFontSize = ImGui.GetFontSize();
            headlineSize = ImGui.CalcTextSize(headline);
        }

        ImFontPtr statusFont;
        float statusFontSize;
        Vector2 statusSize;
        using (_uiSharedService.MediumFont.Push())
        {
            statusFont = ImGui.GetFont();
            statusFontSize = ImGui.GetFontSize();
            statusSize = ImGui.CalcTextSize(status);
        }

        var headlineSpacing = 18f * scale;
        var progressSpacing = 14f * scale;
        var progressBarHeight = 6f * scale;
        var progressBarWidth = MathF.Min(
            360f * scale,
            MathF.Max(180f * scale, contentSize.X * 0.42f));
        progressBarWidth = MathF.Min(progressBarWidth, MathF.Max(0f, contentSize.X - (40f * scale)));
        var headlineColor = hasFailed
            ? ThemeColors.WithAlpha(ThemeColors.Text, 0.98f)
            : ThemeColors.Color(202f / 255f, 204f / 255f, 210f / 255f, 0.98f);
        var statusColor = hasFailed
            ? ThemeColors.WithAlpha(ThemeColors.TextDisabled, 0.96f)
            : ThemeColors.Color(108f / 255f, 108f / 255f, 108f / 255f, 0.96f);
        var shadowOffset = new Vector2(scale, scale);
        var headlineShadowColor = ThemeColors.Color(0f, 0f, 0f, hasFailed ? 0.42f : 0.36f);
        var statusShadowColor = ThemeColors.Color(0f, 0f, 0f, hasFailed ? 0.32f : 0.28f);
        var drawList = ImGui.GetWindowDrawList();
        var totalHeight = headlineSize.Y
            + headlineSpacing
            + statusSize.Y
            + progressSpacing
            + progressBarHeight;
        var headlinePosition = new Vector2(
            contentMin.X + ((contentSize.X - headlineSize.X) * 0.5f),
            contentMin.Y + ((contentSize.Y - totalHeight) * 0.5f));
        var statusPosition = new Vector2(
            contentMin.X + ((contentSize.X - statusSize.X) * 0.5f),
            headlinePosition.Y + headlineSize.Y + headlineSpacing);
        var progressBarMin = new Vector2(
            contentMin.X + ((contentSize.X - progressBarWidth) * 0.5f),
            statusPosition.Y + statusSize.Y + progressSpacing);
        var progressBarMax = progressBarMin + new Vector2(progressBarWidth, progressBarHeight);

        drawList.AddText(headlineFont, headlineFontSize, headlinePosition + shadowOffset, ImGui.GetColorU32(headlineShadowColor), headline);
        drawList.AddText(statusFont, statusFontSize, statusPosition + shadowOffset, ImGui.GetColorU32(statusShadowColor), status);
        drawList.AddText(headlineFont, headlineFontSize, headlinePosition, ImGui.GetColorU32(headlineColor), headline);
        drawList.AddText(statusFont, statusFontSize, statusPosition, ImGui.GetColorU32(statusColor), status);
        DrawLoadingProgressBar(drawList, progressBarMin, progressBarMax, loadStatus.Progress, hasFailed);
    }

    private static void DrawLoadingProgressBar(ImDrawListPtr drawList, Vector2 min, Vector2 max, double progress, bool hasFailed)
    {
        float height = max.Y - min.Y;
        if (height <= 0f || max.X <= min.X)
        {
            return;
        }

        float rounding = height * 0.5f;
        Vector4 trackColor = ThemeColors.Color(17f / 255f, 17f / 255f, 19f / 255f, 0.84f);
        Vector4 borderColor = ThemeColors.WithAlpha(ThemeColors.Border, 0.72f);
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(trackColor), rounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(borderColor), rounding);

        float inset = ImGuiHelpers.GlobalScale;
        Vector2 fillMin = min + new Vector2(inset);
        Vector2 fillMax = max - new Vector2(inset);
        if (fillMax.X <= fillMin.X || fillMax.Y <= fillMin.Y)
        {
            return;
        }

        Vector4 fillColor = hasFailed
            ? ThemeColors.DimRed
            : ThemeColors.AccentPrimary;
        float fillWidth = (fillMax.X - fillMin.X) * (float)Math.Clamp(progress, 0d, 1d);
        if (fillWidth <= 0f)
        {
            return;
        }

        drawList.AddRectFilled(
            fillMin,
            new Vector2(fillMin.X + fillWidth, fillMax.Y),
            ImGui.GetColorU32(fillColor),
            MathF.Min(rounding, fillWidth * 0.5f));
    }
}
