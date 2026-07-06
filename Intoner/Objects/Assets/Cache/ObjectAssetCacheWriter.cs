using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.Assets.Cache;

internal sealed class ObjectAssetCacheWriter : IDisposable
{
    private readonly ILogger<ObjectAssetCacheWriter> _logger;
    private readonly IObjectAssetCacheService _cacheService;
    private readonly Lock _stateLock;
    private readonly Func<bool> _isDisposed;
    private readonly TryGetStateDelegate _tryGetState;
    private Task? _saveTask;
    private bool _saveQueued;
    private int _disposeRequested;

    public ObjectAssetCacheWriter(
        ILogger<ObjectAssetCacheWriter> logger,
        IObjectAssetCacheService cacheService,
        Lock stateLock,
        Func<bool> isDisposed,
        TryGetStateDelegate tryGetState)
    {
        _logger = logger;
        _cacheService = cacheService;
        _stateLock = stateLock;
        _isDisposed = isDisposed;
        _tryGetState = tryGetState;
    }

    public void SaveImmediately(CatalogAssetState state, string successMessage)
    {
        if (state.DirtyCacheSections == ObjectAssetCacheSectionSet.None)
        {
            return;
        }

        ObjectAssetCacheSectionSet sectionsToSave = state.DirtyCacheSections;
        long capturedRevision = state.CacheRevision;
        ObjectAssetCacheSaveBuilder.CaptureData capture = ObjectAssetCacheSaveBuilder.Capture(state, sectionsToSave);
        ObjectAssetCacheSaveRequest saveRequest = ObjectAssetCacheSaveBuilder.BuildRequest(capture);

        try
        {
            _cacheService.Save(saveRequest);
            if (state.CacheRevision == capturedRevision)
            {
                state.DirtyCacheSections &= ~sectionsToSave;
            }

            _logger.LogInformation(successMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to save rebuilt object asset cache immediately");
        }
    }

    public void Schedule()
    {
        if (Volatile.Read(ref _disposeRequested) != 0)
        {
            return;
        }

        lock (_stateLock)
        {
            _saveQueued = true;
            if (_saveTask is { IsCompleted: false })
            {
                return;
            }

            _saveTask = Task.Run(SaveLoop);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
        {
            return;
        }

        Flush();
    }

    private void Flush()
    {
        Task? saveTask;
        lock (_stateLock)
        {
            saveTask = _saveTask;
        }

        try
        {
            saveTask?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to flush object asset cache");
        }

        ObjectAssetCacheSaveWork? saveWork;
        lock (_stateLock)
        {
            saveWork = TryCaptureSaveWorkLocked();
            _saveQueued = false;
            _saveTask = null;
        }

        if (saveWork is null)
        {
            return;
        }

        bool writeSucceeded = SaveWork(saveWork, "failed to flush object asset cache");
        lock (_stateLock)
        {
            ApplySaveResultLocked(saveWork, writeSucceeded);
        }
    }

    private void SaveLoop()
    {
        while (!_isDisposed())
        {
            ObjectAssetCacheSaveWork? saveWork;
            lock (_stateLock)
            {
                saveWork = TryCaptureSaveWorkLocked();
                if (saveWork is null)
                {
                    _saveQueued = false;
                    _saveTask = null;
                    return;
                }

                _saveQueued = false;
            }

            bool writeSucceeded = SaveWork(saveWork, "failed to save object asset cache");
            bool restartAfterFailedSave;

            lock (_stateLock)
            {
                ApplySaveResultLocked(saveWork, writeSucceeded);
                if (!writeSucceeded)
                {
                    restartAfterFailedSave = _saveQueued && !_isDisposed();
                    _saveQueued = false;
                    _saveTask = null;
                }
                else
                {
                    restartAfterFailedSave = false;
                }

                if (writeSucceeded
                 && !_saveQueued
                 && saveWork.State.DirtyCacheSections == ObjectAssetCacheSectionSet.None)
                {
                    _saveTask = null;
                    return;
                }
            }

            if (!writeSucceeded)
            {
                if (restartAfterFailedSave)
                {
                    Schedule();
                }

                return;
            }
        }

        lock (_stateLock)
        {
            _saveTask = null;
        }
    }

    private ObjectAssetCacheSaveWork? TryCaptureSaveWorkLocked()
    {
        if (!_tryGetState(out CatalogAssetState? state)
         || state.DirtyCacheSections == ObjectAssetCacheSectionSet.None)
        {
            return null;
        }

        ObjectAssetCacheSectionSet sectionsToSave = state.DirtyCacheSections;
        long capturedRevision = state.CacheRevision;
        ObjectAssetCacheSaveBuilder.CaptureData capture = ObjectAssetCacheSaveBuilder.Capture(state, sectionsToSave);
        return new ObjectAssetCacheSaveWork(state, sectionsToSave, capturedRevision, capture);
    }

    private bool SaveWork(ObjectAssetCacheSaveWork saveWork, string failureMessage)
    {
        try
        {
            _cacheService.Save(ObjectAssetCacheSaveBuilder.BuildRequest(saveWork.Capture));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, failureMessage);
            return false;
        }
    }

    private static void ApplySaveResultLocked(ObjectAssetCacheSaveWork saveWork, bool writeSucceeded)
    {
        if (writeSucceeded && saveWork.State.CacheRevision == saveWork.CapturedRevision)
        {
            saveWork.State.DirtyCacheSections &= ~saveWork.Sections;
        }
    }

    private sealed record ObjectAssetCacheSaveWork(
        CatalogAssetState State,
        ObjectAssetCacheSectionSet Sections,
        long CapturedRevision,
        ObjectAssetCacheSaveBuilder.CaptureData Capture);

    public delegate bool TryGetStateDelegate([NotNullWhen(true)] out CatalogAssetState? state);
}
