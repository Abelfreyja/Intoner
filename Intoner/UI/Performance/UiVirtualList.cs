using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.UI.Performance;

internal readonly record struct UiVirtualListOptions(float ItemHeight)
{
    public float ItemSpacingY { get; init; }
    public bool DrawTrailingSpacing { get; init; }

    public static UiVirtualListOptions Rows(float itemHeight, float itemSpacingY = 0f)
        => new(MathF.Max(1f, itemHeight))
        {
            ItemSpacingY = MathF.Max(0f, itemSpacingY),
        };
}

internal static class UiVirtualList
{
    public static void Draw<TItem>(
        IReadOnlyList<TItem> items,
        UiVirtualListOptions options,
        Action<TItem, int> drawItem)
        => Draw(items.Count, options, index => drawItem(items[index], index));

    public static void DrawTableRows<TItem>(
        IReadOnlyList<TItem> items,
        UiVirtualListOptions options,
        Action<TItem, int> drawColumns)
        => Draw(items.Count, options, index =>
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, options.ItemHeight);
            drawColumns(items[index], index);
        });

    public static void DrawWithFrameStyle<TItem>(
        IReadOnlyList<TItem> items,
        UiVirtualListOptions options,
        Action<TItem, int> drawItem,
        float frameRounding = 8f,
        float frameBorderSize = 1f)
    {
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, frameRounding);
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, frameBorderSize);
        Draw(items, options, drawItem);
    }

    public static void DrawChildWithFrameStyle<TItem>(
        string id,
        IReadOnlyList<TItem> items,
        UiVirtualListOptions options,
        Action<TItem, int> drawItem,
        Vector2 size = default,
        bool border = false,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None,
        float frameRounding = 8f,
        float frameBorderSize = 1f)
    {
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, frameRounding);
        using var frameBorder = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, frameBorderSize);
        using var child = ImRaii.Child(id, size, border, flags);
        if (child)
        {
            Draw(items, options, drawItem);
        }
    }

    public static void DrawRemainingChild<TItem>(
        string id,
        IReadOnlyList<TItem> items,
        UiVirtualListOptions options,
        Action<TItem, int> drawItem,
        bool border = true,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        var height = MathF.Max(options.ItemHeight, ImGui.GetContentRegionAvail().Y);
        using var child = ImRaii.Child(id, new Vector2(0f, height), border, flags);
        if (child)
        {
            Draw(items, options, drawItem);
        }
    }

    public static void Draw(
        int count,
        UiVirtualListOptions options,
        Action<int> drawItem)
    {
        if (count <= 0)
        {
            return;
        }

        var itemSpacingY = MathF.Max(0f, options.ItemSpacingY);
        using var zeroItemSpacing = itemSpacingY > 0f
            ? ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f))
            : default;
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(count, MathF.Max(1f, options.ItemHeight + itemSpacingY));
        while (clipper.Step())
        {
            for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; ++index)
            {
                drawItem(index);
                if (itemSpacingY > 0f && (options.DrawTrailingSpacing || index + 1 < count))
                {
                    ImGui.Dummy(new Vector2(0f, itemSpacingY));
                }
            }
        }

        clipper.End();
        clipper.Destroy();
    }
}
