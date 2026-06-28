using Intoner.Objects.Filesystem.Configuration;

namespace Intoner.Objects.Rendering.Primitives;

internal readonly record struct PrimitiveDrawState(
    DrawDepthMode DepthMode,
    int AntiAliasing,
    bool DrawOverGameUi);

