using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Collections;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal sealed record ObjectCollectionHeaderStatus(
    FontAwesomeIcon Icon,
    string IssueText,
    string DetailText,
    IReadOnlyList<string> Warnings,
    bool KeepingLastGoodSnapshot);

internal static class CollectionStatusUi
{
    public static bool DrawObjectCollectionHeaderNote(string id, string? text, Vector4 accent)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var paddingX = 8f * scale;
        var paddingY = 4f * scale;
        var textSize = ImGui.CalcTextSize(text);
        var size = new Vector2(
            textSize.X + (paddingX * 2f),
            textSize.Y + (paddingY * 2f));

        using var idScope = ImRaii.PushId(id);
        ImGui.InvisibleButton("##objectCollectionHeaderNote", size);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(ThemeColors.WithAlpha(accent, 0.12f)),
            999f);
        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(ThemeColors.WithAlpha(accent, 0.34f)),
            999f);
        drawList.AddText(
            new Vector2(min.X + paddingX, min.Y + paddingY),
            ImGui.GetColorU32(accent),
            text);

        return ImGui.IsItemHovered();
    }

    public static Vector4 ResolveObjectCollectionAccentColor(ObjectCollectionResolveState compileState)
        => compileState switch
        {
            ObjectCollectionResolveState.Ready => ThemeColors.AccentPrimary,
            ObjectCollectionResolveState.Resolving or ObjectCollectionResolveState.WaitingForPenumbra => ThemeColors.AccentBlue,
            ObjectCollectionResolveState.ModMissing or ObjectCollectionResolveState.ResolveFailed => ThemeColors.AccentOrange,
            _ => ThemeColors.TextDisabled,
        };

    public static string ResolveObjectCollectionStateLabel(ObjectCollectionResolveState compileState)
        => compileState switch
        {
            ObjectCollectionResolveState.Ready => "active",
            ObjectCollectionResolveState.Resolving => "compiling",
            ObjectCollectionResolveState.WaitingForPenumbra => "waiting for Penumbra",
            ObjectCollectionResolveState.ModMissing => "missing mod",
            ObjectCollectionResolveState.ResolveFailed => "resolve failed",
            _ => "inactive",
        };

    public static ObjectCollectionHeaderStatus? BuildObjectCollectionHeaderStatus(
        ObjectCollectionSnapshot collection,
        int assignedObjectCount)
    {
        string statusText = collection.StatusText.Trim();
        if (statusText.Length == 0
         && collection.Warnings.Count == 0
         && !collection.KeepingLastGoodSnapshot)
        {
            return null;
        }

        return new ObjectCollectionHeaderStatus(
            ResolveObjectCollectionStateIcon(collection.ResolveState),
            ResolveObjectCollectionIssueText(collection, assignedObjectCount, statusText),
            statusText,
            collection.Warnings,
            collection.KeepingLastGoodSnapshot);
    }

    public static void DrawObjectCollectionStatusTooltip(ObjectCollectionHeaderStatus status, Vector4 accent)
    {
        string summary = status.DetailText.Length > 0
            ? status.DetailText
            : status.IssueText;
        float measuredWidth = IntonerTooltipContent.MeasureHeaderWidth(
            status.Icon,
            "Collection status",
            summary);
        foreach (string warning in status.Warnings)
        {
            measuredWidth = MathF.Max(
                measuredWidth,
                IntonerTooltipContent.MeasureHeaderWidth(
                    FontAwesomeIcon.ExclamationTriangle,
                    "Warning",
                    warning));
        }

        if (status.KeepingLastGoodSnapshot)
        {
            measuredWidth = MathF.Max(
                measuredWidth,
                IntonerTooltipContent.MeasureHeaderWidth(
                    FontAwesomeIcon.InfoCircle,
                    "Last resolved data",
                    "Kept in use while this issue is resolved"));
        }

        IntonerTooltip.DrawContentSized(
            () =>
            {
                IntonerTooltipContent.Header(
                    status.Icon,
                    "Collection status",
                    summary,
                    accent);

                if (status.Warnings.Count > 0)
                {
                    IntonerTooltipContent.Separator();
                    IntonerTooltipContent.SectionLabel("Warnings", ThemeColors.AccentOrange);
                    foreach (string warning in status.Warnings)
                    {
                        IntonerTooltipContent.Item(
                            FontAwesomeIcon.ExclamationTriangle,
                            "Warning",
                            warning,
                            ThemeColors.AccentOrange);
                    }
                }

                if (status.KeepingLastGoodSnapshot)
                {
                    IntonerTooltipContent.Separator();
                    IntonerTooltipContent.Item(
                        FontAwesomeIcon.InfoCircle,
                        "Last resolved data",
                        "Kept in use while this issue is resolved",
                        ThemeColors.AccentBlue);
                }
            },
            measuredWidth,
            new IntonerTooltipOptions
            {
                Accent = accent,
                MaxWidth = 420f,
            });
    }

    public static IReadOnlyList<EditorBadge> BuildObjectCollectionWorkspaceBadges(
        ObjectCollectionSnapshot collection,
        int assignedObjectCount)
        =>
        [
            EditorBadge.Count(FontAwesomeIcon.Cubes, collection.Record.Entries.Count, "assigned mod", "assigned mods"),
            EditorBadge.Count(FontAwesomeIcon.ProjectDiagram, collection.RedirectCount, "redirect", "redirects"),
            EditorBadge.Count(FontAwesomeIcon.Cube, assignedObjectCount, "placed object", "placed objects"),
        ];

    public static string BuildAssignedModsSubtitle(int modCount)
        => modCount == 1 ? "1 assigned mod" : $"{modCount} assigned mods";

    private static string ResolveObjectCollectionIssueText(
        ObjectCollectionSnapshot collection,
        int assignedObjectCount,
        string statusText)
        => collection.ResolveState switch
        {
            ObjectCollectionResolveState.Ready when collection.Warnings.Count > 0
                => ResolveObjectCollectionWarningIssue(collection.Warnings),
            ObjectCollectionResolveState.Ready
                => "ready",
            ObjectCollectionResolveState.Resolving
                => "resolving",
            ObjectCollectionResolveState.WaitingForPenumbra
                => "waiting for Penumbra",
            ObjectCollectionResolveState.ModMissing
                => "missing Penumbra mods",
            ObjectCollectionResolveState.ResolveFailed
                => "resolve failed",
            _ => ResolveInactiveObjectCollectionIssue(collection, assignedObjectCount, statusText),
        };

    public static FontAwesomeIcon ResolveObjectCollectionStateIcon(ObjectCollectionResolveState state)
        => state switch
        {
            ObjectCollectionResolveState.Ready => FontAwesomeIcon.CheckCircle,
            ObjectCollectionResolveState.Resolving => FontAwesomeIcon.SyncAlt,
            ObjectCollectionResolveState.WaitingForPenumbra => FontAwesomeIcon.Clock,
            ObjectCollectionResolveState.ModMissing => FontAwesomeIcon.ExclamationTriangle,
            ObjectCollectionResolveState.ResolveFailed => FontAwesomeIcon.TimesCircle,
            _ => FontAwesomeIcon.InfoCircle,
        };

    private static string ResolveInactiveObjectCollectionIssue(
        ObjectCollectionSnapshot collection,
        int assignedObjectCount,
        string statusText)
    {
        if (assignedObjectCount == 0)
        {
            return "idle";
        }

        if (collection.Record.Entries.Count == 0)
        {
            return "no assigned mods";
        }

        if (!collection.Record.Entries.Any(static entry => entry.Enabled))
        {
            return "no enabled mods";
        }

        return statusText switch
        {
            _ when statusText.Contains("Penumbra", StringComparison.OrdinalIgnoreCase)
                && statusText.Contains("not available", StringComparison.OrdinalIgnoreCase)
                => "Penumbra unavailable",
            _ when statusText.Contains("no redirects", StringComparison.OrdinalIgnoreCase)
                => "no matching redirects",
            _ when statusText.Contains("no object resource paths", StringComparison.OrdinalIgnoreCase)
                => "no object paths",
            _ when collection.Warnings.Count > 0
                => ResolveObjectCollectionWarningIssue(collection.Warnings),
            _ => "inactive",
        };
    }

    private static string ResolveObjectCollectionWarningIssue(IReadOnlyList<string> warnings)
    {
        if (warnings.Any(static warning => warning.Contains("is missing", StringComparison.OrdinalIgnoreCase)))
        {
            return "missing Penumbra mods";
        }

        return warnings.Any(static warning => warning.Contains("no longer exists", StringComparison.OrdinalIgnoreCase))
            ? "stale mod settings"
            : "collection warnings";
    }
}
