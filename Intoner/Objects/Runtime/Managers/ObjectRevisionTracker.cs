namespace Intoner.Objects.Runtime;

/// <summary>
/// Tracks scene revision counters and publishes persistent-scene change notifications.
/// </summary>
internal interface IObjectRevisionTracker
{
    /// <summary>
    /// Gets the full composed scene revision.
    /// </summary>
    /// <returns>The current scene revision.</returns>
    long GetSceneRevision();

    /// <summary>
    /// Gets the persistent scene revision for standalone objects and the default layout.
    /// </summary>
    /// <returns>The current persistent scene revision.</returns>
    long GetPersistentSceneRevision();

    /// <summary>
    /// Raised when the persistent local object scene changes.
    /// </summary>
    event Action PersistentSceneChanged;

    /// <summary>
    /// Increments the scene revision counters.
    /// </summary>
    /// <param name="persistentChanged">Whether the persistent-scene revision should also advance.</param>
    void Increment(bool persistentChanged = false);
}

internal sealed class ObjectRevisionTracker : IObjectRevisionTracker
{
    private readonly Lock _stateLock;
    private Action? _persistentSceneChanged;
    private long _sceneRevision = 1;
    private long _persistentSceneRevision = 1;

    public ObjectRevisionTracker(ObjectStateLock stateLock)
    {
        _stateLock = stateLock.Value;
    }

    public long GetSceneRevision()
    {
        lock (_stateLock)
        {
            return _sceneRevision;
        }
    }

    public long GetPersistentSceneRevision()
    {
        lock (_stateLock)
        {
            return _persistentSceneRevision;
        }
    }

    public event Action PersistentSceneChanged
    {
        add
        {
            lock (_stateLock)
            {
                _persistentSceneChanged += value;
            }
        }
        remove
        {
            lock (_stateLock)
            {
                _persistentSceneChanged -= value;
            }
        }
    }

    public void Increment(bool persistentChanged = false)
    {
        Action? persistentSceneChanged = null;
        lock (_stateLock)
        {
            _sceneRevision++;
            if (persistentChanged)
            {
                _persistentSceneRevision++;
                persistentSceneChanged = _persistentSceneChanged;
            }
        }

        persistentSceneChanged?.Invoke();
    }
}

