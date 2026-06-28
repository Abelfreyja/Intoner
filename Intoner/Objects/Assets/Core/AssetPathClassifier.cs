namespace Intoner.Objects.Assets;

public static class AssetPathClassifier
{
    public static bool IsSoundPath(string path)
        => path.EndsWith("scd", StringComparison.OrdinalIgnoreCase);

    public static bool IsAnimationPath(string path)
        => path.EndsWith("tmb", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("pap", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("sklb", StringComparison.OrdinalIgnoreCase);

    public static bool IsVfxPath(string path)
        => path.EndsWith("atex", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("avfx", StringComparison.OrdinalIgnoreCase);

    public static bool IsPapPath(string path)
        => path.EndsWith(".pap", StringComparison.OrdinalIgnoreCase);

    public static bool IsSklbPath(string path)
        => path.EndsWith(".sklb", StringComparison.OrdinalIgnoreCase);

    public static bool IsTexturePath(string path)
        => path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase);

    public static bool IsModelPath(string path)
        => path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);
}
