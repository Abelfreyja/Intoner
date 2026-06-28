using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Collections;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal sealed record ObjectCollectionHeaderStatus(
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
            ImGui.GetColorU32(EditorColors.WithAlpha(accent, 0.12f)),
            999f);
        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(EditorColors.WithAlpha(accent, 0.34f)),
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
            ObjectCollectionResolveState.Ready => EditorColors.AccentPurple,
            ObjectCollectionResolveState.Resolving or ObjectCollectionResolveState.WaitingForPenumbra => EditorColors.AccentBlue,
            ObjectCollectionResolveState.ModMissing or ObjectCollectionResolveState.ResolveFailed => EditorColors.AccentOrange,
            _ => EditorColors.TextDisabled,
        };

    public static string ResolveObjectCollectionStateLabel(ObjectCollectionResolveState compileState)
        => compileState switch
        {
            ObjectCollectionResolveState.Ready => "ready",
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
            BuildObjectCollectionHeaderBadgeText(collection, assignedObjectCount, statusText),
            statusText,
            collection.Warnings,
            collection.KeepingLastGoodSnapshot);
    }

    public static string BuildObjectCollectionHeaderBadgeText(
        ObjectCollectionSnapshot collection,
        int assignedObjectCount)
        => ResolveObjectCollectionIssueText(collection, assignedObjectCount, collection.StatusText.Trim());

    private static string BuildObjectCollectionHeaderBadgeText(
        ObjectCollectionSnapshot collection,
        int assignedObjectCount,
        string statusText)
        => ResolveObjectCollectionIssueText(collection, assignedObjectCount, statusText);

    public static void DrawObjectCollectionStatusTooltip(ObjectCollectionHeaderStatus status, Vector4 accent)
    {
        UiSharedService.DrawAccentTooltip(
            () =>
            {
                using var wrap = ImRaiiScope.TextWrapPos(ImGui.GetFontSize() * 42f);
                using (ImRaii.PushColor(ImGuiCol.Text, accent))
                {
                    ImGui.TextUnformatted(status.IssueText);
                }

                if (status.DetailText.Length > 0)
                {
                    ImGuiHelpers.ScaledDummy(3f);
                    using (ImRaii.PushColor(ImGuiCol.Text, EditorColors.TextDisabled))
                    {
                        ImGui.TextWrapped(status.DetailText);
                    }
                }

                if (status.Warnings.Count > 0)
                {
                    ImGuiHelpers.ScaledDummy(4f);
                    ImGui.Separator();
                    using (ImRaii.PushColor(ImGuiCol.Text, EditorColors.AccentOrange))
                    {
                        ImGui.TextUnformatted("warnings:");
                        foreach (string warning in status.Warnings)
                        {
                            ImGui.TextWrapped($"- {warning}");
                        }
                    }
                }

                if (status.KeepingLastGoodSnapshot)
                {
                    ImGuiHelpers.ScaledDummy(4f);
                    ImGui.Separator();
                    using (ImRaii.PushColor(ImGuiCol.Text, EditorColors.AccentBlue))
                    {
                        ImGui.TextWrapped("keeping last resolved data");
                    }
                }
            },
            accent);
    }

    public static string BuildObjectCollectionListStatus(
        IReadOnlyList<ObjectCollectionSnapshot> collections,
        ObjectCollectionSnapshot? selectedCollection)
        => $"{(collections.Count == 1 ? "1 collection" : $"{collections.Count} collections")} | {(selectedCollection is null ? "no selection" : selectedCollection.Record.Name)}";

    public static string BuildObjectCollectionInspectorStatus(
        ObjectCollectionSnapshot collection,
        int assignedObjectCount)
        => $"{BuildAssignedModsSubtitle(collection.Record.Entries.Count)} | {BuildRedirectCountLabel(collection.RedirectCount)} | {BuildAssignedObjectsSubtitle(assignedObjectCount)}";

    public static string BuildObjectCollectionEntryDetail(ObjectCollectionSnapshot collection)
        => $"{ResolveObjectCollectionStateLabel(collection.ResolveState)} | {BuildRedirectCountLabel(collection.RedirectCount)} | {BuildAssignedModsSubtitle(collection.Record.Entries.Count)}";

    public static string BuildAssignedModsSubtitle(int modCount)
        => modCount == 1 ? "1 assigned mod" : $"{modCount} assigned mods";

    private static string BuildRedirectCountLabel(int redirectCount)
        => redirectCount == 1 ? "1 redirect" : $"{redirectCount} redirects";

    private static string BuildAssignedObjectsSubtitle(int objectCount)
        => objectCount == 1 ? "1 placed object" : $"{objectCount} placed objects";

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
        => warnings.Any(static warning => warning.Contains("is missing", StringComparison.OrdinalIgnoreCase))
            ? "missing Penumbra mods"
            : warnings.Any(static warning => warning.Contains("no longer exists", StringComparison.OrdinalIgnoreCase))
                ? "stale mod settings"
                : "collection warnings";
}
