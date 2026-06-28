using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Intoner.Objects.Assets;

internal enum SqpackByteOrder
{
    LittleEndian,
    BigEndian,
}

internal sealed class SqpackBinaryReader : BinaryReader
{
    public SqpackBinaryReader(Stream input, SqpackByteOrder byteOrder)
        : base(input, Encoding.ASCII, leaveOpen: false)
    {
        ByteOrder = byteOrder;
    }

    public SqpackByteOrder ByteOrder { get; }

    public override short ReadInt16()
        => ByteOrder == SqpackByteOrder.LittleEndian
            ? base.ReadInt16()
            : BinaryPrimitives.ReverseEndianness(base.ReadInt16());

    public override int ReadInt32()
        => ByteOrder == SqpackByteOrder.LittleEndian
            ? base.ReadInt32()
            : BinaryPrimitives.ReverseEndianness(base.ReadInt32());

    public override long ReadInt64()
        => ByteOrder == SqpackByteOrder.LittleEndian
            ? base.ReadInt64()
            : BinaryPrimitives.ReverseEndianness(base.ReadInt64());

    public override ushort ReadUInt16()
        => ByteOrder == SqpackByteOrder.LittleEndian
            ? base.ReadUInt16()
            : BinaryPrimitives.ReverseEndianness(base.ReadUInt16());

    public override uint ReadUInt32()
        => ByteOrder == SqpackByteOrder.LittleEndian
            ? base.ReadUInt32()
            : BinaryPrimitives.ReverseEndianness(base.ReadUInt32());

    public override ulong ReadUInt64()
        => ByteOrder == SqpackByteOrder.LittleEndian
            ? base.ReadUInt64()
            : BinaryPrimitives.ReverseEndianness(base.ReadUInt64());
}

internal readonly record struct SqpackNamedPath(string Path);

internal sealed class SqpackIndexFile
{
    private const int SqpackHeaderSize = 0x400;
    private const int Index1EntrySize = 16;
    private const int Index2EntrySize = 8;
    private const int NamedPathRecordSize = 256;
    private const int NamedPathLength = 240;
    private const int SectionHashLength = 20;
    private const int SectionPaddingLength = 44;
    private const int FileInfoUnknownDataLength = 656;
    private const uint SqpackIndexType = 2;

    private SqpackIndexFile(
        int indexId,
        IReadOnlyList<SqpackIndex1EntryKey> index1Entries,
        IReadOnlyList<SqpackIndex2EntryKey> index2Entries,
        IReadOnlyList<SqpackNamedPath> namedPaths)
    {
        IndexId = indexId;
        Index1Entries = index1Entries;
        Index2Entries = index2Entries;
        NamedPaths = namedPaths;
    }

    public int IndexId { get; }
    public IReadOnlyList<SqpackIndex1EntryKey> Index1Entries { get; }
    public IReadOnlyList<SqpackIndex2EntryKey> Index2Entries { get; }
    public IReadOnlyList<SqpackNamedPath> NamedPaths { get; }

    public static SqpackIndexFile Read(string path, CancellationToken cancellationToken = default)
    {
        int indexId = ParseIndexId(path);
        using FileStream stream = File.OpenRead(path);
        SqpackByteOrder byteOrder = ResolveByteOrder(stream);
        using SqpackBinaryReader reader = new(stream, byteOrder);

        VersionInfo versionInfo = VersionInfo.Read(reader);
        if (versionInfo.Type != SqpackIndexType)
        {
            throw new InvalidDataException($"unsupported sqpack file type {versionInfo.Type}");
        }

        HeaderFileInfo fileInfo = HeaderFileInfo.Read(reader);
        fileInfo.Validate(stream.Length);

        IReadOnlyList<SqpackIndex1EntryKey> index1Entries = fileInfo.IndexType == 0
            ? ReadIndex1Entries(reader, fileInfo, indexId, cancellationToken)
            : [];
        IReadOnlyList<SqpackIndex2EntryKey> index2Entries = fileInfo.IndexType == 2
            ? ReadIndex2Entries(reader, fileInfo, indexId, cancellationToken)
            : [];
        IReadOnlyList<SqpackNamedPath> namedPaths = fileInfo.IndexType switch
        {
            0 => ReadNamedPaths64(reader, fileInfo, cancellationToken),
            2 => ReadNamedPaths32(reader, fileInfo, cancellationToken),
            _ => [],
        };

        return new SqpackIndexFile(indexId, index1Entries, index2Entries, namedPaths);
    }

    private static SqpackByteOrder ResolveByteOrder(Stream stream)
    {
        if (stream.Length <= 8)
        {
            throw new InvalidDataException("sqpack index file is too short to read platform id");
        }

        stream.Position = 8;
        int platformId = stream.ReadByte();
        if (platformId < 0)
        {
            throw new EndOfStreamException("failed to read sqpack platform id");
        }

        stream.Position = 0;
        return platformId == 1
            ? SqpackByteOrder.BigEndian
            : SqpackByteOrder.LittleEndian;
    }

    private static int ParseIndexId(string path)
    {
        string fileName = Path.GetFileName(path);
        int separatorIndex = fileName.IndexOf('.');
        string idText = separatorIndex < 0
            ? fileName
            : fileName[..separatorIndex];
        return int.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int indexId)
            ? indexId
            : -1;
    }

    private static IReadOnlyList<SqpackIndex1EntryKey> ReadIndex1Entries(
        SqpackBinaryReader reader,
        HeaderFileInfo fileInfo,
        int indexId,
        CancellationToken cancellationToken)
    {
        int entryCount = checked((int)(fileInfo.IndexDataSize / Index1EntrySize));
        List<SqpackIndex1EntryKey> entries = new(entryCount);

        reader.BaseStream.Seek(fileInfo.IndexDataOffset, SeekOrigin.Begin);
        for (int i = 0; i < entryCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong hash = reader.ReadUInt64();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            if (hash == ulong.MaxValue || indexId < 0)
            {
                continue;
            }

            entries.Add(new SqpackIndex1EntryKey(
                indexId,
                (uint)(hash >> 32),
                (uint)hash));
        }

        return entries;
    }

    private static IReadOnlyList<SqpackIndex2EntryKey> ReadIndex2Entries(
        SqpackBinaryReader reader,
        HeaderFileInfo fileInfo,
        int indexId,
        CancellationToken cancellationToken)
    {
        int entryCount = checked((int)(fileInfo.IndexDataSize / Index2EntrySize));
        List<SqpackIndex2EntryKey> entries = new(entryCount);

        reader.BaseStream.Seek(fileInfo.IndexDataOffset, SeekOrigin.Begin);
        for (int i = 0; i < entryCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint hash = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            if (hash == uint.MaxValue || indexId < 0)
            {
                continue;
            }

            entries.Add(new SqpackIndex2EntryKey(indexId, hash));
        }

        return entries;
    }

    private static IReadOnlyList<SqpackNamedPath> ReadNamedPaths64(
        SqpackBinaryReader reader,
        HeaderFileInfo fileInfo,
        CancellationToken cancellationToken)
    {
        int namedPathCount = checked((int)(fileInfo.CollisionSize / NamedPathRecordSize));
        List<SqpackNamedPath> namedPaths = new(namedPathCount);

        reader.BaseStream.Seek(fileInfo.CollisionOffset, SeekOrigin.Begin);
        for (int i = 0; i < namedPathCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Collision64 entry = Collision64.Read(reader);
            if (entry.Hash == ulong.MaxValue || string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            namedPaths.Add(new SqpackNamedPath(entry.Path));
        }

        return namedPaths;
    }

    private static IReadOnlyList<SqpackNamedPath> ReadNamedPaths32(
        SqpackBinaryReader reader,
        HeaderFileInfo fileInfo,
        CancellationToken cancellationToken)
    {
        int namedPathCount = checked((int)(fileInfo.CollisionSize / NamedPathRecordSize));
        List<SqpackNamedPath> namedPaths = new(namedPathCount);

        reader.BaseStream.Seek(fileInfo.CollisionOffset, SeekOrigin.Begin);
        for (int i = 0; i < namedPathCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Collision32 entry = Collision32.Read(reader);
            if (entry.Hash == uint.MaxValue || string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            namedPaths.Add(new SqpackNamedPath(entry.Path));
        }

        return namedPaths;
    }

    private readonly record struct VersionInfo(
        byte PlatformId,
        uint FileSize,
        uint Version,
        uint Type)
    {
        public static VersionInfo Read(SqpackBinaryReader reader)
        {
            string magic = Encoding.ASCII.GetString(ReadExactBytes(reader, 6));
            if (!string.Equals(magic, "SqPack", StringComparison.Ordinal))
            {
                throw new InvalidDataException("not an sqpack index file");
            }

            reader.BaseStream.Seek(2, SeekOrigin.Current);
            byte platformId = reader.ReadByte();
            reader.BaseStream.Seek(3, SeekOrigin.Current);

            uint fileSize = reader.ReadUInt32();
            uint version = reader.ReadUInt32();
            uint type = reader.ReadUInt32();

            _ = reader.ReadUInt32(); // date
            _ = reader.ReadUInt32(); // time
            _ = reader.ReadUInt32(); // region id
            _ = reader.ReadUInt32(); // language id
            _ = ReadExactBytes(reader, 920);
            _ = ReadExactBytes(reader, SectionHashLength);
            reader.BaseStream.Seek(SectionPaddingLength, SeekOrigin.Current);

            if (reader.BaseStream.Position != SqpackHeaderSize)
            {
                throw new InvalidDataException("sqpack version header did not end at the expected offset");
            }

            return new VersionInfo(platformId, fileSize, version, type);
        }
    }

    private readonly record struct HeaderFileInfo(
        uint Size,
        uint Version,
        uint IndexDataOffset,
        uint IndexDataSize,
        uint DataFileCount,
        uint CollisionOffset,
        uint CollisionSize,
        uint DirectoryOffset,
        uint DirectorySize,
        uint IndexType)
    {
        public static HeaderFileInfo Read(SqpackBinaryReader reader)
        {
            uint size = reader.ReadUInt32();
            uint version = reader.ReadUInt32();
            uint indexDataOffset = reader.ReadUInt32();
            uint indexDataSize = reader.ReadUInt32();
            _ = ReadExactBytes(reader, SectionHashLength);
            reader.BaseStream.Seek(SectionPaddingLength, SeekOrigin.Current);

            uint dataFileCount = reader.ReadUInt32();
            uint collisionOffset = reader.ReadUInt32();
            uint collisionSize = reader.ReadUInt32();
            _ = ReadExactBytes(reader, SectionHashLength);
            reader.BaseStream.Seek(SectionPaddingLength, SeekOrigin.Current);

            _ = reader.ReadUInt32(); // empty block offset
            _ = reader.ReadUInt32(); // empty block size
            _ = ReadExactBytes(reader, SectionHashLength);
            reader.BaseStream.Seek(SectionPaddingLength, SeekOrigin.Current);

            uint directoryOffset = reader.ReadUInt32();
            uint directorySize = reader.ReadUInt32();
            _ = ReadExactBytes(reader, SectionHashLength);
            reader.BaseStream.Seek(SectionPaddingLength, SeekOrigin.Current);

            uint indexType = reader.ReadUInt32();
            _ = ReadExactBytes(reader, FileInfoUnknownDataLength);
            _ = ReadExactBytes(reader, SectionHashLength);
            reader.BaseStream.Seek(SectionPaddingLength, SeekOrigin.Current);

            return new HeaderFileInfo(
                size,
                version,
                indexDataOffset,
                indexDataSize,
                dataFileCount,
                collisionOffset,
                collisionSize,
                directoryOffset,
                directorySize,
                indexType);
        }

        public void Validate(long streamLength)
        {
            if (IndexType is not 0 and not 2)
            {
                throw new InvalidDataException($"unsupported sqpack index type {IndexType}");
            }

            ValidateSection(streamLength, IndexDataOffset, IndexDataSize, "index data");
            if (CollisionSize > 0)
            {
                ValidateSection(streamLength, CollisionOffset, CollisionSize, "named path data");
            }

            if (IndexDataSize % (IndexType == 0 ? Index1EntrySize : Index2EntrySize) != 0)
            {
                throw new InvalidDataException("sqpack index data size is not aligned to the record size");
            }

            if (CollisionSize % NamedPathRecordSize != 0)
            {
                throw new InvalidDataException("sqpack named path section size is not aligned to the record size");
            }
        }

        private static void ValidateSection(long streamLength, uint offset, uint size, string sectionName)
        {
            ulong sectionEnd = (ulong)offset + size;
            if (offset < SqpackHeaderSize || sectionEnd > (ulong)streamLength)
            {
                throw new InvalidDataException($"sqpack {sectionName} section is outside the file bounds");
            }
        }
    }

    private readonly record struct Collision32(uint Hash, string Path)
    {
        public static Collision32 Read(SqpackBinaryReader reader)
        {
            uint hash = reader.ReadUInt32();
            _ = reader.ReadUInt32(); // unknown
            _ = reader.ReadUInt32(); // data
            _ = reader.ReadUInt32(); // index
            string path = ReadPath(reader);
            return new Collision32(hash, path);
        }
    }

    private readonly record struct Collision64(ulong Hash, string Path)
    {
        public static Collision64 Read(SqpackBinaryReader reader)
        {
            ulong hash = reader.ReadUInt64();
            _ = reader.ReadUInt32(); // data
            _ = reader.ReadUInt32(); // index
            string path = ReadPath(reader);
            return new Collision64(hash, path);
        }
    }

    private static string ReadPath(SqpackBinaryReader reader)
    {
        string rawPath = Encoding.ASCII.GetString(ReadExactBytes(reader, NamedPathLength));
        int terminatorIndex = rawPath.IndexOf('\0');
        return terminatorIndex >= 0
            ? rawPath[..terminatorIndex]
            : rawPath;
    }

    private static byte[] ReadExactBytes(SqpackBinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new EndOfStreamException($"expected {count} bytes but only read {bytes.Length}");
        }

        return bytes;
    }
}

