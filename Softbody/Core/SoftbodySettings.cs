using System.Numerics;

namespace Softbody.Core;

/// <summary>
/// Tunables for one softbody. All frequencies are spring frequencies in Hz (Bepu SpringSettings),
/// damping values are damping ratios (1 = critically damped). Shared by the plugin and the offline tests.
/// </summary>
public sealed class SoftbodySettings
{
    /// <summary>Cells per axis of the lattice cage (points per axis = Cells + 1).</summary>
    public int Cells = 4;
    /// <summary>Edge spring frequency between cage points in fully soft regions (Hz).</summary>
    public float Stiffness = 6f;
    /// <summary>Volume spring frequency per tetrahedron in fully soft regions (Hz). 0 = 2 x Stiffness.</summary>
    public float VolumeStiffness = 0f;
    /// <summary>Damping ratio of soft springs and soft servos. Low = wobbly.</summary>
    public float Damping = 0.15f;
    /// <summary>Servo frequency pulling fully soft points back to their rest pose in the slot frame (Hz).</summary>
    public float Follow = 2.5f;
    /// <summary>Servo and edge frequency used for nearly-firm points that are still simulated (Hz).</summary>
    public float RigidFrequency = 30f;
    /// <summary>Points whose softness is at or below this are kinematic: pinned exactly to the slot.</summary>
    public float RigidThreshold = 0.02f;
    /// <summary>Total mass of the cage in kg.</summary>
    public float Mass = 2f;
    /// <summary>Gravity multiplier (1 = 9.81 m/s^2 down the world -Y axis).</summary>
    public float Gravity = 1f;
    /// <summary>Exponential linear damping applied to cage points (1/s).</summary>
    public float LinearDamping = 0.2f;
    /// <summary>Whether cage points collide with statics (ground plane and proxies).</summary>
    public bool Collide = true;
    /// <summary>Collision sphere radius of a cage point as a fraction of the smallest cell size.</summary>
    public float PointRadiusFactor = 0.2f;
    /// <summary>Fixed physics substep (s).</summary>
    public float Substep = 1f / 120f;
    /// <summary>Maximum substeps per frame; extra time is dropped (slow motion instead of a spiral).</summary>
    public int MaxSubsteps = 4;
    public float Friction = 0.8f;
    public int SolverIterations = 8;

    // Per hue band (ROYGBIV, see HueBands): multipliers on Stiffness, Damping and Follow for the parts
    // painted in that hue. 1 = same as the plain settings. Live-tunable.
    public float[] BandStiffness = Ones();
    public float[] BandDamping = Ones();
    public float[] BandFollow = Ones();
    static float[] Ones() { var a = new float[HueBands.Count]; Array.Fill(a, 1f); return a; }
    public bool HasBands => !AllOnes(BandStiffness) || !AllOnes(BandDamping) || !AllOnes(BandFollow);
    static bool AllOnes(float[] a) { foreach (var x in a) if (x != 1f) return false; return true; }

    public Vector3 GravityVector => new(0f, -9.81f * Gravity, 0f);

    public void Clamp()
    {
        // Non-finite input (NaN/Infinity typed into a field) falls back to the default first.
        var d = Defaults;
        static float F(float v, float fallback) => float.IsFinite(v) ? v : fallback;
        Stiffness = F(Stiffness, d.Stiffness); VolumeStiffness = F(VolumeStiffness, d.VolumeStiffness);
        Damping = F(Damping, d.Damping); Follow = F(Follow, d.Follow); RigidFrequency = F(RigidFrequency, d.RigidFrequency);
        RigidThreshold = F(RigidThreshold, d.RigidThreshold); Mass = F(Mass, d.Mass); Gravity = F(Gravity, d.Gravity);
        LinearDamping = F(LinearDamping, d.LinearDamping); PointRadiusFactor = F(PointRadiusFactor, d.PointRadiusFactor);
        Substep = F(Substep, d.Substep); Friction = F(Friction, d.Friction);

        Cells = Math.Clamp(Cells, 1, 32);
        Stiffness = Math.Clamp(Stiffness, 0.2f, 60f);
        VolumeStiffness = VolumeStiffness <= 0 ? 0 : Math.Clamp(VolumeStiffness, 0.2f, 60f);
        Damping = Math.Clamp(Damping, 0.01f, 4f);
        Follow = Math.Clamp(Follow, 0.05f, 60f);
        RigidFrequency = Math.Clamp(RigidFrequency, 1f, 60f);
        RigidThreshold = Math.Clamp(RigidThreshold, 0f, 0.5f);
        Mass = Math.Clamp(Mass, 0.01f, 1000f);
        Gravity = Math.Clamp(Gravity, -10f, 10f);
        LinearDamping = Math.Clamp(LinearDamping, 0f, 50f);
        PointRadiusFactor = Math.Clamp(PointRadiusFactor, 0.02f, 0.5f);
        Substep = Math.Clamp(Substep, 1f / 480f, 1f / 30f);
        MaxSubsteps = Math.Clamp(MaxSubsteps, 1, 16);
        Friction = Math.Clamp(Friction, 0f, 4f);
        SolverIterations = Math.Clamp(SolverIterations, 1, 32);
        BandStiffness = ClampBand(BandStiffness); BandDamping = ClampBand(BandDamping); BandFollow = ClampBand(BandFollow);
    }

    static float[] ClampBand(float[] a)
    {
        var o = Ones();
        if (a != null)
            for (int i = 0; i < o.Length && i < a.Length; i++) o[i] = float.IsFinite(a[i]) ? Math.Clamp(a[i], 0.05f, 20f) : 1f;
        return o;
    }

    public SoftbodySettings Clone()
    {
        var c = (SoftbodySettings)MemberwiseClone();
        c.BandStiffness = (float[])BandStiffness?.Clone() ?? Ones();
        c.BandDamping = (float[])BandDamping?.Clone() ?? Ones();
        c.BandFollow = (float[])BandFollow?.Clone() ?? Ones();
        return c;
    }

    /// <summary>True when the band multipliers equal o's.</summary>
    public bool SameBands(SoftbodySettings o) =>
        BandStiffness.AsSpan().SequenceEqual(o.BandStiffness) && BandDamping.AsSpan().SequenceEqual(o.BandDamping) &&
        BandFollow.AsSpan().SequenceEqual(o.BandFollow);

    static readonly SoftbodySettings Defaults = new();

    /// <summary>
    /// True when both describe the same cage: lattice size, pinned set, collision radius and substep timing.
    /// Everything else (spring frequencies, damping, mass, gravity, drag, friction, iterations, collide)
    /// can be applied to a running simulation with <see cref="CageSimulation.ApplyTuning"/>.
    /// Compare clamped settings.
    /// </summary>
    public bool SameStructure(SoftbodySettings o) =>
        o != null && Cells == o.Cells && RigidThreshold == o.RigidThreshold && PointRadiusFactor == o.PointRadiusFactor &&
        Substep == o.Substep && MaxSubsteps == o.MaxSubsteps;

    /// <summary>Stable text of the structural settings only (see <see cref="SameStructure"/>).</summary>
    public string StructureKey =>
        $"cells={Cells} pin={RigidThreshold:R} radius={PointRadiusFactor:R} substep={Substep:R} maxSubsteps={MaxSubsteps}";

    /// <summary>Copies the live-tunable settings (everything outside <see cref="SameStructure"/>).</summary>
    public void CopyTunablesFrom(SoftbodySettings o)
    {
        Stiffness = o.Stiffness; VolumeStiffness = o.VolumeStiffness; Damping = o.Damping; Follow = o.Follow;
        RigidFrequency = o.RigidFrequency; Mass = o.Mass; Gravity = o.Gravity; LinearDamping = o.LinearDamping;
        Collide = o.Collide; Friction = o.Friction; SolverIterations = o.SolverIterations;
        BandStiffness = (float[])o.BandStiffness.Clone(); BandDamping = (float[])o.BandDamping.Clone(); BandFollow = (float[])o.BandFollow.Clone();
    }

    /// <summary>Stable text form, used to detect changes.</summary>
    public override string ToString() =>
        $"cells={Cells} stiffness={Stiffness:0.###} volume={VolumeStiffness:0.###} damping={Damping:0.###} follow={Follow:0.###} " +
        $"rigid={RigidFrequency:0.###} rigidThreshold={RigidThreshold:0.###} mass={Mass:0.###} gravity={Gravity:0.###} " +
        $"linearDamping={LinearDamping:0.###} collide={Collide} radius={PointRadiusFactor:0.###} substep={Substep:0.#####} " +
        $"maxSubsteps={MaxSubsteps} friction={Friction:0.###} iterations={SolverIterations}" + BandsText();

    string BandsText()
    {
        if (!HasBands) return "";
        var sb = new System.Text.StringBuilder(" bands");
        for (int i = 0; i < HueBands.Count; i++)
            if (BandStiffness[i] != 1f || BandDamping[i] != 1f || BandFollow[i] != 1f)
                sb.Append($" {HueBands.Names[i]}=x{BandStiffness[i]:0.###}/x{BandDamping[i]:0.###}/x{BandFollow[i]:0.###}");
        return sb.ToString();
    }
}

/// <summary>Position, rotation and (axis) scale of the slot the mesh is rendered in.</summary>
public struct AnchorPose
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    public AnchorPose(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Position = position;
        Rotation = Quaternion.Normalize(rotation);
        Scale = SafeScale(scale);
    }

    public static AnchorPose Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    public readonly Vector3 TransformPoint(Vector3 local) => Position + Vector3.Transform(local * Scale, Rotation);

    public readonly Vector3 InverseTransformPoint(Vector3 world) =>
        Vector3.Transform(world - Position, Quaternion.Conjugate(Rotation)) / Scale;

    public static AnchorPose Lerp(in AnchorPose a, in AnchorPose b, float t) => new()
    {
        Position = Vector3.Lerp(a.Position, b.Position, t),
        Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t),
        Scale = Vector3.Lerp(a.Scale, b.Scale, t),
    };

    static Vector3 SafeScale(Vector3 s)
    {
        static float F(float v) => float.IsFinite(v) && MathF.Abs(v) > 1e-6f ? v : (v < 0 ? -1e-6f : 1e-6f);
        return new Vector3(F(s.X), F(s.Y), F(s.Z));
    }

    public readonly bool IsFinite =>
        float.IsFinite(Position.X) && float.IsFinite(Position.Y) && float.IsFinite(Position.Z) &&
        float.IsFinite(Rotation.X) && float.IsFinite(Rotation.Y) && float.IsFinite(Rotation.Z) && float.IsFinite(Rotation.W) &&
        float.IsFinite(Scale.X) && float.IsFinite(Scale.Y) && float.IsFinite(Scale.Z);
}
