using System.Globalization;

namespace Intoner.Objects.Assets;

internal static class SqpackIndexFileSystem
{
    public static IEnumerable<string> EnumerateIndexFiles(string sqpackRoot)
        => Directory.EnumerateFiles(sqpackRoot, "*.index", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(sqpackRoot, "*.index2", SearchOption.AllDirectories))
            .Where(IsCatalogIndexFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => GetRelativeIndexPath(sqpackRoot, path), StringComparer.OrdinalIgnoreCase);

    public static int ParseIndexId(string path)
    {
        string fileName = Path.GetFileName(path);
        int separatorIndex = fileName.IndexOf('.');
        string idText = separatorIndex < 0
            ? fileName
            : fileName[..separatorIndex];
        return int.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int indexId)
            ? indexId
            : -1;
    }

    private static bool IsCatalogIndexFile(string path)
        // common, bgcommon, bg, cut, chara and vfx
        => ParseIndexId(path) >> 16 is 0x00 or 0x01 or 0x02 or 0x03 or 0x04 or 0x08;

    public static bool TryResolveSqpackRoot(out string sqpackRoot)
    {
        sqpackRoot = string.Empty;

        string? gameDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return false;
        }

        sqpackRoot = Path.Combine(gameDirectory, "sqpack");
        return Directory.Exists(sqpackRoot);
    }

    public static string GetRelativeIndexPath(string sqpackRoot, string indexFilePath)
    {
        try
        {
            return Path.GetRelativePath(sqpackRoot, indexFilePath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .ToLowerInvariant();
        }
        catch
        {
            return Path.GetFileName(indexFilePath).ToLowerInvariant();
        }
    }
}
