using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Intoner.Objects.Utils;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Resources;

internal sealed unsafe class ObjectResourceIncRefGuard : IDisposable
{
    private static readonly nint HandleTypeOffset = Marshal.OffsetOf<ResourceHandle>(nameof(ResourceHandle.Type));

    private readonly ILogger _logger;
    private readonly ThreadLocal<int> _depth = new(static () => 0);

    private int _loggedReloadBypass;
    private int _loggedReloadWithoutScope;
    private int _loggedScopeBypass;
    private readonly DisposalState _disposeState = new();

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
            getResourceParameters,
            hasHandleLock,
            out nint resourceHandle);
        if (!inScope && !hasIncRefReloadArguments)
        {
            return false;
        }

        if (hasIncRefReloadArguments)
        {
            LogIncRefReloadBypass(resourceHandle, (nint)path, inScope);
            LogIncRefReloadWithoutScope(resourceHandle, inScope);
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
        ObjectGetResourceParameters* getResourceParameters,
        bool hasHandleLock,
        out nint resourceHandle)
    {
        resourceHandle = nint.Zero;
        if (isSync
            || !hasHandleLock
            || getResourceParameters != null
            || handleType == null
            || resourceType == null
            || resourceHash == null)
        {
            return false;
        }

        ResourceHandle* handle = (ResourceHandle*)((byte*)handleType - HandleTypeOffset);
        if (&handle->FileType != resourceType || &handle->Id != resourceHash)
        {
            return false;
        }

        resourceHandle = (nint)handle;
        return true;
    }

    private void LogIncRefReloadBypass(nint resourceHandle, nint path, bool inScope)
    {
        if (Interlocked.Exchange(ref _loggedReloadBypass, 1) == 0)
        {
            ObjectResourceLog.Write(() => _logger.LogDebug(
                "object resource IncRef reload; skipping redirect Handle=0x{Handle:X} HandleType=0x{HandleType:X} Type=0x{Type:X} Hash=0x{Hash:X} Path=0x{Path:X} HasHandleLock={HasHandleLock} HasParameters={HasParameters} InIncRef={InIncRef}",
                (ulong)resourceHandle,
                (ulong)(&((ResourceHandle*)resourceHandle)->Type),
                (ulong)(&((ResourceHandle*)resourceHandle)->FileType),
                (ulong)(&((ResourceHandle*)resourceHandle)->Id),
                (ulong)path,
                true,
                false,
                inScope));
        }
    }

    private void LogIncRefReloadWithoutScope(nint resourceHandle, bool inScope)
    {
        if (inScope
            || Interlocked.Exchange(ref _loggedReloadWithoutScope, 1) != 0)
        {
            return;
        }

        ObjectResourceLog.Write(() => _logger.LogWarning(
            "object resource IncRef reload without active scope; using argument layout fallback Handle=0x{Handle:X}",
            (ulong)resourceHandle));
    }

    private void LogScopeBypass()
    {
        if (Interlocked.Exchange(ref _loggedScopeBypass, 1) == 0)
        {
            ObjectResourceLog.Write(() => _logger.LogDebug("object resource request bypassed during IncRef scope"));
        }
    }

    public readonly struct Scope : IDisposable
    {
        private readonly ObjectResourceIncRefGuard? _owner;

        public Scope(ObjectResourceIncRefGuard owner)
            => _owner = owner;

        public void Dispose()
            => _owner?.Leave();
    }
}
