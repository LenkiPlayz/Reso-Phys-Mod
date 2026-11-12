using System.Diagnostics;
using System.Reflection;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;

namespace Softbody;

/// <summary>
/// 0.14.0, per-renderer instancing for SkinnedSoftbody. Every StaticMesh with the same URL shares one mesh asset, so two
/// avatars wearing the same model used to share one simulation's output (the second softbody waited, and showed the
/// first's jiggle). Now each running SkinnedSoftbody gives its own avatar's StaticMesh a private copy of the mesh: a
/// dynamic mesh asset of this client, made from the same data and uploaded once, which the provider hands to its
/// renderers instead of the shared one. The softbody's uploads go to the copy, so the shared asset (and every other
/// avatar using it) is never touched. Nothing synced changes: the provider's asset is local runtime state.
///
/// 0.26.1 (found in game, the first time this ran): a provider hands out only an asset that counts as loaded, and a dynamic
/// mesh never does, so right after the swap it handed out nothing (the body vanished) and the softbody rebuilt, every 2 s.
/// <see cref="StandIn"/> now makes the copy stand in for the shared asset the way the engine checks for: loaded, with the
/// shared asset's URL (or the provider's next asset update takes it for a URL change and drops it), and relocated to the
/// shared asset (so a provider that lets go of it, when it is destroyed or loads another URL or variant, releases its
/// request on the shared asset, as the engine expects; a dynamic asset would throw there instead).
///
/// Uses private members of StaticAssetProvider and Asset, by reflection: the provider's <c>_asset</c> (what it hands out)
/// and <c>_lock</c> (taken while it is swapped), and the asset's LoadState, AssetURL, AssetType and VariantManager setters
/// and <c>Asset&lt;V&gt;.relocatedAsset</c>. If any is missing (an engine update), <see cref="Unavailable"/> says so and
/// SkinnedSoftbody goes back to one softbody per mesh asset.
/// </summary>
static class PrivateMeshes
{
    const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly Type _providerType = typeof(StaticAssetProvider<FrooxEngine.Mesh, Elements.Assets.MeshMetadata, FrooxEngine.MeshVariantDescriptor>);
    static readonly FieldInfo _asset = _providerType.GetField("_asset", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo _lock = _providerType.GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic);
    // 0.26.1: what makes the copy stand in for the shared asset
    static readonly MethodInfo _setLoadState = Setter("LoadState", typeof(AssetLoadState));
    static readonly MethodInfo _setUrl = Setter("AssetURL", typeof(Uri));
    static readonly MethodInfo _setType = Setter("AssetType", typeof(FrooxEngine.AssetType));
    static readonly MethodInfo _setVariantManager = Setter("VariantManager", typeof(AssetVariantManager));
    static readonly Type _assetOfMesh = AssetOf(typeof(FrooxEngine.Mesh));
    static readonly FieldInfo _relocated = _assetOfMesh?.GetField("relocatedAsset", BindingFlags.Instance | BindingFlags.NonPublic);

    static MethodInfo Setter(string name, Type type)
    {
        var p = typeof(Asset).GetProperty(name, Inst);
        return p != null && p.PropertyType == type ? p.GetSetMethod(true) : null;
    }

    /// <summary>The Asset&lt;V&gt; a mesh derives from (whose relocatedAsset a request on it follows).</summary>
    static Type AssetOf(Type t)
    {
        for (; t != null; t = t.BaseType)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Asset<>)) return t;
        return null;
    }

    /// <summary>Null when every engine member this needs exists with the expected type; otherwise which one is missing.</summary>
    public static readonly string Unavailable =
        _asset == null || _asset.FieldType != typeof(FrooxEngine.Mesh) ? "StaticAssetProvider._asset (a Mesh) not found" :
        _lock == null || _lock.FieldType != typeof(object) ? "StaticAssetProvider._lock not found" :
        _setLoadState == null ? "Asset.LoadState's setter not found" :
        _setUrl == null ? "Asset.AssetURL's setter not found" :
        _setType == null ? "Asset.AssetType's setter not found" :
        _setVariantManager == null ? "Asset.VariantManager's setter not found" :
        _relocated == null || _relocated.FieldType != _assetOfMesh ? "Asset<V>.relocatedAsset not found" : null;

    public static int Created, Unloaded;

    /// <summary>A new dynamic mesh asset holding <paramref name="data"/>, uploaded to the renderer in full;
    /// <paramref name="done"/> runs (on the renderer-message thread) once the renderer has it.</summary>
    public static FrooxEngine.Mesh Create(World world, MeshX data, BoundingBox bounds, object owner, AssetIntegrated done)
    {
        var m = new FrooxEngine.Mesh();
        m.InitializeDynamic(world.Engine.AssetManager);
        try { m.SetOwner(owner); } catch { }
        m.HighPriorityIntegration = true;
        var hint = default(MeshUploadHint);
        MeshUploadHintHelper.SetFromMesh(ref hint, data);
        m.SetFromMeshX(data, hint, bounds, done);
        Interlocked.Increment(ref Created);
        return m;
    }

    /// <summary>
    /// 0.26.1: right before the provider gets the copy: it stands in for <paramref name="shared"/>. It counts as fully
    /// loaded (set without the load notifications, which are for the engine's own variants), carries the shared asset's URL
    /// and variant manager (the provider compares the URL; the mesh's metadata comes from the variant manager), and is a
    /// static asset relocated to the shared one, so a request removed from it is removed from the shared asset.
    /// Undone by <see cref="Retire"/>.
    /// </summary>
    public static void StandIn(FrooxEngine.Mesh copy, FrooxEngine.Mesh shared)
    {
        if (Unavailable != null) throw new InvalidOperationException(Unavailable);
        try
        {
            _setUrl.Invoke(copy, new object[] { shared.AssetURL });
            _setVariantManager.Invoke(copy, new object[] { shared.VariantManager });
            _relocated.SetValue(copy, shared);
            _setType.Invoke(copy, new object[] { FrooxEngine.AssetType.Static });
            _setLoadState.Invoke(copy, new object[] { AssetLoadState.FullyLoaded });
        }
        catch (Exception ex)
        {
            Retire(copy);
            throw new InvalidOperationException((ex.InnerException ?? ex).Message, ex.InnerException ?? ex);
        }
    }

    /// <summary>
    /// 0.26.1: once no provider holds the copy any more: it is the dynamic asset it was made as again (no URL, no variant
    /// manager, not relocated), so unloading it touches nothing of the shared asset's. Safe to call more than once.
    /// </summary>
    public static void Retire(FrooxEngine.Mesh copy)
    {
        if (copy == null || Unavailable != null) return;
        try
        {
            _relocated.SetValue(copy, null);
            _setVariantManager.Invoke(copy, new object[] { null });
            _setType.Invoke(copy, new object[] { FrooxEngine.AssetType.Dynamic });
            _setUrl.Invoke(copy, new object[] { null });
        }
        catch (Exception ex) { SoftbodyRuntime.Warn("retiring a softbody's mesh copy: " + (ex.InnerException ?? ex).Message); }
    }

    /// <summary>Makes <paramref name="provider"/> hand out <paramref name="to"/> instead of <paramref name="from"/>; false (and
    /// nothing changed) when it does not hold <paramref name="from"/> any more. The caller re-notifies its renderers.</summary>
    public static bool Swap(StaticMesh provider, FrooxEngine.Mesh from, FrooxEngine.Mesh to)
    {
        if (Unavailable != null || provider == null) return false;
        var gate = _lock.GetValue(provider) ?? provider;
        lock (gate)
        {
            if (!ReferenceEquals(_asset.GetValue(provider), from)) return false;
            _asset.SetValue(provider, to);
        }
        return true;
    }

    /// <summary>The asset the provider holds right now (whatever it hands out).</summary>
    public static FrooxEngine.Mesh Held(StaticMesh provider) => Unavailable == null && provider != null ? _asset.GetValue(provider) as FrooxEngine.Mesh : null;

    static readonly List<(FrooxEngine.Mesh mesh, long at)> _unload = new();

    /// <summary>Unloads a copy a couple of seconds from now, after its renderers have moved back to the shared asset.</summary>
    public static void UnloadLater(FrooxEngine.Mesh mesh)
    {
        if (mesh == null) return;
        lock (_unload) _unload.Add((mesh, Stopwatch.GetTimestamp()));
    }

    public static int PendingUnloads { get { lock (_unload) return _unload.Count; } }

    /// <summary>Unloads the copies that waited long enough (called twice a second).</summary>
    public static void ProcessUnloads()
    {
        List<FrooxEngine.Mesh> due = null;
        lock (_unload)
        {
            for (int i = _unload.Count - 1; i >= 0; i--)
                if ((Stopwatch.GetTimestamp() - _unload[i].at) / (double)Stopwatch.Frequency >= 2.0) { (due ??= new()).Add(_unload[i].mesh); _unload.RemoveAt(i); }
        }
        if (due == null) return;
        foreach (var m in due)
        {
            try
            {
                Retire(m);
                if (m.LoadState != AssetLoadState.Unloaded) m.Unload();
                Interlocked.Increment(ref Unloaded);
            }
            catch (Exception ex) { SoftbodyRuntime.Warn("unloading a softbody's mesh copy: " + ex.Message); }
        }
    }
}
