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
        => TryReadAttachments(data, out _, out int count) ? count : 0;

    private static bool TryReadAttachments(ReadOnlySpan<byte> data, out Chunk avfx, out int count)
    {
        count = 0;
        if (!TryReadRoot(data, out avfx))
        {
            return false;
        }

        ChunkCursor cursor = EnumerateChildren(data, avfx);
        while (cursor.TryReadNext(out Chunk block))
        {
            if (block.Tag is not (AvfxMagic.Timeline or AvfxMagic.Binder))
            {
                continue;
            }

            int boundItemCount = 0;
            ChunkCursor children = EnumerateChildren(data, block);
            while (children.TryReadNext(out Chunk child))
            {
                if (block.Tag == AvfxMagic.Timeline && child.Tag == AvfxChunkReader.Tags.TimelineBinderNumber)
                {
                    if (!TryReadSignedPayload(data, child, out _))
                    {
                        return false;
                    }
                }
                else if (block.Tag == AvfxMagic.Timeline && child.Tag == AvfxChunkReader.Tags.TimelineItem)
                {
                    if (!TryReadTimelineItemBindPoints(data, child, out boundItemCount))
                    {
                        return false;
                    }
                }
                else if (block.Tag == AvfxMagic.Binder && IsBinderPropertyTag(child.Tag)
                      && !TryReadBinderProperty(data, child, out _))
                {
                    return false;
                }
            }

            if (!children.IsComplete)
            {
                return false;
            }

            count += boundItemCount;
        }

        return cursor.IsComplete;
    }

    public static RewriteResult RewriteForStandaloneSpawn(Span<byte> data)
    {
        if (!TryReadAttachments(data, out Chunk avfx, out _))
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
            if (child.Tag != AvfxChunkReader.Tags.TimelineItem)
            {
                continue;
            }

            ChunkCursor items = EnumerateChildren(data, child);
            while (items.TryReadNext(out Chunk bindPointChunk))
            {
                if (bindPointChunk.Tag == AvfxChunkReader.Tags.TimelineBindPoint
                 && TryReadSignedPayload(data, bindPointChunk, out int bindPoint)
                 && bindPoint != DetachedTimelineBindPoint)
                {
                    WriteIntegerPayload(data, bindPointChunk, uint.MaxValue);
                    rewritten++;
                }
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
        if (!TryReadBinderProperty(data, property, out BinderProperty fields))
        {
            return 0;
        }

        bool rewritten = false;

        if (fields.BindPoint is { Value: TargetBinderBindPoint } point)
        {
            WriteIntegerPayload(data, point.Chunk, DefaultBinderBindPoint);
            rewritten = true;
        }

        if (fields.BindTargetPoint is { Value: ByNameBinderBindTargetPoint } target)
        {
            WriteIntegerPayload(data, target.Chunk, DefaultBinderBindTargetPoint);
            rewritten = true;
        }

        if (fields.BindPointId is { } id && id.Value != DefaultBinderBindPointId)
        {
            WriteIntegerPayload(data, id.Chunk, DefaultBinderBindPointId);
            rewritten = true;
        }

        return rewritten ? 1 : 0;
    }
}
