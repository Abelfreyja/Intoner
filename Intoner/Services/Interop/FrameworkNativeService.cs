using Dalamud.Plugin.Services;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Services.Interop
{
    /// <summary> starts and releases one native owner on the framework thread </summary>
    internal abstract class FrameworkNativeService : IDisposable, IAsyncDisposable
    {
        private readonly IFramework _framework;
        private readonly Lock _lifecycleLock = new();
        private Task? _startupTask;
        private Task? _disposalTask;
        private int _disposing;

        protected InteropHookCollection Hooks { get; }
        protected bool IsDisposing => Volatile.Read(ref _disposing) != 0;
        protected virtual bool RequiresFrameworkDisposal => true;

        protected FrameworkNativeService(IFramework framework, ILogger logger)
        {
            _framework = framework;
            Hooks = new InteropHookCollection(logger);
        }

        /// <summary> starts once, construction alone does not install hooks! </summary>
        public Task StartAsync()
        {
            lock (_lifecycleLock)
            {
                if (IsDisposing || FrameworkUnloadUtility.IsUnloadActive(_framework))
                {
                    return _startupTask ?? Task.CompletedTask;
                }

                return _startupTask ??= RunOnFrameworkThread(InitializeService);
            }
        }

        [SuppressMessage("Major Code Smell", "S3877", Justification = "Synchronous native teardown requires the framework thread, off thread callers use DisposeAsync")]
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            lock (_lifecycleLock)
            {
                if (_startupTask is not null && !Hooks.IsDisposed && RequiresFrameworkDisposal && !_framework.IsInFrameworkUpdateThread)
                {
                    throw new InvalidOperationException("use DisposeAsync to release native hooks off the framework thread");
                }

                Volatile.Write(ref _disposing, 1);
                DisposeCore();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task disposalTask;
            lock (_lifecycleLock)
            {
                Volatile.Write(ref _disposing, 1);

                if (_disposalTask is null || (_disposalTask.IsCompleted && (_disposalTask.IsFaulted || !Hooks.IsDisposed)))
                {
                    _disposalTask = _startupTask is null || Hooks.IsDisposed || !RequiresFrameworkDisposal
                        ? Invoke(DisposeService)
                        : RunOnFrameworkThread(DisposeService);
                }

                disposalTask = _disposalTask;
            }

            await disposalTask.ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        /// <summary> installs hooks on the framework thread before consumers are activated </summary>
        protected abstract void InitializeCore();

        /// <summary> releases hooks </summary>
        protected virtual void DisposeCore()
            => Hooks.Dispose();

        private void InitializeService()
        {
            lock (_lifecycleLock)
            {
                if (IsDisposing || FrameworkUnloadUtility.IsUnloadActive(_framework))
                {
                    return;
                }

                try
                {
                    InitializeCore();
                }
                catch
                {
                    Volatile.Write(ref _disposing, 1);
                    DisposeCore();
                    throw;
                }
            }
        }

        private void DisposeService()
        {
            lock (_lifecycleLock)
            {
                DisposeCore();
            }
        }

        private Task RunOnFrameworkThread(Action action)
            => _framework.IsInFrameworkUpdateThread ? Invoke(action) : _framework.RunOnFrameworkThread(action);

        private static Task Invoke(Action action)
        {
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }
    }
}
