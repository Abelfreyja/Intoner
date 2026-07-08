using System.Numerics;
using PreviewGeometryReader = Intoner.Objects.Assets.ModelPreviewGeometryReader;

namespace Intoner.Objects.Preview.Rendering;

internal static class ThumbnailProjector
{
    private const float TinyTriangleNormalLengthSquaredThreshold = 0.000000000001f;

    public static void Build(
        PreviewGeometryReader.PreviewGeometry geometry,
        ThumbnailTriangle[] projectedTriangles,
        PreviewScene.Frame scene,
        int width,
        int height,
        Vector3 untexturedDiffuseColor,
        CancellationToken cancellationToken)
    {
        for (var triangleIndex = 0; triangleIndex < geometry.TriangleCount; triangleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            projectedTriangles[triangleIndex] = Project(
                geometry,
                triangleIndex,
                scene,
                width,
                height,
                untexturedDiffuseColor);
        }
    }

    private static ThumbnailTriangle Project(
        PreviewGeometryReader.PreviewGeometry geometry,
        int triangleIndex,
        PreviewScene.Frame scene,
        int width,
        int height,
        Vector3 untexturedDiffuseColor)
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

        var viewDirection = PreviewScene.ResolveDirection(scene.CameraPosition - centroid, Vector3.UnitZ);
        var diffuse = MathF.Abs(Vector3.Dot(normal, scene.LightDirection));
        var facing = MathF.Abs(Vector3.Dot(normal, viewDirection));
        var lighting = Math.Clamp(0.20f + (diffuse * 0.60f) + (facing * 0.20f), 0f, 1f);

        if (!TryProject(world0, scene.View, scene.ViewProjection, width, height, PreviewScene.NearPlane, out var screen0, out var depth0)
         || !TryProject(world1, scene.View, scene.ViewProjection, width, height, PreviewScene.NearPlane, out var screen1, out var depth1)
         || !TryProject(world2, scene.View, scene.ViewProjection, width, height, PreviewScene.NearPlane, out var screen2, out var depth2))
        {
            return default;
        }

        var uv0 = GetPreviewTexCoord(geometry.TexCoords, index0);
        var uv1 = GetPreviewTexCoord(geometry.TexCoords, index1);
        var uv2 = GetPreviewTexCoord(geometry.TexCoords, index2);

        PreviewGeometryReader.PreviewTexture? diffuseTexture = null;
        var applyAlphaClip = false;
        var enableTransparency = false;
        var transparency = 1f;
        if ((uint)triangleIndex < geometry.TriangleMaterialIndices.Length)
        {
            int materialIndex = geometry.TriangleMaterialIndices[triangleIndex];
            if ((uint)materialIndex < geometry.Materials.Length)
            {
                PreviewGeometryReader.PreviewMaterial material = geometry.Materials[materialIndex];
                diffuseTexture = material.DiffuseTexture;
                applyAlphaClip = material.ApplyAlphaClip;
                enableTransparency = material.EnableTransparency;
                transparency = material.Transparency;
            }
        }

        return new ThumbnailTriangle(
            true,
            screen0,
            screen1,
            screen2,
            depth0,
            depth1,
            depth2,
            lighting,
            untexturedDiffuseColor,
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
}
