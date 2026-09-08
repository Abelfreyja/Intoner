using Intoner.Objects.Models;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Runtime;

/// <summary>owns temporary source identity, sessions, revisions, and desired state</summary>
internal interface ITemporarySourceStore
{
    /// <summary> gets immutable snapshots for all active temporary sources </summary>
    IReadOnlyList<ObjectTemporarySourceSnapshot> GetSources();

    /// <summary> tries to get one active temporary source </summary>
    bool TryGetSource(string sourceKey, out ObjectTemporarySourceSnapshot source);

    /// <summary> gets the authoritative revision for an active source or removed source tombstone </summary>
    long GetRevision(string sourceKey);

    /// <summary>replaces a complete source and allows a new session to replace the previous session</summary>
    ObjectTemporaryMutationResult TryReplace(
        ObjectTemporarySourceSnapshot source,
        out ObjectTemporarySourceSnapshot? previousSource,
        out ObjectTemporarySourceSnapshot currentSource);

    /// <summary> replaces the mapped object set of one existing source without changing its session </summary>
    ObjectTemporaryMutationResult TryReplaceObjects(
        string sourceKey,
        Guid sessionId,
        string name,
        long revision,
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectSnapshot> runtimeObjects,
        out ObjectTemporarySourceSnapshot currentSource);

    /// <summary> removes one source and retains its revision tombstone </summary>
    ObjectTemporaryMutationResult TryRemove(
        string sourceKey,
        Guid sessionId,
        long revision,
        out ObjectTemporarySourceSnapshot? removedSource);
}

internal sealed class TemporarySourceStore : ITemporarySourceStore
{
    internal const int MaximumRemovedSourceTombstones = 4096;

    private readonly Lock _stateLock;
    private readonly Dictionary<string, ObjectTemporarySourceSnapshot> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ObjectTemporarySourceState> _removedSourceStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string SourceKey, ObjectTemporarySourceState State)> _removedSourceOrder = new();

    public TemporarySourceStore(ObjectStateLock stateLock)
    {
        _stateLock = stateLock.Value;
    }

    public IReadOnlyList<ObjectTemporarySourceSnapshot> GetSources()
    {
        lock (_stateLock)
        {
            return _sources.Values
                .OrderBy(static source => source.UpdatedAtUtc)
                .ThenBy(static source => source.SourceKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public bool TryGetSource(string sourceKey, out ObjectTemporarySourceSnapshot source)
    {
        string normalizedSourceKey = TemporarySourceUtility.NormalizeKey(sourceKey);
        lock (_stateLock)
        {
            if (_sources.TryGetValue(normalizedSourceKey, out ObjectTemporarySourceSnapshot? resolved))
            {
                source = resolved;
                return true;
            }
        }

        source = null!;
        return false;
    }

    public long GetRevision(string sourceKey)
    {
        string normalizedSourceKey = TemporarySourceUtility.NormalizeKey(sourceKey);
        lock (_stateLock)
        {
            return ResolveState(normalizedSourceKey).Revision;
        }
    }

    public ObjectTemporaryMutationResult TryReplace(
        ObjectTemporarySourceSnapshot source,
        out ObjectTemporarySourceSnapshot? previousSource,
        out ObjectTemporarySourceSnapshot currentSource)
    {
        string sourceKey = TemporarySourceUtility.NormalizeKey(source.SourceKey);
        lock (_stateLock)
        {
            ObjectTemporarySourceState currentState = ResolveState(sourceKey);
            if (!TryValidateWrite(sourceKey, source.SessionId, source.Revision, currentState, allowSessionReplacement: true, out ObjectTemporaryMutationResult error))
            {
                previousSource = null;
                currentSource = _sources.GetValueOrDefault(sourceKey)!;
                return error;
            }

            _sources.TryGetValue(sourceKey, out previousSource);
            if (source.SessionId == currentState.SessionId && source.Revision == currentState.Revision)
            {
                currentSource = previousSource!;
                return new ObjectTemporaryMutationResult(
                    previousSource is null
                        ? ObjectTemporaryMutationStatus.StaleRevision
                        : ObjectTemporaryMutationStatus.AlreadyApplied,
                    currentState.Revision);
            }

            currentSource = source with
            {
                SourceKey = sourceKey,
                Name = TemporarySourceUtility.ResolveName(previousSource?.Name, source.Name, sourceKey),
                UpdatedAtUtc = DateTime.UtcNow,
            };
            Commit(currentSource);
            return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.Success, currentSource.Revision);
        }
    }

    public ObjectTemporaryMutationResult TryReplaceObjects(
        string sourceKey,
        Guid sessionId,
        string name,
        long revision,
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectSnapshot> runtimeObjects,
        out ObjectTemporarySourceSnapshot currentSource)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(runtimeObjects);
        string normalizedSourceKey = TemporarySourceUtility.NormalizeKey(sourceKey);
        lock (_stateLock)
        {
            ObjectTemporarySourceState currentState = ResolveState(normalizedSourceKey);
            if (!TryValidateWrite(normalizedSourceKey, sessionId, revision, currentState, allowSessionReplacement: false, out ObjectTemporaryMutationResult error))
            {
                currentSource = _sources.GetValueOrDefault(normalizedSourceKey)!;
                return error;
            }

            if (!_sources.TryGetValue(normalizedSourceKey, out ObjectTemporarySourceSnapshot? previousSource))
            {
                currentSource = null!;
                return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.ObjectNotFound, currentState.Revision);
            }

            if (revision == currentState.Revision)
            {
                currentSource = previousSource;
                return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.AlreadyApplied, currentState.Revision);
            }

            currentSource = previousSource with
            {
                SourceKey = normalizedSourceKey,
                SessionId = sessionId,
                Revision = revision,
                Name = TemporarySourceUtility.ResolveName(previousSource.Name, name, normalizedSourceKey),
                UpdatedAtUtc = DateTime.UtcNow,
                Objects = TemporarySourceUtility.OrderObjects(objects),
                RuntimeObjects = runtimeObjects,
            };
            Commit(currentSource);
            return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.Success, revision);
        }
    }

    public ObjectTemporaryMutationResult TryRemove(
        string sourceKey,
        Guid sessionId,
        long revision,
        out ObjectTemporarySourceSnapshot? removedSource)
    {
        string normalizedSourceKey = TemporarySourceUtility.NormalizeKey(sourceKey);
        lock (_stateLock)
        {
            ObjectTemporarySourceState currentState = ResolveState(normalizedSourceKey);
            if (!TryValidateWrite(normalizedSourceKey, sessionId, revision, currentState, allowSessionReplacement: false, out ObjectTemporaryMutationResult error))
            {
                removedSource = null;
                return error;
            }

            if (!_sources.TryGetValue(normalizedSourceKey, out removedSource))
            {
                if (currentState.SessionId == sessionId && currentState.Revision == revision)
                {
                    return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.AlreadyApplied, revision);
                }

                return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.ObjectNotFound, currentState.Revision);
            }

            if (revision == currentState.Revision)
            {
                removedSource = null;
                return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.StaleRevision, currentState.Revision);
            }

            _sources.Remove(normalizedSourceKey);
            RetainRemovedSourceState(
                normalizedSourceKey,
                new ObjectTemporarySourceState(sessionId, revision));
            return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.Success, revision);
        }
    }

    private ObjectTemporarySourceState ResolveState(string sourceKey)
        => _sources.TryGetValue(sourceKey, out ObjectTemporarySourceSnapshot? source)
            ? new ObjectTemporarySourceState(source.SessionId, source.Revision)
            : _removedSourceStates.GetValueOrDefault(sourceKey);

    private static bool TryValidateWrite(
        string sourceKey,
        Guid sessionId,
        long revision,
        ObjectTemporarySourceState currentState,
        bool allowSessionReplacement,
        out ObjectTemporaryMutationResult error)
    {
        if (sourceKey.Length == 0 || sessionId == Guid.Empty || revision <= 0)
        {
            error = new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.InvalidSource, currentState.Revision);
            return false;
        }

        if (currentState.SessionId != Guid.Empty && sessionId != currentState.SessionId)
        {
            if (allowSessionReplacement)
            {
                error = default;
                return true;
            }

            error = new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.SourceMismatch, currentState.Revision);
            return false;
        }

        if (revision < currentState.Revision)
        {
            error = new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.StaleRevision, currentState.Revision);
            return false;
        }

        error = default;
        return true;
    }

    private void Commit(ObjectTemporarySourceSnapshot source)
    {
        _sources[source.SourceKey] = source;
        _removedSourceStates.Remove(source.SourceKey);
    }

    private void RetainRemovedSourceState(string sourceKey, ObjectTemporarySourceState state)
    {
        _removedSourceStates[sourceKey] = state;
        _removedSourceOrder.Enqueue((sourceKey, state));
        while (_removedSourceOrder.Count > MaximumRemovedSourceTombstones)
        {
            (string expiredSourceKey, ObjectTemporarySourceState expiredState) = _removedSourceOrder.Dequeue();
            if (_removedSourceStates.TryGetValue(expiredSourceKey, out ObjectTemporarySourceState currentState)
                && currentState == expiredState)
            {
                _removedSourceStates.Remove(expiredSourceKey);
            }
        }
    }

}
