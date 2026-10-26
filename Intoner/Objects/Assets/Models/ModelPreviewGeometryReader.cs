using Intoner.Objects.Utils;
using Lumina.Data.Parsing;
using Penumbra.GameData.Files.Models;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using MdlFile = Penumbra.GameData.Files.Models.MdlFile;

namespace Intoner.Objects.Assets;

internal static class ModelPreviewGeometryReader
{
    private const int MaxStreams = 3;

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PreviewVertexFormat(
        MdlStructs.VertexElement PositionElement,
        MdlStructs.VertexElement? UvElement);

    internal sealed class PreviewTexture(byte[] rgbaPixels, int width, int height)
    {
        public byte[] RgbaPixels { get; } = rgbaPixels;
        public int Width { get; } = width;
        public int Height { get; } = height;
    }

    internal sealed class PreviewMaterial(
        string name,
        string? diffuseTexturePath,
        PreviewTexture? diffuseTexture,
        bool applyAlphaClip,
        bool enableTransparency,
        float transparency)
    {
        public string Name { get; } = name;
        public string? DiffuseTexturePath { get; } = diffuseTexturePath;
        public PreviewTexture? DiffuseTexture { get; set; } = diffuseTexture;
        public bool ApplyAlphaClip { get; } = applyAlphaClip;
        public bool EnableTransparency { get; } = enableTransparency;
        public float Transparency { get; } = transparency;
    }

    internal sealed class PreviewBounds(Vector3 center, float radius)
    {
        public Vector3 Center { get; } = center;
        public float Radius { get; } = radius;
    }

    internal sealed class PreviewGeometry
    {
        public PreviewGeometry(
            Vector3[] positions,
            Vector2[] texCoords,
            int[] indices,
            int[] triangleMaterialIndices,
            PreviewMaterial[] materials)
            : this(positions, texCoords, indices, triangleMaterialIndices, materials,
                BuildPreviewBounds(positions), EstimatePreviewGeometryByteCount(positions, texCoords, indices, triangleMaterialIndices))
        { }

        private PreviewGeometry(
            Vector3[] positions,
            Vector2[] texCoords,
            int[] indices,
            int[] triangleMaterialIndices,
            PreviewMaterial[] materials,
            PreviewBounds bounds,
            long byteCount)
        {
            Positions = positions;
            TexCoords = texCoords;
            Indices = indices;
            TriangleMaterialIndices = triangleMaterialIndices;
            Materials = materials;
            Bounds = bounds;
            EstimatedByteCount = byteCount;
        }

        public Vector3[] Positions { get; }
        public Vector2[] TexCoords { get; }
        public int[] Indices { get; }
        public int[] TriangleMaterialIndices { get; }
        public PreviewMaterial[] Materials { get; }
        public PreviewBounds Bounds { get; }
        public int TriangleCount => Math.Min(Indices.Length / 3, TriangleMaterialIndices.Length);
        public long EstimatedByteCount { get; }

        public PreviewGeometry CreateRenderSnapshot()
            => new(Positions, TexCoords, Indices, TriangleMaterialIndices,
                Array.ConvertAll(Materials, static material => new PreviewMaterial(
                    material.Name, material.DiffuseTexturePath, material.DiffuseTexture,
                    material.ApplyAlphaClip, material.EnableTransparency, material.Transparency)),
                Bounds, EstimatedByteCount);

        private static PreviewBounds BuildPreviewBounds(IReadOnlyList<Vector3> positions)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (var i = 0; i < positions.Count; i++)
            {
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
            }

            Vector3 center = new(
                (float)(((double)min.X + max.X) * 0.5),
                (float)(((double)min.Y + max.Y) * 0.5),
                (float)(((double)min.Z + max.Z) * 0.5));
            double radiusSquared = 0;
            for (var i = 0; i < positions.Count; i++)
            {
                Vector3 position = positions[i];
                double x = (double)position.X - center.X;
                double y = (double)position.Y - center.Y;
                double z = (double)position.Z - center.Z;
                radiusSquared = Math.Max(radiusSquared, x * x + y * y + z * z);
            }

            float radius = (float)Math.Sqrt(radiusSquared);
            return new PreviewBounds(center, MathF.Max(0.001f, radius));
        }

        private static long EstimatePreviewGeometryByteCount(
            IReadOnlyCollection<Vector3> positions,
            IReadOnlyCollection<Vector2> texCoords,
            IReadOnlyCollection<int> indices,
            IReadOnlyCollection<int> triangleMaterialIndices)
            => (positions.Count * 3L * sizeof(float))
             + (texCoords.Count * 2L * sizeof(float))
             + (indices.Count * sizeof(int))
             + (triangleMaterialIndices.Count * sizeof(int));
    }

    public static bool TryLoad(
        byte[] data,
        out PreviewGeometry geometry,
        out string? reason)
    {
        geometry = null!;
        reason = null;

        MdlFile? mdl = ModelFileReader.Read(data);

        if (mdl is null)
        {
            reason = "Invalid model file.";
            return false;
        }

        if (HasShapeData(mdl))
        {
            reason = "Preview does not support shape data models.";
            return false;
        }

        MeshStruct[] meshes = mdl.Meshes;
        if (mdl.LodCount <= 0 || meshes.Length == 0)
        {
            reason = "Model does not contain previewable meshes.";
            return false;
        }

        const int lodIndex = 0;
        var lod = mdl.Lods[lodIndex];
        if (lod.MeshCount == 0)
        {
            reason = "Model LOD does not contain previewable meshes.";
            return false;
        }

        var lodMeshStart = (int)lod.MeshIndex;
        var lodMeshEnd = lodMeshStart + lod.MeshCount;
        if (lodMeshStart < 0 || lodMeshEnd > meshes.Length || lodMeshEnd > mdl.VertexDeclarations.Length)
        {
            reason = "Model LOD mesh range is invalid.";
            return false;
        }

        List<Vector3> positions = [];
        List<Vector2> texCoords = [];
        List<int> indices = [];
        List<int> triangleMaterialIndices = [];
        var materials = Array.ConvertAll(mdl.Materials, static name => new PreviewMaterial(name, null, null, false, false, 1f));
        string? firstReason = null;

        for (var meshIndex = lodMeshStart; meshIndex < lodMeshEnd; meshIndex++)
        {
            var mesh = meshes[meshIndex];
            if (mesh.VertexCount == 0 || mesh.IndexCount == 0)
            {
                firstReason ??= "Mesh does not contain indexed geometry.";
                continue;
            }

            if (!TryBuildPreviewVertexFormat(mdl.VertexDeclarations[meshIndex], out var format, out var formatReason))
            {
                reason = $"Mesh {meshIndex}: {formatReason}";
                return false;
            }

            if (mesh.SubMeshIndex + mesh.SubMeshCount > mdl.SubMeshes.Length)
            {
                reason = $"Mesh {meshIndex}: Submesh range is invalid.";
                return false;
            }

            ReadOnlySpan<MdlStructs.SubmeshStruct> meshSubMeshes = mdl.SubMeshes.AsSpan(mesh.SubMeshIndex, mesh.SubMeshCount);

            if (!TryDecodePreviewMeshData(
                    mdl,
                    lodIndex,
                    mesh,
                    format,
                    meshSubMeshes,
                    out var previewPositions,
                    out var previewTexCoords,
                    out var subMeshIndices,
                    out var decodeReason))
            {
                reason = $"Mesh {meshIndex}: {decodeReason}";
                return false;
            }

            int baseVertexIndex = positions.Count;
            positions.EnsureCapacity(checked(baseVertexIndex + previewPositions.Length));
            texCoords.EnsureCapacity(checked(baseVertexIndex + previewTexCoords.Length));
            positions.AddRange(previewPositions);
            texCoords.AddRange(previewTexCoords);

            var materialIndex = format.UvElement.HasValue && mesh.MaterialIndex < materials.Length ? mesh.MaterialIndex : -1;
            foreach (var subMesh in subMeshIndices)
            {
                for (var index = 0; index + 2 < subMesh.Length; index += 3)
                {
                    indices.Add(baseVertexIndex + subMesh[index]);
                    indices.Add(baseVertexIndex + subMesh[index + 1]);
                    indices.Add(baseVertexIndex + subMesh[index + 2]);
                    triangleMaterialIndices.Add(materialIndex);
                }
            }
        }

        if (positions.Count == 0 || indices.Count < 3)
        {
            reason = firstReason ?? "Model did not expose previewable geometry.";
            return false;
        }

        PreviewGeometry previewGeometry = new(
            positions.ToArray(),
            texCoords.ToArray(),
            indices.ToArray(),
            triangleMaterialIndices.ToArray(),
            materials);

        if (!NumericsUtility.IsFinite(previewGeometry.Bounds.Center) || !float.IsFinite(previewGeometry.Bounds.Radius))
        {
            reason = "Model bounds contain a non-finite value.";
            return false;
        }

        geometry = previewGeometry;
        return true;
    }

    private static bool TryBuildPreviewVertexFormat(
        MdlStructs.VertexDeclarationStruct declaration,
        out PreviewVertexFormat format,
        out string? reason)
    {
        format = default;
        reason = null;
        MdlStructs.VertexElement? position = null;
        MdlStructs.VertexElement? uv = null;

        foreach (MdlStructs.VertexElement element in declaration.VertexElements)
        {
            if ((MdlFile.VertexUsage)element.Usage == MdlFile.VertexUsage.Position)
            {
                if (position.HasValue || GetVertexSize((MdlFile.VertexType)element.Type) == 0)
                {
                    reason = "Expected one supported position element.";
                    return false;
                }

                position = element;
            }
            else if ((MdlFile.VertexUsage)element.Usage == MdlFile.VertexUsage.UV
                  && (!uv.HasValue || element.UsageIndex < uv.Value.UsageIndex))
            {
                uv = element;
            }
        }

        if (!position.HasValue)
        {
            reason = "Expected one supported position element.";
            return false;
        }

        if (uv.HasValue && GetVertexSize((MdlFile.VertexType)uv.Value.Type) == 0)
        {
            reason = "Expected a supported UV element.";
            return false;
        }

        format = new PreviewVertexFormat(position.Value, uv);
        return true;
    }

    private static bool TryDecodePreviewMeshData(
        MdlFile mdl,
        int lodIndex,
        MeshStruct mesh,
        PreviewVertexFormat format,
        ReadOnlySpan<MdlStructs.SubmeshStruct> meshSubMeshes,
        out Vector3[] positions,
        out Vector2[] texCoords,
        out int[][] subMeshIndices,
        out string? reason)
    {
        positions = [];
        texCoords = [];
        subMeshIndices = [];
        reason = null;
        ReadOnlySpan<byte> data = mdl.RemainingData;
        long vertexBufferEnd = (long)mdl.VertexOffset[lodIndex] + mdl.VertexBufferSize[lodIndex];

        if (vertexBufferEnd > data.Length
         || !TryGetElementRange(mdl, lodIndex, mesh, format.PositionElement, vertexBufferEnd, out int positionStart, out int positionStride))
        {
            reason = "Position buffer range is invalid.";
            return false;
        }

        int uvStart = 0;
        int uvStride = 0;

        if (format.UvElement is MdlStructs.VertexElement uv
         && !TryGetElementRange(mdl, lodIndex, mesh, uv, vertexBufferEnd, out uvStart, out uvStride))
        {
            reason = "UV buffer range is invalid.";
            return false;
        }

        long indexStart = (long)mdl.IndexOffset[lodIndex] + (long)mesh.StartIndex * sizeof(ushort);
        long indexEnd = indexStart + (long)mesh.IndexCount * sizeof(ushort);
        long indexBufferEnd = (long)mdl.IndexOffset[lodIndex] + mdl.IndexBufferSize[lodIndex];

        if (indexStart > data.Length || indexEnd > data.Length || indexEnd > indexBufferEnd || mesh.IndexCount > int.MaxValue)
        {
            reason = "Index buffer range is invalid.";
            return false;
        }

        int[] meshIndices = new int[(int)mesh.IndexCount];

        for (int index = 0; index < meshIndices.Length; ++index)
        {
            int vertexIndex = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice((int)indexStart + index * sizeof(ushort)));

            if (vertexIndex >= mesh.VertexCount)
            {
                reason = "Mesh index refers to a missing vertex.";
                return false;
            }

            meshIndices[index] = vertexIndex;
        }

        subMeshIndices = meshSubMeshes.Length == 0 ? [meshIndices] : new int[meshSubMeshes.Length][];

        for (int subMeshIndex = 0; subMeshIndex < meshSubMeshes.Length; ++subMeshIndex)
        {
            MdlStructs.SubmeshStruct subMesh = meshSubMeshes[subMeshIndex];
            long relativeOffset = (long)subMesh.IndexOffset - mesh.StartIndex;

            if (relativeOffset < 0 || relativeOffset + subMesh.IndexCount > meshIndices.Length)
            {
                reason = "Submesh index range is invalid.";
                return false;
            }

            subMeshIndices[subMeshIndex] = meshIndices.AsSpan((int)relativeOffset, (int)subMesh.IndexCount).ToArray();
        }

        positions = new Vector3[mesh.VertexCount];
        texCoords = new Vector2[mesh.VertexCount];

        for (int vertexIndex = 0; vertexIndex < mesh.VertexCount; ++vertexIndex)
        {
            Vector4 position = ReadPreviewVertex((MdlFile.VertexType)format.PositionElement.Type,
                data.Slice(positionStart + vertexIndex * positionStride));

            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            {
                reason = "Position contains a non-finite value.";
                return false;
            }

            positions[vertexIndex] = new Vector3(position.X, position.Y, position.Z);

            if (format.UvElement is MdlStructs.VertexElement uvElement)
            {
                Vector4 uvValue = ReadPreviewVertex((MdlFile.VertexType)uvElement.Type,
                    data.Slice(uvStart + vertexIndex * uvStride));
                texCoords[vertexIndex] = new Vector2(uvValue.X, uvValue.Y);
            }
        }

        return true;
    }

    private static bool TryGetElementRange(
        MdlFile mdl,
        int lodIndex,
        MeshStruct mesh,
        MdlStructs.VertexElement element,
        long bufferEnd,
        out int start,
        out int stride)
    {
        start = 0;
        stride = 0;

        if (element.Stream >= MaxStreams)
        {
            return false;
        }

        int size = GetVertexSize((MdlFile.VertexType)element.Type);
        stride = mesh.VertexBufferStride(element.Stream);
        long offset = (long)mdl.VertexOffset[lodIndex] + mesh.VertexBufferOffset(element.Stream) + element.Offset;
        long end = offset + (long)(mesh.VertexCount - 1) * stride + size;

        if (size == 0 || element.Offset + size > stride || offset > int.MaxValue || end > bufferEnd)
        {
            return false;
        }

        start = (int)offset;
        return true;
    }

    private static bool HasShapeData(MdlFile mdl)
        => mdl.Shapes.Length > 0
            || mdl.ShapeMeshes.Length > 0
            || mdl.ShapeValues.Length > 0
            || mdl.NeckMorphs.Length > 0;

    private static int GetVertexSize(MdlFile.VertexType type)
        => type switch
        {
            MdlFile.VertexType.Single1 => 4,
            MdlFile.VertexType.Single2 => 8,
            MdlFile.VertexType.Single3 => 12,
            MdlFile.VertexType.Single4 => 16,
            MdlFile.VertexType.Half2 or MdlFile.VertexType.Short2 or MdlFile.VertexType.NShort2
                or MdlFile.VertexType.UShort2 or MdlFile.VertexType.UByte4 or MdlFile.VertexType.NByte4 => 4,
            MdlFile.VertexType.Half4 or MdlFile.VertexType.Short4 or MdlFile.VertexType.NShort4
                or MdlFile.VertexType.UShort4 => 8,
            _ => 0,
        };

    private static Vector4 ReadPreviewVertex(MdlFile.VertexType type, ReadOnlySpan<byte> data)
    {
        switch (type)
        {
            case MdlFile.VertexType.Single1:
                return new Vector4(BinaryPrimitives.ReadSingleLittleEndian(data), 0, 0, 0);
            case MdlFile.VertexType.Single2:
                return new Vector4(MemoryMarshal.Read<Vector2>(data), 0, 0);
            case MdlFile.VertexType.Single3:
                return new Vector4(MemoryMarshal.Read<Vector3>(data), 0);
            case MdlFile.VertexType.Single4:
                return MemoryMarshal.Read<Vector4>(data);
        }

        int components = type switch
        {
            MdlFile.VertexType.Half4 or MdlFile.VertexType.Short4
                or MdlFile.VertexType.NShort4 or MdlFile.VertexType.UShort4
                or MdlFile.VertexType.UByte4 or MdlFile.VertexType.NByte4 => 4,
            _ => 2,
        };
        int componentSize = GetVertexSize(type) / components;
        Vector4 value = default;

        for (int component = 0; component < components; ++component)
        {
            ReadOnlySpan<byte> bytes = data[(component * componentSize)..];
            value[component] = type switch
            {
                MdlFile.VertexType.Half2 or MdlFile.VertexType.Half4 => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes)),
                MdlFile.VertexType.Short2 or MdlFile.VertexType.Short4 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
                MdlFile.VertexType.NShort2 or MdlFile.VertexType.NShort4 => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(bytes) / (float)short.MaxValue, -1f),
                MdlFile.VertexType.UShort2 or MdlFile.VertexType.UShort4 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                MdlFile.VertexType.UByte4 => bytes[0],
                MdlFile.VertexType.NByte4 => bytes[0] / 255f,
                _ => throw new InvalidOperationException($"Unsupported preview vertex type {type}"),
            };
        }

        return value;
    }
}
