using Penumbra.GameData.Files;
using System.Runtime.InteropServices;
using static Intoner.Objects.Assets.AvfxChunkReader;

namespace Intoner.Objects.Assets;

internal static class AvfxStandaloneRewriter
{
    private const int DetachedTimelineBindPoint = -1;
    private const int DefaultBinderBindPoint = 0;
    private const int DefaultBinderBindTargetPoint = 0;
    private const int DefaultBinderBindPointId = 0;
    private const int TargetBinderBindPoint = 1;
    private const int ByNameBinderBindTargetPoint = 3;

    [StructLayout(LayoutKind.Auto)]
    public readonly record struct RewriteResult(
        int TimelineBindPointCount,
        int TimelineBinderCount,
        int BinderPropertyCount)
    {
        public int TotalCount
            => TimelineBindPointCount + TimelineBinderCount + BinderPropertyCount;

        public AvfxRewritePolicy.Capability AppliedCapabilities
            => AvfxRewritePolicy.FromRewriteCounts(
                TimelineBindPointCount,
                TimelineBinderCount,
                BinderPropertyCount);
    }

    public static bool HasRewritableTimelineBindPoints(ReadOnlySpan<byte> data)
        => CountRewritableTimelineBindPoints(data) > 0;

    private static int CountRewritableTimelineBindPoints(ReadOnlySpan<byte> data)
    {
        if (!TryFindRootChunk(data, AvfxMagic.AvfxBase, out Chunk avfx))
        {
            return 0;
        }

        int count = 0;
        ChunkCursor cursor = EnumerateChildren(data, avfx);
        while (cursor.TryReadNext(out Chunk child))
        {
            if (child.Tag == AvfxMagic.Timeline)
            {
                count += CountRewritableTimelineBindPoints(data, child);
            }
        }

        return count;
    }

    private static int CountRewritableTimelineBindPoints(ReadOnlySpan<byte> data, in Chunk timeline)
    {
        int count = 0;
        ChunkCursor cursor = EnumerateChildren(data, timeline);
        while (cursor.TryReadNext(out Chunk child))
        {
            if (child.Tag == AvfxChunkReader.Tags.TimelineItem
             && TryReadTimelineBindPoint(data, child, out int bindPoint)
             && bindPoint != DetachedTimelineBindPoint)
            {
                count++;
            }
        }

        return count;
    }

    public static RewriteResult RewriteForStandaloneSpawn(Span<byte> data)
    {
        if (!TryFindRootChunk(data, AvfxMagic.AvfxBase, out Chunk avfx))
        {
            return default;
        }

        int timelineBindPointCount = 0;
        int timelineBinderCount = 0;
        int binderPropertyCount = 0;
        ChunkCursor cursor = EnumerateChildren(data, avfx);
        while (cursor.TryReadNext(out Chunk child))
        {
            switch (child.Tag)
            {
                case AvfxMagic.Timeline:
                    timelineBindPointCount += RewriteTimelineItemBindPoints(data, child);
                    timelineBinderCount += RewriteTimelineBinders(data, child);
                    break;
                case AvfxMagic.Binder:
                    binderPropertyCount += RewriteBinderProperties(data, child);
                    break;
            }
        }

        return new RewriteResult(timelineBindPointCount, timelineBinderCount, binderPropertyCount);
    }

    private static int RewriteTimelineItemBindPoints(Span<byte> data, in Chunk timeline)
    {
        int rewritten = 0;
        ChunkCursor cursor = EnumerateChildren(data, timeline);
        while (cursor.TryReadNext(out Chunk child))
        {
            if (child.Tag == AvfxChunkReader.Tags.TimelineItem
             && TryFindChunk(data, child.PayloadStart, child.PayloadLength, AvfxChunkReader.Tags.TimelineBindPoint, out Chunk bindPointChunk)
             && TryReadSignedPayload(data, bindPointChunk, out int bindPoint)
             && bindPoint != DetachedTimelineBindPoint)
            {
                WriteIntegerPayload(data, bindPointChunk, uint.MaxValue);
                rewritten++;
            }
        }

        return rewritten;
    }

    private static int RewriteTimelineBinders(Span<byte> data, in Chunk timeline)
    {
        int rewritten = 0;
        ChunkCursor cursor = EnumerateChildren(data, timeline);
        while (cursor.TryReadNext(out Chunk child))
        {
            if (child.Tag == AvfxChunkReader.Tags.TimelineBinderNumber
             && TryReadSignedPayload(data, child, out int binderNumber)
             && binderNumber >= 0)
            {
                WriteIntegerPayload(data, child, uint.MaxValue);
                rewritten++;
            }
        }

        return rewritten;
    }

    private static int RewriteBinderProperties(Span<byte> data, in Chunk binder)
    {
        int rewritten = 0;
        ChunkCursor cursor = EnumerateChildren(data, binder);
        while (cursor.TryReadNext(out Chunk child))
        {
            if (IsBinderPropertyTag(child.Tag))
            {
                rewritten += RewriteBinderProperty(data, child);
            }
        }

        return rewritten;
    }

    private static int RewriteBinderProperty(Span<byte> data, in Chunk property)
    {
        int bindPoint = DefaultBinderBindPoint;
        int bindTargetPoint = DefaultBinderBindTargetPoint;
        int bindPointId = DefaultBinderBindPointId;
        bool hasBindPoint = TryFindChunk(data, property.PayloadStart, property.PayloadLength, AvfxChunkReader.Tags.BinderBindPoint, out Chunk bindPointChunk)
                          && TryReadSignedPayload(data, bindPointChunk, out bindPoint);
        bool hasBindTargetPoint = TryFindChunk(data, property.PayloadStart, property.PayloadLength, AvfxChunkReader.Tags.BinderBindTargetPoint, out Chunk bindTargetPointChunk)
                             && TryReadSignedPayload(data, bindTargetPointChunk, out bindTargetPoint);
        bool hasBindPointId = TryFindChunk(data, property.PayloadStart, property.PayloadLength, AvfxChunkReader.Tags.BinderBindPointId, out Chunk bindPointIdChunk)
                           && TryReadSignedPayload(data, bindPointIdChunk, out bindPointId);

        if ((!hasBindPoint || bindPoint != TargetBinderBindPoint)
         && (!hasBindTargetPoint || bindTargetPoint != ByNameBinderBindTargetPoint)
         && (!hasBindPointId || bindPointId == DefaultBinderBindPointId))
        {
            return 0;
        }

        if (hasBindPoint && bindPoint == TargetBinderBindPoint)
        {
            WriteIntegerPayload(data, bindPointChunk, DefaultBinderBindPoint);
        }

        if (hasBindTargetPoint && bindTargetPoint == ByNameBinderBindTargetPoint)
        {
            WriteIntegerPayload(data, bindTargetPointChunk, DefaultBinderBindTargetPoint);
        }

        if (hasBindPointId && bindPointId != DefaultBinderBindPointId)
        {
            WriteIntegerPayload(data, bindPointIdChunk, DefaultBinderBindPointId);
        }

        return 1;
    }

    private static bool TryReadTimelineBindPoint(ReadOnlySpan<byte> data, in Chunk item, out int bindPoint)
    {
        bindPoint = 0;
        return TryFindChunk(data, item.PayloadStart, item.PayloadLength, AvfxChunkReader.Tags.TimelineBindPoint, out Chunk bindPointChunk)
            && TryReadSignedPayload(data, bindPointChunk, out bindPoint);
    }

    private static bool IsBinderPropertyTag(uint tag)
        => tag is AvfxChunkReader.Tags.BinderStartProperty
               or AvfxChunkReader.Tags.BinderPrimaryProperty
               or AvfxChunkReader.Tags.BinderSecondaryProperty
               or AvfxChunkReader.Tags.BinderGoalProperty;
}
