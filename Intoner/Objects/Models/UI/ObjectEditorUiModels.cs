using Dalamud.Interface;

namespace Intoner.Objects.Models;

internal enum DraftKind
{
    BgObject,
    Furniture,
    Vfx,
    Light,
}

internal enum WorkspaceMode
{
    CatalogCreate,
    PlacedInspector,
    LayoutManager,
    Collections,
    History,
    Settings,
    Debug,
}

internal enum BoundsOverlaySpace
{
    World,
    Local,
}

[Flags]
internal enum GizmoTransformMode
{
    None = 0,
    Translation = 1,
    Rotation = 2,
    Scale = 4,
    Universal = Translation | Rotation | Scale,
}

internal static class GizmoTransformModeExtensions
{
    public static bool IsCombined(this GizmoTransformMode mode)
        => mode != GizmoTransformMode.None && (mode & (mode - 1)) != GizmoTransformMode.None;
}

internal enum GizmoAxis
{
    None,
    X,
    Y,
    Z,
}

internal readonly record struct LightCatalogEntry(
    LightType Type,
    string Name,
    string Description,
    FontAwesomeIcon Icon);
