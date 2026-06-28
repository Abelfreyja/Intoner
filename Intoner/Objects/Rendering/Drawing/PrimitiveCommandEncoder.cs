using Intoner.Objects.Rendering.Primitives;
using System.Numerics;

namespace Intoner.Objects.Rendering.Drawing;

internal sealed class PrimitiveCommandEncoder
{
    public PrimitiveCommandList Commands { get; } = new();

    public void Clear()
        => Commands.Clear();

    public bool Append(in DrawContext context, DrawBatch batch)
    {
        if (batch.IsEmpty)
        {
            return false;
        }

        var initialPrimitiveCount = Commands.Count;
        foreach (LinePrimitive line in batch.Lines)
        {
            Commands.Add(new LineCommand(
                line.Start,
                line.End,
                ToRgba(line.Color, context.AlphaMultiplier),
                line.Thickness));
        }

        foreach (PointPrimitive point in batch.Points)
        {
            Commands.Add(new PointCommand(
                point.Position,
                ToRgba(point.Color, context.AlphaMultiplier),
                point.Radius,
                point.Segments));
        }

        foreach (ScreenPrimitive primitive in batch.ScreenPrimitives)
        {
            Commands.Add(new ScreenCommand(
                primitive.Kind,
                primitive.First,
                primitive.Second,
                primitive.Third,
                ToRgba(primitive.Color, context.AlphaMultiplier),
                primitive.Thickness,
                primitive.Caps,
                primitive.Previous,
                primitive.Next));
        }

        return Commands.Count != initialPrimitiveCount;
    }

    private static uint ToRgba(Vector4 color, float alphaMultiplier)
    {
        var r = ToByte(color.X);
        var g = ToByte(color.Y);
        var b = ToByte(color.Z);
        var a = ToByte(color.W * alphaMultiplier);
        return r | (g << 8) | (b << 16) | (a << 24);
    }

    private static uint ToByte(float value)
        => !float.IsFinite(value)
            ? 0
            : (uint)(Math.Clamp(value, 0f, 1f) * 255f + 0.5f);
}

