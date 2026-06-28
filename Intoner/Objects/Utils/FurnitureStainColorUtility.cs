using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;

namespace Intoner.Objects.Utils;

internal readonly record struct FurnitureStainColor(byte StainId, ByteColor Color);

internal static class FurnitureStainColorUtility
{
    public static IReadOnlyList<FurnitureStainColor> CaptureNativeColors(
        IFramework framework,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (framework.IsFrameworkUnloading || ObjectFrameworkUtility.IsGameFrameworkDestroying())
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (framework.IsInFrameworkUpdateThread)
        {
            return CaptureNativeColorsUnsafe();
        }

        return framework
            .RunOnFrameworkThread(CaptureNativeColorsUnsafe)
            .WaitAsync(cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    private static unsafe IReadOnlyList<FurnitureStainColor> CaptureNativeColorsUnsafe()
    {
        List<FurnitureStainColor> colors = new(SharedGroupLayoutInstance.ObjectStainCount - 1);
        for (byte stainIndex = 1; stainIndex < SharedGroupLayoutInstance.ObjectStainCount; stainIndex++)
        {
            ByteColor* nativeColor = SharedGroupLayoutInstance.GetObjectStainColorByIndex(stainIndex);
            if (nativeColor != null)
            {
                colors.Add(new FurnitureStainColor(stainIndex, *nativeColor));
            }
        }

        return colors;
    }
}

