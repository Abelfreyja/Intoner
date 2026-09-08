using System.Runtime.InteropServices;

namespace Intoner.Objects.Runtime;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct ObjectRevisionSnapshot(
    long SceneRevision,
    long PersistentSceneRevision,
    long SavedLayoutsRevision);

/// <summary>
/// Tracks scene revision counters and publishes scene change notifications.
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
    /// Gets the aggregate saved layouts revision.
    /// </summary>
    /// <returns>The current aggregate saved layouts revision.</returns>
    long GetSavedLayoutsRevision();

    /// <summary>
    /// Gets all revision counters from one consistent state snapshot.
    /// </summary>
    /// <returns>The current revision counters.</returns>
    ObjectRevisionSnapshot GetSnapshot();

    /// <summary>
    /// Raised when the composed object scene changes.
    /// </summary>
    event Action SceneChanged;

    /// <summary>
    /// Raised when the persistent local object scene changes.
    /// </summary>
    event Action PersistentSceneChanged;

    /// <summary>
    /// Raised when the saved layout collection changes.
    /// </summary>
    event Action SavedLayoutsChanged;

    /// <summary>
    /// Increments the scene revision counters.
    /// </summary>
    /// <param name="persistentChanged">Whether the persistent-scene revision should also advance.</param>
    void Increment(bool persistentChanged = false);

    /// <summary>
    /// Increments the aggregate saved layouts revision.
    /// </summary>
    void IncrementSavedLayouts();
}

internal sealed class ObjectRevisionTracker : IObjectRevisionTracker
{
    private readonly Lock _stateLock;
    private Action? _sceneChanged;
    private Action? _persistentSceneChanged;
    private Action? _savedLayoutsChanged;
    private long _sceneRevision = 1;
    private long _persistentSceneRevision = 1;
    private long _savedLayoutsRevision = 1;

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

    public long GetSavedLayoutsRevision()
    {
        lock (_stateLock)
        {
            return _savedLayoutsRevision;
        }
    }

    public ObjectRevisionSnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            return new ObjectRevisionSnapshot(
                _sceneRevision,
                _persistentSceneRevision,
                _savedLayoutsRevision);
        }
    }

    public event Action SceneChanged
    {
        add
        {
            lock (_stateLock)
            {
                _sceneChanged += value;
            }
        }
        remove
        {
            lock (_stateLock)
            {
                _sceneChanged -= value;
            }
        }
    }

    public event Action SavedLayoutsChanged
    {
        add
        {
            lock (_stateLock)
            {
                _savedLayoutsChanged += value;
            }
        }
        remove
        {
            lock (_stateLock)
            {
                _savedLayoutsChanged -= value;
            }
        }
    }

    public void Increment(bool persistentChanged = false)
    {
        Action? sceneChanged;
        Action? persistentSceneChanged = null;
        lock (_stateLock)
        {
            _sceneRevision++;
            sceneChanged = _sceneChanged;
            if (persistentChanged)
            {
                _persistentSceneRevision++;
                persistentSceneChanged = _persistentSceneChanged;
            }
        }

        sceneChanged?.Invoke();
        persistentSceneChanged?.Invoke();
    }

    public void IncrementSavedLayouts()
    {
        Action? savedLayoutsChanged;
        lock (_stateLock)
        {
            _savedLayoutsRevision++;
            savedLayoutsChanged = _savedLayoutsChanged;
        }

        savedLayoutsChanged?.Invoke();
    }
}

