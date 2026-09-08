using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Scene;

namespace Intoner.Objects.UI;

internal readonly record struct EditorFrame(
    EditorSceneFrame SceneFrame,
    ObjectCatalogData Catalog,
    IReadOnlyList<ObjectKindInfo> KindInfos,
    IReadOnlyList<ObjectLayoutSnapshot> Layouts,
    Guid? DefaultLayoutId)
{
    public EditorSceneState.EditorSceneData Scene => SceneFrame.Scene;

    public SceneItemSnapshot? SelectedItem
        => SceneFrame.ActiveSelection.Count > 0 ? SceneFrame.ActiveSelection[^1] : null;
}
