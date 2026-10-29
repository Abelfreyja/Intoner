using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Intoner.Services.Interop;

internal readonly record struct NativeDirectSignatureTarget(string Signature, string Label);
internal readonly record struct NativeRelativeBranchTarget(string Signature, string Label);
internal readonly record struct NativeRipRelativePointerTarget(string Signature, int Offset, string Label);
internal readonly record struct NativeResolvedAddressTarget(nint Address, string Label);
internal readonly record struct NativeStaticAddressTarget(string Signature, int DisplacementOffset, string Label);

internal static class NativeAddressResolver
{
    private static readonly ConditionalWeakTable<ISigScanner, Dictionary<string, nint>> TextMatches = new();

    public static nint TryResolveJmpCallTarget(
        ILogger logger,
        ISigScanner sigScanner,
        NativeRelativeBranchTarget target)
    {
        if (!target.Signature.StartsWith("E8 ", StringComparison.OrdinalIgnoreCase)
            && !target.Signature.StartsWith("E9 ", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("native branch target {Label} must start with E8 or E9", target.Label);
            return nint.Zero;
        }

        if (TryScanSingleTextMatch(logger, sigScanner, target.Signature, target.Label) == nint.Zero)
        {
            return nint.Zero;
        }

        try
        {
            if (sigScanner.TryScanText(target.Signature, out nint address) && address != nint.Zero)
            {
                return address;
            }

            logger.LogWarning("failed to resolve relative branch target for {Label}", target.Label);
            return nint.Zero;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to resolve relative branch target for {Label}", target.Label);
            return nint.Zero;
        }
    }

    public static nint TryResolveStaticAddress(
        ILogger logger,
        ISigScanner sigScanner,
        NativeStaticAddressTarget target)
        => TryResolveScannedTextMatch(
            logger,
            sigScanner,
            target.Signature,
            target.Label,
            address => TryResolveRipRelativeAddress(logger, sigScanner, address, target.DisplacementOffset, target.Label));

    [SuppressMessage("Security", "S6640", Justification = "CS virtual tables require typed unmanaged pointers")]
    public static unsafe T* ReadVirtualTable<T>(ISigScanner sigScanner, T* table) where T : unmanaged
    {
        if (table == null || !sigScanner.IsCopy)
        {
            return table;
        }

        long offset = (nint)table - sigScanner.Module.BaseAddress - sigScanner.RDataSectionOffset;
        return offset < 0 || offset > sigScanner.RDataSectionSize - sizeof(T)
            ? null
            : (T*)(sigScanner.RDataSectionBase + (nint)offset);
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
            nint operandAddress = instructionAddress + displacementOffset;
            nint readAddress = operandAddress;

            if (sigScanner.IsCopy)
            {
                long offset = operandAddress - sigScanner.Module.BaseAddress - sigScanner.TextSectionOffset;

                if (offset < 0 || offset > sigScanner.TextSectionSize - sizeof(int))
                {
                    logger.LogWarning("RIP relative operand for {Label} is outside executable game code", label);
                    return nint.Zero;
                }

                readAddress = sigScanner.TextSectionBase + (nint)offset;
            }

            int displacement = Marshal.ReadInt32(readAddress);
            return sigScanner.ResolveRelativeAddress(operandAddress + sizeof(int), displacement);
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
        NativeRipRelativePointerTarget target)
        => TryResolveScannedTextMatch(
            logger,
            sigScanner,
            target.Signature,
            target.Label,
            instructionAddress => TryResolveRipRelativePointerTarget(logger, sigScanner, instructionAddress, target.Offset, target.Label));

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
        if (!sigScanner.IsCopy)
        {
            return ScanSingleTextMatch(logger, sigScanner, signature, label);
        }

        Dictionary<string, nint> matches = TextMatches.GetOrCreateValue(sigScanner);
        lock (matches)
        {
            if (!matches.TryGetValue(signature, out nint address))
            {
                address = ScanSingleTextMatch(logger, sigScanner, signature, label);
                matches.Add(signature, address);
            }

            return address;
        }
    }

    private static nint ScanSingleTextMatch(ILogger logger, ISigScanner sigScanner, string signature, string label)
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
