namespace Intoner.Objects.Assets;

internal static class ObjectAssetPathRules
{
    public static bool IsCatalogModelPath(string modelPath)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(modelPath);
        return IsCatalogModelPathNormalized(normalizedPath);
    }

    public static bool IsCatalogSharedGroupPath(string sharedGroupPath)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(sharedGroupPath);
        return IsCatalogSharedGroupPathNormalized(normalizedPath);
    }

    public static bool IsBgObjectModelPath(string modelPath)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(modelPath);
        return IsCatalogModelPathNormalized(normalizedPath)
            && (normalizedPath.Contains("/bgparts/", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.Contains("/evt/", StringComparison.OrdinalIgnoreCase));
    }

    public static AssetPathKind ClassifyKnownAssetPath(string path)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(path);
        if (IsCatalogModelPathNormalized(normalizedPath))
        {
            return AssetPathKind.Model;
        }

        if (IsCatalogSharedGroupPathNormalized(normalizedPath))
        {
            return AssetPathKind.SharedGroup;
        }

        return GameAssetPathRules.ClassifyNormalizedFilePath(normalizedPath) switch
        {
            GameAssetFileKind.Avfx => AssetPathKind.Vfx,
            GameAssetFileKind.Tmb => AssetPathKind.Timeline,
            GameAssetFileKind.Mtrl => AssetPathKind.Material,
            GameAssetFileKind.Eid => AssetPathKind.Eid,
            _ => AssetPathKind.Unknown,
        };
    }

    public static ObjectResourcePathKind ClassifyResourcePath(string path)
        => ClassifyResourcePathNormalized(GameAssetPathRules.NormalizeGamePath(path));

    public static bool IsSupportedResourcePath(string path)
        => ClassifyResourcePath(path) != ObjectResourcePathKind.Unknown;

    public static bool TryNormalizeSupportedResourcePath(string path, out string normalizedPath)
    {
        if (GameAssetPathRules.TryNormalizeGamePath(path, out normalizedPath)
         && ClassifyResourcePathNormalized(normalizedPath) != ObjectResourcePathKind.Unknown)
        {
            return true;
        }

        normalizedPath = string.Empty;
        return false;
    }

    public static IEnumerable<string> CollectBgModelAnimationResourcePaths(string modelPath)
    {
        string normalizedModelPath = GameAssetPathRules.NormalizeGamePath(modelPath);
        if (GameAssetPathRules.ClassifyNormalizedFilePath(normalizedModelPath) != GameAssetFileKind.Mdl)
        {
            yield break;
        }

        int extensionIndex = normalizedModelPath.LastIndexOf('.');
        if (extensionIndex <= 0)
        {
            yield break;
        }

        string basePath = normalizedModelPath[..extensionIndex];
        yield return $"{basePath}.sklb";
        yield return $"{basePath}.pap";
    }

    public static bool IsRelevantSqpackCatalogSeedPath(string path)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(path);
        return IsCatalogModelPathNormalized(normalizedPath)
            || IsCatalogSharedGroupPathNormalized(normalizedPath)
            || GameAssetPathRules.ClassifyNormalizedFilePath(normalizedPath) is GameAssetFileKind.Avfx
                                                    or GameAssetFileKind.Tmb
                                                    or GameAssetFileKind.Eid;
    }

    private static ObjectResourcePathKind ClassifyResourcePathNormalized(string path)
        => GameAssetPathRules.ClassifyNormalizedFilePath(path) switch
        {
            GameAssetFileKind.Mdl => ObjectResourcePathKind.Model,
            GameAssetFileKind.Sgb => ObjectResourcePathKind.SharedGroup,
            GameAssetFileKind.Tmb => ObjectResourcePathKind.Timeline,
            GameAssetFileKind.Sklb => ObjectResourcePathKind.Sklb,
            GameAssetFileKind.Pap => ObjectResourcePathKind.Pap,
            GameAssetFileKind.Mtrl => ObjectResourcePathKind.Material,
            GameAssetFileKind.Tex => ObjectResourcePathKind.Texture,
            GameAssetFileKind.Shpk => ObjectResourcePathKind.ShaderPackage,
            GameAssetFileKind.Avfx => ObjectResourcePathKind.Vfx,
            GameAssetFileKind.Atex => ObjectResourcePathKind.Atex,
            GameAssetFileKind.Scd => ObjectResourcePathKind.Sound,
            GameAssetFileKind.Eid => ObjectResourcePathKind.Eid,
            _ => ObjectResourcePathKind.Unknown,
        };

    private static bool IsCatalogModelPathNormalized(string path)
        => IsCatalogBgPath(path)
            && GameAssetPathRules.ClassifyNormalizedFilePath(path) == GameAssetFileKind.Mdl;

    private static bool IsCatalogSharedGroupPathNormalized(string path)
        => IsCatalogBgPath(path)
            && GameAssetPathRules.ClassifyNormalizedFilePath(path) == GameAssetFileKind.Sgb;

    private static bool IsCatalogBgPath(string path)
        => !string.IsNullOrWhiteSpace(path)
            && (path.StartsWith("bg/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("bgcommon/", StringComparison.OrdinalIgnoreCase));
}
