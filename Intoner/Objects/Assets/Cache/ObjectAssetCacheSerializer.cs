using Dalamud.Game;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Intoner.Objects.Assets.Cache;

internal static class ObjectAssetCacheSerializer
{
    internal const int FormatVersion = 2;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Encoding CacheEncoding = new UTF8Encoding(false, true);

    public static IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheSectionPayload> SerializeSections(ObjectAssetCacheSaveRequest request)
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
                ObjectAssetCacheSectionKind.RuntimeVfx => BuildRuntimeVfxSection(request.RuntimeVfxAssets),
                ObjectAssetCacheSectionKind.TimelineReferencedVfx => BuildTimelineReferencedVfxSection(request.TimelineReferencedVfxEntries),
                _ => throw new InvalidOperationException($"unsupported cache section kind {descriptor.Kind}"),
            };
        }

        return sections;
    }

    public static ObjectAssetCacheSerializedData BuildSerializedData(
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
            serializedSections.ToArray());
    }

    public static ObjectAssetCacheManifest BuildManifest(
        string? gameVersion,
        string? sqpackIndexFingerprint,
        ClientLanguage language,
        ObjectAssetCacheSerializedData serializedData,
        string cacheFileName)
        => new(
            ObjectAssetCacheService.SchemaVersion,
            FormatVersion,
            cacheFileName,
            string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion,
            string.IsNullOrWhiteSpace(sqpackIndexFingerprint) ? null : sqpackIndexFingerprint,
            language,
            DateTime.UtcNow,
            serializedData.PayloadLength,
            serializedData.Sections
                .Select(static section => new ObjectAssetCacheManifestSection(
                    section.Kind.ToManifestName(),
                    section.Count,
                    section.Offset,
                    section.Length,
                    section.Hash))
                .ToArray());

    public static ObjectAssetCacheSnapshot Deserialize(
        ObjectAssetCacheManifest manifest,
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ReadOnlyMemory<byte>> sectionPayloads)
        => Deserialize(manifest, sectionPayloads, out _);

    public static ObjectAssetCacheSnapshot Deserialize(
        ObjectAssetCacheManifest manifest,
        IReadOnlyDictionary<ObjectAssetCacheSectionKind, ReadOnlyMemory<byte>> sectionPayloads,
        out ObjectAssetCacheSectionSet loadedSections)
    {
        if (!manifest.TryBuildSectionMap(out IReadOnlyDictionary<ObjectAssetCacheSectionKind, ObjectAssetCacheManifestSection>? manifestSections, out string? error))
        {
            throw new InvalidDataException(error);
        }

        ObjectAssetCacheSectionSet loaded = ObjectAssetCacheSectionSet.None;
        ObjectAssetCacheSnapshot snapshot = new(
            manifest.GameVersion,
            Read<string>(ObjectAssetCacheSectionKind.StaticCollisionPaths),
            Read<BgObjectAsset>(ObjectAssetCacheSectionKind.StaticBgObjects),
            Read<ResolvedVfxPath>(ObjectAssetCacheSectionKind.StaticResolvedVfx),
            Read<ObjectAssetCacheBgModel>(ObjectAssetCacheSectionKind.BgModels),
            Read<ObjectAssetCacheRuntimeVfx>(ObjectAssetCacheSectionKind.RuntimeVfx),
            Read<ObjectAssetCacheTimelineReferencedVfxEntry>(ObjectAssetCacheSectionKind.TimelineReferencedVfx));
        loadedSections = loaded;
        return snapshot;

        T[] Read<T>(ObjectAssetCacheSectionKind kind)
        {
            if (!sectionPayloads.TryGetValue(kind, out ReadOnlyMemory<byte> payload)
             || !manifestSections.TryGetValue(kind, out ObjectAssetCacheManifestSection? section))
            {
                return [];
            }

            try
            {
                T[] items = (T[])ReadSectionPayload(kind, section.Count, payload, capture: true);
                loaded |= kind.ToSectionSet();
                return items;
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException or DecoderFallbackException)
            {
                return [];
            }
        }
    }

    public static bool TryValidateSectionPayload(ObjectAssetCacheSectionKind kind, int expectedCount, ReadOnlyMemory<byte> payload)
    {
        try
        {
            _ = ReadSectionPayload(kind, expectedCount, payload, capture: false);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static object ReadSectionPayload(ObjectAssetCacheSectionKind kind, int expectedCount, ReadOnlyMemory<byte> payload, bool capture)
        => kind switch
        {
            ObjectAssetCacheSectionKind.StaticCollisionPaths => ReadStringListSection(payload, expectedCount, capture),
            ObjectAssetCacheSectionKind.StaticBgObjects => ReadStaticBgObjectSection(payload, expectedCount, capture),
            ObjectAssetCacheSectionKind.StaticResolvedVfx => ReadStaticResolvedVfxSection(payload, expectedCount, capture),
            ObjectAssetCacheSectionKind.BgModels => ReadBgModelSection(payload, expectedCount, capture),
            ObjectAssetCacheSectionKind.RuntimeVfx => ReadRuntimeVfxSection(payload, expectedCount, capture),
            ObjectAssetCacheSectionKind.TimelineReferencedVfx => ReadTimelineReferencedVfxSection(payload, expectedCount, capture),
            _ => throw new InvalidDataException($"unsupported cache section kind {kind}"),
        };

    private static ObjectAssetCacheSectionPayload BuildStringListSection(ObjectAssetCacheSectionKind kind, IReadOnlyList<string> values)
    {
        string[] orderedValues = NormalizeStringList(values);
        StringTable stringTable = StringTable.Create(orderedValues);

        return WriteSection(kind, orderedValues, stringTable, static (writer, table, value) =>
        {
            writer.Write7BitEncodedInt(table.GetId(value));
        });
    }

    private static ObjectAssetCacheSectionPayload BuildStaticBgObjectSection(IReadOnlyList<BgObjectAsset> bgObjects)
    {
        List<BgObjectAsset> orderedObjects = bgObjects
            .OrderBy(static asset => asset.ModelPath, PathComparer)
            .ToList();
        StringTable stringTable = StringTable.Create(
            orderedObjects.Select(static asset => asset.ModelPath)
                .Concat(orderedObjects.Select(static asset => asset.Source))
                .Concat(orderedObjects.Select(static asset => asset.SourcePath))
                .Concat(orderedObjects.SelectMany(static asset => asset.TerritoryNames))
                .Concat(orderedObjects.SelectMany(static asset => asset.SearchTerms))
                .Concat(orderedObjects.SelectMany(static asset => asset.ColliderDefinitions.Select(static p => p.SourcePath)))
                .Concat(orderedObjects.SelectMany(static asset => asset.ColliderDefinitions.Select(static p => p.PcbPath))));

        return WriteSection(ObjectAssetCacheSectionKind.StaticBgObjects, orderedObjects, stringTable, static (writer, table, bgObject) =>
        {
            writer.Write7BitEncodedInt(table.GetId(bgObject.ModelPath));
            writer.Write7BitEncodedInt(table.GetId(bgObject.Source));
            writer.Write(bgObject.RowId);
            writer.Write7BitEncodedInt(table.GetId(bgObject.SourcePath));
            WriteUInt32List(writer, bgObject.TerritoryIds);
            WriteStringList(writer, table, bgObject.TerritoryNames);
            WriteStringList(writer, table, bgObject.SearchTerms);
            writer.Write(bgObject.ColliderDefinitionsComplete);
            writer.Write7BitEncodedInt(bgObject.ColliderDefinitions.Count);
            foreach (BgColliderDefinition definition in bgObject.ColliderDefinitions)
            {
                writer.Write7BitEncodedInt(table.GetId(definition.SourcePath));
                writer.Write((byte)definition.Kind);
                writer.Write7BitEncodedInt(table.GetId(definition.PcbPath));
                writer.Write(definition.MaterialId);
                writer.Write(definition.MaterialMask);
                writer.Write(definition.Shape is not null);
                if (definition.Shape is { } shape)
                {
                    WriteVector3(writer, shape.Translation);
                    WriteVector3(writer, shape.Rotation);
                    WriteVector3(writer, shape.Scale);
                    WriteVector3(writer, shape.BoundsMin);
                    WriteVector3(writer, shape.BoundsMax);
                    writer.Write(shape.MaterialId);
                    writer.Write(shape.MaterialMask);
                }
            }
        });
    }

    private static ObjectAssetCacheSectionPayload BuildStaticResolvedVfxSection(IReadOnlyList<ResolvedVfxPath> entries)
    {
        List<ResolvedVfxPath> orderedEntries = entries
            .OrderBy(static asset => asset.Path, PathComparer)
            .ToList();
        StringTable stringTable = StringTable.Create(
            orderedEntries.Select(static asset => asset.Path)
                .Concat(orderedEntries.SelectMany(static asset => asset.SearchTerms)));

        return WriteSection(ObjectAssetCacheSectionKind.StaticResolvedVfx, orderedEntries, stringTable, static (writer, table, entry) =>
        {
            writer.Write7BitEncodedInt(table.GetId(entry.Path));
            writer.Write((ushort)entry.Family);
            writer.Write((uint)entry.Evidence);
            writer.Write((ushort)entry.Sources);
            writer.Write((byte)entry.Contracts);
            WriteStringList(writer, table, entry.SearchTerms);
            WriteVfxAnalysis(writer, entry.Analysis);
            writer.Write((byte)entry.TimelineContext);
        });
    }

    private static ObjectAssetCacheSectionPayload BuildBgModelSection(IReadOnlyList<ObjectAssetCacheBgModel> bgModels)
    {
        List<ObjectAssetCacheBgModel> orderedModels = bgModels
            .OrderBy(static model => model.Path, PathComparer)
            .ToList();
        StringTable stringTable = StringTable.Create(
            orderedModels.Select(static model => model.Path)
                .Concat(orderedModels.SelectMany(static model => model.Sources))
                .Concat(orderedModels.SelectMany(static model => model.TerritoryNames))
                .Concat(orderedModels.SelectMany(static model => model.SearchTerms)));

        return WriteSection(ObjectAssetCacheSectionKind.BgModels, orderedModels, stringTable, static (writer, table, bgModel) =>
        {
            writer.Write7BitEncodedInt(table.GetId(bgModel.Path));
            WriteStringList(writer, table, bgModel.Sources);
            WriteUInt32List(writer, bgModel.TerritoryIds);
            WriteStringList(writer, table, bgModel.TerritoryNames);
            WriteStringList(writer, table, bgModel.SearchTerms);
        });
    }

    private static ObjectAssetCacheSectionPayload BuildRuntimeVfxSection(IReadOnlyList<ObjectAssetCacheRuntimeVfx> assets)
    {
        List<ObjectAssetCacheRuntimeVfx> orderedAssets = assets
            .OrderBy(static asset => asset.Path, PathComparer)
            .ToList();
        StringTable stringTable = StringTable.Create(orderedAssets.Select(static asset => asset.Path));

        return WriteSection(ObjectAssetCacheSectionKind.RuntimeVfx, orderedAssets, stringTable, static (writer, table, vfxAsset) =>
        {
            writer.Write7BitEncodedInt(table.GetId(vfxAsset.Path));
            writer.Write((uint)vfxAsset.Evidence);
            WriteVfxAnalysis(writer, vfxAsset.Analysis);
        });
    }

    private static ObjectAssetCacheSectionPayload BuildTimelineReferencedVfxSection(IReadOnlyList<ObjectAssetCacheTimelineReferencedVfxEntry> entries)
    {
        List<ObjectAssetCacheTimelineReferencedVfxEntry> orderedEntries = entries
            .OrderBy(static asset => asset.Path, PathComparer)
            .ToList();
        StringTable stringTable = StringTable.Create(orderedEntries.Select(static asset => asset.Path));

        return WriteSection(ObjectAssetCacheSectionKind.TimelineReferencedVfx, orderedEntries, stringTable, static (writer, table, entry) =>
        {
            writer.Write7BitEncodedInt(table.GetId(entry.Path));
            writer.Write((uint)entry.Evidence);
            writer.Write((byte)entry.Context);
        });
    }

    private static ObjectAssetCacheSectionPayload WriteSection<T>(
        ObjectAssetCacheSectionKind kind,
        IReadOnlyList<T> items,
        StringTable stringTable,
        Action<BinaryWriter, StringTable, T> writeItem)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        stringTable.Write(writer);
        writer.Write7BitEncodedInt(items.Count);
        foreach (T item in items)
        {
            writeItem(writer, stringTable, item);
        }

        writer.Flush();
        return new ObjectAssetCacheSectionPayload(kind, items.Count, stream.ToArray());
    }

    private static string[] ReadStringListSection(ReadOnlyMemory<byte> payload, int expectedCount, bool capture)
    {
        using MemoryStream stream = CreateReadOnlyStream(payload);
        using BinaryReader reader = new(stream, CacheEncoding, leaveOpen: true);
        string[] stringTable = ReadStringTable(reader);
        string[] values = ReadStringList(reader, stringTable, capture, expectedCount);
        EnsureSectionComplete(reader);
        return values;
    }

    private static BgObjectAsset[] ReadStaticBgObjectSection(ReadOnlyMemory<byte> payload, int expectedCount, bool capture)
    {
        HashSet<string> paths = new(PathComparer);
        return ReadSectionItems(
            payload, expectedCount, minimumItemBytes: 10, capture,
            (reader, stringTable, captureItem) =>
            {
                string modelPath = ReadUniqueTablePath(reader, stringTable, paths);
                string source = ReadTableString(reader, stringTable);
                uint rowId = reader.ReadUInt32();
                string sourcePath = ReadTableString(reader, stringTable);
                uint[] territoryIds = ReadUInt32List(reader, captureItem);
                string[] territoryNames = ReadStringList(reader, stringTable, captureItem);
                string[] searchTerms = ReadStringList(reader, stringTable, captureItem);
                bool definitionsComplete = ReadFlag(reader);
                int definitionCount = ReadCount(reader, minimumItemBytes: 20);
                List<BgColliderDefinition> definitions = captureItem ? new(definitionCount) : [];
                for (int index = 0; index < definitionCount; index++)
                {
                    string definitionSource = ReadTableString(reader, stringTable);
                    BgCollisionKind kind = (BgCollisionKind)reader.ReadByte();
                    string pcbPath = ReadTableString(reader, stringTable);
                    ulong materialId = reader.ReadUInt64();
                    ulong materialMask = reader.ReadUInt64();
                    BgAnalyticShape? shape = ReadFlag(reader) ? new(
                        ReadVector3(reader), ReadVector3(reader), ReadVector3(reader),
                        ReadVector3(reader), ReadVector3(reader), reader.ReadUInt32(), reader.ReadUInt32()) : null;
                    BgColliderDefinition definition = new(modelPath, definitionSource, kind, pcbPath, materialId, materialMask, shape);

                    if (!BgColliderDefinitionUtility.IsValid(definition, modelPath))
                    {
                        throw new InvalidDataException("invalid cached BG collider");
                    }

                    if (captureItem)
                    {
                        definitions.Add(definition);
                    }
                }

                return captureItem ? new BgObjectAsset(modelPath, source, rowId, sourcePath, territoryIds, territoryNames, searchTerms)
                {
                    ColliderDefinitions = definitions,
                    ColliderDefinitionsComplete = definitionsComplete,
                } : null!;
            });
    }

    private static ResolvedVfxPath[] ReadStaticResolvedVfxSection(ReadOnlyMemory<byte> payload, int expectedCount, bool capture)
    {
        HashSet<string> paths = new(PathComparer);
        return ReadSectionItems(
            payload, expectedCount, minimumItemBytes: 13, capture,
            (reader, stringTable, captureItem) =>
            {
                string path = ReadUniqueTablePath(reader, stringTable, paths);
                KnownVfxFamily family = (KnownVfxFamily)reader.ReadUInt16();
                RuntimeVfxEvidence evidence = (RuntimeVfxEvidence)reader.ReadUInt32();
                AssetPathSource sources = (AssetPathSource)reader.ReadUInt16();
                AssetPathContract contracts = (AssetPathContract)reader.ReadByte();
                string[] searchTerms = ReadStringList(reader, stringTable, captureItem);
                VfxAnalysis? analysis = ReadVfxAnalysis(reader, captureItem);
                VfxTimelineContext context = (VfxTimelineContext)reader.ReadByte();
                return captureItem ? new ResolvedVfxPath(path, family, evidence, sources, contracts, searchTerms, analysis, context) : null!;
            });
    }

    private static ObjectAssetCacheBgModel[] ReadBgModelSection(ReadOnlyMemory<byte> payload, int expectedCount, bool capture)
        => ReadSectionItems(
            payload, expectedCount, minimumItemBytes: 5, capture,
            static (reader, stringTable, captureItem) =>
            {
                string path = ReadTableString(reader, stringTable);
                string[] sources = ReadStringList(reader, stringTable, captureItem);
                uint[] territoryIds = ReadUInt32List(reader, captureItem);
                string[] territoryNames = ReadStringList(reader, stringTable, captureItem);
                string[] searchTerms = ReadStringList(reader, stringTable, captureItem);
                return captureItem ? new ObjectAssetCacheBgModel(path, sources, territoryIds, territoryNames, searchTerms) : null!;
            });

    private static ObjectAssetCacheRuntimeVfx[] ReadRuntimeVfxSection(ReadOnlyMemory<byte> payload, int expectedCount, bool capture)
        => ReadSectionItems(
            payload, expectedCount, minimumItemBytes: 6, capture,
            static (reader, stringTable, captureItem) =>
            {
                string path = ReadTableString(reader, stringTable);
                RuntimeVfxEvidence evidence = (RuntimeVfxEvidence)reader.ReadUInt32();
                VfxAnalysis? analysis = ReadVfxAnalysis(reader, captureItem);
                return captureItem ? new ObjectAssetCacheRuntimeVfx(path, evidence, analysis) : null!;
            });

    private static ObjectAssetCacheTimelineReferencedVfxEntry[] ReadTimelineReferencedVfxSection(ReadOnlyMemory<byte> payload, int expectedCount, bool capture)
        => ReadSectionItems(
            payload, expectedCount, minimumItemBytes: 6, capture,
            static (reader, stringTable, captureItem) =>
            {
                string path = ReadTableString(reader, stringTable);
                RuntimeVfxEvidence evidence = (RuntimeVfxEvidence)reader.ReadUInt32();
                VfxTimelineContext context = (VfxTimelineContext)reader.ReadByte();
                return captureItem ? new ObjectAssetCacheTimelineReferencedVfxEntry(path, evidence, context) : null!;
            });

    private static T[] ReadSectionItems<T>(
        ReadOnlyMemory<byte> payload,
        int expectedCount,
        int minimumItemBytes,
        bool capture,
        Func<BinaryReader, string[], bool, T> readItem)
    {
        using MemoryStream stream = CreateReadOnlyStream(payload);
        using BinaryReader reader = new(stream, CacheEncoding, leaveOpen: true);
        string[] stringTable = ReadStringTable(reader);
        int count = ReadCount(reader, minimumItemBytes);
        ValidateItemCount(count, expectedCount);
        T[] items = capture ? new T[count] : [];
        for (int i = 0; i < count; i++)
        {
            T item = readItem(reader, stringTable, capture);

            if (capture)
            {
                items[i] = item;
            }
        }

        EnsureSectionComplete(reader);
        return items;
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector3(BinaryReader reader)
        => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static bool ReadFlag(BinaryReader reader)
        => reader.ReadByte() switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("invalid cache boolean"),
        };

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

    private static string[] ReadStringList(BinaryReader reader, string[] stringTable, bool capture, int? expectedCount = null)
    {
        int count = ReadCount(reader, minimumItemBytes: 1);

        if (expectedCount is { } expected)
        {
            ValidateItemCount(count, expected);
        }

        string[] values = capture ? new string[count] : [];
        for (int i = 0; i < count; i++)
        {
            string value = ReadTableString(reader, stringTable);

            if (capture)
            {
                values[i] = value;
            }
        }

        return values;
    }

    private static string ReadTableString(BinaryReader reader, string[] stringTable)
    {
        int id = reader.Read7BitEncodedInt();

        if ((uint)id >= (uint)stringTable.Length)
        {
            throw new InvalidDataException("invalid cache string table id");
        }

        return stringTable[id];
    }

    private static string ReadUniqueTablePath(BinaryReader reader, string[] stringTable, HashSet<string> paths)
    {
        string path = ObjectResolvedPath.FromGamePath(ReadTableString(reader, stringTable)).Path;

        if (path.Length == 0)
        {
            throw new InvalidDataException("invalid cache asset path");
        }

        if (!paths.Add(path))
        {
            throw new InvalidDataException("duplicate cache asset path");
        }

        return path;
    }

    private static int ReadCount(BinaryReader reader, int minimumItemBytes)
    {
        int count = ReadNonNegativeInt(reader);

        if (count > (reader.BaseStream.Length - reader.BaseStream.Position) / minimumItemBytes)
        {
            throw new InvalidDataException("cache count exceeds the remaining section data");
        }

        return count;
    }

    private static int ReadNonNegativeInt(BinaryReader reader)
    {
        int value = reader.Read7BitEncodedInt();

        if (value < 0)
        {
            throw new InvalidDataException("negative cache count");
        }

        return value;
    }

    private static void ValidateItemCount(int count, int expectedCount)
    {
        if (count != expectedCount)
        {
            throw new InvalidDataException("cache item count does not match its manifest");
        }
    }

    private static void EnsureSectionComplete(BinaryReader reader)
    {
        if (reader.BaseStream.Position != reader.BaseStream.Length)
        {
            throw new InvalidDataException("unexpected data after the cache section");
        }
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

    private static VfxAnalysis? ReadVfxAnalysis(BinaryReader reader, bool capture)
    {
        byte hasAnalysis = reader.ReadByte();

        if (hasAnalysis > 1)
        {
            throw new InvalidDataException("invalid cache analysis flag");
        }

        if (hasAnalysis == 0)
        {
            return null;
        }

        int schedulerCount = ReadNonNegativeInt(reader);
        int schedulerItemCount = ReadNonNegativeInt(reader);
        int schedulerTriggerCount = ReadNonNegativeInt(reader);
        int timelineCount = ReadNonNegativeInt(reader);
        int emitterCount = ReadNonNegativeInt(reader);
        int particleCount = ReadNonNegativeInt(reader);
        int modelCount = ReadNonNegativeInt(reader);
        VfxDrawLayer drawLayer = (VfxDrawLayer)reader.ReadSByte();
        int binderCount = ReadNonNegativeInt(reader);
        int timelineBinderCount = ReadNonNegativeInt(reader);
        VfxBinderTypes binderTypes = (VfxBinderTypes)reader.ReadByte();
        VfxBinderProperties binderPropertyFlags = (VfxBinderProperties)reader.ReadUInt16();
        VfxParticleTypes particleTypes = (VfxParticleTypes)reader.ReadUInt16();
        VfxAnalysisFeatures featureFlags = (VfxAnalysisFeatures)reader.ReadUInt16();
        VfxLoopBehavior loopBehavior = (VfxLoopBehavior)reader.ReadByte();
        VfxLoopSource loopSources = (VfxLoopSource)reader.ReadByte();

        return capture ? new VfxAnalysis(
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
            new VfxLoopFacts(loopBehavior, loopSources)) : null;
    }

    private static uint[] ReadUInt32List(BinaryReader reader, bool capture)
    {
        int count = ReadCount(reader, minimumItemBytes: sizeof(uint));
        uint[] values = capture ? new uint[count] : [];
        for (int i = 0; i < count; i++)
        {
            uint value = reader.ReadUInt32();

            if (capture)
            {
                values[i] = value;
            }
        }

        return values;
    }

    private static string[] ReadStringTable(BinaryReader reader)
    {
        int count = ReadCount(reader, minimumItemBytes: 1);
        string[] strings = new string[count];
        for (int i = 0; i < count; i++)
        {
            long stringOffset = reader.BaseStream.Position;
            _ = ReadCount(reader, minimumItemBytes: 1);
            reader.BaseStream.Position = stringOffset;
            strings[i] = reader.ReadString();
        }

        return strings;
    }

    private static string[] NormalizeStringList(IEnumerable<string> values)
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
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        }

        public string[] Strings { get; }

        public static StringTable Create(IEnumerable<string> values)
            => new(values
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static value => value, StringComparer.Ordinal)
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
