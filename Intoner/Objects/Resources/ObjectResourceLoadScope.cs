using Intoner.Objects.Utils;

namespace Intoner.Objects.Resources;

/// <summary> shared object resource load collection scope </summary>
internal sealed class ObjectResourceLoadScope : IDisposable
{
    private readonly ThreadLocal<string> _activeCollectionId = new(static () => string.Empty);
    private readonly ObjectDisposalState _disposeState = new();

    public ObjectResourceLoadScopeToken EnterCollectionScope(string collectionId)
    {
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        if (normalizedCollectionId.Length == 0
         || !TryReadActiveCollectionId(out string previousCollectionId)
         || !TryWriteActiveCollectionId(normalizedCollectionId))
        {
            return default;
        }

        return new ObjectResourceLoadScopeToken(this, previousCollectionId);
    }

    public bool TryReadActiveCollectionId(out string collectionId)
    {
        collectionId = string.Empty;
        if (_disposeState.IsDisposing)
        {
            return false;
        }

        return ObjectThreadLocalUtility.TryRead(_activeCollectionId, string.Empty, out collectionId);
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        _activeCollectionId.Dispose();
    }

    private bool TryWriteActiveCollectionId(string collectionId)
    {
        if (_disposeState.IsDisposing)
        {
            return false;
        }

        return ObjectThreadLocalUtility.TryWrite(_activeCollectionId, collectionId);
    }

    internal void RestoreCollectionScope(string previousCollectionId)
        => _ = TryWriteActiveCollectionId(previousCollectionId);
}

internal readonly struct ObjectResourceLoadScopeToken : IDisposable
{
    private readonly ObjectResourceLoadScope? _owner;
    private readonly string _previousCollectionId;

    public ObjectResourceLoadScopeToken(ObjectResourceLoadScope owner, string previousCollectionId)
    {
        _owner = owner;
        _previousCollectionId = previousCollectionId;
    }

    public bool IsActive
        => _owner != null;

    public void Dispose()
        => _owner?.RestoreCollectionScope(_previousCollectionId);
}


