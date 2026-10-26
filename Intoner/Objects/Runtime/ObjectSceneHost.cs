using Dalamud.Plugin.Services;
using Intoner.Objects.Filesystem.Layouts;
using Intoner.Objects.Resources;
using Intoner.Scene;
using Intoner.Utils;

namespace Intoner.Objects.Runtime
{
    /// <summary> owns scene updates and subscriptions for one plugin session </summary>
    internal sealed class ObjectSceneHost : IDisposable
    {
        private readonly IFramework                     _framework;
        private readonly IClientState                   _clientState;
        private readonly ISceneLocationService          _location;
        private readonly IObjectLayoutStore             _layouts;
        private readonly IObjectResolvedCollectionStore _collections;
        private readonly IObjectManager                 _objects;
        private readonly IObjectScene                   _scene;
        private readonly IObjectRevisionTracker         _revisions;
        private readonly IObjectLayoutAutoSaveService   _autosave;
        private readonly ObjectFrameworkDiagnostics     _diagnostics;
        private int _pendingLayoutReload;
        private bool _isTransitioning;
        private volatile bool _started;
        private bool _disposed;

        public ObjectSceneHost(IFramework framework, IClientState clientState, ISceneLocationService location,
            IObjectLayoutStore layouts, IObjectResolvedCollectionStore collections, IObjectManager objects,
            IObjectScene scene, IObjectRevisionTracker revisions, IObjectLayoutAutoSaveService autosave,
            ObjectFrameworkDiagnostics diagnostics)
        {
            _framework   = framework;
            _clientState = clientState;
            _location    = location;
            _layouts     = layouts;
            _collections = collections;
            _objects     = objects;
            _scene       = scene;
            _revisions   = revisions;
            _autosave    = autosave;
            _diagnostics = diagnostics;
        }

        public void Start()
            => FrameworkThreadUtility.Run(_framework, StartOnFramework);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (!_started)
            {
                return;
            }

            _started = false;
            FrameworkThreadUtility.RunDuringShutdown(_framework, Stop);
        }

        private void StartOnFramework()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_started)
            {
                return;
            }

            _isTransitioning = _location.IsTransitioning;
            _started = true;
            _framework.Update += HandleFrameworkUpdate;
            _clientState.Logout += HandleLogout;
            _location.TransitionStarted += HandleTransitionStarted;
            _location.LocationInvalidated += HandleLocationInvalidated;
            _layouts.LayoutFilesChanged += HandleLayoutFilesChanged;
            _collections.CollectionChanged += HandleCollectionChanged;

            if (_isTransitioning)
            {
                _scene.HandleZoneSwitchStart();
                _revisions.Increment();
            }
        }

        private void Stop()
        {
            _framework.Update -= HandleFrameworkUpdate;
            _clientState.Logout -= HandleLogout;
            _location.TransitionStarted -= HandleTransitionStarted;
            _location.LocationInvalidated -= HandleLocationInvalidated;
            _layouts.LayoutFilesChanged -= HandleLayoutFilesChanged;
            _collections.CollectionChanged -= HandleCollectionChanged;
        }

        private void HandleFrameworkUpdate(IFramework _)
        {
            if (!_started)
            {
                return;
            }

            using ObjectFrameworkDiagnostics.Scope frame = _diagnostics.BeginFrame();
            using (_diagnostics.Measure(ObjectFrameworkStage.LayoutMaintenance))
            {
                if (Interlocked.Exchange(ref _pendingLayoutReload, 0) != 0)
                {
                    _objects.ReloadSavedLayouts();
                }

                _autosave.FrameworkUpdate();
            }

            _scene.FrameworkUpdate();
        }

        private void HandleLayoutFilesChanged()
            => Interlocked.Exchange(ref _pendingLayoutReload, 1);

        private void HandleTransitionStarted()
        {
            if (!_started || _isTransitioning)
            {
                return;
            }

            _isTransitioning = true;
            _scene.HandleZoneSwitchStart();
            _revisions.Increment();
        }

        private void HandleLocationInvalidated()
        {
            if (!_started || !_isTransitioning)
            {
                return;
            }

            _isTransitioning = false;
            _scene.HandleZoneSwitchEnd();
            _revisions.Increment();
        }

        private void HandleLogout(int type, int code)
        {
            if (_started)
            {
                FrameworkThreadUtility.Run(_framework, HandleLogout);
            }
        }

        private void HandleLogout()
        {
            if (_started)
            {
                _scene.HandleLogout();
                _revisions.Increment();
            }
        }

        private void HandleCollectionChanged(ObjectResolvedCollectionChangedInfo _)
        {
            if (_started)
            {
                _scene.MarkNeedsRefresh();
                _revisions.Increment();
            }
        }
    }
}
