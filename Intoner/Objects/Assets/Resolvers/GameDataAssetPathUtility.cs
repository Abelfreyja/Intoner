using Lumina.Excel.Sheets;

namespace Intoner.Objects.Assets;

internal static class GameDataAssetPathUtility
{
    public static bool TryBuildHousingInteriorSourcePath(HousingInterior row, out string path)
    {
        string directPath = GameAssetPathRules.NormalizeGamePath(row.Unknown0.ExtractText());
        if (ObjectAssetPathRules.IsCatalogSharedGroupPath(directPath)
         || ObjectAssetPathRules.IsCatalogModelPath(directPath))
        {
            path = directPath;
            return true;
        }

        if (row.Unknown2 == 11 && row.Unknown1 != 0)
        {
            path = $"bgcommon/hou/dyna/lmp/lp/{row.Unknown1:0000}/asset/lmp_s0_m{row.Unknown1:0000}.sgb";
            return true;
        }

        path = string.Empty;
        return false;
    }

    public static string BuildIndoorHousingSharedGroupPath(ushort modelKey)
        => $"bgcommon/hou/indoor/general/{modelKey:D4}/asset/fun_b0_m{modelKey:D4}.sgb";

    public static string BuildOutdoorHousingSharedGroupPath(ushort modelKey)
        => $"bgcommon/hou/outdoor/general/{modelKey:D4}/asset/gar_b0_m{modelKey:D4}.sgb";

    public static string BuildTerritoryLayoutPath(string bgToken)
    {
        string normalizedToken = GameAssetPathRules.NormalizeGamePath(bgToken);
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            return string.Empty;
        }

        if (normalizedToken.EndsWith(".lvb", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedToken;
        }

        return normalizedToken.StartsWith("bg/", StringComparison.OrdinalIgnoreCase)
            ? $"{normalizedToken}.lvb"
            : $"bg/{normalizedToken}.lvb";
    }

    public static bool TryBuildCommonEffectVfxPath(string token, out string path)
    {
        string normalizedToken = GameAssetPathRules.NormalizeGamePath(token);
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            path = string.Empty;
            return false;
        }

        if (GameAssetPathRules.IsFileKind(normalizedToken, GameAssetFileKind.Avfx))
        {
            path = normalizedToken;
            return true;
        }

        if (normalizedToken.StartsWith("vfx/", StringComparison.OrdinalIgnoreCase))
        {
            path = $"{normalizedToken}.avfx";
            return true;
        }

        path = $"vfx/common/eff/{normalizedToken}.avfx";
        return true;
    }

    public static bool TryBuildActionTimelinePath(string key, out string path)
    {
        string normalizedKey = GameAssetPathRules.NormalizeGamePath(key);
        if (string.IsNullOrWhiteSpace(normalizedKey)
         || normalizedKey.Contains("[SKL_ID]", StringComparison.OrdinalIgnoreCase))
        {
            path = string.Empty;
            return false;
        }

        if (GameAssetPathRules.IsFileKind(normalizedKey, GameAssetFileKind.Tmb))
        {
            path = normalizedKey;
            return true;
        }

        if (normalizedKey.StartsWith("chara/action/", StringComparison.OrdinalIgnoreCase))
        {
            path = $"{normalizedKey}.tmb";
            return true;
        }

        path = $"chara/action/{normalizedKey}.tmb";
        return true;
    }
}

