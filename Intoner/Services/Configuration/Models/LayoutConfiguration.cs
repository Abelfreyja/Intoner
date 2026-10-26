namespace Intoner.Services.Configuration;

internal sealed class LayoutConfiguration
{
    public required List<Guid> EnabledLayoutIds { get; set; }
    public required Guid? CreationLayoutId { get; set; }

    public static LayoutConfiguration CreateDefault()
        => new()
        {
            EnabledLayoutIds = [],
            CreationLayoutId = null,
        };

    public LayoutConfiguration Copy()
        => new()
        {
            EnabledLayoutIds = [.. EnabledLayoutIds],
            CreationLayoutId = CreationLayoutId,
        };
}
