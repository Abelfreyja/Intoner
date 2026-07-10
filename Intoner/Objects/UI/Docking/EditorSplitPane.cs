using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.UI;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Docking;

/// <summary> draws two constrained editor panes around an interactive divider </summary>
internal static class EditorSplitPane
{
    private const ImGuiTableFlags TableFlags = ImGuiTableFlags.SizingFixedFit
        | ImGuiTableFlags.NoHostExtendY
        | ImGuiTableFlags.NoPadInnerX
        | ImGuiTableFlags.NoPadOuterX
        | ImGuiTableFlags.NoSavedSettings;

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct Layout(
        float PrimaryWidth,
        float DividerWidth,
        float SecondaryWidth,
        float PaneWidth,
        float MinimumPrimaryWidth,
        float MaximumPrimaryWidth);

    [StructLayout(LayoutKind.Auto)]
    public readonly record struct Options(
        float Ratio,
        float DefaultRatio,
        float MinimumPrimaryWidth,
        float MinimumSecondaryWidth,
        Vector4 Accent);

    [StructLayout(LayoutKind.Auto)]
    public readonly record struct Update(float Ratio, bool Changed, bool Commit);

    public static Update Draw(string id, Vector2 size, Options options, Action drawPrimary, Action drawSecondary)
    {
        Vector2 resolvedSize = new(MathF.Max(1f, size.X), MathF.Max(1f, size.Y));
        Layout layout = ResolveLayout(resolvedSize.X, options);

        using var idScope = ImRaii.PushId(id);
        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Vector2.Zero);
        using var table = ImRaii.Table("##splitPane", 3, TableFlags, resolvedSize);
        if (!table)
        {
            return new Update(options.Ratio, false, false);
        }

        ImGui.TableSetupColumn("Primary", ImGuiTableColumnFlags.WidthFixed, layout.PrimaryWidth);
        ImGui.TableSetupColumn("Divider", ImGuiTableColumnFlags.WidthFixed, layout.DividerWidth);
        ImGui.TableSetupColumn("Secondary", ImGuiTableColumnFlags.WidthFixed, layout.SecondaryWidth);
        ImGui.TableNextRow(ImGuiTableRowFlags.None, resolvedSize.Y);

        ImGui.TableNextColumn();
        drawPrimary();

        ImGui.TableNextColumn();
        Update update = DrawDivider(layout, resolvedSize.Y, options);

        ImGui.TableNextColumn();
        drawSecondary();
        return update;
    }

    private static Update DrawDivider(Layout layout, float height, Options options)
    {
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 size = new(layout.DividerWidth, MathF.Max(1f, height));
        ImGui.InvisibleButton("##divider", size);

        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        bool commit = ImGui.IsItemDeactivated();
        float ratio = options.Ratio;
        bool changed = false;

        if (active && MathF.Abs(ImGui.GetIO().MouseDelta.X) > 0f)
        {
            float primaryWidth = Math.Clamp(
                layout.PrimaryWidth + ImGui.GetIO().MouseDelta.X,
                layout.MinimumPrimaryWidth,
                layout.MaximumPrimaryWidth);
            ratio = primaryWidth / layout.PaneWidth;
            changed = true;
        }

        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            ratio = NormalizeRatio(options.DefaultRatio);
            changed = true;
        }

        if (hovered || active)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            UiSharedService.AttachToolTip("Drag to resize panels. Double-click to reset.");
        }

        DrawDividerLine(min, size, options.Accent, hovered, active);
        return new Update(ratio, changed, commit);
    }

    private static Layout ResolveLayout(float width, Options options)
    {
        float dividerWidth = MathF.Min(width, Scaled(10f));
        float paneWidth = MathF.Max(1f, width - dividerWidth);
        float minimumPrimaryWidth = MathF.Max(1f, options.MinimumPrimaryWidth);
        float minimumSecondaryWidth = MathF.Max(1f, options.MinimumSecondaryWidth);
        float minimumWidth = minimumPrimaryWidth + minimumSecondaryWidth;
        if (minimumWidth > paneWidth)
        {
            float scale = paneWidth / minimumWidth;
            minimumPrimaryWidth *= scale;
            minimumSecondaryWidth *= scale;
        }

        float maximumPrimaryWidth = MathF.Max(minimumPrimaryWidth, paneWidth - minimumSecondaryWidth);
        float requestedPrimaryWidth = paneWidth * NormalizeRatio(options.Ratio, options.DefaultRatio);
        float primaryWidth = Math.Clamp(requestedPrimaryWidth, minimumPrimaryWidth, maximumPrimaryWidth);
        return new Layout(
            primaryWidth,
            dividerWidth,
            MathF.Max(1f, paneWidth - primaryWidth),
            paneWidth,
            minimumPrimaryWidth,
            maximumPrimaryWidth);
    }

    private static void DrawDividerLine(Vector2 min, Vector2 size, Vector4 accent, bool hovered, bool active)
    {
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector2 max = min + size;
        if (hovered || active)
        {
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(accent with { W = active ? 0.10f : 0.06f }));
        }

        float inset = Scaled(3f);
        float x = min.X + (size.X * 0.5f);
        Vector4 lineColor = EditorColors.Border with { W = 0.62f };
        if (active)
        {
            lineColor = accent with { W = 0.95f };
        }
        else if (hovered)
        {
            lineColor = accent with { W = 0.68f };
        }

        float thickness = active ? Scaled(2f) : MathF.Max(1f, Scaled(1f));
        drawList.AddLine(
            new Vector2(x, min.Y + inset),
            new Vector2(x, MathF.Max(min.Y + inset, max.Y - inset)),
            ImGui.GetColorU32(lineColor),
            thickness);
    }

    private static float NormalizeRatio(float ratio, float fallback = 0.5f)
    {
        if (float.IsFinite(ratio))
        {
            return Math.Clamp(ratio, 0f, 1f);
        }

        return float.IsFinite(fallback)
            ? Math.Clamp(fallback, 0f, 1f)
            : 0.5f;
    }

    private static float Scaled(float value)
        => value * ImGuiHelpers.GlobalScale;
}
