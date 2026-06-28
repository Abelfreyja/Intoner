using Intoner.Objects.Assets;

namespace Intoner.Objects.Utils;

internal static class ObjectResourceCategoryUtility
{
    private const uint InvalidResourceCategory = uint.MaxValue;
    private const uint BgCommonResourceCategory = 1;
    private const uint BgResourceCategory = 2;

    public static bool TryResolveBgModelResourceCategory(string modelPath, out uint resourceCategory)
    {
        string normalizedPath = ObjectPathRules.NormalizeGamePath(modelPath).ToLowerInvariant();
        if (normalizedPath.StartsWith("bgcommon/", StringComparison.Ordinal))
        {
            resourceCategory = BgCommonResourceCategory;
            return true;
        }

        if (!normalizedPath.StartsWith("bg/", StringComparison.Ordinal))
        {
            resourceCategory = InvalidResourceCategory;
            return false;
        }

        resourceCategory = ResolveBgResourceCategory(normalizedPath);
        return resourceCategory != InvalidResourceCategory;
    }

    private static uint ResolveBgResourceCategory(string path)
    {
        if (path.Length <= 3 || path[3] != 'e')
        {
            return BgResourceCategory;
        }

        if (path.Length > 8 && path[6] == '/')
        {
            int category = (path[5] * 0x100) - 0x3000;
            return BuildBgResourceCategory(category, path[7], path[8]);
        }

        if (path.Length > 9 && path[7] == '/')
        {
            int category = ((path[6] + (path[5] * 10)) * 0x100) - 0x21000;
            return BuildBgResourceCategory(category, path[8], path[9]);
        }

        if (path.Length > 4)
        {
            return BuildBgResourceCategory(0x650, path[3], path[4]);
        }

        return InvalidResourceCategory;
    }

    private static uint BuildBgResourceCategory(int category, char segment, char variant)
    {
        int adjustedCategory = category + (segment < 'a' ? -0x300 : -0x610);
        int variantBase = variant < 'a' ? '0' : 'a';
        return (uint)(((adjustedCategory + (variant - variantBase)) << 16) | (int)BgResourceCategory);
    }
}

