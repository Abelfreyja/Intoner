namespace Intoner.Scene.Rendering;

internal enum SceneRenderPhase
{
    BeforeGameUi3D,
    AfterGameUi3D,
    AfterGameUi,
}

[Flags]
internal enum SceneRenderFeatures
{
    None = 0,
    SceneDepth = 1 << 0,
}

/// <summary> provides render work for a phase of the final game scene </summary>
internal interface ISceneRenderPass
{
    /// <summary> gets the stable order used when multiple passes render in the same phase </summary>
    int Order { get; }

    /// <summary> resolves whether the pass currently has work in a phase and which frame features it requires </summary>
    /// <param name="phase"> the scene phase being prepared </param>
    /// <param name="features"> the frame features required by the pass </param>
    /// <returns> true when the pass should be included in the phase </returns>
    bool TryGetRequest(SceneRenderPhase phase, out SceneRenderFeatures features);

    /// <summary> renders the current pass using immutable scene frame data </summary>
    /// <param name="phase"> the scene phase being rendered </param>
    /// <param name="frame"> the final scene target and projection captured for this callback </param>
    void Draw(SceneRenderPhase phase, in SceneRenderFrame frame);
}
