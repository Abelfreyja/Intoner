using Intoner.Services.Interop;
using Penumbra.String.Classes;
using System.Text;

namespace Intoner.Objects.Utils;

internal static class ObjectLocalFilePathUtility
{
    public static bool IsLocalFilePath(string path)
    {
        string normalizedPath = TextUtility.TrimOrEmpty(path);
        return normalizedPath.Length > 0
            && (Path.IsPathFullyQualified(normalizedPath)
             || WinePathInterop.IsUnixAbsolutePath(normalizedPath));
    }

    public static string NormalizeLocalFilePath(string path)
    {
        string normalizedPath = TextUtility.TrimOrEmpty(path);
        if (normalizedPath.Length == 0)
        {
            return string.Empty;
        }

        if (Path.IsPathFullyQualified(normalizedPath))
        {
            return TryNormalizeWindowsPath(normalizedPath, out string windowsPath)
                ? windowsPath
                : string.Empty;
        }

        return TryNormalizeWineUnixPath(normalizedPath, out string winePath)
            ? winePath
            : string.Empty;
    }

    public static bool TryNormalizeLocalFilePath(string path, out string normalizedPath)
    {
        normalizedPath = NormalizeLocalFilePath(path);
        return normalizedPath.Length > 0;
    }

    public static bool FileExists(string path)
    {
        string normalizedPath = NormalizeLocalFilePath(path);
        return normalizedPath.Length > 0 && File.Exists(ToFileSystemPath(normalizedPath));
    }

    public static string ToFileSystemPath(string path)
        => TextUtility.TrimOrEmpty(path).Replace('/', Path.DirectorySeparatorChar);

    private static bool TryNormalizeWindowsPath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        try
        {
            string fullPath = new FullPath(Path.GetFullPath(path)).InternalName.ToString();
            if (Encoding.UTF8.GetByteCount(fullPath) >= Utf8GamePath.MaxGamePathLength)
            {
                return false;
            }

            normalizedPath = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryNormalizeWineUnixPath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (!WinePathInterop.TryGetWindowsPath(path, out string winePath))
        {
            return false;
        }

        return File.Exists(winePath)
            && TryNormalizeWindowsPath(winePath, out normalizedPath);
    }
}

