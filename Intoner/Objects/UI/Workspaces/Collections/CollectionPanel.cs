using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface;
using Intoner.Objects.UI.Components;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI;

internal sealed class CollectionPanel
{
    private readonly EditorOverlayLayer _editorOverlayLayer;
    internal const int ObjectCollectionFilterMaxLength = 128;

    public CollectionPanel(EditorOverlayLayer editorOverlayLayer)
    {
        _editorOverlayLayer = editorOverlayLayer;
    }

    internal static class CollectionModStyle
    {
        public const float RowHeight = 40f;
        public const float ActionSize = 26f;
        public const float PriorityWidth = 140f;

        public static Vector4 Row => ThemeColors.ButtonDefault with { W = 0.28f };
        public static Vector4 RowHover => Vector4.Lerp(ThemeColors.ButtonDefault, ThemeColors.AccentPrimary, 0.10f)
            with { W = 0.36f };
        public static Vector4 Settings => ThemeColors.ButtonDefault with { W = 0.12f };
        public static Vector4 Setting => ThemeColors.ButtonDefault with { W = 0.22f };
        public static Vector4 SettingChanged => Vector4.Lerp(ThemeColors.ButtonDefault, ThemeColors.AccentPrimary, 0.10f)
            with { W = 0.28f };
        public static Vector4 SettingHover => Vector4.Lerp(ThemeColors.ButtonDefault, ThemeColors.AccentPrimary, 0.08f)
            with { W = 0.34f };
        public static Vector4 Field => ThemeColors.ButtonDefault with { W = 0.34f };
        public static Vector4 FieldHover => Vector4.Lerp(ThemeColors.ButtonDefault, ThemeColors.AccentPrimary, 0.10f)
            with { W = 0.46f };
        public static Vector4 FieldBorder => ThemeColors.Border with { W = 0.44f };
    }

    internal static class ObjectCollectionPanelStyle
    {
        public const float HeaderHeight = 48f;
        public const float IconInset = 11f;
        public const float IconTextGap = 8f;
        public const float TitleInset = 7f;
        public const float SubtitleGap = 2f;

        public static Vector4 Panel => ObjectCollectionWorkspaceStyle.Panel;
        public static Vector4 Header => ThemeColors.ButtonDefault with { W = 0.20f };
        public static Vector4 Border => ObjectCollectionWorkspaceStyle.Border;
        public static Vector4 Divider => ObjectCollectionWorkspaceStyle.Divider;
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct ObjectCollectionPanelHeaderLayout(
        Vector2 Min,
        Vector2 Max,
        float TextStartX,
        float Height);

    internal static float DrawObjectCollectionPanelSearch(
        string id,
        string hint,
        ref string filter,
        int visibleCount,
        int totalCount,
        ObjectCollectionPanelHeaderLayout layout,
        float right)
    {
        float left = MathF.Max(layout.TextStartX + EditorLayout.Scaled(190f), right - EditorLayout.Scaled(260f));
        float width = right - left;
        if (width < EditorLayout.Scaled(120f))
        {
            if (filter.Length == 0)
            {
                return right;
            }

            float buttonSize = EditorLayout.Scaled(32f);
            float buttonLeft = right - buttonSize;
            ImGui.SetCursorScreenPos(new Vector2(buttonLeft, layout.Min.Y + ((layout.Height - buttonSize) * 0.5f)));
            if (EditorIconButton.DrawAccent(
                    id,
                    FontAwesomeIcon.Times,
                    $"Clear filter: {filter}",
                    ThemeColors.AccentPrimary,
                    buttonSize))
            {
                filter = string.Empty;
            }

            return buttonLeft;
        }

        float height = EditorLayout.Scaled(32f);
        string status = filter.Length > 0 ? $"{visibleCount}/{totalCount}" : string.Empty;
        ImGui.SetCursorScreenPos(new Vector2(left, layout.Min.Y + ((layout.Height - height) * 0.5f)));
        _ = EditorSearchField.Draw(
            id,
            ref filter,
            new EditorSearchFieldOptions(
                hint,
                ThemeColors.AccentPrimary,
                StatusText: status,
                MaxLength: ObjectCollectionFilterMaxLength,
                Width: width,
                Height: height));
        return left;
    }

    internal void DrawObjectCollectionPanel(
        string id,
        FontAwesomeIcon icon,
        string title,
        string subtitle,
        Vector4 accent,
        float width,
        float height,
        Func<ObjectCollectionPanelHeaderLayout, float> drawHeaderActions,
        Action<float> drawContent)
    {
        Vector2 startCursor = ImGui.GetCursorPos();
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 max = min + new Vector2(width, height);
        float rounding = EditorLayout.Scaled(ObjectCollectionWorkspaceStyle.PanelRounding);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(ObjectCollectionPanelStyle.Panel), rounding);

        float headerHeight = EditorLayout.Scaled(ObjectCollectionPanelStyle.HeaderHeight);
        Vector2 headerMax = new(max.X, min.Y + headerHeight);
        drawList.AddRectFilled(min, headerMax, ImGui.GetColorU32(ObjectCollectionPanelStyle.Header));
        drawList.AddLine(
            new Vector2(min.X, headerMax.Y),
            headerMax,
            ImGui.GetColorU32(ObjectCollectionPanelStyle.Divider),
            EditorLayout.Scaled(1f));

        EditorIcon.Metrics iconMetrics = EditorIcon.Measure(icon, 0.92f);
        float iconX = min.X + EditorLayout.Scaled(ObjectCollectionPanelStyle.IconInset);
        float textX = iconX + iconMetrics.Size.X + EditorLayout.Scaled(ObjectCollectionPanelStyle.IconTextGap);
        float titleY = min.Y + EditorLayout.Scaled(ObjectCollectionPanelStyle.TitleInset);
        EditorIcon.Draw(
            drawList,
            icon,
            iconMetrics,
            new Vector2(iconX, min.Y + ((headerHeight - iconMetrics.Size.Y) * 0.5f)),
            accent);
        float textRight = drawHeaderActions(new ObjectCollectionPanelHeaderLayout(min, headerMax, textX, headerHeight));
        float textWidth = MathF.Max(1f, textRight - textX - EditorLayout.Scaled(ObjectCollectionPanelStyle.IconTextGap));
        drawList.AddText(
            new Vector2(textX, titleY),
            ImGui.GetColorU32(ThemeColors.Text),
            EditorTextUtility.ClipTextToWidth(title, textWidth));
        drawList.AddText(
            new Vector2(textX, titleY + ImGui.GetTextLineHeight() + EditorLayout.Scaled(ObjectCollectionPanelStyle.SubtitleGap)),
            ImGui.GetColorU32(ThemeColors.TextDisabled),
            EditorTextUtility.ClipTextToWidth(subtitle, textWidth));

        float contentHeight = MathF.Max(0f, height - headerHeight);
        if (contentHeight > 0f)
        {
            ImGui.SetCursorScreenPos(new Vector2(min.X, headerMax.Y));
            using var childBackground = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
            using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            using var itemSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
            using var child = EditorScrollList.Begin(
                $"##{id}Scroll",
                new Vector2(width, contentHeight),
                _editorOverlayLayer.CreateScrollPanelOptions(ObjectCollectionPanelStyle.Panel, rounding, accent));
            if (child)
            {
                drawContent(contentHeight);
            }
        }

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(ObjectCollectionPanelStyle.Border),
            rounding,
            ImDrawFlags.None,
            EditorLayout.Scaled(1f));
        ImGui.SetCursorPos(new Vector2(startCursor.X, startCursor.Y + height));
    }

    internal static class ObjectCollectionWorkspaceStyle
    {
        public const float HeaderHeight = 62f;
        public const float HeaderPadding = 8f;
        public const float HeaderGap = 10f;
        public const float PickerMinWidth = 250f;
        public const float PickerMaxWidth = 310f;
        public const float HeaderActionHeight = 32f;
        public const float HeaderMenuGap = 7f;
        public const float TabsHeight = 39f;
        public const float TabsHorizontalInset = 7f;
        public const float TabsTopInset = 5f;
        public const float TabHeight = 29f;
        public const float TabGap = 3f;
        public const float PanelTopGap = 2f;
        public const float PanelHorizontalInset = 7f;
        public const float PanelBottomInset = 7f;
        public const float PanelRounding = 6f;
        public const float SurfaceRounding = 7f;

        public static Vector4 Surface => ThemeColors.ButtonDefault with { W = 0.14f };
        public static Vector4 Header => ThemeColors.ButtonDefault with { W = 0.24f };
        public static Vector4 Tabs => ThemeColors.ButtonDefault with { W = 0.10f };
        public static Vector4 Panel => ThemeColors.ButtonDefault with { W = 0.20f };
        public static Vector4 Border => Vector4.Lerp(ThemeColors.Border, ThemeColors.AccentPrimary, 0.20f)
            with { W = 0.62f };
        public static Vector4 Divider => Vector4.Lerp(ThemeColors.Separator, ThemeColors.AccentPrimary, 0.12f)
            with { W = 0.52f };
    }
}
