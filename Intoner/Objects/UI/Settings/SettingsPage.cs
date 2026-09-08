using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Settings.Components;
using System.Numerics;

namespace Intoner.Objects.UI.Settings;

internal sealed class SettingsPage
{
    private readonly SettingsCatalog _catalog;
    private readonly IUiOverlayTarget? _overlayTarget;
    private readonly bool[] _entryVisibility;

    private string _searchText = string.Empty;
    private string? _selectedTabId;
    private string _viewSearchText = string.Empty;
    private string? _viewTabId;
    private SettingsView? _view;
    private int _visibleEntryCount;

    public SettingsPage(
        SettingsCatalog catalog,
        IUiOverlayTarget? overlayTarget = null)
    {
        _catalog = catalog;
        _overlayTarget = overlayTarget;
        _entryVisibility = new bool[catalog.EntryCount];
    }

    public void Draw()
    {
        using var child = ImRaii.Child(
            "##objectSettingsWorkspace",
            Vector2.Zero,
            false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!child)
        {
            return;
        }

        SettingsView view = ResolveView();
        if (Toolbar.Draw(view, ref _searchText, _visibleEntryCount))
        {
            view = ResolveView();
        }

        _selectedTabId = BodyPanel.Draw(view, _overlayTarget, _selectedTabId);
    }

    private SettingsView ResolveView()
    {
        bool visibilityChanged = CaptureEntryVisibility();
        if (_view is not null
         && !visibilityChanged
         && string.Equals(_viewSearchText, _searchText, StringComparison.Ordinal)
         && string.Equals(_viewTabId, _selectedTabId, StringComparison.Ordinal))
        {
            return _view;
        }

        _viewSearchText = _searchText;
        _viewTabId = _selectedTabId;
        _view = SearchService.BuildView(
            _catalog,
            _selectedTabId,
            SearchService.BuildQuery(_searchText));
        return _view;
    }

    private bool CaptureEntryVisibility()
    {
        var changed = false;
        _visibleEntryCount = 0;
        for (var index = 0; index < _entryVisibility.Length; ++index)
        {
            bool isVisible = _catalog.Entries[index].IsVisible;
            _visibleEntryCount += isVisible ? 1 : 0;
            changed |= _entryVisibility[index] != isVisible;
            _entryVisibility[index] = isVisible;
        }

        return changed;
    }
}

