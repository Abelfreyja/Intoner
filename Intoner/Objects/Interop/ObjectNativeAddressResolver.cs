using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Interop;

internal static class ObjectNativeAddressResolver
{
    private const byte CallOpcode = 0xE8;
    private const byte JumpOpcode = 0xE9;

    public static nint TryResolveJmpCallTarget(
        ILogger logger,
        ISigScanner sigScanner,
        ObjectSignatures.JmpCallHookTarget target)
        => TryResolveScannedTextMatch(
            logger,
            sigScanner,
            target.Signature,
            target.Label,
            address => TryResolveJmpCallTarget(logger, sigScanner, address, target.Label));

    public static nint TryResolveJmpCallTarget(
        ILogger logger,
        ISigScanner sigScanner,
        ObjectSignatures.JmpCallAddressTarget target)
        => TryResolveScannedTextMatch(
            logger,
            sigScanner,
            target.Signature,
            target.Label,
            address => TryResolveJmpCallTarget(logger, sigScanner, address, target.Label));

    private static nint TryResolveRelativeBranchTarget(
        ILogger logger,
        ISigScanner sigScanner,
        nint relativeOffsetAddress,
        string label)
    {
        try
        {
            var displacement = Marshal.ReadInt32(relativeOffsetAddress);
            return sigScanner.ResolveRelativeAddress(relativeOffsetAddress + sizeof(int), displacement);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to resolve relative branch target for {Label}", label);
            return nint.Zero;
        }
    }

    private static nint TryResolveJmpCallTarget(
        ILogger logger,
        ISigScanner sigScanner,
        nint jmpCallAddress,
        string label)
    {
        if (!TryValidateJmpCallOpcode(logger, jmpCallAddress, label))
        {
            return nint.Zero;
        }

        return TryResolveRelativeBranchTarget(logger, sigScanner, jmpCallAddress + 1, label);
    }

    private static bool IsJmpCallOpcode(byte opcode)
        => opcode is CallOpcode or JumpOpcode;

    private static bool TryValidateJmpCallOpcode(ILogger logger, nint address, string label)
    {
        try
        {
            var opcode = Marshal.ReadByte(address);
            if (IsJmpCallOpcode(opcode))
            {
                return true;
            }

            logger.LogWarning(
                "failed to resolve JMP/CALL target for {Label} because opcode 0x{Opcode:X2} is not E8/E9",
                label,
                opcode);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to verify JMP/CALL opcode for {Label}", label);
            return false;
        }
    }

    public static nint TryResolveVtableFunction(
        ILogger logger,
        ObjectSignatures.VtableHookTarget target)
    {
        try
        {
            var moduleBaseAddress = Process.GetCurrentProcess().MainModule?.BaseAddress ?? nint.Zero;
            if (moduleBaseAddress == nint.Zero)
            {
                logger.LogWarning("failed to resolve module base for {Label}", target.Label);
                return nint.Zero;
            }

            var vtableAddress = moduleBaseAddress + target.VtableRva;
            var functionAddress = Marshal.ReadIntPtr(vtableAddress + target.VfunctionIndex * nint.Size);
            if (functionAddress == nint.Zero)
            {
                logger.LogWarning("resolved zero vtable function for {Label}", target.Label);
            }

            return functionAddress;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to resolve vtable function for {Label}", target.Label);
            return nint.Zero;
        }
    }

    public static nint TryResolveRipRelativePointerTarget(
        ILogger logger,
        ISigScanner sigScanner,
        ObjectSignatures.RipRelativePointerTarget target)
        => TryResolveScannedTextMatch(
            logger,
            sigScanner,
            target.Signature,
            target.Label,
            instructionAddress => TryResolveRipRelativePointerTarget(logger, sigScanner, instructionAddress, target.Offset, target.Label));

    public static nint TryResolveStaticAddress(
        ILogger logger,
        ISigScanner sigScanner,
        ObjectSignatures.StaticAddressTarget target)
    {
        try
        {
            if (sigScanner.TryGetStaticAddressFromSig(target.Signature, out nint address, target.Offset))
            {
                return address;
            }

            logger.LogWarning("failed to resolve static address for {Label}", target.Label);
            return nint.Zero;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to resolve static address for {Label}", target.Label);
            return nint.Zero;
        }
    }

    public static nint TryResolveRipRelativeAddress(
        ILogger logger,
        ISigScanner sigScanner,
        nint instructionAddress,
        int displacementOffset,
        string label)
    {
        try
        {
            var displacement = Marshal.ReadInt32(instructionAddress + displacementOffset);
            return sigScanner.ResolveRelativeAddress(instructionAddress + displacementOffset + sizeof(int), displacement);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to resolve RIP relative address for {Label}", label);
            return nint.Zero;
        }
    }

    public static nint TryResolveRipRelativePointerTarget(
        ILogger logger,
        ISigScanner sigScanner,
        nint instructionAddress,
        int displacementOffset,
        string label)
    {
        try
        {
            nint pointerAddress = TryResolveRipRelativeAddress(logger, sigScanner, instructionAddress, displacementOffset, label);
            if (pointerAddress == nint.Zero)
            {
                return nint.Zero;
            }

            return Marshal.ReadIntPtr(pointerAddress);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to resolve RIP relative pointer target for {Label}", label);
            return nint.Zero;
        }
    }

    public static nint TryScanSingleTextMatch(
        ILogger logger,
        ISigScanner sigScanner,
        string signature,
        string label)
    {
        try
        {
            var results = sigScanner.ScanAllText(signature);
            switch (results.Length)
            {
                case 0:
                    logger.LogWarning("failed to resolve {Label}", label);
                    return nint.Zero;
                case 1:
                    return results[0];
                default:
                    logger.LogWarning("failed to resolve {Label} because signature had {Count} matches", label, results.Length);
                    return nint.Zero;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to resolve {Label}", label);
            return nint.Zero;
        }
    }

    public static nint TryScanText(
        ILogger logger,
        ISigScanner sigScanner,
        string signature,
        string label)
    {
        try
        {
            if (sigScanner.TryScanText(signature, out nint address))
            {
                return address;
            }

            logger.LogWarning("failed to resolve {Label}", label);
            return nint.Zero;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to resolve {Label}", label);
            return nint.Zero;
        }
    }

    private static nint TryResolveScannedTextMatch(
        ILogger logger,
        ISigScanner sigScanner,
        string signature,
        string label,
        Func<nint, nint> resolve)
    {
        nint address = TryScanSingleTextMatch(logger, sigScanner, signature, label);
        return address == nint.Zero
            ? nint.Zero
            : resolve(address);
    }
}

