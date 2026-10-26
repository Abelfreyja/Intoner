using Penumbra.GameData.Files;
using Intoner.Objects.Resources;
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
        public const uint TimelineItemEnabled = 0x62456E61;
        public const uint TimelineBinderNumber = 0x426E4E6F;
        public const uint TimelineBindPoint = 0x42644E6F;
        public const uint LoopStart = 0x4C705374;
        public const uint LoopEnd = 0x4C704564;
        public const uint EmitterSound = 0x53644E6D;
    }

    public static bool TryReadRoot(ReadOnlySpan<byte> data, out Chunk root)
    {
        ChunkCursor cursor = EnumerateChunks(data);
        return cursor.TryReadNext(out root) && root.Tag == AvfxMagic.AvfxBase && cursor.IsComplete;
    }

    public static bool IsBinderPropertyTag(uint tag)
        => tag is Tags.BinderStartProperty
               or Tags.BinderPrimaryProperty
               or Tags.BinderSecondaryProperty
               or Tags.BinderGoalProperty;

    public static ChunkCursor EnumerateChunks(ReadOnlySpan<byte> data)
        => new(data, 0, data.Length);

    public static ChunkCursor EnumerateChildren(ReadOnlySpan<byte> data, in Chunk container)
        => new(data, container.PayloadStart, container.PayloadLength);

    public static bool TryReadBinderProperty(ReadOnlySpan<byte> data, in Chunk container, out BinderProperty property)
    {
        property = default;
        SignedField? bindPoint = null;
        SignedField? bindTargetPoint = null;
        SignedField? bindPointId = null;
        ChunkCursor cursor = EnumerateChildren(data, container);
        while (cursor.TryReadNext(out Chunk child))
        {
            switch (child.Tag)
            {
                case Tags.BinderBindPoint:
                    if (!TryReadSingleSignedField(data, child, ref bindPoint))
                    {
                        return false;
                    }

                    break;
                case Tags.BinderBindTargetPoint:
                    if (!TryReadSingleSignedField(data, child, ref bindTargetPoint))
                    {
                        return false;
                    }

                    break;
                case Tags.BinderBindPointId:
                    if (!TryReadSingleSignedField(data, child, ref bindPointId))
                    {
                        return false;
                    }

                    break;
            }
        }

        property = new(bindPoint, bindTargetPoint, bindPointId);
        return cursor.IsComplete;
    }

    private static bool TryReadSingleSignedField(ReadOnlySpan<byte> data, in Chunk chunk, ref SignedField? field)
    {
        if (field.HasValue || !TryReadSignedPayload(data, chunk, out int value))
        {
            return false;
        }

        field = new(chunk, value);
        return true;
    }

    public static bool TryReadTimelineItemBindPoints(ReadOnlySpan<byte> data, in Chunk container, out int boundItemCount)
    {
        boundItemCount = 0;
        bool hasBindPoint = false;
        ChunkCursor cursor = EnumerateChildren(data, container);
        while (cursor.TryReadNext(out Chunk child))
        {
            if (child.Tag == Tags.TimelineItemEnabled)
            {
                if (!TryReadSignedPayload(data, child, out _))
                {
                    return false;
                }

                hasBindPoint = false;
            }
            else if (child.Tag == Tags.TimelineBindPoint)
            {
                if (hasBindPoint || !TryReadSignedPayload(data, child, out int bindPoint))
                {
                    return false;
                }

                hasBindPoint = true;

                if (bindPoint != -1)
                {
                    boundItemCount++;
                }
            }
        }

        return cursor.IsComplete;
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

    public static bool TryReadPathPayload(ReadOnlySpan<byte> data, in Chunk chunk, out string value)
    {
        value = string.Empty;

        if (chunk.PayloadStart < 0 || chunk.PayloadStart > data.Length
         || chunk.PayloadLength < 0 || chunk.PayloadLength > data.Length - chunk.PayloadStart)
        {
            return false;
        }

        ReadOnlySpan<byte> payload = data.Slice(chunk.PayloadStart, chunk.PayloadLength);
        int terminator = payload.IndexOf((byte)0);

        if (terminator <= 0)
        {
            return false;
        }

        return ObjectResourcePathEncoding.TryDecodeGamePath(payload[..terminator], out value);
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
    public readonly record struct SignedField(Chunk Chunk, int Value);

    [StructLayout(LayoutKind.Auto)]
    public readonly record struct BinderProperty(SignedField? BindPoint, SignedField? BindTargetPoint, SignedField? BindPointId);

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
        private bool _valid;
        private int _consumed;

        public readonly bool IsComplete
            => _valid && _consumed == _containerLength;

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
             || chunkStart > data.Length - HeaderSize
             || chunkStart - containerStart > containerLength - HeaderSize)
            {
                return false;
            }

            uint chunkTag = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(chunkStart, sizeof(uint)));
            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(chunkStart + sizeof(uint), sizeof(uint)));
            int remainingPayloadLength = containerLength - (chunkStart - containerStart) - HeaderSize;

            if (payloadLength > remainingPayloadLength)
            {
                return false;
            }

            int alignedPayloadLength = Align4((int)payloadLength);

            if (alignedPayloadLength > remainingPayloadLength)
            {
                return false;
            }

            chunk = new Chunk(
                chunkTag,
                chunkStart + HeaderSize,
                (int)payloadLength,
                HeaderSize + alignedPayloadLength);
            return true;
        }

        public bool TryReadNext(out Chunk chunk)
        {
            chunk = default;

            if (!_valid || _containerLength - _consumed < HeaderSize)
            {
                return false;
            }

            int childStart = _containerStart + _consumed;
            if (!TryReadChunk(_data, childStart, _containerStart, _containerLength, out chunk))
            {
                _valid = false;
                return false;
            }

            _consumed += chunk.AlignedTotalLength;
            return true;
        }
    }
}
