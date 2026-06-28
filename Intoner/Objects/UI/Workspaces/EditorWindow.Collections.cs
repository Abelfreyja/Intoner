using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Collections;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private const string ObjectCollectionAddModPopupId = "##objectCollectionAddModPopup";
    private const int ObjectCollectionNameMaxLength = 128;
    private const int ObjectCollectionModFilterMaxLength = 128;

    private void DrawCollectionsWorkspace()
    {
        IReadOnlyList<ObjectCollectionSnapshot> collections = _objectCollectionManager.GetCollections();
        SyncSelectedObjectCollection(collections);

        var bodySize = ImGui.GetContentRegionAvail();
        using var table = ImRaii.Table(
            "##objectCollectionsWorkspaceTable",
            2,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.NoHostExtendY,
            bodySize);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Collections", ImGuiTableColumnFlags.WidthFixed, 300f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Editor", ImGuiTableColumnFlags.WidthStretch, 1f);

        ImGui.TableNextColumn();
        DrawChildPanel(
            "##objectCollectionsListPanel",
            Vector2.Zero,
            false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse,
            () => DrawObjectCollectionListPanel(collections),
            transparentBackground: false);

        ImGui.TableNextColumn();
        DrawChildPanel(
            "##objectCollectionsInspectorPanel",
            Vector2.Zero,
            false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse,
            () => DrawObjectCollectionInspectorPanel(collections),
            transparentBackground: false);

        DrawObjectCollectionAddModPopup(collections);
    }
}

