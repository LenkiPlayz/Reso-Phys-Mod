using System.Numerics;
using System.Text;
using Softbody.Core;

namespace Softbody.Tests;

/// <summary>
/// A skinned test mesh converted from an FBX by tools/fbx_to_sbm.py: positions in metres in the FBX's world space at
/// bind time (so that is the mesh's bind space here), the skinned bones' global bind matrices, and blendshape deltas.
/// </summary>
sealed class FleshModel
{
    public Vector3[] Pos, Nrm;
    public int[] Cp, Tri, Bones4;
    public float[] Weights4;
    public int ControlPoints;
    public string[] BoneNames;
    public int[] Parent;
    public Matrix4x4[] Bind;   // bone global at bind (bone local -> mesh space)
    public Vector3[] Heads, Tails;
    public string[] ShapeNames;
    public int[][] ShapeVerts;
    public Vector3[][] ShapeDeltas;
    public SkinData Skin;
    public BlendshapeDeltas Blend;
    public int VertexCount => Pos.Length;
    public int BoneCount => BoneNames.Length;

    public static string DefaultPath()
    {
        var dir = typeof(FleshModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "TestDataDir")?.Value;
        return dir == null ? null : Path.Combine(dir, "kitavali_body.sbm");
    }

    public static FleshModel Load(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "SBM1") throw new InvalidDataException("not an .sbm file");
        int nv = r.ReadInt32(), nt = r.ReadInt32(), nb = r.ReadInt32(), ns = r.ReadInt32();
        var m = new FleshModel { ControlPoints = r.ReadInt32() };
        Vector3 V3() => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        string Name() => Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()));
        m.Pos = new Vector3[nv]; for (int i = 0; i < nv; i++) m.Pos[i] = V3();
        m.Nrm = new Vector3[nv]; for (int i = 0; i < nv; i++) m.Nrm[i] = V3();
        m.Cp = new int[nv]; for (int i = 0; i < nv; i++) m.Cp[i] = r.ReadInt32();
        m.Tri = new int[nt * 3]; for (int i = 0; i < nt * 3; i++) m.Tri[i] = r.ReadInt32();
        m.Bones4 = new int[nv * 4]; for (int i = 0; i < nv * 4; i++) m.Bones4[i] = r.ReadInt32();
        m.Weights4 = new float[nv * 4]; for (int i = 0; i < nv * 4; i++) m.Weights4[i] = r.ReadSingle();
        m.BoneNames = new string[nb]; m.Parent = new int[nb]; m.Bind = new Matrix4x4[nb]; m.Heads = new Vector3[nb]; m.Tails = new Vector3[nb];
        for (int b = 0; b < nb; b++)
        {
            m.BoneNames[b] = Name(); m.Parent[b] = r.ReadInt32();
            var f = new float[16]; for (int k = 0; k < 16; k++) f[k] = r.ReadSingle();
            m.Bind[b] = new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
            m.Heads[b] = m.Bind[b].Translation;
            m.Tails[b] = V3();
        }
        m.ShapeNames = new string[ns]; m.ShapeVerts = new int[ns][]; m.ShapeDeltas = new Vector3[ns][];
        for (int s = 0; s < ns; s++)
        {
            m.ShapeNames[s] = Name();
            int c = r.ReadInt32();
            m.ShapeVerts[s] = new int[c]; m.ShapeDeltas[s] = new Vector3[c];
            for (int k = 0; k < c; k++) { m.ShapeVerts[s][k] = r.ReadInt32(); m.ShapeDeltas[s][k] = V3(); }
        }
        // weights of 0 point at bone 0 in the file: SkinData drops them (it maps them to the slot bone with weight 0)
        m.Skin = SkinData.FromBindings(nb, m.Bones4, m.Weights4);
        m.Blend = new BlendshapeDeltas(ns, nv, s =>
        {
            var full = new Vector3[nv];
            for (int k = 0; k < m.ShapeVerts[s].Length; k++) full[m.ShapeVerts[s][k]] = m.ShapeDeltas[s][k];
            return (full, null, null);
        });
        return m;
    }

    public int Bone(string name) => Array.IndexOf(BoneNames, name);

    /// <summary>Softness per render vertex: the summed weight of these bones, eased from 0.25 to 0.6.</summary>
    public float[] Paint(params string[] bones)
    {
        var set = bones.Select(Bone).Where(b => b >= 0).ToHashSet();
        var s = new float[VertexCount];
        for (int v = 0; v < VertexCount; v++)
        {
            float w = 0;
            for (int q = 0; q < 4; q++) if (set.Contains(Skin.VertexBones[v * 4 + q])) w += Skin.VertexWeights[v * 4 + q];
            float t = Math.Clamp((w - 0.25f) / 0.35f, 0f, 1f);
            s[v] = t * t * (3 - 2 * t);
        }
        return s;
    }

    /// <summary>
    /// Skin matrices (bind space to world) for local bone rotations (in each bone's own frame, about its head; null =
    /// rest) and a world transform of the whole model. The last entry is the slot bone (= world).
    /// </summary>
    public Matrix4x4[] Pose(Dictionary<int, Quaternion> local, Matrix4x4 world)
    {
        int nb = BoneCount;
        var posed = new Matrix4x4[nb]; var done = new bool[nb];
        Matrix4x4 Global(int b)
        {
            if (done[b]) return posed[b];
            int p = Parent[b];
            Matrix4x4 l;
            if (p < 0) l = Bind[b];
            else { Matrix4x4.Invert(Bind[p], out var ip); l = Bind[b] * ip; }
            var rot = local != null && local.TryGetValue(b, out var q) ? Matrix4x4.CreateFromQuaternion(q) : Matrix4x4.Identity;
            posed[b] = rot * l * (p < 0 ? world : Global(p));
            done[b] = true;
            return posed[b];
        }
        var skin = new Matrix4x4[nb + 1];
        for (int b = 0; b < nb; b++)
        {
            Matrix4x4.Invert(Bind[b], out var ib);
            skin[b] = ib * Global(b);
        }
        skin[nb] = world;
        return skin;
    }
}
