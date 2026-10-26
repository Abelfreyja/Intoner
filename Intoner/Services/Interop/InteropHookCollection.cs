using Dalamud.Hooking;
using Intoner.Logging;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Intoner.Services.Interop
{
    internal sealed class InteropHookCollection(ILogger logger) : IDisposable
    {
        private readonly object _lock = new();
        private readonly List<(IDalamudHook Hook, Action Enable, Action Disable)> _hooks = [];
        private int _activeCallbacks;
        private bool _enabled;
        private bool _activationFailed;
        private bool _disposed;
        private bool _hooksDisposed;

        public bool IsDisposed
        {
            get
            {
                lock (_lock)
                {
                    return _hooksDisposed;
                }
            }
        }

        public Hook<TDelegate>? Add<TDelegate>(Hook<TDelegate>? hook) where TDelegate : Delegate
            => Add(hook, static value => value.Enable(), static value => value.Disable());

        public T? Add<T>(T? hook, Action<T> enable, Action<T> disable) where T : class, IDalamudHook
        {
            if (hook is null)
            {
                return null;
            }

            lock (_lock)
            {
                try
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);

                    if (_enabled || _activationFailed)
                    {
                        throw new InvalidOperationException("hooks must be registered before activation");
                    }

                    _hooks.Add((hook, () => enable(hook), () => disable(hook)));
                    return hook;
                }
                catch
                {
                    InteropHookUtility.DisposeHook(logger, hook);
                    throw;
                }
            }
        }

        public bool Enable()
        {
            lock (_lock)
            {
                if (_disposed || _activationFailed)
                {
                    return false;
                }

                if (!_enabled)
                {
                    try
                    {
                        foreach (var entry in _hooks)
                        {
                            entry.Enable();
                        }

                        _enabled = true;
                    }
                    catch
                    {
                        _activationFailed = true;
                        DisableHooks();
                        throw;
                    }
                }

                return true;
            }
        }

        public CallbackScope EnterCallback()
            => EnterScope(operation: false);

        public CallbackScope EnterOperation()
            => EnterScope(operation: true);

        private CallbackScope EnterScope(bool operation)
        {
            lock (_lock)
            {
                if (_hooksDisposed || operation && (_disposed || _activationFailed))
                {
                    return default;
                }

                ++_activeCallbacks;
                return new CallbackScope(this);
            }
        }

        [SuppressMessage("Major Code Smell", "S3877", Justification = "Owners must detect failed native hook cleanup before releasing callback state")]
        public void Dispose()
        {
            lock (_lock)
            {
                if (_hooksDisposed)
                {
                    return;
                }

                _disposed = true;

                while (_activeCallbacks != 0)
                {
                    Monitor.Wait(_lock);
                }

                DisableHooks();

                while (_activeCallbacks != 0)
                {
                    Monitor.Wait(_lock);
                }

                for (int index = _hooks.Count - 1; index >= 0; --index)
                {
                    if (InteropHookUtility.DisposeHook(logger, _hooks[index].Hook))
                    {
                        _hooks.RemoveAt(index);
                    }
                }

                _hooksDisposed = _hooks.Count == 0;

                if (!_hooksDisposed)
                {
                    throw new InvalidOperationException("native hooks could not be disposed");
                }
            }
        }

        private void DisableHooks()
        {
            for (int index = _hooks.Count - 1; index >= 0; --index)
            {
                try
                {
                    _hooks[index].Disable();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write(() => logger.LogError(ex, "failed to disable native hook"));
                }
            }
        }

        private void ExitCallback()
        {
            lock (_lock)
            {
                --_activeCallbacks;

                if (_disposed && _activeCallbacks == 0)
                {
                    Monitor.PulseAll(_lock);
                }
            }
        }

        [StructLayout(LayoutKind.Auto)]
        public readonly struct CallbackScope(InteropHookCollection owner) : IDisposable
        {
            public bool IsActive => owner is not null;

            public void Dispose()
                => owner?.ExitCallback();
        }
    }
}