using Penumbra.GameData.Files;
using System.Text;
using Intoner.Objects.Utils;
using Intoner.Objects.Assets;

namespace Intoner.Objects.Assets;

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
    private const string SchedulerItemCountChunk = "ItCn";
    private const string SchedulerTriggerCountChunk = "TrCn";
    private const string ParticleTypeChunk = "PrVT";
    private const string BinderTypeChunk = "BnVr";
    private const string BinderStartPropertyChunk = "PrpS";
    private const string BinderPrimaryPropertyChunk = "Prp1";
    private const string BinderSecondaryPropertyChunk = "Prp2";
    private const string BinderGoalPropertyChunk = "PrpG";
    private const string BinderBindPointChunk = "BPT";
    private const string BinderBindTargetPointChunk = "BPTP";
    private const string BinderBindPointIdChunk = "BPID";
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
        analysis = new VfxAnalysis(
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
            VfxAnalysisFeatures.None);

        var normalizedPath = ObjectPathRules.NormalizeGamePath(vfxPath);
        if (!ObjectPathRules.IsVfxPath(normalizedPath))
        {
            return false;
        }

        var gameFile = gameData.GetFile(normalizedPath);
        if (gameFile is null)
        {
            return false;
        }

        try
        {
            var file = new AvfxFile(gameFile.Data);
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
            bool hasStrongContextPath = HasStrongContextPathHints(normalizedPath);
            VfxAnalysisFeatures featureFlags =
                (isFitGround ? VfxAnalysisFeatures.FitGround : VfxAnalysisFeatures.None)
              | (isCameraSpace ? VfxAnalysisFeatures.CameraSpace : VfxAnalysisFeatures.None)
              | (isAllStopOnHide ? VfxAnalysisFeatures.AllStopOnHide : VfxAnalysisFeatures.None)
              | (usesWaterLayer ? VfxAnalysisFeatures.UsesWaterLayer : VfxAnalysisFeatures.None)
              | (usesScreenLayer ? VfxAnalysisFeatures.UsesScreenLayer : VfxAnalysisFeatures.None)
              | (hasStrongContextPath ? VfxAnalysisFeatures.StrongContextPath : VfxAnalysisFeatures.None);

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
                featureFlags);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<TmbVfxReference> CollectTmbVfxReferences(IObjectAssetGameData gameData, string tmbPath)
    {
        var normalizedPath = ObjectPathRules.NormalizeGamePath(tmbPath);
        if (!ObjectPathRules.IsTimelinePath(normalizedPath))
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
                    hasAnimationReferences |= ParsePapAnimationEntry(reader, itemStart);
                    break;
                case TmbAnimationEntryMagic:
                    hasAnimationReferences |= ParseAnimationEntry(reader, itemStart);
                    break;
                case TmbVfxEntryMagic:
                    ParseVfxEntry(reader, itemStart, directReferences);
                    break;
                case TmbSoundEntryMagic:
                    hasSoundReferences |= ParseSoundEntry(reader, itemStart);
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
                string normalizedTimelinePath = ObjectPathRules.NormalizeGamePath(nestedTimelinePath);
                if (!ObjectPathRules.IsTimelinePath(normalizedTimelinePath)
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
        var normalizedPath = ObjectPathRules.NormalizeGamePath(vfxPath);
        if (!ObjectPathRules.IsVfxPath(normalizedPath))
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

    private static bool ParsePapAnimationEntry(BinaryReader reader, long itemStart)
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

    private static bool ParseSoundEntry(BinaryReader reader, long itemStart)
    {
        _ = reader.ReadInt32(); // loop / duration
        _ = reader.ReadInt32(); // interrupt
        int pathOffset = reader.ReadInt32();
        string path = ReadOffsetString(reader, itemStart, pathOffset);
        return !string.IsNullOrWhiteSpace(path);
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
        string normalizedPath = ObjectPathRules.NormalizeGamePath(path);
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
        ParseNestedBlocks(block.Data, (chunkName, chunkSize, chunkReader) =>
        {
            if (string.Equals(chunkName, SchedulerItemCountChunk, StringComparison.Ordinal))
            {
                itemCount += ReadInt32(chunkReader, chunkSize);
            }

            if (string.Equals(chunkName, SchedulerTriggerCountChunk, StringComparison.Ordinal))
            {
                triggerCount += ReadInt32(chunkReader, chunkSize);
            }
        });

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
            bool hasBinder = false;
            ParseNestedBlocks(timeline.Data, (chunkName, chunkSize, chunkReader) =>
            {
                if (string.Equals(chunkName, "BnNo", StringComparison.Ordinal) && ReadInt32(chunkReader, chunkSize) >= 0)
                {
                    hasBinder = true;
                }
            });

            if (hasBinder)
            {
                count++;
            }
        }

        return count;
    }

    private static BinderParseState ParseBinder(byte[] data)
    {
        BinderParseState state = new();
        ParseNestedBlocks(data, (chunkName, chunkSize, chunkReader) =>
        {
            switch (chunkName)
            {
                case BinderTypeChunk:
                    state.TypeFlags = GetBinderTypeFlags((BinderType)ReadInt32(chunkReader, chunkSize));
                    break;
                case BinderStartPropertyChunk:
                case BinderPrimaryPropertyChunk:
                case BinderSecondaryPropertyChunk:
                case BinderGoalPropertyChunk:
                    MergeBinderProperty(ParseBinderProperty(chunkReader.ReadBytes(chunkSize)), ref state);
                    break;
            }
        });

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
        foreach (var particle in particles)
        {
            ParseNestedBlocks(particle.Data, (chunkName, chunkSize, chunkReader) =>
            {
                if (!string.Equals(chunkName, ParticleTypeChunk, StringComparison.Ordinal))
                {
                    return;
                }

                var particleType = (VfxParticleType)ReadInt32(chunkReader, chunkSize);
                particleTypes |= GetParticleTypeFlag(particleType);
            });
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
        string normalizedPath = ObjectPathRules.NormalizeGamePath(path);
        if (normalizedPath.EndsWith(".atex", StringComparison.OrdinalIgnoreCase))
        {
            dependencyPaths.Add(normalizedPath);
            return;
        }

        if (AssetPathClassifier.IsModelPath(normalizedPath)
         && (!requireExistingModelPath
          || (gameData is not null && gameData.FileExists(normalizedPath))))
        {
            dependencyPaths.Add(normalizedPath);
        }
    }

    private static void ParseNestedBlocks(
        byte[] data,
        Action<string, int, BinaryReader> onChunk)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunkName = ReadChunkName(reader.ReadUInt32());
            var chunkSize = reader.ReadInt32();
            if (chunkSize < 0 || reader.BaseStream.Position + chunkSize > reader.BaseStream.Length)
            {
                break;
            }

            var finalPosition = reader.BaseStream.Position + chunkSize;
            onChunk(chunkName, chunkSize, reader);
            reader.BaseStream.Position = finalPosition;

            var padding = CalculatePadding(chunkSize);
            if (padding > 0)
            {
                reader.BaseStream.Position = Math.Min(reader.BaseStream.Length, reader.BaseStream.Position + padding);
            }
        }
    }

    private static BinderPropertyState ParseBinderProperty(byte[] data)
    {
        BinderPropertyState state = new();
        ParseNestedBlocks(data, (chunkName, chunkSize, chunkReader) =>
        {
            switch (chunkName)
            {
                case BinderBindPointChunk:
                    state.HasBindPoint = true;
                    state.BindPoint = (BinderBindPoint)ReadInt32(chunkReader, chunkSize);
                    break;
                case BinderBindTargetPointChunk:
                    state.HasBindTargetPoint = true;
                    state.BindTargetPoint = (BinderBindTargetPoint)ReadInt32(chunkReader, chunkSize);
                    break;
                case BinderBindPointIdChunk:
                    state.HasBindPointId = true;
                    state.BindPointId = ReadInt32(chunkReader, chunkSize);
                    break;
            }
        });

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
        reader.BaseStream.Position = itemStart + 8 + offset;

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
        return ObjectPathRules.NormalizeGamePath(Encoding.ASCII.GetString([.. bytes]));
    }

    private static string ReadAscii(BinaryReader reader, int size)
        => Encoding.ASCII.GetString(reader.ReadBytes(size));

    private static int ReadInt32(BinaryReader reader, int size)
        => size switch
        {
            >= 4 => reader.ReadInt32(),
            2 => reader.ReadInt16(),
            1 => reader.ReadByte(),
            _ => 0,
        };

    private static int CalculatePadding(int size)
        => size % 4 == 0 ? 0 : 4 - (size % 4);

    private static string ReadChunkName(uint rawValue)
    {
        Span<byte> bytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(bytes, rawValue);
        bytes.Reverse();
        return Encoding.ASCII.GetString(bytes);
    }

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

    private readonly record struct TmbReferenceAccumulator(
        RuntimeVfxEvidence Evidence,
        VfxTimelineContext ContextFlags);

    private readonly record struct TmbPendingVfxReference(
        string Path,
        RuntimeVfxEvidence Evidence,
        VfxTimelineContext ContextFlags);
}

