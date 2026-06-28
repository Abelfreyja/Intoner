using Dalamud.Interface;
using Intoner.Objects.Runtime;

namespace Intoner.Objects.UI.TitleBar;

internal sealed class HousingContextIndicatorProvider(IObjectHousingModePolicy housingModePolicy) : IEditorTitleBarIndicatorProvider
{
    public bool TryCreate(TitleBarIndicatorContext context, out TitleBarIndicator indicator)
    {
        ObjectHousingModeState state = housingModePolicy.GetState();
        if (!state.IsHousingMode)
        {
            indicator = default;
            return false;
        }

        string label = HousingTitleBarText.FormatContext(state);
        indicator = new TitleBarIndicator(
            FontAwesomeIcon.Compass,
            label,
            HousingTitleBarText.FormatCompactContext(state),
            EditorColors.AccentPurple);
        return true;
    }
}

