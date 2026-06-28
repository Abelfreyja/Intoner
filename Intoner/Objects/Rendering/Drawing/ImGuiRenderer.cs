using Dalamud.Bindings.ImGui;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Rendering.Drawing;

internal sealed class ImGuiRenderer : IRenderer
{
    public void BeginFrame()
    {
    }

    public void Draw(in DrawContext context, DrawBatch batch)
    {
        if (batch.IsEmpty)
        {
            return;
        }

        var drawList = ResolveDrawList(context.Layer);
        foreach (var line in batch.Lines)
        {
            if (!ObjectViewportProjectionUtility.TryProjectWorldLineToViewport(
                    context.ViewProjection,
                    context.ViewMatrix,
                    context.NearPlane,
                    line.Start,
                    line.End,
                    context.ViewportPos,
                    context.ViewportSize,
                    out var screenStart,
                    out var screenEnd))
            {
                continue;
            }

            drawList.AddLine(screenStart, screenEnd, ImGui.GetColorU32(line.Color), line.Thickness);
        }

        foreach (var point in batch.Points)
        {
            if (!ObjectViewportProjectionUtility.TryProjectWorldPointToViewport(
                    context.ViewProjection,
                    point.Position,
                    context.ViewportPos,
                    context.ViewportSize,
                    out var screenPoint))
            {
                continue;
            }

            drawList.AddCircleFilled(screenPoint, point.Radius, ImGui.GetColorU32(point.Color), point.Segments);
        }

        foreach (var primitive in batch.ScreenPrimitives)
        {
            switch (primitive.Kind)
            {
                case ScreenPrimitiveKind.Line:
                    drawList.AddLine(primitive.First, primitive.Second, ImGui.GetColorU32(primitive.Color), primitive.Thickness);
                    break;
                case ScreenPrimitiveKind.Triangle:
                    drawList.AddTriangleFilled(primitive.First, primitive.Second, primitive.Third, ImGui.GetColorU32(primitive.Color));
                    break;
            }
        }
    }

    private static ImDrawListPtr ResolveDrawList(DrawLayer layer)
        => layer switch
        {
            DrawLayer.Foreground => ImGui.GetForegroundDrawList(),
            _                    => ImGui.GetWindowDrawList(),
        };
}

