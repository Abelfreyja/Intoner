using System.Diagnostics;

namespace Intoner.Objects.Assets;

internal static class SqpackIndexFileSystem
{
    public static IEnumerable<string> EnumerateIndexFiles(string sqpackRoot)
        => Directory.EnumerateFiles(sqpackRoot, "*.index", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(sqpackRoot, "*.index2", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => GetRelativeIndexPath(sqpackRoot, path), StringComparer.OrdinalIgnoreCase);

    public static bool TryResolveSqpackRoot(out string sqpackRoot)
    {
        sqpackRoot = string.Empty;

        string? executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        string gameDirectory = executablePath is null
            ? string.Empty
            : Path.GetDirectoryName(executablePath) ?? string.Empty;
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

