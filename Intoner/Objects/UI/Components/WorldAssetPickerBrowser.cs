using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Scene;
using Intoner.UI.Performance;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal sealed class WorldAssetPickerBrowser(EditorWorldPicker picker)
{
    private sealed record Row(WorldAsset Asset, string Id, string Name, EditorBadge[] Badges);

    private static readonly IntonerTooltipKeyHint[] UseCameraHint = [new("Click", "LMB", "to use camera distance")];
    private static readonly IntonerTooltipKeyHint[] UsePlayerHint = [new("Click", "LMB", "to use player distance")];

    private IReadOnlyList<WorldAsset>? _source;
    private readonly Dictionary<int, Row> _rows = [];
    private readonly List<int> _filtered = [];
    private string _search = string.Empty;
    private bool _recent;
    private int _nearbyCount = -1;
    private int _recentCount = -1;
    private string _nearbyLabel = string.Empty;
    private string _recentLabel = string.Empty;

    private static float SearchHeight => MathF.Min(EditorSearchField.Height, 26f * ImGuiHelpers.GlobalScale);
    private static float HeaderHeight => 22f * ImGuiHelpers.GlobalScale;
    private static float RowGap => 3f * ImGuiHelpers.GlobalScale;
    private static float ListHeight => 180f * ImGuiHelpers.GlobalScale;
    public static float Height => HeaderHeight + SearchHeight + ListHeight + 2f * RowGap;

    public void Clear()
    {
        _source = null;
        _rows.Clear();
        _filtered.Clear();
    }

    public void Draw()
    {
        if (!_recent)
        {
            picker.RefreshNearby();
        }

        WorldAssetPickState state = picker.State;
        if (_nearbyCount != state.Nearby.Count || _recentCount != state.Recent.Count)
        {
            _nearbyCount = state.Nearby.Count;
            _recentCount = state.Recent.Count;
            _nearbyLabel = _nearbyCount.ToString();
            _recentLabel = _recentCount.ToString();
        }

        float scale = ImGuiHelpers.GlobalScale;
        float gap = 6f * scale;
        float width = EditorSegmentedControl.ResolveSegmentWidth(ImGui.GetContentRegionAvail().X - HeaderHeight - gap, 2);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(gap, RowGap));
        bool changed = false;
        if (EditorSegmentedControl.DrawSegment("nearby", null, "Nearby", _nearbyLabel, !_recent, true,
                string.Empty, ThemeColors.AccentPrimary, new Vector2(width, HeaderHeight), 0, 2, showSelectionIndicator: false))
        {
            changed = _recent;
            _recent = false;

            if (changed)
            {
                picker.RefreshNearby(force: true);
            }
        }

        ImGui.SameLine(0f, EditorSegmentedControl.Spacing);
        if (EditorSegmentedControl.DrawSegment("recent", null, "Recent", _recentLabel, _recent, true,
                string.Empty, ThemeColors.AccentPrimary, new Vector2(width, HeaderHeight), 1, 2, showSelectionIndicator: false))
        {
            changed = !_recent;
            _recent = true;
        }

        ImGui.SameLine(0f, gap);
        FontAwesomeIcon distanceIcon = state.NearbyFromCamera ? FontAwesomeIcon.Camera : FontAwesomeIcon.User;

        if (EditorSegmentedControl.DrawCompactActionSegment("distanceOrigin", distanceIcon, !_recent, string.Empty,
                ThemeColors.AccentPrimary, new Vector2(HeaderHeight), 0, 1, emphasized: false))
        {
            picker.ToggleNearbyOrigin();
            changed = true;
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            bool fromCamera = picker.State.NearbyFromCamera;
            IReadOnlyList<IntonerTooltipKeyHint> hints = fromCamera ? UsePlayerHint : UseCameraHint;
            EditorToolbarTooltip.Draw(
                fromCamera ? FontAwesomeIcon.Camera : FontAwesomeIcon.User,
                fromCamera ? "Distance from Camera" : "Distance from Player",
                ThemeColors.AccentPrimary,
                actions: [], statuses: [],
                keyHints: _recent ? [] : hints,
                description: _recent ? "Available in Nearby." : string.Empty);
        }

        changed |= EditorSearchField.Draw("assetSearch", ref _search,
            new EditorSearchFieldOptions(
                "Search assets", ThemeColors.AccentPrimary, Height: SearchHeight, CenterSearchIcon: true));
        bool resetScroll = changed;
        state = picker.State;
        IReadOnlyList<WorldAsset> source = _recent ? state.Recent : state.Nearby;
        if (!ReferenceEquals(_source, source))
        {
            _source = source;
            _rows.Clear();
            changed = true;
        }

        if (changed)
        {
            _filtered.Clear();
            for (int index = 0; index < source.Count; ++index)
            {
                if (source[index].Path.Contains(_search, StringComparison.OrdinalIgnoreCase))
                {
                    _filtered.Add(index);
                }
            }
        }

        using var list = EditorScrollList.Begin("pickerAssets", new Vector2(0f, ListHeight),
            EditorScrollListOptions.Panel(ThemeColors.WindowBg, 4f * scale, ThemeColors.AccentPrimary));
        if (!list)
        {
            return;
        }

        if (resetScroll)
        {
            ImGui.SetScrollY(0f);
        }

        if (_filtered.Count == 0)
        {
            string message = _recent ? "No recent picks" : "No nearby assets";
            EditorEmptyState.Draw(_search.Length > 0 ? "No matching assets" : message, ListHeight);
            return;
        }

        using UiVirtualList.Scope rows = UiVirtualList.Begin(_filtered.Count,
            UiVirtualListOptions.Rows(EditorContextMenu.ItemHeight, 2f * scale));
        while (rows.Step())
        {
            for (int index = rows.DisplayStart; index < rows.DisplayEnd; ++index)
            {
                int sourceIndex = _filtered[index];
                if (!_rows.TryGetValue(sourceIndex, out Row? row))
                {
                    WorldAsset asset = source[sourceIndex];
                    EditorBadge[] badges = _recent ? [] :
                        [EditorBadge.Label($"{Vector3.Distance(asset.Transform.Translation, state.NearbyOrigin):0.#} y", color: ThemeColors.TextDisabled)];
                    row = new(asset, asset.InstanceId.ToString(), Path.GetFileNameWithoutExtension(asset.Path), badges);
                    _rows.Add(sourceIndex, row);
                }

                if (EditorContextMenu.DrawItem(SceneItemPresentation.ResolveObjectKindIcon(row.Asset.Kind), row.Name,
                        selected: row.Asset.InstanceId == state.SelectedInstanceId, id: row.Id,
                        tooltip: row.Asset.Path, constrainWidth: true, badges: row.Badges,
                        accent: ThemeColors.AccentPrimary, closeOnActivation: false))
                {
                    picker.Select(row.Asset);
                }

                if (!_recent && ImGui.IsItemHovered())
                {
                    picker.BrowserHover = row.Asset;
                }

                rows.FinishItem(index);
            }
        }
    }
}
