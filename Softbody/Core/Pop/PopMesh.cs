using System.Numerics;

namespace Softbody.Core.Pop;

/// <summary>
/// 0.34.0: one mesh as it looked when the pop went off (the pose of that moment: bones and blendshapes), in the world.
/// A channel the mesh lacks is null (or missing from <see cref="Uvs"/>).
/// </summary>
public sealed class PopMesh
{
    public Vector3[] Positions = Array.Empty<Vector3>();
    public Vector3[] Normals;
    public Vector4[] Tangents;
    public Vector4[] Colors;
    /// <summary>The UV channels in order, as xyzw (the plugin keeps each channel's own size).</summary>
    public readonly List<Vector4[]> Uvs = new();
    /// <summary>Triangle lists (three indices each), one per material, in the renderer's material order.</summary>
    public readonly List<int[]> Submeshes = new();
}

/// <summary>0.34.0: how a pop cuts (from Pop's synced numbers, so every client cuts alike).</summary>
public struct PopCutSettings
{
    /// <summary>0..1: from a few big pieces (0) up to confetti (1), in the Cloth Blast's steps.</summary>
    public float Shred;
    /// <summary>At most about this many pieces: confetti gets no smaller than the surface split this many ways.</summary>
    public int MaxPieces;
    public int Seed;
}

/// <summary>
/// 0.34.0: the cut. One mesh of pieces: each piece's front vertices together, and the same vertices again in the second
/// half as its back faces (so a flake shows from both sides). Submeshes go in the source order (mesh 0's materials, then
/// mesh 1's...), each with its front triangles and then its back ones. Per piece: its origin (the middle of the points
/// it collides with), its size, its vertices and its collision points.
/// </summary>
public sealed class PopPieces
{
    public Vector3[] Positions = Array.Empty<Vector3>();
    public Vector3[] Normals = Array.Empty<Vector3>();
    public Vector4[] Tangents = Array.Empty<Vector4>();
    /// <summary>Whether any source mesh had tangents (Tangents is filled either way).</summary>
    public bool HasTangents;
    /// <summary>Null when no source mesh had colours.</summary>
    public Vector4[] Colors;
    public readonly List<Vector4[]> Uvs = new();
    public readonly List<int[]> Submeshes = new();
    /// <summary>Front vertices: 0..FrontVertices-1; the back faces' vertex for v is v + FrontVertices.</summary>
    public int FrontVertices;

    public int Count;
    /// <summary>Each piece's origin, the middle of its collision points.</summary>
    public Vector3[] Origin = Array.Empty<Vector3>();
    /// <summary>The farthest of its vertices from its origin.</summary>
    public float[] Radius = Array.Empty<float>();
    public int[] VertexStart = Array.Empty<int>(), VertexCount = Array.Empty<int>();
    /// <summary>Per piece: where its collision points start, how many it collides with while fast (they add up to zero
    /// about its origin), and how many while slow (the same first ones and more: all its vertices, up to
    /// <see cref="PopCut.MostSlowPoints"/>).</summary>
    public int[] PointStart = Array.Empty<int>(), PointCount = Array.Empty<int>(), SlowPointCount = Array.Empty<int>();
    /// <summary>The collision points, as offsets from their piece's origin.</summary>
    public Vector3[] Points = Array.Empty<Vector3>();

    /// <summary>What the cut came to: the pieces' size, the length triangles were cut down to (0: none were cut), the
    /// surface's area, and the triangles before and after (front only).</summary>
    public float Spacing, CutTo, Area;
    public int SourceTriangles, Triangles, Tries;
    /// <summary>0.35.0: the cut's weld, where its grid starts and how big a cell is (0: none): two vertices in one cell
    /// touch. The soft pieces weld their vertices the same way, so a piece holds together just as the cut joined it.</summary>
    public Vector3 WeldFrom;
    public float WeldSize;
}
