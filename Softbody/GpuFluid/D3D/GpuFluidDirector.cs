// 0.16.0: what the emitters do to the GPU fluid each frame (the CPU's FluidDirector, for a simulation whose counts arrive a
// few frames late): every emitter gets a source (a slot in the liquids table) with its liquid, kept live; Volume emitters
// fill their block when they first run and on Refill; ClearLiquid, Refill and a removed emitter retire the source (its
// particles are all removed on the GPU's next step, and the slot is reused once a readback shows it empty); pouring stops at
// MaxParticles counting what was poured since the last readback; a lowered MaxParticles removes exactly the particles over
// it (not the oldest, as the CPU does: which ones is up to the GPU's threads). The kill box is the CPU's: 50 m around the emitters, 20 m below. netstandard2.0.
#nullable disable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Softbody.FluidGpu
{
    public sealed class GpuFluidDirector
    {
        sealed class Entry
        {
            public int Source = -1;
            public int Refills, Clears;
            public bool Filled, HasLast;
            public Vector3 LastPos;
            public int Seen;
            public float Carry;
            public int PourIndex;
            public int TrimFrame = -1;
            // what it spawned in recent frames (frame, count), for counting what the last readback has not seen yet
            public readonly int[] SpawnFrame = new int[History];
            public readonly int[] SpawnCount = new int[History];
            public int SpawnNext;
        }

        sealed class SourceSlot
        {
            public bool Used;
            public int RetiredFrame = -1;   // >= 0: retired (its particles are being removed)
        }

        const int History = 64;
        readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        readonly List<int> _gone = new List<int>();
        readonly List<SourceSlot> _slots = new List<SourceSlot>();
        readonly Random _rng = new Random(1234);
        int _seen;

        public readonly int MaxSources;
        public GpuLiquid[] Liquids;
        /// <summary>How many entries of <see cref="Liquids"/> are in use (the highest slot + 1).</summary>
        public int SourceCount => _slots.Count;
        public GpuSpawn[] Spawns = new GpuSpawn[1024];
        public int SpawnCount;
        /// <summary>Some liquid has a share to lose this frame (a lowered MaxParticles).</summary>
        public bool Trim;
        public Vector3 KillMin = new Vector3(-1e4f), KillMax = new Vector3(1e4f);
        public uint CollideMask;
        public int Refilled;
        /// <summary>The emitters' positions this frame (for the bounds of the colliders' grid).</summary>
        public Vector3 EmitMin, EmitMax;

        public GpuFluidDirector(int maxSources) { MaxSources = Math.Max(1, maxSources); Liquids = new GpuLiquid[MaxSources]; }

        public bool Has(int id) => _entries.ContainsKey(id);

        /// <summary>An emitter's particles as last read back (0 if unknown).</summary>
        public int CountOf(int id, GpuFluidStats stats)
        {
            if (!_entries.TryGetValue(id, out var e) || e.Source < 0 || stats == null || e.Source >= stats.SourceCounts.Length) return 0;
            return (int)stats.SourceCounts[e.Source];
        }

        /// <summary>The ids of the emitters it knows.</summary>
        public IEnumerable<int> Ids => _entries.Keys;

        static float C(float v, float lo, float hi, float d) => float.IsNaN(v) || float.IsInfinity(v) ? d : Math.Max(lo, Math.Min(hi, v));

        static GpuLiquid LiquidOf(in FluidEmitterRecord r)
        {
            var col = new Vector4(C(r.ColR, 0f, 64f, 0.6f), C(r.ColG, 0f, 64f, 0.8f), C(r.ColB, 0f, 64f, 1f), C(r.ColA, 0f, 1f, 0.35f));
            // (0.25.2: cohesion up to 5, thickening the liquid above 1, and adhesion up to 10, as FluidLiquid.Sanitize)
            float visc = Math.Max(C(r.Viscosity, 0f, 20f, 0.02f), 0.75f * Math.Max(0f, C(r.Cohesion, 0f, 5f, 0.3f) - 1f));
            return new GpuLiquid
            {
                A = new Vector4(visc, C(r.Cohesion, 0f, 5f, 0.3f), C(r.Adhesion, 0f, 10f, 0.1f), C(r.Friction, 0f, 1f, 0.1f)),
                B = new Vector4(C(r.Density, 0.1f, 10f, 1f), C(r.Gravity, -10f, 10f, 1f), C(r.Damping, 0f, 50f, 0.05f), C(r.Lifetime, 0f, 1e6f, 0f)),
                Color = col,
                Mask = r.CollideMask,
                Alive = 1,
                // 0.27.0: its foam, and a thick liquid's real viscosity (its Viscosity, held to 15, on a log scale; 1 and under: XSPH)
                D = new Vector4(C(r.Foam, 0f, 10f, 0f),
                                r.Thick != 0 ? GpuFluidSim.KinematicViscosity(Math.Min(visc, GpuFluidSim.ThickViscosityCap)) : 0f,
                                C(r.FoamLifetime, 0.1f, 60f, 3f), 0f),
            };
        }

        int NewSource(in GpuLiquid liquid)
        {
            for (int i = 0; i < _slots.Count; i++)
                if (!_slots[i].Used && _slots[i].RetiredFrame < 0) { _slots[i].Used = true; Liquids[i] = liquid; return i; }
            if (_slots.Count >= MaxSources) return -1;
            _slots.Add(new SourceSlot { Used = true });
            Liquids[_slots.Count - 1] = liquid;
            return _slots.Count - 1;
        }

        void Retire(int source, int frame)
        {
            if (source < 0 || source >= _slots.Count) return;
            _slots[source].Used = false;
            _slots[source].RetiredFrame = frame;
            Liquids[source].Alive = 0;
            Liquids[source].Trim = 0;
        }

        /// <summary>
        /// Brings the GPU fluid in line with this frame's emitters. <paramref name="frame"/> is the step about to run (the
        /// simulation's Frame + 1); <paramref name="stats"/> the latest readback.
        /// </summary>
        public void Sync(FluidEmitterRecord[] emitters, int count, float dt, int frame, GpuFluidStats stats, float spacing)
        {
            _seen++;
            SpawnCount = 0; Trim = false; Refilled = 0;
            uint mask = 0;
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            // retired slots whose particles a readback after the retirement shows gone are free again
            for (int i = 0; i < _slots.Count; i++)
            {
                var sl = _slots[i];
                if (sl.RetiredFrame >= 0 && stats != null && stats.Frame > sl.RetiredFrame && (i >= stats.SourceCounts.Length || stats.SourceCounts[i] == 0))
                    sl.RetiredFrame = -1;
            }
            for (int k = 0; k < count; k++)
            {
                var r = emitters[k];
                var pos = new Vector3(r.PosX, r.PosY, r.PosZ);
                if (!Finite(pos)) continue;
                var rot = new Quaternion(r.RotX, r.RotY, r.RotZ, r.RotW);
                float ql = rot.Length();
                rot = ql > 1e-9f && !float.IsNaN(ql) ? Quaternion.Normalize(rot) : Quaternion.Identity;
                var liquid = LiquidOf(r);
                if (!_entries.TryGetValue(r.Id, out var en))
                {
                    en = new Entry { Source = NewSource(liquid), Refills = r.RefillCount, Clears = r.ClearCount };
                    _entries[r.Id] = en;
                }
                en.Seen = _seen;
                if (en.Source < 0) en.Source = NewSource(liquid);
                if (en.Source < 0) continue;
                // the liquid, live (keeping this frame's trim share off until decided below)
                Liquids[en.Source] = liquid;
                mask |= liquid.Mask;
                lo = Vector3.Min(lo, pos); hi = Vector3.Max(hi, pos);
                var vel = en.HasLast && dt > 0f ? (pos - en.LastPos) / dt : Vector3.Zero;
                if (!(vel.LengthSquared() < 400f)) vel = Vector3.Zero; // a teleport is not a throw
                en.LastPos = pos; en.HasLast = true;
                int cap = Math.Max(1, r.MaxParticles);
                // ClearLiquid: a fresh source, the old one's particles go (a cleared Volume stays empty until Refill)
                if (r.ClearCount != en.Clears)
                {
                    Retire(en.Source, frame);
                    en.Source = NewSource(liquid);
                    en.Clears = r.ClearCount;
                    ClearHistory(en);
                    if (en.Source < 0) continue;
                }
                int estimate = Estimate(en, stats);
                if (r.Mode == 1)
                {
                    if (r.Active != 0 && (!en.Filled || r.RefillCount != en.Refills))
                    {
                        if (estimate > 0 || en.Filled)
                        {
                            Retire(en.Source, frame);
                            en.Source = NewSource(liquid);
                            ClearHistory(en);
                            if (en.Source < 0) continue;
                        }
                        int added = FillBox(en.Source, pos, rot, Abs(new Vector3(r.HalfX, r.HalfY, r.HalfZ)), vel, cap, spacing, C(r.RandomVelocity, 0f, 1000f, 0f));
                        Record(en, frame, added);
                        en.Filled = true; en.Refills = r.RefillCount;
                        Refilled++;
                    }
                }
                else
                {
                    en.Refills = r.RefillCount;
                    // (a readback older than the spawn history cannot be counted on: no pouring until a newer one)
                    bool stale = stats != null && stats.Frame >= 0 && frame - stats.Frame >= History - 2;
                    if (r.Active != 0 && r.Pouring != 0 && !stale)
                    {
                        int added = Emit(en, pos, rot, vel, r.Rate, r.Speed, r.NozzleRadius, cap - estimate, dt, spacing, C(r.RandomVelocity, 0f, 1000f, 0f));
                        Record(en, frame, added);
                    }
                }
                // a lowered MaxParticles: the particles over it go (once the last trim shows in a readback)
                if (stats != null && stats.Frame >= 0 && en.Source < stats.SourceCounts.Length && stats.Frame >= en.TrimFrame)
                {
                    int have = (int)stats.SourceCounts[en.Source];
                    if (have > cap)
                    {
                        Liquids[en.Source].Trim = (uint)(have - cap);
                        en.TrimFrame = frame;
                        Trim = true;
                    }
                }
            }
            // emitters that are gone take their liquid with them
            _gone.Clear();
            foreach (var kv in _entries) if (kv.Value.Seen != _seen) _gone.Add(kv.Key);
            foreach (int id in _gone) { Retire(_entries[id].Source, frame); _entries.Remove(id); }
            CollideMask = mask;
            if (lo.X <= hi.X)
            {
                KillMin = lo - new Vector3(50f, 20f, 50f);
                KillMax = hi + new Vector3(50f, 50f, 50f);
                EmitMin = lo; EmitMax = hi;
            }
        }

        static bool Finite(Vector3 v) => !(float.IsNaN(v.X + v.Y + v.Z) || float.IsInfinity(v.X + v.Y + v.Z));
        static Vector3 Abs(Vector3 v) => Vector3.Abs(v);

        static void ClearHistory(Entry en) { Array.Clear(en.SpawnCount, 0, en.SpawnCount.Length); Array.Clear(en.SpawnFrame, 0, en.SpawnFrame.Length); en.TrimFrame = -1; }

        static void Record(Entry en, int frame, int added)
        {
            if (added <= 0) return;
            en.SpawnFrame[en.SpawnNext] = frame; en.SpawnCount[en.SpawnNext] = added;
            en.SpawnNext = (en.SpawnNext + 1) % en.SpawnFrame.Length;
        }

        /// <summary>The emitter's particles now: the last readback plus what it spawned since.</summary>
        static int Estimate(Entry en, GpuFluidStats stats)
        {
            int known = 0, since = -1;
            if (stats != null && stats.Frame >= 0 && en.Source < stats.SourceCounts.Length) { known = (int)stats.SourceCounts[en.Source]; since = stats.Frame; }
            for (int i = 0; i < en.SpawnFrame.Length; i++)
                if (en.SpawnCount[i] > 0 && en.SpawnFrame[i] > since) known += en.SpawnCount[i];
            return known;
        }

        bool Spawn(int source, Vector3 p, Vector3 v)
        {
            if (!Finite(p)) return false;
            if (SpawnCount >= Spawns.Length) Array.Resize(ref Spawns, Spawns.Length * 2);
            Spawns[SpawnCount++] = new GpuSpawn { Pos = new Vector4(p, GpuFluidSim.AsFloat((uint)source)), Vel = new Vector4(Finite(v) ? v : Vector3.Zero, 0f) };
            return true;
        }

        float J(float a) => (float)(_rng.NextDouble() * 2 - 1) * a;

        /// <summary>0.19.0: a random velocity, even over the ball of that speed.</summary>
        Vector3 RandomBall(float speed)
        {
            if (!(speed > 0f)) return Vector3.Zero;
            Vector3 v;
            do v = new Vector3(J(1f), J(1f), J(1f)); while (v.LengthSquared() > 1f);
            return v * speed;
        }

        int FillBox(int source, Vector3 center, Quaternion rotation, Vector3 half, Vector3 velocity, int max, float s, float random)
        {
            int nx = Math.Max(1, (int)(2f * half.X / s)), ny = Math.Max(1, (int)(2f * half.Y / s)), nz = Math.Max(1, (int)(2f * half.Z / s));
            int added = 0;
            for (int y = 0; y < ny; y++)
                for (int z = 0; z < nz; z++)
                    for (int x = 0; x < nx; x++)
                    {
                        if (added >= max) return added;
                        var local = new Vector3((x + 0.5f) * s - half.X, (y + 0.5f) * s - half.Y, (z + 0.5f) * s - half.Z) + new Vector3(J(0.05f * s), J(0.05f * s), J(0.05f * s));
                        if (!Spawn(source, center + Vector3.Transform(local, rotation), velocity + RandomBall(random))) return added;
                        added++;
                    }
            return added;
        }

        int Emit(Entry en, Vector3 position, Quaternion rotation, Vector3 velocity, float rate, float speedIn, float nozzle, int room, float dt, float s, float random)
        {
            if (!(dt > 0f)) return 0;
            float perSecond = Math.Max(0f, C(rate, 0f, 5000f, 0f)) * 0.001f / (s * s * s);
            float want = en.Carry + perSecond * dt;
            int n = (int)want;
            en.Carry = want - n;
            if (n <= 0) return 0;
            if (room <= 0) { en.Carry = 0f; return 0; }
            if (n > room) { n = room; en.Carry = 0f; }
            var dir = Vector3.Transform(Vector3.UnitZ, rotation);
            var ax = Vector3.Transform(Vector3.UnitX, rotation); var ay = Vector3.Transform(Vector3.UnitY, rotation);
            float radius = Math.Max(0f, C(nozzle, 0f, 10f, 0f)), speed = Math.Max(0f, C(speedIn, 0f, 100f, 0f));
            var vel = dir * speed + velocity;
            int added = 0;
            for (int k = 0; k < n; k++)
            {
                int idx = en.PourIndex++;
                float t = (idx % 64 + 0.5f) / 64f, ang = idx * 2.39996323f;
                float rr = radius * (float)Math.Sqrt(t);
                var off = ax * (rr * (float)Math.Cos(ang)) + ay * (rr * (float)Math.Sin(ang));
                float back = speed * dt * (k + 0.5f) / n;
                if (!Spawn(en.Source, position + off + dir * back, vel + RandomBall(random))) break;
                added++;
            }
            return added;
        }
    }
}
