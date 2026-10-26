using Dalamud.Bindings.ImGui;
using Intoner.Objects.Rendering.Drawing;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class GizmoIndicatorRow
{
    private readonly List<string> _icons = new(4);

    public void Clear() => _icons.Clear();

    public void Add(string icon, bool visible)
    {
        if (visible)
        {
            _icons.Add(icon);
        }
    }

    public Vector2 Measure(Vector2 textSize, float scale)
    {
        if (_icons.Count == 0)
        {
            return textSize;
        }

        float iconSize = ImGui.GetFontSize() * 0.72f;
        return new Vector2(MathF.Max(textSize.X, _icons.Count * (iconSize + 4f * scale) - 4f * scale),
            textSize.Y + iconSize + 3f * scale);
    }

    public void Draw(DrawBatch batch, Vector2 textPosition, Vector2 textSize, ImFontPtr iconFont, float scale, bool followAnchor = true)
    {
        Vector2 position = new(textPosition.X, textPosition.Y + textSize.Y + 3f * scale);
        float iconSize = ImGui.GetFontSize() * 0.72f;
        TextStyle style = new(ThemeColors.Color(1f, 1f, 1f, 0.55f));
        foreach (string icon in _icons)
        {
            batch.AddText(position, icon, style, iconFont, iconSize, followAnchor);
            position.X += iconSize + 4f * scale;
        }
    }
}
