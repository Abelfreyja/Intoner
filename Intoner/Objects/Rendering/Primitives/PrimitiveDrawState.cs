using Intoner.Scene.Rendering;
using Intoner.Services.Configuration;

namespace Intoner.Objects.Rendering.Primitives;

internal readonly record struct PrimitiveDrawState(
    DrawDepthMode DepthMode,
    int AntiAliasing,
    bool DrawOverGameUi)
{
    public bool RequiresSceneDepth
        => DepthMode is DrawDepthMode.Occluded or DrawDepthMode.InvertOccluded;

    public SceneRenderPhase RenderPhase
        => DrawOverGameUi
            ? SceneRenderPhase.AfterGameUi
            : SceneRenderPhase.AfterGameUi3D;
}

