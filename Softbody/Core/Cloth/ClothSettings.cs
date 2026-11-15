using System.Globalization;
using System.Numerics;

namespace Softbody.Core.Cloth;

/// <summary>0.29.0: a cloth's settings (the Cloth component's fields, sanitized). All of them apply live.</summary>
public sealed class ClothSettings
{
    /// <summary>g/m²: silk is under 100, shirting 100..150, denim 250..350 (10..2000).</summary>
    public float Weight = 200f;
    /// <summary>0: doesn't stretch (woven cloth); 1: rubbery.</summary>
    public float Stretch = 0f;
    /// <summary>0: drapes like silk; 1: stiff like leather.</summary>
    public float Bend = 0.3f;
    /// <summary>0..1: how fast motion dies down (1 = like honey).</summary>
    public float Damping = 0.1f;
    /// <summary>Air drag and lift per triangle (0..5).</summary>
    public float Drag = 1f, Lift = 0.5f;
    /// <summary>Gravity multiplier (-10..10).</summary>
    public float Gravity = 1f;
    /// <summary>0..2: friction against colliders.</summary>
    public float Friction = 0.8f;
    /// <summary>Metres: how close the cloth gets to colliders (0.001..0.1).</summary>
    public float Thickness = 0.01f;
    /// <summary>Steps per second (240..1440): each step one pass over the constraints.</summary>
    public int StepRate = 720;
    /// <summary>Metres, 0 = no limit: a free point stays within its brightness times this of its rest place.</summary>
    public float MaxDistance = 0f;
    public bool Collide = true;
    public bool Tearable = false;
    /// <summary>An edge tears once it is longer than this times its rest length (1.05..10).</summary>
    public float TearAt = 1.5f;
    /// <summary>0..1: how much a fresh tear weakens the edges next to it (so a rip runs along a line).</summary>
    public float TearSpread = 0.3f;
    /// <summary>Metres: past this distance from the viewer it stops (0 = never).</summary>
    public float CullDistance = 30f;
    /// <summary>0.30.0, avatar cloth: how much of the avatar's own motion the cloth feels (0..1). 1: all of it, as world
    /// cloth (a fast spin flares a skirt out the most); 0: none (the cloth moves with the avatar as if painted on, and only
    /// the animation and gravity swing it).</summary>
    public float Inertia = 0.75f;
    /// <summary>0.30.0, avatar cloth: metres a free point may go behind where the animation puts it, along the animated
    /// cloth's normal (0..1; 0 = no backstop). Keeps a skirt out of the legs it covers.</summary>
    public float Backstop = 0.02f;

    /// <summary>Most steps in one frame; below StepRate / this frames per second the cloth runs slower than real time.</summary>
    public const int MaxStepsPerFrame = 24;
    /// <summary>Most tears one frame decides.</summary>
    public const int MaxTearsPerFrame = 8;
    public const float AirDensity = 1.2f;

    public void Sanitize()
    {
        Weight = Clamp(Weight, 10f, 2000f, 200f);
        Stretch = Clamp(Stretch, 0f, 1f, 0f);
        Bend = Clamp(Bend, 0f, 1f, 0.3f);
        Damping = Clamp(Damping, 0f, 1f, 0.1f);
        Drag = Clamp(Drag, 0f, 5f, 1f);
        Lift = Clamp(Lift, 0f, 5f, 0.5f);
        Gravity = Clamp(Gravity, -10f, 10f, 1f);
        Friction = Clamp(Friction, 0f, 2f, 0.8f);
        Thickness = Clamp(Thickness, 0.001f, 0.1f, 0.01f);
        StepRate = Math.Clamp(StepRate, 240, 1440);
        MaxDistance = Clamp(MaxDistance, 0f, 100f, 0f);
        TearAt = Clamp(TearAt, 1.05f, 10f, 1.5f);
        TearSpread = Clamp(TearSpread, 0f, 1f, 0.3f);
        CullDistance = Clamp(CullDistance, 0f, 10000f, 30f);
        Inertia = Clamp(Inertia, 0f, 1f, 0.75f);
        Backstop = Clamp(Backstop, 0f, 1f, 0.02f);
    }

    static float Clamp(float v, float lo, float hi, float fallback) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;

    /// <summary>XPBD compliance of the edges, m/N (0 = rigid). Roughly the inverse of the sheet's stiffness per unit width,
    /// which an edge of a triangle mesh has whatever its length: 1 kN/m at 0, about 3 N/m at 1.</summary>
    public float StretchCompliance => Stretch <= 0f ? 0f : MathF.Pow(10f, -3f + 2.5f * Stretch);

    /// <summary>XPBD compliance of the bending pairs, m/N (0 = rigid at 1, practically none at 0).</summary>
    public float BendCompliance => Bend >= 1f ? 0f : MathF.Pow(10f, 2f - 7f * Bend);

    /// <summary>Per second, the velocity damping.</summary>
    public float DampingRate => 10f * Damping;

    public ClothSettings Clone() => (ClothSettings)MemberwiseClone();

    public override string ToString()
    {
        var ic = CultureInfo.InvariantCulture;
        return string.Format(ic, "weight {0:0.#} g/m², stretch {1:0.##}, bend {2:0.##}, damping {3:0.##}, drag {4:0.##}, lift {5:0.##}, gravity {6:0.##}, friction {7:0.##}, " +
                                 "thickness {8:0.###} m, {9} steps/s, max distance {10:0.###} m, collide {11}, tearable {12} (at {13:0.##}x, spread {14:0.##}), cull {15:0.#} m, " +
                                 "inertia {16:0.##}, backstop {17:0.###} m",
            Weight, Stretch, Bend, Damping, Drag, Lift, Gravity, Friction, Thickness, StepRate, MaxDistance, Collide, Tearable, TearAt, TearSpread, CullDistance,
            Inertia, Backstop);
    }
}

/// <summary>0.29.0: one Cloth Wind as the solver sees it (world space).</summary>
public struct ClothWindSource
{
    public Vector3 Position;
    /// <summary>Unit: where it blows (its slot's forward).</summary>
    public Vector3 Direction;
    /// <summary>m/s.</summary>
    public float Strength;
    /// <summary>0..1: how much the strength rises and falls.</summary>
    public float Gusts;
    /// <summary>Roughly gusts per second.</summary>
    public float GustRate;
    /// <summary>0..1: swirls, as a fraction of the strength.</summary>
    public float Turbulence;
    /// <summary>Metres: the size of the swirls.</summary>
    public float SwirlSize;
    /// <summary>Metres, 0 = everywhere: inside half of it full strength, fading to nothing at it, and only in front.</summary>
    public float Radius;
    /// <summary>Different per wind, so two winds don't gust in step.</summary>
    public int Seed;
}

/// <summary>
/// 0.29.0: the wind at a point. Each Cloth Wind blows along its direction with gusts (a few slow waves in time) and swirls
/// (a few travelling waves across space, each pushing across its own direction, so the swirls neither pile up nor thin out
/// the air), carried along with the wind. World time is the clock, so clients see roughly the same gusts.
/// </summary>
public static class ClothWindField
{
    const int Waves = 6;
    static readonly Vector3[] _k =
    {
        Vector3.Normalize(new Vector3(1f, 0.31f, 0.17f)), Vector3.Normalize(new Vector3(-0.22f, 1f, 0.41f)), Vector3.Normalize(new Vector3(0.37f, -0.18f, 1f)),
        Vector3.Normalize(new Vector3(0.71f, 0.69f, -0.23f)), Vector3.Normalize(new Vector3(-0.6f, 0.27f, 0.75f)), Vector3.Normalize(new Vector3(0.13f, -0.83f, 0.54f)),
    };
    static readonly float[] _len = { 1.0f, 0.73f, 1.37f, 0.58f, 1.61f, 0.89f };

    public static Vector3 Sample(ReadOnlySpan<ClothWindSource> winds, Vector3 p, double time)
    {
        var sum = Vector3.Zero;
        for (int i = 0; i < winds.Length; i++) sum += Sample(winds[i], p, time);
        return sum;
    }

    public static Vector3 Sample(in ClothWindSource w, Vector3 p, double time)
    {
        if (!(w.Strength != 0f)) return Vector3.Zero;
        float fall = 1f;
        if (w.Radius > 0f)
        {
            var rel = p - w.Position;
            float d = rel.Length();
            if (d >= w.Radius) return Vector3.Zero;
            if (d > 0.5f * w.Radius) { float t = (w.Radius - d) / (0.5f * w.Radius); fall = t * t * (3f - 2f * t); }
            float front = Vector3.Dot(rel, w.Direction) / (0.1f * w.Radius);
            if (front <= -1f) return Vector3.Zero;
            if (front < 1f) { float t = 0.5f * (front + 1f); fall *= t * t * (3f - 2f * t); }
        }
        float seed = (w.Seed & 1023) * 0.618034f;
        float gust = 0f;
        if (w.Gusts > 0f && w.GustRate > 0f)
        {
            double f = 2.0 * Math.PI * w.GustRate * time;
            gust = 0.55f * (float)Math.Sin(f + seed) + 0.3f * (float)Math.Sin(2.13 * f + 1.7 + 2.0 * seed) + 0.15f * (float)Math.Sin(4.71 * f + 0.6 + 3.0 * seed);
        }
        float strength = w.Strength * MathF.Max(0f, 1f + w.Gusts * gust) * fall;
        var v = w.Direction * strength;
        if (w.Turbulence > 0f && w.SwirlSize > 0f)
        {
            var drift = (double)w.Strength * time;   // the swirls travel with the wind
            var turb = Vector3.Zero;
            for (int k = 0; k < Waves; k++)
            {
                var kd = _k[(k + (w.Seed & 7)) % Waves];
                float wl = w.SwirlSize * _len[k];
                double kk = 2.0 * Math.PI / wl;
                double phase = kk * (Vector3.Dot(kd, p) - Vector3.Dot(kd, w.Direction) * drift) + 1.3 * k + seed + 0.7 * time;
                phase %= 2.0 * Math.PI;
                // a direction across the wave's own, so the swirl field has no sources or sinks
                var a = Vector3.Normalize(Vector3.Cross(kd, k % 2 == 0 ? Vector3.UnitY : Vector3.UnitX));
                if (!float.IsFinite(a.X)) a = Vector3.UnitZ;
                turb += a * (float)Math.Sin(phase);
            }
            v += turb * (w.Turbulence * MathF.Abs(w.Strength) * fall * 0.6f);
        }
        return v;
    }
}
