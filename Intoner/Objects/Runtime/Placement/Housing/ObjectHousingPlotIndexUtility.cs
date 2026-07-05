namespace Intoner.Objects.Runtime;

internal static class ObjectHousingPlotIndexUtility
{
    public static bool TryConvertNativePlotIndex(int nativePlotIndex, out int plot)
    {
        plot = 0;
        if (nativePlotIndex is < 0 or >= ObjectHousingPlotBasisTable.PlotCount)
        {
            return false;
        }

        plot = nativePlotIndex + 1;
        return true;
    }
}
