using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Assets;

internal static class AvfxChunkReader
{
    private const int HeaderSize = 8;

    public static class Tags
    {
        public const uint SchedulerItemCount = 0x4974436E;
        public const uint SchedulerTriggerCount = 0x5472436E;
        public const uint ParticleType = 0x50725654;
        public const uint BinderType = 0x426E5672;
        public const uint BinderStartProperty = 0x50727053;
        public const uint BinderPrimaryProperty = 0x50727031;
        public const uint BinderSecondaryProperty = 0x50727032;
        public const uint BinderGoalProperty = 0x50727047;
        public const uint BinderBindPoint = 0x00425054;
        public const uint BinderBindTargetPoint = 0x42505450;
        public const uint BinderBindPointId = 0x42504944;
        public const uint TimelineItem = 0x4974656D;
        public const uint TimelineBinderNumber = 0x426E4E6F;
        public const uint TimelineBindPoint = 0x42644E6F;
        public const uint LoopStart = 0x4C705374;
        public const uint LoopEnd = 0x4C704564;
    }

    public static ChunkCursor EnumerateChunks(ReadOnlySpan<byte> data)
        => new(data, 0, data.Length);

    public static ChunkCursor EnumerateChildren(ReadOnlySpan<byte> data, in Chunk container)
        => new(data, container.PayloadStart, container.PayloadLength);

    private static ChunkCursor EnumerateChildren(ReadOnlySpan<byte> data, int containerStart, int containerLength)
        => new(data, containerStart, containerLength);

    public static bool TryFindRootChunk(ReadOnlySpan<byte> data, uint tag, out Chunk chunk)
        => TryFindChunk(data, 0, data.Length, tag, out chunk);

    public static bool TryFindChunk(ReadOnlySpan<byte> data, int containerStart, int containerLength, uint tag, out Chunk chunk)
    {
        chunk = default;
        ChunkCursor cursor = EnumerateChildren(data, containerStart, containerLength);
        while (cursor.TryReadNext(out Chunk child))
        {
            if (child.Tag != tag)
            {
                continue;
            }

            chunk = child;
            return true;
        }

        return false;
    }

    public static bool TryReadSignedPayload(ReadOnlySpan<byte> data, in Chunk chunk, out int value)
    {
        value = 0;
        if (chunk.PayloadLength >= sizeof(int)
         && chunk.PayloadStart + sizeof(int) <= data.Length)
        {
            value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(chunk.PayloadStart, sizeof(int)));
            return true;
        }

        if (chunk.PayloadLength >= sizeof(short)
         && chunk.PayloadStart + sizeof(short) <= data.Length)
        {
            value = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(chunk.PayloadStart, sizeof(short)));
            return true;
        }

        if (chunk.PayloadLength >= sizeof(byte)
         && chunk.PayloadStart + sizeof(byte) <= data.Length)
        {
            value = unchecked((sbyte)data[chunk.PayloadStart]);
            return true;
        }

        return false;
    }

    public static void WriteIntegerPayload(Span<byte> data, in Chunk chunk, uint value)
    {
        if (chunk.PayloadLength >= sizeof(uint)
         && chunk.PayloadStart + sizeof(uint) <= data.Length)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(chunk.PayloadStart, sizeof(uint)), value);
            return;
        }

        if (chunk.PayloadLength >= sizeof(ushort)
         && chunk.PayloadStart + sizeof(ushort) <= data.Length)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(chunk.PayloadStart, sizeof(ushort)), (ushort)value);
            return;
        }

        if (chunk.PayloadLength >= sizeof(byte)
         && chunk.PayloadStart + sizeof(byte) <= data.Length)
        {
            data[chunk.PayloadStart] = (byte)value;
        }
    }

    [StructLayout(LayoutKind.Auto)]
    public readonly record struct Chunk(
        uint Tag,
        int PayloadStart,
        int PayloadLength,
        int AlignedTotalLength);

    public ref struct ChunkCursor
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly int _containerStart;
        private readonly int _containerLength;
        private readonly bool _valid;
        private int _consumed;

        private static int Align4(int value)
            => (int)(((uint)value + 3u) & ~3u);

        public ChunkCursor(ReadOnlySpan<byte> data, int containerStart, int containerLength)
        {
            _data = data;
            _containerStart = containerStart;
            _containerLength = containerLength;
            _valid = containerStart >= 0
                  && containerLength >= 0
                  && containerStart <= data.Length
                  && containerLength <= data.Length - containerStart;
            _consumed = 0;
        }

        private static bool TryReadChunk(
            ReadOnlySpan<byte> data,
            int chunkStart,
            int containerStart,
            int containerLength,
            out Chunk chunk)
        {
            chunk = default;
            if (chunkStart < containerStart
             || chunkStart + HeaderSize > data.Length
             || chunkStart + HeaderSize > containerStart + containerLength)
            {
                return false;
            }

            uint chunkTag = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(chunkStart, sizeof(uint)));
            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(chunkStart + sizeof(uint), sizeof(uint)));
            if (payloadLength > int.MaxValue - 3)
            {
                return false;
            }

            int alignedPayloadLength = Align4((int)payloadLength);
            int totalLength = HeaderSize + alignedPayloadLength;
            if (totalLength < HeaderSize
             || totalLength > containerStart + containerLength - chunkStart
             || chunkStart + HeaderSize + (int)payloadLength > data.Length)
            {
                return false;
            }

            chunk = new Chunk(
                chunkTag,
                chunkStart + HeaderSize,
                (int)payloadLength,
                totalLength);
            return true;
        }

        public bool TryReadNext(out Chunk chunk)
        {
            chunk = default;
            if (!_valid || _consumed + HeaderSize > _containerLength)
            {
                return false;
            }

            int childStart = _containerStart + _consumed;
            if (!TryReadChunk(_data, childStart, _containerStart, _containerLength, out chunk))
            {
                _consumed = _containerLength;
                return false;
            }

            _consumed += chunk.AlignedTotalLength;
            return true;
        }
    }
}
