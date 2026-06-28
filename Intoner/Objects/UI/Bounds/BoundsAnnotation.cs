using Dalamud.Interface;
using Intoner.Objects.Runtime;
using System.Numerics;

namespace Intoner.Objects.UI.Bounds;

internal enum BoundsAnnotationCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

internal readonly record struct BoundsAnnotation(
    Guid BoundsId,
    BoundsAnnotationCorner Corner,
    FontAwesomeIcon Icon,
    Vector4 Accent,
    string TooltipTitle,
    string TooltipText,
    IReadOnlyList<PlacementFixProposal> Fixes);

