using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace Intoner.Objects.Resources;

internal sealed unsafe class ObjectResourceIncRefGuard : IDisposable
{
    private const int HandleTypeOffset = 0x08;
    private const int TypeOffset = 0x0C;
    private const int HashOffset = 0x10;

    private readonly ILogger _logger;
    private readonly ThreadLocal<int> _depth = new(static () => 0);

    private int _loggedReloadBypass;
    private int _loggedReloadWithoutScope;
    private int _loggedScopeBypass;
    private readonly ObjectDisposalState _disposeState = new();

    public ObjectResourceIncRefGuard(ILogger logger)
        => _logger = logger;

    public Scope EnterScope()
    {
        if (IsDisposing)
        {
            return default;
        }

        if (!ObjectThreadLocalUtility.TryRead(_depth, 0, out var depth)
            || !ObjectThreadLocalUtility.TryWrite(_depth, depth + 1))
        {
            return default;
        }

        return new Scope(this);
    }

    public bool ShouldBypassRedirect(
        bool isSync,
        ResourceHandleType* handleType,
        uint* resourceType,
        uint* resourceHash,
        byte* path,
        ObjectGetResourceParameters* getResourceParameters,
        bool hasHandleLock)
    {
        if (IsDisposing)
        {
            return true;
        }

        if (!ObjectThreadLocalUtility.TryRead(_depth, 0, out var depth))
        {
            return true;
        }

        var inScope = depth > 0;
        var hasIncRefReloadArguments = TryGetIncRefReloadArguments(
            isSync,
            handleType,
            resourceType,
            resourceHash,
            path,
            getResourceParameters,
            hasHandleLock,
            out var reload);
        if (!inScope && !hasIncRefReloadArguments)
        {
            return false;
        }

        if (hasIncRefReloadArguments)
        {
            LogIncRefReloadBypass(reload, inScope);
            LogIncRefReloadWithoutScope(reload, inScope);
        }
        else
        {
            LogScopeBypass();
        }

        return true;
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        _depth.Dispose();
    }

    private void Leave()
    {
        if (IsDisposing)
        {
            return;
        }

        if (!ObjectThreadLocalUtility.TryRead(_depth, 0, out var depth))
        {
            return;
        }

        _ = ObjectThreadLocalUtility.TryWrite(_depth, Math.Max(0, depth - 1));
    }

    private bool IsDisposing
        => _disposeState.IsDisposing;

    private static bool TryGetIncRefReloadArguments(
        bool isSync,
        ResourceHandleType* handleType,
        uint* resourceType,
        uint* resourceHash,
        byte* path,
        ObjectGetResourceParameters* getResourceParameters,
        bool hasHandleLock,
        out IncRefReload reload)
    {
        reload = default;
        if (isSync
            || !hasHandleLock
            || getResourceParameters != null
            || handleType == null
            || resourceType == null
            || resourceHash == null)
        {
            return false;
        }

        var handle = (nint)handleType - HandleTypeOffset;
        if (handle != (nint)resourceType - TypeOffset
            || handle != (nint)resourceHash - HashOffset)
        {
            return false;
        }

        reload = new IncRefReload(
            handle,
            (nint)handleType,
            (nint)resourceType,
            (nint)resourceHash,
            (nint)path,
            hasHandleLock,
            getResourceParameters != null);
        return true;
    }

    private void LogIncRefReloadBypass(IncRefReload reload, bool inScope)
    {
        if (Interlocked.Exchange(ref _loggedReloadBypass, 1) == 0)
        {
            _logger.LogDebug(
                "object resource IncRef reload; skipping redirect Handle=0x{Handle:X} HandleType=0x{HandleType:X} Type=0x{Type:X} Hash=0x{Hash:X} Path=0x{Path:X} HasHandleLock={HasHandleLock} HasParameters={HasParameters} InIncRef={InIncRef}",
                (ulong)reload.ResourceHandle,
                (ulong)reload.HandleTypePointer,
                (ulong)reload.TypePointer,
                (ulong)reload.HashPointer,
                (ulong)reload.PathPointer,
                reload.HasHandleLock,
                reload.HasParameters,
                inScope);
        }
    }

    private void LogIncRefReloadWithoutScope(IncRefReload reload, bool inScope)
    {
        if (inScope
            || Interlocked.Exchange(ref _loggedReloadWithoutScope, 1) != 0)
        {
            return;
        }

        _logger.LogWarning(
            "object resource IncRef reload without active scope; using argument layout fallback Handle=0x{Handle:X}",
            (ulong)reload.ResourceHandle);
    }

    private void LogScopeBypass()
    {
        if (Interlocked.Exchange(ref _loggedScopeBypass, 1) == 0)
        {
            _logger.LogDebug("object resource request bypassed during IncRef scope");
        }
    }

    private readonly record struct IncRefReload(
        nint ResourceHandle,
        nint HandleTypePointer,
        nint TypePointer,
        nint HashPointer,
        nint PathPointer,
        bool HasHandleLock,
        bool HasParameters);

    public readonly struct Scope : IDisposable
    {
        private readonly ObjectResourceIncRefGuard? _owner;

        public Scope(ObjectResourceIncRefGuard owner)
            => _owner = owner;

        public void Dispose()
            => _owner?.Leave();
    }
}

