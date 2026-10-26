using Intoner.Objects.Resources;
using Penumbra.GameData.Files;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Assets;

internal static class VfxAssetAnalyzer
{
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

    public static bool TryAnalyzeAvfx(IObjectAssetGameData gameData, string vfxPath, out VfxAnalysis analysis)
    {
        var normalizedPath = ObjectResolvedPath.FromGamePath(vfxPath).Path;
        if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
        {
            analysis = CreateEmptyAnalysis();
            return false;
        }

        Lumina.Data.FileResource? gameFile;
        try
        {
            gameFile = gameData.GetFile(normalizedPath);
        }
        catch (Exception ex) when (ObjectAssetFileUtility.IsReadFailure(ex))
        {
            gameFile = null;
        }

        if (gameFile is null)
        {
            analysis = CreateEmptyAnalysis();
            return false;
        }

        return TryAnalyzeAvfx(normalizedPath, gameFile.Data, out analysis);
    }

    public static bool TryAnalyzeAvfx(string vfxPath, byte[] data, out VfxAnalysis analysis)
    {
        var normalizedPath = ObjectResolvedPath.FromGamePath(vfxPath).Path;
        analysis = CreateEmptyAnalysis();

        if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
        {
            return false;
        }

        if (!AvfxChunkReader.TryReadRoot(data, out AvfxChunkReader.Chunk avfx))
        {
            return false;
        }

        int schedulerCount = 0;
        int schedulerItemCount = 0;
        int schedulerTriggerCount = 0;
        int timelineCount = 0;
        int timelineBinderCount = 0;
        int emitterCount = 0;
        int particleCount = 0;
        int modelCount = 0;
        int binderCount = 0;
        VfxDrawLayer drawLayer = VfxDrawLayer.Unknown;
        VfxBinderTypes binderTypes = VfxBinderTypes.None;
        VfxBinderProperties binderProperties = VfxBinderProperties.None;
        VfxParticleTypes particleTypes = VfxParticleTypes.None;
        VfxLoopSource loopSources = VfxLoopSource.None;
        bool isFitGround = false;
        bool isCameraSpace = false;
        bool isAllStopOnHide = false;
        bool hasBoundTimelineItems = false;
        AvfxChunkReader.ChunkCursor cursor = AvfxChunkReader.EnumerateChildren(data, avfx);
        while (cursor.TryReadNext(out AvfxChunkReader.Chunk chunk))
        {
            ReadOnlySpan<byte> payload = data.AsSpan(chunk.PayloadStart, chunk.PayloadLength);
            switch (chunk.Tag)
            {
                case AvfxMagic.Scheduler:
                    if (!TryReadSchedulerCounts(payload, out int itemCount, out int triggerCount)
                     || itemCount > int.MaxValue - schedulerItemCount
                     || triggerCount > int.MaxValue - schedulerTriggerCount)
                    {
                        return false;
                    }

                    schedulerCount++;
                    schedulerItemCount += itemCount;
                    schedulerTriggerCount += triggerCount;
                    break;
                case AvfxMagic.Timeline:
                    if (!TryReadTimelineFacts(payload, out bool hasTimelineBinder, out bool hasTimelineLoop, out bool hasBoundItems))
                    {
                        return false;
                    }

                    timelineCount++;
                    timelineBinderCount += hasTimelineBinder ? 1 : 0;
                    hasBoundTimelineItems |= hasBoundItems;

                    if (hasTimelineLoop)
                    {
                        loopSources |= VfxLoopSource.Timeline;
                    }
                    break;
                case AvfxMagic.Emitter:
                case AvfxMagic.Effector:
                    if (!TryReadBlockLoop(payload, out bool hasLoop))
                    {
                        return false;
                    }

                    if (chunk.Tag == AvfxMagic.Emitter)
                    {
                        emitterCount++;
                    }

                    if (hasLoop)
                    {
                        loopSources |= chunk.Tag == AvfxMagic.Emitter ? VfxLoopSource.Emitter : VfxLoopSource.Effector;
                    }

                    break;
                case AvfxMagic.Particle:
                    if (!TryAnalyzeParticle(payload, out VfxParticleTypes particleFlags))
                    {
                        return false;
                    }

                    particleCount++;
                    particleTypes |= particleFlags;
                    break;
                case AvfxMagic.Model:
                    modelCount++;
                    break;
                case AvfxMagic.Binder:
                    if (!TryParseBinder(payload, out BinderParseState binderState))
                    {
                        return false;
                    }

                    binderCount++;
                    binderTypes |= binderState.TypeFlags;
                    binderProperties |= binderState.PropertyFlags;
                    break;
                case AvfxMagic.DrawLayerType:
                    if (!AvfxChunkReader.TryReadSignedPayload(data, chunk, out int rawDrawLayer))
                    {
                        return false;
                    }

                    drawLayer = ReadDrawLayer(unchecked((uint)rawDrawLayer));
                    break;
                case AvfxMagic.IsFitGround:
                case AvfxMagic.IsCameraSpace:
                case AvfxMagic.IsAllStopOnHide:
                    if (payload.IsEmpty)
                    {
                        return false;
                    }

                    if (chunk.Tag == AvfxMagic.IsFitGround)
                    {
                        isFitGround = payload[0] != 0;
                    }
                    else if (chunk.Tag == AvfxMagic.IsCameraSpace)
                    {
                        isCameraSpace = payload[0] != 0;
                    }
                    else
                    {
                        isAllStopOnHide = payload[0] != 0;
                    }

                    break;
            }
        }

        if (!cursor.IsComplete)
        {
            return false;
        }

        bool usesWaterLayer = drawLayer is VfxDrawLayer.InWater or VfxDrawLayer.FitWater;
        bool usesScreenLayer = drawLayer is VfxDrawLayer.Screen or VfxDrawLayer.PostUi or VfxDrawLayer.PrevUi;
        bool hasStrongContextPath = HasStrongContextPathHints(normalizedPath);
        VfxAnalysisFeatures featureFlags =
            (isFitGround ? VfxAnalysisFeatures.FitGround : VfxAnalysisFeatures.None)
          | (isCameraSpace ? VfxAnalysisFeatures.CameraSpace : VfxAnalysisFeatures.None)
          | (isAllStopOnHide ? VfxAnalysisFeatures.AllStopOnHide : VfxAnalysisFeatures.None)
          | (usesWaterLayer ? VfxAnalysisFeatures.UsesWaterLayer : VfxAnalysisFeatures.None)
          | (usesScreenLayer ? VfxAnalysisFeatures.UsesScreenLayer : VfxAnalysisFeatures.None)
          | (hasStrongContextPath ? VfxAnalysisFeatures.StrongContextPath : VfxAnalysisFeatures.None)
          | (hasBoundTimelineItems ? VfxAnalysisFeatures.BoundTimelineItem : VfxAnalysisFeatures.None);
        VfxLoopFacts loopFacts = loopSources == VfxLoopSource.None
            ? VfxLoopFacts.Unknown
            : new VfxLoopFacts(VfxLoopBehavior.Permanent, loopSources);

        analysis = new VfxAnalysis(
            schedulerCount,
            schedulerItemCount,
            schedulerTriggerCount,
            timelineCount,
            emitterCount,
            particleCount,
            modelCount,
            drawLayer,
            new VfxBinderFacts(binderCount, timelineBinderCount, binderTypes, binderProperties),
            particleTypes,
            featureFlags,
            loopFacts);
        return true;
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

    public static AssetDependencyPaths CollectAvfxDependencyPaths(
        IObjectAssetGameData gameData,
        string vfxPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedPath = ObjectResolvedPath.FromGamePath(vfxPath).Path;

        if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
        {
            return AssetDependencyPaths.Empty;
        }

        ObjectAssetFileData file = ObjectAssetFileUtility.ReadGameFile(gameData, normalizedPath, cancellationToken);
        return file.Data is null ? new([], file.IsComplete) : CollectAvfxDependencyPaths(file.Data, cancellationToken);
    }

    public static AssetDependencyPaths CollectLocalAvfxDependencyPaths(
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        byte[]? data = ObjectAssetFileUtility.TryReadLocalFileBytes(localFilePath, cancellationToken);
        return data is null ? AssetDependencyPaths.Incomplete : CollectAvfxDependencyPaths(data, cancellationToken);
    }

    private static bool TryReadSchedulerCounts(ReadOnlySpan<byte> data, out int itemCount, out int triggerCount)
    {
        itemCount = 0;
        triggerCount = 0;
        AvfxChunkReader.ChunkCursor cursor = AvfxChunkReader.EnumerateChunks(data);
        while (cursor.TryReadNext(out AvfxChunkReader.Chunk chunk))
        {
            if (chunk.Tag != AvfxChunkReader.Tags.SchedulerItemCount
             && chunk.Tag != AvfxChunkReader.Tags.SchedulerTriggerCount)
            {
                continue;
            }

            if (!AvfxChunkReader.TryReadSignedPayload(data, chunk, out int value) || value <= 0)
            {
                continue;
            }

            if (chunk.Tag == AvfxChunkReader.Tags.SchedulerItemCount)
            {
                if (value > int.MaxValue - itemCount)
                {
                    return false;
                }

                itemCount += value;
            }
            else if (chunk.Tag == AvfxChunkReader.Tags.SchedulerTriggerCount)
            {
                if (value > int.MaxValue - triggerCount)
                {
                    return false;
                }

                triggerCount += value;
            }
        }

        return cursor.IsComplete;
    }

    private static bool TryReadTimelineFacts(
        ReadOnlySpan<byte> data,
        out bool hasTimelineBinder,
        out bool hasPermanentLoop,
        out bool hasBoundTimelineItems)
    {
        hasTimelineBinder = false;
        hasBoundTimelineItems = false;
        LoopChunks loop = default;
        AvfxChunkReader.ChunkCursor cursor = AvfxChunkReader.EnumerateChunks(data);
        while (cursor.TryReadNext(out AvfxChunkReader.Chunk chunk))
        {
            if (chunk.Tag == AvfxChunkReader.Tags.TimelineBinderNumber)
            {
                if (!AvfxChunkReader.TryReadSignedPayload(data, chunk, out int binderNumber))
                {
                    hasPermanentLoop = false;
                    return false;
                }

                hasTimelineBinder |= binderNumber >= 0;
            }

            if (chunk.Tag == AvfxChunkReader.Tags.TimelineItem)
            {
                if (!AvfxChunkReader.TryReadTimelineItemBindPoints(data, chunk, out int boundItemCount))
                {
                    hasPermanentLoop = false;
                    return false;
                }

                hasBoundTimelineItems = boundItemCount > 0;
            }

            loop.Observe(chunk);
        }

        hasPermanentLoop = loop.IsPermanent(data);
        return cursor.IsComplete;
    }

    private static bool TryReadBlockLoop(ReadOnlySpan<byte> data, out bool hasPermanentLoop)
    {
        LoopChunks loop = default;
        AvfxChunkReader.ChunkCursor cursor = AvfxChunkReader.EnumerateChunks(data);
        while (cursor.TryReadNext(out AvfxChunkReader.Chunk chunk))
        {
            loop.Observe(chunk);
        }

        hasPermanentLoop = loop.IsPermanent(data);
        return cursor.IsComplete;
    }

    [StructLayout(LayoutKind.Auto)]
    private struct LoopChunks
    {
        private AvfxChunkReader.Chunk? _start;
        private AvfxChunkReader.Chunk? _end;

        public void Observe(AvfxChunkReader.Chunk chunk)
        {
            if (chunk.Tag == AvfxChunkReader.Tags.LoopStart)
            {
                _start ??= chunk;
            }
            else if (chunk.Tag == AvfxChunkReader.Tags.LoopEnd)
            {
                _end ??= chunk;
            }
        }

        public readonly bool IsPermanent(ReadOnlySpan<byte> data)
            => _start is AvfxChunkReader.Chunk start
            && _end is AvfxChunkReader.Chunk end
            && TryReadNativeTimelineFrame(data, start, out int startFrame)
            && TryReadNativeTimelineFrame(data, end, out int endFrame)
            && endFrame > startFrame;

        private static bool TryReadNativeTimelineFrame(ReadOnlySpan<byte> data, AvfxChunkReader.Chunk chunk, out int value)
        {
            value = 0;

            if (chunk.PayloadLength < sizeof(short) || !AvfxChunkReader.TryReadSignedPayload(data, chunk, out int rawValue))
            {
                return false;
            }

            value = rawValue & 0x00FFFFFF;
            return true;
        }
    }

    private static bool TryParseBinder(ReadOnlySpan<byte> data, out BinderParseState state)
    {
        state = new();
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
                case uint tag when AvfxChunkReader.IsBinderPropertyTag(tag):
                    if (!AvfxChunkReader.TryReadBinderProperty(data, chunk, out AvfxChunkReader.BinderProperty property))
                    {
                        return false;
                    }

                    MergeBinderProperty(property, ref state);
                    break;
            }
        }

        return cursor.IsComplete;
    }

    private static VfxDrawLayer ReadDrawLayer(uint rawValue)
        => Enum.IsDefined(typeof(VfxDrawLayer), (int)rawValue)
            ? (VfxDrawLayer)(int)rawValue
            : VfxDrawLayer.Unknown;

    private static bool TryAnalyzeParticle(ReadOnlySpan<byte> data, out VfxParticleTypes particleTypes)
    {
        particleTypes = VfxParticleTypes.None;
        AvfxChunkReader.ChunkCursor cursor = AvfxChunkReader.EnumerateChunks(data);
        while (cursor.TryReadNext(out AvfxChunkReader.Chunk chunk))
        {
            if (chunk.Tag != AvfxChunkReader.Tags.ParticleType
             || !AvfxChunkReader.TryReadSignedPayload(data, chunk, out int particleType))
            {
                continue;
            }

            particleTypes |= GetParticleTypeFlag((VfxParticleType)particleType);
        }

        return cursor.IsComplete;
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

        if (normalizedPath.StartsWith("bgcommon/hou/", StringComparison.OrdinalIgnoreCase)
         && (normalizedPath.Contains("/vfx_hou_dyna", StringComparison.OrdinalIgnoreCase)
          || normalizedPath.Contains("reac", StringComparison.OrdinalIgnoreCase)
          || normalizedPath.Contains("dcomm", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (normalizedPath.StartsWith("bgcommon/hou/common/vfx_hou_ind", StringComparison.OrdinalIgnoreCase)
         && fileName.StartsWith("igene", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.Contains("/foot_", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Contains("swim", StringComparison.OrdinalIgnoreCase);
    }

    private static AssetDependencyPaths CollectAvfxDependencyPaths(
        ReadOnlySpan<byte> data,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AvfxChunkReader.TryReadRoot(data, out AvfxChunkReader.Chunk avfx))
        {
            return AssetDependencyPaths.Incomplete;
        }

        HashSet<string> dependencyPaths = new(StringComparer.OrdinalIgnoreCase);
        AvfxChunkReader.ChunkCursor cursor = AvfxChunkReader.EnumerateChildren(data, avfx);
        while (cursor.TryReadNext(out AvfxChunkReader.Chunk chunk))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (chunk.Tag)
            {
                case AvfxMagic.Texture:
                case AvfxMagic.Model:
                    GameAssetFileKind kind = chunk.Tag == AvfxMagic.Texture ? GameAssetFileKind.Atex : GameAssetFileKind.Mdl;

                    if (!TryAddAvfxDependencyPath(data, chunk, kind, dependencyPaths))
                    {
                        return AssetDependencyPaths.Incomplete;
                    }

                    break;
                case AvfxMagic.Emitter:
                    if (!CollectAvfxSoundDependencyPaths(data, chunk, dependencyPaths, cancellationToken))
                    {
                        return AssetDependencyPaths.Incomplete;
                    }

                    break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return cursor.IsComplete
            ? new(dependencyPaths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray())
            : AssetDependencyPaths.Incomplete;
    }

    private static bool CollectAvfxSoundDependencyPaths(
        ReadOnlySpan<byte> data,
        in AvfxChunkReader.Chunk emitter,
        ISet<string> dependencyPaths,
        CancellationToken cancellationToken)
    {
        AvfxChunkReader.ChunkCursor cursor = AvfxChunkReader.EnumerateChildren(data, emitter);

        while (cursor.TryReadNext(out AvfxChunkReader.Chunk chunk))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (chunk.Tag == AvfxChunkReader.Tags.EmitterSound
             && !TryAddAvfxDependencyPath(data, chunk, GameAssetFileKind.Scd, dependencyPaths))
            {
                return false;
            }
        }

        return cursor.IsComplete;
    }

    private static bool TryAddAvfxDependencyPath(
        ReadOnlySpan<byte> data,
        in AvfxChunkReader.Chunk chunk,
        GameAssetFileKind kind,
        ISet<string> dependencyPaths)
    {
        if (!AvfxChunkReader.TryReadPathPayload(data, chunk, out string path))
        {
            return false;
        }

        string normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;

        if (GameAssetPathRules.ClassifyNormalizedFilePath(normalizedPath) == kind)
        {
            dependencyPaths.Add(normalizedPath);
        }

        return true;
    }

    private static void MergeBinderProperty(AvfxChunkReader.BinderProperty property, ref BinderParseState state)
    {
        if (property.BindPointId is { Value: not 0 })
        {
            state.PropertyFlags |= VfxBinderProperties.ExplicitBindPointId;
        }

        if (property.BindTargetPoint is { } target)
        {
            BinderBindPoint point = (BinderBindPoint)(property.BindPoint?.Value ?? (int)DefaultBinderBindPoint);
            state.PropertyFlags |= GetBinderPropertyFlags(point, (BinderBindTargetPoint)target.Value);
        }
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

    [StructLayout(LayoutKind.Auto)]
    private struct BinderParseState
    {
        public VfxBinderTypes TypeFlags { get; set; }
        public VfxBinderProperties PropertyFlags { get; set; }
    }
}
