using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Assets;
using Intoner.Objects.Catalog;
using Intoner.Objects.Preview;
using Intoner.Objects.Preview.Rendering;
using Intoner.Objects.UI.Components;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class ObjectPreviewRenderer
{
    private readonly IObjectCatalogService _objectCatalog;
    private readonly ViewportService       _viewportService;
    private readonly EditorOverlayLayer    _editorOverlayLayer;

    public bool IsHovered { get; private set; }

    public ObjectPreviewRenderer(
        IObjectCatalogService objectCatalog,
        ViewportService viewportService,
        EditorOverlayLayer editorOverlayLayer)
    {
        _objectCatalog      = objectCatalog;
        _viewportService    = viewportService;
        _editorOverlayLayer = editorOverlayLayer;
    }

    internal void DrawPreviewSection(
        string id,
        string controlsText,
        ObjectCatalogKind kind,
        string assetPath,
        PreviewState state)
    {
        state.SyncAsset(assetPath);

        var accent = EditorColors.CatalogAccent(kind);
        var previewWidth  = ResolvePreviewHostWidth();
        var previewHeight = MathF.Max(
            180f * ImGuiHelpers.GlobalScale,
            MathF.Min(260f * ImGuiHelpers.GlobalScale, previewWidth * 0.66f));
        var request = state.CreateRequest(
            (int)MathF.Round(MathF.Max(96f, previewWidth - (24f * ImGuiHelpers.GlobalScale))),
            (int)MathF.Round(MathF.Max(96f, previewHeight - (24f * ImGuiHelpers.GlobalScale))));
        PreviewAsset? previewAsset = CatalogPreviewAssetFactory.TryCreate(_objectCatalog, kind, state.AssetPath, out PreviewAsset? resolvedAsset)
            ? resolvedAsset
            : null;

        DrawViewport(
            $"{id}-viewport",
            new Vector2(previewWidth, previewHeight),
            accent,
            controlsText,
            previewAsset,
            request,
            state);
    }

    private static float ResolvePreviewHostWidth()
        => MathF.Max(140f * ImGuiHelpers.GlobalScale, EditorScrollList.GetStableContentWidth());

    private void DrawViewport(
        string id,
        Vector2 viewportSize,
        Vector4 accent,
        string controlsText,
        PreviewAsset? previewAsset,
        PreviewRender.Request request,
        PreviewState state)
    {
        var width = MathF.Max(1f, viewportSize.X);
        var height = MathF.Max(1f, viewportSize.Y);

        var viewportRectSize = new Vector2(width, height);
        using var zeroPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var viewportChild = ImRaii.Child($"##{id}_viewport", viewportRectSize, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!viewportChild)
        {
            return;
        }

        var childHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        IsHovered |= childHovered;

        var viewportMin = ImGui.GetCursorScreenPos();
        var cursorBeforeViewport = ImGui.GetCursorPos();
        var drawList = ImGui.GetWindowDrawList();
        var viewportMax = viewportMin + viewportRectSize;
        var rounding = 14f * ImGuiHelpers.GlobalScale;
        var imageMin = viewportMin;
        var imageMax      = viewportMax;
        var imageRounding = rounding;
        using var zeroItemSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        ImGui.InvisibleButton($"##{id}", viewportRectSize);
        ImGui.SetItemAllowOverlap();
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var cursorAfterViewport = cursorBeforeViewport + viewportRectSize;

        PreviewRender.Result preview = new(null, false, null);
        bool drewGpuPreview = false;
        if (previewAsset is not null)
        {
            drewGpuPreview = _viewportService.TryDrawPreview(
                drawList,
                previewAsset,
                request,
                imageMin,
                imageMax,
                imageRounding,
                out preview);
        }

        if (!drewGpuPreview)
        {
            var placeholderBackground = PreviewRender.BackgroundPalette.GetPlaceholderFill(state.BackgroundStyle);
            drawList.AddRectFilled(
                imageMin,
                imageMax,
                ImGui.GetColorU32(placeholderBackground),
                imageRounding);
            var placeholder = "select to preview";
            if (preview.IsLoading)
            {
                placeholder = "Loading preview...";
            }
            else if (!string.IsNullOrWhiteSpace(preview.Error))
            {
                placeholder = preview.Error;
            }

            DrawCenteredPreviewMessage(imageMin, imageMax, placeholder, accent);
        }

        var controlsSize = ImGui.CalcTextSize(controlsText);
        var controlsPosition = new Vector2(
            imageMin.X + MathF.Max(0f, ((imageMax.X - imageMin.X) - controlsSize.X) * 0.5f),
            imageMin.Y + (10f * ImGuiHelpers.GlobalScale));
        var controlsShadowColor = ThemeColors.Color(0f, 0f, 0f, 0.55f);
        drawList.AddText(
            controlsPosition + new Vector2(1f * ImGuiHelpers.GlobalScale, 1f * ImGuiHelpers.GlobalScale),
            ImGui.GetColorU32(controlsShadowColor),
            controlsText);
        drawList.AddText(
            controlsPosition,
            ImGui.GetColorU32(ThemeColors.TextDisabled),
            controlsText);
        drawList.AddRect(
            imageMin,
            imageMax,
            ImGui.GetColorU32((hovered || active) ? accent with { W = 0.80f } : accent with { W = 0.34f }),
            imageRounding,
            ImDrawFlags.None,
            1.25f * ImGuiHelpers.GlobalScale);

        var backgroundButtonsHovered = DrawPreviewBackgroundButtons($"{id}-background", imageMin, imageMax, imageRounding, accent, state);
        ImGui.SetCursorPos(cursorAfterViewport);
        _editorOverlayLayer.CaptureCurrentWindow();

        if (hovered && !backgroundButtonsHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                state.ResetView();
            }
            else if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                state.Orbit(ImGui.GetIO().MouseDelta);
            }

            var wheelDelta = ImGui.GetIO().MouseWheel;
            if (MathF.Abs(wheelDelta) > 0f)
            {
                state.ZoomBy(wheelDelta);
                ImGui.SetNextFrameWantCaptureMouse(true);
                return;
            }
        }
    }

    private static bool DrawPreviewBackgroundButtons(
        string id,
        Vector2 imageMin,
        Vector2 imageMax,
        float imageRounding,
        Vector4 accent,
        PreviewState state)
    {
        var swatchSize = new Vector2(20f, 20f) * ImGuiHelpers.GlobalScale;
        var padding = 12f * ImGuiHelpers.GlobalScale;
        var spacing = 8f * ImGuiHelpers.GlobalScale;
        var buttonY = imageMax.Y - padding - swatchSize.Y;
        var firstButtonX = imageMin.X + padding;
        var hoveredAny = false;

        hoveredAny |= DrawPreviewBackgroundButton(
            $"{id}-white",
            new Vector2(firstButtonX, buttonY),
            swatchSize,
            imageRounding,
            PreviewRender.BackgroundPalette.GetSwatchFill(PreviewRender.BackgroundStyle.White),
            state.BackgroundStyle == PreviewRender.BackgroundStyle.White,
            accent,
            "light",
            () => state.BackgroundStyle = PreviewRender.BackgroundStyle.White);

        hoveredAny |= DrawPreviewBackgroundButton(
            $"{id}-dark",
            new Vector2(firstButtonX + swatchSize.X + spacing, buttonY),
            swatchSize,
            imageRounding,
            PreviewRender.BackgroundPalette.GetSwatchFill(PreviewRender.BackgroundStyle.DarkBlue),
            state.BackgroundStyle == PreviewRender.BackgroundStyle.DarkBlue,
            accent,
            "dark",
            () => state.BackgroundStyle = PreviewRender.BackgroundStyle.DarkBlue);

        return hoveredAny;
    }

    private static bool DrawPreviewBackgroundButton(
        string id,
        Vector2 screenPosition,
        Vector2 size,
        float imageRounding,
        Vector4 fillColor,
        bool selected,
        Vector4 accent,
        string tooltip,
        Action onClick)
    {
        var drawList = ImGui.GetWindowDrawList();
        var framePadding = 2f * ImGuiHelpers.GlobalScale;
        var buttonRounding = MathF.Max(4f * ImGuiHelpers.GlobalScale, imageRounding * 0.35f);
        var outerMin = screenPosition;
        var outerMax = screenPosition + size;
        var innerMin = outerMin + new Vector2(framePadding);
        var innerMax = outerMax - new Vector2(framePadding);

        ImGui.SetCursorScreenPos(screenPosition);
        ImGui.InvisibleButton($"##{id}", size);
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem | ImGuiHoveredFlags.AllowWhenOverlapped);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            IntonerTooltip.Attach(tooltip);
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            onClick();
        }

        Vector4 outlineColor;
        if (selected)
        {
            outlineColor = accent with { W = 0.90f };
        }
        else if (hovered)
        {
            outlineColor = ThemeColors.Color(1f, 1f, 1f, 0.70f);
        }
        else
        {
            outlineColor = ThemeColors.Color(1f, 1f, 1f, 0.38f);
        }

        drawList.AddRectFilled(outerMin, outerMax, ImGui.GetColorU32(ThemeColors.Color(0f, 0f, 0f, 0.28f)), buttonRounding);
        drawList.AddRectFilled(innerMin, innerMax, ImGui.GetColorU32(fillColor), MathF.Max(2f * ImGuiHelpers.GlobalScale, buttonRounding - framePadding));
        drawList.AddRect(outerMin, outerMax, ImGui.GetColorU32(outlineColor), buttonRounding, ImDrawFlags.None, selected ? 1.8f * ImGuiHelpers.GlobalScale : 1.1f * ImGuiHelpers.GlobalScale);
        return hovered;
    }

    private static void DrawCenteredPreviewMessage(Vector2 min, Vector2 max, string text, Vector4 accent)
    {
        var padding = 16f * ImGuiHelpers.GlobalScale;
        var wrapWidth = MathF.Max(1f, (max.X - min.X) - (padding * 2f));
        var singleLineSize = ImGui.CalcTextSize(text);
        var useWrappedText = singleLineSize.X > wrapWidth || text.Contains('\n');
        var textSize = useWrappedText
            ? ImGui.CalcTextSize(text, wrapWidth: wrapWidth)
            : singleLineSize;
        var position = new Vector2(
            useWrappedText
                ? min.X + padding
                : min.X + MathF.Max(0f, ((max.X - min.X) - textSize.X) * 0.5f),
            min.Y + MathF.Max(0f, ((max.Y - min.Y) - textSize.Y) * 0.5f));

        ImGui.SetCursorScreenPos(position);
        using var color = ImRaii.PushColor(ImGuiCol.Text, accent with { W = 0.82f });
        if (useWrappedText)
        {
            using var textWrap = ImRaiiScope.TextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
            ImGui.TextWrapped(text);
        }
        else
        {
            ImGui.TextUnformatted(text);
        }

        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));
    }

    internal sealed class PreviewState
    {
        private const float DefaultYaw = -0.85f;
        private const float DefaultPitch = 0.34f;
        private const float DefaultZoom = 1.00f;

        public string AssetPath { get; private set; } = string.Empty;
        public float Yaw { get; private set; } = DefaultYaw;
        public float Pitch { get; private set; } = DefaultPitch;
        public float Zoom { get; private set; } = DefaultZoom;
        public PreviewRender.BackgroundStyle BackgroundStyle { get; set; } = PreviewRender.BackgroundStyle.White;

        public void SyncAsset(string assetPath)
        {
            var normalizedPath = GameAssetPathRules.NormalizeGamePath(assetPath);
            if (string.Equals(AssetPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AssetPath = normalizedPath;
            ResetView();
        }

        public void Orbit(Vector2 delta)
        {
            Yaw += delta.X * 0.014f;
            Pitch = Math.Clamp(Pitch - (delta.Y * 0.014f), -1.10f, 1.10f);
        }

        public void ZoomBy(float wheelDelta)
        {
            var scale = 1f - (wheelDelta * 0.10f);
            if (scale <= 0.05f)
            {
                scale = 0.05f;
            }

            Zoom = Math.Clamp(Zoom * scale, 0.65f, 2.40f);
        }

        public void ResetView()
        {
            Yaw = DefaultYaw;
            Pitch = DefaultPitch;
            Zoom = DefaultZoom;
        }

        public PreviewRender.Request CreateRequest(int width, int height, PreviewRender.Mode mode = PreviewRender.Mode.Detail)
            => new(
                Math.Clamp(width, 96, 640),
                Math.Clamp(height, 96, 480),
                (int)MathF.Round(Yaw * 100f),
                (int)MathF.Round(Pitch * 100f),
                (int)MathF.Round(Zoom * 100f),
                BackgroundStyle,
                mode);
    }

    public void ResetHover()
        => IsHovered = false;
}
