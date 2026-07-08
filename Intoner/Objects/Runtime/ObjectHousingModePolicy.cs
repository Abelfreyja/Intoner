using Intoner.Objects.Assets;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal readonly record struct ObjectHousingModeState(
    ObjectWorkspaceMode Mode,
    ObjectHousingSize Size,
    ObjectHousingArea Area,
    int FurnitureLimit)
{
    public bool IsHousingMode
        => Mode == ObjectWorkspaceMode.Housing;

    public bool TabletopPlacementEnabled
        => IsHousingMode;

    public bool FloatingPlacementProtectionEnabled
        => IsHousingMode;
}

/// <summary>
/// Applies housing mode object restrictions at creation, import, and mutation boundaries.
/// </summary>
internal interface IObjectHousingModePolicy
{
    /// <summary>
    /// Gets the current resolved housing mode state.
    /// </summary>
    /// <returns>the current mode, selected housing size, selected area, and resolved furniture limit.</returns>
    ObjectHousingModeState GetState();

    /// <summary>
    /// Checks whether an object kind is allowed by the current mode.
    /// </summary>
    /// <param name="kind">the object kind to check.</param>
    /// <returns>true when the kind can be used.</returns>
    bool AllowsKind(ObjectKind kind);

    /// <summary>
    /// Checks whether a furniture sgb path is allowed by the current mode.
    /// </summary>
    /// <param name="sharedGroupPath">the furniture sgb path.</param>
    /// <returns>true when the path can be used.</returns>
    bool AllowsFurniturePath(string sharedGroupPath);

    /// <summary>
    /// Validates one snapshot against current mode restrictions.
    /// </summary>
    /// <param name="snapshot">the snapshot to validate.</param>
    /// <param name="errorMessage">the rejection reason when validation fails.</param>
    /// <returns>true when the snapshot is allowed.</returns>
    bool TryValidateSnapshot(ObjectSnapshot snapshot, out string errorMessage);

    /// <summary>
    /// Validates a create request against current mode restrictions and the selected furniture limit.
    /// </summary>
    /// <param name="snapshot">the snapshot being created.</param>
    /// <param name="currentSnapshots">the current scene snapshots that count toward the limit.</param>
    /// <param name="errorMessage">the rejection reason when validation fails.</param>
    /// <returns>true when the create is allowed.</returns>
    bool TryValidateCreate(ObjectSnapshot snapshot, IReadOnlyList<ObjectSnapshot> currentSnapshots, out string errorMessage);

    /// <summary>
    /// Validates a complete layout snapshot list against current mode restrictions.
    /// </summary>
    /// <param name="snapshots">the layout snapshots to validate.</param>
    /// <param name="errorMessage">the rejection reason when validation fails.</param>
    /// <returns>true when the layout is allowed.</returns>
    bool TryValidateLayout(IReadOnlyList<ObjectSnapshot> snapshots, out string errorMessage);
}

internal sealed class ObjectHousingModePolicy(IObjectConfigurationService configurationService) : IObjectHousingModePolicy
{
    private const string IndoorFurniturePrefix = "bgcommon/hou/indoor/";
    private const string OutdoorFurniturePrefix = "bgcommon/hou/outdoor/";

    public ObjectHousingModeState GetState()
    {
        HousingModeConfiguration configuration = configurationService.Current.HousingMode;
        return new ObjectHousingModeState(
            configuration.Mode,
            configuration.Size,
            configuration.Area,
            ResolveFurnitureLimit(configuration.Size, configuration.Area));
    }

    public bool AllowsKind(ObjectKind kind)
        => !GetState().IsHousingMode || kind == ObjectKind.Furniture;

    public bool AllowsFurniturePath(string sharedGroupPath)
    {
        ObjectHousingModeState state = GetState();
        return !state.IsHousingMode || AllowsFurniturePath(state, sharedGroupPath);
    }

    public bool TryValidateSnapshot(ObjectSnapshot snapshot, out string errorMessage)
    {
        ObjectHousingModeState state = GetState();
        return TryValidateSnapshot(snapshot, state, out errorMessage);
    }

    public bool TryValidateCreate(ObjectSnapshot snapshot, IReadOnlyList<ObjectSnapshot> currentSnapshots, out string errorMessage)
    {
        ObjectHousingModeState state = GetState();
        if (!TryValidateSnapshot(snapshot, state, out errorMessage))
        {
            return false;
        }

        if (!state.IsHousingMode || snapshot.Kind != ObjectKind.Furniture)
        {
            return true;
        }

        int currentFurnitureCount = HousingFurnitureCounter.CountAndContains(currentSnapshots, snapshot.Id, out bool snapshotAlreadyCounted);
        int nextFurnitureCount = snapshotAlreadyCounted
            ? currentFurnitureCount
            : currentFurnitureCount + 1;
        if (nextFurnitureCount > state.FurnitureLimit)
        {
            errorMessage = $"Housing mode furniture limit reached ({state.FurnitureLimit}).";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool TryValidateLayout(IReadOnlyList<ObjectSnapshot> snapshots, out string errorMessage)
    {
        ObjectHousingModeState state = GetState();
        if (!state.IsHousingMode)
        {
            errorMessage = string.Empty;
            return true;
        }

        foreach (ObjectSnapshot snapshot in snapshots)
        {
            if (!TryValidateSnapshot(snapshot, state, out errorMessage))
            {
                return false;
            }
        }

        int furnitureCount = HousingFurnitureCounter.Count(snapshots);
        if (furnitureCount > state.FurnitureLimit)
        {
            errorMessage = $"Housing mode layout exceeds the selected furniture limit ({furnitureCount}/{state.FurnitureLimit}).";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private static bool AllowsFurniturePath(ObjectHousingModeState state, string sharedGroupPath)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(sharedGroupPath);
        return state.Area switch
        {
            ObjectHousingArea.Indoor  => normalizedPath.StartsWith(IndoorFurniturePrefix, StringComparison.OrdinalIgnoreCase),
            ObjectHousingArea.Outdoor => normalizedPath.StartsWith(OutdoorFurniturePrefix, StringComparison.OrdinalIgnoreCase),
            _                         => false,
        };
    }

    private static bool TryValidateSnapshot(ObjectSnapshot snapshot, ObjectHousingModeState state, out string errorMessage)
    {
        if (!state.IsHousingMode)
        {
            errorMessage = string.Empty;
            return true;
        }

        if (snapshot.Kind != ObjectKind.Furniture)
        {
            errorMessage = "Housing mode only allows furniture objects.";
            return false;
        }

        if (snapshot.Model is not FurnitureModel furnitureModel
            || string.IsNullOrWhiteSpace(furnitureModel.SharedGroupPath))
        {
            errorMessage = "Housing mode requires a valid housing furniture .sgb path.";
            return false;
        }

        if (!AllowsFurniturePath(state, furnitureModel.SharedGroupPath))
        {
            errorMessage = state.Area == ObjectHousingArea.Indoor
                ? "Housing mode is set to indoor furniture only."
                : "Housing mode is set to outdoor furniture only.";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private static int ResolveFurnitureLimit(ObjectHousingSize size, ObjectHousingArea area)
    {
        if (size == ObjectHousingSize.Apartment)
        {
            return 150;
        }

        return area switch
        {
            ObjectHousingArea.Outdoor => size switch
            {
                ObjectHousingSize.Small     => 40,
                ObjectHousingSize.Medium    => 60,
                ObjectHousingSize.Large     => 80,
                ObjectHousingSize.Apartment => 150,
                _                           => throw new InvalidDataException($"unsupported object housing size '{size}'"),
            },
            ObjectHousingArea.Indoor => size switch
            {
                ObjectHousingSize.Apartment => 150,
                ObjectHousingSize.Small     => 300,
                ObjectHousingSize.Medium    => 450,
                ObjectHousingSize.Large     => 600,
                _                           => throw new InvalidDataException($"unsupported object housing size '{size}'"),
            },
            _ => throw new InvalidDataException($"unsupported object housing area '{area}'"),
        };
    }
}

