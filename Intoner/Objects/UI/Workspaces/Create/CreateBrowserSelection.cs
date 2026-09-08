using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Services;

namespace Intoner.Objects.UI;

internal sealed class CreateBrowserSelection
{
    private readonly IObjectHousingModePolicy _housingModePolicy;

    public EditorSelectionService LibrarySelection { get; } = new();

    public CreateBrowserSource Source { get; set; }

    public DraftKind Kind { get; private set; } = DraftKind.Furniture;

    public CreateBrowserSelection(IObjectHousingModePolicy housingModePolicy)
    {
        _housingModePolicy = housingModePolicy;
    }

    internal enum CreateBrowserSource
    {
        Catalog,
        Library,
    }

    public void ApplyPresetKind(DraftKind kind)
        => Kind = kind;

    internal void SetDraftKind(DraftKind kind)
    {
        if (Kind == kind)
        {
            return;
        }

        Kind = kind;
        _ = LibrarySelection.TryClear();
    }

    internal void NormalizeDraftKindForHousingMode()
    {
        if (_housingModePolicy.GetState().IsHousingMode && Kind != DraftKind.Furniture)
        {
            SetDraftKind(DraftKind.Furniture);
        }
    }
}
