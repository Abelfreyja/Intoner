using Intoner.Objects.Utils;
using Intoner.Objects.Assets;

namespace Intoner.Objects.Assets;

internal static class ObjectPathRules
{
    public static bool IsSharedGroupPath(string sharedGroupPath)
        => HasExtension(sharedGroupPath, ".sgb");

    public static bool IsTimelinePath(string timelinePath)
        => HasExtension(timelinePath, ".tmb");

    public static bool IsSklbPath(string sklbPath)
        => HasExtension(sklbPath, ".sklb");

    public static bool IsPapPath(string papPath)
        => HasExtension(papPath, ".pap");

    public static bool IsVfxPath(string vfxPath)
        => HasExtension(vfxPath, ".avfx");

    public static bool IsMaterialPath(string materialPath)
        => HasExtension(materialPath, ".mtrl");

    public static bool IsEidPath(string eidPath)
        => HasExtension(eidPath, ".eid");

    public static bool IsTexturePath(string texturePath)
        => HasExtension(texturePath, ".tex");

    public static bool IsAtexPath(string texturePath)
        => HasExtension(texturePath, ".atex");

    public static bool IsShaderPackagePath(string shaderPackagePath)
        => HasExtension(shaderPackagePath, ".shpk");

    public static bool IsSoundPath(string soundPath)
        => HasExtension(soundPath, ".scd");

    public static string NormalizeGamePath(string path)
        => ObjectStringUtility.TrimOrEmpty(path).Replace('\\', '/').TrimStart('/');

    public static bool TryNormalizeGamePath(string path, out string normalizedPath)
    {
        normalizedPath = NormalizeGamePath(path);
        return normalizedPath.Length > 0;
    }

    public static bool IsCatalogModelPath(string modelPath)
        => !string.IsNullOrWhiteSpace(modelPath)
            && (modelPath.StartsWith("bg/", StringComparison.OrdinalIgnoreCase)
                || modelPath.StartsWith("bgcommon/", StringComparison.OrdinalIgnoreCase))
            && AssetPathClassifier.IsModelPath(modelPath);

    public static bool IsCatalogSharedGroupPath(string sharedGroupPath)
        => !string.IsNullOrWhiteSpace(sharedGroupPath)
            && (sharedGroupPath.StartsWith("bg/", StringComparison.OrdinalIgnoreCase)
                || sharedGroupPath.StartsWith("bgcommon/", StringComparison.OrdinalIgnoreCase))
            && IsSharedGroupPath(sharedGroupPath);

    public static bool IsCatalogTimelinePath(string timelinePath)
        => !string.IsNullOrWhiteSpace(timelinePath)
            && IsTimelinePath(timelinePath);

    public static bool IsBgObjectModelPath(string modelPath)
        => IsCatalogModelPath(modelPath)
            && (modelPath.Contains("/bgparts/", StringComparison.OrdinalIgnoreCase)
                || modelPath.Contains("/evt/", StringComparison.OrdinalIgnoreCase));

    public static AssetPathKind ClassifyPath(string path)
    {
        if (IsCatalogModelPath(path))
        {
            return AssetPathKind.Model;
        }

        if (IsCatalogSharedGroupPath(path))
        {
            return AssetPathKind.SharedGroup;
        }

        if (IsVfxPath(path))
        {
            return AssetPathKind.Vfx;
        }

        if (IsCatalogTimelinePath(path))
        {
            return AssetPathKind.Timeline;
        }

        if (IsMaterialPath(path))
        {
            return AssetPathKind.Material;
        }

        if (IsEidPath(path))
        {
            return AssetPathKind.Eid;
        }

        return AssetPathKind.Unknown;
    }

    public static ObjectResourcePathKind ClassifyObjectResourcePath(string path)
    {
        if (AssetPathClassifier.IsModelPath(path) || HasExtension(path, ".mdl"))
        {
            return ObjectResourcePathKind.Model;
        }

        if (IsSharedGroupPath(path))
        {
            return ObjectResourcePathKind.SharedGroup;
        }

        if (IsTimelinePath(path))
        {
            return ObjectResourcePathKind.Timeline;
        }

        if (IsSklbPath(path))
        {
            return ObjectResourcePathKind.Sklb;
        }

        if (IsPapPath(path))
        {
            return ObjectResourcePathKind.Pap;
        }

        if (IsMaterialPath(path))
        {
            return ObjectResourcePathKind.Material;
        }

        if (IsTexturePath(path))
        {
            return ObjectResourcePathKind.Texture;
        }

        if (IsShaderPackagePath(path))
        {
            return ObjectResourcePathKind.ShaderPackage;
        }

        if (IsVfxPath(path))
        {
            return ObjectResourcePathKind.Vfx;
        }

        if (IsAtexPath(path))
        {
            return ObjectResourcePathKind.Atex;
        }

        if (IsSoundPath(path))
        {
            return ObjectResourcePathKind.Sound;
        }

        if (IsEidPath(path))
        {
            return ObjectResourcePathKind.Eid;
        }

        return ObjectResourcePathKind.Unknown;
    }

    public static bool IsSupportedObjectResourcePath(string path)
        => ClassifyObjectResourcePath(path) != ObjectResourcePathKind.Unknown;

    public static bool TryNormalizeSupportedObjectResourcePath(string path, out string normalizedPath)
    {
        if (TryNormalizeGamePath(path, out normalizedPath)
         && IsSupportedObjectResourcePath(normalizedPath))
        {
            return true;
        }

        normalizedPath = string.Empty;
        return false;
    }

    public static IEnumerable<string> CollectBgModelAnimationResourcePaths(string modelPath)
    {
        string normalizedModelPath = NormalizeGamePath(modelPath);
        if (!AssetPathClassifier.IsModelPath(normalizedModelPath))
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
        => IsCatalogModelPath(path)
         || IsCatalogSharedGroupPath(path)
         || IsVfxPath(path)
         || IsCatalogTimelinePath(path)
         || IsEidPath(path);

    private static bool HasExtension(string path, string extension)
        => !string.IsNullOrWhiteSpace(path)
            && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}

