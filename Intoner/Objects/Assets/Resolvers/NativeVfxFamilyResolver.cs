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

    public IReadOnlyList<ResolvedVfxPath> Resolve(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, ResolvedVfxPathAccumulator> resolvedPaths = new(StringComparer.OrdinalIgnoreCase);

        ResolveItemFamilies(sqpackIndexSnapshot, resolvedPaths, cancellationToken);
        ResolveModelCharaFamilies(sqpackIndexSnapshot, resolvedPaths, cancellationToken);
        ResolveGlassesFamily(sqpackIndexSnapshot, resolvedPaths, cancellationToken);
        ResolveLoVmFamilies(sqpackIndexSnapshot, resolvedPaths, cancellationToken);

        IReadOnlyList<ResolvedVfxPath> snapshot = ResolvedVfxPathAccumulator.BuildSnapshot(resolvedPaths.Values);

        _logger.LogInformation("resolved {VfxCount} static vfx paths from native deterministic family resolvers", snapshot.Count);
        return snapshot;
    }

    private void ResolveItemFamilies(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        CancellationToken cancellationToken)
    {
        ExcelSheet<Item>? sheet = _gameData.GetExcelSheet<Item>();
        if (sheet is null)
        {
            return;
        }

        foreach (Item row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EquipSlot slot = ((EquipSlot)row.EquipSlotCategory.RowId).ToSlot();
            ulong modelMain = row.ModelMain;
            ulong modelSub = row.ModelSub;

            if (slot.IsEquipment() && modelMain != 0)
            {
                TryAddEquipmentPath(
                    sqpackIndexSnapshot,
                    resolvedPaths,
                    (PrimaryId)(ushort)modelMain,
                    slot,
                    (Variant)(ushort)(modelMain >> 16));
            }

            if (slot.IsAccessory() && modelMain != 0)
            {
                TryAddAccessoryPath(
                    sqpackIndexSnapshot,
                    resolvedPaths,
                    (PrimaryId)(ushort)modelMain,
                    slot,
                    (Variant)(ushort)(modelMain >> 16));
            }

            if (slot == EquipSlot.MainHand && modelMain != 0)
            {
                TryAddWeaponPath(
                    sqpackIndexSnapshot,
                    resolvedPaths,
                    (PrimaryId)(ushort)modelMain,
                    (SecondaryId)(ushort)(modelMain >> 16),
                    (Variant)(ushort)(modelMain >> 32),
                    offhand: false);
            }

            if (slot == EquipSlot.OffHand && modelSub != 0)
            {
                TryAddWeaponPath(
                    sqpackIndexSnapshot,
                    resolvedPaths,
                    (PrimaryId)(ushort)modelSub,
                    (SecondaryId)(ushort)(modelSub >> 16),
                    (Variant)(ushort)(modelSub >> 32),
                    offhand: true);
            }
        }
    }

    private void ResolveModelCharaFamilies(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        CancellationToken cancellationToken)
    {
        ExcelSheet<ModelChara>? sheet = _gameData.GetExcelSheet<ModelChara>();
        if (sheet is null)
        {
            return;
        }

        foreach (ModelChara row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Model == 0 || row.Base == 0 || row.Variant == 0)
            {
                continue;
            }

            switch ((CharacterBase.ModelType)row.Type)
            {
                case CharacterBase.ModelType.Monster:
                    TryAddMonsterPath(
                        sqpackIndexSnapshot,
                        resolvedPaths,
                        row.RowId,
                        (PrimaryId)row.Model,
                        (SecondaryId)row.Base,
                        (Variant)row.Variant);
                    break;
                case CharacterBase.ModelType.DemiHuman:
                    TryAddDemiHumanPath(
                        sqpackIndexSnapshot,
                        resolvedPaths,
                        row.RowId,
                        (PrimaryId)row.Model,
                        (SecondaryId)row.Base,
                        (Variant)row.Variant);
                    break;
            }
        }
    }

    private void ResolveGlassesFamily(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        CancellationToken cancellationToken)
    {
        ExcelSheet<Glasses>? sheet = _gameData.GetCurrentLanguageExcelSheet<Glasses>();
        if (sheet is null)
        {
            return;
        }

        foreach (Glasses row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrimaryId equipmentId = (PrimaryId)(ushort)row.Unknown_70_8;
            Variant variant = (Variant)(row.Unknown_70_8 >> 16);
            if (equipmentId.Id == 0 || variant.Id == 0)
            {
                continue;
            }

            if (!TryReadImcVfxId(GamePaths.Imc.Equipment(equipmentId), EquipSlot.Head, variant, out byte effectId))
            {
                continue;
            }

            TryAddResolvedPath(
                sqpackIndexSnapshot,
                resolvedPaths,
                GamePaths.Avfx.Equipment(equipmentId, effectId),
                KnownVfxFamily.Equipment,
                RuntimeVfxEvidence.Glasses,
                AssetPathSource.NativeFamilyResolver,
                AssetPathContract.DeterministicBuilder,
                BuildGlassesSearchTerms(row.RowId, row.Name.ExtractText(), equipmentId, variant, effectId));
        }
    }

    private void ResolveLoVmFamilies(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        CancellationToken cancellationToken)
    {
        ResolveLoVmCompanionFamily(sqpackIndexSnapshot, resolvedPaths, cancellationToken);
        ResolveLoVmKnownConstantFamily(sqpackIndexSnapshot, resolvedPaths, cancellationToken);
        ResolveLoVmStageFamily(sqpackIndexSnapshot, resolvedPaths, cancellationToken);
    }

    private void ResolveLoVmCompanionFamily(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        CancellationToken cancellationToken)
    {
        ExcelSheet<Companion>? sheet = _gameData.GetExcelSheet<Companion>();
        if (sheet is null)
        {
            return;
        }

        foreach (Companion row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Unknown1 == 0)
            {
                continue;
            }

            // native lovm callers read effect ids from selector 0xac -> Companion
            TryAddLoVmPath(
                sqpackIndexSnapshot,
                resolvedPaths,
                row.Unknown1,
                BuildLoVmCompanionSearchTerms(row.RowId, row.Singular.ExtractText(), row.Unknown1));
        }
    }

    private void ResolveLoVmKnownConstantFamily(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        CancellationToken cancellationToken)
    {
        // these ids are used directly by native lovm callers without a sheet lookup
        foreach (ushort effectId in LoVmKnownConstantEffectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryAddLoVmPath(
                sqpackIndexSnapshot,
                resolvedPaths,
                effectId,
                BuildLoVmConstantSearchTerms(effectId));
        }
    }

    private void ResolveLoVmStageFamily(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        CancellationToken cancellationToken)
    {
        ExcelSheet<MinionStage>? sheet = _gameData.GetExcelSheet<MinionStage>();
        if (sheet is null)
        {
            return;
        }

        foreach (MinionStage row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Unknown1 == 0)
            {
                continue;
            }

            // stage rows also expose lovm effect ids
            TryAddLoVmPath(
                sqpackIndexSnapshot,
                resolvedPaths,
                row.Unknown1,
                BuildLoVmStageSearchTerms(row.RowId, row.Unknown0.ExtractText(), row.Unknown1, row.Unknown2));
        }
    }

    private void TryAddEquipmentPath(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
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
            sqpackIndexSnapshot,
            resolvedPaths,
            GamePaths.Avfx.Equipment(equipmentId, effectId),
            KnownVfxFamily.Equipment,
            RuntimeVfxEvidence.Equipment,
            AssetPathSource.NativeFamilyResolver,
            AssetPathContract.DeterministicBuilder,
            BuildEquipmentSearchTerms(equipmentId, variant, effectId, slot));
    }

    private void TryAddAccessoryPath(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
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
            sqpackIndexSnapshot,
            resolvedPaths,
            $"chara/accessory/a{accessoryId.Id:D4}/vfx/eff/va{effectId:D4}.avfx",
            KnownVfxFamily.Accessory,
            RuntimeVfxEvidence.Accessory,
            AssetPathSource.NativeFamilyResolver,
            AssetPathContract.DeterministicBuilder,
            BuildAccessorySearchTerms(accessoryId, variant, effectId, slot));
    }

    private void TryAddWeaponPath(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        PrimaryId weaponId,
        SecondaryId bodyId,
        Variant variant,
        bool offhand)
    {
        if (weaponId.Id == 0
         || bodyId.Id == 0
         || variant.Id == 0
         || !TryReadImcVfxId(GamePaths.Imc.Weapon(weaponId, bodyId), EquipSlot.Unknown, variant, out byte effectId))
        {
            return;
        }

        string path = effectId < 100
            ? $"chara/weapon/w{weaponId.Id:D4}/obj/body/b{bodyId.Id:D4}/vfx/eff/vw{effectId:D4}.avfx"
            : $"vfx/weapon/eff/vw{effectId:D4}.avfx";

        TryAddResolvedPath(
            sqpackIndexSnapshot,
            resolvedPaths,
            path,
            KnownVfxFamily.Weapon,
            RuntimeVfxEvidence.Weapon,
            AssetPathSource.NativeFamilyResolver,
            AssetPathContract.DeterministicBuilder,
            BuildWeaponSearchTerms(weaponId, bodyId, variant, effectId, offhand));
    }

    private void TryAddMonsterPath(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
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
            sqpackIndexSnapshot,
            resolvedPaths,
            BuildMonsterPath(monsterId, bodyId, effectId),
            KnownVfxFamily.Monster,
            RuntimeVfxEvidence.Monster,
            AssetPathSource.NativeFamilyResolver,
            AssetPathContract.DeterministicBuilder,
            BuildMonsterSearchTerms(rowId, monsterId, bodyId, variant, effectId));
    }

    private void TryAddDemiHumanPath(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
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
            sqpackIndexSnapshot,
            resolvedPaths,
            path,
            KnownVfxFamily.DemiHuman,
            RuntimeVfxEvidence.DemiHuman,
            AssetPathSource.NativeFamilyResolver,
            AssetPathContract.DeterministicBuilder,
            BuildDemiHumanSearchTerms(rowId, demiHumanId, equipmentId, variant, effectId));
    }

    private void TryAddResolvedPath(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        string path,
        KnownVfxFamily family,
        RuntimeVfxEvidence evidence,
        AssetPathSource sources,
        AssetPathContract contracts,
        IEnumerable<string> searchTerms)
        => _ = VfxResolvedPathUtility.TryMergeResolvedPath(
            resolvedPaths,
            _gameData,
            path,
            sqpackIndexSnapshot,
            family,
            evidence,
            sources,
            contracts,
            searchTerms);

    private void TryAddLoVmPath(
        SqpackIndexSnapshot sqpackIndexSnapshot,
        IDictionary<string, ResolvedVfxPathAccumulator> resolvedPaths,
        ushort effectId,
        IEnumerable<string> searchTerms)
        => TryAddResolvedPath(
            sqpackIndexSnapshot,
            resolvedPaths,
            BuildLoVmPath(effectId),
            KnownVfxFamily.LoVM,
            RuntimeVfxEvidence.LoVM,
            AssetPathSource.NativeFamilyResolver,
            AssetPathContract.DeterministicBuilder,
            searchTerms);

    private bool TryReadImcVfxId(string imcPath, EquipSlot slot, Variant variant, out byte effectId)
    {
        effectId = 0;

        string normalizedPath = ObjectPathRules.NormalizeGamePath(imcPath);
        if (string.IsNullOrWhiteSpace(normalizedPath)
         || !_gameData.FileExists(normalizedPath))
        {
            return false;
        }

        ImcFile? imcFile = _gameData.GetFile<ImcFile>(normalizedPath);
        if (imcFile is null || variant.Id == 0)
        {
            return false;
        }

        int partIndex = GetImcPartIndex(slot);
        var parts = imcFile.GetParts().ToArray();
        if (partIndex < 0 || partIndex >= parts.Length)
        {
            return false;
        }

        var part = parts[partIndex];
        int variantIndex = variant.Id - 1;
        var variants = part.Variants.ToArray();
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

    private static IReadOnlyList<string> BuildSearchTerms(params string?[] terms)
        => ObjectSearchTermUtility.BuildStableTerms(terms);

    private static IReadOnlyList<string> BuildEquipmentSearchTerms(PrimaryId equipmentId, Variant variant, byte effectId, EquipSlot slot)
        => BuildSearchTerms(
            "equipment effect",
            "equipment",
            slot.ToString(),
            $"e{equipmentId.Id:D4}",
            $"variant {variant.Id:D2}",
            $"ve{effectId:D4}");

    private static IReadOnlyList<string> BuildWeaponSearchTerms(PrimaryId weaponId, SecondaryId bodyId, Variant variant, byte effectId, bool offhand)
        => BuildSearchTerms(
            "weapon effect",
            "weapon",
            offhand ? "offhand" : "mainhand",
            $"w{weaponId.Id:D4}",
            $"b{bodyId.Id:D4}",
            $"variant {variant.Id:D2}",
            $"vw{effectId:D4}");

    private static IReadOnlyList<string> BuildAccessorySearchTerms(PrimaryId accessoryId, Variant variant, byte effectId, EquipSlot slot)
        => BuildSearchTerms(
            "accessory effect",
            "accessory",
            slot.ToString(),
            $"a{accessoryId.Id:D4}",
            $"variant {variant.Id:D2}",
            $"va{effectId:D4}");

    private static IReadOnlyList<string> BuildMonsterSearchTerms(uint rowId, PrimaryId monsterId, SecondaryId bodyId, Variant variant, byte effectId)
        => BuildSearchTerms(
            "monster effect",
            "monster",
            "ModelChara",
            rowId.ToString(),
            $"m{monsterId.Id:D4}",
            $"b{bodyId.Id:D4}",
            $"variant {variant.Id:D2}",
            $"vm{effectId:D4}");

    private static IReadOnlyList<string> BuildDemiHumanSearchTerms(uint rowId, PrimaryId demiHumanId, SecondaryId equipmentId, Variant variant, byte effectId)
        => BuildSearchTerms(
            "demihuman effect",
            "demihuman",
            "ModelChara",
            rowId.ToString(),
            $"d{demiHumanId.Id:D4}",
            $"e{equipmentId.Id:D4}",
            $"variant {variant.Id:D2}",
            $"ve{effectId:D4}");

    private static IReadOnlyList<string> BuildGlassesSearchTerms(uint rowId, string name, PrimaryId equipmentId, Variant variant, byte effectId)
        => BuildSearchTerms(
            "glasses effect",
            "glasses",
            rowId.ToString(),
            name,
            $"e{equipmentId.Id:D4}",
            $"variant {variant.Id:D2}",
            $"ve{effectId:D4}");

    private static IReadOnlyList<string> BuildLoVmCompanionSearchTerms(uint rowId, string minionName, ushort effectId)
        => BuildSearchTerms(
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
        => BuildSearchTerms(
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
        => BuildSearchTerms(
            "lovm effect",
            "lovm",
            "Lord of Verminion",
            "verminion",
            "native constant",
            $"lovm {effectId:D3}");

    private static ReadOnlySpan<ushort> LoVmKnownConstantEffectIds
        => [1, 12, 16, 22];
}

