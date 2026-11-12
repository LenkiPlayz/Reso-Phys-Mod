// 0.26.0: what the gas emitters do each frame (DESIGN-gas.md): every emitter gets a slot (its parameters on the GPU, read by
// its puffs for as long as they live), and adds puffs at its rate (held so its live puffs stay within MaxPuffs), in its nozzle's
// disc, along its forward (strayed by Spread), with its speed plus its own motion. A gone emitter's slot is kept a minute (the
// longest a puff lives) before another may take it. netstandard2.0.
#nullable disable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Softbody.FluidGpu
{
    public sealed class GpuGasDirector
    {
        /// <summary>This frame's new puffs: pairs of (position, its emitter's slot as uint bits) and (velocity, lifetime).</summary>
        public Vector4[] Spawns = new Vector4[256];
        public int SpawnCount;
        /// <summary>The emitters by slot, and how many slots are in use (the highest one + 1).</summary>
        public readonly GpuGasEmitter[] Emitters = new GpuGasEmitter[GpuFluidSim.MaxGasEmitters];
        public int EmitterSlots;
        /// <summary>The colliders' categories the gas hits, and where its emitters are.</summary>
        public uint CollideMask;
        public Vector3 EmitMin, EmitMax;
        /// <summary>0.27.0: the strongest Vortices of the kept emitters (0: no vortex particles run).</summary>
        public float Vortices;

        readonly Dictionary<int, int> _slotOf = new Dictionary<int, int>();
        readonly float[] _carry = new float[GpuFluidSim.MaxGasEmitters];
        readonly float[] _lastSeen = new float[GpuFluidSim.MaxGasEmitters];
        readonly bool[] _used = new bool[GpuFluidSim.MaxGasEmitters];
        readonly uint[] _mask = new uint[GpuFluidSim.MaxGasEmitters];
        readonly float[] _vortices = new float[GpuFluidSim.MaxGasEmitters];
        float _clock;
        uint _seed = 0x9E3779B9u;

        float Rand() { _seed = _seed * 1664525u + 1013904223u; return (_seed >> 8) * (1f / 16777216f); }

        static float C(float v, float lo, float hi, float d) => float.IsNaN(v) || float.IsInfinity(v) ? d : Math.Max(lo, Math.Min(hi, v));

        public void Sync(GasEmitterRecord[] records, int count, float dt)
        {
            if (!(dt > 0f) || float.IsNaN(dt) || float.IsInfinity(dt)) dt = 0f;
            _clock += dt;
            SpawnCount = 0; CollideMask = 0; Vortices = 0f;
            EmitMin = new Vector3(float.MaxValue); EmitMax = new Vector3(float.MinValue);
            for (int k = 0; k < count && records != null; k++)
            {
                var r = records[k];
                if (!_slotOf.TryGetValue(r.Id, out int s))
                {
                    s = Array.IndexOf(_used, false);
                    if (s < 0) continue;   // more emitters than slots: the rest add nothing
                    _slotOf[r.Id] = s; _used[s] = true; _carry[s] = 0f;
                }
                _lastSeen[s] = _clock;
                _mask[s] = r.CollideMask;
                if (s + 1 > EmitterSlots) EmitterSlots = s + 1;
                float life = C(r.Lifetime, 0.1f, 60f, 4f);
                _vortices[s] = C(r.Vortices, 0f, 10f, 0f);   // 0.27.0
                Emitters[s] = new GpuGasEmitter
                {
                    A = new Vector4(C(r.Buoyancy, -20f, 20f, 0.5f), C(r.Drag, 0f, 50f, 1.5f), C(r.Swirl, 0f, 10f, 0.3f), C(r.Spread, 0f, 1f, 0.2f)),
                    B = new Vector4(C(r.StartSize, 0.005f, 10f, 0.1f), C(r.EndSize, 0.005f, 20f, 0.5f), life, GpuFluidSim.AsFloat(r.CollideMask)),
                    Color = new Vector4(C(r.ColR, 0f, 16f, 0.8f), C(r.ColG, 0f, 16f, 0.8f), C(r.ColB, 0f, 16f, 0.8f), C(r.ColA, 0f, 1f, 0.3f)),
                    D = new Vector4(_vortices[s], 0f, 0f, 0f),
                };
                var pos = new Vector3(r.PosX, r.PosY, r.PosZ);
                if (float.IsNaN(pos.X + pos.Y + pos.Z) || float.IsInfinity(pos.X + pos.Y + pos.Z)) continue;
                EmitMin = Vector3.Min(EmitMin, pos); EmitMax = Vector3.Max(EmitMax, pos);
                if (r.Active == 0 || r.Emitting == 0 || dt <= 0f) { _carry[s] = 0f; continue; }
                float rate = Math.Min(C(r.Rate, 0f, 5000f, 20f), Math.Max(1, r.MaxPuffs) / life);
                _carry[s] += rate * dt;
                int n = (int)_carry[s];
                _carry[s] -= n;
                var q = new Quaternion(r.RotX, r.RotY, r.RotZ, r.RotW);
                float ql = q.Length();
                q = ql > 1e-6f && !float.IsNaN(ql) ? Quaternion.Normalize(q) : Quaternion.Identity;
                var fwd = Vector3.Transform(Vector3.UnitZ, q);
                float speed = C(r.Speed, 0f, 50f, 0.5f), radius = C(r.NozzleRadius, 0f, 10f, 0.02f), spread = Emitters[s].A.W;
                var vel = new Vector3(C(r.VelX, -100f, 100f, 0f), C(r.VelY, -100f, 100f, 0f), C(r.VelZ, -100f, 100f, 0f));
                for (int j = 0; j < n && SpawnCount < GpuFluidSim.MaxGasSpawns; j++)
                {
                    float a = Rand() * 6.2831853f, rr = radius * (float)Math.Sqrt(Rand());
                    var off = Vector3.Transform(new Vector3((float)Math.Cos(a) * rr, (float)Math.Sin(a) * rr, 0f), q);
                    var dir = fwd + new Vector3(Rand() - 0.5f, Rand() - 0.5f, Rand() - 0.5f) * (2f * spread);
                    float dl = dir.Length();
                    dir = dl > 1e-6f ? dir / dl : fwd;
                    // (spread along the frame's travel, so a stream is even rather than a train of clumps, one a frame)
                    var p = pos + off + fwd * (Rand() * speed * dt);
                    var v = dir * speed + vel;
                    if (Spawns.Length < (SpawnCount + 1) * 2) Array.Resize(ref Spawns, Spawns.Length * 2);
                    Spawns[SpawnCount * 2] = new Vector4(p, GpuFluidSim.AsFloat((uint)s));
                    Spawns[SpawnCount * 2 + 1] = new Vector4(v, life * (0.8f + 0.4f * Rand()));
                    SpawnCount++;
                }
            }
            // a gone emitter's slot frees up a minute later (the longest a puff lives), so its last puffs keep its look
            List<int> gone = null;
            foreach (var kv in _slotOf)
                if (_clock - _lastSeen[kv.Value] > 60f) (gone ?? (gone = new List<int>())).Add(kv.Key);
            if (gone != null) foreach (int id in gone) { _used[_slotOf[id]] = false; _slotOf.Remove(id); }
            // what the puffs hit: every kept slot's (a stopped or removed emitter's last puffs still collide while they live)
            // (0.27.0: and the vortex strength, the strongest kept slot's: its last puffs still swirl)
            for (int s = 0; s < EmitterSlots; s++) if (_used[s]) { CollideMask |= _mask[s]; Vortices = Math.Max(Vortices, _vortices[s]); }
        }

        /// <summary>Forgets every emitter (a world change).</summary>
        public void Reset()
        {
            _slotOf.Clear();
            Array.Clear(_used, 0, _used.Length);
            Array.Clear(_vortices, 0, _vortices.Length);
            EmitterSlots = 0; SpawnCount = 0; Vortices = 0f;
        }
    }
}
