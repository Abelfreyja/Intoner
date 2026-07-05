namespace Intoner.Objects.Api;

internal static class LayoutAttachmentTree
{
    public static IReadOnlyList<LayoutAttachmentNode<TItem>> Flatten<TItem>(
        IEnumerable<TItem?> roots,
        Func<TItem, IEnumerable<TItem?>> selectChildren)
        where TItem : class
    {
        List<LayoutAttachmentNode<TItem>> flattened = [];
        Append(flattened, roots, selectChildren, -1);
        return flattened;
    }

    public static int Count<TItem>(
        IEnumerable<TItem?> roots,
        Func<TItem, IEnumerable<TItem?>> selectChildren)
        where TItem : class
    {
        int count = 0;
        foreach (TItem? root in roots)
        {
            if (root is null)
            {
                continue;
            }

            count++;
            count += Count(selectChildren(root), selectChildren);
        }

        return count;
    }

    private static void Append<TItem>(
        List<LayoutAttachmentNode<TItem>> flattened,
        IEnumerable<TItem?> roots,
        Func<TItem, IEnumerable<TItem?>> selectChildren,
        int parentIndex)
        where TItem : class
    {
        foreach (TItem? root in roots)
        {
            if (root is null)
            {
                continue;
            }

            int nodeIndex = flattened.Count;
            flattened.Add(new LayoutAttachmentNode<TItem>(root, parentIndex));
            Append(flattened, selectChildren(root), selectChildren, nodeIndex);
        }
    }
}
