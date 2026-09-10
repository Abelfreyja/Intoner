using Intoner.Objects.Models;
using Intoner.Objects.Runtime;

namespace Intoner.Objects.Api;

/// <summary> validates and saves prepared layout imports on the framework thread </summary>
internal sealed class ObjectLayoutImportService(
    ObjectStateLock stateLock,
    IObjectLayoutManager layoutManager,
    IObjectIdentityService objectIdentityService,
    IObjectRuntimeLocationService locationService,
    IObjectHousingModePolicy housingModePolicy)
{
    public ObjectLayoutTransferResult Apply(ObjectLayoutImportPayload payload)
    {
        if (payload.RequiredLocation is { } requiredLocation)
        {
            ObjectRuntimeLocationContext currentLocation = locationService.GetCurrentContext();
            if (currentLocation.Scope != requiredLocation.Scope
             || currentLocation.Housing.CurrentArea != requiredLocation.Housing.CurrentArea
             || currentLocation.Housing.CurrentSize != requiredLocation.Housing.CurrentSize
             || currentLocation.Housing.PlotBasis != requiredLocation.Housing.PlotBasis)
            {
                return ObjectLayoutTransferResult.Failure("The housing location changed during import. Import the layout again in the target housing area.");
            }
        }

        lock (stateLock.Value)
        {
            if (!housingModePolicy.TryValidateLayout(payload.Snapshots, out string housingModeError))
            {
                return ObjectLayoutTransferResult.Failure(housingModeError);
            }

            if (!objectIdentityService.TryValidateSavedLayoutAddition(payload.Snapshots, out Guid conflictingId))
            {
                return ObjectLayoutTransferResult.Failure($"The imported layout contains object id {conflictingId:D}, which is already owned by another scene source.");
            }

            if (!layoutManager.TryCreateLayout(payload.Name, payload.Snapshots, payload.Folders, out ObjectLayoutSnapshot layout))
            {
                return ObjectLayoutTransferResult.Failure("Failed to save the imported layout.");
            }

            return new ObjectLayoutTransferResult(true, layout, payload.SuccessMessage);
        }
    }
}
