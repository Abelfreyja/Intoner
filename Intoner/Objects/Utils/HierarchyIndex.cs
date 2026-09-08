namespace Intoner.Objects.Utils;

/// <summary> indexes a rooted hierarchy while preserving the caller's node order </summary>
internal sealed class HierarchyIndex<TId, TNode>
    where TId : notnull
{
    private readonly IReadOnlyDictionary<TId, TNode> _nodes;
    private readonly IReadOnlyDictionary<TId, IReadOnlyList<TNode>> _children;
    private readonly IReadOnlyDictionary<TId, TId> _parents;
    private readonly Func<TNode, TId> _getId;
    private readonly IEqualityComparer<TId> _comparer;

    private HierarchyIndex(
        IReadOnlyDictionary<TId, TNode> nodes,
        IReadOnlyDictionary<TId, IReadOnlyList<TNode>> children,
        IReadOnlyDictionary<TId, TId> parents,
        Func<TNode, TId> getId,
        IEqualityComparer<TId> comparer,
        TId rootId)
    {
        _nodes = nodes;
        _children = children;
        _parents = parents;
        _getId = getId;
        _comparer = comparer;
        RootId = rootId;
    }

    public TId RootId { get; }

    public IReadOnlyList<TNode> Roots
        => GetChildren(RootId);

    public bool Contains(TId id)
        => _nodes.ContainsKey(id);

    public bool TryGetNode(TId id, out TNode node)
        => _nodes.TryGetValue(id, out node!);

    public IReadOnlyList<TNode> GetChildren(TId parentId)
        => _children.GetValueOrDefault(parentId) ?? [];

    public IReadOnlyList<TNode> GetDescendants(TId parentId)
    {
        List<TNode> descendants = [];
        AddDescendants(parentId, descendants);
        return descendants;
    }

    public IReadOnlyList<TNode> GetSubtree(TId id)
    {
        if (!_nodes.TryGetValue(id, out TNode? node))
        {
            return [];
        }

        List<TNode> subtree = [node];
        AddDescendants(id, subtree);
        return subtree;
    }

    public bool IsDescendantOf(TId candidateId, TId ancestorId)
    {
        if (_comparer.Equals(candidateId, ancestorId))
        {
            return false;
        }

        while (_parents.TryGetValue(candidateId, out TId? parentId))
        {
            if (_comparer.Equals(parentId, ancestorId))
            {
                return true;
            }

            candidateId = parentId;
        }

        return false;
    }

    public IReadOnlyDictionary<TId, IReadOnlyList<TValue>> CollectSubtreeValues<TValue>(
        Func<TNode, IEnumerable<TValue>> getDirectValues)
    {
        ArgumentNullException.ThrowIfNull(getDirectValues);
        List<TNode> traversal = [];
        AddDescendants(RootId, traversal);
        Dictionary<TId, IReadOnlyList<TValue>> values = new(_comparer);
        for (int index = traversal.Count - 1; index >= 0; --index)
        {
            TNode node = traversal[index];
            TId id = _getId(node);
            List<TValue> subtreeValues = [.. getDirectValues(node)];
            foreach (TNode child in GetChildren(id))
            {
                subtreeValues.AddRange(values[_getId(child)]);
            }

            values.Add(id, subtreeValues.ToArray());
        }

        return values;
    }

    public static bool TryCreate(
        IEnumerable<TNode> nodes,
        Func<TNode, TId> getId,
        Func<TNode, TId> getParentId,
        TId rootId,
        IEqualityComparer<TId>? comparer,
        out HierarchyIndex<TId, TNode> index,
        out IReadOnlySet<TId> invalidIds)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(getId);
        ArgumentNullException.ThrowIfNull(getParentId);

        comparer ??= EqualityComparer<TId>.Default;
        TNode[] orderedNodes = nodes.ToArray();
        Dictionary<TId, TNode> byId = new(comparer);
        Dictionary<TId, List<TNode>> children = new(comparer);
        Dictionary<TId, TId> parents = new(comparer);
        HashSet<TId> invalid = new(comparer);

        foreach (TNode node in orderedNodes)
        {
            TId id = getId(node);
            if (comparer.Equals(id, rootId) || !byId.TryAdd(id, node))
            {
                _ = invalid.Add(id);
            }
        }

        foreach (TNode node in orderedNodes)
        {
            TId id = getId(node);
            if (invalid.Contains(id))
            {
                continue;
            }

            TId parentId = getParentId(node);
            if (!comparer.Equals(parentId, rootId) && !byId.ContainsKey(parentId))
            {
                _ = invalid.Add(id);
                continue;
            }

            if (!children.TryGetValue(parentId, out List<TNode>? siblings))
            {
                siblings = [];
                children.Add(parentId, siblings);
            }

            siblings.Add(node);
            parents.Add(id, parentId);
        }

        HashSet<TId> reachable = new(comparer);
        AddReachable(rootId, children, getId, invalid, reachable);
        invalid.UnionWith(byId.Keys.Where(id => !reachable.Contains(id)));

        invalidIds = invalid;
        if (invalid.Count > 0)
        {
            index = null!;
            return false;
        }

        Dictionary<TId, IReadOnlyList<TNode>> readOnlyChildren = children.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyList<TNode>)Array.AsReadOnly(entry.Value.ToArray()),
            comparer);
        index = new HierarchyIndex<TId, TNode>(byId, readOnlyChildren, parents, getId, comparer, rootId);
        return true;
    }

    private void AddDescendants(TId parentId, ICollection<TNode> destination)
    {
        Stack<TNode> pending = [];
        IReadOnlyList<TNode> children = GetChildren(parentId);
        for (int index = children.Count - 1; index >= 0; --index)
        {
            pending.Push(children[index]);
        }

        while (pending.TryPop(out TNode? node))
        {
            destination.Add(node);
            IReadOnlyList<TNode> descendants = GetChildren(_getId(node));
            for (int index = descendants.Count - 1; index >= 0; --index)
            {
                pending.Push(descendants[index]);
            }
        }
    }

    private static void AddReachable(
        TId parentId,
        IReadOnlyDictionary<TId, List<TNode>> children,
        Func<TNode, TId> getId,
        IReadOnlySet<TId> invalid,
        ISet<TId> reachable)
    {
        Stack<TNode> pending = [];
        if (children.TryGetValue(parentId, out List<TNode>? childNodes))
        {
            for (int index = childNodes.Count - 1; index >= 0; --index)
            {
                pending.Push(childNodes[index]);
            }
        }

        while (pending.TryPop(out TNode? child))
        {
            TId childId = getId(child);
            if (invalid.Contains(childId) || !reachable.Add(childId))
            {
                continue;
            }

            if (!children.TryGetValue(childId, out List<TNode>? descendants))
            {
                continue;
            }

            for (int index = descendants.Count - 1; index >= 0; --index)
            {
                pending.Push(descendants[index]);
            }
        }
    }
}
