using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Intoner.Services.Interop;

internal readonly record struct NativeDirectSignatureTarget(string Signature, string Label);
internal readonly record struct NativeRelativeBranchTarget(string Signature, string Label);
internal readonly record struct NativeRipRelativePointerTarget(string Signature, int Offset, string Label);
internal readonly record struct NativeResolvedAddressTarget(nint Address, string Label);
internal readonly record struct NativeStaticAddressTarget(string Signature, int Offset, string Label);

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
    {
        if (TryScanSingleTextMatch(logger, sigScanner, target.Signature, target.Label) == nint.Zero)
        {
            return nint.Zero;
        }

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
