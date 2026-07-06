using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Intoner.Objects.Utils;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Assets;

internal static class TerritoryLayoutAssetResolver
{
    internal sealed class AssetInfo(
        IReadOnlyList<string> bgObjectModelPaths,
        IReadOnlyList<string> referencedLayoutPaths,
        IReadOnlyList<string> referencedSharedGroupPaths,
        IReadOnlyList<ResolvedVfxPath> resolvedVfxPaths)
    {
        public IReadOnlyList<string> BgObjectModelPaths { get; } = bgObjectModelPaths;
        public IReadOnlyList<string> ReferencedLayoutPaths { get; } = referencedLayoutPaths;
        public IReadOnlyList<string> ReferencedSharedGroupPaths { get; } = referencedSharedGroupPaths;
        public IReadOnlyList<ResolvedVfxPath> ResolvedVfxPaths { get; } = resolvedVfxPaths;
    }

    private static readonly uint LvbSectionMagic = MakeSectionMagic('S', 'C', 'N', '1');
    private static readonly uint LgbSectionMagic = MakeSectionMagic('L', 'G', 'P', '1');
    private static readonly uint TmlbSectionMagic = MakeSectionMagic('T', 'M', 'L', 'B');

    [StructLayout(LayoutKind.Explicit, Size = 0x5C)]
    private unsafe struct FileLayerGroupInstanceVfx
    {
        [FieldOffset(0x00)] public FileLayerGroupInstance Base;
        [FieldOffset(0x30)] public int OffsetPath;

        public byte* Path => OffsetPath > 0 ? (byte*)Unsafe.AsPointer(ref this) + OffsetPath : null;
    }

    public static AssetInfo AnalyzeTerritoryLayout(IObjectAssetGameData gameData, string territoryLayoutPath)
    {
        string normalizedPath = ObjectPathRules.NormalizeGamePath(territoryLayoutPath);
        if (string.IsNullOrWhiteSpace(normalizedPath)
         || !normalizedPath.EndsWith(".lvb", StringComparison.OrdinalIgnoreCase)
         || !gameData.FileExists(normalizedPath))
        {
            return new AssetInfo([], [], [], []);
        }

        byte[]? fileData = gameData.GetFile(normalizedPath)?.Data;
        if (fileData is null || fileData.Length < Unsafe.SizeOf<FileHeader>())
        {
            return new AssetInfo([], [], [], []);
        }

        return new TerritoryLayoutAssetCollector(gameData).Collect(normalizedPath, fileData);
    }

    private static uint MakeSectionMagic(char a, char b, char c, char d)
        => (uint)(byte)a
         | ((uint)(byte)b << 8)
         | ((uint)(byte)c << 16)
         | ((uint)(byte)d << 24);

    private sealed unsafe class TerritoryLayoutAssetCollector(IObjectAssetGameData gameData)
    {
        private readonly IObjectAssetGameData _gameData = gameData;
        private readonly VfxTimelineReferenceCache _timelineReferenceCache = new(gameData);
        private readonly List<string> _bgObjectModelPaths = [];
        private readonly List<string> _referencedLayoutPaths = [];
        private readonly List<string> _referencedSharedGroupPaths = [];
        private readonly Dictionary<string, ResolvedVfxPathAccumulator> _resolvedVfxPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenBgObjectModels = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenLayoutPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenSharedGroupPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenTimelinePaths = new(StringComparer.OrdinalIgnoreCase);

        public AssetInfo Collect(string territoryLayoutPath, byte[] fileData)
        {
            CollectLvb(territoryLayoutPath, fileData);
            return new AssetInfo(
                _bgObjectModelPaths,
                _referencedLayoutPaths,
                _referencedSharedGroupPaths,
                ResolvedVfxPathAccumulator.BuildSnapshot(_resolvedVfxPaths.Values));
        }

        private void CollectLvb(string territoryLayoutPath, byte[] fileData)
        {
            if (!_seenLayoutPaths.Add(territoryLayoutPath))
            {
                return;
            }

            _referencedLayoutPaths.Add(territoryLayoutPath);

            fixed (byte* dataPointer = fileData)
            {
                if (fileData.Length < Unsafe.SizeOf<FileHeader>())
                {
                    return;
                }

                FileHeader* fileHeader = (FileHeader*)dataPointer;
                foreach (FileSectionHeader* section in fileHeader->Sections)
                {
                    if (section->Magic != LvbSectionMagic)
                    {
                        continue;
                    }

                    FileSceneHeader* sceneHeader = section->Data<FileSceneHeader>();
                    CollectEmbeddedLayerGroups(sceneHeader);
                    CollectReferencedLayerGroups(sceneHeader);
                    CollectSceneTimelines(territoryLayoutPath, sceneHeader);
                }
            }
        }

        private void CollectEmbeddedLayerGroups(FileSceneHeader* sceneHeader)
        {
            Span<FileLayerGroupHeader> embeddedLayerGroups = sceneHeader->EmbeddedLayerGroups;
            for (int i = 0; i < embeddedLayerGroups.Length; i++)
            {
                FileLayerGroupHeader* layerGroup = (FileLayerGroupHeader*)Unsafe.AsPointer(ref embeddedLayerGroups[i]);
                CollectLayerGroup(layerGroup);
            }
        }

        private void CollectReferencedLayerGroups(FileSceneHeader* sceneHeader)
        {
            Span<int> layerGroupResourceOffsets = sceneHeader->LayerGroupResourceOffsets;
            for (int i = 0; i < layerGroupResourceOffsets.Length; i++)
            {
                string referencedLayoutPath = ObjectPathRules.NormalizeGamePath(
                    ReadCString(sceneHeader->LayerGroupResource(layerGroupResourceOffsets[i])));
                if (string.IsNullOrWhiteSpace(referencedLayoutPath)
                 || !referencedLayoutPath.EndsWith(".lgb", StringComparison.OrdinalIgnoreCase)
                 || !_gameData.FileExists(referencedLayoutPath))
                {
                    continue;
                }

                CollectLgb(referencedLayoutPath);
            }
        }

        private void CollectLgb(string layoutGroupPath)
        {
            if (!_seenLayoutPaths.Add(layoutGroupPath))
            {
                return;
            }

            _referencedLayoutPaths.Add(layoutGroupPath);

            byte[]? fileData = _gameData.GetFile(layoutGroupPath)?.Data;
            if (fileData is null || fileData.Length < Unsafe.SizeOf<FileHeader>())
            {
                return;
            }

            fixed (byte* dataPointer = fileData)
            {
                FileHeader* fileHeader = (FileHeader*)dataPointer;
                foreach (FileSectionHeader* section in fileHeader->Sections)
                {
                    if (section->Magic != LgbSectionMagic)
                    {
                        continue;
                    }

                    CollectLayerGroup(section->Data<FileLayerGroupHeader>());
                }
            }
        }

        private void CollectLayerGroup(FileLayerGroupHeader* layerGroup)
        {
            Span<int> layerOffsets = layerGroup->LayerOffsets;
            for (int i = 0; i < layerOffsets.Length; i++)
            {
                CollectLayer(layerGroup->Layer(layerOffsets[i]));
            }
        }

        private void CollectLayer(FileLayerGroupLayer* layer)
        {
            Span<int> instanceOffsets = layer->InstanceOffsets;
            for (int i = 0; i < instanceOffsets.Length; i++)
            {
                FileLayerGroupInstance* instance = layer->Instance(instanceOffsets[i]);
                switch (instance->Type)
                {
                    case InstanceType.BgPart:
                        CollectBgPart((FileLayerGroupInstanceBgPart*)instance);
                        break;
                    case InstanceType.Vfx:
                        CollectVfx((FileLayerGroupInstanceVfx*)instance);
                        break;
                    case InstanceType.LineVfx:
                        break;
                    case InstanceType.SharedGroup:
                    case InstanceType.HelperObject:
                        CollectSharedGroup((FileLayerGroupInstanceSharedGroup*)instance);
                        break;
                }
            }
        }

        private void CollectBgPart(FileLayerGroupInstanceBgPart* bgPart)
        {
            string modelPath = ObjectPathRules.NormalizeGamePath(ReadCString(bgPart->PathMdl));
            if (!ObjectPathRules.IsCatalogModelPath(modelPath)
             || !_gameData.FileExists(modelPath)
             || !_seenBgObjectModels.Add(modelPath))
            {
                return;
            }

            _bgObjectModelPaths.Add(modelPath);
        }

        private void CollectVfx(FileLayerGroupInstanceVfx* vfx)
        {
            _ = TryMergeResolvedVfxPath(
                ReadCString(vfx->Path),
                RuntimeVfxEvidence.LayoutInstance,
                AssetPathSource.GameData,
                AssetPathContract.ParsedFileReference,
                ObjectSearchTermUtility.BuildStableTerms("layout vfx", ReadCString(vfx->Base.Name)));
        }

        private void CollectSharedGroup(FileLayerGroupInstanceSharedGroup* sharedGroup)
        {
            string sharedGroupPath = ObjectPathRules.NormalizeGamePath(ReadCString(sharedGroup->Path));
            if (!ObjectPathRules.IsCatalogSharedGroupPath(sharedGroupPath)
             || !_gameData.FileExists(sharedGroupPath)
             || !_seenSharedGroupPaths.Add(sharedGroupPath))
            {
                return;
            }

            _referencedSharedGroupPaths.Add(sharedGroupPath);

            SharedGroupAssetInfo sharedGroupAssets = SharedGroupAssetResolver.AnalyzeSharedGroup(_gameData, sharedGroupPath);
            foreach (string modelPath in sharedGroupAssets.BgObjectModelPaths.Where(_seenBgObjectModels.Add))
            {
                _bgObjectModelPaths.Add(modelPath);
            }

            foreach (string nestedSharedGroupPath in sharedGroupAssets.NestedSharedGroupPaths.Where(_seenSharedGroupPaths.Add))
            {
                _referencedSharedGroupPaths.Add(nestedSharedGroupPath);
            }

            IReadOnlyList<string> searchTerms = ObjectSearchTermUtility.BuildStableTerms("layout autoplay", sharedGroupPath);
            foreach (string vfxPath in sharedGroupAssets.StandaloneVfxPaths)
            {
                _ = TryMergeResolvedVfxPath(
                    vfxPath,
                    RuntimeVfxEvidence.LayoutAutoplay,
                    AssetPathSource.SharedGroup,
                    AssetPathContract.ParsedFileReference,
                    searchTerms);
            }
        }

        private void CollectSceneTimelines(string territoryLayoutPath, FileSceneHeader* sceneHeader)
        {
            FileSceneTimelineList* timelines = sceneHeader->Timelines;
            if (timelines is null)
            {
                return;
            }

            Span<FileSceneTimeline> entries = timelines->Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                FileSceneTimeline* timeline = (FileSceneTimeline*)Unsafe.AsPointer(ref entries[i]);
                CollectSceneTimeline(territoryLayoutPath, timeline);
            }
        }

        private void CollectSceneTimeline(string territoryLayoutPath, FileSceneTimeline* timeline)
        {
            if (GameDataAssetPathUtility.TryBuildActionTimelinePath(ReadCString(timeline->ActionTimelineKey.Value), out string actionTimelinePath))
            {
                CollectTimelineReferences(
                    actionTimelinePath,
                    RuntimeVfxEvidence.LayoutTimeline,
                    ObjectSearchTermUtility.BuildStableTerms("layout timeline", territoryLayoutPath, actionTimelinePath));
            }

            if (timeline->OffsetTmlb == 0)
            {
                return;
            }

            FileSceneTimelineDescription* timelineDescription = (FileSceneTimelineDescription*)timeline->Tmlb;
            if (timelineDescription is null
             || timelineDescription->Tag != (int)TmlbSectionMagic
             || timelineDescription->Size <= 0)
            {
                return;
            }

            byte[] tmlbData = new ReadOnlySpan<byte>(timelineDescription, timelineDescription->Size).ToArray();
            IReadOnlyList<string> searchTerms = ObjectSearchTermUtility.BuildStableTerms("layout timeline", territoryLayoutPath, "embedded tmlb");
            foreach (TmbVfxReference reference in VfxAssetAnalyzer.CollectTmbVfxReferences(_gameData, tmlbData))
            {
                _ = TryMergeResolvedVfxPath(
                    reference.Path,
                    RuntimeVfxEvidence.LayoutTimeline | reference.Evidence,
                    AssetPathSource.GameData,
                    AssetPathContract.ParsedFileReference,
                    ObjectSearchTermUtility.MergeTerms(searchTerms, reference.SearchTerms));
            }
        }

        private void CollectTimelineReferences(
            string timelinePath,
            RuntimeVfxEvidence sourceEvidence,
            IReadOnlyList<string> searchTerms)
        {
            string normalizedTimelinePath = ObjectPathRules.NormalizeGamePath(timelinePath);
            if (!ObjectPathRules.IsCatalogTimelinePath(normalizedTimelinePath)
             || !_gameData.FileExists(normalizedTimelinePath)
             || !_seenTimelinePaths.Add(normalizedTimelinePath))
            {
                return;
            }

            foreach (TmbVfxReference reference in _timelineReferenceCache.Get(normalizedTimelinePath))
            {
                _ = TryMergeResolvedVfxPath(
                    reference.Path,
                    sourceEvidence | reference.Evidence,
                    AssetPathSource.GameData,
                    AssetPathContract.ParsedFileReference,
                    ObjectSearchTermUtility.MergeTerms([.. searchTerms, normalizedTimelinePath], reference.SearchTerms));
            }
        }

        private bool TryMergeResolvedVfxPath(
            string path,
            RuntimeVfxEvidence evidence,
            AssetPathSource source,
            AssetPathContract contract,
            IReadOnlyList<string> searchTerms)
            => VfxResolvedPathUtility.TryMergeResolvedPath(
                _resolvedVfxPaths,
                _gameData,
                path,
                sqpackIndexSnapshot: null,
                KnownVfxFamily.None,
                evidence,
                source,
                contract,
                searchTerms);

        private static string ReadCString(byte* pointer)
            => pointer is null
                ? string.Empty
                : ObjectStringUtility.TrimOrEmpty(Marshal.PtrToStringUTF8((nint)pointer));
    }
}


