namespace Intoner.Objects.UI.TitleBar;

/// <summary> builds title bar indicators for the window </summary>
internal sealed class EditorTitleBarIndicatorService
{
    private readonly IReadOnlyList<IEditorTitleBarIndicatorProvider> _providers;
    private readonly List<TitleBarIndicator> _indicators = [];

    public EditorTitleBarIndicatorService(params IEditorTitleBarIndicatorProvider[] providers)
        => _providers = providers;

    public IReadOnlyList<TitleBarIndicator> Build(TitleBarIndicatorContext context)
    {
        _indicators.Clear();
        foreach (IEditorTitleBarIndicatorProvider provider in _providers)
        {
            if (provider.TryCreate(context, out TitleBarIndicator indicator))
            {
                _indicators.Add(indicator);
            }
        }

        return _indicators;
    }
}

/// <summary> provides one optional title bar indicator </summary>
internal interface IEditorTitleBarIndicatorProvider
{
    /// <summary> attempts to build the indicator for the current editor state </summary>
    /// <param name="context">the current indicator input context</param>
    /// <param name="indicator">the resolved indicator when available</param>
    /// <returns>true when the indicator should be drawn</returns>
    bool TryCreate(TitleBarIndicatorContext context, out TitleBarIndicator indicator);
}

