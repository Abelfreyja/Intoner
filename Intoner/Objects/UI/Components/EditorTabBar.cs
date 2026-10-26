using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.Objects.UI.Components
{
    internal sealed class EditorTabBar
    {
        internal sealed record Tab(
            string Id,
            FontAwesomeIcon Icon,
            string Label,
            string Count,
            bool Enabled = true,
            IReadOnlyList<EditorBadge>? Badges = null,
            Vector4? Accent = null);

        private readonly EditorOverflowMenu<Tab> _overflowMenu = new(static tab => tab.Id, static tab => tab.Label);
        private int _startIndex;
        private int _hiddenCount = -1;
        private string _overflowLabel = string.Empty;

        public static float Height => EditorLayout.Scaled(29f);

        public string Draw(string id, IReadOnlyList<Tab> tabs, string selectedId, float width, Vector4 accent, string searchHint = "Search")
        {
            if (tabs.Count == 0)
                return selectedId;

            using var scope = ImRaii.PushId(id);
            Vector2 start = ImGui.GetCursorScreenPos();
            float gap = EditorSegmentedControl.Spacing;
            float minimumWidth = EditorLayout.Scaled(108f);
            bool overflow = tabs.Count * minimumWidth + gap * (tabs.Count - 1) > width;
            float pickerWidth = overflow ? MathF.Min(EditorLayout.Scaled(58f), width * 0.4f) : 0f;
            float tabSpace = MathF.Max(1f, width - (overflow ? pickerWidth + gap : 0f));
            int capacity = overflow ? Math.Max(1, (int)((tabSpace + gap) / (minimumWidth + gap))) : tabs.Count;
            int selectedIndex = 0;
            for (int index = 0; index < tabs.Count; ++index)
            {
                if (string.Equals(tabs[index].Id, selectedId, StringComparison.Ordinal))
                    selectedIndex = index;
            }

            _startIndex = Math.Clamp(_startIndex, 0, Math.Max(0, tabs.Count - capacity));
            if (selectedIndex < _startIndex)
                _startIndex = selectedIndex;
            else if (selectedIndex >= _startIndex + capacity)
                _startIndex = selectedIndex - capacity + 1;

            float tabWidth = MathF.Max(1f, (tabSpace - gap * (capacity - 1)) / capacity);
            int segmentCount = capacity + (overflow ? 1 : 0);
            for (int index = 0; index < capacity; ++index)
            {
                Tab tab = tabs[_startIndex + index];
                ImGui.SetCursorScreenPos(new Vector2(start.X + index * (tabWidth + gap), start.Y));
                if (EditorSegmentedControl.DrawSegment(tab.Id, tab.Icon, tab.Label, tab.Count,
                        string.Equals(tab.Id, selectedId, StringComparison.Ordinal), tab.Enabled, tab.Label, tab.Accent ?? accent,
                        new Vector2(tabWidth, Height), index, segmentCount, rounding: 4f, showSelectionIndicator: false, background: EditorSegmentBackground.Emphasized))
                    selectedId = tab.Id;
            }

            if (overflow)
            {
                int hiddenCount = tabs.Count - capacity;

                if (_hiddenCount != hiddenCount)
                {
                    _hiddenCount = hiddenCount;
                    _overflowLabel = $"+{hiddenCount}";
                }

                ImGui.SetCursorScreenPos(new Vector2(start.X + width - pickerWidth, start.Y));
                bool open = EditorSegmentedControl.DrawSegment("overflow", FontAwesomeIcon.ChevronDown,
                    _overflowLabel, null, ImGui.IsPopupOpen("overflowMenu"), true,
                    "All tabs", accent, new Vector2(pickerWidth, Height), capacity, segmentCount,
                    rounding: 4f, showSelectionIndicator: false, background: EditorSegmentBackground.Emphasized);
                selectedId = _overflowMenu.Draw("overflowMenu", tabs, selectedId, open, start, start + new Vector2(width, Height),
                    accent, searchHint, static (tab, selected, rowAccent, rowHeight) => EditorContextMenu.DrawItem(
                        tab.Icon, tab.Label, selected, tab.Enabled, tab.Id,
                        selected && !tab.Accent.HasValue ? rowAccent : null, constrainWidth: true,
                        badges: tab.Badges, accent: tab.Accent, height: rowHeight));
            }

            ImGui.SetCursorScreenPos(start + new Vector2(0f, Height));
            return selectedId;
        }
    }
}
