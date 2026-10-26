using Dalamud.Bindings.ImGui;
using Intoner.Objects.Runtime;
using Intoner.Scene;
using System.Globalization;

namespace Intoner.Objects.UI.Components;

internal sealed class SceneLocationEditor
{
    private readonly ISceneLocationService _locations;
    private readonly SceneCreationContext? _housingAddress;

    public readonly SceneWorldSelector Worlds;
    public int Ward;
    public int House;
    public int Room;
    public bool Subdivision;

    public SceneLocationEditor(ISceneLocationService locations, IReadOnlyList<SceneCreationContext> originals)
    {
        _locations = locations;
        Worlds = new(locations, originals.Select(static original => original.Worlds));
        SceneCreationContext first = originals[0];
        _housingAddress = ObjectHousingAddress.TryResolveHousingTerritory(first.Address, out _)
            && originals.All(original => original.Address == first.Address) ? first : null;
        Ward = (int)(_housingAddress?.WardId ?? 1);
        House = (int)(_housingAddress?.HouseId ?? 1);
        Room = (int)(_housingAddress?.RoomId ?? 0);
        Subdivision = _housingAddress?.DivisionId == 2;
    }

    public int RowCount => (_housingAddress switch
    {
        null => 0,
        { HouseId: 0 } => 1,
        { HouseId: 100 } or { RoomId: > 0 } => 3,
        _ => 2,
    }) + 1;

    public bool HousingChanged => _housingAddress is { } original
        && (Ward != original.WardId || House != original.HouseId || Room != original.RoomId
            || original.HouseId == 100 && Subdivision != (original.DivisionId == 2));

    public bool CanApply => (Worlds.Changed || HousingChanged) && !Worlds.HasInvalidSelection && TryResolveHousingAddress(out _);

    public bool TryGetChange(out SceneLocationChange change)
    {
        change = null!;

        if (Worlds.HasInvalidSelection || !TryResolveHousingAddress(out SceneLocationAddress address))
        {
            return false;
        }

        SceneCreationContext? housing = null;

        if (HousingChanged && _housingAddress is { } original)
        {
            string territoryName = address.TerritoryId == original.TerritoryId
                ? original.TerritoryName : _locations.GetTerritoryName(address.TerritoryId);
            housing = original with
            {
                TerritoryId = address.TerritoryId,
                TerritoryName = territoryName,
                DivisionId = address.DivisionId,
                WardId = address.WardId,
                HouseId = address.HouseId,
                RoomId = address.RoomId,
            };
        }

        change = new SceneLocationChange(Worlds.Changed ? Worlds.Value : null, housing);
        return true;
    }

    private bool TryResolveHousingAddress(out SceneLocationAddress address)
    {
        address = _housingAddress?.Address ?? default;

        if (!HousingChanged)
        {
            return true;
        }

        if (Ward < 1 || House < 0 || Room < 0)
        {
            return false;
        }

        uint division = House switch
        {
            0 => address.DivisionId,
            100 => Subdivision ? 2u : 1u,
            > 30 => 2u,
            _ => 1u,
        };
        address = address with { WardId = (uint)Ward, HouseId = (uint)House, RoomId = (uint)Room, DivisionId = division };

        if (!ObjectHousingAddress.TryResolveHousingTerritory(address, out uint territoryId))
        {
            return false;
        }

        address = address with { TerritoryId = territoryId };
        return true;
    }

    public bool Draw()
    {
        using var table = EditorPropertyTable.Begin("objectLocationFields");

        if (!table)
        {
            return false;
        }

        EditorPropertyTable.NextRow("Worlds", originalValue: Worlds.Changed ? Worlds.OriginalLabel : null);
        bool changed = Worlds.Draw("##objectWorlds");

        if (_housingAddress is not { } original)
        {
            return changed;
        }

        EditorPropertyTable.NextRow("Ward (1–30)", originalValue: Ward != original.WardId ? Number(original.WardId) : null);
        changed |= ImGui.InputInt("##housingWard", ref Ward);

        if (original.HouseId == 100)
        {
            string originalDivision = original.DivisionId == 2 ? "Subdivision" : "Main division";
            EditorPropertyTable.NextRow("Subdivision", originalValue: Subdivision != (original.DivisionId == 2) ? originalDivision : null);
            changed |= ImGui.Checkbox("##housingSubdivision", ref Subdivision);
        }
        else if (original.HouseId != 0)
        {
            EditorPropertyTable.NextRow("Plot (1–60)", originalValue: House != original.HouseId ? Number(original.HouseId) : null);
            changed |= ImGui.InputInt("##housingPlot", ref House);

            if (ImGui.IsItemHovered() && TryResolveHousingAddress(out SceneLocationAddress target)
                && target.TerritoryId != original.TerritoryId)
            {
                IntonerTooltip.DrawText("Different interior size. Position stays unchanged.");
            }
        }

        if (original.RoomId != 0)
        {
            EditorPropertyTable.NextRow(original.HouseId == 100 ? "Room (1–90)" : "Room (1–512)",
                originalValue: Room != original.RoomId ? Number(original.RoomId) : null);
            changed |= ImGui.InputInt("##housingRoom", ref Room);
        }

        return changed;
    }

    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);
}
