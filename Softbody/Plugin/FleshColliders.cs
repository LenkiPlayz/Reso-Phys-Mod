using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Softbody.Core.Flesh;

namespace Softbody;

/// <summary>
/// Triangle trees of mesh colliders' meshes, built once per mesh data on the thread pool and shared by every
/// SkinnedSoftbody (a world's floor mesh is usually one big MeshCollider). Weakly keyed: a mesh the engine drops is
/// forgotten. Null from <see cref="Get"/> while a tree is still being built.
/// </summary>
static class MeshBvhCache
{
    sealed class Entry { public volatile TriangleBvh Bvh; public volatile string Error; }
    static readonly ConditionalWeakTable<MeshX, Entry> _table = new();
    public static int Built, Failed;

    public static TriangleBvh Get(MeshX mesh)
    {
        Entry e;
        lock (_table)
        {
            if (_table.TryGetValue(mesh, out e)) return e.Bvh;
            e = new Entry();
            _table.Add(mesh, e);
        }
        Task.Run(() =>
        {
            try
            {
                int n = mesh.VertexCount;
                var pos = MemoryMarshal.Cast<float3, Vector3>(mesh.RawPositions.AsSpan(0, n));
                var tris = new List<int>();
                foreach (var sub in mesh.Submeshes)
                    if (sub is TriangleSubmesh ts) tris.AddRange(new ArraySegment<int>(ts.RawIndicies, 0, ts.IndicieCount));
                e.Bvh = TriangleBvh.Build(pos, CollectionsMarshal.AsSpan(tris));
                Interlocked.Increment(ref Built);
            }
            catch (Exception ex)
            {
                e.Error = ex.Message;
                Interlocked.Increment(ref Failed);
                SoftbodyRuntime.LogOnce("bvh-fail:" + ex.Message, "mesh collider tree could not be built: " + ex.Message);
            }
        });
        return null;
    }
}

/// <summary>
/// The users' colliders the engine already keeps for dynamic bones (DynamicBoneChainManager, one per world): spheres on
/// each user's head, along the body from hips to neck, down the legs and arms (tapered), on the hands, and any
/// DynamicBoneSphereColliders an avatar registers. Recomputed by the engine every frame from tracking; read here through
/// compiled accessors (the array is private). If an engine update renames them, users simply stop colliding (logged once).
/// </summary>
static class UserColliderReader
{
    public const byte HeadFlag = 1, LeftHandFlag = 2, RightHandFlag = 4, BodyFlag = 8, GenericFlag = 16;
    static readonly Func<DynamicBoneChainManager, Array> _array;
    static readonly Func<Array, int, Vector3> _pos;
    static readonly Func<Array, int, float> _radius;
    static readonly Func<Array, int, byte> _mask;
    static readonly Func<Array, int, User> _user;
    public static readonly string Unavailable;

    static UserColliderReader()
    {
        try
        {
            var mgr = typeof(DynamicBoneChainManager);
            var arrField = mgr.GetField("colliders", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException("DynamicBoneChainManager.colliders");
            var dataType = arrField.FieldType.GetElementType() ?? throw new MissingFieldException("ColliderData");
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var fPos = dataType.GetField("position", flags) ?? throw new MissingFieldException("ColliderData.position");
            var fRad = dataType.GetField("radius", flags) ?? throw new MissingFieldException("ColliderData.radius");
            var fMask = dataType.GetField("mask", flags) ?? throw new MissingFieldException("ColliderData.mask");
            var fOwner = dataType.GetField("owner", flags) ?? throw new MissingFieldException("ColliderData.owner");
            var pUser = fOwner.FieldType.GetProperty("User", flags) ?? throw new MissingMemberException("UserColliders.User");

            var m = Expression.Parameter(mgr, "m");
            _array = Expression.Lambda<Func<DynamicBoneChainManager, Array>>(Expression.Convert(Expression.Field(m, arrField), typeof(Array)), m).Compile();
            var a = Expression.Parameter(typeof(Array), "a"); var i = Expression.Parameter(typeof(int), "i");
            var elem = Expression.ArrayIndex(Expression.Convert(a, arrField.FieldType), i);
            var p = Expression.Field(elem, fPos);
            _pos = Expression.Lambda<Func<Array, int, Vector3>>(
                Expression.New(typeof(Vector3).GetConstructor(new[] { typeof(float), typeof(float), typeof(float) }),
                               Expression.Field(p, "x"), Expression.Field(p, "y"), Expression.Field(p, "z")), a, i).Compile();
            _radius = Expression.Lambda<Func<Array, int, float>>(Expression.Field(elem, fRad), a, i).Compile();
            _mask = Expression.Lambda<Func<Array, int, byte>>(Expression.Field(elem, fMask), a, i).Compile();
            var owner = Expression.Field(elem, fOwner);
            _user = Expression.Lambda<Func<Array, int, User>>(
                Expression.Condition(Expression.Equal(owner, Expression.Constant(null, fOwner.FieldType)), Expression.Constant(null, typeof(User)),
                                     Expression.Property(owner, pUser)), a, i).Compile();
        }
        catch (Exception ex) { Unavailable = ex.Message; }
    }

    /// <summary>Adds the users' spheres inside the box, for users without bone colliders (<paramref name="withRig"/>: their
    /// bone colliders are used instead). The own user without bone colliders only adds its hands: its body, head and limb
    /// spheres sit inside its own flesh.</summary>
    public static int Collect(World world, User own, HashSet<User> withRig, Vector3 min, Vector3 max, List<FleshCollider> into, FleshSettings s)
    {
        if (Unavailable != null) { SoftbodyRuntime.LogOnce("usercolliders", "users' colliders are not available (" + Unavailable + "): SkinnedSoftbody only collides with world colliders"); return 0; }
        var mgr = world?.DynamicBones;
        if (mgr == null) return 0;
        var arr = _array(mgr);
        if (arr == null) return 0;
        int count = Math.Min(mgr.CollidersCount, arr.Length), added = 0;
        for (int k = 0; k < count; k++)
        {
            float r = _radius(arr, k);
            if (!(r > 0f) || !float.IsFinite(r)) continue;
            var c = _pos(arr, k);
            if (c.X + r < min.X || c.X - r > max.X || c.Y + r < min.Y || c.Y - r > max.Y || c.Z + r < min.Z || c.Z - r > max.Z) continue;
            var user = _user(arr, k);
            if (user != null && withRig != null && withRig.Contains(user)) continue;
            byte mask = _mask(arr, k);
            var part = (mask & HeadFlag) != 0 ? PlayerPart.Head : (mask & (LeftHandFlag | RightHandFlag)) != 0 ? PlayerPart.Hands : PlayerPart.Body;
            if (own != null && ReferenceEquals(user, own))
            {
                // the own user's spheres: only the hands (its head and body spheres sit inside its own flesh)
                if (part != PlayerPart.Hands || !s.CollideSelf || !s.SelfHands) continue;
            }
            else if (!s.CollideOthers || !FleshInstance.Allowed(part, s.OthersHead, s.OthersBody, s.OthersHands)) continue;
            into.Add(FleshCollider.Sphere(c, r, source: 1));
            added++;
        }
        return added;
    }

    /// <summary>0.15.0, for the liquid: every user's spheres inside the box (users in <paramref name="withRig"/> use their
    /// bone colliders instead), each tagged by <paramref name="sourceOf"/> from the part of the player it is on (0 = left out).</summary>
    public static int CollectParts(World world, HashSet<User> withRig, Vector3 min, Vector3 max, List<FleshCollider> into, Func<PlayerPart, byte> sourceOf)
    {
        if (Unavailable != null) return 0;
        var mgr = world?.DynamicBones;
        if (mgr == null) return 0;
        var arr = _array(mgr);
        if (arr == null) return 0;
        int count = Math.Min(mgr.CollidersCount, arr.Length), added = 0;
        for (int k = 0; k < count; k++)
        {
            float r = _radius(arr, k);
            if (!(r > 0f) || !float.IsFinite(r)) continue;
            var c = _pos(arr, k);
            if (c.X + r < min.X || c.X - r > max.X || c.Y + r < min.Y || c.Y - r > max.Y || c.Z + r < min.Z || c.Z - r > max.Z) continue;
            var user = _user(arr, k);
            if (user != null && withRig != null && withRig.Contains(user)) continue;
            byte mask = _mask(arr, k);
            var part = (mask & HeadFlag) != 0 ? PlayerPart.Head : (mask & (LeftHandFlag | RightHandFlag)) != 0 ? PlayerPart.Hands : PlayerPart.Body;
            byte src = sourceOf(part);
            if (src == 0) continue;
            into.Add(FleshCollider.Sphere(c, r, source: src));
            added++;
        }
        return added;
    }
}

/// <summary>
/// The bone colliders VRIKAvatar generates for an avatar (capsules on the major bones, a head sphere, hand and foot boxes),
/// found exactly through its list of the drives that switch them on and off (the engine enables them only while users are
/// close). Its separate single "simplified" collider is not in that list. Read by reflection (a protected field); if an
/// engine update renames it, avatars simply have no bone colliders here (logged once).
/// </summary>
static class AvatarRigColliders
{
    static readonly FieldInfo _list = typeof(FrooxEngine.FinalIK.VRIKAvatar).GetField("_rigCollidersEnabledStates", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
    public static readonly string Unavailable = _list == null ? "VRIKAvatar._rigCollidersEnabledStates not found" :
        _list.FieldType != typeof(SyncList<FieldDrive<bool>>) ? "VRIKAvatar._rigCollidersEnabledStates is " + _list.FieldType : null;

    public static void Collect(FrooxEngine.FinalIK.VRIKAvatar avatar, List<Collider> into)
    {
        if (Unavailable != null) { SoftbodyRuntime.LogOnce("rigcolliders", "avatars' bone colliders are not available (" + Unavailable + ")"); return; }
        if (avatar == null || avatar.IsRemoved) return;
        if (_list.GetValue(avatar) is not SyncList<FieldDrive<bool>> drives) return;
        for (int i = 0; i < drives.Count; i++)
        {
            var target = drives[i]?.Target;
            if (target is SyncElement el && el.Worker is Collider c && !c.IsRemoved) into.Add(c);
        }
    }
}

/// <summary>0.12.0: the parts of a player the collision toggles choose between.</summary>
enum PlayerPart : byte { Head, Body, Hands }
