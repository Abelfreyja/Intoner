using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Utility;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services.Backdrop;
using System.Globalization;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
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

    private enum SplashScreenLayoutDetail
    {
        ObjectCount,
        ModifiedAt,
    }

    private readonly record struct SplashScreenActions(
        IReadOnlyList<SplashScreenActionSection> Sections,
        IReadOnlyList<SplashScreenActionItem> LayoutPickerItems);

    private readonly string _splashScreenVersionLabel;
    private bool _splashScreenVisible = true;
    private bool _splashScreenManualOpen;

    public override void OnClose()
    {
        ResetSplashScreen();
        base.OnClose();
    }

    private bool ShouldShowSplashScreen()
        => _splashScreenVisible
           && (_splashScreenManualOpen || _objectConfigurationService.Current.Ui.ShowSplashScreenOnStartup);

    private void ShowSplashScreen()
    {
        _splashScreenVisible = true;
        _splashScreenManualOpen = true;
        CloseSplashScreenLayoutPicker();
        SetSplashScreenStatus(null, isError: false);
    }

    private void ResetSplashScreen()
    {
        _splashScreenVisible = true;
        _splashScreenManualOpen = false;
        CloseSplashScreenLayoutPicker();
        SetSplashScreenStatus(null, isError: false);
    }

    private void DrawSplashScreen()
    {
        if (!TryGetWindowBodyRect(out Vector2 overlayMin, out Vector2 overlayMax, out float rounding, out ImDrawFlags roundingFlags))
        {
            return;
        }

        Vector2 overlaySize = overlayMax - overlayMin;
        if (overlaySize.X <= 1f || overlaySize.Y <= 1f)
        {
            return;
        }

        float scale = ImGuiHelpers.GlobalScale;
        Vector2 cardSize = ResolveSplashScreenSize(overlaySize, scale);
        Vector2 cardMin = overlayMin + ((overlaySize - cardSize) * 0.5f);

        SplashScreenActionRequest? request = DrawSplashScreenOverlay(overlayMin, overlayMax, cardMin, cardSize, rounding, roundingFlags, scale, out bool blockDismiss);
        if (request is not null)
        {
            blockDismiss = true;
            HandleSplashScreenAction(request.Value);
        }

        HandleSplashScreenDismissClick(overlayMin, overlayMax, blockDismiss);
    }

    private SplashScreenActionRequest? DrawSplashScreenOverlay(
        Vector2 overlayMin,
        Vector2 overlayMax,
        Vector2 cardMin,
        Vector2 cardSize,
        float rounding,
        ImDrawFlags roundingFlags,
        float scale,
        out bool blockDismiss)
    {
        SplashScreenActionRequest? request = null;
        bool localBlockDismiss = false;
        DrawEditorOverlayClipped(overlayMin, overlayMax, drawList =>
        {
            DrawSplashScreenBackdrop(drawList, overlayMin, overlayMax, rounding, roundingFlags);
            request = DrawSplashScreenCard(drawList, cardMin, cardSize, scale, out localBlockDismiss);
        });

        blockDismiss = localBlockDismiss;
        return request;
    }

    private void DrawSplashScreenBackdrop(
        ImDrawListPtr drawList,
        Vector2 overlayMin,
        Vector2 overlayMax,
        float rounding,
        ImDrawFlags roundingFlags)
    {
        drawList.AddRectFilled(
            overlayMin,
            overlayMax,
            ImGui.GetColorU32(EditorColors.Color(0.015f, 0.016f, 0.020f, 0.54f)),
            rounding,
            roundingFlags);
    }

    private Vector2 ResolveSplashScreenSize(Vector2 overlaySize, float scale)
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
        Vector4 cardFill = EditorColors.WithAlpha(_windowBodyBackgroundColor, 0.98f);
        Vector4 bannerFill = Vector4.Lerp(cardFill, EditorColors.ButtonDefault, 0.26f) with { W = 0.98f };

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
            ImGui.GetColorU32(EditorColors.WithAlpha(EditorColors.Separator, 0.55f)),
            borderThickness);
        drawList.AddRect(
            cardMin,
            cardMax,
            ImGui.GetColorU32(EditorColors.WithAlpha(EditorColors.Border, 0.68f)),
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
        Vector4 titleColor = EditorColors.WithAlpha(EditorColors.Text, 0.98f);
        Vector4 metaColor = EditorColors.Color(1f, 1f, 1f, 0.94f);
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

    private SplashScreenActionRequest? DrawSplashScreenLayoutPicker(
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
            ? EditorColors.WithAlpha(EditorColors.DimRed, 0.92f)
            : EditorColors.WithAlpha(EditorColors.AccentGreen, 0.90f);
        string status = EditorTextUtility.ClipTextToWidth(_splashScreenStatusMessage, contentMax.X - contentMin.X);
        DrawCenteredSplashScreenText(drawList, status, contentMin.X, contentMax.X, contentMax.Y - hintHeight - statusHeight, color);
    }

    private void DrawSplashScreenHint(ImDrawListPtr drawList, Vector2 bodyMin, Vector2 cardMax, float padding)
    {
        const string hint = "click anywhere to close";
        DrawCenteredSplashScreenText(
            drawList,
            hint,
            bodyMin.X,
            cardMax.X,
            cardMax.Y - padding - ImGui.CalcTextSize(hint).Y,
            EditorColors.WithAlpha(EditorColors.TextDisabled, 0.82f));
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
                EditorColors.AccentPurple,
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
                EditorColors.AccentBlue,
                Enabled: layoutCount > 0),
            new(
                SplashScreenActionKind.RecoverLastSession,
                FontAwesomeIcon.History,
                "Recover Last Session",
                hasRecovery ? "available" : "no autosave",
                EditorColors.AccentGreen,
                Enabled: hasRecovery),
            new(
                SplashScreenActionKind.Redirect,
                FontAwesomeIcon.Heart,
                SplashScreenRedirectLabel,
                SplashScreenRedirectDetail,
                EditorColors.DimRed,
                Enabled: !string.IsNullOrWhiteSpace(SplashScreenRedirectUrl),
                Tooltip: SplashScreenRedirectUrl),
        ];

    private static SplashScreenActionItem CreateLayoutActionItem(ObjectLayoutSnapshot layout, Guid? defaultLayoutId, SplashScreenLayoutDetail detailKind)
    {
        string detail = detailKind == SplashScreenLayoutDetail.ModifiedAt
            ? layout.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
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
            EditorColors.AccentPurple,
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
        if (defaultLayoutId.HasValue && defaultLayoutId.Value != layoutId.Value && !_objectManager.TrySelectLayout(null))
        {
            SetSplashScreenStatus("Failed to unload the current layout.", isError: true);
            return;
        }

        if (!_objectManager.TrySelectLayout(layoutId.Value))
        {
            SetSplashScreenStatus("Failed to load layout. Check housing policy validity.", isError: true);
            return;
        }

        _selectedLayoutId = layoutId.Value;
        HandleSelectionChanged(_editorSelection.TryClear());
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

        _selectedLayoutId = _layoutManager.GetDefaultLayoutId();
        HandleSelectionChanged(_editorSelection.TryClear());
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

    private void SetSplashScreenStatus(string? message, bool isError)
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

    private void HandleSplashScreenDismissClick(Vector2 overlayMin, Vector2 overlayMax, bool blocked)
    {
        if (!blocked
            && EditorInputUtility.IsAnyMouseClickedInside(overlayMin, overlayMax))
        {
            DismissSplashScreen();
        }
    }

    private void DismissSplashScreen()
    {
        _splashScreenVisible = false;
        _splashScreenManualOpen = false;
        CloseSplashScreenLayoutPicker();
    }

    private void CloseSplashScreenLayoutPicker()
        => _splashScreenLayoutPickerOpen = false;

}

