using Dalamud.Interface;
using System.Numerics;

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

internal enum ToolbarDockPosition
{
    Top,
    Right,
    Bottom,
    Left,
}

internal enum BoundsOverlaySpace
{
    World,
    Local,
}

internal enum GizmoTransformMode
{
    None,
    Translation,
    Rotation,
    Scale,
}

internal enum GizmoAxis
{
    None,
    X,
    Y,
    Z,
}

internal readonly record struct LightCatalogEntry(LightType Type, string Name, FontAwesomeIcon BadgeIcon, string BadgeTooltip, string Description);

internal readonly record struct PlacedObjectColorBadge(Vector4 PreviewColor, string Label, string Tooltip);

