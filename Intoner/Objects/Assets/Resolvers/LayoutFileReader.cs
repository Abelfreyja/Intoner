using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Intoner.Objects.Utils;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Intoner.Objects.Assets;

internal delegate void LayoutSectionVisitor(LayoutFileReader reader, uint magic);
internal delegate void LayoutInstanceVisitor(LayoutFileReader reader, int offset, FileLayerGroupInstance instance);

internal readonly ref struct LayoutFileReader(ReadOnlySpan<byte> data, string filePath)
{
    private static readonly UTF8Encoding StringEncoding = new(false, true);
    private readonly ReadOnlySpan<byte> _data = data;

    public string FilePath { get; } = filePath;
    public int Length => _data.Length;

    public void VisitSections(LayoutSectionVisitor visitor, CancellationToken cancellationToken)
    {
        FileHeader header = Read<FileHeader>(0);
        int fileHeaderSize = Unsafe.SizeOf<FileHeader>();
        int sectionHeaderSize = Unsafe.SizeOf<FileSectionHeader>();

        if (header.TotalSize < fileHeaderSize)
        {
            throw Invalid("invalid container length");
        }

        LayoutFileReader contents = View(0, header.TotalSize);
        if (header.NumSections > (contents.Length - fileHeaderSize) / sectionHeaderSize)
        {
            throw Invalid("section count exceeds the container");
        }

        int sectionOffset = fileHeaderSize;
        for (uint index = 0; index < header.NumSections; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSectionHeader section = contents.Read<FileSectionHeader>(sectionOffset);
            if (section.TotalSize < sectionHeaderSize)
            {
                throw Invalid("invalid section length");
            }

            _ = contents.View(sectionOffset, section.TotalSize);
            int dataOffset = sectionOffset + sectionHeaderSize;
            sectionOffset += (int)section.TotalSize;
            int headerSize = section.Magic switch
            {
                0x314E4353 => Unsafe.SizeOf<FileSceneHeader>(),
                0x3150474C => Unsafe.SizeOf<FileLayerGroupHeader>(),
                _ => 0,
            };
            if (section.TotalSize - sectionHeaderSize < headerSize)
            {
                throw Invalid("truncated section header");
            }

            visitor(contents.View(dataOffset, contents.Length - dataOffset), section.Magic);
        }
    }

    public void VisitSceneInstances(LayoutInstanceVisitor visitor, CancellationToken cancellationToken)
    {
        FileSceneHeader scene = Read<FileSceneHeader>(0);
        int groups = ArrayOffset<FileLayerGroupHeader>(0, scene.OffsetEmbeddedLayerGroups, scene.NumEmbeddedLayerGroups);
        for (int index = 0; index < scene.NumEmbeddedLayerGroups; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VisitLayerGroup(groups + index * Unsafe.SizeOf<FileLayerGroupHeader>(), visitor, cancellationToken);
        }
    }

    public void VisitLayerGroup(int offset, LayoutInstanceVisitor visitor, CancellationToken cancellationToken)
    {
        FileLayerGroupHeader group = Read<FileLayerGroupHeader>(offset);
        int layers = ArrayOffset<int>(offset, group.OffsetLayers, group.NumLayers);
        for (int layerIndex = 0; layerIndex < group.NumLayers; layerIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int layerOffset = RelativeOffset(layers, Read<int>(layers + (long)layerIndex * sizeof(int)), Unsafe.SizeOf<FileLayerGroupLayer>());
            FileLayerGroupLayer layer = Read<FileLayerGroupLayer>(layerOffset);
            int instances = ArrayOffset<int>(layerOffset, layer.OffsetInstances, layer.NumInstances);
            for (int index = 0; index < layer.NumInstances; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int instanceOffset = RelativeOffset(instances, Read<int>(instances + (long)index * sizeof(int)), Unsafe.SizeOf<FileLayerGroupInstance>());
                visitor(this, instanceOffset, Read<FileLayerGroupInstance>(instanceOffset));
            }
        }
    }

    public T Read<T>(long offset) where T : unmanaged
        => MemoryMarshal.Read<T>(Slice(offset, Unsafe.SizeOf<T>()));

    public LayoutFileReader View(long offset, long length)
        => new(Slice(offset, length), FilePath);

    public int ArrayOffset<T>(int origin, int relativeOffset, int count) where T : unmanaged
    {
        if (count < 0)
        {
            throw Invalid("negative array count");
        }

        return RelativeOffset(origin, relativeOffset, (long)count * Unsafe.SizeOf<T>());
    }

    public int RelativeOffset(int origin, int relativeOffset, long length, bool allowNegative = false)
    {
        if (relativeOffset < 0 && !allowNegative)
        {
            throw Invalid("negative relative offset");
        }

        long offset = (long)origin + relativeOffset;
        _ = Slice(offset, length);
        return (int)offset;
    }

    public string ReadOptionalString(int origin, int relativeOffset)
        => relativeOffset == 0 ? string.Empty : ReadString(RelativeOffset(origin, relativeOffset, 1));

    public string ReadString(int offset)
    {
        ReadOnlySpan<byte> bytes = Slice(offset, _data.Length - (long)offset);
        int terminator = bytes.IndexOf((byte)0);

        if (terminator < 0)
        {
            throw Invalid("unterminated UTF8 string");
        }

        try
        {
            return TextUtility.TrimOrEmpty(StringEncoding.GetString(bytes[..terminator]));
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"invalid layout file '{FilePath}': invalid UTF8 string", ex);
        }
    }

    public ReadOnlySpan<byte> ReadEmbeddedTimeline(int origin, int relativeOffset)
    {
        int headerSize = Unsafe.SizeOf<FileSceneTimelineDescription>() - Unsafe.SizeOf<FileSceneTimelineNode>();
        int offset = RelativeOffset(origin, relativeOffset, headerSize, allowNegative: true);
        int length = Read<int>(offset + sizeof(int));
        return Slice(offset, length);
    }

    public InvalidDataException Invalid(string detail)
        => new($"invalid layout file '{FilePath}': {detail}");

    private ReadOnlySpan<byte> Slice(long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > _data.Length || length > _data.Length - offset)
        {
            throw Invalid("byte range exceeds the declared data");
        }

        return _data.Slice((int)offset, (int)length);
    }
}
