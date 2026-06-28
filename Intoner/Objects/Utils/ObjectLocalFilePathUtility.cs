using Dalamud.Utility;
using Penumbra.String.Classes;
using System.Text;

namespace Intoner.Objects.Utils;

internal static class ObjectLocalFilePathUtility
{
    private const string WineRootDrivePrefix = "Z:";

    public static bool IsLocalFilePath(string path)
    {
        string normalizedPath = ObjectStringUtility.TrimOrEmpty(path);
        return normalizedPath.Length > 0
            && (IsWindowsQualifiedPath(normalizedPath)
             || IsUnixAbsolutePath(normalizedPath));
    }

    public static string NormalizeLocalFilePath(string path)
    {
        string normalizedPath = ObjectStringUtility.TrimOrEmpty(path);
        if (normalizedPath.Length == 0)
        {
            return string.Empty;
        }

        if (IsWindowsQualifiedPath(normalizedPath))
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
        => ObjectStringUtility.TrimOrEmpty(path).Replace('/', Path.DirectorySeparatorChar);

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
        if (!Util.IsWine() || !IsUnixAbsolutePath(path))
        {
            return false;
        }

        string winePath = WineRootDrivePrefix + path.Replace('/', '\\');
        return File.Exists(winePath)
            && TryNormalizeWindowsPath(winePath, out normalizedPath);
    }

    private static bool IsWindowsQualifiedPath(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnixAbsolutePath(string path)
        => path.Length > 1
        && path[0] == '/'
        && path[1] != '/'
        && path[1] != '\\';
}

