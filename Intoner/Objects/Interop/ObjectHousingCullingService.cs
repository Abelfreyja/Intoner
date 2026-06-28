using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Interop;

/// <summary> controls native housing furnitrue culling behavior </summary>
internal interface IObjectHousingCullingService
{
    /// <summary> whether the we keep furniture slots visible after caching </summary>
    bool DisableFurnitureDisplayCulling { get; }

    /// <summary> whether the current client build resolved the required hook </summary>
    bool IsHookAvailable { get; }

    /// <summary> updates and saves the culling setting </summary>
    /// <param name="enabled">true to keep slots visible</param>
    void SetDisableFurnitureDisplayCulling(bool enabled);
}

internal sealed unsafe class ObjectHousingCullingService : IObjectHousingCullingService, IHostedService, IDisposable
{
    private const int FurnitureCullingEntryCount = 700;
    private const int EntryStride = 0x30;
    private const int EntryStateOffset = 0x58;
    private const int SlotTransitionIndexOffset = 0x8AF8;
    private const int VisibilityThresholdOffset = 0x9070;
    private const int TransitionCountOffset = 0x907A;
    private const ushort EmptyTransitionIndex = 0xFFFF;

    private readonly ILogger<ObjectHousingCullingService> _logger;
    private readonly IObjectConfigurationService _configurationService;
    private readonly IGameInteropProvider _gameInteropProvider;
    private readonly ISigScanner _sigScanner;
    private readonly Lock _hookLock = new();
    private readonly ObjectDisposalState _disposeState = new();

    private Hook<HousingFurnitureCullingUpdateDelegate>? _cullingUpdateHook;
    private bool _disableFurnitureDisplayCulling;
    private bool _hookEnabled;
    private bool _hookResolveFailed;
    private int _loggedApplyFailure;

    private delegate void HousingFurnitureCullingUpdateDelegate(nint cullingTable);

    public ObjectHousingCullingService(
        ILogger<ObjectHousingCullingService> logger,
        IObjectConfigurationService configurationService,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner)
    {
        _logger = logger;
        _configurationService = configurationService;
        _gameInteropProvider = gameInteropProvider;
        _sigScanner = sigScanner;
        _disableFurnitureDisplayCulling = _configurationService.Current.HousingCulling.DisableFurnitureDisplayCulling;
    }

    public bool DisableFurnitureDisplayCulling
        => Volatile.Read(ref _disableFurnitureDisplayCulling);

    public bool IsHookAvailable
        => !Volatile.Read(ref _hookResolveFailed);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (DisableFurnitureDisplayCulling)
        {
            EnableHook();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisableHook();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        lock (_hookLock)
        {
            ObjectInteropHookUtility.DisposeHook(_cullingUpdateHook);
            _cullingUpdateHook = null;
            _hookEnabled = false;
        }
    }

    public void SetDisableFurnitureDisplayCulling(bool enabled)
    {
        Volatile.Write(ref _disableFurnitureDisplayCulling, enabled);
        if (enabled)
        {
            EnableHook();
        }
        else
        {
            DisableHook();
        }

        if (_configurationService.Current.HousingCulling.DisableFurnitureDisplayCulling != enabled)
        {
            _configurationService.Update(
                configuration => configuration.HousingCulling.DisableFurnitureDisplayCulling = enabled);
        }
    }

    private void EnableHook()
    {
        lock (_hookLock)
        {
            if (_disposeState.IsDisposing)
            {
                return;
            }

            if (_hookEnabled)
            {
                return;
            }

            if (!TryEnsureHook())
            {
                return;
            }

            _cullingUpdateHook!.Enable();
            _hookEnabled = true;
        }
    }

    private void DisableHook()
    {
        lock (_hookLock)
        {
            if (!_hookEnabled || _cullingUpdateHook == null)
            {
                return;
            }

            _cullingUpdateHook?.Disable();
            _hookEnabled = false;
        }
    }

    private bool TryEnsureHook()
    {
        if (_cullingUpdateHook != null)
        {
            return true;
        }

        if (_hookResolveFailed)
        {
            return false;
        }

        _cullingUpdateHook = ObjectInteropHookUtility.CreateHook<HousingFurnitureCullingUpdateDelegate>(
            _logger,
            _gameInteropProvider,
            _sigScanner,
            ObjectSignatures.HousingFurnitureCulling,
            HousingFurnitureCullingUpdateDetour);
        if (_cullingUpdateHook != null)
        {
            return true;
        }

        _hookResolveFailed = true;
        _logger.LogWarning("object housing furniture culling hook was unavailable");
        return false;
    }

    private void HousingFurnitureCullingUpdateDetour(nint cullingTable)
    {
        _cullingUpdateHook!.Original(cullingTable);

        if (!DisableFurnitureDisplayCulling || cullingTable == nint.Zero)
        {
            return;
        }

        try
        {
            KeepFurnitureSlotsVisible(cullingTable);
        }
        catch (Exception ex) when (Interlocked.Exchange(ref _loggedApplyFailure, 1) == 0)
        {
            _logger.LogWarning(ex, "failed to apply object housing furniture culling override");
        }
    }

    private static void KeepFurnitureSlotsVisible(nint cullingTable)
    {
        byte* table = (byte*)cullingTable;
        *(float*)(table + VisibilityThresholdOffset) = float.MaxValue;
        table[TransitionCountOffset] = 0;

        for (var slot = 0; slot < FurnitureCullingEntryCount; ++slot)
        {
            table[EntryStateOffset + (slot * EntryStride)] = 0;
            *(ushort*)(table + SlotTransitionIndexOffset + (slot * sizeof(ushort))) = EmptyTransitionIndex;
        }
    }
}

