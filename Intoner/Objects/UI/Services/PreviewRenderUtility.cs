using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Buffers;
using System.Numerics;
using MdlDecimator = Intoner.Objects.Assets.ModelPreviewGeometryReader;

namespace Intoner.Objects.UI.Services;

internal static class PreviewRenderUtility
{
    private const float AlphaClipThreshold = 0.10f;
    private const float AlphaClipThresholdByte = AlphaClipThreshold * 255f;
    private const float PreviewNearPlane = 0.001f;
    private const int MaxBackgroundCacheEntries = 16;
    private const float TinyTriangleNormalLengthSquaredThreshold = 0.000000000001f;

    private readonly record struct BackgroundCacheKey(int Width, int Height, ObjectPreviewBackgroundStyle BackgroundStyle);
    private sealed class CachedBackgroundState(byte[] pixels)
    {
        public byte[] Pixels { get; } = pixels;
        public long LastAccessAtMs { get; set; } = Environment.TickCount64;
    }

    private readonly struct ProjectedTriangle(
        bool isVisible,
        Vector2 screen0,
        Vector2 screen1,
        Vector2 screen2,
        float depth0,
        float depth1,
        float depth2,
        float lighting,
        Vector3 fallbackAlbedo,
        MdlDecimator.PreviewTexture? diffuseTexture,
        bool applyAlphaClip,
        bool enableTransparency,
        float transparency,
        float sortDepth,
        Vector2 uv0,
        Vector2 uv1,
        Vector2 uv2)
    {
        public bool IsVisible { get; } = isVisible;
        public Vector2 Screen0 { get; } = screen0;
        public Vector2 Screen1 { get; } = screen1;
        public Vector2 Screen2 { get; } = screen2;
        public float Depth0 { get; } = depth0;
        public float Depth1 { get; } = depth1;
        public float Depth2 { get; } = depth2;
        public float Lighting { get; } = lighting;
        public Vector3 FallbackAlbedo { get; } = fallbackAlbedo;
        public MdlDecimator.PreviewTexture? DiffuseTexture { get; } = diffuseTexture;
        public bool ApplyAlphaClip { get; } = applyAlphaClip;
        public bool EnableTransparency { get; } = enableTransparency;
        public float Transparency { get; } = transparency;
        public float SortDepth { get; } = sortDepth;
        public Vector2 Uv0 { get; } = uv0;
        public Vector2 Uv1 { get; } = uv1;
        public Vector2 Uv2 { get; } = uv2;
    }

    private static readonly Lock _backgroundCacheLock = new();
    private static readonly Dictionary<BackgroundCacheKey, CachedBackgroundState> _backgroundCache = new();

    public static byte[] Render(
        MdlDecimator.PreviewGeometry geometry,
        ObjectCatalogKind kind,
        ObjectPreviewCameraState camera,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        var pixels = new byte[Math.Max(1, width * height * 4)];
        if (geometry.Positions.Length == 0 || geometry.TriangleCount == 0 || width <= 0 || height <= 0)
        {
            return pixels;
        }

        CopyBackground(pixels, width, height, camera.BackgroundStyle);

        var pixelCount = width * height;
        var depthBuffer = ArrayPool<float>.Shared.Rent(pixelCount);
        Array.Fill(depthBuffer, float.PositiveInfinity, 0, pixelCount);

        try
        {
            var center = geometry.Bounds.Center;
            var radius = geometry.Bounds.Radius;
            var zoom = Math.Clamp(camera.Zoom, 0.65f, 2.40f);
            var yaw = camera.Yaw;
            var pitch = Math.Clamp(camera.Pitch, -1.25f, 1.25f);
            var orbit = new Vector3(
                MathF.Cos(pitch) * MathF.Sin(yaw),
                MathF.Sin(pitch),
                MathF.Cos(pitch) * MathF.Cos(yaw));
            orbit = ResolvePreviewDirection(orbit, new Vector3(-0.8f, 0.45f, 1f));

            var distance = MathF.Max(radius * (2.2f * zoom + 0.85f), radius + 0.1f);
            var cameraPosition = center + (orbit * distance);
            var view = Matrix4x4.CreateLookAt(cameraPosition, center, Vector3.UnitY);

            var nearPlane = PreviewNearPlane;
            var farPlane = distance + (radius * 6f) + 10f;
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4.1f, width / (float)height, nearPlane, farPlane);
            var viewProjection = view * projection;

            var fallbackAlbedo = kind switch
            {
                ObjectCatalogKind.BgObject => new Vector3(0.83f, 0.71f, 0.58f),
                ObjectCatalogKind.Furniture => new Vector3(0.58f, 0.70f, 0.84f),
                _ => new Vector3(0.74f, 0.74f, 0.74f),
            };
            var lightDirection = ResolvePreviewDirection(new Vector3(-0.45f, 0.75f, 0.35f), Vector3.UnitY);
            var projectedTriangles = ArrayPool<ProjectedTriangle>.Shared.Rent(geometry.TriangleCount);

            try
            {
                BuildProjectedTriangles(
                    geometry,
                    projectedTriangles,
                    cameraPosition,
                    view,
                    viewProjection,
                    width,
                    height,
                    nearPlane,
                    lightDirection,
                    fallbackAlbedo,
                    cancellationToken);

                for (var triangleIndex = 0; triangleIndex < geometry.TriangleCount; triangleIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var triangle = projectedTriangles[triangleIndex];
                    if (!triangle.IsVisible || triangle.EnableTransparency)
                    {
                        continue;
                    }

                    RasterizeTriangle(
                        pixels,
                        depthBuffer,
                        width,
                        height,
                        triangle.Screen0,
                        triangle.Screen1,
                        triangle.Screen2,
                        triangle.Depth0,
                        triangle.Depth1,
                        triangle.Depth2,
                        triangle.Lighting,
                        triangle.FallbackAlbedo,
                        triangle.DiffuseTexture,
                        triangle.ApplyAlphaClip,
                        false,
                        triangle.Transparency,
                        triangle.Uv0,
                        triangle.Uv1,
                        triangle.Uv2,
                        cancellationToken);
                }

                var transparentTriangles = new List<ProjectedTriangle>(geometry.TriangleCount);
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
                    var triangle = transparentTriangles[i];
                    RasterizeTriangle(
                        pixels,
                        depthBuffer,
                        width,
                        height,
                        triangle.Screen0,
                        triangle.Screen1,
                        triangle.Screen2,
                        triangle.Depth0,
                        triangle.Depth1,
                        triangle.Depth2,
                        triangle.Lighting,
                        triangle.FallbackAlbedo,
                        triangle.DiffuseTexture,
                        triangle.ApplyAlphaClip,
                        true,
                        triangle.Transparency,
                        triangle.Uv0,
                        triangle.Uv1,
                        triangle.Uv2,
                        cancellationToken);
                }
            }
            finally
            {
                ArrayPool<ProjectedTriangle>.Shared.Return(projectedTriangles, true);
            }

            return pixels;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(depthBuffer);
        }
    }

    private static void CopyBackground(byte[] pixels, int width, int height, ObjectPreviewBackgroundStyle backgroundStyle)
    {
        var cacheKey = new BackgroundCacheKey(width, height, backgroundStyle);
        byte[] backgroundPixels;
        lock (_backgroundCacheLock)
        {
            long now = Environment.TickCount64;
            if (!_backgroundCache.TryGetValue(cacheKey, out CachedBackgroundState? backgroundState))
            {
                backgroundState = new CachedBackgroundState(CreateBackground(width, height, backgroundStyle));
                _backgroundCache[cacheKey] = backgroundState;
                TrimBackgroundCache();
            }

            backgroundState.LastAccessAtMs = now;
            backgroundPixels = backgroundState.Pixels;
        }

        Buffer.BlockCopy(backgroundPixels, 0, pixels, 0, backgroundPixels.Length);
    }

    private static void TrimBackgroundCache()
    {
        if (_backgroundCache.Count <= MaxBackgroundCacheEntries)
        {
            return;
        }

        foreach (BackgroundCacheKey key in _backgroundCache
                     .OrderBy(static entry => entry.Value.LastAccessAtMs)
                     .Take(_backgroundCache.Count - MaxBackgroundCacheEntries)
                     .Select(static entry => entry.Key)
                     .ToArray())
        {
            _backgroundCache.Remove(key);
        }
    }

    private static byte[] CreateBackground(int width, int height, ObjectPreviewBackgroundStyle backgroundStyle)
    {
        var pixels = new byte[Math.Max(1, width * height * 4)];
        FillBackground(pixels, width, height, backgroundStyle);
        return pixels;
    }

    private static void BuildProjectedTriangles(
        MdlDecimator.PreviewGeometry geometry,
        ProjectedTriangle[] projectedTriangles,
        Vector3 cameraPosition,
        Matrix4x4 view,
        Matrix4x4 viewProjection,
        int width,
        int height,
        float nearPlane,
        Vector3 lightDirection,
        Vector3 fallbackAlbedo,
        CancellationToken cancellationToken)
    {
        for (var triangleIndex = 0; triangleIndex < geometry.TriangleCount; triangleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            projectedTriangles[triangleIndex] = ProjectTriangle(
                geometry,
                triangleIndex,
                cameraPosition,
                view,
                viewProjection,
                width,
                height,
                nearPlane,
                lightDirection,
                fallbackAlbedo);
        }
    }

    private static ProjectedTriangle ProjectTriangle(
        MdlDecimator.PreviewGeometry geometry,
        int triangleIndex,
        Vector3 cameraPosition,
        Matrix4x4 view,
        Matrix4x4 viewProjection,
        int width,
        int height,
        float nearPlane,
        Vector3 lightDirection,
        Vector3 fallbackAlbedo)
    {
        int indexOffset = triangleIndex * 3;
        if ((uint)(indexOffset + 2) >= geometry.Indices.Length)
        {
            return default;
        }

        int index0 = geometry.Indices[indexOffset];
        int index1 = geometry.Indices[indexOffset + 1];
        int index2 = geometry.Indices[indexOffset + 2];
        if ((uint)index0 >= geometry.Positions.Length
         || (uint)index1 >= geometry.Positions.Length
         || (uint)index2 >= geometry.Positions.Length)
        {
            return default;
        }

        var world0 = geometry.Positions[index0];
        var world1 = geometry.Positions[index1];
        var world2 = geometry.Positions[index2];
        var centroid = (world0 + world1 + world2) / 3f;
        var normal = Vector3.Cross(world1 - world0, world2 - world0);
        var normalLengthSquared = normal.LengthSquared();
        if (normalLengthSquared >= TinyTriangleNormalLengthSquaredThreshold)
        {
            normal /= MathF.Sqrt(normalLengthSquared);
        }
        else
        {
            normal = Vector3.UnitY;
        }

        var viewDirection = ResolvePreviewDirection(cameraPosition - centroid, Vector3.UnitZ);
        var diffuse = MathF.Abs(Vector3.Dot(normal, lightDirection));
        var facing = MathF.Abs(Vector3.Dot(normal, viewDirection));
        var lighting = Math.Clamp(0.20f + (diffuse * 0.60f) + (facing * 0.20f), 0f, 1f);

        if (!TryProject(world0, view, viewProjection, width, height, nearPlane, out var screen0, out var depth0)
         || !TryProject(world1, view, viewProjection, width, height, nearPlane, out var screen1, out var depth1)
         || !TryProject(world2, view, viewProjection, width, height, nearPlane, out var screen2, out var depth2))
        {
            return default;
        }

        var uv0 = GetPreviewTexCoord(geometry.TexCoords, index0);
        var uv1 = GetPreviewTexCoord(geometry.TexCoords, index1);
        var uv2 = GetPreviewTexCoord(geometry.TexCoords, index2);

        MdlDecimator.PreviewTexture? diffuseTexture = null;
        var applyAlphaClip = false;
        var enableTransparency = false;
        var transparency = 1f;
        if ((uint)triangleIndex < geometry.TriangleMaterialIndices.Length)
        {
            int materialIndex = geometry.TriangleMaterialIndices[triangleIndex];
            if ((uint)materialIndex < geometry.Materials.Length)
            {
                MdlDecimator.PreviewMaterial material = geometry.Materials[materialIndex];
                diffuseTexture = material.DiffuseTexture;
                applyAlphaClip = material.ApplyAlphaClip;
                enableTransparency = material.EnableTransparency;
                transparency = material.Transparency;
            }
        }

        return new ProjectedTriangle(
            true,
            screen0,
            screen1,
            screen2,
            depth0,
            depth1,
            depth2,
            lighting,
            fallbackAlbedo,
            diffuseTexture,
            applyAlphaClip,
            enableTransparency,
            transparency,
            (depth0 + depth1 + depth2) / 3f,
            uv0,
            uv1,
            uv2);
    }

    private static Vector2 GetPreviewTexCoord(IReadOnlyList<Vector2> texCoords, int index)
        => (uint)index < texCoords.Count ? texCoords[index] : Vector2.Zero;

    private static void FillBackground(byte[] pixels, int width, int height, ObjectPreviewBackgroundStyle backgroundStyle)
    {
        var (top, bottom) = ObjectPreviewBackgroundPalette.GetGradient(backgroundStyle);

        for (var y = 0; y < height; y++)
        {
            var t = height <= 1 ? 0f : y / (float)(height - 1);
            var rowColor = Vector3.Lerp(top, bottom, t);
            for (var x = 0; x < width; x++)
            {
                var pixelIndex = ((y * width) + x) * 4;
                WritePixel(pixels, pixelIndex, rowColor);
            }
        }
    }

    private static bool TryProject(
        Vector3 position,
        Matrix4x4 view,
        Matrix4x4 viewProjection,
        int width,
        int height,
        float nearPlane,
        out Vector2 screen,
        out float depth)
    {
        screen = default;
        depth = 0f;

        var viewPosition = Vector3.Transform(position, view);
        depth = -viewPosition.Z;
        if (depth <= nearPlane)
        {
            return false;
        }

        var clip = Vector4.Transform(new Vector4(position, 1f), viewProjection);
        if (MathF.Abs(clip.W) < 0.00001f)
        {
            return false;
        }

        var inverseW = 1f / clip.W;
        var ndc = new Vector3(clip.X * inverseW, clip.Y * inverseW, clip.Z * inverseW);
        if (ndc.Z < 0f || ndc.Z > 1f)
        {
            return false;
        }

        screen = new Vector2(
            (ndc.X * 0.5f + 0.5f) * (width - 1),
            (1f - (ndc.Y * 0.5f + 0.5f)) * (height - 1));
        return true;
    }

    private static void RasterizeTriangle(
        byte[] pixels,
        float[] depthBuffer,
        int width,
        int height,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        float d0,
        float d1,
        float d2,
        float lighting,
        Vector3 fallbackAlbedo,
        MdlDecimator.PreviewTexture? diffuseTexture,
        bool applyAlphaClip,
        bool enableTransparency,
        float transparency,
        Vector2 uv0,
        Vector2 uv1,
        Vector2 uv2,
        CancellationToken cancellationToken)
    {
        var area = Edge(p0, p1, p2);
        if (MathF.Abs(area) < 0.0001f)
        {
            return;
        }

        var isPositiveArea = area > 0f;
        var inverseArea = 1f / area;
        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
        var maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
        var maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));

        var edge0StepX = p2.Y - p1.Y;
        var edge1StepX = p0.Y - p2.Y;
        var edge2StepX = p1.Y - p0.Y;
        var edge0StepY = p1.X - p2.X;
        var edge1StepY = p2.X - p0.X;
        var edge2StepY = p0.X - p1.X;

        var rowStartSample = new Vector2(minX + 0.5f, minY + 0.5f);
        var rowEdge0 = Edge(p1, p2, rowStartSample);
        var rowEdge1 = Edge(p2, p0, rowStartSample);
        var rowEdge2 = Edge(p0, p1, rowStartSample);

        var depthStepX = ((edge0StepX * d0) + (edge1StepX * d1) + (edge2StepX * d2)) * inverseArea;
        var depthStepY = ((edge0StepY * d0) + (edge1StepY * d1) + (edge2StepY * d2)) * inverseArea;
        var depthRow = ((rowEdge0 * d0) + (rowEdge1 * d1) + (rowEdge2 * d2)) * inverseArea;

        var hasTexture = diffuseTexture is not null;
        var uvStepX = hasTexture
            ? ((uv0 * edge0StepX) + (uv1 * edge1StepX) + (uv2 * edge2StepX)) * inverseArea
            : Vector2.Zero;
        var uvStepY = hasTexture
            ? ((uv0 * edge0StepY) + (uv1 * edge1StepY) + (uv2 * edge2StepY)) * inverseArea
            : Vector2.Zero;
        var uvRow = hasTexture
            ? ((uv0 * rowEdge0) + (uv1 * rowEdge1) + (uv2 * rowEdge2)) * inverseArea
            : Vector2.Zero;

        var litFallbackColor = fallbackAlbedo * lighting;
        var fallbackRed = ToByteColorComponent(litFallbackColor.X);
        var fallbackGreen = ToByteColorComponent(litFallbackColor.Y);
        var fallbackBlue = ToByteColorComponent(litFallbackColor.Z);

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
                if (isPositiveArea)
                {
                    if (edge0 < 0f || edge1 < 0f || edge2 < 0f)
                    {
                        edge0 += edge0StepX;
                        edge1 += edge1StepX;
                        edge2 += edge2StepX;
                        depth += depthStepX;
                        if (hasTexture)
                        {
                            uv += uvStepX;
                        }

                        continue;
                    }
                }
                else if (edge0 > 0f || edge1 > 0f || edge2 > 0f)
                {
                    edge0 += edge0StepX;
                    edge1 += edge1StepX;
                    edge2 += edge2StepX;
                    depth += depthStepX;
                    if (hasTexture)
                    {
                        uv += uvStepX;
                    }

                    continue;
                }

                var bufferIndex = (y * width) + x;
                if (depth >= depthBuffer[bufferIndex])
                {
                    edge0 += edge0StepX;
                    edge1 += edge1StepX;
                    edge2 += edge2StepX;
                    depth += depthStepX;
                    if (hasTexture)
                    {
                        uv += uvStepX;
                    }

                    continue;
                }

                if (hasTexture)
                {
                    if (!TrySampleTexturedColor(diffuseTexture!, uv, lighting, applyAlphaClip, transparency, out var red, out var green, out var blue, out var alpha))
                    {
                        edge0 += edge0StepX;
                        edge1 += edge1StepX;
                        edge2 += edge2StepX;
                        depth += depthStepX;
                        uv += uvStepX;
                        continue;
                    }

                    var texturedPixelIndex = bufferIndex * 4;
                    if (enableTransparency)
                    {
                        BlendPixel(texturedPixelIndex, pixels, red, green, blue, alpha);
                    }
                    else
                    {
                        depthBuffer[bufferIndex] = depth;
                        WritePixel(texturedPixelIndex, pixels, red, green, blue);
                    }
                }
                else
                {
                    var flatPixelIndex = bufferIndex * 4;
                    if (enableTransparency)
                    {
                        BlendPixel(flatPixelIndex, pixels, fallbackRed, fallbackGreen, fallbackBlue, ToByteColorComponentByteScale(transparency * 255f));
                    }
                    else
                    {
                        depthBuffer[bufferIndex] = depth;
                        WritePixel(flatPixelIndex, pixels, fallbackRed, fallbackGreen, fallbackBlue);
                    }
                }

                edge0 += edge0StepX;
                edge1 += edge1StepX;
                edge2 += edge2StepX;
                depth += depthStepX;
                if (hasTexture)
                {
                    uv += uvStepX;
                }
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

    private static bool TrySampleTexturedColor(
        MdlDecimator.PreviewTexture texture,
        Vector2 uv,
        float lighting,
        bool applyAlphaClip,
        float transparency,
        out byte red,
        out byte green,
        out byte blue,
        out byte alpha)
    {
        if (texture.Width <= 0 || texture.Height <= 0 || texture.RgbaPixels.Length < texture.Width * texture.Height * 4)
        {
            red = ToByteColorComponentByteScale(255f * lighting);
            green = red;
            blue = red;
            alpha = ToByteColorComponentByteScale(transparency * 255f);
            return true;
        }

        var wrappedU = WrapUv(uv.X);
        var wrappedV = WrapUv(uv.Y);
        var sampleX = wrappedU * (texture.Width - 1);
        var sampleY = wrappedV * (texture.Height - 1);

        var x0 = (int)MathF.Floor(sampleX);
        var y0 = (int)MathF.Floor(sampleY);
        var x1 = Math.Min(texture.Width - 1, x0 + 1);
        var y1 = Math.Min(texture.Height - 1, y0 + 1);
        var tx = sampleX - x0;
        var ty = sampleY - y0;
        var weightX0 = 1f - tx;
        var weightY0 = 1f - ty;
        var weight00 = weightX0 * weightY0;
        var weight10 = tx * weightY0;
        var weight01 = weightX0 * ty;
        var weight11 = tx * ty;
        var pixels = texture.RgbaPixels;
        var pixel00 = ((y0 * texture.Width) + x0) * 4;
        var pixel10 = ((y0 * texture.Width) + x1) * 4;
        var pixel01 = ((y1 * texture.Width) + x0) * 4;
        var pixel11 = ((y1 * texture.Width) + x1) * 4;
        var sampledAlpha = (pixels[pixel00 + 3] * weight00)
            + (pixels[pixel10 + 3] * weight10)
            + (pixels[pixel01 + 3] * weight01)
            + (pixels[pixel11 + 3] * weight11);
        if (applyAlphaClip && sampledAlpha < AlphaClipThresholdByte)
        {
            red = 0;
            green = 0;
            blue = 0;
            alpha = 0;
            return false;
        }

        red = ToByteColorComponentByteScale((
            (pixels[pixel00] * weight00)
          + (pixels[pixel10] * weight10)
          + (pixels[pixel01] * weight01)
          + (pixels[pixel11] * weight11)) * lighting);
        green = ToByteColorComponentByteScale((
            (pixels[pixel00 + 1] * weight00)
          + (pixels[pixel10 + 1] * weight10)
          + (pixels[pixel01 + 1] * weight01)
          + (pixels[pixel11 + 1] * weight11)) * lighting);
        blue = ToByteColorComponentByteScale((
            (pixels[pixel00 + 2] * weight00)
          + (pixels[pixel10 + 2] * weight10)
          + (pixels[pixel01 + 2] * weight01)
          + (pixels[pixel11 + 2] * weight11)) * lighting);
        alpha = ToByteColorComponentByteScale(sampledAlpha * transparency);
        return true;
    }

    private static float WrapUv(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return 0f;
        }

        value -= MathF.Floor(value);
        return value < 0f ? value + 1f : value;
    }

    private static float Edge(Vector2 start, Vector2 end, Vector2 point)
        => ((point.X - start.X) * (end.Y - start.Y)) - ((point.Y - start.Y) * (end.X - start.X));

    private static void WritePixel(int pixelIndex, byte[] pixels, byte red, byte green, byte blue)
    {
        pixels[pixelIndex] = red;
        pixels[pixelIndex + 1] = green;
        pixels[pixelIndex + 2] = blue;
        pixels[pixelIndex + 3] = 255;
    }

    private static void BlendPixel(int pixelIndex, byte[] pixels, byte red, byte green, byte blue, byte alpha)
    {
        if (alpha <= 0)
        {
            return;
        }

        if (alpha >= 255)
        {
            WritePixel(pixelIndex, pixels, red, green, blue);
            return;
        }

        var inverseAlpha = 255 - alpha;
        pixels[pixelIndex] = (byte)(((red * alpha) + (pixels[pixelIndex] * inverseAlpha)) / 255);
        pixels[pixelIndex + 1] = (byte)(((green * alpha) + (pixels[pixelIndex + 1] * inverseAlpha)) / 255);
        pixels[pixelIndex + 2] = (byte)(((blue * alpha) + (pixels[pixelIndex + 2] * inverseAlpha)) / 255);
        pixels[pixelIndex + 3] = 255;
    }

    private static void WritePixel(byte[] pixels, int pixelIndex, Vector3 color)
    {
        WritePixel(
            pixelIndex,
            pixels,
            ToByteColorComponent(color.X),
            ToByteColorComponent(color.Y),
            ToByteColorComponent(color.Z));
    }

    private static byte ToByteColorComponent(float value)
        => (byte)Math.Clamp(value * 255f, 0f, 255f);

    private static byte ToByteColorComponentByteScale(float value)
        => (byte)Math.Clamp(value, 0f, 255f);

    private static Vector3 ResolvePreviewDirection(Vector3 direction, Vector3 fallback)
    {
        if (ObjectMathUtility.TryNormalize(direction, out var normalizedDirection))
        {
            return normalizedDirection;
        }

        return ObjectMathUtility.TryNormalize(fallback, out var normalizedFallback)
            ? normalizedFallback
            : Vector3.UnitY;
    }
}
