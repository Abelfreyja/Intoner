using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Intoner.UI.Performance;
using System.Numerics;

namespace Intoner.Objects.UI.Components
{
    internal sealed class EditorOverflowMenu<TItem>
    {
        private readonly Func<TItem, string> _getId;
        private readonly Func<TItem, string> _getLabel;
        private readonly StringComparison _idComparison;
        private readonly List<int> _filteredIndices = [];
        private IReadOnlyList<TItem>? _filteredSource;
        private string _search = string.Empty;
        private string _lastSearch = string.Empty;

        public EditorOverflowMenu(Func<TItem, string> getId, Func<TItem, string> getLabel,
            StringComparison idComparison = StringComparison.Ordinal)
        {
            _getId = getId;
            _getLabel = getLabel;
            _idComparison = idComparison;
        }

        public string Draw(string id, IReadOnlyList<TItem> items, string selectedId, bool open,
            Vector2 min, Vector2 max, Vector4 accent, string searchHint,
            Func<TItem, bool, Vector4, float, bool> drawItem, IUiOverlayTarget? overlayTarget = null)
        {
            if (!open && !ImGui.IsPopupOpen(id))
            {
                return selectedId;
            }

            if (open)
            {
                _search = string.Empty;
            }

            float popupWidth = MathF.Min(max.X - min.X, EditorLayout.Scaled(292f));
            Vector2 anchor = new(max.X - popupWidth, max.Y + EditorLayout.Scaled(4f));
            using ImRaii.ColorDisposable background = ImRaii.PushColor(ImGuiCol.PopupBg, ThemeColors.WindowBg with { W = 1f })
                .Push(ImGuiCol.Border, ThemeColors.Border with { W = 0.85f });
            using ImRaii.StyleDisposable popupStyle = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, EditorLayout.Scaled(6f))
                .Push(ImGuiStyleVar.PopupBorderSize, EditorLayout.Scaled(1f));
            using EditorContextMenu.PopupScope popup = EditorContextMenu.BeginDropdown(id, open, anchor, popupWidth);

            if (!popup)
            {
                return selectedId;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !ImGui.GetIO().WantTextInput
                && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            {
                ImGui.CloseCurrentPopup();
                return selectedId;
            }

            float inset = EditorLayout.Scaled(4f);
            float contentWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X - inset * 2f);
            Vector2 searchStart = ImGui.GetCursorScreenPos() + new Vector2(inset);
            ImGui.SetCursorScreenPos(searchStart);
            bool filterChanged = EditorSearchField.Draw("overflowSearch", ref _search,
                new EditorSearchFieldOptions(searchHint, accent, Width: contentWidth));
            UpdateFilter(items);

            float rowHeight = EditorContextMenu.ItemHeight;
            float rowSpacing = EditorLayout.Scaled(2f);
            float listHeight = MathF.Max(EditorLayout.Scaled(120f), Math.Min(8, items.Count) * (rowHeight + rowSpacing));
            bool revealSelected = ImGui.IsWindowAppearing();
            Vector2 listStart = new(searchStart.X, ImGui.GetItemRectMax().Y + EditorLayout.Scaled(9f));
            float availableHeight = ImGui.GetWindowViewport().WorkPos.Y + ImGui.GetWindowViewport().WorkSize.Y
                - listStart.Y - EditorLayout.Scaled(12f);
            listHeight = MathF.Min(listHeight, MathF.Max(rowHeight, availableHeight));
            ImGui.SetCursorScreenPos(listStart);
            using ImRaii.ColorDisposable childBackground = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
            using ImRaii.StyleDisposable listPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            using (EditorScrollList.EditorScrollListScope list = EditorScrollList.Begin("overflowResults", new Vector2(contentWidth, listHeight),
                       EditorScrollListOptions.Panel(ThemeColors.WindowBg, EditorLayout.Scaled(6f), accent) with { OverlayTarget = overlayTarget }))
            {
                if (list)
                {
                    if (filterChanged)
                    {
                        ImGui.SetScrollY(0f);
                    }

                    int focusIndex = revealSelected && !filterChanged ? FindSelectedIndex(items, selectedId) : -1;

                    if (_filteredIndices.Count == 0)
                    {
                        EditorEmptyState.Draw("No matching items", listHeight);
                    }

                    using UiVirtualList.Scope rows = UiVirtualList.Begin(_filteredIndices.Count,
                        UiVirtualListOptions.Rows(rowHeight, rowSpacing) with { FocusIndex = focusIndex });

                    while (rows.Step())
                    {
                        for (int index = rows.DisplayStart; index < rows.DisplayEnd; ++index)
                        {
                            TItem item = items[_filteredIndices[index]];
                            string itemId = _getId(item);

                            if (drawItem(item, string.Equals(itemId, selectedId, _idComparison), accent, rowHeight))
                            {
                                selectedId = itemId;
                            }

                            rows.FinishItem(index);
                        }
                    }
                }
            }

            overlayTarget?.CaptureCurrentWindow();
            return selectedId;
        }

        private void UpdateFilter(IReadOnlyList<TItem> items)
        {
            if (ReferenceEquals(_filteredSource, items) && string.Equals(_lastSearch, _search, StringComparison.Ordinal))
            {
                return;
            }

            _filteredIndices.Clear();

            for (int index = 0; index < items.Count; ++index)
            {
                if (_getLabel(items[index]).Contains(_search, StringComparison.OrdinalIgnoreCase))
                {
                    _filteredIndices.Add(index);
                }
            }

            _filteredSource = items;
            _lastSearch = _search;
        }

        private int FindSelectedIndex(IReadOnlyList<TItem> items, string selectedId)
        {
            for (int index = 0; index < _filteredIndices.Count; ++index)
            {
                if (string.Equals(_getId(items[_filteredIndices[index]]), selectedId, _idComparison))
                {
                    return index;
                }
            }

            return -1;
        }
    }
}
