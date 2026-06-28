namespace Intoner.Services;

/// <summary> describes the requested primary Intoner window behavior </summary>
internal enum IntonerMainWindowRequestKind
{
    Toggle,
    OpenSettings,
}

/// <summary> requests a primary Intoner window action through the mediator </summary>
/// <param name="Kind">window action to perform</param>
internal readonly record struct IntonerMainWindowRequest(IntonerMainWindowRequestKind Kind)
{
    public static IntonerMainWindowRequest Toggle { get; } = new(IntonerMainWindowRequestKind.Toggle);
    public static IntonerMainWindowRequest OpenSettings { get; } = new(IntonerMainWindowRequestKind.OpenSettings);
}
