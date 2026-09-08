using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.UI.Performance;

internal readonly record struct UiVirtualListOptions(float ItemHeight)
{
    public float ItemSpacingY { get; init; }
    public bool DrawTrailingSpacing { get; init; }
    public int FocusIndex { get; init; } = -1;

    public static UiVirtualListOptions Rows(float itemHeight, float itemSpacingY = 0f)
        => new(MathF.Max(1f, itemHeight))
        {
            ItemSpacingY = MathF.Max(0f, itemSpacingY),
        };
}

internal static class UiVirtualList
{
    public static Scope Begin(int count, UiVirtualListOptions options)
        => new(count, options);

    public static void Draw<TItem>(
        IReadOnlyList<TItem> items,
        UiVirtualListOptions options,
        Action<TItem, int> drawItem)
        => DrawIndices(items.Count, options, index => drawItem(items[index], index));

    public static void DrawTableRows<TItem>(
        IReadOnlyList<TItem> items,
        UiVirtualListOptions options,
        Action<TItem, int> drawColumns)
        => DrawIndices(items.Count, options, index =>
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

    public static void DrawIndices(
        int count,
        UiVirtualListOptions options,
        Action<int> drawItem)
    {
        using Scope list = Begin(count, options);
        while (list.Step())
        {
            for (int index = list.DisplayStart; index < list.DisplayEnd; ++index)
            {
                drawItem(index);
                list.FinishItem(index);
            }
        }
    }

    internal ref struct Scope
    {
        private readonly UiVirtualListOptions _options;
        private readonly int _count;
        private readonly int _focusIndex;
        private readonly float _itemSpacingY;
        private readonly ImRaii.StyleDisposable? _spacingStyle;
        private ImGuiListClipperPtr _clipper;
        private bool _active;

        internal Scope(int count, UiVirtualListOptions options)
        {
            _options = options;
            _count = Math.Max(0, count);
            _focusIndex = options.FocusIndex >= 0 && options.FocusIndex < count
                ? options.FocusIndex
                : -1;
            _itemSpacingY = MathF.Max(0f, options.ItemSpacingY);
            _spacingStyle = default;
            _clipper = default;
            _active = _count > 0;
            if (!_active)
            {
                return;
            }

            _spacingStyle = _itemSpacingY > 0f
                ? ImRaii.PushStyle(
                    ImGuiStyleVar.ItemSpacing,
                    new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f))
                : default;

            _clipper = ImGui.ImGuiListClipper();
            _clipper.Begin(_count, MathF.Max(1f, options.ItemHeight + _itemSpacingY));
            if (_focusIndex >= 0)
            {
                _clipper.ForceDisplayRangeByIndices(_focusIndex, _focusIndex + 1);
            }
        }

        public int DisplayStart
            => _active ? _clipper.DisplayStart : 0;

        public int DisplayEnd
            => _active ? _clipper.DisplayEnd : 0;

        public bool Step()
            => _active && _clipper.Step();

        public void FinishItem(int index)
        {
            if (index == _focusIndex)
            {
                ImGui.SetScrollHereY(0.5f);
            }

            if (_itemSpacingY > 0f && (_options.DrawTrailingSpacing || index + 1 < _count))
            {
                ImGui.Dummy(new Vector2(0f, _itemSpacingY));
            }
        }

        public void Dispose()
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            _clipper.End();
            _clipper.Destroy();
            _spacingStyle?.Dispose();
        }
    }
}
