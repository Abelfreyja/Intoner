using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private bool DrawPositionClipboardRow(string id, ref Vector3 value)
        => DrawTransformClipboardRow(id, "Position", TransformClipboardKind.Position, ref value, 0.05f, -10000f, 10000f, "%.3f");

    private bool DrawRotationClipboardRow(string id, ref Vector3 value)
        => DrawTransformClipboardRow(id, "Rotation", TransformClipboardKind.Rotation, ref value, 0.5f, -360f, 360f, "%.1f");

    private bool DrawScaleClipboardRow(string id, ref Vector3 value)
        => DrawTransformClipboardRow(id, "Scale", TransformClipboardKind.Scale, ref value, 0.01f, 0.01f, 100f, "%.3f");

    private bool DrawTransformClipboardRow(
        string id,
        string title,
        TransformClipboardKind kind,
        ref Vector3 value,
        float speed,
        float min,
        float max,
        string format)
    {
        Vector3 rowValue = value;
        bool pasted = false;
        float actionWidth = TransformClipboardControls.ResolveWidth();
        bool changed = DrawDragFloat3Row(
            id,
            title,
            ref rowValue,
            speed,
            min,
            max,
            format,
            actionWidth,
            () =>
            {
                if (!TransformClipboardControls.Draw(_clipboardExportService, id, kind, rowValue, out Vector3 pastedValue))
                {
                    return;
                }

                rowValue = ClampVector3(pastedValue, min, max);
                pasted = true;
            });

        if (changed || pasted)
        {
            value = rowValue;
        }

        return changed || pasted;
    }

    private static Vector3 ClampVector3(Vector3 value, float min, float max)
        => new(
            Math.Clamp(value.X, min, max),
            Math.Clamp(value.Y, min, max),
            Math.Clamp(value.Z, min, max));
}

