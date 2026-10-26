using Intoner.Objects.Resources;
using Lumina.Excel.Sheets;

namespace Intoner.Objects.Assets;

internal static class GameDataAssetPathUtility
{
    public static bool TryBuildHousingInteriorSourcePath(HousingInterior row, out string path)
    {
        string directPath = ObjectResolvedPath.FromGamePath(row.Unknown0.ExtractText()).Path;
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
        string normalizedToken = ObjectResolvedPath.FromGamePath(bgToken).Path;
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            return string.Empty;
        }

        if (normalizedToken.EndsWith(".lvb", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedToken;
        }

        string path = normalizedToken.StartsWith("bg/", StringComparison.OrdinalIgnoreCase)
            ? $"{normalizedToken}.lvb"
            : $"bg/{normalizedToken}.lvb";
        return ObjectResolvedPath.FromGamePath(path).Path;
    }

    public static bool TryBuildCommonEffectVfxPath(string token, out string path)
    {
        string normalizedToken = ObjectResolvedPath.FromGamePath(token).Path;
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

        return TryBuildVfxFamilyPath("vfx/common/eff", normalizedToken, out path);
    }

    public static bool TryBuildVfxFamilyPath(string prefix, string token, out string path)
    {
        string normalizedPrefix = ObjectResolvedPath.FromGamePath(prefix).Path;
        string normalizedToken = ObjectResolvedPath.FromGamePath(token).Path;
        if (string.IsNullOrWhiteSpace(normalizedPrefix) || string.IsNullOrWhiteSpace(normalizedToken))
        {
            path = string.Empty;
            return false;
        }

        bool hasExtension = GameAssetPathRules.IsFileKind(normalizedToken, GameAssetFileKind.Avfx);
        if (normalizedToken.StartsWith("vfx/", StringComparison.OrdinalIgnoreCase))
        {
            path = hasExtension ? normalizedToken : $"{normalizedToken}.avfx";
        }
        else
        {
            path = hasExtension
                ? $"{normalizedPrefix}/{normalizedToken}"
                : $"{normalizedPrefix}/{normalizedToken}.avfx";
        }

        path = ObjectResolvedPath.FromGamePath(path).Path;
        return path.Length > 0;
    }

    public static bool TryBuildActionTimelinePath(string key, out string path)
    {
        string normalizedKey = ObjectResolvedPath.FromGamePath(key).Path;
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
            path = ObjectResolvedPath.FromGamePath($"{normalizedKey}.tmb").Path;
            return path.Length > 0;
        }

        path = ObjectResolvedPath.FromGamePath($"chara/action/{normalizedKey}.tmb").Path;
        return path.Length > 0;
    }
}
