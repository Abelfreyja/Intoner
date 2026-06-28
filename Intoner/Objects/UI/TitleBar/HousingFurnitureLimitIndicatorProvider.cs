using Dalamud.Interface;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Runtime;
using Intoner.UI;
using System.Globalization;
using System.Numerics;

namespace Intoner.Objects.UI.TitleBar;

internal sealed class HousingFurnitureLimitIndicatorProvider(IObjectHousingModePolicy housingModePolicy) : IEditorTitleBarIndicatorProvider
{
    public bool TryCreate(TitleBarIndicatorContext context, out TitleBarIndicator indicator)
    {
        ObjectHousingModeState state = housingModePolicy.GetState();
        if (!state.IsHousingMode)
        {
            indicator = default;
            return false;
        }

        int count = context.FurnitureCount;
        int limit = state.FurnitureLimit;
        string countText = count.ToString(CultureInfo.InvariantCulture);
        string limitText = limit.ToString(CultureInfo.InvariantCulture);
        string limitLabel = $"{countText} / {limitText}";
        indicator = new TitleBarIndicator(
            FontAwesomeIcon.Home,
            limitLabel,
            limitLabel,
            ResolveAccent(count, limit));
        return true;
    }

    private static Vector4 ResolveAccent(int count, int limit)
    {
        if (limit <= 0 || count > limit)
        {
            return EditorColors.DimRed;
        }

        float ratio = count / (float)limit;
        if (ratio >= 1f)
        {
            return EditorColors.DimRed;
        }

        if (ratio >= 0.90f)
        {
            return EditorColors.AccentYellow;
        }

        return EditorColors.AccentBlue;
    }
}

