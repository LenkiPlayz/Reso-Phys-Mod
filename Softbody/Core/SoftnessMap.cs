namespace Softbody.Core;

/// <summary>Which part of a vertex colour is the softness (0 = firm, 1 = squishy).</summary>
public enum SoftnessSource
{
    Red = 0,
    Green = 1,
    Blue = 2,
    Alpha = 3,
    /// <summary>HSV value (brightness) = max(r, g, b). Hue and saturation are free, so any number of
    /// colours can be painted: black is firm, any fully bright colour is fully squishy.</summary>
    Value = 4,
}

/// <summary>
/// The seven rainbow hue bands (ROYGBIV). A vertex colour's hue picks its band (blending linearly
/// between neighbouring band centres, so hue gradients give smooth transitions) and its saturation
/// says how strongly: grey, white and black belong to no band and use the plain settings.
/// </summary>
public static class HueBands
{
    public const int Count = 7;
    public static readonly string[] Names = { "Red", "Orange", "Yellow", "Green", "Blue", "Indigo", "Violet" };
    /// <summary>Band centres in degrees of hue.</summary>
    public static readonly float[] Centers = { 0f, 30f, 60f, 120f, 240f, 270f, 300f };

    /// <summary>Writes the 7 band weights for one colour. They sum to the colour's HSV saturation;
    /// the remaining (1 - saturation) uses the plain, unbanded settings.</summary>
    public static void Weights(float r, float g, float b, Span<float> w)
    {
        w.Slice(0, Count).Clear();
        if (!float.IsFinite(r + g + b)) return;
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        if (max <= 1e-6f) return;
        float sat = Math.Clamp((max - min) / max, 0f, 1f);
        if (sat <= 1e-4f) return;
        float d = max - min, h;
        if (max == r) h = 60f * (((g - b) / d) % 6f);
        else if (max == g) h = 60f * ((b - r) / d + 2f);
        else h = 60f * ((r - g) / d + 4f);
        if (h < 0) h += 360f;
        for (int i = 0; i < Count; i++)
        {
            float c0 = Centers[i], c1 = i + 1 < Count ? Centers[i + 1] : 360f;
            if (h < c0 || h >= c1) continue;
            float t = (h - c0) / (c1 - c0);
            w[i] += (1f - t) * sat;
            w[(i + 1) % Count] += t * sat;
            return;
        }
    }
}

public static class SoftnessMap
{
    /// <summary>Softness 0..1 from one vertex colour (non-finite channels count as squishy).</summary>
    public static float FromColor(float r, float g, float b, float a, SoftnessSource source, bool invert)
    {
        float v = source switch
        {
            SoftnessSource.Green => g,
            SoftnessSource.Blue => b,
            SoftnessSource.Alpha => a,
            SoftnessSource.Value => MathF.Max(r, MathF.Max(g, b)),
            _ => r,
        };
        if (!float.IsFinite(v)) v = 1f;
        v = Math.Clamp(v, 0f, 1f);
        return invert ? 1f - v : v;
    }
}
