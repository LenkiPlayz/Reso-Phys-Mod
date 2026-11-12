using System.Numerics;
using Softbody.Core;
using Softbody.Core.Flesh;

namespace Softbody.Tests;

// 0.26.3: the skinned softbody's shading with blendshapes and normal maps. Lenki's "blendshape test" (a skinned plane whose
// one shape key bends it a long way, normal mapped in game) showed the normal map going wrong on the shaped part, and much
// worse with the softbody on: 0.26.2 turned the normals as if the shape were off, and never turned the tangents.
static partial class Program
{
    static void FleshShadingTests()
    {
        Console.WriteLine("TEST flesh shading with blendshapes (0.26.3): normals turned on the blendshaped shape, tangents turned with them");
        var plane = RolledPlane(61);
        Console.WriteLine($"  plane: {plane.Count} vertices, a shape rolling it into a half cylinder (normals turn up to 90 degrees), " +
                          $"a 3 cm bump along the rolled normal at 45 degrees (its slope up to {plane.BumpSlopeDeg:0.0} degrees)");
        ShadingOnShape(plane, withShapeTangents: true);
        ShadingOnShape(plane, withShapeTangents: false);
        ShadingRestAndOff(plane);
        ShadingContactTurn(plane);
        ShadingMergeTangents();
        var path = FleshModel.DefaultPath();
        if (path != null && File.Exists(path)) ShadingRig(FleshModel.Load(path));
        else Console.WriteLine("  (rig check skipped: no test model at " + path + ")");
    }

    sealed class ShadePlane
    {
        public int N, Count;
        public Vector3[] Pos, Nrm, Dp, Dn, Dt, Off;   // rest, the shape's deltas, the bump (bind space)
        public Vector4[] Tan;
        public int[] Tri, Weld, Moving;
        public Vector3[] TrueN, TrueT;                // the shaped and bumped surface's exact normal and x direction
        public float BumpSlopeDeg;
    }

    /// <summary>A 1 m plane facing +z (tangents +x, w 1) and one shape that rolls it into a half cylinder about y (the front
    /// becomes the inside), with exact normal and tangent deltas; and a 3 cm bump along the rolled normal at 45 degrees.</summary>
    static ShadePlane RolledPlane(int N)
    {
        const float A = 0.03f, Sigma = 0.07f;
        float R = 1f / MathF.PI;           // x in [-0.5, 0.5] m -> theta in [-90, 90] degrees
        float x0 = R * MathF.PI / 4f;      // the bump's centre: theta 45 degrees
        int n = N * N;
        var p = new ShadePlane
        {
            N = N, Count = n, Pos = new Vector3[n], Nrm = new Vector3[n], Tan = new Vector4[n], Dp = new Vector3[n], Dn = new Vector3[n],
            Dt = new Vector3[n], Off = new Vector3[n], TrueN = new Vector3[n], TrueT = new Vector3[n], Weld = new int[n], Moving = new int[n],
        };
        float maxSlope = 0;
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                int v = j * N + i;
                float x = -0.5f + i / (float)(N - 1), y = -0.5f + j / (float)(N - 1);
                float th = x / R, s = MathF.Sin(th), c = MathF.Cos(th);
                var rolled = new Vector3(R * s, y, R * (1f - c));
                var rn = new Vector3(-s, 0f, c);
                var rt = new Vector3(c, 0f, s);
                p.Pos[v] = new Vector3(x, y, 0f); p.Nrm[v] = Vector3.UnitZ; p.Tan[v] = new Vector4(1f, 0f, 0f, 1f);
                p.Dp[v] = rolled - p.Pos[v]; p.Dn[v] = rn - Vector3.UnitZ; p.Dt[v] = rt - Vector3.UnitX;
                float g = MathF.Exp(-((x - x0) * (x - x0) + y * y) / (2f * Sigma * Sigma));
                float gx = -(x - x0) / (Sigma * Sigma) * g, gy = -y / (Sigma * Sigma) * g;
                p.Off[v] = rn * (A * g);
                // the exact surface P = rolled + A g n(theta), with dn/dx = -t / R
                var px = rt * (1f - A * g / R) + rn * (A * gx);
                var py = Vector3.UnitY + rn * (A * gy);
                p.TrueN[v] = Vector3.Normalize(Vector3.Cross(px, py));
                p.TrueT[v] = Vector3.Normalize(px);
                maxSlope = MathF.Max(maxSlope, ShDeg(p.TrueN[v], rn));
                p.Weld[v] = v; p.Moving[v] = v;
            }
        var tri = new List<int>();
        for (int j = 0; j + 1 < N; j++)
            for (int i = 0; i + 1 < N; i++)
            {
                int a = j * N + i, b = a + 1, c = a + N, d = c + 1;
                tri.Add(a); tri.Add(b); tri.Add(d);
                tri.Add(a); tri.Add(d); tri.Add(c);
            }
        p.Tri = tri.ToArray();
        p.BumpSlopeDeg = maxSlope;
        return p;
    }

    static BlendshapeDeltas ShShapes(ShadePlane p, bool withTangents) =>
        BlendshapeDeltas.FromSparse(1, b => (p.Moving, p.Dp, p.Dn, withTangents ? p.Dt : null));

    static float ShDeg(Vector3 a, Vector3 b)
    {
        float la = a.Length(), lb = b.Length();
        if (!(la > 0f) || !(lb > 0f)) return 180f;
        return MathF.Acos(Math.Clamp(Vector3.Dot(a, b) / (la * lb), -1f, 1f)) * 180f / MathF.PI;
    }

    static Vector3 ShXyz(Vector4 t) => new(t.X, t.Y, t.Z);

    /// <summary>The shape on and the bump: what the renderer shows (our output plus the shape's deltas) against the exact
    /// surface, now and as 0.26.2 sent it (turned on the plane as if the shape were off, tangents at rest).</summary>
    static void ShadingOnShape(ShadePlane p, bool withShapeTangents)
    {
        int n = p.Count;
        var fn = new FleshNormals(p.Pos, p.Nrm, p.Tri, p.Weld, p.Moving, ShShapes(p, withShapeTangents));
        var outPos = new Vector3[n]; var outNrm = new Vector3[n]; var outTan = new Vector4[n];
        fn.Compute(p.Moving, p.Off, p.Pos, p.Nrm, p.Tan, new[] { 1f }, outPos, outNrm, outTan);
        var old = new FleshNormals(p.Pos, p.Nrm, p.Tri, p.Weld, p.Moving);
        var oldPos = new Vector3[n]; var oldNrm = new Vector3[n];
        old.Compute(p.Moving, p.Off, p.Pos, p.Nrm, oldPos, oldNrm);
        var dtR = withShapeTangents ? p.Dt : new Vector3[n];   // what the renderer adds to the tangents
        float newErr = 0, oldErr = 0, newMean = 0, oldMean = 0, newOrtho = 0, oldOrtho = 0, newFrame = 0, oldFrame = 0, newTanErr = 0;
        bool posSame = true;
        int counted = 0;
        for (int j = 2; j < p.N - 2; j++)        // the outer rows only have triangles on one side
            for (int i = 2; i < p.N - 2; i++)
            {
                int v = j * p.N + i;
                posSame &= outPos[v] == oldPos[v];
                var nNew = outNrm[v] + p.Dn[v]; var tNew = ShXyz(outTan[v]) + dtR[v];
                var nOld = oldNrm[v] + p.Dn[v]; var tOld = ShXyz(p.Tan[v]) + dtR[v];
                float en = ShDeg(nNew, p.TrueN[v]), eo = ShDeg(nOld, p.TrueN[v]);
                newErr = MathF.Max(newErr, en); oldErr = MathF.Max(oldErr, eo); newMean += en; oldMean += eo; counted++;
                if (withShapeTangents)
                {
                    newOrtho = MathF.Max(newOrtho, MathF.Abs(90f - ShDeg(tNew, nNew)));
                    oldOrtho = MathF.Max(oldOrtho, MathF.Abs(90f - ShDeg(tOld, nOld)));
                    var inPlane = tNew - p.TrueN[v] * (Vector3.Dot(tNew, p.TrueN[v]) / tNew.Length());
                    newTanErr = MathF.Max(newTanErr, ShDeg(inPlane, p.TrueT[v]));
                }
                else
                {
                    // the renderer's own frame without the softbody: the rest tangent against the shaped normal
                    float van = ShDeg(ShXyz(p.Tan[v]), p.Nrm[v] + p.Dn[v]);
                    newFrame = MathF.Max(newFrame, MathF.Abs(ShDeg(tNew, nNew) - van));
                    oldFrame = MathF.Max(oldFrame, MathF.Abs(ShDeg(tOld, nOld) - van));
                }
            }
        newMean /= counted; oldMean /= counted;
        string which = withShapeTangents ? "with the mesh's own blendshape tangents" : "without blendshape tangents (as Resonite imports a model)";
        Console.WriteLine($"  {which}: normal vs the exact surface: 0.26.3 max {newErr:0.00} (mean {newMean:0.000}) degrees, 0.26.2 max {oldErr:0.00} (mean {oldMean:0.000})");
        if (withShapeTangents)
        {
            Console.WriteLine($"    tangent off a right angle to the normal: 0.26.3 max {newOrtho:0.0000} degrees, 0.26.2 max {oldOrtho:0.00}; " +
                              $"0.26.3 tangent vs the surface's own x direction max {newTanErr:0.00} degrees");
            Check(newErr < 1.5f && newErr * 4f < oldErr && posSame,
                  $"0.26.3: a bump on a blendshaped part turns the shown normals like the real surface (max {newErr:0.00} degrees off; 0.26.2 turned them as if the shape were off: {oldErr:0.0} degrees); positions unchanged");
            Check(newOrtho < 0.01f && oldOrtho > 2f && newTanErr < 1.5f,
                  $"0.26.3: with blendshape tangents, the shown tangent stays at right angles to the shown normal and follows the surface (0.26.2 left it skewed by up to {oldOrtho:0.0} degrees)");
        }
        else
        {
            Console.WriteLine($"    change of the tangent-normal angle from what the renderer shows without the softbody: 0.26.3 max {newFrame:0.0000} degrees, 0.26.2 max {oldFrame:0.00}");
            Check(newErr < 1.5f && newFrame < 0.01f && oldFrame > 2f,
                  $"0.26.3: without blendshape tangents the softbody keeps the normal map's frame as the renderer has it (0.26.2 skewed it by up to {oldFrame:0.0} more degrees)");
        }
    }

    /// <summary>At rest the output is the renderer's own; with every weight 0 it is exactly what it was without blendshapes.</summary>
    static void ShadingRestAndOff(ShadePlane p)
    {
        int n = p.Count;
        var fn = new FleshNormals(p.Pos, p.Nrm, p.Tri, p.Weld, p.Moving, ShShapes(p, true));
        var plain = new FleshNormals(p.Pos, p.Nrm, p.Tri, p.Weld, p.Moving);
        var zero = new Vector3[n];
        var oP = new Vector3[n]; var oN = new Vector3[n]; var oT = new Vector4[n];
        // the shape on, no offset: the renderer shows the shape's own normals and tangents
        fn.Compute(p.Moving, zero, p.Pos, p.Nrm, p.Tan, new[] { 1f }, oP, oN, oT);
        float worstN = 0, worstT = 0, worstP = 0;
        for (int v = 0; v < n; v++)
        {
            worstN = MathF.Max(worstN, Vector3.Distance(oN[v], p.Nrm[v]));
            worstT = MathF.Max(worstT, Vector4.Distance(oT[v], p.Tan[v]));
            worstP = MathF.Max(worstP, Vector3.Distance(oP[v], p.Pos[v]));
        }
        Check(worstN < 1e-5f && worstT < 1e-5f && worstP == 0f,
              $"0.26.3: with a shape on and the flesh at rest the output is the mesh's own normals, tangents and positions (the renderer adds the shape) (worst {Math.Max(worstN, worstT):0.0e0})");
        // every weight 0, the bump: bit for bit what the plain version (no blendshapes) gives
        var pP = new Vector3[n]; var pN = new Vector3[n]; var pT = new Vector4[n];
        fn.Compute(p.Moving, p.Off, p.Pos, p.Nrm, p.Tan, new[] { 0f }, oP, oN, oT);
        plain.Compute(p.Moving, p.Off, p.Pos, p.Nrm, p.Tan, default, pP, pN, pT);
        bool same = oP.AsSpan().SequenceEqual(pP) && oN.AsSpan().SequenceEqual(pN) && oT.AsSpan().SequenceEqual(pT);
        // and the old six-argument call still gives the same normals
        var lP = new Vector3[n]; var lN = new Vector3[n];
        plain.Compute(p.Moving, p.Off, p.Pos, p.Nrm, lP, lN);
        same &= lN.AsSpan().SequenceEqual(pN) && lP.AsSpan().SequenceEqual(pP);
        Check(same, "0.26.3: with every blendshape weight 0 the normals, tangents and positions are bit for bit those of the blendshape-free path");
    }

    /// <summary>The contact shading turn, on the shown frame: the shown normal reaches the target, the tangent turns along.</summary>
    static void ShadingContactTurn(ShadePlane p)
    {
        int n = p.Count;
        var fn = new FleshNormals(p.Pos, p.Nrm, p.Tri, p.Weld, p.Moving, ShShapes(p, true));
        var oP = new Vector3[n]; var oN = new Vector3[n]; var oT = new Vector4[n];
        fn.Compute(p.Moving, p.Off, p.Pos, p.Nrm, p.Tan, new[] { 1f }, oP, oN, oT);
        float hit = 0, ortho = 0, half = 0;
        for (int j = 5; j < p.N - 5; j += 7)
            for (int i = 5; i < p.N - 5; i += 7)
            {
                int v = j * p.N + i;
                var dir = Vector3.Normalize(p.TrueN[v] + new Vector3(0.4f, -0.3f, 0.2f));
                var n0 = Vector3.Normalize(oN[v] + p.Dn[v]);
                var nh = oN.ToArray(); var th = oT.ToArray();
                fn.TurnToward(v, dir, 0.5f, nh, th);
                half = MathF.Max(half, ShDeg(nh[v] + p.Dn[v], Vector3.Normalize(n0 * 0.5f + dir * 0.5f)));
                fn.TurnToward(v, dir, 1f, oN, oT);
                hit = MathF.Max(hit, ShDeg(oN[v] + p.Dn[v], dir));
                ortho = MathF.Max(ortho, MathF.Abs(90f - ShDeg(ShXyz(oT[v]) + p.Dt[v], oN[v] + p.Dn[v])));
            }
        // without blendshapes it is the 0.26.2 blend exactly: the unit normal mixed toward the target, normalised
        var plain = new FleshNormals(p.Pos, p.Nrm, p.Tri, p.Weld, p.Moving);
        plain.Compute(p.Moving, p.Off, p.Pos, p.Nrm, p.Tan, default, oP, oN, oT);
        float legacy = 0;
        for (int v = 0; v < n; v += 37)
        {
            var dir = Vector3.Normalize(new Vector3(0.2f, 0.5f, 0.8f));
            var want = oN[v] * 0.7f + dir * 0.3f; want /= want.Length();
            plain.TurnToward(v, dir, 0.3f, oN, oT);
            legacy = MathF.Max(legacy, Vector3.Distance(oN[v], want));
        }
        Check(hit < 0.01f && half < 0.01f && ortho < 0.01f && legacy < 1e-5f,
              $"0.26.3: the contact shading turns the shown normal (shape included) to its target and the tangent with it (target {hit:0.000}, half way {half:0.000}, " +
              $"right angle {ortho:0.0000} degrees); without shapes it is the 0.26.2 blend ({legacy:0.0e0})");
    }

    /// <summary>The layer merge carries tangents like normals: exact for one layer, the sum of changes for two, w kept.</summary>
    static void ShadingMergeTangents()
    {
        var restP = new Vector3[4]; var restN = new Vector3[4]; var restT = new Vector4[4];
        for (int v = 0; v < 4; v++) { restP[v] = new Vector3(v, 0, 0); restN[v] = Vector3.UnitZ; restT[v] = new Vector4(1, 0, 0, v == 1 ? -1f : 1f); }
        static Vector4 Turn(Vector4 t, float deg)
        {
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, deg * MathF.PI / 180f);
            return new Vector4(Vector3.Transform(new Vector3(t.X, t.Y, t.Z), q), t.W);
        }
        var m = new FleshOutputMerge();
        m.Begin(4);
        int[] a = { 0, 1 }, b = { 1, 2 };
        var aT = new[] { Turn(restT[0], 10), Turn(restT[1], 10) };
        var bT = new[] { Turn(restT[1], -4), Turn(restT[2], 20) };
        var aN = new[] { restN[0], restN[1] }; var bN = new[] { restN[1], restN[2] };
        var aP = new[] { restP[0], restP[1] }; var bP = new[] { restP[1], restP[2] };
        m.Add(a, aP, aN, aT, restP, restN, restT);
        m.Add(b, bP, bN, bT, restP, restN, restT);
        m.End(restN, restT);
        var sum = ShXyz(restT[1]) + (ShXyz(aT[1]) - ShXyz(restT[1])) + (ShXyz(bT[0]) - ShXyz(restT[1]));
        var want1 = new Vector4(Vector3.Normalize(sum), -1f);
        bool ok = m.Tangent(0) == aT[0] && m.Tangent(2) == bT[1] && Vector4.Distance(m.Tangent(1), want1) < 1e-6f && m.Shared == 1;
        Check(ok, "0.26.3: the layer merge carries tangents: one layer's exactly, two layers' changes summed and renormalised, handedness kept");
    }

    /// <summary>On the test body: the rig passes tangents through, at rest unchanged, after a jump turned and still at right angles.</summary>
    static void ShadingRig(FleshModel m)
    {
        var torso = m.Paint("Hips", "Spine", "Chest");
        var rig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
        int n = m.VertexCount, k = rig.MovingVertices.Length;
        var tan = new Vector4[n];
        for (int v = 0; v < n; v++)
        {
            var nn = Vector3.Normalize(m.Nrm[v]);
            var up = MathF.Abs(nn.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
            tan[v] = new Vector4(Vector3.Normalize(Vector3.Cross(up, nn)), v % 2 == 0 ? 1f : -1f);
        }
        // the blendshape that moves the most soft torso vertices, fully on, the flesh reset onto it
        int best = -1, bestCount = 0;
        for (int s = 0; s < m.ShapeNames.Length; s++)
        {
            int c = 0;
            for (int q = 0; q < m.ShapeVerts[s].Length; q++) if (torso[m.ShapeVerts[s][q]] > 0.5f && m.ShapeDeltas[s][q].Length() > 1e-3f) c++;
            if (c > bestCount) { bestCount = c; best = s; }
        }
        var w = new float[m.ShapeNames.Length];
        if (best >= 0) w[best] = 1f;
        var outPos = new Vector3[k]; var outNrm = new Vector3[k]; var outTan = new Vector4[k];
        rig.Reset(m.Pose(null, Matrix4x4.Identity), w);
        rig.FillOutput(m.Pos, m.Nrm, tan, outPos, outNrm, outTan);
        float restT = 0, restN = 0;
        for (int i = 0; i < k; i++)
        {
            int v = rig.MovingVertices[i];
            restT = MathF.Max(restT, Vector4.Distance(outTan[i], tan[v]));
            restN = MathF.Max(restN, Vector3.Distance(outNrm[i], m.Nrm[v]));
        }
        for (int f = 0; f < 8; f++) rig.Step(Dt, m.Pose(null, Matrix4x4.CreateTranslation(0, 0.08f * Ease(f / 7f), 0)), w);
        rig.FillOutput(m.Pos, m.Nrm, tan, outPos, outNrm, outTan);
        float ortho = 0, turned = 0; int wrongW = 0;
        for (int i = 0; i < k; i++)
        {
            int v = rig.MovingVertices[i];
            ortho = MathF.Max(ortho, MathF.Abs(90f - ShDeg(ShXyz(outTan[i]), outNrm[i])));
            turned = MathF.Max(turned, ShDeg(ShXyz(outTan[i]), ShXyz(tan[v])));
            if (outTan[i].W != tan[v].W) wrongW++;
        }
        Console.WriteLine($"  test body, {k} moving vertices, blendshape {(best >= 0 ? "\"" + Ascii(m.ShapeNames[best]) + "\"" : "none")} on: at rest worst normal {restN:0.0e0}, " +
                          $"tangent {restT:0.0e0}; after a jump tangents turned up to {turned:0.0} degrees, worst {ortho:0.0000} degrees off a right angle");
        Check(restT < 1e-4f && restN < 1e-3f && turned > 1f && ortho < 0.01f && wrongW == 0,
              "0.26.3: on the test body the rig passes tangents through: the mesh's own at rest (blendshape on), turned with the normals after a jump, at right angles, handedness kept");
    }
}
