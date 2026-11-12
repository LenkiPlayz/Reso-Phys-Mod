using System.Numerics;
using Softbody.Core.Fluid;
using Softbody.Core.Flesh;

namespace Softbody.Tests;

// 0.15.0: the particle liquid (Position Based Fluids) and its surface mesh, engine-free.
static partial class Program
{
    const float FluidDt = 1f / 60f;

    /// <summary>An open tank: a floor and four walls (thick boxes), inner size w x d, walls h high, floor top at y = 0.</summary>
    static List<FleshCollider> Tank(float w, float d, float h)
    {
        float t = 0.1f;
        return new List<FleshCollider>
        {
            FleshCollider.Box(new Vector3(0, -t, 0), Quaternion.Identity, new Vector3(w / 2 + 2 * t, t, d / 2 + 2 * t)),
            FleshCollider.Box(new Vector3(-w / 2 - t, h / 2, 0), Quaternion.Identity, new Vector3(t, h / 2, d / 2 + 2 * t)),
            FleshCollider.Box(new Vector3(w / 2 + t, h / 2, 0), Quaternion.Identity, new Vector3(t, h / 2, d / 2 + 2 * t)),
            FleshCollider.Box(new Vector3(0, h / 2, -d / 2 - t), Quaternion.Identity, new Vector3(w / 2, h / 2, t)),
            FleshCollider.Box(new Vector3(0, h / 2, d / 2 + t), Quaternion.Identity, new Vector3(w / 2, h / 2, t)),
        };
    }

    static void Run(FluidSim sim, float seconds, Action<int> perFrame = null)
    {
        int frames = (int)(seconds / FluidDt);
        for (int f = 0; f < frames; f++) { perFrame?.Invoke(f); sim.Step(FluidDt); }
    }

    static (float mean, float max) Densities(FluidSim sim, int sample = 400)
    {
        double sum = 0; float max = 0; int n = 0;
        int stride = Math.Max(1, sim.Count / sample);
        for (int i = 0; i < sim.Count; i += stride) { float d = sim.RelativeDensity(i); sum += d; max = MathF.Max(max, d); n++; }
        return (n > 0 ? (float)(sum / n) : 0, max);
    }

    static void FluidTests()
    {
        Console.WriteLine("TEST fluid (0.15.0): position based fluids with liquids per emitter, and the surface mesh");

        // ---- a dam break in a tank
        {
            var sim = new FluidSim(new FluidSettings());
            sim.Colliders.AddRange(Tank(0.6f, 0.4f, 0.5f));
            int water = sim.AddSource(FluidLiquid.Water);
            int n0 = sim.FillBox(water, new Vector3(-0.15f, 0.15f, 0f), Quaternion.Identity, new Vector3(0.15f, 0.15f, 0.2f), Vector3.Zero);
            Run(sim, 3f);
            int outside = 0; float maxSpeed = 0, top = 0, meanSpeed = 0;
            for (int i = 0; i < sim.Count; i++)
            {
                var p = sim.X[i];
                if (MathF.Abs(p.X) > 0.3f + 0.01f || MathF.Abs(p.Z) > 0.2f + 0.01f || p.Y < -0.01f || p.Y > 0.6f) outside++;
                maxSpeed = MathF.Max(maxSpeed, sim.V[i].Length()); top = MathF.Max(top, p.Y); meanSpeed += sim.V[i].Length() / sim.Count;
            }
            var (mean, max) = Densities(sim);
            // the water should have spread to a layer of about n0 * s^3 / (0.6 * 0.4)
            float expected = n0 * sim.ParticleVolume / (0.6f * 0.4f);
            Console.WriteLine($"  dam break: {n0} particles ({n0 * sim.ParticleVolume * 1000:0.0} L) in a 60 x 40 cm tank, after 3 s: {sim.Count} left, {outside} outside the tank, " +
                              $"density {mean:0.00} of rest on average ({max:0.00} at most), moving {meanSpeed:0.000} m/s on average (fastest {maxSpeed:0.00}), top {top * 100:0.0} cm (a flat layer would be {expected * 100:0.0} cm); " +
                              $"{sim.MeanNeighbours:0} neighbours each, step {sim.StepMs:0.00} ms");
            Check(sim.Count == n0 && outside == 0, "a dam break stays in its tank: no particle lost or through a wall");
            Check(mean > 0.85f && mean < 1.15f && max < 1.6f, "the water keeps its volume (density near rest, nothing squashed)");
            Check(meanSpeed < 0.1f && top > 0.6f * expected && top < 1.5f * expected, "it settles into a layer (only sloshing gently) about as deep as the water's volume over the tank's floor");
        }

        // ---- the pour rate
        {
            var sim = new FluidSim(new FluidSettings());
            int src = sim.AddSource(FluidLiquid.Water);
            var e = new FluidEmitterState { Source = src, Mode = FluidEmitMode.Pour, Active = true, Position = new Vector3(0, 2, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), Rate = 2f, Speed = 1f, NozzleRadius = 0.03f };
            Run(sim, 1f, f => sim.Emit(e, FluidDt));
            float litres = sim.Count * sim.ParticleVolume * 1000f;
            Console.WriteLine($"  pouring 2 L/s for 1 s: {sim.Count} particles, {litres:0.00} L");
            Check(MathF.Abs(litres - 2f) < 0.05f, "an emitter pours its rate in litres per second");
        }

        // ---- honey against water: a block dropped onto a floor spreads less when viscous
        float Spread(float viscosity)
        {
            var sim = new FluidSim(new FluidSettings());
            sim.Colliders.Add(FleshCollider.Box(new Vector3(0, -0.1f, 0), Quaternion.Identity, new Vector3(2f, 0.1f, 2f)));
            var l = FluidLiquid.Water; l.Viscosity = viscosity;
            int src = sim.AddSource(l);
            sim.FillBox(src, new Vector3(0, 0.12f, 0), Quaternion.Identity, new Vector3(0.1f, 0.1f, 0.1f), Vector3.Zero);
            Run(sim, 1f);
            float r = 0; for (int i = 0; i < sim.Count; i++) r += new Vector2(sim.X[i].X, sim.X[i].Z).Length();
            return r / sim.Count;
        }
        float sWater = Spread(0.02f), sHoney = Spread(0.6f);
        Console.WriteLine($"  a 20 cm block dropped on a floor, after 1 s: water spread to {sWater * 100:0.0} cm on average from the middle, honey {sHoney * 100:0.0} cm");
        Check(sHoney < 0.8f * sWater, "a viscous liquid (honey) flows slower than water");

        // ---- cohesion: a floating blob holds together
        float BlobSize(float cohesion)
        {
            var sim = new FluidSim(new FluidSettings());
            var l = FluidLiquid.Water; l.Gravity = 0f; l.Cohesion = cohesion; l.Damping = 0f;
            int src = sim.AddSource(l);
            sim.FillBox(src, Vector3.Zero, Quaternion.Identity, new Vector3(0.05f, 0.05f, 0.05f), Vector3.Zero);
            var rng = new Random(5);
            for (int i = 0; i < sim.Count; i++) sim.V[i] = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 0.6f;
            Run(sim, 1f);
            var c = Vector3.Zero; for (int i = 0; i < sim.Count; i++) c += sim.X[i]; c /= sim.Count;
            float r = 0; for (int i = 0; i < sim.Count; i++) r += Vector3.Distance(sim.X[i], c);
            return r / sim.Count;
        }
        float loose = BlobSize(0f), held = BlobSize(0.8f);
        Console.WriteLine($"  a floating 10 cm blob kicked apart, after 1 s: {loose * 100:0.0} cm from its middle on average without cohesion, {held * 100:0.0} cm with cohesion 0.8");
        Check(held < 0.4f * loose, "a cohesive liquid holds together as a drop");

        // ---- 0.25.2: cohesion above 1 (Lenki: "cohesion and adhesion is limited from 0-1"): a 20 cm block dropped on a floor beads
        // up into a blob that settles, and a value past the top (5) is held to it. As a force, cohesion past about 5 overshot (10
        // shook, 20 threw water through the floor); above 1 it now thickens the liquid too (FluidLiquid.CohesionViscosity)
        (float radius, float median, bool bad) Bead(float cohesion)
        {
            var sim = new FluidSim(new FluidSettings());
            sim.Colliders.Add(FleshCollider.Box(new Vector3(0, -0.1f, 0), Quaternion.Identity, new Vector3(2, 0.1f, 2)));
            var l = FluidLiquid.Water; l.Cohesion = cohesion;
            int src = sim.AddSource(l);
            sim.FillBox(src, new Vector3(0, 0.3f, 0), Quaternion.Identity, new Vector3(0.1f), Vector3.Zero);
            Run(sim, 4f);
            var c = Vector3.Zero; for (int i = 0; i < sim.Count; i++) c += sim.X[i]; c /= sim.Count;
            float r = 0; bool bad = false;
            for (int i = 0; i < sim.Count; i++) { var q = sim.X[i]; r += MathF.Sqrt((q.X - c.X) * (q.X - c.X) + (q.Z - c.Z) * (q.Z - c.Z)); if (!float.IsFinite(q.Y) || q.Y < -0.05f) bad = true; }
            var sp = Enumerable.Range(0, sim.Count).Select(i => sim.V[i].Length()).OrderBy(v => v).ToArray();
            return (r / sim.Count, sp[sp.Length / 2], bad);
        }
        var bead1 = Bead(1f); var bead5 = Bead(5f); var bead20 = Bead(20f);
        Console.WriteLine($"  a 20 cm block dropped on a floor, after 4 s: cohesion 1 spreads to {bead1.radius * 100:0.0} cm, cohesion 5 beads to {bead5.radius * 100:0.0} cm (median speed {bead5.median * 100:0.0} cm/s), 20 (held to 5) {bead20.radius * 100:0.0} cm (median {bead20.median * 100:0.0} cm/s)");
        Check(bead5.radius < 0.5f * bead1.radius && bead5.median < 0.01f && !bead5.bad && !bead20.bad && bead20.median < 0.01f && MathF.Abs(bead20.radius - bead5.radius) < 0.01f,
              "a strongly cohesive liquid (up to 5) beads up and settles; past 5 it is held to 5");

        // ---- oil floats on water
        {
            var sim = new FluidSim(new FluidSettings());
            sim.Colliders.AddRange(Tank(0.3f, 0.3f, 0.6f));
            var oilL = FluidLiquid.Water; oilL.Density = 0.6f; oilL.Color = new Vector4(0.9f, 0.7f, 0.1f, 0.8f);
            int water = sim.AddSource(FluidLiquid.Water), oil = sim.AddSource(oilL);
            // the oil starts at the bottom, the water on top
            sim.FillBox(oil, new Vector3(0, 0.075f, 0), Quaternion.Identity, new Vector3(0.15f, 0.075f, 0.15f), Vector3.Zero);
            sim.FillBox(water, new Vector3(0, 0.225f, 0), Quaternion.Identity, new Vector3(0.15f, 0.075f, 0.15f), Vector3.Zero);
            Run(sim, 6f); // (0.21.0: 6 s; it overturns more slowly, and at 4 s the two were level)
            double yw = 0, yo = 0; int nw = 0, no = 0;
            for (int i = 0; i < sim.Count; i++) { if (sim.Src[i] == water) { yw += sim.X[i].Y; nw++; } else { yo += sim.X[i].Y; no++; } }
            Console.WriteLine($"  oil (density 0.6) under water in a tank, after 6 s: oil at {yo / no * 100:0.0} cm on average, water at {yw / nw * 100:0.0} cm");
            Check(yo / no > yw / nw, "a lighter liquid rises above a heavier one (oil floats on water)");
        }

        // ---- no particle inside a solid box
        {
            var sim = new FluidSim(new FluidSettings());
            sim.Colliders.Add(FleshCollider.Box(new Vector3(0, -0.1f, 0), Quaternion.Identity, new Vector3(2f, 0.1f, 2f)));
            var block = FleshCollider.Box(new Vector3(0, 0.1f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f), new Vector3(0.1f, 0.1f, 0.1f));
            sim.Colliders.Add(block);
            int src = sim.AddSource(FluidLiquid.Water);
            sim.FillBox(src, new Vector3(0, 0.45f, 0), Quaternion.Identity, new Vector3(0.12f, 0.1f, 0.12f), Vector3.Zero);
            Run(sim, 2f);
            int inside = 0;
            for (int i = 0; i < sim.Count; i++)
            {
                var local = Vector3.Transform(sim.X[i] - block.A, Quaternion.Conjugate(block.Q));
                if (MathF.Abs(local.X) < block.B.X - 0.003f && MathF.Abs(local.Y) < block.B.Y - 0.003f && MathF.Abs(local.Z) < block.B.Z - 0.003f) inside++;
            }
            Console.WriteLine($"  water poured over a box on a floor, after 2 s: {inside} of {sim.Count} particles inside the box");
            Check(inside == 0, "the liquid flows around solid colliders, never through them");
        }

        // ---- a hand pushes the water
        {
            var sim = new FluidSim(new FluidSettings());
            sim.Colliders.AddRange(Tank(0.8f, 0.3f, 0.4f));
            int src = sim.AddSource(FluidLiquid.Water);
            sim.FillBox(src, new Vector3(0, 0.06f, 0), Quaternion.Identity, new Vector3(0.4f, 0.06f, 0.15f), Vector3.Zero);
            Run(sim, 1f);
            int walls = sim.Colliders.Count;
            float pushedX = 0; int near = 0, inside = 0;
            for (int f = 0; f < 45; f++)
            {
                var hand = new Vector3(-0.3f + 0.6f * f / 45f, 0.05f, 0f);
                var sphere = FleshCollider.Sphere(hand, 0.05f, (byte)FluidSim.CategoryHands);
                sphere.Velocity = new Vector3(0.6f / (45f * FluidDt), 0, 0);
                if (sim.Colliders.Count > walls) sim.Colliders.RemoveAt(walls);
                sim.Colliders.Add(sphere);
                sim.Step(FluidDt);
                if (f == 30)
                    for (int i = 0; i < sim.Count; i++)
                    {
                        float d = Vector3.Distance(sim.X[i], hand);
                        if (d < 0.05f - 0.003f) inside++;
                        if (d < 0.12f && sim.X[i].X > hand.X) { pushedX += sim.V[i].X; near++; }
                    }
            }
            Console.WriteLine($"  a 10 cm hand sphere swept through a shallow tank at 0.8 m/s: {inside} particles inside it, the water just ahead moving {pushedX / Math.Max(1, near):0.00} m/s along with it");
            Check(inside == 0 && near > 0 && pushedX / near > 0.05f, "a user's hand pushes the water ahead of it (one way)");
        }

        // ---- 0.15.2, Lenki's first in-game notes: friction did nothing, MaxParticles and Refill seemed not to work
        {
            float Slide(float friction)
            {
                var sim = new FluidSim(new FluidSettings());
                sim.Colliders.Add(FleshCollider.Box(new Vector3(0, -0.5f, 0), Quaternion.Identity, new Vector3(50, 0.5f, 50)));
                var l = FluidLiquid.Water; l.Friction = friction; l.Viscosity = 0.02f;
                int src = sim.AddSource(l);
                sim.FillBox(src, new Vector3(0, 0.05f, 0), Quaternion.Identity, new Vector3(0.1f, 0.05f, 0.1f), new Vector3(2f, 0, 0));
                Run(sim, 1.5f);
                float x = 0; for (int i = 0; i < sim.Count; i++) x += sim.X[i].X; return x / sim.Count;
            }
            float none = Slide(0f), full = Slide(1f);
            Console.WriteLine($"  a block of water sliding on a floor at 2 m/s, after 1.5 s: {none * 100:0} cm with no friction, {full * 100:0} cm with Friction 1");
            Check(full < 0.5f * none, "Friction slows water sliding over a surface (contact counts in every iteration, not only the last)");

            var dsim = new FluidSim(new FluidSettings());
            dsim.Colliders.Add(FleshCollider.Box(new Vector3(0, -0.5f, 0), Quaternion.Identity, new Vector3(50, 0.5f, 50)));
            var dir = new FluidDirector(dsim);
            var pour = new FluidEmitterInput { Id = 1, Mode = FluidEmitMode.Pour, Active = true, Pouring = true, Position = new Vector3(0, 1, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), Rate = 5f, Speed = 1f, NozzleRadius = 0.03f, MaxParticles = 100, Liquid = FluidLiquid.Water };
            var vol = new FluidEmitterInput { Id = 2, Mode = FluidEmitMode.Volume, Active = true, Position = new Vector3(2, 0.2f, 0), Rotation = Quaternion.Identity, VolumeHalf = new Vector3(0.1f), MaxParticles = 4000, Liquid = FluidLiquid.Water };
            var inputs = new List<FluidEmitterInput> { pour, vol };
            void Frames(int n) { for (int f = 0; f < n; f++) { dir.Sync(inputs, FluidDt); dir.Step(FluidDt); } }
            Frames(180);
            int capped = dir.CountOf(1), filled = dir.CountOf(2);
            pour.MaxParticles = 40; inputs[0] = pour;
            Frames(1);
            int trimmed = dir.CountOf(1);
            vol.ClearCount++; inputs[1] = vol;
            Frames(1);
            int cleared = dir.CountOf(2);
            Frames(30);
            int stillClear = dir.CountOf(2);
            vol.RefillCount++; vol.Position = new Vector3(3, 0.2f, 0); inputs[1] = vol;
            Frames(1);
            int refilled = dir.CountOf(2);
            float refillX = 0; int rn = 0; for (int i = 0; i < dsim.Count; i++) if (dsim.Src[i] == dsim.Src[dsim.Count - 1]) { refillX += dsim.X[i].X; rn++; }
            inputs.RemoveAt(0);
            Frames(1);
            int gone = dir.CountOf(1);
            Console.WriteLine($"  emitters: pouring 5 L/s with MaxParticles 100 for 3 s keeps {capped}; MaxParticles lowered to 40 keeps {trimmed}; a 20 cm volume fills {filled}, " +
                              $"ClearLiquid leaves {cleared} (and {stillClear} after half a second), Refill at a new place gives {refilled}; a removed emitter leaves {gone}");
            Check(capped == 100 && trimmed == 40, "MaxParticles caps an emitter (pouring stops at it, and lowering it removes the oldest particles over it)");
            Check(filled == 512 && cleared == 0 && stillClear == 0 && refilled == 512, "a Volume emitter fills its block, ClearLiquid empties it (it stays empty), and Refill fills it again");
            Check(gone == 0, "an emitter that is gone takes its liquid with it");
        }

        // ---- 0.15.1, the in-game freeze: a stream poured from 1.5 m fell through a single-sided mesh floor, and the water
        // falling to the kill height stretched the surface's grid over 100 m
        {
            var sim = new FluidSim(new FluidSettings());
            sim.Settings.KillMin = new Vector3(-50, 1.5f - 20, -50); sim.Settings.KillMax = new Vector3(50, 51.5f, 50);
            var (fpos, ftris) = BumpyFloor(40, 80f, new Vector3(-40, 0, -40));
            sim.Colliders.Add(FleshCollider.MeshShape(TriangleBvh.Build(fpos, ftris), Matrix4x4.Identity, false));
            int src = sim.AddSource(FluidLiquid.Water);
            var e = new FluidEmitterState { Source = src, Mode = FluidEmitMode.Pour, Active = true, Position = new Vector3(0, 1.5f, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f), Rate = 1f, Speed = 1.5f, NozzleRadius = 0.02f };
            var surf = new FluidSurface();
            double worst = 0;
            for (int f = 0; f < 300; f++) { sim.Emit(e, FluidDt); sim.Step(FluidDt); surf.Build(sim.X, sim.Src, sim.Count, sim.Liquids, sim.Spacing); if (f > 10) worst = Math.Max(worst, sim.StepMs + surf.BuildMs); }
            int below = 0; for (int i = 0; i < sim.Count; i++) if (sim.X[i].Y < -0.01f) below++;
            Console.WriteLine($"  pouring 1 L/s from 1.5 m onto a single-sided mesh floor for 5 s: {sim.Count} particles, {below} below the floor; worst frame {worst:0.0} ms (step and surface)");
            Check(sim.Count > 300 && below == 0, "a stream poured from a height lands on a mesh floor (single-sided triangles) instead of falling through it");
            Check(worst < 30, "pouring onto a floor stays cheap every frame (no frame near the old freeze)");

            var sim2 = new FluidSim(new FluidSettings());
            sim2.Settings.KillMin = new Vector3(-50, 1.5f - 20, -50); sim2.Settings.KillMax = new Vector3(50, 51.5f, 50);
            int src2 = sim2.AddSource(FluidLiquid.Water);
            e.Source = src2;
            var surf2 = new FluidSurface();
            double worst2 = 0;
            for (int f = 0; f < 600; f++) { sim2.Emit(e, FluidDt); sim2.Step(FluidDt); surf2.Build(sim2.X, sim2.Src, sim2.Count, sim2.Liquids, sim2.Spacing); if (f > 10) worst2 = Math.Max(worst2, surf2.BuildMs); }
            float lowest = 0; for (int i = 0; i < sim2.Count; i++) lowest = MathF.Min(lowest, sim2.X[i].Y);
            Console.WriteLine($"  pouring into nothing for 10 s: {sim2.Count} particles kept (the rest removed below {lowest:0.0} m), surface at most {worst2:0.0} ms ({surf2.Bricks} blocks)");
            Check(lowest > -18.6f && worst2 < 15, "liquid falling away is removed 20 m below its emitter, and the surface stays cheap however far it spreads (sparse grid)");
        }

        // ---- the surface
        {
            var sim = new FluidSim(new FluidSettings());
            sim.Colliders.AddRange(Tank(0.6f, 0.4f, 0.5f));
            int src = sim.AddSource(FluidLiquid.Water);
            sim.FillBox(src, new Vector3(0, 0.08f, 0), Quaternion.Identity, new Vector3(0.3f, 0.08f, 0.2f), Vector3.Zero);
            Run(sim, 1.5f);
            var surf = new FluidSurface();
            surf.Build(sim.X, sim.Src, sim.Count, sim.Liquids, sim.Spacing);
            // the top of the pool: vertices over the middle face up
            int up = 0, total = 0, outward = 0, tris = surf.IndexCount / 3;
            float topY = 0; int topN = 0;
            for (int v = 0; v < surf.VertexCount; v++)
            {
                var p = surf.Positions[v];
                if (MathF.Abs(p.X) < 0.2f && MathF.Abs(p.Z) < 0.12f && p.Y > 0.05f) { total++; if (surf.Normals[v].Y > 0.8f) up++; topY += p.Y; topN++; }
            }
            for (int t = 0; t < tris; t++)
            {
                int a = surf.Indices[3 * t], b = surf.Indices[3 * t + 1], c = surf.Indices[3 * t + 2];
                var fn = Vector3.Cross(surf.Positions[b] - surf.Positions[a], surf.Positions[c] - surf.Positions[a]);
                if (Vector3.Dot(fn, surf.Normals[a] + surf.Normals[b] + surf.Normals[c]) > 0f) outward++;
            }
            float waterTop = 0; for (int i = 0; i < sim.Count; i++) waterTop = MathF.Max(waterTop, sim.X[i].Y);
            Console.WriteLine($"  surface of a settled pool ({sim.Count} particles): {surf.VertexCount} vertices, {tris} triangles, cell {surf.Cell * 1000:0} mm, built in {surf.BuildMs:0.0} ms; " +
                              $"{(total > 0 ? 100f * up / total : 0):0}% of the top's normals face up, top at {(topN > 0 ? topY / topN * 100 : 0):0.0} cm (highest particle {waterTop * 100:0.0} cm); {100f * outward / Math.Max(1, tris):0.0}% of triangles wound outward");
            Check(surf.VertexCount > 100 && total > 0 && up > 0.8f * total, "the surface of a still pool is flat on top (its normals face up)");
            Check(outward > 0.97f * tris, "the surface's triangles are wound outward, matching their normals");
            Check(topN > 0 && MathF.Abs(topY / topN - waterTop) < 1.5f * sim.Spacing, "the surface sits on the water (within a particle or so of the top layer)");
        }
        {
            // a lone drop: a closed bead (every edge shared by two triangles)
            var sim = new FluidSim(new FluidSettings());
            var l = FluidLiquid.Water; l.Gravity = 0f;
            int src = sim.AddSource(l);
            sim.FillBox(src, Vector3.Zero, Quaternion.Identity, new Vector3(0.04f, 0.04f, 0.04f), Vector3.Zero);
            var surf = new FluidSurface();
            surf.Build(sim.X, sim.Src, sim.Count, sim.Liquids, sim.Spacing);
            var edges = new Dictionary<(int, int), int>();
            for (int t = 0; t < surf.IndexCount; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = surf.Indices[t + e], b = surf.Indices[t + (e + 1) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
            int open = edges.Values.Count(c => c != 2);
            Console.WriteLine($"  a lone drop of {sim.Count} particles: {surf.VertexCount} vertices, {surf.IndexCount / 3} triangles, {open} edges not shared by exactly two");
            Check(surf.VertexCount > 20 && open == 0, "a drop's surface is closed");
        }

        // ---- timing
        foreach (int target in new[] { 5000, 10000, 20000 })
        {
            var sim = new FluidSim(new FluidSettings { MaxParticles = target + 100 });
            sim.Colliders.AddRange(Tank(1.2f, 1.2f, 0.8f));
            int src = sim.AddSource(FluidLiquid.Water);
            float side = MathF.Cbrt(target) * sim.Spacing;
            sim.FillBox(src, new Vector3(-0.6f + side / 2, side / 2, -0.6f + side / 2), Quaternion.Identity, new Vector3(side / 2), Vector3.Zero, target);
            var times = new List<double>();
            var speedLog = new System.Text.StringBuilder(); var escLog = new List<string>();
            for (int f = 0; f < 90; f++)
            {
                sim.Step(FluidDt); if (f >= 30) times.Add(sim.StepMs);
                if (Environment.GetEnvironmentVariable("FLUID_DIAG") == "1" && target == 10000 && escLog.Count < 6)
                    for (int i = 0; i < sim.Count && escLog.Count < 6; i++)
                    {
                        var p = sim.X[i];
                        if (MathF.Abs(p.X) > 0.6f || MathF.Abs(p.Z) > 0.6f) escLog.Add($"f{f}: at ({p.X:0.000}, {p.Y:0.000}, {p.Z:0.000}) moving ({sim.V[i].X:0.0}, {sim.V[i].Y:0.0}, {sim.V[i].Z:0.0})");
                    }
                if (Environment.GetEnvironmentVariable("FLUID_DIAG") == "1" && target == 10000 && f % 6 == 0)
                {
                    float vmax = 0, ymax = 0; int fast = 0;
                    for (int i = 0; i < sim.Count; i++) { float sp = sim.V[i].Length(); vmax = MathF.Max(vmax, sp); ymax = MathF.Max(ymax, sim.X[i].Y); if (sp > 3f) fast++; }
                    speedLog.Append($" f{f}:{vmax:0.0}m/s,{fast}fast,top{ymax:0.00}");
                }
            }
            if (speedLog.Length > 0) Console.WriteLine("    speeds:" + speedLog);
            foreach (var e in escLog) Console.WriteLine("    first out: " + e);
            times.Sort();
            if (Environment.GetEnvironmentVariable("FLUID_DIAG") == "1")
            {
                int outside = 0; var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
                for (int i = 0; i < sim.Count; i++) { var p = sim.X[i]; lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); if (MathF.Abs(p.X) > 0.61f || MathF.Abs(p.Z) > 0.61f || p.Y < -0.01f) outside++; }
                Console.WriteLine($"    diag: {outside} outside the tank, bounds {lo} .. {hi}; phases per frame (ms): {string.Join(" ", sim.PhaseMs.Select(x => (x / 90).ToString("0.00")))}");
            }
            var surf = new FluidSurface();
            surf.Build(sim.X, sim.Src, sim.Count, sim.Liquids, sim.Spacing);
            surf.Build(sim.X, sim.Src, sim.Count, sim.Liquids, sim.Spacing);
            Console.WriteLine($"  {sim.Count} particles splashing in a tank: step median {times[times.Count / 2]:0.00} ms, 95th {times[(int)(times.Count * 0.95)]:0.00} ms ({sim.Settings.Substeps} substeps, {sim.Settings.Iterations} iterations, {Environment.ProcessorCount} threads); " +
                              $"surface {surf.BuildMs:0.0} ms, {surf.VertexCount} vertices");
            if (target == 10000)
            {
                int outsideT = 0; for (int i = 0; i < sim.Count; i++) { var p = sim.X[i]; if (MathF.Abs(p.X) > 0.61f || MathF.Abs(p.Z) > 0.61f || p.Y < -0.01f) outsideT++; }
                Check(outsideT == 0, $"a violent splash stays in its tank too (nothing flung out: {outsideT} outside)");
                // (generous: 0.20.0's 2 substeps of 3 iterations took about 6.5 ms on an idle machine; 0.21.0's 4 of 2 calm a deep
                // pool but take about twice that while the liquid is moving (a settled one sleeps); more with a game running)
                Check(times[times.Count / 2] < 25.0, "10,000 particles step within a few ms on the worker");
            }
        }
    }
}
