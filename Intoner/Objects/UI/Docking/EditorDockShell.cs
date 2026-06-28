using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.Objects.UI.Docking;

/// <summary> lays out editor content around reusable dock panels/sections </summary>
internal static class EditorDockShell
{
    private const ImGuiTableFlags CenterTableFlags = ImGuiTableFlags.SizingFixedFit
        | ImGuiTableFlags.NoHostExtendY
        | ImGuiTableFlags.NoPadInnerX
        | ImGuiTableFlags.NoPadOuterX;

    private const ImGuiWindowFlags DockChildFlags = ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoScrollWithMouse;

    private readonly record struct DockLayout(
        float Top,
        float Right,
        float Bottom,
        float Left,
        float TopGap,
        float BottomGap,
        Vector2 CenterSize)
    {
        public bool HasBottomPanel => Bottom > 0f;

        public bool HasSidePanels => Left > 0f || Right > 0f;
    }

    /// <summary> draws dock panels/sections around the center editor content </summary>
    /// <param name="panels">panels/sections to reserve and draw for this frame</param>
    /// <param name="drawCenter">draws the remaining center content</param>
    public static void Draw(IReadOnlyList<EditorDockPanel> panels, Action drawCenter)
    {
        if (panels.Count == 0)
        {
            drawCenter();
            return;
        }

        Span<float> panelSizes = panels.Count <= 16
            ? stackalloc float[panels.Count]
            : new float[panels.Count];
        Vector2 available = ImGui.GetContentRegionAvail();
        DockLayout layout = ResolveLayout(panels, available, panelSizes);

        DrawSlotPanels(panels, panelSizes, EditorDockSlot.Top, new Vector2(available.X, layout.Top));
        DrawGap(layout.TopGap);
        DrawCenterArea(panels, panelSizes, layout, drawCenter);
        DrawBottomGap(layout.BottomGap);
        DrawSlotPanels(panels, panelSizes, EditorDockSlot.Bottom, new Vector2(available.X, layout.Bottom));
    }

    private static DockLayout ResolveLayout(IReadOnlyList<EditorDockPanel> panels, Vector2 available, Span<float> panelSizes)
    {
        ResolveSlotSizes(panels, available, panelSizes, EditorDockSlot.Top, EditorDockSlot.Bottom, out float top, out float bottom);

        float topGap = top > 0f ? ResolveTopGap() : 0f;
        float bottomGap = bottom > 0f ? ResolveBottomGap() : 0f;
        Vector2 centerSize = available with
        {
            Y = MathF.Max(1f, available.Y - top - topGap - bottom - bottomGap),
        };

        ResolveSlotSizes(panels, centerSize, panelSizes, EditorDockSlot.Left, EditorDockSlot.Right, out float left, out float right);
        return new DockLayout(top, right, bottom, left, topGap, bottomGap, centerSize);
    }

    private static void ResolveSlotSizes(
        IReadOnlyList<EditorDockPanel> panels,
        Vector2 available,
        Span<float> panelSizes,
        EditorDockSlot first,
        EditorDockSlot second,
        out float firstSize,
        out float secondSize)
    {
        firstSize = 0f;
        secondSize = 0f;

        for (int index = 0; index < panels.Count; index++)
        {
            EditorDockSlot slot = panels[index].Slot;
            if (slot != first && slot != second)
            {
                continue;
            }

            float size = ResolvePanelFixedSize(panels[index], available);
            panelSizes[index] = size;
            if (slot == first)
            {
                firstSize += size;
            }
            else
            {
                secondSize += size;
            }
        }
    }

    private static float ResolvePanelFixedSize(EditorDockPanel panel, Vector2 available)
    {
        Vector2 requestedSize = panel.ResolveSize(new EditorDockPanelContext(panel.Slot, available));
        return MathF.Max(0f, IsHorizontal(panel.Slot) ? requestedSize.Y : requestedSize.X);
    }

    private static void DrawCenterArea(
        IReadOnlyList<EditorDockPanel> panels,
        ReadOnlySpan<float> panelSizes,
        DockLayout layout,
        Action drawCenter)
    {
        if (!layout.HasSidePanels)
        {
            DrawCenterOnly(layout, drawCenter);
            return;
        }

        Vector2 contentCellPadding = ImGui.GetStyle().CellPadding;
        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Vector2.Zero);
        using var table = ImRaii.Table("##editorDockShellCenter", ResolveCenterColumnCount(layout), CenterTableFlags, layout.CenterSize);
        if (!table)
        {
            return;
        }

        SetupCenterColumns(layout);
        ImGui.TableNextRow();

        if (layout.Left > 0f)
        {
            ImGui.TableNextColumn();
            DrawSlotPanels(panels, panelSizes, EditorDockSlot.Left, new Vector2(layout.Left, layout.CenterSize.Y));
            ImGui.TableNextColumn();
        }

        ImGui.TableNextColumn();
        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, contentCellPadding))
        {
            drawCenter();
        }

        if (layout.Right > 0f)
        {
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            DrawSlotPanels(panels, panelSizes, EditorDockSlot.Right, new Vector2(layout.Right, layout.CenterSize.Y));
        }
    }

    private static int ResolveCenterColumnCount(DockLayout layout)
        => 1
           + (layout.Left > 0f ? 2 : 0)
           + (layout.Right > 0f ? 2 : 0);

    private static void SetupCenterColumns(DockLayout layout)
    {
        if (layout.Left > 0f)
        {
            ImGui.TableSetupColumn("Left", ImGuiTableColumnFlags.WidthFixed, layout.Left);
            ImGui.TableSetupColumn("LeftGap", ImGuiTableColumnFlags.WidthFixed, ResolveSideGap());
        }

        ImGui.TableSetupColumn("Center", ImGuiTableColumnFlags.WidthStretch, 1f);

        if (layout.Right > 0f)
        {
            ImGui.TableSetupColumn("RightGap", ImGuiTableColumnFlags.WidthFixed, ResolveSideGap());
            ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthFixed, layout.Right);
        }
    }

    private static void DrawCenterOnly(DockLayout layout, Action drawCenter)
    {
        if (!layout.HasBottomPanel)
        {
            drawCenter();
            return;
        }

        DrawCenterChild("##editorDockShellCenterContent", layout.CenterSize, drawCenter);
    }

    private static void DrawSlotPanels(
        IReadOnlyList<EditorDockPanel> panels,
        ReadOnlySpan<float> panelSizes,
        EditorDockSlot slot,
        Vector2 slotSize)
    {
        for (int index = 0; index < panels.Count; index++)
        {
            EditorDockPanel panel = panels[index];
            float size = panel.Slot == slot ? panelSizes[index] : 0f;
            if (size <= 0f)
            {
                continue;
            }

            Vector2 panelSize = IsHorizontal(slot)
                ? new Vector2(slotSize.X, size)
                : new Vector2(size, slotSize.Y);
            DrawDockPanelChild(panel.Id, panelSize, slot, () => panel.Draw(new EditorDockPanelContext(slot, panelSize)));
        }
    }

    private static void DrawDockPanelChild(string id, Vector2 size, EditorDockSlot slot, Action draw)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        bool sidePanel = IsSide(slot);
        ImGuiWindowFlags flags = sidePanel
            ? DockChildFlags | ImGuiWindowFlags.NoBackground
            : DockChildFlags;
        Vector2 padding = sidePanel
            ? Vector2.Zero
            : new Vector2(style.WindowPadding.X, 0f);

        using var outerSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(style.ItemSpacing.X, 0f));
        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, padding);
        using var child = ImRaii.Child(id, size, false, flags);
        if (!child)
        {
            return;
        }

        using var innerSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, style.ItemSpacing);
        draw();
    }

    private static void DrawCenterChild(string id, Vector2 size, Action draw)
    {
        using var child = ImRaii.Child(id, size, false, DockChildFlags | ImGuiWindowFlags.AlwaysUseWindowPadding);
        if (child)
        {
            draw();
        }
    }

    private static void DrawGap(float height)
    {
        if (height > 0f)
        {
            ImGui.Dummy(new Vector2(0f, height));
        }
    }

    private static void DrawBottomGap(float height)
    {
        if (height <= 0f)
        {
            return;
        }

        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        if (spacing > 0f)
        {
            ImGui.SetCursorPosY(MathF.Max(0f, ImGui.GetCursorPosY() - spacing));
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector2 start = ImGui.GetCursorScreenPos();
        float thickness = ResolveDividerThickness();
        float y = start.Y + spacing + (thickness * 0.5f);
        using var zeroSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
        drawList.AddLine(
            new Vector2(start.X, y),
            new Vector2(start.X + MathF.Max(0f, ImGui.GetContentRegionAvail().X), y),
            ImGui.GetColorU32(ImGuiCol.Separator),
            thickness);
        ImGui.Dummy(new Vector2(0f, height));
    }

    private static bool IsHorizontal(EditorDockSlot slot)
        => slot is EditorDockSlot.Top or EditorDockSlot.Bottom;

    private static bool IsSide(EditorDockSlot slot)
        => slot is EditorDockSlot.Left or EditorDockSlot.Right;

    private static float ResolveTopGap()
        => ImGui.GetStyle().ItemSpacing.Y * 0.5f;

    private static float ResolveBottomGap()
        => ResolveDividerThickness() + (ImGui.GetStyle().ItemSpacing.Y * 2f);

    private static float ResolveSideGap()
        => ImGui.GetStyle().ItemSpacing.X;

    private static float ResolveDividerThickness()
        => MathF.Max(1f, ImGui.GetStyle().FrameBorderSize + 1f);
}

