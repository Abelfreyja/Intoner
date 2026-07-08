using Lumina.Data.Parsing;
using Penumbra.GameData.Files.ModelStructs;
using System.Numerics;
using MdlFile = Penumbra.GameData.Files.MdlFile;

namespace Intoner.Objects.Assets;

internal static class ModelPreviewGeometryReader
{
    private const int MaxStreams = 3;

    private sealed class PreviewVertexFormat(
        List<MdlStructs.VertexElement> sortedElements,
        MdlStructs.VertexElement positionElement,
        MdlStructs.VertexElement? uvElement)
    {
        public List<MdlStructs.VertexElement> SortedElements { get; } = sortedElements;
        public MdlStructs.VertexElement PositionElement { get; } = positionElement;
        public MdlStructs.VertexElement? UvElement { get; } = uvElement;
    }

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

    internal sealed class PreviewGeometry(
        Vector3[] positions,
        Vector2[] texCoords,
        int[] indices,
        int[] triangleMaterialIndices,
        PreviewMaterial[] materials)
    {
        public Vector3[] Positions { get; } = positions;
        public Vector2[] TexCoords { get; } = texCoords;
        public int[] Indices { get; } = indices;
        public int[] TriangleMaterialIndices { get; } = triangleMaterialIndices;
        public PreviewMaterial[] Materials { get; } = materials;
        public PreviewBounds Bounds { get; } = BuildPreviewBounds(positions);
        public int TriangleCount { get; } = Math.Min(indices.Length / 3, triangleMaterialIndices.Length);
        public long EstimatedByteCount { get; } = EstimatePreviewGeometryByteCount(positions, texCoords, indices, triangleMaterialIndices);

        private static PreviewBounds BuildPreviewBounds(IReadOnlyList<Vector3> positions)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (var i = 0; i < positions.Count; i++)
            {
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
            }

            var center = (min + max) * 0.5f;
            var radiusSquared = 0f;
            for (var i = 0; i < positions.Count; i++)
            {
                radiusSquared = MathF.Max(radiusSquared, Vector3.DistanceSquared(positions[i], center));
            }

            return new PreviewBounds(center, MathF.Max(0.001f, MathF.Sqrt(radiusSquared)));
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

        var mdl = new MdlFile(data);
        if (!mdl.Valid)
        {
            reason = "Invalid model file.";
            return false;
        }

        if (HasShapeData(mdl))
        {
            reason = "Preview does not support shape data models.";
            return false;
        }

        var meshes = mdl.Meshes.ToArray();
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
        if (lodMeshStart < 0 || lodMeshEnd > meshes.Length)
        {
            reason = "Model LOD mesh range is invalid.";
            return false;
        }

        var positions = new List<Vector3>();
        var texCoords = new List<Vector2>();
        var indices = new List<int>();
        var triangleMaterialIndices = new List<int>();
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
                firstReason ??= $"Mesh {meshIndex}: {formatReason}";
                continue;
            }

            var meshSubMeshes = mdl.SubMeshes
                .Skip(mesh.SubMeshIndex)
                .Take(mesh.SubMeshCount)
                .ToArray();

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
                firstReason ??= $"Mesh {meshIndex}: {decodeReason}";
                continue;
            }

            var baseVertexIndex = positions.Count;
            for (var i = 0; i < previewPositions.Length; i++)
            {
                positions.Add(previewPositions[i]);
                texCoords.Add(previewTexCoords[i]);
            }

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

        geometry = new PreviewGeometry(
            positions.ToArray(),
            texCoords.ToArray(),
            indices.ToArray(),
            triangleMaterialIndices.ToArray(),
            materials);
        return true;
    }

    public static bool TryLoadPreviewGeometry(
        byte[] data,
        out PreviewGeometry geometry,
        out string? reason)
        => TryLoad(data, out geometry, out reason);

    private static bool TryBuildPreviewVertexFormat(
        MdlStructs.VertexDeclarationStruct declaration,
        out PreviewVertexFormat format,
        out string? reason)
    {
        format = default!;
        reason = null;

        var elements = declaration.VertexElements;
        foreach (var element in elements)
        {
            if (element.Stream >= MaxStreams)
            {
                reason = "Vertex stream index out of range.";
                return false;
            }

            if (!CanReadPreviewVertexType((MdlFile.VertexType)element.Type))
            {
                reason = $"Unsupported preview vertex type {(MdlFile.VertexType)element.Type}.";
                return false;
            }
        }

        var positionElements = elements
            .Where(static element => (MdlFile.VertexUsage)element.Usage == MdlFile.VertexUsage.Position)
            .ToArray();
        if (positionElements.Length != 1)
        {
            reason = "Expected single position element.";
            return false;
        }

        var positionType = (MdlFile.VertexType)positionElements[0].Type;
        if (!CanReadPreviewPositionType(positionType))
        {
            reason = $"Unsupported position element type {positionType}.";
            return false;
        }

        MdlStructs.VertexElement? uvElement = null;
        foreach (var element in elements
                     .Where(static element => (MdlFile.VertexUsage)element.Usage == MdlFile.VertexUsage.UV)
                     .OrderBy(static element => element.UsageIndex))
        {
            if (CanReadPreviewUvType((MdlFile.VertexType)element.Type))
            {
                uvElement = element;
                break;
            }
        }

        format = new PreviewVertexFormat(
            elements.OrderBy(static element => element.Offset).ToList(),
            positionElements[0],
            uvElement);
        return true;
    }

    private static bool TryDecodePreviewMeshData(
        MdlFile mdl,
        int lodIndex,
        MeshStruct mesh,
        PreviewVertexFormat format,
        MdlStructs.SubmeshStruct[] meshSubMeshes,
        out Vector3[] positions,
        out Vector2[] texCoords,
        out int[][] subMeshIndices,
        out string? reason)
    {
        positions = [];
        texCoords = [];
        subMeshIndices = [];
        reason = null;

        if (meshSubMeshes.Length == 0)
        {
            subMeshIndices = [ReadIndices(mdl, lodIndex, mesh)];
        }
        else if (!TryBuildSubMeshIndices(mdl, lodIndex, mesh, meshSubMeshes, out subMeshIndices, out reason))
        {
            return false;
        }

        var vertexCount = mesh.VertexCount;
        positions = new Vector3[vertexCount];
        texCoords = new Vector2[vertexCount];

        var streams = new BinaryReader[MaxStreams];
        for (var streamIndex = 0; streamIndex < MaxStreams; streamIndex++)
        {
            streams[streamIndex] = new BinaryReader(new MemoryStream(mdl.RemainingData));
            streams[streamIndex].BaseStream.Position = mdl.VertexOffset[lodIndex] + mesh.VertexBufferOffset(streamIndex);
        }

        for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            foreach (var element in format.SortedElements)
            {
                var usage = (MdlFile.VertexUsage)element.Usage;
                var type = (MdlFile.VertexType)element.Type;
                var stream = streams[element.Stream];

                if (usage == MdlFile.VertexUsage.Position && IsSameElement(element, format.PositionElement))
                {
                    positions[vertexIndex] = ReadPreviewPosition(type, stream);
                    continue;
                }

                if (format.UvElement.HasValue
                 && usage == MdlFile.VertexUsage.UV
                 && IsSameElement(element, format.UvElement.Value))
                {
                    texCoords[vertexIndex] = ReadPreviewUv(type, stream);
                    continue;
                }

                _ = ReadAndDiscard(type, stream);
            }
        }

        foreach (var stream in streams)
        {
            stream.Dispose();
        }

        return true;
    }

    private static bool TryBuildSubMeshIndices(
        MdlFile mdl,
        int lodIndex,
        MeshStruct mesh,
        MdlStructs.SubmeshStruct[] meshSubMeshes,
        out int[][] subMeshIndices,
        out string? reason)
    {
        reason = null;
        subMeshIndices = new int[meshSubMeshes.Length][];
        var meshIndices = ReadIndices(mdl, lodIndex, mesh);

        for (var subMeshIndex = 0; subMeshIndex < meshSubMeshes.Length; subMeshIndex++)
        {
            var subMesh = meshSubMeshes[subMeshIndex];
            if (subMesh.IndexCount == 0)
            {
                subMeshIndices[subMeshIndex] = [];
                continue;
            }

            var relativeOffset = (int)(subMesh.IndexOffset - mesh.StartIndex);
            if (relativeOffset < 0 || relativeOffset + subMesh.IndexCount > meshIndices.Length)
            {
                reason = "Submesh index range out of bounds.";
                return false;
            }

            var slice = new int[subMesh.IndexCount];
            Array.Copy(meshIndices, relativeOffset, slice, 0, slice.Length);
            subMeshIndices[subMeshIndex] = slice;
        }

        return true;
    }

    private static int[] ReadIndices(MdlFile mdl, int lodIndex, MeshStruct mesh)
    {
        using var reader = new BinaryReader(new MemoryStream(mdl.RemainingData));
        reader.BaseStream.Position = mdl.IndexOffset[lodIndex] + mesh.StartIndex * sizeof(ushort);

        var indices = new int[mesh.IndexCount];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = reader.ReadUInt16();
        }

        return indices;
    }

    private static bool HasShapeData(MdlFile mdl)
        => mdl.Shapes.Length > 0
            || mdl.ShapeMeshes.Length > 0
            || mdl.ShapeValues.Length > 0
            || mdl.NeckMorphs.Length > 0;

    private static bool CanReadPreviewPositionType(MdlFile.VertexType type)
        => type switch
        {
            MdlFile.VertexType.Single3 => true,
            MdlFile.VertexType.Single4 => true,
            MdlFile.VertexType.Half4 => true,
            MdlFile.VertexType.Short4 => true,
            MdlFile.VertexType.NShort4 => true,
            MdlFile.VertexType.UShort4 => true,
            MdlFile.VertexType.UByte4 => true,
            MdlFile.VertexType.NByte4 => true,
            _ => false,
        };

    private static bool CanReadPreviewUvType(MdlFile.VertexType type)
        => type switch
        {
            MdlFile.VertexType.Single1 => true,
            MdlFile.VertexType.Half2 => true,
            MdlFile.VertexType.Single2 => true,
            MdlFile.VertexType.Short2 => true,
            MdlFile.VertexType.NShort2 => true,
            MdlFile.VertexType.UShort2 => true,
            MdlFile.VertexType.Half4 => true,
            MdlFile.VertexType.Single4 => true,
            MdlFile.VertexType.Short4 => true,
            MdlFile.VertexType.NShort4 => true,
            MdlFile.VertexType.UShort4 => true,
            _ => false,
        };

    private static bool CanReadPreviewVertexType(MdlFile.VertexType type)
        => type switch
        {
            MdlFile.VertexType.Single1 => true,
            MdlFile.VertexType.Single2 => true,
            MdlFile.VertexType.Single3 => true,
            MdlFile.VertexType.Single4 => true,
            MdlFile.VertexType.Half2 => true,
            MdlFile.VertexType.Half4 => true,
            MdlFile.VertexType.UByte4 => true,
            MdlFile.VertexType.NByte4 => true,
            MdlFile.VertexType.Short2 => true,
            MdlFile.VertexType.Short4 => true,
            MdlFile.VertexType.NShort2 => true,
            MdlFile.VertexType.NShort4 => true,
            MdlFile.VertexType.UShort2 => true,
            MdlFile.VertexType.UShort4 => true,
            _ => false,
        };

    private static Vector3 ReadPreviewPosition(MdlFile.VertexType type, BinaryReader reader)
        => type switch
        {
            MdlFile.VertexType.Single3 => ReadPosition(reader),
            MdlFile.VertexType.Single4 => ReadPositionWithW(reader, out _),
            MdlFile.VertexType.Half4 => ToPreviewPosition(ReadAndDiscard(type, reader)),
            MdlFile.VertexType.Short4 => ToPreviewPosition(ReadAndDiscard(type, reader)),
            MdlFile.VertexType.NShort4 => ToPreviewPosition(ReadAndDiscard(type, reader)),
            MdlFile.VertexType.UShort4 => ToPreviewPosition(ReadAndDiscard(type, reader)),
            MdlFile.VertexType.UByte4 => ToPreviewPosition(ReadAndDiscard(type, reader)),
            MdlFile.VertexType.NByte4 => ToPreviewPosition(ReadAndDiscard(type, reader)),
            _ => throw new InvalidOperationException($"Unsupported preview position type {type}"),
        };

    private static Vector2 ReadPreviewUv(MdlFile.VertexType type, BinaryReader reader)
        => type switch
        {
            MdlFile.VertexType.Single1 => new Vector2(reader.ReadSingle(), 0f),
            MdlFile.VertexType.Half2 => new Vector2(ReadHalf(reader), ReadHalf(reader)),
            MdlFile.VertexType.Single2 => new Vector2(reader.ReadSingle(), reader.ReadSingle()),
            MdlFile.VertexType.Short2 => ReadShort2(reader),
            MdlFile.VertexType.NShort2 => ReadNShort2(reader),
            MdlFile.VertexType.UShort2 => ReadUShort2(reader),
            MdlFile.VertexType.Half4 => ToPreviewUv(ReadAndDiscard(type, reader)),
            MdlFile.VertexType.Single4 => ToPreviewUv(ReadAndDiscard(type, reader)),
            MdlFile.VertexType.Short4 => ToPreviewUv(ReadAndDiscard(type, reader)),
            MdlFile.VertexType.NShort4 => ToPreviewUv(ReadAndDiscard(type, reader)),
            MdlFile.VertexType.UShort4 => ToPreviewUv(ReadAndDiscard(type, reader)),
            _ => throw new InvalidOperationException($"Unsupported preview uv type {type}"),
        };

    private static bool IsSameElement(MdlStructs.VertexElement left, MdlStructs.VertexElement right)
        => left.Stream == right.Stream
        && left.Offset == right.Offset
        && left.Type == right.Type
        && left.Usage == right.Usage
        && left.UsageIndex == right.UsageIndex;

    private static Vector3 ReadPosition(BinaryReader reader)
        => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static Vector3 ReadPositionWithW(BinaryReader reader, out float w)
    {
        var x = reader.ReadSingle();
        var y = reader.ReadSingle();
        var z = reader.ReadSingle();
        w = reader.ReadSingle();
        return new Vector3(x, y, z);
    }

    private static Vector2 ReadShort2(BinaryReader reader)
        => new(reader.ReadInt16(), reader.ReadInt16());

    private static Vector2 ReadUShort2(BinaryReader reader)
        => new(reader.ReadUInt16(), reader.ReadUInt16());

    private static Vector2 ReadUShort2Normalized(BinaryReader reader)
        => new(reader.ReadUInt16() / (float)ushort.MaxValue, reader.ReadUInt16() / (float)ushort.MaxValue);

    private static Vector2 ReadNShort2(BinaryReader reader)
    {
        var value = ReadUShort2Normalized(reader);
        return (value * 2f) - Vector2.One;
    }

    private static Vector4 ReadUByte4(BinaryReader reader)
        => new(
            reader.ReadByte() / 255f,
            reader.ReadByte() / 255f,
            reader.ReadByte() / 255f,
            reader.ReadByte() / 255f);

    private static Vector4 ReadNByte4(BinaryReader reader)
        => (ReadUByte4(reader) * 2f) - Vector4.One;

    private static Vector4 ReadShort4(BinaryReader reader)
        => new(reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16());

    private static Vector4 ReadUShort4(BinaryReader reader)
        => new(reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16());

    private static Vector4 ReadUShort4Normalized(BinaryReader reader)
        => new(
            reader.ReadUInt16() / (float)ushort.MaxValue,
            reader.ReadUInt16() / (float)ushort.MaxValue,
            reader.ReadUInt16() / (float)ushort.MaxValue,
            reader.ReadUInt16() / (float)ushort.MaxValue);

    private static Vector4 ReadNShort4(BinaryReader reader)
        => (ReadUShort4Normalized(reader) * 2f) - Vector4.One;

    private static Vector4 ReadAndDiscard(MdlFile.VertexType type, BinaryReader reader)
        => type switch
        {
            MdlFile.VertexType.Single1 => new Vector4(reader.ReadSingle(), 0, 0, 0),
            MdlFile.VertexType.Single2 => new Vector4(reader.ReadSingle(), reader.ReadSingle(), 0, 0),
            MdlFile.VertexType.Single3 => new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), 0),
            MdlFile.VertexType.Single4 => new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            MdlFile.VertexType.Half2 => new Vector4(ReadHalf(reader), ReadHalf(reader), 0, 0),
            MdlFile.VertexType.Half4 => new Vector4(ReadHalf(reader), ReadHalf(reader), ReadHalf(reader), ReadHalf(reader)),
            MdlFile.VertexType.UByte4 => ReadUByte4(reader),
            MdlFile.VertexType.NByte4 => ReadNByte4(reader),
            MdlFile.VertexType.Short2 => ToVector4(ReadShort2(reader)),
            MdlFile.VertexType.Short4 => ReadShort4(reader),
            MdlFile.VertexType.NShort2 => ToVector4(ReadNShort2(reader)),
            MdlFile.VertexType.NShort4 => ReadNShort4(reader),
            MdlFile.VertexType.UShort2 => ToVector4(ReadUShort2(reader)),
            MdlFile.VertexType.UShort4 => ReadUShort4(reader),
            _ => Vector4.Zero,
        };

    private static Vector3 ToPreviewPosition(Vector4 value)
        => new(value.X, value.Y, value.Z);

    private static Vector2 ToPreviewUv(Vector4 value)
        => new(value.X, value.Y);

    private static Vector4 ToVector4(Vector2 value)
        => new(value.X, value.Y, 0, 0);

    private static float ReadHalf(BinaryReader reader)
        => (float)BitConverter.UInt16BitsToHalf(reader.ReadUInt16());
}
