using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Interop;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;

namespace Intoner.Objects.UI.Settings;

internal sealed class DrawContext
{
    public DrawContext(
        IObjectConfigurationService configurationService,
        IObjectHousingCullingService housingCullingService,
        IObjectHousingModePolicy housingModePolicy,
        IEditorOverlayTarget? overlayTarget = null)
    {
        ConfigurationService = configurationService;
        HousingCullingService = housingCullingService;
        HousingModePolicy = housingModePolicy;
        OverlayTarget = overlayTarget;
    }

    public IObjectConfigurationService ConfigurationService { get; }

    public IObjectHousingCullingService HousingCullingService { get; }

    public IObjectHousingModePolicy HousingModePolicy { get; }

    public IEditorOverlayTarget? OverlayTarget { get; }
}

