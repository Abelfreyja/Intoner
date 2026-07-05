using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using Penumbra.GameData.Structs;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Intoner.Objects.Catalog;

internal sealed class FurnitureStainService : IFurnitureStainService, IDisposable
{
    private readonly IFramework _framework;
    private readonly IDataManager _gameData;
    private readonly ObjectWarmupState<IReadOnlyList<FurnitureStainOption>> _warmupState;

    public FurnitureStainService(
        ILogger<FurnitureStainService> logger,
        IFramework framework,
        IDataManager gameData)
    {
        _framework = framework;
        _gameData = gameData;
        _warmupState = new ObjectWarmupState<IReadOnlyList<FurnitureStainOption>>(
            logger,
            cancellationToken => BuildFurnitureStains(_framework, _gameData, cancellationToken),
            "waiting to load furniture stains",
            "building furniture stains",
            "furniture stains ready",
            "furniture stain load failed",
            "failed to build furniture stains in background");
    }

    public bool IsReady
        => _warmupState.IsReady;

    public bool IsLoading
        => _warmupState.IsLoading;

    public bool HasFailed
        => _warmupState.HasFailed;

    public string StatusText
        => _warmupState.StatusText;

    public void EnsureWarmup()
        => _warmupState.EnsureWarmup();

    public bool TryGetStains([NotNullWhen(true)] out IReadOnlyList<FurnitureStainOption>? stains)
        => _warmupState.TryGetValue(out stains);

    public IReadOnlyList<FurnitureStainOption> GetStains()
        => _warmupState.GetValue();

    public bool TryFindNearestStain(byte red, byte green, byte blue, out byte stainId)
    {
        stainId = 0;
        int bestDistance = int.MaxValue;
        foreach (FurnitureStainOption stain in GetStains())
        {
            if (stain.Id == 0)
            {
                continue;
            }

            ByteColor stainColor = ObjectColorUtility.ToByteColor(stain.PreviewColor);
            int distance = ObjectColorUtility.ComputeRgbDistanceSquared(red, green, blue, stainColor.R, stainColor.G, stainColor.B);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            stainId = stain.Id;
        }

        return stainId != 0;
    }

    public bool TryResolveStainColor(byte stainId, out ByteColor color)
    {
        color = default;
        if (stainId == 0)
        {
            return false;
        }

        foreach (FurnitureStainOption stain in GetStains())
        {
            if (stain.Id != stainId)
            {
                continue;
            }

            color = ObjectColorUtility.ToByteColor(stain.PreviewColor);
            return true;
        }

        return false;
    }

    public void Dispose()
        => _warmupState.Dispose();

    private static IReadOnlyList<FurnitureStainOption> BuildFurnitureStains(
        IFramework framework,
        IDataManager gameData,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FurnitureStainColor> nativeStainColors = FurnitureStainColorUtility.CaptureNativeColors(framework, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SheetFurnitureStainOption> sheetStains = BuildSheetFurnitureStains(gameData, cancellationToken);
        List<FurnitureStainOption> stains = new(nativeStainColors.Count + 1)
        {
            new(0, "Default", new Vector4(0f, 0f, 0f, 1f), false),
        };

        foreach (FurnitureStainColor nativeStainColor in nativeStainColors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SheetFurnitureStainOption? match = FindSheetFurnitureStain(nativeStainColor.Color, sheetStains);
            stains.Add(new FurnitureStainOption(
                nativeStainColor.StainId,
                match?.Name ?? $"Object Stain {nativeStainColor.StainId}",
                ObjectColorUtility.ToOpaqueNormalizedColor(nativeStainColor.Color),
                match?.IsMetallic ?? false));
        }

        return stains
            .OrderBy(static stain => stain.Id)
            .ToArray();
    }

    private static IReadOnlyList<SheetFurnitureStainOption> BuildSheetFurnitureStains(
        IDataManager gameData,
        CancellationToken cancellationToken)
    {
        List<SheetFurnitureStainOption> stains = [];
        var sheet = gameData.GetExcelSheet<Lumina.Excel.Sheets.Stain>(gameData.Language);
        if (sheet == null)
        {
            return stains;
        }

        foreach (var row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.RowId == 0 || !row.IsHousingApplicable)
            {
                continue;
            }

            Stain stain = new(row);
            string name = stain.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            stains.Add(new SheetFurnitureStainOption(
                name,
                ObjectColorUtility.ToOpaqueByteColor(stain.R, stain.G, stain.B),
                stain.Gloss));
        }

        return stains;
    }

    private static SheetFurnitureStainOption? FindSheetFurnitureStain(
        ByteColor nativeColor,
        IReadOnlyList<SheetFurnitureStainOption> sheetStains)
    {
        for (int i = 0; i < sheetStains.Count; i++)
        {
            SheetFurnitureStainOption stain = sheetStains[i];
            if (stain.Color.R == nativeColor.R
             && stain.Color.G == nativeColor.G
             && stain.Color.B == nativeColor.B)
            {
                return stain;
            }
        }

        return null;
    }
}

