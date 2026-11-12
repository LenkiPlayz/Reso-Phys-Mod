using System.Diagnostics;
using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.FluidGpu;

namespace Softbody;

/// <summary>
/// 0.16.0: the plugin's side of the GPU fluid in the Softbody renderer fork. The focused world's <see cref="FluidWorld"/>
/// writes its emitters and the colliders near its liquid into the shared channel each update (one writer at a time: the
/// world that wrote last owns it, and a new owner makes the renderer start over), and reads back the counts, bounds and
/// timings the renderer reports. The renderer is there when its handshake event exists (checked once a second).
/// </summary>
static class FluidGpuBridge
{
    static FluidChannelIO _io;
    static long _checkedAt;
    static bool _available;
    static int _frame;
    static float _time;
    static FluidWorld _writer;
    static readonly int[] _pairs = new int[FluidChannel.MaxEmitters * 2];
    static readonly Dictionary<int, int> _counts = new();
    static FluidEmitterRecord[] _em = new FluidEmitterRecord[16];
    static FluidColliderRecord[] _col = new FluidColliderRecord[256];
    static readonly List<int> _hits = new();

    public static FluidBack Back;
    public static bool HaveBack;

    /// <summary>Whether this client's renderer runs the GPU fluid (its handshake event exists).</summary>
    public static bool Available
    {
        get
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _checkedAt > Stopwatch.Frequency)
            {
                _checkedAt = now;
                try
                {
                    _available = EventWaitHandle.TryOpenExisting(FluidChannel.HandshakeName, out var h);
                    h?.Dispose();
                }
                catch { _available = false; }
            }
            return _available;
        }
    }

    static FluidChannelIO IO => _io ??= FluidChannelIO.Open();

    /// <summary>An emitter's particles as the renderer last reported them.</summary>
    public static int CountOf(int id) => _counts.TryGetValue(id, out int n) ? n : 0;

    /// <summary>Reads the renderer's block (its counts by emitter, its bounds and timings).</summary>
    public static bool ReadBack(int owner)
    {
        if (!IO.TryReadBack(out var b, _pairs)) return HaveBack;
        Back = b;
        HaveBack = true;
        _counts.Clear();
        if (b.Owner == owner)
            for (int i = 0; i < Math.Min(b.EmitterCountsN, FluidChannel.MaxEmitters); i++) _counts[_pairs[2 * i]] = _pairs[2 * i + 1];
        return true;
    }

    public static FluidEmitterRecord[] Emitters(int n) { if (_em.Length < n) _em = new FluidEmitterRecord[Math.Max(n, _em.Length * 2)]; return _em; }

    /// <summary>Writes this world's frame (it owns the channel from now on). 0.26.0: with its gas emitters, its gas renderer's
    /// placeholder mesh and the local user's head (the puffs face it).</summary>
    public static void Write(FluidWorld world, int owner, float dt, int meshAssetId, float particleSize, float surfaceDetail, float dropletSize, bool classic, int capacity, int emitterCount, List<FleshCollider> colliders, Vector3 lo, Vector3 hi, out int colliderCount, out bool truncated,
                             GasEmitterRecord[] gas = null, int gasCount = 0, int gasMeshAssetId = -1, Vector3 head = default)
    {
        colliderCount = Colliders(colliders, lo, hi, out truncated);
        _frame++;
        _time += dt;
        var h = new FluidHeader
        {
            Frame = _frame, Active = 1, Owner = owner, Dt = dt, Time = _time, ParticleSize = particleSize, Substeps = 4, Iterations = 2,
            MaxParticles = capacity, MeshAssetId = meshAssetId, SurfaceDetail = surfaceDetail, DropletSize = dropletSize,
            Flags = classic ? 1 : 0,
            GasMeshAssetId = gasMeshAssetId, HeadX = head.X, HeadY = head.Y, HeadZ = head.Z,
        };
        IO.WriteFrame(h, _em, emitterCount, _col, colliderCount, gas, gas == null ? 0 : gasCount);
        _writer = world;
    }

    /// <summary>The world stops using the GPU (if it owns the channel, the renderer stops showing its liquid).</summary>
    public static void Release(FluidWorld world, int owner)
    {
        if (_writer != world) return;
        _writer = null;
        try { IO.WriteInactive(owner); } catch { }
    }

    /// <summary>The colliders as records: meshes as their triangles inside the box (they cannot all go).</summary>
    static int Colliders(List<FleshCollider> shapes, Vector3 lo, Vector3 hi, out bool truncatedOut)
    {
        int n = 0;
        bool truncated = false;
        void Add(in FleshCollider c, int kind)
        {
            if (n >= FluidChannel.MaxColliders) { truncated = true; return; }
            if (_col.Length <= n) Array.Resize(ref _col, Math.Min(FluidChannel.MaxColliders, Math.Max(n + 1, _col.Length * 2)));
            _col[n++] = new FluidColliderRecord
            {
                Kind = kind, Category = c.Source == 0 ? FluidChannel.CategoryWorld : c.Source,
                AX = c.A.X, AY = c.A.Y, AZ = c.A.Z, BX = c.B.X, BY = c.B.Y, BZ = c.B.Z, CX = c.C.X, CY = c.C.Y, CZ = c.C.Z, R = c.R,
                QX = c.Q.X, QY = c.Q.Y, QZ = c.Q.Z, QW = c.Q.W, TwoSided = c.TwoSided ? 1 : 0,
                VelX = c.Velocity.X, VelY = c.Velocity.Y, VelZ = c.Velocity.Z,
            };
        }
        foreach (var c0 in shapes)
        {
            var c = c0;
            if (c.Kind != FleshColliderKind.Mesh) { Add(c, (int)c.Kind); continue; }
            if (c.Mesh == null) continue;
            bool flip = c.M.GetDeterminant() < 0f;
            FleshCollider.TransformBox(lo, hi, c.MInv, out var qmin, out var qmax);
            _hits.Clear();
            c.Mesh.Query(qmin, qmax, _hits);
            foreach (int t in _hits)
            {
                var tri = FleshCollider.Triangle(Vector3.Transform(c.Mesh.A[t], c.M), Vector3.Transform(c.Mesh.B[t], c.M), Vector3.Transform(c.Mesh.C[t], c.M), c.TwoSided);
                if (flip) (tri.B, tri.C) = (tri.C, tri.B);
                tri.Source = c.Source; tri.Velocity = c.Velocity;
                Add(tri, FluidChannel.KindTriangle);
                if (truncated) break;
            }
            if (truncated) break;
        }
        truncatedOut = truncated;
        return n;
    }
}
