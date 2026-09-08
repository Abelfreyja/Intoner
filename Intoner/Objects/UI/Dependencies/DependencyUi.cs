using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Services.Dependencies;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI.Dependencies;

internal readonly record struct DependencyPresentation(
    FontAwesomeIcon Icon,
    string Label,
    Vector4 Accent);

internal static class DependencyUi
{
    public static DependencyPresentation ResolvePresentation(DependencyState state)
        => new(ResolveIcon(state), ResolveLabel(state), ResolveAccent(state));

    public static FontAwesomeIcon ResolveIcon(DependencyState state)
        => state switch
        {
            DependencyState.Available    => FontAwesomeIcon.CheckCircle,
            DependencyState.Incompatible => FontAwesomeIcon.ExclamationTriangle,
            DependencyState.NotReady     => FontAwesomeIcon.Clock,
            DependencyState.Unknown      => FontAwesomeIcon.Clock,
            _                            => FontAwesomeIcon.TimesCircle,
        };

    public static string ResolveLabel(DependencyState state)
        => state switch
        {
            DependencyState.Available       => "Connected",
            DependencyState.Missing         => "Not Installed",
            DependencyState.Disabled        => "Not Running",
            DependencyState.FeatureDisabled => "Mod Loading Off",
            DependencyState.Incompatible    => "Update Needed",
            DependencyState.NotReady        => "Starting",
            DependencyState.Error           => "Connection Failed",
            _                               => "Checking",
        };

    public static Vector4 ResolveAccent(DependencyState state)
        => state switch
        {
            DependencyState.Available    => ThemeColors.AccentGreen,
            DependencyState.Incompatible => ThemeColors.DimRed,
            DependencyState.NotReady     => ThemeColors.AccentBlue,
            DependencyState.Error        => ThemeColors.DimRed,
            DependencyState.Unknown      => ThemeColors.TextDisabled,
            _                            => ThemeColors.AccentYellow,
        };

    public static string BuildVersionText(DependencyStatus status)
    {
        if (status.PluginVersion is null)
        {
            return string.Empty;
        }

        string pluginText = $"v{status.PluginVersion}";
        return status.ApiVersion.HasValue
            ? $"{pluginText} | API {status.ApiVersion.Value}"
            : pluginText;
    }

    public static void DrawTitleBarTooltip(
        IReadOnlyList<DependencyStatus> statuses,
        bool hasRequiredFailure)
    {
        int count = statuses.Count;
        if (count == 0)
        {
            return;
        }

        Vector4 accent = hasRequiredFailure
            ? ThemeColors.DimRed
            : ThemeColors.AccentYellow;
        IntonerTooltipContent.Header(
            FontAwesomeIcon.Plug,
            count == 1 ? "Dependency missing" : "Dependencies missing",
            count == 1 ? "1 dependency is missing" : $"{count} dependencies are missing",
            accent);
        IntonerTooltipContent.Separator();

        string impact = hasRequiredFailure
            ? "A required dependency is unavailable. Some features will not work until it is resolved."
            : "Intoner will continue to work, but some features may not be available.";
        IntonerTooltipContent.Notice(FontAwesomeIcon.InfoCircle, impact, accent);

        IntonerTooltipContent.Separator();
        for (int index = 0; index < count; ++index)
        {
            DrawTooltipStatus(statuses[index]);
        }
    }

    private static void DrawTooltipStatus(DependencyStatus status)
    {
        DependencyPresentation presentation = ResolvePresentation(status.State);
        string requirement = status.Definition.Requirement == DependencyRequirement.Required
            ? "Required"
            : "Optional";
        string metadata = BuildVersionText(status);
        string detail = string.IsNullOrEmpty(metadata)
            ? requirement
            : $"{requirement} | {metadata}";
        IntonerTooltipContent.Item(
            presentation.Icon,
            $"{status.Definition.Name} | {presentation.Label}",
            $"{detail}\n{status.Message}",
            presentation.Accent);
    }

    public static void DrawInlineStatus(DependencyStatus status)
    {
        DependencyPresentation presentation = ResolvePresentation(status.State);
        EditorIcon.DrawInline(presentation.Icon, presentation.Accent);
        ImGui.SameLine(0f, 7f * ImGuiHelpers.GlobalScale);
        using (ImRaii.PushColor(ImGuiCol.Text, presentation.Accent))
        {
            ImGui.TextUnformatted(presentation.Label);
        }

        ImGui.SameLine(0f, 7f * ImGuiHelpers.GlobalScale);
        using (ImRaii.PushColor(ImGuiCol.Text, ThemeColors.TextDisabled))
        using (ImRaiiScope.TextWrapPos())
        {
            ImGui.TextUnformatted(status.Message);
        }
    }

}
