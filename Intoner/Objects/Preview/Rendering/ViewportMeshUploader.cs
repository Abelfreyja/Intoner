using Intoner.Objects.Assets;
using Intoner.Services.Gpu;
using SharpDX.Direct3D11;
using System.Numerics;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Objects.Preview.Rendering;

internal static class ViewportMeshUploader
{
    public static ViewportMesh Upload(
        Device device,
        ModelPreviewGeometryReader.PreviewGeometry geometry,
        Vector3 untexturedDiffuseColor,
        GpuTextureView whiteTexture)
    {
        List<ViewportMesh.Vertex> vertices = [];
        List<ViewportMesh.DrawRange> opaqueRanges = [];
        List<ViewportMesh.DrawRange> transparentRanges = [];
        Dictionary<int, List<int>> trianglesByMaterial = BuildTriangleGroups(geometry);
        Dictionary<string, GpuTextureView> texturesByPath = new(StringComparer.OrdinalIgnoreCase);
        List<GpuTextureView> ownedTextures = [];

        foreach ((int materialIndex, List<int> triangleIndices) in trianglesByMaterial.OrderBy(static pair => pair.Key))
        {
            ModelPreviewGeometryReader.PreviewMaterial? material = ResolveMaterial(geometry, materialIndex);
            List<ViewportMesh.DrawRange> targetRanges = IsTransparentMaterial(material)
                ? transparentRanges
                : opaqueRanges;
            int startVertex = vertices.Count;
            AppendTriangleVertices(vertices, geometry, triangleIndices);
            int vertexCount = vertices.Count - startVertex;
            if (vertexCount == 0)
            {
                continue;
            }

            GpuTextureView texture = ResolveDiffuseTexture(device, material, whiteTexture, texturesByPath, ownedTextures);
            targetRanges.Add(new ViewportMesh.DrawRange(
                startVertex,
                vertexCount,
                texture.View,
                material?.DiffuseTexture is not null,
                material?.ApplyAlphaClip ?? false,
                material?.Transparency ?? 1f,
                untexturedDiffuseColor));
        }

        if (vertices.Count == 0)
        {
            foreach (GpuTextureView ownedTexture in ownedTextures)
            {
                ownedTexture.Dispose();
            }

            throw new InvalidOperationException("preview mesh did not contain drawable vertices");
        }

        Buffer vertexBuffer = Buffer.Create(device, BindFlags.VertexBuffer, vertices.ToArray());
        return new ViewportMesh(vertexBuffer, opaqueRanges, transparentRanges, ownedTextures);
    }

    private static Dictionary<int, List<int>> BuildTriangleGroups(ModelPreviewGeometryReader.PreviewGeometry geometry)
    {
        Dictionary<int, List<int>> trianglesByMaterial = [];
        for (var triangleIndex = 0; triangleIndex < geometry.TriangleCount; triangleIndex++)
        {
            int materialIndex = -1;
            if (triangleIndex < geometry.TriangleMaterialIndices.Length)
            {
                materialIndex = geometry.TriangleMaterialIndices[triangleIndex];
                if (materialIndex < 0 || materialIndex >= geometry.Materials.Length)
                {
                    materialIndex = -1;
                }
            }

            if (!trianglesByMaterial.TryGetValue(materialIndex, out List<int>? triangleIndices))
            {
                triangleIndices = [];
                trianglesByMaterial.Add(materialIndex, triangleIndices);
            }

            triangleIndices.Add(triangleIndex);
        }

        return trianglesByMaterial;
    }

    private static void AppendTriangleVertices(
        List<ViewportMesh.Vertex> vertices,
        ModelPreviewGeometryReader.PreviewGeometry geometry,
        IReadOnlyList<int> triangleIndices)
    {
        for (var i = 0; i < triangleIndices.Count; i++)
        {
            int indexOffset = triangleIndices[i] * 3;
            if (indexOffset + 2 >= geometry.Indices.Length)
            {
                continue;
            }

            int index0 = geometry.Indices[indexOffset];
            int index1 = geometry.Indices[indexOffset + 1];
            int index2 = geometry.Indices[indexOffset + 2];
            if (!TryGetVertexData(geometry, index0, out Vector3 position0, out Vector2 uv0)
             || !TryGetVertexData(geometry, index1, out Vector3 position1, out Vector2 uv1)
             || !TryGetVertexData(geometry, index2, out Vector3 position2, out Vector2 uv2))
            {
                continue;
            }

            Vector3 normal = Vector3.Cross(position1 - position0, position2 - position0);
            float normalLengthSquared = normal.LengthSquared();
            normal = normalLengthSquared > 0.000000000001f
                ? normal / MathF.Sqrt(normalLengthSquared)
                : Vector3.UnitY;

            vertices.Add(new ViewportMesh.Vertex(position0, normal, uv0));
            vertices.Add(new ViewportMesh.Vertex(position1, normal, uv1));
            vertices.Add(new ViewportMesh.Vertex(position2, normal, uv2));
        }
    }

    private static bool TryGetVertexData(
        ModelPreviewGeometryReader.PreviewGeometry geometry,
        int vertexIndex,
        out Vector3 position,
        out Vector2 uv)
    {
        position = default;
        uv = default;
        if (vertexIndex < 0 || vertexIndex >= geometry.Positions.Length)
        {
            return false;
        }

        position = geometry.Positions[vertexIndex];
        if (vertexIndex < geometry.TexCoords.Length)
        {
            uv = geometry.TexCoords[vertexIndex];
        }

        return true;
    }

    private static ModelPreviewGeometryReader.PreviewMaterial? ResolveMaterial(
        ModelPreviewGeometryReader.PreviewGeometry geometry,
        int materialIndex)
        => materialIndex >= 0 && materialIndex < geometry.Materials.Length
            ? geometry.Materials[materialIndex]
            : null;

    private static bool IsTransparentMaterial(ModelPreviewGeometryReader.PreviewMaterial? material)
        => material is not null
            && (material.EnableTransparency || material.Transparency < 0.999f);

    private static GpuTextureView ResolveDiffuseTexture(
        Device device,
        ModelPreviewGeometryReader.PreviewMaterial? material,
        GpuTextureView whiteTexture,
        IDictionary<string, GpuTextureView> texturesByPath,
        ICollection<GpuTextureView> ownedTextures)
    {
        if (material?.DiffuseTexture is null || string.IsNullOrWhiteSpace(material.DiffuseTexturePath))
        {
            return whiteTexture;
        }

        if (texturesByPath.TryGetValue(material.DiffuseTexturePath, out GpuTextureView? texture))
        {
            return texture;
        }

        texture = CreateTexture(device, material.DiffuseTexture);
        texturesByPath.Add(material.DiffuseTexturePath, texture);
        ownedTextures.Add(texture);
        return texture;
    }

    private static GpuTextureView CreateTexture(Device device, ModelPreviewGeometryReader.PreviewTexture previewTexture)
        => GpuTextureView.CreateRgba(
            device,
            previewTexture.Width,
            previewTexture.Height,
            previewTexture.RgbaPixels);
}
