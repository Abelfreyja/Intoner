using System.Buffers;
using System.Numerics;
using PreviewGeometryReader = Intoner.Objects.Assets.ModelPreviewGeometryReader;

namespace Intoner.Objects.Preview.Rendering;

internal static class ThumbnailRenderer
{
    public static byte[] Render(
        PreviewGeometryReader.PreviewGeometry geometry,
        Vector3 untexturedDiffuseColor,
        PreviewRender.CameraState camera,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        var pixels = new byte[Math.Max(1, width * height * 4)];
        if (geometry.Positions.Length == 0 || geometry.TriangleCount == 0 || width <= 0 || height <= 0)
        {
            return pixels;
        }

        ThumbnailBackgroundCache.CopyTo(pixels, width, height, camera.BackgroundStyle);

        var pixelCount = width * height;
        var depthBuffer = ArrayPool<float>.Shared.Rent(pixelCount);
        Array.Fill(depthBuffer, float.PositiveInfinity, 0, pixelCount);

        try
        {
            PreviewScene.Frame scene = PreviewScene.CreateFrame(geometry.Bounds, camera, width, height);
            ThumbnailTriangle[] projectedTriangles = ArrayPool<ThumbnailTriangle>.Shared.Rent(geometry.TriangleCount);

            try
            {
                ThumbnailProjector.Build(
                    geometry,
                    projectedTriangles,
                    scene,
                    width,
                    height,
                    untexturedDiffuseColor,
                    cancellationToken);

                for (var triangleIndex = 0; triangleIndex < geometry.TriangleCount; triangleIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var triangle = projectedTriangles[triangleIndex];
                    if (!triangle.IsVisible || triangle.EnableTransparency)
                    {
                        continue;
                    }

                    ThumbnailRasterizer.Rasterize(
                        triangle,
                        pixels,
                        depthBuffer,
                        width,
                        height,
                        false,
                        cancellationToken);
                }

                var transparentTriangles = new List<ThumbnailTriangle>(geometry.TriangleCount);
                for (var triangleIndex = 0; triangleIndex < geometry.TriangleCount; triangleIndex++)
                {
                    var triangle = projectedTriangles[triangleIndex];
                    if (triangle.IsVisible && triangle.EnableTransparency)
                    {
                        transparentTriangles.Add(triangle);
                    }
                }

                transparentTriangles.Sort(static (left, right) => right.SortDepth.CompareTo(left.SortDepth));
                for (var i = 0; i < transparentTriangles.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThumbnailRasterizer.Rasterize(
                        transparentTriangles[i],
                        pixels,
                        depthBuffer,
                        width,
                        height,
                        true,
                        cancellationToken);
                }
            }
            finally
            {
                ArrayPool<ThumbnailTriangle>.Shared.Return(projectedTriangles, true);
            }

            return pixels;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(depthBuffer);
        }
    }
}
