using Intoner.Objects.Assets;
using Intoner.Objects.Resources;
using Intoner.Services.Loading;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.UI
{
    internal sealed class EditorAssetSoundQuery : IAsyncDisposable
    {
        private readonly ObjectAssetDependencyResolver _dependencies;
        private readonly IObjectResolvedCollectionStore _collections;
        private readonly ILogger<EditorAssetSoundQuery> _logger;
        private readonly BackgroundOperation<(SoundKey Key, bool? HasSound)> _soundLookup;
        private readonly Dictionary<SoundKey, (bool? HasSound, long CompletedAt)> _sounds = [];
        private readonly record struct SoundKey(string Path, string CollectionId, long Revision);

        public EditorAssetSoundQuery(ObjectAssetDependencyResolver dependencies,
            IObjectResolvedCollectionStore collections, ILogger<EditorAssetSoundQuery> logger)
        {
            _dependencies = dependencies;
            _collections = collections;
            _logger = logger;
            _soundLookup = new(logger);
        }

        internal bool HasSound(string path, string collectionId = "")
        {
            if (_soundLookup.TryTakeCompleted(out Task<(SoundKey Key, bool? HasSound)>? completed)
                && completed.IsCompletedSuccessfully)
            {
                if (_sounds.Count >= 128)
                {
                    _sounds.Clear();
                }

                _sounds[completed.Result.Key] = (completed.Result.HasSound, Environment.TickCount64);
            }

            path = ObjectResolvedPath.FromGamePath(path).Path;

            if (GameAssetPathRules.ClassifyNormalizedFilePath(path) is not (GameAssetFileKind.Sgb or GameAssetFileKind.Avfx))
            {
                return false;
            }

            ObjectCollectionResolveData? collection = null;

            long revision = 0;

            if (collectionId.Length > 0)
            {
                if (!_collections.TryGetCollection(collectionId, out collection)
                    || collection.GetResourceRevision(path) is not { } resourceRevision)
                {
                    return false;
                }

                revision = resourceRevision;
            }

            SoundKey key = new(path, collection?.CollectionId ?? string.Empty, revision);

            if (_sounds.TryGetValue(key, out var cached)
                && (cached.HasSound.HasValue || Environment.TickCount64 - cached.CompletedAt < ObjectAssetFileUtility.AssetReadRetryDelay.TotalMilliseconds))
            {
                return cached.HasSound == true;
            }

            if (!_soundLookup.IsBusy)
            {
                QueueSoundLookup(key, collection);
            }

            return false;
        }

        private void QueueSoundLookup(SoundKey key, ObjectCollectionResolveData? collection)
            => _soundLookup.TryStart(token =>
            {
                bool? hasSound;
                try
                {
                    hasSound = _dependencies.HasSound(key.Path, collection, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "could not inspect asset sound for {Path}", key.Path);
                    hasSound = null;
                }

                return Task.FromResult((key, hasSound));
            });

        public ValueTask DisposeAsync()
            => _soundLookup.DisposeAsync();
    }
}
