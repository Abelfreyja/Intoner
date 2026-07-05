using Dalamud.Plugin.Services;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal sealed unsafe class NativePlacementAreaQuery
{
    private const byte InvalidHousingBlockId = byte.MaxValue;

    private readonly NativeAreaContainmentDelegate? _areaContainment;
    private readonly NativeBlockForPositionDelegate? _blockForPosition;

    public NativePlacementAreaQuery(
        ILogger<NativePlacementAreaQuery> logger,
        ISigScanner sigScanner)
    {
        _areaContainment = ObjectInteropHookUtility.CreateDelegate<NativeAreaContainmentDelegate>(
            logger,
            sigScanner,
            ObjectSignatures.NativeHousingPlacementAreaContainment);
        _blockForPosition = ObjectInteropHookUtility.CreateDelegate<NativeBlockForPositionDelegate>(
            logger,
            sigScanner,
            ObjectSignatures.NativeHousingPlacementBlockForPosition);
    }

    public PlacementValidationStatus CheckCurrentPlot(Vector3 position)
    {
        if (_areaContainment is null || !ObjectCollisionSceneQuery.HasScene())
        {
            return PlacementValidationStatus.Unknown;
        }

        Vector3 queryPosition = position;
        return _areaContainment(&queryPosition) != 0
            ? PlacementValidationStatus.Valid
            : PlacementValidationStatus.Invalid;
    }

    public PlacementValidationStatus CheckCurrentBlock(Vector3 position, byte expectedBlockId)
    {
        if (_blockForPosition is null || !ObjectCollisionSceneQuery.HasScene())
        {
            return PlacementValidationStatus.Unknown;
        }

        Vector3 queryPosition = position;
        byte resolvedBlockId = _blockForPosition(&queryPosition);
        if (resolvedBlockId == InvalidHousingBlockId)
        {
            return PlacementValidationStatus.Invalid;
        }

        return resolvedBlockId == expectedBlockId
            ? PlacementValidationStatus.Valid
            : PlacementValidationStatus.Invalid;
    }

    public bool TryResolveBlock(Vector3 position, out byte blockId)
    {
        blockId = 0;
        if (_blockForPosition is null || !ObjectCollisionSceneQuery.HasScene())
        {
            return false;
        }

        Vector3 queryPosition = position;
        byte resolvedBlockId = _blockForPosition(&queryPosition);
        if (resolvedBlockId == InvalidHousingBlockId)
        {
            return false;
        }

        blockId = resolvedBlockId;
        return true;
    }

    private delegate byte NativeAreaContainmentDelegate(Vector3* position);

    private delegate byte NativeBlockForPositionDelegate(Vector3* position);
}
