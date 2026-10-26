using Intoner.Services.Configuration;
using Intoner.Scene;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Runtime;

internal enum ObjectHousingDistrict
{
    Mist,
    LavenderBeds,
    Goblet,
    Shirogane,
    Empyreum,
}

internal static class ObjectHousingAddress
{
    public static ObjectHousingSize GetSize(ObjectHousingDistrict district, int plot)
    {
        int normalizedPlot = NormalizePlot(plot);
        if (IsLargePlot(district, normalizedPlot))
        {
            return ObjectHousingSize.Large;
        }

        return IsMediumPlot(district, normalizedPlot)
            ? ObjectHousingSize.Medium
            : ObjectHousingSize.Small;
    }

    private static readonly HousingDistrict[] Districts =
    [
        new(ObjectHousingDistrict.Mist, 339, 282, 283, 284, 384, 423, 608),
        new(ObjectHousingDistrict.LavenderBeds, 340, 342, 343, 344, 385, 425, 609),
        new(ObjectHousingDistrict.Goblet, 341, 345, 346, 347, 386, 424, 610),
        new(ObjectHousingDistrict.Shirogane, 641, 649, 650, 651, 652, 653, 655),
        new(ObjectHousingDistrict.Empyreum, 979, 980, 981, 982, 983, 984, 999),
    ];

    public static bool TryResolveDistrict(uint territoryId, out ObjectHousingDistrict district)
    {
        foreach (HousingDistrict candidate in Districts.AsSpan())
        {
            if (candidate.Contains(territoryId))
            {
                district = candidate.District;
                return true;
            }
        }

        district = default;
        return false;
    }

    public static SceneLocationAddress Normalize(SceneLocationAddress location)
    {
        if (location.DivisionId != 0 || location.HouseId is < 1 or > 60)
        {
            return location;
        }

        foreach (HousingDistrict district in Districts)
        {
            if (district.IsHouse(location.TerritoryId) || location.TerritoryId == district.Chamber || location.TerritoryId == district.Workshop)
            {
                return location with { DivisionId = location.HouseId > 30 ? 2u : 1u };
            }
        }

        return location;
    }

    public static bool TryResolveHousingTerritory(SceneLocationAddress location, out uint territoryId)
    {
        territoryId = 0;
        location = Normalize(location);

        if (location.WardId is < 1 or > 30 || location.DivisionId is < 1 or > 2)
        {
            return false;
        }

        foreach (HousingDistrict district in Districts)
        {
            if (location.TerritoryId == district.Outdoor)
            {
                territoryId = district.Outdoor;
                return location.HouseId == 0 && location.RoomId == 0;
            }

            if (location.TerritoryId == district.Apartment)
            {
                if (location.HouseId != 100 || location.RoomId is < 1 or > 90)
                {
                    return false;
                }

                territoryId = district.Apartment;
                return true;
            }

            bool chamber = location.TerritoryId == district.Chamber;
            bool workshop = location.TerritoryId == district.Workshop;

            if (!chamber && !workshop && !district.IsHouse(location.TerritoryId))
            {
                continue;
            }

            if (location.HouseId is < 1 or > 60
                || location.DivisionId != (location.HouseId > 30 ? 2u : 1u)
                || (chamber ? location.RoomId is < 1 or > 512 : location.RoomId != 0))
            {
                return false;
            }

            territoryId = chamber || workshop ? location.TerritoryId : GetSize(district.District, (int)location.HouseId) switch
            {
                ObjectHousingSize.Medium => district.Medium,
                ObjectHousingSize.Large => district.Large,
                _ => district.Small,
            };
            return true;
        }

        return false;
    }

    public static bool CanChangeLocation(SceneLocationAddress current, SceneLocationAddress requested)
    {
        if (current == requested)
        {
            return true;
        }

        foreach (HousingDistrict district in Districts)
        {
            bool sameArea = district.IsHouse(current.TerritoryId) && district.IsHouse(requested.TerritoryId)
                || current.TerritoryId == requested.TerritoryId
                && (current.TerritoryId == district.Apartment || current.TerritoryId == district.Chamber || current.TerritoryId == district.Workshop
                    || current.TerritoryId == district.Outdoor && current.DivisionId == requested.DivisionId);

            if (sameArea)
            {
                return TryResolveHousingTerritory(current, out _)
                    && TryResolveHousingTerritory(requested, out uint territoryId)
                    && territoryId == requested.TerritoryId;
            }
        }

        return false;
    }

    private static int NormalizePlot(int plot)
        => plot > 30
            ? plot - 30
            : plot;

    private static bool IsLargePlot(ObjectHousingDistrict district, int plot)
        => district switch
        {
            ObjectHousingDistrict.Mist         => plot is 2 or 5 or 15,
            ObjectHousingDistrict.LavenderBeds => plot is 28 or 3 or 6,
            ObjectHousingDistrict.Goblet       => plot is 5 or 13 or 30,
            ObjectHousingDistrict.Shirogane    => plot is 30 or 16 or 7,
            ObjectHousingDistrict.Empyreum     => plot is 22 or 30 or 12,
            _                                  => false,
        };

    private static bool IsMediumPlot(ObjectHousingDistrict district, int plot)
        => district switch
        {
            ObjectHousingDistrict.Mist         => plot is 1 or 4 or 6 or 7 or 14 or 29 or 30,
            ObjectHousingDistrict.LavenderBeds => plot is 30 or 27 or 21 or 5 or 1 or 16 or 11,
            ObjectHousingDistrict.Goblet       => plot is 4 or 6 or 11 or 12 or 8 or 25 or 19,
            ObjectHousingDistrict.Shirogane    => plot is 28 or 24 or 19 or 1 or 8 or 13 or 15,
            ObjectHousingDistrict.Empyreum     => plot is 21 or 18 or 17 or 8 or 7 or 2 or 26,
            _                                  => false,
        };

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct HousingDistrict(
        ObjectHousingDistrict District, uint Outdoor, uint Small, uint Medium, uint Large,
        uint Chamber, uint Workshop, uint Apartment)
    {
        public bool IsHouse(uint territoryId)
            => territoryId == Small || territoryId == Medium || territoryId == Large;

        public bool Contains(uint territoryId)
            => IsHouse(territoryId) || territoryId == Outdoor || territoryId == Chamber
            || territoryId == Workshop || territoryId == Apartment;
    }
}
