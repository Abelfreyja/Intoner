using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Utility;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services.Backdrop;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI;

internal sealed partial class EditorStartupView
{
    private const string SplashScreenRedirectUrl = "https://abelfreyja.xyz/";
    private const string SplashScreenRedirectLabel = "goober";
    private const string SplashScreenRedirectDetail = "abelfreyja.xyz";
    private const float SplashScreenWidth = 340f;
    private const float SplashScreenHeight = 438f;
    private const float SplashScreenBannerHeight = 176f;
    private const float SplashScreenPadding = 20f;
    private const float SplashScreenStatusHeight = 20f;
    private const float SplashScreenPickerHeight = 190f;
    private const int SplashScreenRecentLayoutCount = 3;
    private readonly string _splashScreenVersionLabel;
    private bool _splashScreenVisible = true;
    private bool _splashScreenManualOpen;

    internal enum SplashScreenLayoutDetail
    {
        ObjectCount,
        ModifiedAt,
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct SplashScreenActions(
        IReadOnlyList<SplashScreenActionSection> Sections,
        IReadOnlyList<SplashScreenActionItem> LayoutPickerItems);

    internal bool ShouldShowSplashScreen()
        => !_interaction.Dialog.IsOpen
           && _splashScreenVisible
           && (_splashScreenManualOpen || _configurationService.Current.Ui.ShowSplashScreenOnStartup);

    internal void ShowSplashScreen()
    {
        _interaction.Dialog.Dismiss();
        _splashScreenVisible = true;
        _splashScreenManualOpen = true;
        CloseSplashScreenLayoutPicker();
        SetSplashScreenStatus(null, isError: false);
    }

    internal void DrawSplashScreen()
    {
        if (!EditorLayout.TryGetWindowBodyArea(out EditorOverlayArea area))
        {
            return;
        }

        Vector2 overlaySize = area.Size;
        if (overlaySize.X <= 1f || overlaySize.Y <= 1f)
        {
            return;
        }

        float scale = ImGuiHelpers.GlobalScale;
        Vector2 cardSize = ResolveSplashScreenSize(overlaySize, scale);
        Vector2 cardMin = area.Min + ((overlaySize - cardSize) * 0.5f);

        SplashScreenActionRequest? request = DrawSplashScreenOverlay(area, cardMin, cardSize, scale, out bool blockDismiss);
        if (request is not null)
        {
            blockDismiss = true;
            HandleSplashScreenAction(request.Value);
        }

        HandleSplashScreenDismissClick(area, blockDismiss);
    }

    private SplashScreenActionRequest? DrawSplashScreenOverlay(
        EditorOverlayArea area,
        Vector2 cardMin,
        Vector2 cardSize,
        float scale,
        out bool blockDismiss)
    {
        SplashScreenActionRequest? request = null;
        bool localBlockDismiss = false;
        _editorOverlayLayer.DrawClipped(area, drawList =>
        {
            DrawSplashScreenBackdrop(drawList, area);
            request = DrawSplashScreenCard(drawList, cardMin, cardSize, scale, out localBlockDismiss);
        });

        blockDismiss = localBlockDismiss;
        return request;
    }

    private static void DrawSplashScreenBackdrop(ImDrawListPtr drawList, EditorOverlayArea area)
    {
        drawList.AddRectFilled(
            area.Min,
            area.Max,
            ImGui.GetColorU32(ThemeColors.Color(0.015f, 0.016f, 0.020f, 0.54f)),
            area.Rounding,
            area.RoundingFlags);
    }

    private static Vector2 ResolveSplashScreenSize(Vector2 overlaySize, float scale)
    {
        float horizontalInset = MathF.Min(48f * scale, overlaySize.X * 0.12f);
        float verticalInset = MathF.Min(46f * scale, overlaySize.Y * 0.12f);
        return new Vector2(
            MathF.Min(SplashScreenWidth * scale, MathF.Max(1f, overlaySize.X - (horizontalInset * 2f))),
            MathF.Min(SplashScreenHeight * scale, MathF.Max(1f, overlaySize.Y - (verticalInset * 2f))));
    }

    private SplashScreenActionRequest? DrawSplashScreenCard(
        ImDrawListPtr drawList,
        Vector2 cardMin,
        Vector2 cardSize,
        float scale,
        out bool blockDismiss)
    {
        Vector2 cardMax = cardMin + cardSize;
        float rounding = 5f * scale;
        float borderThickness = MathF.Max(1f, scale);
        float bannerHeight = MathF.Min(SplashScreenBannerHeight * scale, cardSize.Y * 0.62f);
        Vector2 bannerMax = new(cardMax.X, cardMin.Y + bannerHeight);
        Vector2 bodyMin = new(cardMin.X, bannerMax.Y);
        Vector4 cardFill = ThemeColors.WithAlpha(_editorOverlayLayer.BackgroundColor, 0.98f);
        Vector4 bannerFill = Vector4.Lerp(cardFill, ThemeColors.ButtonDefault, 0.26f) with { W = 0.98f };

        drawList.AddRectFilled(
            cardMin,
            cardMax,
            ImGui.GetColorU32(cardFill),
            rounding,
            ImDrawFlags.RoundCornersAll);
        drawList.AddRectFilled(
            cardMin,
            bannerMax,
            ImGui.GetColorU32(bannerFill),
            rounding,
            ImDrawFlags.RoundCornersTop);
        _windowBackdropRenderer.GetEffect<SplashScreenBannerEffect>().DrawRegion(
            drawList,
            cardMin,
            bannerMax,
            rounding,
            new SplashScreenBannerEffect.Style
            {
                TimeSeconds = (float)ImGui.GetTime(),
            },
            ImDrawFlags.RoundCornersTop);
        drawList.AddLine(
            new Vector2(cardMin.X, bodyMin.Y),
            new Vector2(cardMax.X, bodyMin.Y),
            ImGui.GetColorU32(ThemeColors.WithAlpha(ThemeColors.Separator, 0.55f)),
            borderThickness);
        drawList.AddRect(
            cardMin,
            cardMax,
            ImGui.GetColorU32(ThemeColors.WithAlpha(ThemeColors.Border, 0.68f)),
            rounding,
            ImDrawFlags.RoundCornersAll,
            borderThickness);
        DrawSplashScreenBannerText(drawList, cardMin, cardMax, scale);
        return DrawSplashScreenBody(drawList, cardMin, bodyMin, cardMax, scale, out blockDismiss);
    }

    private void DrawSplashScreenBannerText(
        ImDrawListPtr drawList,
        Vector2 cardMin,
        Vector2 cardMax,
        float scale)
    {
        float padding = SplashScreenPadding * scale;
        Vector2 titlePos = cardMin + new Vector2(padding, 30f * scale);
        Vector2 metaPos = new(cardMax.X - padding, cardMin.Y + 28f * scale);
        Vector4 titleColor = ThemeColors.WithAlpha(ThemeColors.Text, 0.98f);
        Vector4 metaColor = ThemeColors.Color(1f, 1f, 1f, 0.94f);
        string versionText = string.IsNullOrWhiteSpace(_splashScreenVersionLabel)
            ? "unknown"
            : _splashScreenVersionLabel;

        using (_uiSharedService.MediumFont.Push())
        {
            drawList.AddText(titlePos, ImGui.GetColorU32(titleColor), "Intoner");
        }

        string versionLabel = $"v{versionText}";
        DrawRightAlignedSplashScreenText(drawList, versionLabel, metaPos, metaColor);
        DrawRightAlignedSplashScreenText(drawList, "abelfreyja", metaPos + new Vector2(0f, ImGui.GetTextLineHeightWithSpacing()), metaColor);
    }

    private SplashScreenActionRequest? DrawSplashScreenBody(
        ImDrawListPtr drawList,
        Vector2 cardMin,
        Vector2 bodyMin,
        Vector2 cardMax,
        float scale,
        out bool blockDismiss)
    {
        float padding = SplashScreenPadding * scale;
        Vector2 contentMin = bodyMin + new Vector2(padding, 14f * scale);
        Vector2 contentMax = cardMax - new Vector2(padding, padding);
        float hintHeight = ImGui.CalcTextSize("click anywhere to close").Y;
        float statusHeight = string.IsNullOrWhiteSpace(_splashScreenStatusMessage)
            ? 0f
            : SplashScreenStatusHeight * scale;
        Vector2 listMax = new(
            contentMax.X,
            contentMax.Y - hintHeight - (10f * scale) - statusHeight);

        SplashScreenActions actions = BuildSplashScreenActions();
        SplashScreenActionRequest? request = SplashScreenActionList.Draw(
            drawList,
            contentMin,
            listMax,
            actions.Sections,
            scale,
            out bool actionsHovered);
        blockDismiss = actionsHovered;

        if (_splashScreenLayoutPickerOpen)
        {
            SplashScreenActionRequest? pickerRequest = DrawSplashScreenLayoutPicker(drawList, cardMin, bodyMin, cardMax, actions.LayoutPickerItems, scale, out bool pickerHovered);
            request ??= pickerRequest;
            blockDismiss |= pickerHovered;
            if (EditorInputUtility.IsMouseClickedInside(cardMin, cardMax) && !blockDismiss)
            {
                CloseSplashScreenLayoutPicker();
                blockDismiss = true;
            }
        }

        DrawSplashScreenStatus(drawList, contentMin, contentMax, statusHeight, hintHeight);
        DrawSplashScreenHint(drawList, bodyMin, cardMax, padding);
        return request;
    }

    private static SplashScreenActionRequest? DrawSplashScreenLayoutPicker(
        ImDrawListPtr drawList,
        Vector2 cardMin,
        Vector2 bodyMin,
        Vector2 cardMax,
        IReadOnlyList<SplashScreenActionItem> layouts,
        float scale,
        out bool hovered)
    {
        float padding = SplashScreenPadding * scale;
        float pickerHeight = MathF.Min(SplashScreenPickerHeight * scale, MathF.Max(1f, cardMax.Y - bodyMin.Y - (padding * 2f)));
        Vector2 pickerMin = new(cardMin.X + padding, bodyMin.Y + (12f * scale));
        Vector2 pickerMax = new(cardMax.X - padding, pickerMin.Y + pickerHeight);
        return SplashScreenLayoutPicker.Draw(
            drawList,
            pickerMin,
            pickerMax,
            layouts,
            scale,
            out hovered);
    }

    private void DrawSplashScreenStatus(
        ImDrawListPtr drawList,
        Vector2 contentMin,
        Vector2 contentMax,
        float statusHeight,
        float hintHeight)
    {
        if (statusHeight <= 0f)
        {
            return;
        }

        Vector4 color = _splashScreenStatusIsError
            ? ThemeColors.WithAlpha(ThemeColors.DimRed, 0.92f)
            : ThemeColors.WithAlpha(ThemeColors.AccentGreen, 0.90f);
        string status = EditorTextUtility.ClipTextToWidth(_splashScreenStatusMessage, contentMax.X - contentMin.X);
        DrawCenteredSplashScreenText(drawList, status, contentMin.X, contentMax.X, contentMax.Y - hintHeight - statusHeight, color);
    }

    private static void DrawSplashScreenHint(ImDrawListPtr drawList, Vector2 bodyMin, Vector2 cardMax, float padding)
    {
        const string hint = "click anywhere to close";
        DrawCenteredSplashScreenText(
            drawList,
            hint,
            bodyMin.X,
            cardMax.X,
            cardMax.Y - padding - ImGui.CalcTextSize(hint).Y,
            ThemeColors.WithAlpha(ThemeColors.TextDisabled, 0.82f));
    }

    private SplashScreenActions BuildSplashScreenActions()
    {
        IReadOnlyList<ObjectLayoutSnapshot> layouts = _layoutManager.GetLayouts()
            .OrderByDescending(static layout => layout.UpdatedAtUtc)
            .ThenBy(static layout => layout.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Guid? defaultLayoutId = _layoutManager.GetDefaultLayoutId();
        bool hasRecovery = _objectLayoutRecoveryService.HasCurrentRecovery();
        List<SplashScreenActionItem> recentLayoutItems = layouts
            .Take(SplashScreenRecentLayoutCount)
            .Select(layout => CreateLayoutActionItem(layout, defaultLayoutId, SplashScreenLayoutDetail.ObjectCount))
            .ToList();
        if (recentLayoutItems.Count == 0)
        {
            recentLayoutItems.Add(new SplashScreenActionItem(
                SplashScreenActionKind.Layout,
                FontAwesomeIcon.FolderOpen,
                "No recent layouts",
                "none saved",
                ThemeColors.AccentPrimary,
                Enabled: false));
        }

        return new SplashScreenActions(
        [
            new SplashScreenActionSection("Recent Layouts", recentLayoutItems),
            new SplashScreenActionSection("Other", BuildSplashScreenUtilityActions(layouts.Count, hasRecovery)),
        ],
        layouts.Select(layout => CreateLayoutActionItem(layout, defaultLayoutId, SplashScreenLayoutDetail.ModifiedAt)).ToList());
    }

    private static IReadOnlyList<SplashScreenActionItem> BuildSplashScreenUtilityActions(int layoutCount, bool hasRecovery)
        =>
        [
            new(
                SplashScreenActionKind.OpenLayouts,
                FontAwesomeIcon.FolderOpen,
                "Open..",
                layoutCount == 0 ? "no layouts" : $"{layoutCount} saved",
                ThemeColors.AccentBlue,
                Enabled: layoutCount > 0),
            new(
                SplashScreenActionKind.RecoverLastSession,
                FontAwesomeIcon.History,
                "Recover Last Session",
                hasRecovery ? "available" : "no autosave",
                ThemeColors.AccentGreen,
                Enabled: hasRecovery),
            new(
                SplashScreenActionKind.Redirect,
                FontAwesomeIcon.Heart,
                SplashScreenRedirectLabel,
                SplashScreenRedirectDetail,
                ThemeColors.DimRed,
                Enabled: !string.IsNullOrWhiteSpace(SplashScreenRedirectUrl),
                Tooltip: SplashScreenRedirectUrl),
        ];

    private static SplashScreenActionItem CreateLayoutActionItem(ObjectLayoutSnapshot layout, Guid? defaultLayoutId, SplashScreenLayoutDetail detailKind)
    {
        string detail = detailKind == SplashScreenLayoutDetail.ModifiedAt
            ? EditorTimestampFormatter.FormatToMinute(layout.UpdatedAtUtc)
            : $"{layout.Objects.Count} objects";
        if (defaultLayoutId == layout.Id)
        {
            detail = string.IsNullOrWhiteSpace(detail)
                ? "default"
                : $"{detail} | default";
        }

        return new SplashScreenActionItem(
            SplashScreenActionKind.Layout,
            FontAwesomeIcon.FolderOpen,
            layout.Name,
            detail,
            ThemeColors.AccentPrimary,
            layout.Id);
    }

    private void HandleSplashScreenAction(SplashScreenActionRequest request)
    {
        switch (request.Kind)
        {
            case SplashScreenActionKind.Layout:
                TryOpenSplashScreenLayout(request.LayoutId);
                break;
            case SplashScreenActionKind.OpenLayouts:
                _splashScreenLayoutPickerOpen = !_splashScreenLayoutPickerOpen;
                SetSplashScreenStatus(null, isError: false);
                break;
            case SplashScreenActionKind.CloseLayouts:
                CloseSplashScreenLayoutPicker();
                SetSplashScreenStatus(null, isError: false);
                break;
            case SplashScreenActionKind.RecoverLastSession:
                TryRecoverSplashScreenSession();
                break;
            case SplashScreenActionKind.Redirect:
                OpenSplashScreenRedirect();
                break;
        }
    }

    private void TryOpenSplashScreenLayout(Guid? layoutId)
    {
        if (!layoutId.HasValue)
        {
            SetSplashScreenStatus("No layout was selected.", isError: true);
            return;
        }

        Guid? defaultLayoutId = _layoutManager.GetDefaultLayoutId();
        if (defaultLayoutId.HasValue
            && defaultLayoutId.Value != layoutId.Value
            && !_objectManager.SelectLayout(null).IsAccepted)
        {
            SetSplashScreenStatus("Failed to unload the current layout.", isError: true);
            return;
        }

        if (!_objectManager.SelectLayout(layoutId.Value).IsAccepted)
        {
            SetSplashScreenStatus("Failed to load layout. Check housing policy validity.", isError: true);
            return;
        }

        _layouts.SelectedLayoutId = layoutId.Value;
        _interaction.SelectionChanged(_interaction.Selection.TryClear());
        DismissSplashScreen();
    }

    private void TryRecoverSplashScreenSession()
    {
        if (!_objectLayoutRecoveryService.TryLoadCurrentRecovery(out ObjectPersistentWorkspaceSnapshot workspace, out string message)
            || !_objectManager.TryRecoverWorkspace(workspace, out message))
        {
            SetSplashScreenStatus(message, isError: true);
            return;
        }

        _layouts.SelectedLayoutId = _layoutManager.GetDefaultLayoutId();
        _interaction.SelectionChanged(_interaction.Selection.TryClear());
        DismissSplashScreen();
    }

    private void OpenSplashScreenRedirect()
    {
        if (string.IsNullOrWhiteSpace(SplashScreenRedirectUrl))
        {
            SetSplashScreenStatus("Redirect link is not configured.", isError: true);
            return;
        }

        Util.OpenLink(SplashScreenRedirectUrl);
        DismissSplashScreen();
    }

    internal void SetSplashScreenStatus(string? message, bool isError)
    {
        _splashScreenStatusMessage = string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : message;
        _splashScreenStatusIsError = isError;
    }

    private static void DrawRightAlignedSplashScreenText(ImDrawListPtr drawList, string text, Vector2 maxPos, Vector4 color)
    {
        Vector2 textSize = ImGui.CalcTextSize(text);
        drawList.AddText(maxPos - new Vector2(textSize.X, 0f), ImGui.GetColorU32(color), text);
    }

    private static void DrawCenteredSplashScreenText(
        ImDrawListPtr drawList,
        string text,
        float minX,
        float maxX,
        float y,
        Vector4 color)
    {
        Vector2 textSize = ImGui.CalcTextSize(text);
        drawList.AddText(new Vector2(minX + ((maxX - minX - textSize.X) * 0.5f), y), ImGui.GetColorU32(color), text);
    }

    private void HandleSplashScreenDismissClick(EditorOverlayArea area, bool blocked)
    {
        if (!blocked
            && EditorInputUtility.IsAnyMouseClickedInside(area.Min, area.Max))
        {
            DismissSplashScreen();
        }
    }

    internal void DismissSplashScreen()
    {
        _splashScreenVisible = false;
        _splashScreenManualOpen = false;
        CloseSplashScreenLayoutPicker();
    }

    private void CloseSplashScreenLayoutPicker()
        => _splashScreenLayoutPickerOpen = false;
}
