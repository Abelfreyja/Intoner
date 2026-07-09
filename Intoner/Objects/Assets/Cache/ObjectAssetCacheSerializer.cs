using System.Runtime.InteropServices;
using System.Text;

namespace Intoner.Objects.Assets.Cache;

internal sealed class ObjectAssetCacheSerializer
{
    internal const int FormatVersion = 1;
    internal const string CacheFileName = "assets.cache";
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSectionPayload> SerializeSections(ObjectAssetCacheSaveRequest request)
    {
        Dictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSectionPayload> sections = [];
        foreach (ObjectAssetCacheSectionDescriptor descriptor in request.Sections.EnumerateDescriptors())
        {
            sections[descriptor.Kind] = descriptor.Kind switch
            {
                ObjectAssetCacheSectionKind.StaticCollisionPaths => BuildStringListSection(
                    ObjectAssetCacheSectionKind.StaticCollisionPaths,
                    request.StaticCollisionPaths),
                ObjectAssetCacheSectionKind.StaticBgObjects => BuildStaticBgObjectSection(request.StaticGameDataBgObjects),
                ObjectAssetCacheSectionKind.StaticResolvedVfx => BuildStaticResolvedVfxSection(request.StaticResolvedVfxEntries),
                ObjectAssetCacheSectionKind.BgModels => BuildBgModelSection(request.BgModels),
                ObjectAssetCacheSectionKind.StandaloneVfx => BuildStandaloneVfxSection(request.StandaloneVfxAssets),
                ObjectAssetCacheSectionKind.TimelineReferencedVfx => BuildTimelineReferencedVfxSection(request.TimelineReferencedVfxEntries),
                _ => throw new InvalidOperationException($"unsupported cache section kind {descriptor.Kind}"),
            };
        }

        return sections;
    }

    public ObjectAssetCacheSerializedData BuildSerializedData(
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSectionPayload> sections)
    {
        using MemoryStream stream = new();
        List<SerializedSection> serializedSections = new(sections.Count);
        foreach (ObjectAssetCacheSectionDescriptor descriptor in ObjectAssetCacheSectionSetExtensions.EnumerateDescriptors())
        {
            if (!sections.TryGetValue(descriptor.Kind, out ObjectAssetCacheSectionPayload? section))
            {
                continue;
            }

            long payloadOffset = stream.Position;
            stream.Write(section.Payload.Span);

            serializedSections.Add(new SerializedSection(
                section.Kind,
                section.Count,
                payloadOffset,
                section.Payload.Length,
                ObjectAssetHashUtility.ComputeSha256Hex(section.Payload.Span)));
        }

        byte[] payload = stream.ToArray();
        return new ObjectAssetCacheSerializedData(
            payload,
            payload.LongLength,
            ObjectAssetHashUtility.ComputeSha256Hex(payload),
            serializedSections.ToArray());
    }

    public ObjectAssetCacheManifest BuildManifest(
        string? gameVersion,
        string? sqpackIndexFingerprint,
        ObjectAssetCacheSerializedData serializedData)
        => new(
            ObjectAssetCacheService.SchemaVersion,
            FormatVersion,
            CacheFileName,
            string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion,
            string.IsNullOrWhiteSpace(sqpackIndexFingerprint) ? null : sqpackIndexFingerprint,
            DateTime.UtcNow,
            serializedData.PayloadLength,
            serializedData.PayloadHash,
            serializedData.Sections
                .Select(static section => new ObjectAssetCacheManifestSection(
                    section.Kind.ToManifestName(),
                    section.Count,
                    section.Offset,
                    section.Length,
                    section.Hash))
                .ToArray());

    public ObjectAssetCacheSnapshot Deserialize(
        ObjectAssetCacheManifest manifest,
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ReadOnlyMemory<byte>> sectionPayloads)
    {
        IReadOnlyList<string> staticCollisionPaths =
            ReadStringListSection(sectionPayloads, ObjectAssetCacheSectionKind.StaticCollisionPaths);
        IReadOnlyList<ObjectAssetCacheStaticBgObject> staticBgObjects =
            ReadStaticBgObjectSection(sectionPayloads, ObjectAssetCacheSectionKind.StaticBgObjects);
        IReadOnlyList<ObjectAssetCacheResolvedVfxEntry> staticResolvedVfxEntries =
            ReadStaticResolvedVfxSection(sectionPayloads, ObjectAssetCacheSectionKind.StaticResolvedVfx);
        IReadOnlyList<ObjectAssetCacheBgModel> bgModels =
            ReadBgModelSection(sectionPayloads, ObjectAssetCacheSectionKind.BgModels);
        IReadOnlyList<ObjectAssetCacheStandaloneVfx> standaloneVfxAssets =
            ReadStandaloneVfxSection(sectionPayloads, ObjectAssetCacheSectionKind.StandaloneVfx);
        IReadOnlyList<ObjectAssetCacheTimelineReferencedVfxEntry> timelineReferencedVfxEntries =
            ReadTimelineReferencedVfxSection(sectionPayloads, ObjectAssetCacheSectionKind.TimelineReferencedVfx);

        return new ObjectAssetCacheSnapshot(
            manifest.GameVersion,
            staticCollisionPaths,
            staticBgObjects,
            staticResolvedVfxEntries,
            bgModels,
            standaloneVfxAssets,
            timelineReferencedVfxEntries);
    }

    private static ObjectAssetCacheSectionPayload BuildStringListSection(ObjectAssetCacheSectionKind kind, IReadOnlyList<string> values)
    {
        string[] orderedValues = NormalizeStringList(values);
        StringTable stringTable = StringTable.Create(orderedValues);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        stringTable.Write(writer);
        writer.Write7BitEncodedInt(orderedValues.Length);
        foreach (string value in orderedValues)
        {
            writer.Write7BitEncodedInt(stringTable.GetId(value));
        }

        writer.Flush();
        return new ObjectAssetCacheSectionPayload(kind, orderedValues.Length, stream.ToArray());
    }

    private static ObjectAssetCacheSectionPayload BuildStaticBgObjectSection(IReadOnlyList<ObjectAssetCacheStaticBgObject> bgObjects)
    {
        List<ObjectAssetCacheStaticBgObject> orderedObjects = bgObjects
            .OrderBy(static asset => asset.ModelPath, PathComparer)
            .ToList();
        StringTable stringTable = StringTable.Create(
            orderedObjects.Select(static asset => asset.ModelPath)
                .Concat(orderedObjects.Select(static asset => asset.Source))
                .Concat(orderedObjects.Select(static asset => asset.SourcePath))
                .Concat(orderedObjects.SelectMany(static asset => asset.TerritoryNames))
                .Concat(orderedObjects.SelectMany(static asset => asset.SearchTerms)));

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        stringTable.Write(writer);
        writer.Write7BitEncodedInt(orderedObjects.Count);
        foreach (ObjectAssetCacheStaticBgObject bgObject in orderedObjects)
        {
            writer.Write7BitEncodedInt(stringTable.GetId(bgObject.ModelPath));
            writer.Write7BitEncodedInt(stringTable.GetId(bgObject.Source));
            writer.Write(bgObject.RowId);
            writer.Write7BitEncodedInt(stringTable.GetId(bgObject.SourcePath));
            WriteUInt32List(writer, bgObject.TerritoryIds);
            WriteStringList(writer, stringTable, bgObject.TerritoryNames);
            WriteStringList(writer, stringTable, bgObject.SearchTerms);
        }

        writer.Flush();
        return new ObjectAssetCacheSectionPayload(ObjectAssetCacheSectionKind.StaticBgObjects, orderedObjects.Count, stream.ToArray());
    }

    private static ObjectAssetCacheSectionPayload BuildStaticResolvedVfxSection(IReadOnlyList<ObjectAssetCacheResolvedVfxEntry> entries)
    {
        List<ObjectAssetCacheResolvedVfxEntry> orderedEntries = entries
            .OrderBy(static asset => asset.Path, PathComparer)
            .ToList();
        StringTable stringTable = StringTable.Create(
            orderedEntries.Select(static asset => asset.Path)
                .Concat(orderedEntries.SelectMany(static asset => asset.SearchTerms)));

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        stringTable.Write(writer);
        writer.Write7BitEncodedInt(orderedEntries.Count);
        foreach (ObjectAssetCacheResolvedVfxEntry entry in orderedEntries)
        {
            writer.Write7BitEncodedInt(stringTable.GetId(entry.Path));
            writer.Write((ushort)entry.Family);
            writer.Write((uint)entry.Evidence);
            writer.Write((ushort)entry.Sources);
            writer.Write((byte)entry.Contracts);
            WriteStringList(writer, stringTable, entry.SearchTerms);
            WriteVfxAnalysis(writer, entry.Analysis);
        }

        writer.Flush();
        return new ObjectAssetCacheSectionPayload(ObjectAssetCacheSectionKind.StaticResolvedVfx, orderedEntries.Count, stream.ToArray());
    }

    private static ObjectAssetCacheSectionPayload BuildBgModelSection(IReadOnlyList<ObjectAssetCacheBgModel> bgModels)
    {
        List<ObjectAssetCacheBgModel> orderedModels = bgModels
            .OrderBy(static model => model.Path, PathComparer)
            .ToList();
        StringTable stringTable = StringTable.Create(
            orderedModels.Select(static model => model.Path)
                .Concat(orderedModels.SelectMany(static model => model.Sources))
                .Concat(orderedModels.SelectMany(static model => model.TerritoryNames)));

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        stringTable.Write(writer);
        writer.Write7BitEncodedInt(orderedModels.Count);
        foreach (ObjectAssetCacheBgModel bgModel in orderedModels)
        {
            writer.Write7BitEncodedInt(stringTable.GetId(bgModel.Path));
            WriteStringList(writer, stringTable, bgModel.Sources);
            WriteUInt32List(writer, bgModel.TerritoryIds);
            WriteStringList(writer, stringTable, bgModel.TerritoryNames);
        }

        writer.Flush();
        return new ObjectAssetCacheSectionPayload(ObjectAssetCacheSectionKind.BgModels, orderedModels.Count, stream.ToArray());
    }

    private static ObjectAssetCacheSectionPayload BuildStandaloneVfxSection(IReadOnlyList<ObjectAssetCacheStandaloneVfx> assets)
    {
        List<ObjectAssetCacheStandaloneVfx> orderedAssets = assets
            .OrderBy(static asset => asset.Path, PathComparer)
            .ToList();
        StringTable stringTable = StringTable.Create(orderedAssets.Select(static asset => asset.Path));

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        stringTable.Write(writer);
        writer.Write7BitEncodedInt(orderedAssets.Count);
        foreach (ObjectAssetCacheStandaloneVfx vfxAsset in orderedAssets)
        {
            writer.Write7BitEncodedInt(stringTable.GetId(vfxAsset.Path));
            writer.Write((uint)vfxAsset.Evidence);
            WriteVfxAnalysis(writer, vfxAsset.Analysis);
        }

        writer.Flush();
        return new ObjectAssetCacheSectionPayload(ObjectAssetCacheSectionKind.StandaloneVfx, orderedAssets.Count, stream.ToArray());
    }

    private static ObjectAssetCacheSectionPayload BuildTimelineReferencedVfxSection(IReadOnlyList<ObjectAssetCacheTimelineReferencedVfxEntry> entries)
    {
        List<ObjectAssetCacheTimelineReferencedVfxEntry> orderedEntries = entries
            .OrderBy(static asset => asset.Path, PathComparer)
            .ToList();
        StringTable stringTable = StringTable.Create(orderedEntries.Select(static asset => asset.Path));

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        stringTable.Write(writer);
        writer.Write7BitEncodedInt(orderedEntries.Count);
        foreach (ObjectAssetCacheTimelineReferencedVfxEntry entry in orderedEntries)
        {
            writer.Write7BitEncodedInt(stringTable.GetId(entry.Path));
            writer.Write((uint)entry.Evidence);
            writer.Write((byte)entry.Context);
        }

        writer.Flush();
        return new ObjectAssetCacheSectionPayload(ObjectAssetCacheSectionKind.TimelineReferencedVfx, orderedEntries.Count, stream.ToArray());
    }

    private static IReadOnlyList<string> ReadStringListSection(
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ReadOnlyMemory<byte>> sectionPayloads,
        ObjectAssetCacheSectionKind kind)
    {
        if (!sectionPayloads.TryGetValue(kind, out ReadOnlyMemory<byte> payload))
        {
            return [];
        }

        using MemoryStream stream = CreateReadOnlyStream(payload);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        string[] stringTable = ReadStringTable(reader);
        return ReadStringList(reader, stringTable);
    }

    private static IReadOnlyList<ObjectAssetCacheStaticBgObject> ReadStaticBgObjectSection(
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ReadOnlyMemory<byte>> sectionPayloads,
        ObjectAssetCacheSectionKind kind)
        => ReadSectionItems(
            sectionPayloads,
            kind,
            static (reader, stringTable) =>
            {
                string modelPath = stringTable[reader.Read7BitEncodedInt()];
                string source = stringTable[reader.Read7BitEncodedInt()];
                uint rowId = reader.ReadUInt32();
                string sourcePath = stringTable[reader.Read7BitEncodedInt()];
                uint[] territoryIds = ReadUInt32List(reader);
                string[] territoryNames = ReadStringList(reader, stringTable);
                string[] searchTerms = ReadStringList(reader, stringTable);
                return new ObjectAssetCacheStaticBgObject(modelPath, source, rowId, sourcePath, territoryIds, territoryNames, searchTerms);
            });

    private static IReadOnlyList<ObjectAssetCacheResolvedVfxEntry> ReadStaticResolvedVfxSection(
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ReadOnlyMemory<byte>> sectionPayloads,
        ObjectAssetCacheSectionKind kind)
        => ReadSectionItems(
            sectionPayloads,
            kind,
            static (reader, stringTable) =>
            {
                string path = stringTable[reader.Read7BitEncodedInt()];
                KnownVfxFamily family = (KnownVfxFamily)reader.ReadUInt16();
                RuntimeVfxEvidence evidence = (RuntimeVfxEvidence)reader.ReadUInt32();
                AssetPathSource sources = (AssetPathSource)reader.ReadUInt16();
                AssetPathContract contracts = (AssetPathContract)reader.ReadByte();
                string[] searchTerms = ReadStringList(reader, stringTable);
                VfxAnalysis? analysis = ReadVfxAnalysis(reader);
                return new ObjectAssetCacheResolvedVfxEntry(path, family, evidence, sources, contracts, searchTerms, analysis);
            });

    private static IReadOnlyList<ObjectAssetCacheBgModel> ReadBgModelSection(
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ReadOnlyMemory<byte>> sectionPayloads,
        ObjectAssetCacheSectionKind kind)
        => ReadSectionItems(
            sectionPayloads,
            kind,
            static (reader, stringTable) =>
            {
                string path = stringTable[reader.Read7BitEncodedInt()];
                string[] sources = ReadStringList(reader, stringTable);
                uint[] territoryIds = ReadUInt32List(reader);
                string[] territoryNames = ReadStringList(reader, stringTable);
                return new ObjectAssetCacheBgModel(path, sources, territoryIds, territoryNames);
            });

    private static IReadOnlyList<ObjectAssetCacheStandaloneVfx> ReadStandaloneVfxSection(
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ReadOnlyMemory<byte>> sectionPayloads,
        ObjectAssetCacheSectionKind kind)
        => ReadSectionItems(
            sectionPayloads,
            kind,
            static (reader, stringTable) =>
            {
                string path = stringTable[reader.Read7BitEncodedInt()];
                RuntimeVfxEvidence evidence = (RuntimeVfxEvidence)reader.ReadUInt32();
                VfxAnalysis? analysis = ReadVfxAnalysis(reader);
                return new ObjectAssetCacheStandaloneVfx(path, evidence, analysis);
            });

    private static IReadOnlyList<ObjectAssetCacheTimelineReferencedVfxEntry> ReadTimelineReferencedVfxSection(
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ReadOnlyMemory<byte>> sectionPayloads,
        ObjectAssetCacheSectionKind kind)
        => ReadSectionItems(
            sectionPayloads,
            kind,
            static (reader, stringTable) =>
            {
                string path = stringTable[reader.Read7BitEncodedInt()];
                RuntimeVfxEvidence evidence = (RuntimeVfxEvidence)reader.ReadUInt32();
                VfxTimelineContext context = (VfxTimelineContext)reader.ReadByte();
                return new ObjectAssetCacheTimelineReferencedVfxEntry(path, evidence, context);
            });

    private static T[] ReadSectionItems<T>(
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ReadOnlyMemory<byte>> sectionPayloads,
        ObjectAssetCacheSectionKind kind,
        Func<BinaryReader, string[], T> readItem)
    {
        if (!sectionPayloads.TryGetValue(kind, out ReadOnlyMemory<byte> payload))
        {
            return [];
        }

        using MemoryStream stream = CreateReadOnlyStream(payload);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        string[] stringTable = ReadStringTable(reader);
        int count = reader.Read7BitEncodedInt();
        T[] items = new T[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = readItem(reader, stringTable);
        }

        return items;
    }

    private static void WriteStringList(BinaryWriter writer, StringTable stringTable, IReadOnlyList<string> values)
    {
        string[] orderedValues = NormalizeStringList(values);
        writer.Write7BitEncodedInt(orderedValues.Length);
        foreach (string value in orderedValues)
        {
            writer.Write7BitEncodedInt(stringTable.GetId(value));
        }
    }

    private static void WriteUInt32List(BinaryWriter writer, IReadOnlyList<uint> values)
    {
        uint[] orderedValues = NormalizeUInt32List(values);
        writer.Write7BitEncodedInt(orderedValues.Length);
        foreach (uint value in orderedValues)
        {
            writer.Write(value);
        }
    }

    private static string[] ReadStringList(BinaryReader reader, string[] stringTable)
    {
        int count = reader.Read7BitEncodedInt();
        string[] values = new string[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = stringTable[reader.Read7BitEncodedInt()];
        }

        return values;
    }

    private static void WriteVfxAnalysis(BinaryWriter writer, VfxAnalysis? analysis)
    {
        writer.Write(analysis is not null);
        if (analysis is null)
        {
            return;
        }

        writer.Write7BitEncodedInt(analysis.SchedulerCount);
        writer.Write7BitEncodedInt(analysis.SchedulerItemCount);
        writer.Write7BitEncodedInt(analysis.SchedulerTriggerCount);
        writer.Write7BitEncodedInt(analysis.TimelineCount);
        writer.Write7BitEncodedInt(analysis.EmitterCount);
        writer.Write7BitEncodedInt(analysis.ParticleCount);
        writer.Write7BitEncodedInt(analysis.ModelCount);
        writer.Write((sbyte)analysis.DrawLayer);
        writer.Write7BitEncodedInt(analysis.BinderCount);
        writer.Write7BitEncodedInt(analysis.BinderFacts.TimelineCount);
        writer.Write((byte)analysis.BinderFacts.Types);
        writer.Write((ushort)analysis.BinderFacts.PropertyFlags);
        writer.Write((ushort)analysis.ParticleTypes);
        writer.Write((ushort)analysis.FeatureFlags);
        writer.Write((byte)analysis.LoopFacts.Behavior);
        writer.Write((byte)analysis.LoopFacts.Sources);
    }

    private static VfxAnalysis? ReadVfxAnalysis(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
        {
            return null;
        }

        int schedulerCount = reader.Read7BitEncodedInt();
        int schedulerItemCount = reader.Read7BitEncodedInt();
        int schedulerTriggerCount = reader.Read7BitEncodedInt();
        int timelineCount = reader.Read7BitEncodedInt();
        int emitterCount = reader.Read7BitEncodedInt();
        int particleCount = reader.Read7BitEncodedInt();
        int modelCount = reader.Read7BitEncodedInt();
        VfxDrawLayer drawLayer = (VfxDrawLayer)reader.ReadSByte();
        int binderCount = reader.Read7BitEncodedInt();
        int timelineBinderCount = reader.Read7BitEncodedInt();
        VfxBinderTypes binderTypes = (VfxBinderTypes)reader.ReadByte();
        VfxBinderProperties binderPropertyFlags = (VfxBinderProperties)reader.ReadUInt16();
        VfxParticleTypes particleTypes = (VfxParticleTypes)reader.ReadUInt16();
        VfxAnalysisFeatures featureFlags = (VfxAnalysisFeatures)reader.ReadUInt16();
        VfxLoopBehavior loopBehavior = (VfxLoopBehavior)reader.ReadByte();
        VfxLoopSource loopSources = (VfxLoopSource)reader.ReadByte();

        return new VfxAnalysis(
            schedulerCount,
            schedulerItemCount,
            schedulerTriggerCount,
            timelineCount,
            emitterCount,
            particleCount,
            modelCount,
            drawLayer,
            new VfxBinderFacts(
                binderCount,
                timelineBinderCount,
                binderTypes,
                binderPropertyFlags),
            particleTypes,
            featureFlags,
            new VfxLoopFacts(loopBehavior, loopSources));
    }

    private static uint[] ReadUInt32List(BinaryReader reader)
    {
        int count = reader.Read7BitEncodedInt();
        uint[] values = new uint[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = reader.ReadUInt32();
        }

        return values;
    }

    private static string[] ReadStringTable(BinaryReader reader)
    {
        int count = reader.Read7BitEncodedInt();
        string[] strings = new string[count];
        for (int i = 0; i < count; i++)
        {
            strings[i] = reader.ReadString();
        }

        return strings;
    }

    private static string[] NormalizeStringList(IReadOnlyList<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static uint[] NormalizeUInt32List(IReadOnlyList<uint> values)
        => values
            .Where(static value => value != 0)
            .Distinct()
            .OrderBy(static value => value)
            .ToArray();

    internal sealed record ObjectAssetCacheSerializedData(
        byte[] Payload,
        long PayloadLength,
        string PayloadHash,
        IReadOnlyList<SerializedSection> Sections);

    internal sealed record ObjectAssetCacheSectionPayload(
        ObjectAssetCacheSectionKind Kind,
        int Count,
        ReadOnlyMemory<byte> Payload);

    internal sealed record SerializedSection(
        ObjectAssetCacheSectionKind Kind,
        int Count,
        long Offset,
        int Length,
        string Hash);

    private static MemoryStream CreateReadOnlyStream(ReadOnlyMemory<byte> payload)
    {
        if (MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment)
         && segment.Array is not null)
        {
            return new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false, publiclyVisible: true);
        }

        return new MemoryStream(payload.ToArray(), writable: false);
    }

    private sealed class StringTable
    {
        private readonly Dictionary<string, int> _ids;

        private StringTable(string[] strings)
        {
            Strings = strings;
            _ids = strings
                .Select(static (value, index) => new KeyValuePair<string, int>(value, index))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        public string[] Strings { get; }

        public static StringTable Create(IEnumerable<string> values)
            => new(
                values
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray());

        public int GetId(string value)
            => _ids[value];

        public void Write(BinaryWriter writer)
        {
            writer.Write7BitEncodedInt(Strings.Length);
            foreach (string value in Strings)
            {
                writer.Write(value);
            }
        }
    }
}

