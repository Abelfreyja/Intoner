using System.Numerics;

namespace Intoner.Objects.Preview.Rendering;

internal static class ThumbnailRasterizer
{
    public static void Rasterize(
        ThumbnailTriangle triangle,
        byte[] pixels,
        float[] depthBuffer,
        int width,
        int height,
        bool enableTransparency,
        CancellationToken cancellationToken)
    {
        var area = Edge(triangle.Screen0, triangle.Screen1, triangle.Screen2);
        if (MathF.Abs(area) < 0.0001f)
        {
            return;
        }

        var isPositiveArea = area > 0f;
        var inverseArea = 1f / area;
        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(triangle.Screen0.X, MathF.Min(triangle.Screen1.X, triangle.Screen2.X))));
        var maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(triangle.Screen0.X, MathF.Max(triangle.Screen1.X, triangle.Screen2.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(triangle.Screen0.Y, MathF.Min(triangle.Screen1.Y, triangle.Screen2.Y))));
        var maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(triangle.Screen0.Y, MathF.Max(triangle.Screen1.Y, triangle.Screen2.Y))));

        var edge0StepX = triangle.Screen2.Y - triangle.Screen1.Y;
        var edge1StepX = triangle.Screen0.Y - triangle.Screen2.Y;
        var edge2StepX = triangle.Screen1.Y - triangle.Screen0.Y;
        var edge0StepY = triangle.Screen1.X - triangle.Screen2.X;
        var edge1StepY = triangle.Screen2.X - triangle.Screen0.X;
        var edge2StepY = triangle.Screen0.X - triangle.Screen1.X;

        var rowStartSample = new Vector2(minX + 0.5f, minY + 0.5f);
        var rowEdge0 = Edge(triangle.Screen1, triangle.Screen2, rowStartSample);
        var rowEdge1 = Edge(triangle.Screen2, triangle.Screen0, rowStartSample);
        var rowEdge2 = Edge(triangle.Screen0, triangle.Screen1, rowStartSample);

        var depthStepX = ((edge0StepX * triangle.Depth0) + (edge1StepX * triangle.Depth1) + (edge2StepX * triangle.Depth2)) * inverseArea;
        var depthStepY = ((edge0StepY * triangle.Depth0) + (edge1StepY * triangle.Depth1) + (edge2StepY * triangle.Depth2)) * inverseArea;
        var depthRow = ((rowEdge0 * triangle.Depth0) + (rowEdge1 * triangle.Depth1) + (rowEdge2 * triangle.Depth2)) * inverseArea;

        var hasTexture = triangle.DiffuseTexture is not null;
        var uvStepX = hasTexture
            ? ((triangle.Uv0 * edge0StepX) + (triangle.Uv1 * edge1StepX) + (triangle.Uv2 * edge2StepX)) * inverseArea
            : Vector2.Zero;
        var uvStepY = hasTexture
            ? ((triangle.Uv0 * edge0StepY) + (triangle.Uv1 * edge1StepY) + (triangle.Uv2 * edge2StepY)) * inverseArea
            : Vector2.Zero;
        var uvRow = hasTexture
            ? ((triangle.Uv0 * rowEdge0) + (triangle.Uv1 * rowEdge1) + (triangle.Uv2 * rowEdge2)) * inverseArea
            : Vector2.Zero;

        var litUntexturedDiffuseColor = triangle.UntexturedDiffuseColor * triangle.Lighting;
        var untexturedRed = ThumbnailPixels.ToByteColorComponent(litUntexturedDiffuseColor.X);
        var untexturedGreen = ThumbnailPixels.ToByteColorComponent(litUntexturedDiffuseColor.Y);
        var untexturedBlue = ThumbnailPixels.ToByteColorComponent(litUntexturedDiffuseColor.Z);

        for (var y = minY; y <= maxY; y++)
        {
            if ((y & 15) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var edge0 = rowEdge0;
            var edge1 = rowEdge1;
            var edge2 = rowEdge2;
            var depth = depthRow;
            var uv = uvRow;

            for (var x = minX; x <= maxX; x++)
            {
                if (ShouldSkipPixel(isPositiveArea, edge0, edge1, edge2))
                {
                    StepPixel(hasTexture, uvStepX, ref edge0, ref edge1, ref edge2, ref depth, ref uv, edge0StepX, edge1StepX, edge2StepX, depthStepX);
                    continue;
                }

                var bufferIndex = (y * width) + x;
                if (depth >= depthBuffer[bufferIndex])
                {
                    StepPixel(hasTexture, uvStepX, ref edge0, ref edge1, ref edge2, ref depth, ref uv, edge0StepX, edge1StepX, edge2StepX, depthStepX);
                    continue;
                }

                WriteTrianglePixel(
                    triangle,
                    pixels,
                    depthBuffer,
                    bufferIndex,
                    depth,
                    uv,
                    enableTransparency,
                    hasTexture,
                    untexturedRed,
                    untexturedGreen,
                    untexturedBlue);

                StepPixel(hasTexture, uvStepX, ref edge0, ref edge1, ref edge2, ref depth, ref uv, edge0StepX, edge1StepX, edge2StepX, depthStepX);
            }

            rowEdge0 += edge0StepY;
            rowEdge1 += edge1StepY;
            rowEdge2 += edge2StepY;
            depthRow += depthStepY;
            if (hasTexture)
            {
                uvRow += uvStepY;
            }
        }
    }

    private static bool ShouldSkipPixel(bool isPositiveArea, float edge0, float edge1, float edge2)
        => isPositiveArea
            ? edge0 < 0f || edge1 < 0f || edge2 < 0f
            : edge0 > 0f || edge1 > 0f || edge2 > 0f;

    private static void StepPixel(
        bool hasTexture,
        Vector2 uvStepX,
        ref float edge0,
        ref float edge1,
        ref float edge2,
        ref float depth,
        ref Vector2 uv,
        float edge0StepX,
        float edge1StepX,
        float edge2StepX,
        float depthStepX)
    {
        edge0 += edge0StepX;
        edge1 += edge1StepX;
        edge2 += edge2StepX;
        depth += depthStepX;
        if (hasTexture)
        {
            uv += uvStepX;
        }
    }

    private static void WriteTrianglePixel(
        ThumbnailTriangle triangle,
        byte[] pixels,
        float[] depthBuffer,
        int bufferIndex,
        float depth,
        Vector2 uv,
        bool enableTransparency,
        bool hasTexture,
        byte untexturedRed,
        byte untexturedGreen,
        byte untexturedBlue)
    {
        if (hasTexture)
        {
            if (!ThumbnailTextureSampler.TrySampleTexturedColor(
                    triangle.DiffuseTexture!,
                    uv,
                    triangle.Lighting,
                    triangle.ApplyAlphaClip,
                    triangle.Transparency,
                    out var red,
                    out var green,
                    out var blue,
                    out var alpha))
            {
                return;
            }

            var texturedPixelIndex = bufferIndex * 4;
            if (enableTransparency)
            {
                ThumbnailPixels.BlendPixel(texturedPixelIndex, pixels, red, green, blue, alpha);
            }
            else
            {
                depthBuffer[bufferIndex] = depth;
                ThumbnailPixels.WritePixel(texturedPixelIndex, pixels, red, green, blue);
            }
        }
        else
        {
            var flatPixelIndex = bufferIndex * 4;
            if (enableTransparency)
            {
                ThumbnailPixels.BlendPixel(
                    flatPixelIndex,
                    pixels,
                    untexturedRed,
                    untexturedGreen,
                    untexturedBlue,
                    ThumbnailPixels.ToByteColorComponentByteScale(triangle.Transparency * 255f));
            }
            else
            {
                depthBuffer[bufferIndex] = depth;
                ThumbnailPixels.WritePixel(flatPixelIndex, pixels, untexturedRed, untexturedGreen, untexturedBlue);
            }
        }
    }

    private static float Edge(Vector2 start, Vector2 end, Vector2 point)
        => ((point.X - start.X) * (end.Y - start.Y)) - ((point.Y - start.Y) * (end.X - start.X));
}
