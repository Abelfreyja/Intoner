using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.Rendering.Drawing;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI.Bounds;

internal sealed class BoundsAnnotationRenderer
{
    private const float BadgeSize = 20f;
    private const float BadgeRounding = 4f;
    private const float BadgeOffset = 5f;
    private const float ViewportPadding = 3f;
    private const float TooltipWidth = 280f;
    private const float BadgeShadowOffset = 2f;
    private const float BadgeShadowOpacity = 0.34f;
    private const float ExclamationStemWidth = 2.4f;
    private const float ExclamationStemHeight = 8.4f;
    private const float ExclamationDotSize = 2.8f;
    private const float ExclamationGap = 2.0f;

    private readonly Dictionary<Guid, ObjectBoundsSnapshot> _boundsLookup = [];

    public void Draw(
        ImDrawListPtr drawList,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        IReadOnlyList<BoundsAnnotation> annotations,
        BoundsOverlaySpace overlaySpace,
        in DrawContext context)
    {
        _boundsLookup.Clear();
        if (annotations.Count == 0 || boundsSnapshots.Count == 0)
        {
            return;
        }

        BuildBoundsLookup(boundsSnapshots, annotations.Count);
        float scale = ImGuiHelpers.GlobalScale;
        for (int index = 0; index < annotations.Count; ++index)
        {
            BoundsAnnotation annotation = annotations[index];
            ObjectBoundsSnapshot? boundsSnapshot = ResolveBounds(boundsSnapshots, annotation.BoundsId);
            if (boundsSnapshot is null
                || !TryResolveAnchor(boundsSnapshot, overlaySpace, context, annotation.Corner, out Vector2 anchor, out Vector2 direction))
            {
                continue;
            }

            DrawBadge(drawList, annotation, anchor, direction, context, scale);
        }
    }

    private void BuildBoundsLookup(IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots, int annotationCount)
    {
        if (annotationCount <= 1 || boundsSnapshots.Count <= 1)
        {
            return;
        }

        foreach (ObjectBoundsSnapshot boundsSnapshot in boundsSnapshots)
        {
            _boundsLookup[boundsSnapshot.Id] = boundsSnapshot;
        }
    }

    private ObjectBoundsSnapshot? ResolveBounds(IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots, Guid id)
    {
        if (_boundsLookup.Count != 0)
        {
            return _boundsLookup.TryGetValue(id, out ObjectBoundsSnapshot? boundsSnapshot)
                ? boundsSnapshot
                : null;
        }

        return FindBounds(boundsSnapshots, id);
    }

    private static ObjectBoundsSnapshot? FindBounds(IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots, Guid id)
    {
        foreach (ObjectBoundsSnapshot candidate in boundsSnapshots)
        {
            if (candidate.Id == id)
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool TryResolveAnchor(
        ObjectBoundsSnapshot boundsSnapshot,
        BoundsOverlaySpace overlaySpace,
        in DrawContext context,
        BoundsAnnotationCorner corner,
        out Vector2 anchor,
        out Vector2 direction)
    {
        anchor = default;
        direction = ResolveCornerDirection(corner);

        Span<Vector3> worldCorners = stackalloc Vector3[BoundsOverlayGeometry.BoxCornerCount];
        Span<Vector2> screenCorners = stackalloc Vector2[BoundsOverlayGeometry.BoxCornerCount];
        BoundsOverlayGeometry.CopyBoxCorners(boundsSnapshot, overlaySpace, worldCorners);

        int projectedCount = 0;
        Vector2 min = new(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 max = new(float.NegativeInfinity, float.NegativeInfinity);
        foreach (Vector3 worldCorner in worldCorners)
        {
            if (!ObjectViewportProjectionUtility.TryProjectWorldPointToViewport(
                    context.ViewProjection,
                    worldCorner,
                    context.ViewportPos,
                    context.ViewportSize,
                    out Vector2 screenCorner))
            {
                continue;
            }

            screenCorners[projectedCount++] = screenCorner;
            min = Vector2.Min(min, screenCorner);
            max = Vector2.Max(max, screenCorner);
        }

        if (projectedCount == 0)
        {
            return false;
        }

        Vector2 desired = ResolveScreenRectCorner(min, max, corner);
        anchor = screenCorners[0];
        float bestDistance = Vector2.DistanceSquared(anchor, desired);
        for (int index = 1; index < projectedCount; ++index)
        {
            float distance = Vector2.DistanceSquared(screenCorners[index], desired);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            anchor = screenCorners[index];
        }

        return ObjectMathUtility.IsFinite(anchor);
    }

    private static Vector2 ResolveScreenRectCorner(Vector2 min, Vector2 max, BoundsAnnotationCorner corner)
        => corner switch
        {
            BoundsAnnotationCorner.TopLeft     => min,
            BoundsAnnotationCorner.TopRight    => new Vector2(max.X, min.Y),
            BoundsAnnotationCorner.BottomLeft  => new Vector2(min.X, max.Y),
            BoundsAnnotationCorner.BottomRight => max,
            _                                  => max,
        };

    private static Vector2 ResolveCornerDirection(BoundsAnnotationCorner corner)
        => corner switch
        {
            BoundsAnnotationCorner.TopLeft     => new Vector2(-1f, -1f),
            BoundsAnnotationCorner.TopRight    => new Vector2(1f, -1f),
            BoundsAnnotationCorner.BottomLeft  => new Vector2(-1f, 1f),
            BoundsAnnotationCorner.BottomRight => new Vector2(1f, 1f),
            _                                  => Vector2.One,
        };

    private static void DrawBadge(
        ImDrawListPtr drawList,
        BoundsAnnotation annotation,
        Vector2 anchor,
        Vector2 direction,
        in DrawContext context,
        float scale)
    {
        Vector2 badgeSize = new(BadgeSize * scale);
        float offset = BadgeOffset * scale;
        Vector2 center = anchor + (direction * offset);
        Vector2 min = ClampBadgeMin(center - (badgeSize * 0.5f), badgeSize, context, scale);
        Vector2 max = min + badgeSize;

        Vector2 shadowOffset = Vector2.One * (BadgeShadowOffset * scale);
        Vector4 fill = EditorColors.WithAlpha(annotation.Accent, 0.95f);
        Vector4 iconColor = EditorColors.Color(1f, 1f, 1f, 0.96f);
        Vector4 shadowColor = EditorColors.Color(0f, 0f, 0f, BadgeShadowOpacity);

        drawList.AddRectFilled(min + shadowOffset, max + shadowOffset, ImGui.GetColorU32(shadowColor), BadgeRounding * scale);
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), BadgeRounding * scale);
        DrawBadgeIcon(drawList, annotation.Icon, min, badgeSize, iconColor, scale);
        DrawTooltip(annotation, min, max);
    }

    private static void DrawBadgeIcon(
        ImDrawListPtr drawList,
        FontAwesomeIcon icon,
        Vector2 badgeMin,
        Vector2 badgeSize,
        Vector4 color,
        float scale)
    {
        if (icon == FontAwesomeIcon.Exclamation)
        {
            DrawExclamationIcon(drawList, badgeMin, badgeSize, color, scale);
            return;
        }

        DrawFontIcon(drawList, icon.ToIconString(), badgeMin, badgeSize, color);
    }

    private static void DrawExclamationIcon(
        ImDrawListPtr drawList,
        Vector2 badgeMin,
        Vector2 badgeSize,
        Vector4 color,
        float scale)
    {
        Vector2 stemSize = new(ExclamationStemWidth * scale, ExclamationStemHeight * scale);
        float dotSize = ExclamationDotSize * scale;
        float gap = ExclamationGap * scale;
        float contentHeight = stemSize.Y + gap + dotSize;
        float centerX = badgeMin.X + (badgeSize.X * 0.5f);
        float top = badgeMin.Y + ((badgeSize.Y - contentHeight) * 0.5f);
        Vector2 stemMin = new(centerX - (stemSize.X * 0.5f), top);
        Vector2 stemMax = stemMin + stemSize;
        Vector2 dotCenter = new(centerX, stemMax.Y + gap + (dotSize * 0.5f));
        uint colorU32 = ImGui.GetColorU32(color);

        drawList.AddRectFilled(stemMin, stemMax, colorU32, stemSize.X * 0.5f);
        drawList.AddCircleFilled(dotCenter, dotSize * 0.5f, colorU32, 12);
    }

    private static void DrawFontIcon(
        ImDrawListPtr drawList,
        string iconText,
        Vector2 badgeMin,
        Vector2 badgeSize,
        Vector4 color)
    {
        (Vector2 iconSize, float iconFontSize) = MeasureIcon(iconText);
        Vector2 iconPosition = badgeMin + ((badgeSize - iconSize) * 0.5f);
        drawList.AddText(UiBuilder.IconFont, iconFontSize, iconPosition, ImGui.GetColorU32(color), iconText);
    }

    private static (Vector2 Size, float FontSize) MeasureIcon(string iconText)
    {
        using var iconFont = ImRaii.PushFont(UiBuilder.IconFont);
        return (ImGui.CalcTextSize(iconText), ImGui.GetFontSize());
    }

    private static Vector2 ClampBadgeMin(Vector2 min, Vector2 badgeSize, in DrawContext context, float scale)
    {
        float padding = ViewportPadding * scale;
        Vector2 minLimit = context.ViewportPos + new Vector2(padding);
        Vector2 maxLimit = context.ViewportPos + context.ViewportSize - badgeSize - new Vector2(padding);
        maxLimit = Vector2.Max(maxLimit, minLimit);
        return Vector2.Min(Vector2.Max(min, minLimit), maxLimit);
    }

    private static void DrawTooltip(BoundsAnnotation annotation, Vector2 min, Vector2 max)
    {
        if (!EditorInputUtility.IsMouseInside(min, max))
        {
            return;
        }

        string title = annotation.TooltipTitle ?? string.Empty;
        string text = annotation.TooltipText ?? string.Empty;
        UiSharedService.DrawAccentTooltip(() =>
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(annotation.Accent, annotation.Icon.ToIconString());
            }

            ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
            ImGui.TextUnformatted(title);
            ImGui.Separator();
            using IDisposable wrap = ImRaiiScope.TextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextUnformatted(text);
            DrawFixes(annotation);
        }, annotation.Accent, unscaledFixedWidth: TooltipWidth);
    }

    private static void DrawFixes(BoundsAnnotation annotation)
    {
        if (annotation.Fixes.Count == 0)
        {
            return;
        }

        ImGui.Dummy(new Vector2(0f, 3f * ImGuiHelpers.GlobalScale));
        ImGui.Separator();
        ImGui.TextDisabled("Suggested fixes");
        foreach (PlacementFixProposal fix in annotation.Fixes)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(fix.Label);
        }
    }
}
