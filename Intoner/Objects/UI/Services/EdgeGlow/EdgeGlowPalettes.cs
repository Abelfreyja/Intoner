using System.Numerics;

namespace Intoner.Objects.UI.Services.EdgeGlow;

internal sealed unsafe partial class EdgeGlowRenderer
{
    private static readonly GradientEllipse[] ColorfulLinePalette =
    [
        new(new Vector2(0f, 2f), new Vector2(36f, 36f), Color(255f, 50f, 100f)),
        new(new Vector2(39f, 0f), new Vector2(30f, 32f), Color(40f, 180f, 220f)),
        new(new Vector2(-36f, 2f), new Vector2(33f, 28f), Color(50f, 200f, 80f)),
        new(new Vector2(-54f, 0f), new Vector2(29f, 34f), Color(180f, 40f, 240f)),
        new(new Vector2(51f, -1f), new Vector2(27f, 30f), Color(255f, 160f, 30f)),
        new(new Vector2(21f, 1f), new Vector2(36f, 24f), Color(100f, 70f, 255f)),
        new(new Vector2(-21f, 0f), new Vector2(30f, 22f), Color(40f, 140f, 255f)),
        new(new Vector2(66f, 1f), new Vector2(25f, 28f), Color(240f, 50f, 180f)),
        new(new Vector2(-66f, -1f), new Vector2(23f, 30f), Color(30f, 185f, 170f)),
    ];

    private static readonly GradientEllipse[] SunsetLinePalette =
    [
        new(new Vector2(0f, 2f), new Vector2(36f, 36f), Color(255f, 100f, 60f)),
        new(new Vector2(39f, 0f), new Vector2(30f, 32f), Color(255f, 180f, 50f)),
        new(new Vector2(-36f, 2f), new Vector2(33f, 28f), Color(255f, 140f, 70f)),
        new(new Vector2(-54f, 0f), new Vector2(29f, 34f), Color(255f, 80f, 80f)),
        new(new Vector2(51f, -1f), new Vector2(27f, 30f), Color(255f, 200f, 60f)),
        new(new Vector2(21f, 1f), new Vector2(36f, 24f), Color(255f, 120f, 50f)),
        new(new Vector2(-21f, 0f), new Vector2(30f, 22f), Color(255f, 160f, 80f)),
        new(new Vector2(66f, 1f), new Vector2(25f, 28f), Color(255f, 90f, 60f)),
        new(new Vector2(-66f, -1f), new Vector2(23f, 30f), Color(255f, 70f, 70f)),
    ];

    private static readonly GradientEllipse[] ColorfulLineInnerPalette =
    [
        new(new Vector2(0f, 0f), new Vector2(33f, 30f), Color(255f, 50f, 100f, 0.48f)),
        new(new Vector2(39f, -3f), new Vector2(24f, 26f), Color(40f, 180f, 220f, 0.42f)),
        new(new Vector2(-36f, 0f), new Vector2(27f, 24f), Color(50f, 200f, 80f, 0.48f)),
        new(new Vector2(-54f, -2f), new Vector2(23f, 28f), Color(180f, 40f, 240f, 0.42f)),
        new(new Vector2(51f, -1f), new Vector2(24f, 24f), Color(255f, 160f, 30f, 0.50f)),
        new(new Vector2(21f, 0f), new Vector2(30f, 20f), Color(100f, 70f, 255f, 0.45f)),
        new(new Vector2(-21f, -2f), new Vector2(25f, 18f), Color(40f, 140f, 255f, 0.40f)),
        new(new Vector2(66f, 0f), new Vector2(21f, 24f), Color(240f, 50f, 180f, 0.45f)),
        new(new Vector2(-66f, -1f), new Vector2(18f, 26f), Color(30f, 185f, 170f, 0.52f)),
    ];

    private static readonly GradientEllipse[] SunsetLineInnerPalette =
    [
        new(new Vector2(0f, 0f), new Vector2(33f, 30f), Color(255f, 100f, 60f, 0.48f)),
        new(new Vector2(39f, -3f), new Vector2(24f, 26f), Color(255f, 180f, 50f, 0.42f)),
        new(new Vector2(-36f, 0f), new Vector2(27f, 24f), Color(255f, 140f, 70f, 0.48f)),
        new(new Vector2(-54f, -2f), new Vector2(23f, 28f), Color(255f, 80f, 80f, 0.42f)),
        new(new Vector2(51f, -1f), new Vector2(24f, 24f), Color(255f, 200f, 60f, 0.50f)),
        new(new Vector2(21f, 0f), new Vector2(30f, 20f), Color(255f, 120f, 50f, 0.45f)),
        new(new Vector2(-21f, -2f), new Vector2(25f, 18f), Color(255f, 160f, 80f, 0.40f)),
        new(new Vector2(66f, 0f), new Vector2(21f, 24f), Color(255f, 90f, 60f, 0.45f)),
        new(new Vector2(-66f, -1f), new Vector2(18f, 26f), Color(255f, 70f, 70f, 0.52f)),
    ];

    private static readonly BorderGradientSpot[] ColorfulBorderPalette =
    [
        new(new Vector2(0.33f, -0.074f), new Vector2(70f, 40f), Color(255f, 50f, 100f)),
        new(new Vector2(0.12f, -0.05f), new Vector2(60f, 35f), Color(40f, 140f, 255f)),
        new(new Vector2(0.021f, 0.683f), new Vector2(40f, 70f), Color(50f, 200f, 80f)),
        new(new Vector2(0.021f, 0.683f), new Vector2(20f, 35f), Color(30f, 185f, 170f)),
        new(new Vector2(0.744f, 1.0f), new Vector2(180f, 32f), Color(100f, 70f, 255f)),
        new(new Vector2(0.55f, 1.0f), new Vector2(85f, 26f), Color(40f, 140f, 255f)),
        new(new Vector2(0.939f, 0.0f), new Vector2(74f, 32f), Color(255f, 120f, 40f)),
        new(new Vector2(1.0f, 0.271f), new Vector2(26f, 42f), Color(240f, 50f, 180f)),
        new(new Vector2(1.0f, 0.271f), new Vector2(52f, 48f), Color(180f, 40f, 240f)),
    ];

    private static readonly BorderGradientSpot[] SunsetBorderPalette =
    [
        new(new Vector2(0.33f, -0.074f), new Vector2(70f, 40f), Color(255f, 80f, 50f)),
        new(new Vector2(0.12f, -0.05f), new Vector2(60f, 35f), Color(255f, 160f, 40f)),
        new(new Vector2(0.021f, 0.683f), new Vector2(40f, 70f), Color(255f, 120f, 60f)),
        new(new Vector2(0.021f, 0.683f), new Vector2(20f, 35f), Color(255f, 200f, 50f)),
        new(new Vector2(0.744f, 1.0f), new Vector2(180f, 32f), Color(255f, 100f, 80f)),
        new(new Vector2(0.55f, 1.0f), new Vector2(85f, 26f), Color(255f, 180f, 60f)),
        new(new Vector2(0.939f, 0.0f), new Vector2(74f, 32f), Color(255f, 60f, 60f)),
        new(new Vector2(1.0f, 0.271f), new Vector2(26f, 42f), Color(255f, 140f, 50f)),
        new(new Vector2(1.0f, 0.271f), new Vector2(52f, 48f), Color(255f, 90f, 70f)),
    ];

    private static readonly BloomPalette ColorfulBloomPalette = new(
        Color(255f, 60f, 80f),
        Color(40f, 190f, 180f, 0.98f),
        [
            new BloomColorPair(Color(100f, 70f, 255f), Color(100f, 70f, 255f, 1f)),
            new BloomColorPair(Color(255f, 170f, 40f, 0.59f), Color(255f, 170f, 40f, 0.29f)),
            new BloomColorPair(Color(50f, 200f, 100f), Color(50f, 200f, 100f, 1f)),
            new BloomColorPair(Color(200f, 50f, 240f, 0.91f), Color(200f, 50f, 240f, 0.45f)),
            new BloomColorPair(Color(40f, 140f, 255f), Color(40f, 140f, 255f, 1f)),
        ]);

    private static readonly BloomPalette SunsetBloomPalette = new(
        Color(255f, 140f, 80f),
        Color(255f, 100f, 60f, 0.98f),
        [
            new BloomColorPair(Color(255f, 100f, 80f), Color(255f, 100f, 80f)),
            new BloomColorPair(Color(255f, 150f, 80f, 0.59f), Color(255f, 150f, 80f, 0.29f)),
            new BloomColorPair(Color(255f, 80f, 60f), Color(255f, 80f, 60f)),
            new BloomColorPair(Color(255f, 120f, 50f, 0.91f), Color(255f, 120f, 50f, 0.45f)),
            new BloomColorPair(Color(255f, 140f, 70f), Color(255f, 140f, 70f)),
        ]);

    private static BeamVariantResources ResolveVariantResources(EdgeGlowColorVariant colorVariant)
        => colorVariant switch
        {
            EdgeGlowColorVariant.Sunset => new BeamVariantResources(SunsetLinePalette, SunsetLineInnerPalette, SunsetBloomPalette, SunsetBorderPalette),
            _ => new BeamVariantResources(ColorfulLinePalette, ColorfulLineInnerPalette, ColorfulBloomPalette, ColorfulBorderPalette),
        };
}

