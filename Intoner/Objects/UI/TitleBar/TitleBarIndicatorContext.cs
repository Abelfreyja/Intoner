using Intoner.Objects.Models;
using Intoner.Objects.Runtime;

namespace Intoner.Objects.UI.TitleBar;

internal readonly record struct TitleBarIndicatorContext(
    IReadOnlyList<ObjectSnapshot> PlacedObjects,
    int FurnitureCount)
{
    public static TitleBarIndicatorContext Create(IReadOnlyList<ObjectSnapshot> placedObjects)
        => new(placedObjects, HousingFurnitureCounter.Count(placedObjects));
}

