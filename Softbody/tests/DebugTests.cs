using System.Diagnostics;
using System.Numerics;
using Softbody.Core;

namespace Softbody.Tests;

// 0.10.0 debug views: the snapshot taken from a running cage and the geometry built from it.
static partial class Program
{
    static void DebugTests()
    {
        Console.WriteLine("TEST debug views: cells and colliders drawn from a running cage (ground, a sphere and a box pressing in)");
        var m = Cube(30);
        var s = new SoftbodySettings { Cells = 6 };
        using var rig = Rig(m, s, AnchorPose.Identity);
        rig.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
        var proxies = new[]
        {
            ProxyShape.Sphere(new Vector3(0, 0.62f, 0), 0.12f, key: 1),
            ProxyShape.Box(new Vector3(0.36f, 0.45f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f), new Vector3(0.08f, 0.08f, 0.08f), key: 2),
        };
        rig.Sim.SetProxies(proxies);
        for (int f = 0; f < 30; f++) rig.Step(Dt, AnchorPose.Identity);

        var snap = new DebugSnapshot();
        rig.Sim.FillDebug(snap, cells: true, colliders: true);
        snap.HasSearch = true; snap.RayOrigin = new Vector3(0, 0.3f, 0); snap.SearchCenter = snap.RayOrigin; snap.SearchSize = new Vector3(2.1f);
        var view = new DebugView();
        var geo = new DebugGeometry();
        var sw = Stopwatch.StartNew();
        var c = view.Build(snap, geo);
        double ms = sw.Elapsed.TotalMilliseconds;

        var cage = rig.Cage;
        // expected lattice edges, straight from the grid: neighbours along one axis that are both cage points
        int expected = 0;
        for (int k = 0; k < cage.Nz; k++)
            for (int j = 0; j < cage.Ny; j++)
                for (int i = 0; i < cage.Nx; i++)
                {
                    if (cage.Index(i, j, k) < 0) continue;
                    if (i + 1 < cage.Nx && cage.Index(i + 1, j, k) >= 0) expected++;
                    if (j + 1 < cage.Ny && cage.Index(i, j + 1, k) >= 0) expected++;
                    if (k + 1 < cage.Nz && cage.Index(i, j, k + 1) >= 0) expected++;
                }
        int lattice = view.LatticeEdges(cage).Length / 2;
        int dynamicPoints = cage.PointCount - cage.KinematicCount;
        Console.WriteLine($"  cage {cage.PointCount} points ({cage.KinematicCount} pinned), {cage.EdgeCount} tet edges, {lattice} lattice edges (grid says {expected})");
        Console.WriteLine($"  drawn: {c.Lines} lines, {c.Points} point markers, {c.Spheres} collision spheres ({c.Touching} touching), {c.Proxies} proxies; " +
                          $"{c.Vertices} vertices, {c.Triangles} triangles, built in {ms:0.00} ms");
        Check(lattice == expected && lattice < cage.EdgeCount, "cells view draws the cells' edges (every grid neighbour pair in the cage), not the tetrahedra's diagonals");
        Check(c.Lines == lattice && c.Points == cage.PointCount, "every lattice edge and every cage point is drawn");
        Check(c.Spheres == dynamicPoints, "colliders view: one collision sphere per simulated (unpinned) point");
        Check(c.Proxies == 2 && snap.Proxies.Count == 2 && snap.HasGround, "colliders view: both proxies and the ground are in the snapshot");
        Check(c.Touching > 0 && c.Touching < c.Spheres, "some collision spheres touch (the sphere and box pressing in, the floor), not all");

        bool finite = true, inRange = true, alphaOk = true;
        for (int i = 0; i < geo.VertexCount; i++)
        {
            var p = geo.Positions[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) finite = false;
            var col = geo.Colors[i];
            if (!(col.W >= 0f && col.W <= 1f && col.X >= 0f && col.X <= 1f && col.Y >= 0f && col.Y <= 1f && col.Z >= 0f && col.Z <= 1f)) alphaOk = false;
        }
        for (int i = 0; i < geo.IndexCount; i++) if ((uint)geo.Indices[i] >= (uint)geo.VertexCount) inRange = false;
        Check(finite && inRange && alphaOk && geo.IndexCount % 3 == 0, "geometry is valid: finite positions, indices in range, colours in 0..1");

        // touching is judged per point against the actual shapes
        var top = new Vector3(0, 0.62f - 0.12f, 0);
        Check(DebugView.Touching(snap, top, snap.RadiusWorld) && !DebugView.Touching(snap, new Vector3(0, 0.3f, 0), 0.01f),
              "touch test: a point at the sphere's surface touches, one in the middle of the cube does not");

        // each toggle on its own
        rig.Sim.FillDebug(snap, cells: true, colliders: false);
        var cells = view.Build(snap, geo);
        rig.Sim.FillDebug(snap, cells: false, colliders: true);
        var cols = view.Build(snap, geo);
        Check(cells.Spheres == 0 && cells.Proxies == 0 && cells.Lines == lattice, "cells only: no collision spheres or proxies");
        Check(cols.Lines == 0 && cols.Points == 0 && cols.Spheres == dynamicPoints && cols.Proxies == 2, "colliders only: no cage edges or markers, all the collision shapes");
        rig.Sim.FillDebug(snap, cells: false, colliders: false);
        var none = view.Build(snap, geo);
        Check(none.Vertices == 0, "both off: nothing is built");

        // the vertex cap
        rig.Sim.FillDebug(snap, cells: true, colliders: true);
        var small = new DebugGeometry { MaxVertices = 2000 };
        var cut = view.Build(snap, small);
        Check(cut.Truncated && small.VertexCount <= 2000 && cut.Proxies == 2, "a vertex cap stops the build early (big things first: proxies and ground survive)");

        // the untangler's points are flagged
        Array.Clear(snap.Untangling);
        snap.Untangling[Array.FindIndex(cage.Kinematic, k => !k)] = true;
        geo.Clear();
        view.Build(snap, geo);
        bool red = false;
        for (int i = 0; i < geo.VertexCount; i++) if (geo.Colors[i] == DebugView.Untangle) { red = true; break; }
        Check(red, "points the untangler is pulling back are drawn red");

        // white offset lines: a point pushed away from where it wants to be
        rig.Sim.FillDebug(snap, cells: true, colliders: false);
        int pushed = Array.FindIndex(cage.Kinematic, k => !k);
        snap.Points[pushed] = snap.Targets[pushed] + new Vector3(0, 0.05f, 0);
        var pushedCounts = view.Build(snap, geo);
        Check(pushedCounts.Offsets >= 1, "a point pushed out of its rest place gets a white line from where it should be");

        // collide off: the snapshot drops the ground and proxies, and no spheres are drawn
        var off = new SoftbodySettings { Cells = 6, Collide = false };
        rig.ApplyTuning(off);
        rig.Sim.FillDebug(snap, cells: false, colliders: true);
        snap.HasSearch = false;
        var nc = view.Build(snap, geo);
        Check(!snap.HasGround && snap.Proxies.Count == 0 && nc.Spheres == 0, "with Collide off the colliders view shows nothing to collide with");
        Check(ms < 20, $"building a full debug view of this cage is quick ({ms:0.00} ms, off the world thread in the plugin)");
    }
}
