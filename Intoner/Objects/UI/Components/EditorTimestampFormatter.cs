using System.Globalization;

namespace Intoner.Objects.UI.Components;

internal static class EditorTimestampFormatter
{
    private const string CompactFormat = "MMM d, HH:mm";
    private const string MinuteFormat = "yyyy-MM-dd HH:mm";
    private const string FullFormat = "yyyy-MM-dd HH:mm:ss";

    public static string FormatCompact(DateTime timestampUtc)
        => FormatLocal(timestampUtc, CompactFormat);

    public static string FormatToMinute(DateTime timestampUtc)
        => FormatLocal(timestampUtc, MinuteFormat);

    public static string FormatFull(DateTime timestampUtc)
        => FormatLocal(timestampUtc, FullFormat);

    private static string FormatLocal(DateTime timestampUtc, string format)
    {
        DateTime utc = DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc);
        return utc.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
    }
}
