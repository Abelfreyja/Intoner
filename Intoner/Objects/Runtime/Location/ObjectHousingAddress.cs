using Intoner.Objects.Filesystem.Configuration;

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

    public static bool TryResolveDistrict(uint territoryId, out ObjectHousingDistrict district)
    {
        (bool success, ObjectHousingDistrict resolvedDistrict) = ((HousingTerritory)territoryId) switch
        {
            HousingTerritory.Mist
                or HousingTerritory.MistSmall
                or HousingTerritory.MistMedium
                or HousingTerritory.MistLarge
                or HousingTerritory.MistFCRoom
                or HousingTerritory.MistFCWorkshop
                or HousingTerritory.MistApartment => (true, ObjectHousingDistrict.Mist),
            HousingTerritory.Lavender
                or HousingTerritory.LavenderSmall
                or HousingTerritory.LavenderMedium
                or HousingTerritory.LavenderLarge
                or HousingTerritory.LavenderFCRoom
                or HousingTerritory.LavenderFCWorkshop
                or HousingTerritory.LavenderApartment => (true, ObjectHousingDistrict.LavenderBeds),
            HousingTerritory.Goblet
                or HousingTerritory.GobletSmall
                or HousingTerritory.GobletMedium
                or HousingTerritory.GobletLarge
                or HousingTerritory.GobletFCRoom
                or HousingTerritory.GobletFCWorkshop
                or HousingTerritory.GobletApartment => (true, ObjectHousingDistrict.Goblet),
            HousingTerritory.Shirogane
                or HousingTerritory.ShiroganeSmall
                or HousingTerritory.ShiroganeMedium
                or HousingTerritory.ShiroganeLarge
                or HousingTerritory.ShiroganeFCRoom
                or HousingTerritory.ShiroganeFCWorkshop
                or HousingTerritory.ShiroganeApartment => (true, ObjectHousingDistrict.Shirogane),
            HousingTerritory.Empyreum
                or HousingTerritory.EmpyreumSmall
                or HousingTerritory.EmpyreumMedium
                or HousingTerritory.EmpyreumLarge
                or HousingTerritory.EmpyreumFCRoom
                or HousingTerritory.EmpyreumFCWorkshop
                or HousingTerritory.EmpyreumApartment => (true, ObjectHousingDistrict.Empyreum),
            _ => (false, default),
        };

        district = resolvedDistrict;
        return success;
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

    private enum HousingTerritory : uint
    {
        Mist = 339,
        MistSmall = 282,
        MistMedium = 283,
        MistLarge = 284,
        MistFCRoom = 384,
        MistFCWorkshop = 423,
        MistApartment = 608,

        Lavender = 340,
        LavenderSmall = 342,
        LavenderMedium = 343,
        LavenderLarge = 344,
        LavenderFCRoom = 385,
        LavenderFCWorkshop = 425,
        LavenderApartment = 609,

        Goblet = 341,
        GobletSmall = 345,
        GobletMedium = 346,
        GobletLarge = 347,
        GobletFCRoom = 386,
        GobletFCWorkshop = 424,
        GobletApartment = 610,

        Shirogane = 641,
        ShiroganeSmall = 649,
        ShiroganeMedium = 650,
        ShiroganeLarge = 651,
        ShiroganeFCRoom = 652,
        ShiroganeFCWorkshop = 653,
        ShiroganeApartment = 655,

        Empyreum = 979,
        EmpyreumSmall = 980,
        EmpyreumMedium = 981,
        EmpyreumLarge = 982,
        EmpyreumFCRoom = 983,
        EmpyreumFCWorkshop = 984,
        EmpyreumApartment = 999,
    }
}

