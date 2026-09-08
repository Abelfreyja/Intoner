using Intoner.Services.Configuration;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal sealed record ObjectHousingPlotBasis(
    ObjectHousingDistrict District,
    int Plot,
    byte BlockId,
    ObjectHousingSize Size,
    Vector3 Origin,
    float RotationRadians);
