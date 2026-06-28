using Intoner.Objects.Models;

namespace Intoner.Objects.UI;

internal sealed record ObjectKindInfo
{
    public ObjectKind Kind { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public bool CanCreate { get; init; }
}

