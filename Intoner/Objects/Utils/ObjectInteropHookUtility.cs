using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Intoner.Objects.Interop;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Utils;

internal static class ObjectInteropHookUtility
{
    public static Hook<TDelegate>? CreateHook<TDelegate>(
        ILogger logger,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner,
        ObjectSignatures.SignatureHookTarget target,
        TDelegate detour)
        where TDelegate : Delegate
        => CreateHook(logger, gameInteropProvider, sigScanner, target.Signature, detour, target.Label);

    public static Hook<TDelegate>? CreateHook<TDelegate>(
        ILogger logger,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner,
        ObjectSignatures.JmpCallHookTarget target,
        TDelegate detour)
        where TDelegate : Delegate
    {
        nint address = ObjectNativeAddressResolver.TryResolveJmpCallTarget(logger, sigScanner, target);
        return address == nint.Zero
            ? null
            : CreateHookFromAddress(logger, gameInteropProvider, address, detour, target);
    }

    public static Hook<TDelegate>? CreateHook<TDelegate>(
        ILogger logger,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner,
        string signature,
        TDelegate detour,
        string label)
        where TDelegate : Delegate
    {
        nint address = ObjectNativeAddressResolver.TryScanText(logger, sigScanner, signature, label);
        return address == nint.Zero
            ? null
            : CreateHookFromAddress(logger, gameInteropProvider, address, detour, label);
    }

    public static Hook<TDelegate>? CreateHookFromAddress<TDelegate>(
        ILogger logger,
        IGameInteropProvider gameInteropProvider,
        ObjectSignatures.AddressHookTarget target,
        TDelegate detour)
        where TDelegate : Delegate
        => CreateHookFromAddress(logger, gameInteropProvider, target.Address, detour, target.Label);

    public static Hook<TDelegate>? CreateHookFromAddress<TDelegate>(
        ILogger logger,
        IGameInteropProvider gameInteropProvider,
        nint address,
        TDelegate detour,
        ObjectSignatures.RipRelativePointerTarget target)
        where TDelegate : Delegate
        => CreateHookFromAddress(logger, gameInteropProvider, address, detour, target.Label);

    public static Hook<TDelegate>? CreateHookFromAddress<TDelegate>(
        ILogger logger,
        IGameInteropProvider gameInteropProvider,
        nint address,
        TDelegate detour,
        ObjectSignatures.JmpCallHookTarget target)
        where TDelegate : Delegate
        => CreateHookFromAddress(logger, gameInteropProvider, address, detour, target.Label);

    public static Hook<TDelegate>? CreateHookFromAddress<TDelegate>(
        ILogger logger,
        IGameInteropProvider gameInteropProvider,
        nint address,
        TDelegate detour,
        string label)
        where TDelegate : Delegate
    {
        if (!TryValidateExecutableAddress(logger, address, label, "hook"))
        {
            return null;
        }

        try
        {
            return gameInteropProvider.HookFromAddress<TDelegate>(address, detour);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to hook {Label}", label);
            return null;
        }
    }

    public static TDelegate? CreateDelegate<TDelegate>(
        ILogger logger,
        ISigScanner sigScanner,
        ObjectSignatures.SignatureDelegateTarget target)
        where TDelegate : Delegate
        => CreateDelegate<TDelegate>(logger, sigScanner, target.Signature, target.Label);

    public static TDelegate? CreateDelegate<TDelegate>(
        ILogger logger,
        ISigScanner sigScanner,
        string signature,
        string label)
        where TDelegate : Delegate
    {
        nint address = ObjectNativeAddressResolver.TryScanText(logger, sigScanner, signature, label);
        return address == nint.Zero
            ? null
            : CreateDelegateFromAddress<TDelegate>(logger, address, label);
    }

    public static TDelegate? CreateDelegateFromAddress<TDelegate>(
        ILogger logger,
        nint address,
        string label)
        where TDelegate : Delegate
    {
        if (!TryValidateExecutableAddress(logger, address, label, "create delegate for"))
        {
            return null;
        }

        try
        {
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to create delegate for {Label}", label);
            return null;
        }
    }

    public static Action CreateEnableAction<TDelegate>(Hook<TDelegate>? hook)
        where TDelegate : Delegate
        => () => hook?.Enable();

    public static Action CreateDisposeAction<TDelegate>(Hook<TDelegate>? hook)
        where TDelegate : Delegate
        => () => DisposeHook(hook);

    public static void DisposeHook<TDelegate>(Hook<TDelegate>? hook)
        where TDelegate : Delegate
    {
        hook?.Disable();
        hook?.Dispose();
    }

    private static bool TryValidateExecutableAddress(ILogger logger, nint address, string label, string action)
    {
        if (address == nint.Zero)
        {
            return false;
        }

        if (IsExecutableMainModuleAddress(address))
        {
            return true;
        }

        logger.LogWarning(
            "failed to {Action} {Label} because resolved address 0x{Address:X} is outside executable game code",
            action,
            label,
            (ulong)address);
        return false;
    }

    private static bool IsExecutableMainModuleAddress(nint address)
    {
        const ushort DosHeaderMagic = 0x5A4D;
        const uint NtHeaderSignature = 0x00004550;
        const uint ImageScnMemExecute = 0x20000000;

        try
        {
            ProcessModule? mainModule = Process.GetCurrentProcess().MainModule;
            nint moduleBase = mainModule?.BaseAddress ?? nint.Zero;
            int moduleSize = mainModule?.ModuleMemorySize ?? 0;
            if (moduleBase == nint.Zero || moduleSize <= 0 || address < moduleBase || address >= moduleBase + moduleSize)
            {
                return false;
            }

            if ((ushort)Marshal.ReadInt16(moduleBase) != DosHeaderMagic)
            {
                return false;
            }

            long addressRva = address - moduleBase;
            int peHeaderOffset = Marshal.ReadInt32(moduleBase + 0x3C);
            if (peHeaderOffset <= 0 || peHeaderOffset > moduleSize - 0x18)
            {
                return false;
            }

            nint peHeader = moduleBase + peHeaderOffset;
            if ((uint)Marshal.ReadInt32(peHeader) != NtHeaderSignature)
            {
                return false;
            }

            ushort sectionCount = (ushort)Marshal.ReadInt16(moduleBase + peHeaderOffset + 0x06);
            ushort optionalHeaderSize = (ushort)Marshal.ReadInt16(moduleBase + peHeaderOffset + 0x14);
            int sectionTableOffset = peHeaderOffset + 0x18 + optionalHeaderSize;
            int sectionTableSize = sectionCount * 0x28;
            if (sectionCount == 0 || sectionTableOffset <= 0 || sectionTableOffset > moduleSize - sectionTableSize)
            {
                return false;
            }

            nint sectionHeader = moduleBase + sectionTableOffset;

            for (var i = 0; i < sectionCount; ++i)
            {
                nint section = sectionHeader + i * 0x28;
                uint virtualSize = (uint)Marshal.ReadInt32(section + 0x08);
                uint virtualAddress = (uint)Marshal.ReadInt32(section + 0x0C);
                uint rawSize = (uint)Marshal.ReadInt32(section + 0x10);
                uint characteristics = (uint)Marshal.ReadInt32(section + 0x24);
                uint sectionSize = Math.Max(virtualSize, rawSize);
                long sectionStart = virtualAddress;
                long sectionEnd = sectionStart + sectionSize;

                if ((characteristics & ImageScnMemExecute) == 0 || sectionSize == 0)
                {
                    continue;
                }

                if (addressRva >= sectionStart && addressRva < sectionEnd)
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}

