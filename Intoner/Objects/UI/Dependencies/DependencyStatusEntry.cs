using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Settings;
using Intoner.Objects.UI.Settings.Components;
using Intoner.Services.Dependencies;
using System.Numerics;

namespace Intoner.Objects.UI.Dependencies;

internal sealed class DependencyStatusEntry : ISettingEntry
{
    private readonly IDependencyService _dependencies;
    private readonly string _dependencyId;

    public DependencyStatusEntry(
        IDependencyService dependencies,
        DependencyDefinition dependency)
    {
        _dependencies = dependencies;
        _dependencyId = dependency.Id;
        Definition = new SettingDefinition(
            $"dependency.{dependency.Id}",
            dependency.Name,
            dependency.Description,
            $"{dependency.Keywords} {dependency.Requirement} dependency available unavailable missing disabled incompatible update starting error");
    }

    public SettingDefinition Definition { get; }

    public SettingRowLayout Layout { get; } = new(FullWidth: true);

    public void DrawRow(Vector4 accent, bool prominentControl)
    {
        _ = accent;
        _ = prominentControl;
        DependencyStatus status = _dependencies.GetStatus(_dependencyId);
        DependencyPresentation presentation = DependencyUi.ResolvePresentation(status.State);
        string metadata = DependencyUi.BuildVersionText(status);
        float scale = ImGuiHelpers.GlobalScale;
        float lineHeight = ImGui.GetTextLineHeight();
        float lineGap = 4f * scale;
        float rowHeight = (lineHeight * 2f) + lineGap + (10f * scale);

        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
        ImGui.TableNextColumn();

        Vector2 start = ImGui.GetCursorScreenPos();
        float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);

        EditorIcon.DrawInline(presentation.Icon, presentation.Accent);
        ImGui.SameLine(0f, 8f * scale);
        float textX = ImGui.GetCursorScreenPos().X;
        ImGui.TextUnformatted(Definition.Label);
        RowChrome.DrawTooltip(status.Definition.Description);
        if (!string.IsNullOrEmpty(metadata))
        {
            ImGui.SameLine(0f, 7f * scale);
            ImGui.TextDisabled(metadata);
            RowChrome.DrawTooltip(status.Message);
        }

        ImGui.SameLine(0f, 10f * scale);
        using (ImRaii.PushColor(ImGuiCol.Text, presentation.Accent))
        {
            ImGui.TextUnformatted(presentation.Label);
        }

        RowChrome.DrawTooltip(status.Message);

        float secondLineY = start.Y + lineHeight + lineGap;
        float descriptionWidth = MathF.Max(1f, start.X + width - textX);
        ImGui.SetCursorScreenPos(new Vector2(textX, secondLineY));
        ImGui.TextDisabled(EditorTextUtility.ClipTextToWidth(status.Message, descriptionWidth));
        RowChrome.DrawTooltip(status.Message);

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + rowHeight));
    }
}
