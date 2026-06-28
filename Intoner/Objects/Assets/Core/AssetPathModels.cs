namespace Intoner.Objects.Assets;

[Flags]
internal enum AssetPathSource
{
    None                 = 0,
    SqpackCollision      = 1 << 0,
    RootExl              = 1 << 1,
    NativeFamilyResolver = 1 << 2,
    SharedGroup          = 1 << 3,
    RuntimeObserved      = 1 << 4,
    Persisted            = 1 << 5,
    GameData             = 1 << 6,
}

[Flags]
internal enum AssetPathContract
{
    None                 = 0,
    SqpackNamedLeak      = 1 << 0,
    ParsedFileReference  = 1 << 1,
    DeterministicBuilder = 1 << 2,
    SheetConvention      = 1 << 3,
    RuntimeObservation   = 1 << 4,
    PersistedCache       = 1 << 5,
}

internal enum AssetPathKind
{
    Unknown,
    Model,
    SharedGroup,
    Vfx,
    Timeline,
    Material,
    Eid,
}

internal enum ObjectResourcePathKind
{
    Unknown,
    Model,
    SharedGroup,
    Timeline,
    Sklb,
    Pap,
    Material,
    Texture,
    ShaderPackage,
    Vfx,
    Atex,
    Sound,
    Eid,
}

[Flags]
internal enum KnownVfxFamily
{
    None       = 0,
    Common     = 1 << 0,
    Omen       = 1 << 1,
    Channeling = 1 << 2,
    Lockon     = 1 << 3,
    Event      = 1 << 4,
    Equipment  = 1 << 5,
    Weapon     = 1 << 6,
    Monster    = 1 << 7,
    DemiHuman  = 1 << 8,
    Accessory  = 1 << 9,
    LoVM       = 1 << 10,
}

internal sealed record KnownAssetPath(
    string Path,
    AssetPathKind Kind,
    AssetPathSource Sources,
    AssetPathContract Contracts,
    KnownVfxFamily VfxFamily,
    IReadOnlyList<string> SearchTerms);

internal static class AssetPathFlagExtensions
{
    public static bool HasAny(this AssetPathContract value, AssetPathContract flags)
        => (value & flags) != AssetPathContract.None;

    public static bool HasAll(this AssetPathContract value, AssetPathContract flags)
        => (value & flags) == flags;

    public static bool HasAny(this KnownVfxFamily value, KnownVfxFamily flags)
        => (value & flags) != KnownVfxFamily.None;
}

internal static class KnownVfxFamilyExtensions
{
    private readonly record struct PrefixRule(KnownVfxFamily Family, string[] Prefixes);
    private readonly record struct LabelRule(KnownVfxFamily Family, string Label);

    private static readonly PrefixRule[] PrefixRules =
    [
        new(KnownVfxFamily.Common, ["vfx/common/eff/"]),
        new(KnownVfxFamily.Omen, ["vfx/omen/eff/"]),
        new(KnownVfxFamily.Channeling, ["vfx/channeling/eff/"]),
        new(KnownVfxFamily.Lockon, ["vfx/lockon/eff/"]),
        new(KnownVfxFamily.Event, ["vfx/cut/general/eff/", "vfx/grouppose/eff/"]),
        new(KnownVfxFamily.Equipment, ["chara/equipment/", "vfx/equipment/eff/"]),
        new(KnownVfxFamily.Accessory, ["chara/accessory/", "vfx/accessory/eff/"]),
        new(KnownVfxFamily.Weapon, ["chara/weapon/", "vfx/weapon/eff/"]),
        new(KnownVfxFamily.Monster, ["chara/monster/", "vfx/monster/eff/"]),
        new(KnownVfxFamily.DemiHuman, ["chara/demihuman/", "vfx/demihuman/eff/"]),
        new(KnownVfxFamily.LoVM, ["vfx/lovm/eff/"]),
    ];

    private static readonly LabelRule[] LabelRules =
    [
        new(KnownVfxFamily.Common, "common effect"),
        new(KnownVfxFamily.Omen, "omen"),
        new(KnownVfxFamily.Channeling, "channeling"),
        new(KnownVfxFamily.Lockon, "lockon"),
        new(KnownVfxFamily.Event, "event vfx"),
        new(KnownVfxFamily.Equipment, "equipment effect"),
        new(KnownVfxFamily.Accessory, "accessory effect"),
        new(KnownVfxFamily.Weapon, "weapon effect"),
        new(KnownVfxFamily.Monster, "monster effect"),
        new(KnownVfxFamily.DemiHuman, "demihuman effect"),
        new(KnownVfxFamily.LoVM, "lovm effect"),
    ];

    public static string? TryGetSearchLabel(this KnownVfxFamily familyHint)
    {
        foreach (LabelRule rule in LabelRules)
        {
            if (familyHint.HasAny(rule.Family))
            {
                return rule.Label;
            }
        }

        return null;
    }

    public static KnownVfxFamily InferFamilyHintFromPath(string path)
    {
        string normalizedPath = ObjectPathRules.NormalizeGamePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return KnownVfxFamily.None;
        }

        foreach (PrefixRule rule in PrefixRules)
        {
            foreach (string prefix in rule.Prefixes)
            {
                if (normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return rule.Family;
                }
            }
        }

        return KnownVfxFamily.None;
    }
}

