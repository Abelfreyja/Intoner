using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Interop;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Settings.Components;
using System.Numerics;

namespace Intoner.Objects.UI.Settings;

internal sealed class SettingsPage
{
    private readonly SettingsCatalog _catalog = SettingsCatalog.CreateDefault();
    private readonly DrawContext _drawContext;

    private string _searchText = string.Empty;
    private SettingsTab? _selectedTab;

    public SettingsPage(
        IObjectConfigurationService configurationService,
        IObjectHousingCullingService housingCullingService,
        IObjectHousingModePolicy housingModePolicy,
        IEditorOverlayTarget? overlayTarget = null)
    {
        _drawContext = new DrawContext(configurationService, housingCullingService, housingModePolicy, overlayTarget);
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

        var totalSettingCount = ResolveTotalSettingCount();
        SettingsView view = BuildView();
        if (Toolbar.Draw(view, ref _searchText, totalSettingCount))
        {
            view = BuildView();
        }

        _selectedTab = BodyPanel.Draw(view, _drawContext, _selectedTab);
    }

    private SettingsView BuildView()
    {
        SearchQuery query = SearchService.BuildQuery(_searchText);
        return SearchService.BuildView(_catalog, _selectedTab, query);
    }

    private int ResolveTotalSettingCount()
    {
        var count = 0;
        foreach (SettingsSection section in _catalog.Sections)
        {
            count += section.Entries.Count;
        }

        return count;
    }
}

