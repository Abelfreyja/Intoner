namespace Intoner.Objects.Assets;

internal static class GameAssetPathRules
{
    private static readonly FileExtensionRule[] FileExtensionRules =
    [
        new(".mdl", GameAssetFileKind.Mdl),
        new(".sgb", GameAssetFileKind.Sgb),
        new(".tmb", GameAssetFileKind.Tmb),
        new(".sklb", GameAssetFileKind.Sklb),
        new(".pap", GameAssetFileKind.Pap),
        new(".mtrl", GameAssetFileKind.Mtrl),
        new(".tex", GameAssetFileKind.Tex),
        new(".shpk", GameAssetFileKind.Shpk),
        new(".avfx", GameAssetFileKind.Avfx),
        new(".atex", GameAssetFileKind.Atex),
        new(".scd", GameAssetFileKind.Scd),
        new(".eid", GameAssetFileKind.Eid),
    ];

    public static GameAssetFileKind ClassifyFilePath(string path)
    {
        string normalizedPath = NormalizeGamePath(path);
        return ClassifyNormalizedFilePath(normalizedPath);
    }

    public static GameAssetFileKind ClassifyNormalizedFilePath(string normalizedPath)
    {
        if (normalizedPath.Length == 0)
        {
            return GameAssetFileKind.Unknown;
        }

        foreach (FileExtensionRule rule in FileExtensionRules)
        {
            if (normalizedPath.EndsWith(rule.Extension, StringComparison.OrdinalIgnoreCase))
            {
                return rule.Kind;
            }
        }

        return GameAssetFileKind.Unknown;
    }

    public static bool IsFileKind(string path, GameAssetFileKind kind)
        => kind != GameAssetFileKind.Unknown
            && ClassifyFilePath(path) == kind;

    public static string NormalizeGamePath(string path)
        => string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Replace('\\', '/').TrimStart('/');

    public static bool TryNormalizeGamePath(string path, out string normalizedPath)
    {
        normalizedPath = NormalizeGamePath(path);
        return normalizedPath.Length > 0;
    }

    private readonly record struct FileExtensionRule(string Extension, GameAssetFileKind Kind);
}
