using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Softbody.Core;
using Softbody.Core.Cloth;
using Softbody.Core.Fluid;

namespace Softbody;

/// <summary>
/// 0.29.2: the players' body shapes for the cloth, one registry per world (used from that world's thread only).
/// <para>For each player whose head is near a cloth: the skinned meshes under their avatar are copied (one per frame, so a
/// big avatar never costs one long frame), spheres are fitted just inside each mesh's skin on a worker
/// (<see cref="BodyShape"/>), and from then on every frame only the bones are read: each mesh's skin matrices, once per
/// frame however many cloths are near. The cloth's step poses the spheres itself. The avatar is checked again every
/// second; a different avatar, outfit or mesh starts a new fit.</para>
/// <para>A player is only shaped when every one of their skinned meshes could be read; until then (and if one can't be),
/// the cloth keeps using their bone colliders.</para>
/// </summary>
sealed class BodyShapes
{
    static readonly ConditionalWeakTable<World, BodyShapes> _worlds = new();
    public static BodyShapes For(World world) => _worlds.GetValue(world, _ => new BodyShapes());

    /// <summary>A mesh with more vertices than this is not copied (the player keeps their bone colliders).</summary>
    public const int MaxVertices = 600_000;
    /// <summary>A player counts as near when their head is within this (times their scale) of the cloth's box.</summary>
    const float NearPlayer = 2.5f;
    /// <summary>A mesh counts as near when its bones are within this (times the player's scale) of the cloth's box.</summary>
    const float NearPart = 0.6f;
    static readonly BodyFitSettings Fit = new();

    sealed class Part
    {
        public SkinnedMeshRenderer Renderer;
        public Slot[] Bones;
        public Matrix4x4[] BindPoses;
        public BodyMesh Input;
        public BodyShape Shape;
        public Matrix4x4[] Skin, Last;
        public bool Valid, HasLast;
        public float LastDt;
        public double PosedAt = double.NaN;
        public Vector3 Min, Max;
        public Dictionary<Slot[], bool[]> Skips;
    }

    sealed class Avatar
    {
        public User User;
        public Slot Top;
        public List<SkinnedMeshRenderer> Renderers = new();
        public List<MeshX> Meshes = new();
        public List<Part> Parts = new();
        public bool Ready;
        public Queue<SkinnedMeshRenderer> ToCopy;
        public List<Part> Copied;
        public Task<List<Part>> Fitting;
        public double CheckedAt = double.NegativeInfinity, SeenAt;
        public string Note = "";
        public int Spheres;
    }

    readonly Dictionary<User, Avatar> _avatars = new();
    double _copiedAt = double.NaN, _sweptAt;

    /// <summary>What <see cref="Collect"/> found for one cloth this frame.</summary>
    public struct Stats
    {
        public int Shaped, Building, Fallback;
        public string FallbackReason;
    }

    /// <summary>
    /// Fills <paramref name="f"/>'s bodies with the shaped players near lo..hi (world), and <paramref name="users"/> with
    /// every player whose shape stands in for their bone colliders this frame (also when none of their meshes is near).
    /// </summary>
    public void Collect(World world, Vector3 lo, Vector3 hi, Slot[] exclude, ClothFrame f, HashSet<User> users, out Stats stats)
    {
        stats = default;
        f.BodyCount = 0;
        users.Clear();
        double now;
        try { now = world.Time.WorldTime; } catch { return; }
        if (now - _sweptAt > 10.0 || now < _sweptAt) Sweep(now);
        foreach (var u in world.AllUsers)
        {
            if (u == null || u.IsRemoved) continue;
            UserRoot root;
            float scale;
            Vector3 head;
            try
            {
                root = u.Root;
                if (root == null || root.IsRemoved || root.Slot == null) continue;
                head = V(root.HeadPosition);
                scale = MathF.Max(1f, root.Slot.GlobalScale.x);
            }
            catch { continue; }
            var reach = new Vector3(NearPlayer * scale);
            if (!Overlaps(head, head, lo - reach, hi + reach)) continue;
            var av = Get(u, root, now);
            if (!av.Ready)
            {
                if (av.Fitting != null || av.ToCopy != null) stats.Building++;
                else { stats.Fallback++; stats.FallbackReason ??= av.Note; }
                continue;
            }
            users.Add(u);
            stats.Shaped++;
            var pad = new Vector3(NearPart * scale);
            foreach (var part in av.Parts)
            {
                if (!Pose(part, now) || !Overlaps(part.Min, part.Max, lo - pad, hi + pad)) continue;
                if (f.BodyCount == f.Bodies.Count) f.Bodies.Add(new ClothBody());
                var body = f.Bodies[f.BodyCount++];
                body.Set(part.Shape, part.Skin, part.HasLast ? part.Last : ReadOnlySpan<Matrix4x4>.Empty, part.LastDt, (byte)FluidSim.CategoryBody);
                body.SkipBone = SkipFor(part, exclude);
                body.User = u;
                body.Renderer = part.Renderer;
            }
        }
    }

    Avatar Get(User u, UserRoot root, double now)
    {
        if (!_avatars.TryGetValue(u, out var av)) { av = new Avatar { User = u }; _avatars[u] = av; }
        av.SeenAt = now;
        if (av.Fitting != null && av.Fitting.IsCompleted) Finish(av);
        if (now - av.CheckedAt >= 1.0 || now < av.CheckedAt) { av.CheckedAt = now; Check(av, root); }
        if (av.ToCopy != null && !(_copiedAt == now)) { _copiedAt = now; CopyNext(av); }
        return av;
    }

    /// <summary>Starts over when the avatar, its skinned meshes or their mesh data changed. The skinned meshes under the
    /// avatar (VRIKAvatar's slot), or under the user's root when there is no VRIK avatar.</summary>
    void Check(Avatar av, UserRoot root)
    {
        Slot top = null;
        var renderers = new List<SkinnedMeshRenderer>();
        var meshes = new List<MeshX>();
        try
        {
            var rig = root.GetRegisteredComponent<FrooxEngine.FinalIK.VRIKAvatar>();
            top = rig != null && !rig.IsRemoved && rig.Slot != null ? rig.Slot : root.Slot;
            foreach (var r in top.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (r == null || r.IsRemoved || !r.Enabled || r.Slot == null || !r.Slot.IsActive || r.Mesh.Target == null) continue;
                renderers.Add(r);
                // 0.30.0: the shared asset's data, never a softbody's or cloth's own copy (see PrivateMeshes.SharedOf)
                meshes.Add(r.Mesh.Target is StaticMesh sm && PrivateMeshes.SharedOf(sm.Asset) is { LoadState: AssetLoadState.FullyLoaded } a ? a.Data : null);
            }
        }
        catch (Exception ex) { top = null; renderers.Clear(); meshes.Clear(); av.Note = "its avatar could not be read (" + ex.Message + ")"; }
        if (ReferenceEquals(top, av.Top) && Same(renderers, av.Renderers) && Same(meshes, av.Meshes)) return;
        av.Top = top; av.Renderers = renderers; av.Meshes = meshes;
        av.Ready = false; av.Parts = new List<Part>(); av.Spheres = 0;
        av.Fitting = null; av.ToCopy = null; av.Copied = null;
        if (top == null) return;
        if (renderers.Count == 0) { av.Note = "no skinned meshes on the avatar"; return; }
        for (int i = 0; i < renderers.Count; i++)
            if (meshes[i] == null)
            {
                av.Note = renderers[i].Mesh.Target is StaticMesh ? "waiting for the avatar's meshes to load" : "one of the avatar's meshes is not a StaticMesh";
                return;
            }
        av.ToCopy = new Queue<SkinnedMeshRenderer>(renderers);
        av.Copied = new List<Part>();
        av.Note = "reading the avatar's meshes";
    }

    static bool Same<T>(List<T> a, List<T> b) where T : class
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    /// <summary>Copies one mesh (on the world thread); after the last, starts the fit on a worker.</summary>
    void CopyNext(Avatar av)
    {
        var r = av.ToCopy.Dequeue();
        Part part;
        string why;
        bool retry = false;
        try { part = Copy(r, out why, out retry); }
        catch (Exception ex) { part = null; why = "reading a mesh failed (" + ex.Message + ")"; }
        if (part == null && why != null)
        {
            av.ToCopy = null; av.Copied = null; av.Note = why;
            if (retry) av.Meshes = new List<MeshX>();   // the next check (in a second) starts over
            return;
        }
        if (part != null) av.Copied.Add(part);
        if (av.ToCopy.Count > 0) return;
        var parts = av.Copied;
        av.ToCopy = null; av.Copied = null;
        if (parts.Count == 0) { av.Note = "no usable skinned meshes on the avatar"; return; }
        av.Note = "fitting the body shape";
        av.Fitting = Task.Run(() =>
        {
            foreach (var p in parts)
            {
                p.Shape = BodyShape.Fit(p.Input, Fit);
                p.Input = null;
            }
            return parts;
        });
    }

    /// <summary>The mesh's data (bind space) and bones; null with a reason when it can't be used, null without one when it
    /// has nothing to fit (no triangles).</summary>
    static Part Copy(SkinnedMeshRenderer r, out string why, out bool retry)
    {
        why = null; retry = false;
        if (r.IsRemoved || r.Slot == null) { why = "the avatar changed"; retry = true; return null; }
        if (r.Mesh.Target is not StaticMesh provider || PrivateMeshes.SharedOf(provider.Asset) is not { LoadState: AssetLoadState.FullyLoaded } asset || asset.Data == null)
        { why = "waiting for the avatar's meshes to load"; retry = true; return null; }
        var mesh = asset.Data;
        int n = mesh.VertexCount;
        if (n <= 0) return null;
        if (n > MaxVertices) { why = $"a mesh has {n} vertices (cap {MaxVertices})"; return null; }
        if (Unsafe.SizeOf<float3>() != Unsafe.SizeOf<Vector3>()) { why = "unexpected Elements.Core vector layout"; return null; }
        var tris = new List<int>();
        foreach (var sub in mesh.Submeshes)
            if (sub is TriangleSubmesh ts) tris.AddRange(new ArraySegment<int>(ts.RawIndicies, 0, ts.IndicieCount));
        if (tris.Count == 0) return null;
        var pos = MemoryMarshal.Cast<float3, Vector3>(mesh.RawPositions.AsSpan(0, n)).ToArray();
        Vector3[] nrm = mesh.HasNormals && mesh.RawNormals != null && mesh.RawNormals.Length >= n
            ? MemoryMarshal.Cast<float3, Vector3>(mesh.RawNormals.AsSpan(0, n)).ToArray() : null;
        SkinData skin;
        Slot[] bones;
        Matrix4x4[] bind;
        if (mesh.BoneCount > 0 && mesh.HasBoneBindings)
        {
            var setup = SoftbodyInstance.ReadSkinSetup(r, mesh, n);
            skin = setup.Skin; bones = setup.Bones; bind = setup.BindPoses;
        }
        else { skin = SkinData.Rigid(n); bones = Array.Empty<Slot>(); bind = Array.Empty<Matrix4x4>(); }
        var now = new Matrix4x4[bones.Length + 1];
        if (!SoftbodyInstance.ComputeSkin(r.Slot, bones, bind, now, null)) { why = "the avatar's skeleton is not finite"; retry = true; return null; }
        return new Part
        {
            Renderer = r, Bones = bones, BindPoses = bind,
            Input = new BodyMesh { Positions = pos, Normals = nrm, Triangles = tris.ToArray(), Skin = skin, SkinNow = now },
            Skin = new Matrix4x4[bones.Length + 1], Last = new Matrix4x4[bones.Length + 1],
        };
    }

    void Finish(Avatar av)
    {
        var task = av.Fitting;
        av.Fitting = null;
        if (task.IsFaulted || task.IsCanceled)
        {
            var ex = task.Exception?.GetBaseException();
            av.Note = "fitting its body shape failed (" + (ex?.Message ?? "cancelled") + ")";
            SoftbodyRuntime.Warn("players' body shapes: " + av.Note + (ex != null ? "\n" + ex : ""));
            return;
        }
        var parts = task.Result;
        av.Parts = parts.Where(p => p.Shape != null && p.Shape.Count > 0).ToList();
        av.Spheres = av.Parts.Sum(p => p.Shape.Count);
        av.Ready = av.Parts.Count > 0;
        av.Note = av.Ready ? $"{av.Spheres} spheres" : "nothing on the avatar is thick enough for spheres";
        string name = "?";
        try { name = av.User.UserName; } catch { }
        double ms = parts.Sum(p => p.Shape?.FitMs ?? 0);
        int dropped = parts.Sum(p => p.Shape?.Dropped ?? 0);
        SoftbodyRuntime.Log($"cloth: body shape for '{name}': {av.Spheres} spheres from {av.Parts.Count} of {parts.Count} skinned meshes " +
                            $"({parts.Sum(p => p.Shape?.Samples ?? 0)} samples, fitted in {ms:0} ms{(dropped > 0 ? $", {dropped} smallest left out" : "")})");
    }

    /// <summary>The mesh's skin matrices for this frame (once per frame), and the box around its bones.</summary>
    static bool Pose(Part p, double now)
    {
        if (p.PosedAt == now) return p.Valid;
        var r = p.Renderer;
        bool had = p.Valid && !double.IsNaN(p.PosedAt);
        double gap = now - p.PosedAt;
        (p.Skin, p.Last) = (p.Last, p.Skin);
        p.PosedAt = now;
        p.Valid = false;
        p.HasLast = false;
        try
        {
            if (r == null || r.IsRemoved || !r.Enabled || r.Slot == null || !r.Slot.IsActive) return false;
            if (!SoftbodyInstance.ComputeSkin(r.Slot, p.Bones, p.BindPoses, p.Skin, null)) return false;
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            var at = V(r.Slot.GlobalPosition);
            lo = Vector3.Min(lo, at); hi = Vector3.Max(hi, at);
            foreach (var b in p.Bones)
            {
                if (b == null || b.IsRemoved) continue;
                at = V(b.GlobalPosition);
                lo = Vector3.Min(lo, at); hi = Vector3.Max(hi, at);
            }
            p.Min = lo; p.Max = hi;
        }
        catch { return false; }
        p.Valid = true;
        p.HasLast = had && gap > 1e-4 && gap < 0.25;
        p.LastDt = (float)gap;
        return true;
    }

    /// <summary>Per bone (and the renderer slot last): on or under one of the cloth's own slots (its anchors).</summary>
    static bool[] SkipFor(Part p, Slot[] exclude)
    {
        if (exclude == null || exclude.Length == 0) return null;
        p.Skips ??= new Dictionary<Slot[], bool[]>();
        if (p.Skips.TryGetValue(exclude, out var skip)) return skip;
        if (p.Skips.Count > 16) p.Skips.Clear();
        bool Under(Slot s)
        {
            for (; s != null; s = s.Parent)
                foreach (var x in exclude)
                    if (x != null && ReferenceEquals(s, x) && !x.IsRootSlot) return true;
            return false;
        }
        int nb = p.Bones.Length;
        for (int b = 0; b <= nb; b++)
        {
            var s = b < nb ? p.Bones[b] : p.Renderer?.Slot;
            if (s == null || !Under(s)) continue;
            skip ??= new bool[nb + 1];
            skip[b] = true;
        }
        p.Skips[exclude] = skip;
        return skip;
    }

    /// <summary>Forgets players who left or have not been near a cloth for two minutes.</summary>
    void Sweep(double now)
    {
        _sweptAt = now;
        List<User> gone = null;
        foreach (var (u, av) in _avatars)
            if (u.IsRemoved || now - av.SeenAt > 120.0 || now < av.SeenAt) (gone ??= new List<User>()).Add(u);
        if (gone != null) foreach (var u in gone) _avatars.Remove(u);
    }

    static bool Overlaps(Vector3 aLo, Vector3 aHi, Vector3 bLo, Vector3 bHi) =>
        aLo.X <= bHi.X && aHi.X >= bLo.X && aLo.Y <= bHi.Y && aHi.Y >= bLo.Y && aLo.Z <= bHi.Z && aHi.Z >= bLo.Z;

    static Vector3 V(float3 v) => new(v.x, v.y, v.z);
}
