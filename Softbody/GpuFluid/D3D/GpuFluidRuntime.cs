// 0.16.0: one GPU fluid, driven by the plugin's frame records (emitters and colliders, as they arrive through the shared
// channel): the director's spawns and liquids, the colliders' grid over where the liquid can be, then the step and the
// surface. The renderer fork runs this on its render thread; the offline tests run it directly. netstandard2.0.
#nullable disable
using System;
using System.Collections.Generic;
using System.Numerics;
using SharpDX.Direct3D11;

namespace Softbody.FluidGpu
{
    public sealed class GpuFluidRuntime : IDisposable
    {
        public readonly GpuFluidSim Sim;
        public readonly GpuFluidDirector Director;
        public readonly GpuShapeGrid Shapes = new GpuShapeGrid();
        public double CpuMs { get; private set; }
        float _autoDetail = -1f, _autoRequested = -1f;
        int _autoChanged = -1;
        readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();

        public GpuFluidRuntime(Device device, GpuFluidConfig config, string hlsl, Func<string, byte[]> cachedBytecode = null)
        {
            Sim = new GpuFluidSim(device, config, hlsl, cachedBytecode);
            Director = new GpuFluidDirector(Sim.Config.MaxSources);
            Gas = new GpuGasDirector();
            Shapes.MaxShapes = Sim.Config.MaxShapes;
        }

        public GpuFluidStats Stats => Sim.Stats;

        /// <summary>0.26.0: how close a puff's centre comes to a collider (metres).</summary>
        public const float GasRadius = 0.03f;

        public int CountOf(int emitterId) => Director.CountOf(emitterId, Sim.Stats);

        /// <summary>0.26.0: the gas emitters' puffs (DESIGN-gas.md).</summary>
        public readonly GpuGasDirector Gas;
        static readonly GasEmitterRecord[] NoGas = new GasEmitterRecord[0];

        /// <summary>One frame of the plugin's records (0.17.0: and the surface's detail, live; 0.26.0: and the gas, and the local
        /// user's head that its puffs face; 0.27.0: and how the renderer draws the gas and the foam: as quads for its smoke shader,
        /// lit from <paramref name="towardsLight"/> (the world's main directional light in the gas's space; zero: none), or discs).</summary>
        public void Frame(float dt, FluidEmitterRecord[] emitters, int emitterCount, FluidColliderRecord[] colliders, int colliderCount, bool buildSurface, float surfaceDetail = 1f, float dropletSize = 1.5f,
                          GasEmitterRecord[] gas = null, int gasCount = 0, Vector3 head = default(Vector3),
                          Vector3 towardsLight = default(Vector3), bool gasQuads = false, bool foamQuads = false)
        {
            _sw.Restart();
            Sim.Poll();
            if (!(dt > 0f) || float.IsNaN(dt) || float.IsInfinity(dt)) dt = 0f;
            dt = Math.Min(dt, 1f / 30f);
            var stats = Sim.Stats;
            Director.Sync(emitters, emitterCount, dt, Sim.Frame + 1, stats, Sim.Spacing);
            Gas.Sync(gas ?? NoGas, gas == null ? 0 : gasCount, dt);
            Sim.SetKillBox(Director.KillMin, Director.KillMax);
            // 0.19.0: the surface's detail drops (just enough) while what it needs does not fit its vertices, indices or blocks,
            // and climbs back a tenth at a time while it uses under half of them: a big thin sheet at SurfaceDetail 3 needed far
            // more than there is, and what missed out left a patchwork of flat pieces
            float requested = float.IsNaN(surfaceDetail) || float.IsInfinity(surfaceDetail) ? 1f : Math.Max(0.5f, Math.Min(3f, surfaceDetail));
            // (a new asked-for detail applies at once; only a lack of room lowers it)
            if (_autoDetail < 0f || Math.Abs(requested - _autoRequested) > 1e-4f) { _autoDetail = requested; _autoRequested = requested; _autoChanged = Sim.Frame; }
            if (stats.Frame > _autoChanged)
            {
                float vr = (float)stats.VerticesNeeded / Sim.Config.VertexCapacity, ir = (float)stats.IndicesNeeded / Sim.Config.IndexCapacity;
                float br = (float)stats.BricksNeeded / Math.Max(1, Sim.MaxBricks);
                float f = 1f;
                if (vr > 1f) f = Math.Min(f, (float)Math.Sqrt(0.85f / vr));
                if (ir > 1f) f = Math.Min(f, (float)Math.Sqrt(0.85f / ir));
                if (br > 1f) f = Math.Min(f, (float)Math.Pow(0.85f / br, 1.0 / 3.0));
                if (f < 1f) { _autoDetail = Math.Max(0.25f, _autoDetail * f); _autoChanged = Sim.Frame; }
                else if (vr < 0.5f && ir < 0.5f && br < 0.5f && _autoDetail < requested) { _autoDetail = Math.Min(requested, _autoDetail * 1.1f); _autoChanged = Sim.Frame; }
            }
            Sim.SetSurfaceDetail(requested, _autoDetail);
            float maxVisc = 0f;
            // (0.27.0: a thick liquid takes no XSPH passes: the implicit solve is its viscosity)
            for (int i = 0; i < Director.SourceCount; i++) if (Director.Liquids[i].Alive != 0 && !(Director.Liquids[i].D.Y > 0f)) maxVisc = Math.Max(maxVisc, Director.Liquids[i].A.X);
            Sim.SetViscosityPasses((int)Math.Ceiling(maxVisc));
            Sim.SetDropletSize(dropletSize);
            if (emitterCount > 0 && Director.EmitMin.X <= Director.EmitMax.X) Sim.SetLatticeCenter(0.5f * (Director.EmitMin + Director.EmitMax));
            Sim.SetLiquids(Director.Liquids, Director.SourceCount);

            // where the liquid can be this frame: its bounds as last read back (moved on for the frames since), what is
            // spawned now, and the emitters
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            if (stats.HasBounds) { lo = stats.Min; hi = stats.Max; }
            for (int i = 0; i < Director.SpawnCount; i++)
            {
                var p = Director.Spawns[i].Pos;
                var q = new Vector3(p.X, p.Y, p.Z);
                lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q);
            }
            if (emitterCount > 0 && Director.EmitMin.X <= Director.EmitMax.X) { lo = Vector3.Min(lo, Director.EmitMin); hi = Vector3.Max(hi, Director.EmitMax); }
            // 0.26.0: and where the gas is (its puffs as last read back, the new ones, its emitters)
            if (stats.HasGasBounds) { lo = Vector3.Min(lo, stats.GasMin); hi = Vector3.Max(hi, stats.GasMax); }
            for (int i = 0; i < Gas.SpawnCount; i++)
            {
                var p = Gas.Spawns[2 * i];
                var q = new Vector3(p.X, p.Y, p.Z);
                lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q);
            }
            if (Gas.EmitMin.X <= Gas.EmitMax.X) { lo = Vector3.Min(lo, Gas.EmitMin); hi = Vector3.Max(hi, Gas.EmitMax); }
            // 0.27.0: and where the spray, foam and bubbles are (spray flies beyond the liquid)
            if (stats.HasDiffuseBounds) { lo = Vector3.Min(lo, stats.DiffuseMin); hi = Vector3.Max(hi, stats.DiffuseMax); }
            uint collideMask = Director.CollideMask | Gas.CollideMask;
            Shapes.Clear();
            if (lo.X <= hi.X && colliderCount > 0 && collideMask != 0)
            {
                // as far as the fastest particle can have gone since that readback (falling all the while)
                // (the readback can be many frames old when the GPU runs behind: then the box grows to match)
                int lag = stats.Frame >= 0 ? Math.Max(1, Sim.Frame + 1 - stats.Frame) : 8;
                float t = dt * (lag + 1);
                float speed = stats.Frame >= 0 ? Math.Min(Sim.Config.MaxSpeed, stats.MaxSpeed + 9.81f * t) : Sim.Config.MaxSpeed;
                float travel = Math.Min(speed * t, 20f);
                // (0.25.0: a substep is a fixed 60th of a second over Substeps, whatever the frame took)
                float reach = Sim.CollisionRadius + Sim.H + Sim.Config.MaxSpeed / (60f * Math.Max(1, Sim.Config.Substeps));
                var pad = new Vector3(travel + reach);
                lo -= pad; hi += pad;
                // (0.26.0: the kill box is the liquid's; the gas has its own, around its emitters)
                var kmin = Director.KillMin; var kmax = Director.KillMax;
                if (Gas.EmitMin.X <= Gas.EmitMax.X) { kmin = Vector3.Min(kmin, Gas.EmitMin - new Vector3(50f, 20f, 50f)); kmax = Vector3.Max(kmax, Gas.EmitMax + new Vector3(50f)); }
                lo = Vector3.Max(lo, kmin); hi = Vector3.Min(hi, kmax);
                // 0.19.0: moving shapes (users' parts, anything moving) first, still ones after (see Collide in the shader)
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < colliderCount; i++)
                    {
                        ref readonly var c = ref colliders[i];
                        bool moving = (c.Category != 0 && c.Category != FluidChannel.CategoryWorld) || c.VelX * c.VelX + c.VelY * c.VelY + c.VelZ * c.VelZ > 4e-4f;
                        if (moving != (pass == 0)) continue;
                        if ((c.Category & collideMask) == 0 && !(c.Category == 0 && (collideMask & FluidChannel.CategoryWorld) != 0)) continue;
                        if (!GpuShapeGrid.Overlaps(c, lo, hi)) continue;
                        if (!moving) _stillNow[StillKey(c)] = BoundsOf(c);
                        if (!Shapes.Add(c)) break;
                    }
                Shapes.Build(lo, hi, reach, Sim.H);
            }
            Sim.SetShapes(Shapes);
            // 0.21.0: a still shape near the liquid that is gone (or moved without a velocity: a floor taken away from under a
            // sleeping pool) wakes the sleepers where it was (0.23.0: only there; waking everything woke a pool in a room every
            // frame, as the room's triangles at the edge of the liquid's bounds came and went)
            // (within a particle of where it was: what then falls or flows wakes the rest)
            float wakeReach = Sim.CollisionRadius + Sim.Spacing;
            foreach (var kv in _stillLast)
                if (!_stillNow.ContainsKey(kv.Key)) Sim.WakeIn(kv.Value.lo - new Vector3(wakeReach), kv.Value.hi + new Vector3(wakeReach));
            (_stillLast, _stillNow) = (_stillNow, _stillLast);
            _stillNow.Clear();
            Sim.SetGas(Gas.Spawns, Gas.SpawnCount, Gas.Emitters, Gas.EmitterSlots, head, GasRadius);
            // 0.27.0: the smoke shader's density grid has cells of about 0.72 of a puff's mean size (a trilinear splat read back along a
            // line through a puff then gives about its own optical depth); the largest of the emitters'
            float meanSize = 0f;
            for (int i = 0; i < Gas.EmitterSlots; i++) meanSize = Math.Max(meanSize, 0.5f * (Gas.Emitters[i].B.X + Gas.Emitters[i].B.Y));
            Sim.SetSmoke(towardsLight, gasQuads, foamQuads, meanSize > 0f ? 0.72f * meanSize : 0.25f);
            Sim.SetVortices(Gas.Vortices);   // 0.27.0
            Sim.Step(dt, Director.Spawns, Director.SpawnCount, Director.Trim, buildSurface);
            CpuMs = _sw.Elapsed.TotalMilliseconds;
        }

        Dictionary<long, (Vector3 lo, Vector3 hi)> _stillLast = new Dictionary<long, (Vector3, Vector3)>(), _stillNow = new Dictionary<long, (Vector3, Vector3)>();

        static (Vector3 lo, Vector3 hi) BoundsOf(in FluidColliderRecord c)
        {
            var a = new Vector3(c.AX, c.AY, c.AZ); var b = new Vector3(c.BX, c.BY, c.BZ); var cc = new Vector3(c.CX, c.CY, c.CZ);
            switch (c.Kind)
            {
                case FluidChannel.KindTriangle: return (Vector3.Min(a, Vector3.Min(b, cc)), Vector3.Max(a, Vector3.Max(b, cc)));
                case 1: return (a - new Vector3(c.R), a + new Vector3(c.R));                                      // sphere
                case 2: case 4: case 5: return (Vector3.Min(a, b) - new Vector3(c.R), Vector3.Max(a, b) + new Vector3(c.R)); // capsule, cylinder, cone
                case 3:                                                                                            // box: its rotated half extents
                {
                    var q = new Quaternion(c.QX, c.QY, c.QZ, c.QW);
                    var e = Vector3.Abs(Vector3.Transform(new Vector3(b.X, 0, 0), q)) + Vector3.Abs(Vector3.Transform(new Vector3(0, b.Y, 0), q)) + Vector3.Abs(Vector3.Transform(new Vector3(0, 0, b.Z), q));
                    return (a - e, a + e);
                }
                default: return (new Vector3(-1e6f), new Vector3(1e6f));                                           // a plane: everything
            }
        }

        static long StillKey(in FluidColliderRecord c)
        {
            unchecked
            {
                long h = c.Kind * 31 + c.Category;
                foreach (float f in new[] { c.AX, c.AY, c.AZ, c.BX, c.BY, c.BZ, c.CX, c.CY, c.CZ, c.R, c.QX, c.QY, c.QZ, c.QW })
                    h = h * 1000003 + BitConverter.ToInt32(BitConverter.GetBytes(f), 0);
                return h * 31 + c.TwoSided;
            }
        }

        /// <summary>Removes all liquid and forgets the emitters (a world change).</summary>
        public void Reset()
        {
            Director.Sync(new FluidEmitterRecord[0], 0, 0f, Sim.Frame + 1, null, Sim.Spacing);
            Gas.Reset();
            Sim.Reset();
        }

        public void Dispose() => Sim.Dispose();
    }
}
