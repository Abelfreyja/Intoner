using Dalamud.Interface;
using Intoner.Objects.UI.TitleBar;
using Intoner.Services.Configuration;
using Intoner.Services.Dependencies;
using System.Globalization;
using System.Numerics;

namespace Intoner.Objects.UI.Dependencies;

internal sealed class DependencyIndicatorProvider : IEditorTitleBarIndicatorProvider, IDisposable
{
    private sealed record Snapshot(TitleBarIndicator? Indicator);

    private readonly IDependencyService _dependencies;
    private readonly IIntonerConfigurationService _configurationService;
    private Snapshot _snapshot;

    public DependencyIndicatorProvider(
        IDependencyService dependencies,
        IIntonerConfigurationService configurationService)
    {
        _dependencies = dependencies;
        _configurationService = configurationService;
        _snapshot = BuildSnapshot(dependencies, configurationService);
        _dependencies.Changed += HandleStateChanged;
        _configurationService.ConfigurationChanged += HandleStateChanged;
    }

    public bool TryCreate(TitleBarIndicatorContext context, out TitleBarIndicator indicator)
    {
        _ = context;
        Snapshot current = Volatile.Read(ref _snapshot);
        if (current.Indicator is not { } currentIndicator)
        {
            indicator = default;
            return false;
        }

        indicator = currentIndicator;
        return true;
    }

    public void Dispose()
    {
        _dependencies.Changed -= HandleStateChanged;
        _configurationService.ConfigurationChanged -= HandleStateChanged;
    }

    private static Snapshot BuildSnapshot(
        IDependencyService dependencies,
        IIntonerConfigurationService configurationService)
    {
        if (!configurationService.Current.Ui.ShowDependencyStatusInTitleBar)
        {
            return new Snapshot(null);
        }

        DependencyStatus[] unavailable = dependencies.Statuses
            .Where(static status => !status.IsAvailable)
            .ToArray();
        if (unavailable.Length == 0)
        {
            return new Snapshot(null);
        }

        bool hasRequiredFailure = unavailable.Any(static status =>
            status.Definition.Requirement == DependencyRequirement.Required);
        string countText = unavailable.Length.ToString(CultureInfo.InvariantCulture);
        Vector4 accent = hasRequiredFailure
            ? ThemeColors.DimRed
            : ThemeColors.AccentYellow;

        return new Snapshot(new TitleBarIndicator(
            FontAwesomeIcon.Plug,
            countText,
            countText,
            accent,
            new TitleBarIndicatorTooltip(
                () => DependencyUi.DrawTitleBarTooltip(unavailable, hasRequiredFailure),
                340f),
            TitleBarIndicatorLayout.Counter));
    }

    private void HandleStateChanged()
        => Volatile.Write(ref _snapshot, BuildSnapshot(_dependencies, _configurationService));
}
