using Penumbra.GameData.Files;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Intoner.Objects.Assets;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct TmbVfxReference(
    string Path,
    RuntimeVfxEvidence Evidence,
    VfxTimelineContext ContextFlags)
{
    public IReadOnlyList<string> SearchTerms
        => new VfxTimelineReferenceInfo(Evidence, ContextFlags).BuildSearchTerms();
}

internal static class VfxAssetAnalyzer
{
    private const string TmbHeaderMagic = "TMLB";
    private const string TmbNestedTimelineEntryMagic = "C002";
    private const string TmbPapAnimationEntryMagic = "C009";
    private const string TmbAnimationEntryMagic = "C010";
    private const string TmbVfxEntryMagic = "C012";
    private const string TmbSoundEntryMagic = "C063";
    private const string TmbAsyncVfxEntryMagic = "C173";

    private const BinderBindPoint DefaultBinderBindPoint = BinderBindPoint.Caster;
    private static readonly string[] StrongContextTokens =
    [
        "reac",
        "reaction",
        "swim",
        "foot",
        "splash",
        "dcomm",
        "frmreac",
    ];

    private enum VfxParticleType
    {
        Parameter = 0,
        Powder = 1,
        Windmill = 2,
        Line = 3,
        Laser = 4,
        Model = 5,
        Polyline = 6,
        Reserve0 = 7,
        Quad = 8,
        Polygon = 9,
        Decal = 10,
        DecalRing = 11,
        Disc = 12,
        LightModel = 13,
        ModelSkin = 14,
        Dissolve = 15,
    }

    private enum TmbVfxVisibility
    {
        DefaultNoTriggers = 0,
        DefaultWithTriggers = 1,
        AlwaysNoTriggers = 2,
        AlwaysWithTriggers = 3,
    }

    private enum BinderType
    {
        Point = 0,
        Linear = 1,
        Spline = 2,
        Camera = 3,
        LinearAdjust = 4,
    }

    private enum BinderBindPoint
    {
        Caster = 0,
        Target = 1,
    }

    private enum BinderBindTargetPoint
    {
        Origin = 0,
        FitGround = 1,
        DamageCircle = 2,
        ByName = 3,
    }

    [Flags]
    private enum BinderBindPoints
    {
        None   = 0,
        Caster = 1 << 0,
        Target = 1 << 1,
    }

    [Flags]
    private enum BinderBindTargetPoints
    {
        None         = 0,
        Origin       = 1 << 0,
        FitGround    = 1 << 1,
        DamageCircle = 1 << 2,
        ByName       = 1 << 3,
    }

    public static bool TryAnalyzeAvfx(IObjectAssetGameData gameData, string vfxPath, out VfxAnalysis analysis)
    {
        var normalizedPath = GameAssetPathRules.NormalizeGamePath(vfxPath);
        if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
        {
            analysis = CreateEmptyAnalysis();
            return false;
        }

        var gameFile = gameData.GetFile(normalizedPath);
        if (gameFile is null)
        {
            analysis = CreateEmptyAnalysis();
            return false;
        }

        return TryAnalyzeAvfx(normalizedPath, gameFile.Data, out analysis);
    }

    public static bool TryAnalyzeAvfx(string vfxPath, byte[] data, out VfxAnalysis analysis)
    {
        var normalizedPath = GameAssetPathRules.NormalizeGamePath(vfxPath);
        if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
        {
            analysis = CreateEmptyAnalysis();
            return false;
        }

        try
        {
            var file = new AvfxFile(data);
            int schedulerItemCount = 0;
            int schedulerTriggerCount = 0;
            foreach (AvfxFile.Block scheduler in file.Schedulers)
            {
                (int itemCount, int triggerCount) = ReadSchedulerCounts(scheduler);
                schedulerItemCount += itemCount;
                schedulerTriggerCount += triggerCount;
            }

            VfxBinderFacts binderFacts = AnalyzeBinders(file.Binders, file.Timelines);
            var drawLayer = ReadDrawLayer(file.DrawLayerType);
            var isFitGround = file.IsFitGround == 1;
            var isCameraSpace = file.IsCameraSpace == 1;
            var isAllStopOnHide = file.IsAllStopOnHide == 1;
            var usesWaterLayer = drawLayer is VfxDrawLayer.InWater or VfxDrawLayer.FitWater;
            var usesScreenLayer = drawLayer is VfxDrawLayer.Screen or VfxDrawLayer.PostUi or VfxDrawLayer.PrevUi;
            VfxParticleTypes particleTypes = AnalyzeParticles(file.Particles);
            VfxLoopFacts loopFacts = AnalyzeLoopFacts(
                file.Timelines,
                file.Emitters,
                file.Effectors);
            bool hasStrongContextPath = HasStrongContextPathHints(normalizedPath);
            bool hasBoundTimelineItems = AvfxStandaloneRewriter.HasRewritableTimelineBindPoints(data);
            VfxAnalysisFeatures featureFlags =
                (isFitGround ? VfxAnalysisFeatures.FitGround : VfxAnalysisFeatures.None)
              | (isCameraSpace ? VfxAnalysisFeatures.CameraSpace : VfxAnalysisFeatures.None)
              | (isAllStopOnHide ? VfxAnalysisFeatures.AllStopOnHide : VfxAnalysisFeatures.None)
              | (usesWaterLayer ? VfxAnalysisFeatures.UsesWaterLayer : VfxAnalysisFeatures.None)
              | (usesScreenLayer ? VfxAnalysisFeatures.UsesScreenLayer : VfxAnalysisFeatures.None)
              | (hasStrongContextPath ? VfxAnalysisFeatures.StrongContextPath : VfxAnalysisFeatures.None)
              | (hasBoundTimelineItems ? VfxAnalysisFeatures.BoundTimelineItem : VfxAnalysisFeatures.None);

            analysis = new VfxAnalysis(
                file.Schedulers.Length,
                schedulerItemCount,
                schedulerTriggerCount,
                file.Timelines.Length,
                file.Emitters.Length,
                file.Particles.Length,
                file.Models.Length,
                drawLayer,
                binderFacts,
                particleTypes,
                featureFlags,
                loopFacts);
            return true;
        }
        catch
        {
            analysis = CreateEmptyAnalysis();
            return false;
        }
    }

    private static VfxAnalysis CreateEmptyAnalysis()
        => new(
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            VfxDrawLayer.Unknown,
            new VfxBinderFacts(0, 0, VfxBinderTypes.None, VfxBinderProperties.None),
            VfxParticleTypes.None,
            VfxAnalysisFeatures.None,
            VfxLoopFacts.Unknown);

    public static IReadOnlyList<TmbVfxReference> CollectTmbVfxReferences(IObjectAssetGameData gameData, string tmbPath)
    {
        var normalizedPath = GameAssetPathRules.NormalizeGamePath(tmbPath);
        if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Tmb))
        {
            return [];
        }

        HashSet<string> visitedTimelinePaths = new(StringComparer.OrdinalIgnoreCase);
        return CollectTmbVfxReferences(gameData, normalizedPath, visitedTimelinePaths);
    }

    public static IReadOnlyList<TmbVfxReference> CollectTmbVfxReferences(byte[] data)
        => CollectTmbVfxReferences(null, (ReadOnlySpan<byte>)data, null);

    public static IReadOnlyList<TmbVfxReference> CollectTmbVfxReferences(IObjectAssetGameData gameData, byte[] data)
        => CollectTmbVfxReferences(gameData, (ReadOnlySpan<byte>)data);

    public static IReadOnlyList<TmbVfxReference> CollectTmbVfxReferences(IObjectAssetGameData gameData, ReadOnlySpan<byte> data)
        => CollectTmbVfxReferences(gameData, data, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyList<TmbVfxReference> CollectTmbVfxReferences(
        IObjectAssetGameData gameData,
        string normalizedTimelinePath,
        ISet<string> visitedTimelinePaths)
    {
        if (!visitedTimelinePaths.Add(normalizedTimelinePath))
        {
            return [];
        }

        var gameFile = gameData.GetFile(normalizedTimelinePath);
        if (gameFile is null)
        {
            return [];
        }

        return CollectTmbVfxReferences(gameData, (ReadOnlySpan<byte>)gameFile.Data, visitedTimelinePaths);
    }

    private static IReadOnlyList<TmbVfxReference> CollectTmbVfxReferences(
        IObjectAssetGameData? gameData,
        ReadOnlySpan<byte> data,
        ISet<string>? visitedTimelinePaths)
    {
        Dictionary<string, TmbReferenceAccumulator> references = new(StringComparer.OrdinalIgnoreCase);
        List<TmbPendingVfxReference> directReferences = [];
        List<string> nestedTimelinePaths = [];
        bool hasAnimationReferences = false;
        bool hasSoundReferences = false;

        using var stream = new MemoryStream(data.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        if (!string.Equals(ReadAscii(reader, 4), TmbHeaderMagic, StringComparison.Ordinal))
        {
            return [];
        }

        var fileSize = reader.ReadInt32();
        var itemCount = reader.ReadInt32();
        if (fileSize <= 0 || itemCount <= 0)
        {
            return [];
        }

        for (var i = 0; i < itemCount && reader.BaseStream.Position + 8 <= reader.BaseStream.Length; i++)
        {
            var itemStart = reader.BaseStream.Position;
            var magic = ReadAscii(reader, 4);
            var size = reader.ReadInt32();
            if (size < 8 || itemStart + size > reader.BaseStream.Length)
            {
                break;
            }

            switch (magic)
            {
                case TmbNestedTimelineEntryMagic:
                    ParseNestedTimelineEntry(reader, itemStart, nestedTimelinePaths);
                    break;
                case TmbPapAnimationEntryMagic:
                    hasAnimationReferences |= ParseSimplePathEntry(reader, itemStart);
                    break;
                case TmbAnimationEntryMagic:
                    hasAnimationReferences |= ParseAnimationEntry(reader, itemStart);
                    break;
                case TmbVfxEntryMagic:
                    ParseVfxEntry(reader, itemStart, directReferences);
                    break;
                case TmbSoundEntryMagic:
                    hasSoundReferences |= ParseSimplePathEntry(reader, itemStart);
                    break;
                case TmbAsyncVfxEntryMagic:
                    ParseAsyncVfxEntry(reader, itemStart, directReferences);
                    break;
            }

            reader.BaseStream.Position = itemStart + size;
        }

        VfxTimelineContext fileContextFlags = VfxTimelineContext.None;
        if (hasAnimationReferences)
        {
            fileContextFlags |= VfxTimelineContext.AnimationReferenced;
        }

        if (hasSoundReferences)
        {
            fileContextFlags |= VfxTimelineContext.SoundReferenced;
        }

        foreach (TmbPendingVfxReference directReference in directReferences)
        {
            AddReference(
                references,
                directReference.Path,
                directReference.Evidence,
                fileContextFlags | directReference.ContextFlags);
        }

        if (gameData is not null && visitedTimelinePaths is not null)
        {
            foreach (string nestedTimelinePath in nestedTimelinePaths)
            {
                string normalizedTimelinePath = GameAssetPathRules.NormalizeGamePath(nestedTimelinePath);
                if (!GameAssetPathRules.IsFileKind(normalizedTimelinePath, GameAssetFileKind.Tmb)
                 || !gameData.FileExists(normalizedTimelinePath))
                {
                    continue;
                }

                foreach (TmbVfxReference reference in CollectTmbVfxReferences(gameData, normalizedTimelinePath, visitedTimelinePaths))
                {
                    AddReference(
                        references,
                        reference.Path,
                        reference.Evidence,
                        fileContextFlags | VfxTimelineContext.NestedTimeline | reference.ContextFlags);
                }
            }
        }

        return references
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => new TmbVfxReference(pair.Key, pair.Value.Evidence, pair.Value.ContextFlags))
            .ToArray();
    }

    private static void ParseNestedTimelineEntry(BinaryReader reader, long itemStart, ICollection<string> nestedTimelinePaths)
    {
        _ = reader.ReadInt32(); // duration
        _ = reader.ReadInt32(); // unknown
        _ = reader.ReadInt32(); // unknown
        int pathOffset = reader.ReadInt32();

        string path = ReadOffsetString(reader, itemStart, pathOffset);
        if (!string.IsNullOrWhiteSpace(path))
        {
            nestedTimelinePaths.Add(path);
        }
    }

    public static IReadOnlyList<string> CollectAvfxDependencyPaths(IObjectAssetGameData gameData, string vfxPath)
    {
        var normalizedPath = GameAssetPathRules.NormalizeGamePath(vfxPath);
        if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
        {
            return [];
        }

        var gameFile = gameData.GetFile(normalizedPath);
        if (gameFile is null)
        {
            return [];
        }

        try
        {
            var file = new AvfxFile(gameFile.Data);
            return CollectAvfxDependencyPaths(file, gameData, requireExistingModelPath: true);
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<string> CollectLocalAvfxDependencyPaths(string localFilePath)
    {
        try
        {
            byte[]? data = ObjectAssetFileUtility.TryReadLocalFileBytes(localFilePath);
            if (data is null)
            {
                return [];
            }

            var file = new AvfxFile(data);
            return CollectAvfxDependencyPaths(file, gameData: null, requireExistingModelPath: false);
        }
        catch
        {
            return [];
        }
    }

    private static bool ParseSimplePathEntry(BinaryReader reader, long itemStart)
    {
        _ = reader.ReadInt32(); // duration
        _ = reader.ReadInt32(); // unknown
        int pathOffset = reader.ReadInt32();
        string path = ReadOffsetString(reader, itemStart, pathOffset);
        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool ParseAnimationEntry(BinaryReader reader, long itemStart)
    {
        _ = reader.ReadInt32(); // duration
        _ = reader.ReadInt32(); // unknown
        _ = reader.ReadInt32(); // flags
        _ = reader.ReadSingle(); // animation start frame
        _ = reader.ReadSingle(); // animation end frame
        int pathOffset = reader.ReadInt32();
        _ = reader.ReadInt32(); // unknown

        string path = ReadOffsetString(reader, itemStart, pathOffset);
        return !string.IsNullOrWhiteSpace(path);
    }

    private static void ParseVfxEntry(BinaryReader reader, long itemStart, ICollection<TmbPendingVfxReference> references)
    {
        _ = reader.ReadInt16(); // id
        _ = reader.ReadInt16(); // time
        _ = reader.ReadInt32(); // duration
        _ = reader.ReadInt32(); // unknown
        int pathOffset = reader.ReadInt32();
        short bindPoint1 = reader.ReadInt16();
        short bindPoint2 = reader.ReadInt16();
        short bindPoint3 = reader.ReadInt16();
        short bindPoint4 = reader.ReadInt16();
        _ = reader.ReadInt32(); // scale offset
        _ = reader.ReadInt32(); // scale count
        _ = reader.ReadInt32(); // rotation offset
        _ = reader.ReadInt32(); // rotation count
        _ = reader.ReadInt32(); // position offset
        _ = reader.ReadInt32(); // position count
        _ = reader.ReadInt32(); // rgba offset
        _ = reader.ReadInt32(); // rgba count
        TmbVfxVisibility visibility = (TmbVfxVisibility)reader.ReadInt32();
        _ = reader.ReadInt32(); // unknown

        RuntimeVfxEvidence evidence = RuntimeVfxEvidence.TimelineReferenced;
        if (visibility is TmbVfxVisibility.DefaultWithTriggers or TmbVfxVisibility.AlwaysWithTriggers)
        {
            evidence |= RuntimeVfxEvidence.TriggerReferenced;
        }

        VfxTimelineContext contextFlags = HasNonDefaultBindPoints(
            [bindPoint1, bindPoint2, bindPoint3, bindPoint4],
            [(short)1, unchecked((short)0x00FF), (short)2, unchecked((short)0x00FF)])
            ? VfxTimelineContext.NonDefaultBindPoints
            : VfxTimelineContext.None;

        string path = ReadOffsetString(reader, itemStart, pathOffset);
        if (!string.IsNullOrWhiteSpace(path))
        {
            references.Add(new TmbPendingVfxReference(path, evidence, contextFlags));
        }
    }

    private static void ParseAsyncVfxEntry(BinaryReader reader, long itemStart, ICollection<TmbPendingVfxReference> references)
    {
        _ = reader.ReadInt32(); // loop / wait
        _ = reader.ReadInt32(); // unknown
        int pathOffset = reader.ReadInt32();
        short bindPoint1 = reader.ReadInt16();
        short bindPoint2 = reader.ReadInt16();

        RuntimeVfxEvidence evidence = RuntimeVfxEvidence.TimelineReferenced | RuntimeVfxEvidence.AsyncTimelineReferenced;
        VfxTimelineContext contextFlags = HasNonDefaultBindPoints(
            [bindPoint1, bindPoint2],
            [(short)1, unchecked((short)0x00FF)])
            ? VfxTimelineContext.NonDefaultBindPoints
            : VfxTimelineContext.None;

        string path = ReadOffsetString(reader, itemStart, pathOffset);
        if (!string.IsNullOrWhiteSpace(path))
        {
            references.Add(new TmbPendingVfxReference(path, evidence, contextFlags));
        }
    }

    private static void AddReference(
        IDictionary<string, TmbReferenceAccumulator> references,
        string path,
        RuntimeVfxEvidence evidence,
        VfxTimelineContext contextFlags)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        if (references.TryGetValue(normalizedPath, out TmbReferenceAccumulator existingReference))
        {
            references[normalizedPath] = existingReference with
            {
                Evidence = existingReference.Evidence | evidence,
                ContextFlags = existingReference.ContextFlags | contextFlags,
            };
            return;
        }

        references.Add(normalizedPath, new TmbReferenceAccumulator(evidence, contextFlags));
    }

    private static (int ItemCount, int TriggerCount) ReadSchedulerCounts(AvfxFile.Block block)
    {
        int itemCount = 0;
        int triggerCount = 0;
        AvfxChunkReader.ChunkCursor cursor = AvfxChunkReader.EnumerateChunks(block.Data);
        while (cursor.TryReadNext(out AvfxChunkReader.Chunk chunk))
        {
            if (chunk.Tag != AvfxChunkReader.Tags.SchedulerItemCount
             && chunk.Tag != AvfxChunkReader.Tags.SchedulerTriggerCount)
            {
                continue;
            }

            if (!AvfxChunkReader.TryReadSignedPayload(block.Data, chunk, out int value) || value <= 0)
            {
                continue;
            }

            if (chunk.Tag == AvfxChunkReader.Tags.SchedulerItemCount)
            {
                itemCount += value;
            }
            else if (chunk.Tag == AvfxChunkReader.Tags.SchedulerTriggerCount)
            {
                triggerCount += value;
            }
        }

        return (itemCount, triggerCount);
    }

    private static VfxBinderFacts AnalyzeBinders(AvfxFile.Block[] binders, AvfxFile.Block[] timelines)
    {
        int timelineBinderCount = CountTimelineBinders(timelines);
        VfxBinderTypes binderTypes = VfxBinderTypes.None;
        VfxBinderProperties propertyFlags = VfxBinderProperties.None;

        foreach (AvfxFile.Block binder in binders)
        {
            BinderParseState binderState = ParseBinder(binder.Data);
            binderTypes |= binderState.TypeFlags;
            propertyFlags |= binderState.PropertyFlags;
        }

        return new VfxBinderFacts(
            binders.Length,
            timelineBinderCount,
            binderTypes,
            propertyFlags);
    }

    private static int CountTimelineBinders(AvfxFile.Block[] timelines)
    {
        int count = 0;
        foreach (AvfxFile.Block timeline in timelines)
        {
            AvfxChunkReader.ChunkCursor cursor = AvfxChunkReader.EnumerateChunks(timeline.Data);
            while (cursor.TryReadNext(out AvfxChunkReader.Chunk chunk))
            {
                if (chunk.Tag == AvfxChunkReader.Tags.TimelineBinderNumber
                 && AvfxChunkReader.TryReadSignedPayload(timeline.Data, chunk, out int binderNumber)
                 && binderNumber >= 0)
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    private static VfxLoopFacts AnalyzeLoopFacts(
        AvfxFile.Block[] timelines,
        AvfxFile.Block[] emitters,
        AvfxFile.Block[] effectors)
    {
        VfxLoopSource loopSources = VfxLoopSource.None;
        MergeLoopSource(timelines, VfxLoopSource.Timeline, ref loopSources);
        MergeLoopSource(emitters, VfxLoopSource.Emitter, ref loopSources);
        MergeLoopSource(effectors, VfxLoopSource.Effector, ref loopSources);

        if (loopSources != VfxLoopSource.None)
        {
            return new VfxLoopFacts(VfxLoopBehavior.Permanent, loopSources);
        }

        return VfxLoopFacts.Unknown;
    }

    private static void MergeLoopSource(
        AvfxFile.Block[] blocks,
        VfxLoopSource source,
        ref VfxLoopSource loopSources)
    {
        foreach (AvfxFile.Block block in blocks)
        {
            if (AnalyzeBlockLoopBehavior(block.Data) == VfxLoopBehavior.Permanent)
            {
                loopSources |= source;
            }
        }
    }

    private static VfxLoopBehavior AnalyzeBlockLoopBehavior(byte[] data)
    {
        bool hasLoopStart = AvfxChunkReader.TryFindChunk(data, 0, data.Length, AvfxChunkReader.Tags.LoopStart, out AvfxChunkReader.Chunk loopStartChunk);
        bool hasLoopEnd = AvfxChunkReader.TryFindChunk(data, 0, data.Length, AvfxChunkReader.Tags.LoopEnd, out AvfxChunkReader.Chunk loopEndChunk);
        if (!hasLoopStart && !hasLoopEnd)
        {
            return VfxLoopBehavior.Unknown;
        }

        if (!hasLoopStart
         || !hasLoopEnd
         || !TryReadNativeTimelineFrame(data, loopStartChunk, out int loopStart)
         || !TryReadNativeTimelineFrame(data, loopEndChunk, out int loopEnd))
        {
            return VfxLoopBehavior.Unknown;
        }

        return loopEnd > loopStart
            ? VfxLoopBehavior.Permanent
            : VfxLoopBehavior.Unknown;
    }

    private static bool TryReadNativeTimelineFrame(ReadOnlySpan<byte> data, AvfxChunkReader.Chunk chunk, out int value)
    {
        value = 0;
        if (chunk.PayloadLength >= sizeof(int)
         && chunk.PayloadStart + sizeof(int) <= data.Length)
        {
            value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(chunk.PayloadStart, sizeof(int))) & 0x00FFFFFF;
            return true;
        }

        if (chunk.PayloadLength >= sizeof(short)
         && chunk.PayloadStart + sizeof(short) <= data.Length)
        {
            value = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(chunk.PayloadStart, sizeof(short))) & 0x00FFFFFF;
            return true;
        }

        return false;
    }

    private static BinderParseState ParseBinder(byte[] data)
    {
        BinderParseState state = new();
        AvfxChunkReader.ChunkCursor cursor = AvfxChunkReader.EnumerateChunks(data);
        while (cursor.TryReadNext(out AvfxChunkReader.Chunk chunk))
        {
            switch (chunk.Tag)
            {
                case AvfxChunkReader.Tags.BinderType:
                    if (AvfxChunkReader.TryReadSignedPayload(data, chunk, out int binderType))
                    {
                        state.TypeFlags = GetBinderTypeFlags((BinderType)binderType);
                    }

                    break;
                case AvfxChunkReader.Tags.BinderStartProperty:
                case AvfxChunkReader.Tags.BinderPrimaryProperty:
                case AvfxChunkReader.Tags.BinderSecondaryProperty:
                case AvfxChunkReader.Tags.BinderGoalProperty:
                    MergeBinderProperty(ParseBinderProperty(data, chunk), ref state);
                    break;
            }
        }

        state.PropertyFlags |= ResolveDeferredBinderPropertyFlags(state);
        return state;
    }

    private static VfxDrawLayer ReadDrawLayer(uint rawValue)
        => Enum.IsDefined(typeof(VfxDrawLayer), (int)rawValue)
            ? (VfxDrawLayer)(int)rawValue
            : VfxDrawLayer.Unknown;

    private static VfxParticleTypes AnalyzeParticles(AvfxFile.Block[] particles)
    {
        VfxParticleTypes particleTypes = VfxParticleTypes.None;
        foreach (AvfxFile.Block particle in particles)
        {
            AvfxChunkReader.ChunkCursor cursor = AvfxChunkReader.EnumerateChunks(particle.Data);
            while (cursor.TryReadNext(out AvfxChunkReader.Chunk chunk))
            {
                if (chunk.Tag != AvfxChunkReader.Tags.ParticleType
                 || !AvfxChunkReader.TryReadSignedPayload(particle.Data, chunk, out int particleType))
                {
                    continue;
                }

                particleTypes |= GetParticleTypeFlag((VfxParticleType)particleType);
            }
        }

        return particleTypes;
    }

    private static bool HasStrongContextPathHints(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(normalizedPath);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            foreach (var token in StrongContextTokens)
            {
                if (fileName.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (normalizedPath.Contains("/bgcommon/hou/", StringComparison.OrdinalIgnoreCase)
         && (normalizedPath.Contains("/vfx_hou_dyna", StringComparison.OrdinalIgnoreCase)
          || normalizedPath.Contains("reac", StringComparison.OrdinalIgnoreCase)
          || normalizedPath.Contains("dcomm", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (normalizedPath.Contains("/bgcommon/hou/common/vfx_hou_ind", StringComparison.OrdinalIgnoreCase)
         && fileName.StartsWith("igene", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.Contains("/foot_", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Contains("swim", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> CollectAvfxDependencyPaths(
        AvfxFile file,
        IObjectAssetGameData? gameData,
        bool requireExistingModelPath)
    {
        HashSet<string> dependencyPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string texturePath in file.Textures)
        {
            AddAvfxDependencyPath(gameData, dependencyPaths, texturePath, requireExistingModelPath);
        }

        foreach (AvfxFile.Block modelBlock in file.Models)
        {
            AddAvfxModelDependencyPath(gameData, dependencyPaths, modelBlock, requireExistingModelPath);
        }

        return dependencyPaths.Count == 0
            ? []
            : dependencyPaths
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private static void AddAvfxModelDependencyPath(
        IObjectAssetGameData? gameData,
        ISet<string> dependencyPaths,
        AvfxFile.Block modelBlock,
        bool requireExistingModelPath)
    {
        try
        {
            AddAvfxDependencyPath(gameData, dependencyPaths, modelBlock.ToString(), requireExistingModelPath);
        }
        catch
        {
            // ignore invalid model
        }
    }

    private static void AddAvfxDependencyPath(
        IObjectAssetGameData? gameData,
        ISet<string> dependencyPaths,
        string path,
        bool requireExistingModelPath)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(path);
        if (GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Atex))
        {
            dependencyPaths.Add(normalizedPath);
            return;
        }

        if (GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Mdl)
         && (!requireExistingModelPath
          || (gameData is not null && gameData.FileExists(normalizedPath))))
        {
            dependencyPaths.Add(normalizedPath);
        }
    }

    private static BinderPropertyState ParseBinderProperty(ReadOnlySpan<byte> data, in AvfxChunkReader.Chunk property)
    {
        BinderPropertyState state = new();
        AvfxChunkReader.ChunkCursor cursor = AvfxChunkReader.EnumerateChildren(data, property);
        while (cursor.TryReadNext(out AvfxChunkReader.Chunk chunk))
        {
            switch (chunk.Tag)
            {
                case AvfxChunkReader.Tags.BinderBindPoint:
                    if (AvfxChunkReader.TryReadSignedPayload(data, chunk, out int bindPoint))
                    {
                        state.HasBindPoint = true;
                        state.BindPoint = (BinderBindPoint)bindPoint;
                    }

                    break;
                case AvfxChunkReader.Tags.BinderBindTargetPoint:
                    if (AvfxChunkReader.TryReadSignedPayload(data, chunk, out int bindTargetPoint))
                    {
                        state.HasBindTargetPoint = true;
                        state.BindTargetPoint = (BinderBindTargetPoint)bindTargetPoint;
                    }

                    break;
                case AvfxChunkReader.Tags.BinderBindPointId:
                    if (AvfxChunkReader.TryReadSignedPayload(data, chunk, out int bindPointId))
                    {
                        state.HasBindPointId = true;
                        state.BindPointId = bindPointId;
                    }

                    break;
            }
        }

        return state;
    }

    private static void MergeBinderProperty(BinderPropertyState property, ref BinderParseState state)
    {
        if (property.HasBindPointId && property.BindPointId != 0)
        {
            state.PropertyFlags |= VfxBinderProperties.ExplicitBindPointId;
        }

        if (property.HasBindPoint)
        {
            state.BindPointFlags |= GetBinderBindPoints(property.BindPoint);
        }

        if (property.HasBindTargetPoint)
        {
            state.BindTargetPointFlags |= GetBinderBindTargetPoints(property.BindTargetPoint);
        }

        if (property.HasBindPoint && property.HasBindTargetPoint)
        {
            state.PropertyFlags |= GetBinderPropertyFlags(property.BindPoint, property.BindTargetPoint);
        }
    }

    private static VfxBinderProperties ResolveDeferredBinderPropertyFlags(BinderParseState state)
    {
        if (state.BindTargetPointFlags == BinderBindTargetPoints.None)
        {
            return VfxBinderProperties.None;
        }

        var bindPoints = EnumerateBindPoints(state.BindPointFlags);
        var bindTargetPoints = EnumerateBindTargetPoints(state.BindTargetPointFlags);
        VfxBinderProperties propertyFlags = VfxBinderProperties.None;
        foreach (var bindPoint in bindPoints)
        {
            foreach (var bindTargetPoint in bindTargetPoints)
            {
                propertyFlags |= GetBinderPropertyFlags(bindPoint, bindTargetPoint);
            }
        }

        return propertyFlags;
    }

    private static VfxBinderTypes GetBinderTypeFlags(BinderType binderType)
        => binderType switch
        {
            BinderType.Point => VfxBinderTypes.Point,
            BinderType.Linear => VfxBinderTypes.Linear,
            BinderType.Spline => VfxBinderTypes.Spline,
            BinderType.Camera => VfxBinderTypes.Camera,
            BinderType.LinearAdjust => VfxBinderTypes.LinearAdjust,
            _ => VfxBinderTypes.None,
        };

    private static BinderBindPoints GetBinderBindPoints(BinderBindPoint bindPoint)
        => bindPoint switch
        {
            BinderBindPoint.Caster => BinderBindPoints.Caster,
            BinderBindPoint.Target => BinderBindPoints.Target,
            _ => BinderBindPoints.None,
        };

    private static BinderBindTargetPoints GetBinderBindTargetPoints(BinderBindTargetPoint bindTargetPoint)
        => bindTargetPoint switch
        {
            BinderBindTargetPoint.Origin => BinderBindTargetPoints.Origin,
            BinderBindTargetPoint.FitGround => BinderBindTargetPoints.FitGround,
            BinderBindTargetPoint.DamageCircle => BinderBindTargetPoints.DamageCircle,
            BinderBindTargetPoint.ByName => BinderBindTargetPoints.ByName,
            _ => BinderBindTargetPoints.None,
        };

    private static VfxBinderProperties GetBinderPropertyFlags(BinderBindPoint bindPoint, BinderBindTargetPoint bindTargetPoint)
        => (bindPoint, bindTargetPoint) switch
        {
            (BinderBindPoint.Caster, BinderBindTargetPoint.Origin) => VfxBinderProperties.CasterOrigin,
            (BinderBindPoint.Target, BinderBindTargetPoint.Origin) => VfxBinderProperties.TargetOrigin,
            (BinderBindPoint.Caster, BinderBindTargetPoint.FitGround) => VfxBinderProperties.CasterFitGround,
            (BinderBindPoint.Target, BinderBindTargetPoint.FitGround) => VfxBinderProperties.TargetFitGround,
            (BinderBindPoint.Caster, BinderBindTargetPoint.DamageCircle) => VfxBinderProperties.CasterDamageCircle,
            (BinderBindPoint.Target, BinderBindTargetPoint.DamageCircle) => VfxBinderProperties.TargetDamageCircle,
            (BinderBindPoint.Caster, BinderBindTargetPoint.ByName) => VfxBinderProperties.CasterByName,
            (BinderBindPoint.Target, BinderBindTargetPoint.ByName) => VfxBinderProperties.TargetByName,
            _ => VfxBinderProperties.None,
        };

    private static IReadOnlyList<BinderBindPoint> EnumerateBindPoints(BinderBindPoints bindPointFlags)
    {
        if (bindPointFlags == BinderBindPoints.None)
        {
            return [DefaultBinderBindPoint];
        }

        List<BinderBindPoint> bindPoints = [];
        if (HasAny(bindPointFlags, BinderBindPoints.Caster))
        {
            bindPoints.Add(BinderBindPoint.Caster);
        }

        if (HasAny(bindPointFlags, BinderBindPoints.Target))
        {
            bindPoints.Add(BinderBindPoint.Target);
        }

        return bindPoints;
    }

    private static IReadOnlyList<BinderBindTargetPoint> EnumerateBindTargetPoints(BinderBindTargetPoints bindTargetPointFlags)
    {
        List<BinderBindTargetPoint> bindTargetPoints = [];
        if (HasAny(bindTargetPointFlags, BinderBindTargetPoints.Origin))
        {
            bindTargetPoints.Add(BinderBindTargetPoint.Origin);
        }

        if (HasAny(bindTargetPointFlags, BinderBindTargetPoints.FitGround))
        {
            bindTargetPoints.Add(BinderBindTargetPoint.FitGround);
        }

        if (HasAny(bindTargetPointFlags, BinderBindTargetPoints.DamageCircle))
        {
            bindTargetPoints.Add(BinderBindTargetPoint.DamageCircle);
        }

        if (HasAny(bindTargetPointFlags, BinderBindTargetPoints.ByName))
        {
            bindTargetPoints.Add(BinderBindTargetPoint.ByName);
        }

        return bindTargetPoints;
    }

    private static VfxParticleTypes GetParticleTypeFlag(VfxParticleType particleType)
        => particleType switch
        {
            VfxParticleType.Parameter => VfxParticleTypes.Parameter,
            VfxParticleType.Powder => VfxParticleTypes.Powder,
            VfxParticleType.Windmill => VfxParticleTypes.Windmill,
            VfxParticleType.Line => VfxParticleTypes.Line,
            VfxParticleType.Laser => VfxParticleTypes.Laser,
            VfxParticleType.Model => VfxParticleTypes.Model,
            VfxParticleType.Polyline => VfxParticleTypes.Polyline,
            VfxParticleType.Reserve0 => VfxParticleTypes.Reserve0,
            VfxParticleType.Quad => VfxParticleTypes.Quad,
            VfxParticleType.Polygon => VfxParticleTypes.Polygon,
            VfxParticleType.Decal => VfxParticleTypes.Decal,
            VfxParticleType.DecalRing => VfxParticleTypes.DecalRing,
            VfxParticleType.Disc => VfxParticleTypes.Disc,
            VfxParticleType.LightModel => VfxParticleTypes.LightModel,
            VfxParticleType.ModelSkin => VfxParticleTypes.ModelSkin,
            VfxParticleType.Dissolve => VfxParticleTypes.Dissolve,
            _ => VfxParticleTypes.None,
        };

    private static bool HasAny(BinderBindPoints value, BinderBindPoints flags)
        => (value & flags) != BinderBindPoints.None;

    private static bool HasAny(BinderBindTargetPoints value, BinderBindTargetPoints flags)
        => (value & flags) != BinderBindTargetPoints.None;

    private static string ReadOffsetString(BinaryReader reader, long itemStart, int offset)
    {
        if (offset <= 0)
        {
            return string.Empty;
        }

        var savePosition = reader.BaseStream.Position;
        long targetPosition = itemStart + 8L + offset;
        if (targetPosition < 0 || targetPosition >= reader.BaseStream.Length)
        {
            return string.Empty;
        }

        reader.BaseStream.Position = targetPosition;

        var bytes = new List<byte>();
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var next = reader.ReadByte();
            if (next == 0)
            {
                break;
            }

            bytes.Add(next);
        }

        reader.BaseStream.Position = savePosition;
        return GameAssetPathRules.NormalizeGamePath(Encoding.ASCII.GetString([.. bytes]));
    }

    private static string ReadAscii(BinaryReader reader, int size)
        => Encoding.ASCII.GetString(reader.ReadBytes(size));

    private sealed class BinderParseState
    {
        public VfxBinderTypes TypeFlags { get; set; }
        public VfxBinderProperties PropertyFlags { get; set; }
        public BinderBindPoints BindPointFlags { get; set; }
        public BinderBindTargetPoints BindTargetPointFlags { get; set; }
    }

    private sealed class BinderPropertyState
    {
        public bool HasBindPoint { get; set; }
        public BinderBindPoint BindPoint { get; set; }
        public bool HasBindTargetPoint { get; set; }
        public BinderBindTargetPoint BindTargetPoint { get; set; }
        public bool HasBindPointId { get; set; }
        public int BindPointId { get; set; }
    }

    private static bool HasNonDefaultBindPoints(ReadOnlySpan<short> bindPoints, ReadOnlySpan<short> defaultBindPoints)
    {
        if (bindPoints.Length != defaultBindPoints.Length)
        {
            return bindPoints.Length > 0;
        }

        for (int i = 0; i < bindPoints.Length; i++)
        {
            if (bindPoints[i] != defaultBindPoints[i])
            {
                return true;
            }
        }

        return false;
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct TmbReferenceAccumulator(
        RuntimeVfxEvidence Evidence,
        VfxTimelineContext ContextFlags);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct TmbPendingVfxReference(
        string Path,
        RuntimeVfxEvidence Evidence,
        VfxTimelineContext ContextFlags);
}

