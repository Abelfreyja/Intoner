using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services.EdgeGlow;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using Intoner.Objects.Catalog;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private bool TryResolveEditorWarmupData([NotNullWhen(true)] out ObjectCatalogData? catalog, out string statusText, out bool hasFailed)
    {
        catalog = null;

        if (_furnitureStainService.TryGetStains(out var stains))
        {
            _furnitureStains = stains;
        }

        hasFailed = _objectCatalog.HasFailed || _furnitureStainService.HasFailed;
        statusText = ResolveEditorWarmupStatusText();

        if (!_objectCatalog.TryGetCatalog(out catalog))
        {
            return false;
        }

        if (_furnitureStains.Count == 0)
        {
            return false;
        }

        statusText = string.Empty;
        return true;
    }

    private string ResolveEditorWarmupStatusText()
    {
        if (_objectCatalog.HasFailed)
        {
            return ResolveWarmupStatusText(_objectCatalog.StatusText, "failed to build object catalog");
        }

        if (!_objectCatalog.IsReady)
        {
            return ResolveWarmupStatusText(_objectCatalog.StatusText, "building object catalog");
        }

        if (_furnitureStainService.HasFailed)
        {
            return ResolveWarmupStatusText(_furnitureStainService.StatusText, "furniture stain load failed");
        }

        if (!_furnitureStainService.IsReady)
        {
            return ResolveWarmupStatusText(_furnitureStainService.StatusText, "building furniture stains");
        }

        return "preparing object editor";
    }

    private static string ResolveWarmupStatusText(string statusText, string fallbackStatusText)
        => string.IsNullOrWhiteSpace(statusText) ? fallbackStatusText : statusText;

    private void DrawEditorWarmupScreen(string statusText, bool hasFailed)
    {
        if (TryGetWindowBodyArea(out EditorOverlayArea area))
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

        var status = ResolveWarmupStatusText(
            statusText,
            hasFailed ? "object editor warmup failed" : "preparing objects");
        var headline = hasFailed ? "Object Editor Unavailable" : "Preparing Objects";
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

        var verticalSpacing = 16f * scale;
        var headlineColor = hasFailed
            ? EditorColors.WithAlpha(EditorColors.Text, 0.98f)
            : EditorColors.Color(202f / 255f, 204f / 255f, 210f / 255f, 0.98f);
        var statusColor = hasFailed
            ? EditorColors.WithAlpha(EditorColors.TextDisabled, 0.96f)
            : EditorColors.Color(108f / 255f, 108f / 255f, 108f / 255f, 0.96f);
        var shadowOffset = new Vector2(scale, scale);
        var headlineShadowColor = EditorColors.Color(0f, 0f, 0f, hasFailed ? 0.42f : 0.36f);
        var statusShadowColor = EditorColors.Color(0f, 0f, 0f, hasFailed ? 0.32f : 0.28f);
        var drawList = ImGui.GetWindowDrawList();
        var totalHeight = headlineSize.Y + statusSize.Y + verticalSpacing;
        var headlinePosition = new Vector2(
            contentMin.X + ((contentSize.X - headlineSize.X) * 0.5f),
            contentMin.Y + ((contentSize.Y - totalHeight) * 0.5f));
        var statusPosition = new Vector2(
            contentMin.X + ((contentSize.X - statusSize.X) * 0.5f),
            headlinePosition.Y + headlineSize.Y + verticalSpacing);

        drawList.AddText(headlineFont, headlineFontSize, headlinePosition + shadowOffset, ImGui.GetColorU32(headlineShadowColor), headline);
        drawList.AddText(statusFont, statusFontSize, statusPosition + shadowOffset, ImGui.GetColorU32(statusShadowColor), status);
        drawList.AddText(headlineFont, headlineFontSize, headlinePosition, ImGui.GetColorU32(headlineColor), headline);
        drawList.AddText(statusFont, statusFontSize, statusPosition, ImGui.GetColorU32(statusColor), status);
    }
}

