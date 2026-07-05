namespace Intoner.Objects.Api;

internal sealed record LayoutAttachmentNode<TItem>(TItem Item, int ParentIndex)
    where TItem : class
{
    public bool HasParent
        => ParentIndex >= 0;
}
