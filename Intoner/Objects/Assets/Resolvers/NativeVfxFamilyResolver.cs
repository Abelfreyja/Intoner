using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Intoner.Objects.Utils;
using Lumina.Data.Files;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using Penumbra.GameData.Data;
using Penumbra.GameData.Enums;
using Penumbra.GameData.Structs;

namespace Intoner.Objects.Assets;

internal sealed class NativeVfxFamilyResolver
{
    private readonly ILogger<NativeVfxFamilyResolver> _logger;
    private readonly IObjectAssetGameData _gameData;

    public NativeVfxFamilyResolver(ILogger<NativeVfxFamilyResolver> logger, IObjectAssetGameData gameData)
    {
        _logger = logger;
        _gameData = gameData;
    }

    public ResolvedVfxDiscoverySnapshot Resolve(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        CancellationToken cancellationToken = default)
    {
        ResolvedVfxDiscoverySnapshot snapshot = new Collector(_gameData, sqpackIndexSnapshot, cancellationToken).Collect();
        _logger.LogInformation("resolved {VfxCount} static vfx paths from native deterministic family resolvers", snapshot.ResolvedVfxPaths.Count);
        return snapshot;
    }

    private sealed class Collector(
        IObjectAssetGameData gameData,
        SqpackIndexSnapshot sqpackIndexSnapshot,
        CancellationToken cancellationToken)
    {
        private readonly IObjectAssetGameData _gameData = gameData;
        private readonly SqpackIndexSnapshot _sqpackIndexSnapshot = sqpackIndexSnapshot;
        private readonly CancellationToken _cancellationToken = cancellationToken;
        private readonly Dictionary<string, ResolvedVfxPathAccumulator> _resolvedPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ImcFile.ImageChangeParts[]> _imcParts = new(StringComparer.OrdinalIgnoreCase);
        private bool _isComplete = true;

        public ResolvedVfxDiscoverySnapshot Collect()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            ResolveItemFamilies();
            ResolveModelCharaFamilies();
            ResolveGlassesFamily();
            ResolveLoVmFamilies();
            _cancellationToken.ThrowIfCancellationRequested();
            return new ResolvedVfxDiscoverySnapshot(ResolvedVfxPathAccumulator.BuildSnapshot(_resolvedPaths.Values), _isComplete);
        }

        private void ResolveItemFamilies()
        {
            ExcelSheet<Item>? sheet = _gameData.GetExcelSheet<Item>();

            if (sheet is null)
            {
                _isComplete = false;
                return;
            }

            foreach (Item row in sheet)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ResolveItemModels(
                    ((EquipSlot)row.EquipSlotCategory.RowId).ToSlot(),
                    (WeaponCategory)row.ItemUICategory.RowId,
                    row.ModelMain,
                    row.ModelSub);
            }
        }

        private void ResolveItemModels(
            EquipSlot slot,
            WeaponCategory weaponCategory,
            ulong modelMain,
            ulong modelSub)
        {
            if (slot.IsEquipment() && modelMain != 0)
            {
                TryAddEquipmentPath(
                    (PrimaryId)(ushort)modelMain,
                    slot,
                    (Variant)(ushort)(modelMain >> 16));
            }

            if (slot.IsAccessory() && modelMain != 0)
            {
                TryAddAccessoryPath(
                    (PrimaryId)(ushort)modelMain,
                    slot,
                    (Variant)(ushort)(modelMain >> 16));
            }

            if (slot is not (EquipSlot.MainHand or EquipSlot.OffHand))
            {
                return;
            }

            if (modelMain != 0)
            {
                TryAddWeaponModelPath(modelMain, slot == EquipSlot.OffHand);
            }

            if (modelSub == 0)
            {
                return;
            }

            FullEquipType type = slot.ToEquipType(weaponCategory);
            if (type == FullEquipType.Fists && modelSub < 0x100000000)
            {
                // fist gauntlets use the secondary field and the offhand weapon is derived from the primary
                TryAddEquipmentPath(
                    (PrimaryId)(ushort)modelSub,
                    EquipSlot.Hands,
                    (Variant)(ushort)(modelSub >> 16));
                TryAddWeaponPath(
                    (PrimaryId)(ushort)((ushort)modelMain + 50),
                    (SecondaryId)(ushort)(modelMain >> 16),
                    (Variant)(ushort)(modelMain >> 32),
                    offhand: true);
                return;
            }

            if (type.IsWeapon() || type.IsTool())
            {
                TryAddWeaponModelPath(modelSub, offhand: true);
            }
        }

        private void TryAddWeaponModelPath(
            ulong model,
            bool offhand)
            => TryAddWeaponPath(
                (PrimaryId)(ushort)model,
                (SecondaryId)(ushort)(model >> 16),
                (Variant)(ushort)(model >> 32),
                offhand);

        private void ResolveModelCharaFamilies()
        {
            ExcelSheet<ModelChara>? sheet = _gameData.GetExcelSheet<ModelChara>();

            if (sheet is null)
            {
                _isComplete = false;
                return;
            }

            foreach (ModelChara row in sheet)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (row.Model == 0 || row.Base == 0 || row.Variant == 0)
                {
                    continue;
                }

                switch ((CharacterBase.ModelType)row.Type)
                {
                    case CharacterBase.ModelType.Monster:
                        TryAddMonsterPath(
                            row.RowId,
                            (PrimaryId)row.Model,
                            (SecondaryId)row.Base,
                            (Variant)row.Variant);
                        break;
                    case CharacterBase.ModelType.DemiHuman:
                        TryAddDemiHumanPath(
                            row.RowId,
                            (PrimaryId)row.Model,
                            (SecondaryId)row.Base,
                            (Variant)row.Variant);
                        break;
                }
            }
        }

        private void ResolveGlassesFamily()
        {
            ExcelSheet<Glasses>? sheet = _gameData.GetCurrentLanguageExcelSheet<Glasses>();

            if (sheet is null)
            {
                _isComplete = false;
                return;
            }

            foreach (Glasses row in sheet)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                PrimaryId equipmentId = (PrimaryId)(ushort)row.Model;
                Variant variant = (Variant)(row.Model >> 16);
                if (equipmentId.Id == 0 || variant.Id == 0)
                {
                    continue;
                }

                if (!TryReadImcVfxId(GamePaths.Imc.Equipment(equipmentId), EquipSlot.Head, variant, out byte effectId))
                {
                    continue;
                }

                TryAddResolvedPath(
                    GamePaths.Avfx.Equipment(equipmentId, effectId),
                    KnownVfxFamily.Equipment,
                    RuntimeVfxEvidence.Glasses,
                    BuildGlassesSearchTerms(row.RowId, row.Name.ExtractText(), equipmentId, variant));
            }
        }

        private void ResolveLoVmFamilies()
        {
            ResolveLoVmCompanionFamily();
            ResolveLoVmKnownConstantFamily();
            ResolveLoVmStageFamily();
        }

        private void ResolveLoVmCompanionFamily()
        {
            ExcelSheet<Companion>? sheet = _gameData.GetExcelSheet<Companion>();

            if (sheet is null)
            {
                _isComplete = false;
                return;
            }

            foreach (Companion row in sheet)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (row.Unknown1 == 0)
                {
                    continue;
                }

                // native lovm callers read effect ids from selector 0xac -> Companion
                TryAddLoVmPath(
                    row.Unknown1,
                    BuildLoVmCompanionSearchTerms(row.RowId, row.Singular.ExtractText(), row.Unknown1));
            }
        }

        private void ResolveLoVmKnownConstantFamily()
        {
            // these ids are used directly by native lovm callers without a sheet lookup
            foreach (ushort effectId in LoVmKnownConstantEffectIds)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                TryAddLoVmPath(
                    effectId,
                    BuildLoVmConstantSearchTerms(effectId));
            }
        }

        private void ResolveLoVmStageFamily()
        {
            ExcelSheet<MinionStage>? sheet = _gameData.GetExcelSheet<MinionStage>();

            if (sheet is null)
            {
                _isComplete = false;
                return;
            }

            foreach (MinionStage row in sheet)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (row.Unknown1 == 0)
                {
                    continue;
                }

                // stage rows also expose lovm effect ids
                TryAddLoVmPath(
                    row.Unknown1,
                    BuildLoVmStageSearchTerms(row.RowId, row.Unknown0.ExtractText(), row.Unknown1, row.Unknown2));
            }
        }

        private void TryAddEquipmentPath(
            PrimaryId equipmentId,
            EquipSlot slot,
            Variant variant)
        {
            if (equipmentId.Id == 0
             || variant.Id == 0
             || !TryReadImcVfxId(GamePaths.Imc.Equipment(equipmentId), slot, variant, out byte effectId))
            {
                return;
            }

            TryAddResolvedPath(
                GamePaths.Avfx.Equipment(equipmentId, effectId),
                KnownVfxFamily.Equipment,
                RuntimeVfxEvidence.Equipment,
                BuildEquipmentSearchTerms(equipmentId, variant, slot));
        }

        private void TryAddAccessoryPath(
            PrimaryId accessoryId,
            EquipSlot slot,
            Variant variant)
        {
            if (accessoryId.Id == 0
             || variant.Id == 0
             || !TryReadImcVfxId(GamePaths.Imc.Accessory(accessoryId), slot, variant, out byte effectId))
            {
                return;
            }

            TryAddResolvedPath(
                GamePaths.Avfx.Accessory(accessoryId, effectId),
                KnownVfxFamily.Accessory,
                RuntimeVfxEvidence.Accessory,
                BuildAccessorySearchTerms(accessoryId, variant, slot));
        }

        private void TryAddWeaponPath(
            PrimaryId weaponId,
            SecondaryId bodyId,
            Variant variant,
            bool offhand)
        {
            ItemData.AdaptOffhandImc(weaponId, out PrimaryId imcWeaponId);

            if (weaponId.Id == 0
             || bodyId.Id == 0
             || variant.Id == 0
             || !TryReadImcVfxId(GamePaths.Imc.Weapon(imcWeaponId, bodyId), EquipSlot.Unknown, variant, out byte effectId))
            {
                return;
            }

            string path = effectId < 100
                ? $"chara/weapon/w{weaponId.Id:D4}/obj/body/b{bodyId.Id:D4}/vfx/eff/vw{effectId:D4}.avfx"
                : $"vfx/weapon/eff/vw{effectId:D4}.avfx";

            TryAddResolvedPath(
                path,
                KnownVfxFamily.Weapon,
                RuntimeVfxEvidence.Weapon,
                BuildWeaponSearchTerms(weaponId, bodyId, variant, offhand));
        }

        private void TryAddMonsterPath(
            uint rowId,
            PrimaryId monsterId,
            SecondaryId bodyId,
            Variant variant)
        {
            if (!TryReadImcVfxId(GamePaths.Imc.Monster(monsterId, bodyId), EquipSlot.Unknown, variant, out byte effectId))
            {
                return;
            }

            TryAddResolvedPath(
                BuildMonsterPath(monsterId, bodyId, effectId),
                KnownVfxFamily.Monster,
                RuntimeVfxEvidence.Monster,
                BuildMonsterSearchTerms(rowId, monsterId, bodyId, variant));
        }

        private void TryAddDemiHumanPath(
            uint rowId,
            PrimaryId demiHumanId,
            SecondaryId equipmentId,
            Variant variant)
        {
            if (!TryReadImcVfxId(GamePaths.Imc.DemiHuman(demiHumanId, equipmentId), EquipSlot.Unknown, variant, out byte effectId))
            {
                return;
            }

            string path = BuildDemiHumanPath(demiHumanId, equipmentId, effectId);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            TryAddResolvedPath(
                path,
                KnownVfxFamily.DemiHuman,
                RuntimeVfxEvidence.DemiHuman,
                BuildDemiHumanSearchTerms(rowId, demiHumanId, equipmentId, variant));
        }

        private void TryAddResolvedPath(
            string path,
            KnownVfxFamily family,
            RuntimeVfxEvidence evidence,
            IEnumerable<string> searchTerms)
            => _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                _resolvedPaths,
                _gameData,
                path,
                _sqpackIndexSnapshot,
                family,
                evidence,
                AssetPathSource.NativeFamilyResolver,
                AssetPathContract.DeterministicBuilder,
                searchTerms);

        private void TryAddLoVmPath(
            ushort effectId,
            IEnumerable<string> searchTerms)
            => TryAddResolvedPath(
                BuildLoVmPath(effectId),
                KnownVfxFamily.LoVM,
                RuntimeVfxEvidence.LoVM,
                searchTerms);

        private bool TryReadImcVfxId(string imcPath, EquipSlot slot, Variant variant, out byte effectId)
        {
            effectId = 0;

            _cancellationToken.ThrowIfCancellationRequested();
            string normalizedPath = GameAssetPathRules.NormalizeGamePath(imcPath);

            if (string.IsNullOrWhiteSpace(normalizedPath) || variant.Id == 0)
            {
                return false;
            }

            if (!_imcParts.TryGetValue(normalizedPath, out ImcFile.ImageChangeParts[]? parts))
            {
                bool exists = _gameData.FileExists(normalizedPath);
                _cancellationToken.ThrowIfCancellationRequested();

                if (!exists)
                {
                    _imcParts.Add(normalizedPath, []);
                    return false;
                }

                ImcFile? imcFile;

                try
                {
                    imcFile = _gameData.GetFile<ImcFile>(normalizedPath);
                }
                catch (Exception ex) when (ObjectAssetFileUtility.IsReadFailure(ex))
                {
                    imcFile = null;
                }

                _cancellationToken.ThrowIfCancellationRequested();

                if (imcFile is null)
                {
                    _isComplete = false;
                    return false;
                }

                parts = imcFile.GetParts();
                _cancellationToken.ThrowIfCancellationRequested();
                _imcParts.Add(normalizedPath, parts);
            }

            int partIndex = GetImcPartIndex(slot);
            if (partIndex < 0 || partIndex >= parts.Length)
            {
                return false;
            }

            int variantIndex = variant.Id - 1;
            ImcFile.ImageChangeData[] variants = parts[partIndex].Variants;
            if (variantIndex < 0 || variantIndex >= variants.Length)
            {
                return false;
            }

            effectId = variants[variantIndex].VfxId;
            return effectId != 0;
        }

        private static int GetImcPartIndex(EquipSlot slot)
            => slot switch
            {
                EquipSlot.Head => 0,
                EquipSlot.Ears => 0,
                EquipSlot.Body => 1,
                EquipSlot.Neck => 1,
                EquipSlot.Hands => 2,
                EquipSlot.Wrists => 2,
                EquipSlot.Legs => 3,
                EquipSlot.RFinger => 3,
                EquipSlot.Feet => 4,
                EquipSlot.LFinger => 4,
                _ => 0,
            };

        private static string BuildMonsterPath(PrimaryId monsterId, SecondaryId bodyId, byte effectId)
            => effectId < 200
                ? $"chara/monster/m{monsterId.Id:D4}/obj/body/b{bodyId.Id:D4}/vfx/eff/vm{effectId:D4}.avfx"
                : $"vfx/monster/eff/vm{effectId:D4}.avfx";

        private static string BuildDemiHumanPath(PrimaryId demiHumanId, SecondaryId equipmentId, byte effectId)
            => effectId is > 0 and < 100
                ? $"chara/demihuman/d{demiHumanId.Id:D4}/obj/equipment/e{equipmentId.Id:D4}/vfx/eff/ve{effectId:D4}.avfx"
                : string.Empty;

        private static string BuildLoVmPath(ushort effectId)
            => $"vfx/lovm/eff/{effectId:D3}.avfx";

        private static IReadOnlyList<string> BuildEquipmentSearchTerms(PrimaryId equipmentId, Variant variant, EquipSlot slot)
            => SearchTermUtility.BuildStableTerms(
                "equipment effect",
                "equipment",
                slot.ToString(),
                $"e{equipmentId.Id:D4}",
                $"variant {variant.Id:D2}");

        private static IReadOnlyList<string> BuildWeaponSearchTerms(PrimaryId weaponId, SecondaryId bodyId, Variant variant, bool offhand)
            => SearchTermUtility.BuildStableTerms(
                "weapon effect",
                "weapon",
                offhand ? "offhand" : "mainhand",
                $"w{weaponId.Id:D4}",
                $"b{bodyId.Id:D4}",
                $"variant {variant.Id:D2}");

        private static IReadOnlyList<string> BuildAccessorySearchTerms(PrimaryId accessoryId, Variant variant, EquipSlot slot)
            => SearchTermUtility.BuildStableTerms(
                "accessory effect",
                "accessory",
                slot.ToString(),
                $"a{accessoryId.Id:D4}",
                $"variant {variant.Id:D2}");

        private static IReadOnlyList<string> BuildMonsterSearchTerms(uint rowId, PrimaryId monsterId, SecondaryId bodyId, Variant variant)
            => SearchTermUtility.BuildStableTerms(
                "monster effect",
                "monster",
                "ModelChara",
                rowId.ToString(),
                $"m{monsterId.Id:D4}",
                $"b{bodyId.Id:D4}",
                $"variant {variant.Id:D2}");

        private static IReadOnlyList<string> BuildDemiHumanSearchTerms(uint rowId, PrimaryId demiHumanId, SecondaryId equipmentId, Variant variant)
            => SearchTermUtility.BuildStableTerms(
                "demihuman effect",
                "demihuman",
                "ModelChara",
                rowId.ToString(),
                $"d{demiHumanId.Id:D4}",
                $"e{equipmentId.Id:D4}",
                $"variant {variant.Id:D2}");

        private static IReadOnlyList<string> BuildGlassesSearchTerms(uint rowId, string name, PrimaryId equipmentId, Variant variant)
            => SearchTermUtility.BuildStableTerms(
                "glasses effect",
                "glasses",
                rowId.ToString(),
                name,
                $"e{equipmentId.Id:D4}",
                $"variant {variant.Id:D2}");

        private static IReadOnlyList<string> BuildLoVmCompanionSearchTerms(uint rowId, string minionName, ushort effectId)
            => SearchTermUtility.BuildStableTerms(
                "lovm effect",
                "lovm",
                "Lord of Verminion",
                "verminion",
                "Companion",
                "minion",
                rowId.ToString(),
                $"lovm {effectId:D3}",
                minionName);

        private static IReadOnlyList<string> BuildLoVmStageSearchTerms(uint rowId, string stageName, ushort effectId, bool challengeStage)
            => SearchTermUtility.BuildStableTerms(
                "lovm effect",
                "lovm",
                "Lord of Verminion",
                "verminion",
                "MinionStage",
                rowId.ToString(),
                $"lovm {effectId:D3}",
                stageName,
                challengeStage ? "challenge stage" : null);

        private static IReadOnlyList<string> BuildLoVmConstantSearchTerms(ushort effectId)
            => SearchTermUtility.BuildStableTerms(
                "lovm effect",
                "lovm",
                "Lord of Verminion",
                "verminion",
                "native constant",
                $"lovm {effectId:D3}");

        private static ReadOnlySpan<ushort> LoVmKnownConstantEffectIds
            => [1, 12, 16, 22];
    }
}
