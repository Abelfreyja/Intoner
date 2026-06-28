using System.Numerics;

namespace Intoner.Objects.Rendering.Primitives;

internal readonly record struct LineCommand(Vector3 Start, Vector3 End, uint Color, float Thickness);

internal readonly record struct PointCommand(Vector3 Position, uint Color, float Radius, int Segments);

internal readonly record struct ScreenCommand(
    ScreenPrimitiveKind Kind,
    Vector2 First,
    Vector2 Second,
    Vector2 Third,
    uint Color,
    float Thickness,
    ScreenLineCaps Caps,
    Vector2 Previous,
    Vector2 Next);

