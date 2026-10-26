using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Api;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Collections.Immutable;
using System.Numerics;

namespace Intoner.Objects.UI.Components
{
    internal sealed class SceneWorldSelector
    {
        private readonly ISceneLocationService _locations;
        private readonly ObjectWorldSelection?[] _originals;
        private readonly Dictionary<uint, SceneWorldInfo[]> _worldsByDataCenter;
        private readonly HashSet<uint> _dataCenterIds;
        private readonly HashSet<ushort> _worldIds;
        private readonly HashSet<uint> _expandedDataCenters = [];
        private Dictionary<uint, SceneWorldInfo[]> _filteredWorlds;
        private string _filter = string.Empty;
        private string _worldPreview;
        private bool _allDataCenters;

        public SceneWorldSelector(ISceneLocationService locations, IEnumerable<ObjectWorldSelection?> originals)
        {
            _locations = locations;
            _originals = originals.Distinct().ToArray();
            bool mixed = _originals.Length > 1;
            Value = mixed ? null : _originals[0];
            OriginalLabel = mixed ? "Mixed selection" : SceneItemPresentation.DescribeWorldSelection(Value, locations).Worlds;
            _worldPreview = Value?.AllDataCenters == true ? "All data centers" : OriginalLabel;
            _allDataCenters = Value?.AllDataCenters ?? false;
            _dataCenterIds = Value?.DataCenterIds.ToHashSet() ?? [];
            _worldIds = Value?.WorldIds.ToHashSet() ?? [];
            _worldsByDataCenter = locations.PublicWorlds.GroupBy(static world => world.DataCenterId)
                .ToDictionary(static group => group.Key, static group => group.ToArray());
            _filteredWorlds = _worldsByDataCenter;
        }

        public ObjectWorldSelection? Value { get; private set; }
        public bool Changed { get; private set; }
        public bool HasInvalidSelection => Changed && Value is null;
        public string OriginalLabel { get; }

        public void SelectAllDataCenters(bool selected)
        {
            _allDataCenters = selected;
            _dataCenterIds.Clear();
            _worldIds.Clear();
            RefreshWorldChange();
        }

        public void SelectDataCenter(uint id, bool selected)
        {
            if (!_worldsByDataCenter.TryGetValue(id, out SceneWorldInfo[]? worlds))
            {
                return;
            }

            ExpandAllDataCenters();
            _worldIds.ExceptWith(worlds.Select(static world => world.Id));

            if (selected)
            {
                _dataCenterIds.Add(id);
            }
            else
            {
                _dataCenterIds.Remove(id);
            }

            RefreshWorldChange();
        }

        public void SelectWorld(ushort id, bool selected)
        {
            if (!_locations.TryResolveWorld(id, out SceneWorldInfo? world)
                || !_worldsByDataCenter.TryGetValue(world.DataCenterId, out SceneWorldInfo[]? worlds))
            {
                return;
            }

            ExpandAllDataCenters();

            if (_dataCenterIds.Remove(world.DataCenterId))
            {
                _worldIds.UnionWith(worlds.Select(static item => item.Id));
            }

            if (selected)
            {
                _worldIds.Add(id);
            }
            else
            {
                _worldIds.Remove(id);
            }

            RefreshWorldChange();
        }

        private void ExpandAllDataCenters()
        {
            if (_allDataCenters)
            {
                _dataCenterIds.UnionWith(_worldsByDataCenter.Keys);
                _allDataCenters = false;
            }
        }

        private void RefreshWorldChange()
        {
            Value = null;
            Changed = true;
            _worldPreview = "Select worlds";

            if (!_allDataCenters && _dataCenterIds.Count == 0 && _worldIds.Count == 0)
            {
                return;
            }

            ObjectWorldSelection selection = new(_allDataCenters, _dataCenterIds.ToImmutableArray(), _worldIds.ToImmutableArray());

            if (!_locations.IsValidWorldSelection(selection))
            {
                return;
            }

            Value = selection;
            Changed = _originals.Any(original => original != selection);
            _worldPreview = _allDataCenters ? "All data centers" : SceneItemPresentation.DescribeWorldSelection(selection, _locations).Worlds;
        }

        public bool Draw(string label)
        {
            Vector2 popupSize = EditorContextMenu.ApplyPopupConstraints(
                280f * ImGuiHelpers.GlobalScale, minimumWidth: ImGui.CalcItemWidth());
            using var appearance = IntonerTooltip.PushStyle(new IntonerTooltipOptions
            {
                Accent = ThemeColors.AccentPrimary,
                ItemSpacing = new(0f, 2f),
            });
            using var comboPadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding,
                new Vector2(ImGui.GetStyle().WindowPadding.X, ImGui.GetStyle().FramePadding.Y));
            using var combo = ImRaii.Combo(label, _worldPreview);

            if (!combo)
            {
                IntonerTooltip.Attach(_worldPreview);
                return false;
            }

            IntonerTooltipContent.Header(FontAwesomeIcon.Globe, "Worlds");

            bool appearing = ImGui.IsWindowAppearing();

            if (appearing)
            {
                _filter = string.Empty;
                _filteredWorlds = _worldsByDataCenter;
                ImGui.SetKeyboardFocusHere();
            }

            bool filterChanged = EditorSearchField.Draw("worldSearch", ref _filter,
                new EditorSearchFieldOptions("Search worlds or DCs", ThemeColors.AccentPrimary));

            if (filterChanged)
            {
                UpdateFilter();

                if (_filter.Length > 0)
                {
                    _expandedDataCenters.UnionWith(_filteredWorlds.Keys);
                }
            }

            bool changed = false;
            ImGui.Dummy(new Vector2(0f, MathF.Max(0f,
                ImGui.GetStyle().WindowPadding.Y - (ImGui.GetStyle().ItemSpacing.Y * 2f))));
            int rowCount = 1;
            foreach (SceneWorldInfo[] worlds in _filteredWorlds.Values)
            {
                rowCount += worlds.Length + 1;
            }

            float availableHeight = popupSize.Y - (ImGui.GetCursorScreenPos().Y - ImGui.GetWindowPos().Y) - ImGui.GetStyle().WindowPadding.Y;
            float listHeight = MathF.Max(1f, MathF.Min(availableHeight,
                Math.Clamp(rowCount, 1, 8) * (EditorContextMenu.ItemHeight + ImGui.GetStyle().ItemSpacing.Y) + EditorContextMenu.SeparatorHeight));
            using var background = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
            using var listPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            using var list = EditorScrollList.Begin("worldChoices", new Vector2(0f, listHeight),
                EditorScrollListOptions.Panel(ThemeColors.WindowBg, 4f * ImGuiHelpers.GlobalScale, ThemeColors.AccentPrimary) with { HideEdgeFade = true });

            if (!list)
            {
                return changed;
            }

            if (filterChanged || appearing)
            {
                ImGui.SetScrollY(0f);
            }

            if (EditorContextMenu.DrawChoice("allDataCenters", "All data centers", _allDataCenters,
                    ThemeColors.AccentPrimary, closeOnActivation: false))
            {
                SelectAllDataCenters(!_allDataCenters);
                changed = true;
            }

            EditorContextMenu.DrawSeparator();

            if (_filteredWorlds.Count == 0)
            {
                EditorContextMenu.DrawHint("No matching worlds");
            }

            foreach ((uint id, SceneWorldInfo[] worlds) in _filteredWorlds)
            {
                using var dataCenterId = ImRaii.PushId((int)id);
                bool selected = _allDataCenters || _dataCenterIds.Contains(id);
                bool partial = !selected && HasSelectedWorld(_worldsByDataCenter[id]);

                bool expanded = _expandedDataCenters.Contains(id);

                if (EditorContextMenu.DrawTreeChoice("includeDataCenter", worlds[0].DataCenterName,
                        partial ? null : selected, ref expanded, ThemeColors.AccentPrimary))
                {
                    SelectDataCenter(id, !selected);
                    changed = true;
                }

                if (expanded)
                {
                    _expandedDataCenters.Add(id);
                }
                else
                {
                    _expandedDataCenters.Remove(id);
                }

                if (!expanded)
                {
                    continue;
                }

                using var indent = ImRaii.PushIndent(EditorContextMenu.ChildIndent, scaled: false);
                foreach (SceneWorldInfo world in worlds)
                {
                    bool included = _allDataCenters || _dataCenterIds.Contains(id) || _worldIds.Contains(world.Id);

                    if (EditorContextMenu.DrawChoice(world.Name, world.Name, included,
                            ThemeColors.AccentPrimary, closeOnActivation: false))
                    {
                        SelectWorld(world.Id, !included);
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private void UpdateFilter()
        {
            string[] tokens = SearchTermUtility.BuildSearchTokens(_filter);

            if (tokens.Length == 0)
            {
                _filteredWorlds = _worldsByDataCenter;
                return;
            }

            _filteredWorlds = [];
            foreach ((uint id, SceneWorldInfo[] worlds) in _worldsByDataCenter)
            {
                SceneWorldInfo[] matches = worlds.Where(world => SearchTermUtility.MatchesSearchText(
                    SearchTermUtility.BuildSearchText([world.Name, world.DataCenterName]), tokens)).ToArray();

                if (matches.Length > 0)
                {
                    _filteredWorlds.Add(id, matches);
                }
            }
        }

        private bool HasSelectedWorld(ReadOnlySpan<SceneWorldInfo> worlds)
        {
            foreach (SceneWorldInfo world in worlds)
            {
                if (_worldIds.Contains(world.Id))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
