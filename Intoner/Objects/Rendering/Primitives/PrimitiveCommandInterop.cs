using Dalamud.Plugin.Services;
using Intoner.Objects.Interop;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Rendering.Primitives;

internal unsafe delegate void ContextSetRenderTargetsDelegate(
    nint context,
    int targetCount,
    nint targetSlots,
    nint depthTarget,
    short left,
    short top,
    short right,
    short bottom);

internal unsafe delegate nint ContextAllocateCommandDelegate(nint context, ulong size);

internal unsafe delegate void ContextPushBackCommandDelegate(nint context, nint command);

internal sealed class PrimitiveCommandBindings
{
    private PrimitiveCommandBindings(
        ContextAllocateCommandDelegate allocateCommand,
        ContextPushBackCommandDelegate pushBackCommand)
    {
        AllocateCommand = allocateCommand;
        PushBackCommand = pushBackCommand;
    }

    public ContextAllocateCommandDelegate AllocateCommand { get; }
    public ContextPushBackCommandDelegate PushBackCommand { get; }

    public static PrimitiveCommandBindings? Create(ILogger logger, ISigScanner sigScanner)
    {
        nint allocateAddress = ObjectNativeAddressResolver.TryScanSingleTextMatch(
            logger,
            sigScanner,
            ObjectSignatures.NativeContextAllocateCommand.Signature,
            ObjectSignatures.NativeContextAllocateCommand.Label);
        var allocateCommand = ObjectInteropHookUtility.CreateDelegateFromAddress<ContextAllocateCommandDelegate>(
            logger,
            allocateAddress,
            ObjectSignatures.NativeContextAllocateCommand.Label);
        nint pushBackAddress = ObjectNativeAddressResolver.TryResolveJmpCallTarget(
            logger,
            sigScanner,
            ObjectSignatures.NativeContextPushBackCommand);
        var pushBackCommand = ObjectInteropHookUtility.CreateDelegateFromAddress<ContextPushBackCommandDelegate>(
            logger,
            pushBackAddress,
            ObjectSignatures.NativeContextPushBackCommand.Label);

        return allocateCommand == null || pushBackCommand == null
            ? null
            : new PrimitiveCommandBindings(allocateCommand, pushBackCommand);
    }
}

[StructLayout(LayoutKind.Explicit, Size = 0x20)]
internal unsafe struct NativeCallbackCommand
{
    [FieldOffset(0x00)] public int SwitchType;
    [FieldOffset(0x08)] public delegate* unmanaged<nint, void> Callback;
    [FieldOffset(0x10)] public nint Context;
    [FieldOffset(0x18)] public int Flags;
}

