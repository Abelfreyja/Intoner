namespace Intoner.Objects.Rendering.Drawing;

internal enum DrawLayer
{
    CurrentWindow = 1,
    Foreground = 2,
}

/// <summary>draws editor primitives through a concrete backend</summary>
internal interface IRenderer
{
    /// <summary>resets backend frame state before draw passes are submitted</summary>
    void BeginFrame();

    /// <summary>draws the provided batch into the given viewport</summary>
    /// <param name="context">viewport, camera, and layer for this draw</param>
    /// <param name="batch">primitives to draw</param>
    void Draw(in DrawContext context, DrawBatch batch);
}

