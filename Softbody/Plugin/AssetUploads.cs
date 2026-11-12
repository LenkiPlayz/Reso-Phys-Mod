using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;

namespace Softbody;

/// <summary>
/// Serializes our uploads per mesh asset. FrooxEngine's Mesh keeps exactly one pending
/// "upload finished" callback (Mesh.onAssetUpdated) and RenderSystem.HandleCommand -> Mesh.HandleResult
/// invokes it without a null check, so two uploads in flight on one asset would throw on the
/// renderer-message thread. Rule: never start an upload on an asset until the previous one was acked,
/// and always pass a non-null callback.
/// The GPU path's own partial uploads (<see cref="TryUploadPartial"/>) follow the same rule.
/// </summary>
static class AssetUploads
{
    static readonly object _lock = new();
    static readonly Dictionary<FrooxEngine.Mesh, long> _inFlight = new(ReferenceEqualityComparer.Instance);
    static readonly List<PendingRestore> _restores = new();

    sealed class PendingRestore
    {
        public FrooxEngine.Mesh Asset;
        public MeshX Original;
        public MeshX Work;
        public bool Readable;
        public bool HighPriority;
        public long Since;
        public Action<bool> OnDone;   // e.g. re-bind renderers when the renderer made a new mesh instance
        public MeshUploadHint? Partial; // GPU path (0.9.0): put the original's vertex streams back in place instead of a full upload
        public System.Runtime.CompilerServices.StrongBox<bool> Recreated; // set when the renderer recreated the mesh: then a full one
    }

    public static bool IsBusy(FrooxEngine.Mesh asset, out double seconds)
    {
        lock (_lock)
        {
            PurgeDead();
            if (_inFlight.TryGetValue(asset, out var t)) { seconds = Elapsed(t); return true; }
            seconds = 0; return false;
        }
    }

    // An unloaded asset is gone from the renderer's asset table, so a late result for it is dropped
    // by RenderSystem (TryGet fails) and can never reach a callback: its entry can be forgotten.
    static void PurgeDead()
    {
        if (_inFlight.Count == 0) return;
        List<FrooxEngine.Mesh> dead = null;
        foreach (var a in _inFlight.Keys)
            if (a.LoadState == AssetLoadState.Unloaded || a.LoadState == AssetLoadState.Failed) (dead ??= new()).Add(a);
        if (dead != null) foreach (var a in dead) _inFlight.Remove(a);
    }

    public static bool HasPendingRestore(FrooxEngine.Mesh asset)
    {
        lock (_lock) return _restores.Any(r => ReferenceEquals(r.Asset, asset));
    }

    /// <summary>Starts an upload if the asset is idle. The callback runs on whatever thread the renderer result arrives on.</summary>
    public static bool TryUpload(FrooxEngine.Mesh asset, MeshX data, MeshUploadHint hint, BoundingBox? bounds, Action<bool> onDone)
    {
        lock (_lock)
        {
            PurgeDead();
            if (_inFlight.ContainsKey(asset)) return false;
            _inFlight[asset] = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        void Done(bool changed)
        {
            lock (_lock) _inFlight.Remove(asset);
            try { onDone?.Invoke(changed); } catch (Exception ex) { SoftbodyRuntime.Warn("upload callback: " + ex.Message); }
        }
        try
        {
            asset.SetFromMeshX(data, hint, bounds, Done);
        }
        catch
        {
            lock (_lock) _inFlight.Remove(asset);
            throw;
        }
        return true;
    }

    /// <summary>
    /// GPU path (0.9.0): starts a true partial upload (vertex streams only, no layout widening; see <see cref="GpuUpload"/>) if
    /// the asset is idle. Returns null when it started, "busy" when another upload is in flight, else why it is not possible
    /// (nothing was sent).
    /// </summary>
    public static string TryUploadPartial(FrooxEngine.Mesh asset, MeshX data, MeshUploadHint hint, BoundingBox bounds, Action<bool> onDone)
    {
        lock (_lock)
        {
            PurgeDead();
            if (_inFlight.ContainsKey(asset)) return Busy;
            _inFlight[asset] = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        void Done(bool changed)
        {
            // the callback first: it records whether the renderer recreated the mesh before a queued restore can see the asset idle
            try { onDone?.Invoke(changed); } catch (Exception ex) { SoftbodyRuntime.Warn("upload callback: " + ex.Message); }
            finally { lock (_lock) _inFlight.Remove(asset); }
        }
        string why;
        try { why = GpuUpload.TrySend(asset, data, hint, bounds, Done); }
        catch
        {
            lock (_lock) _inFlight.Remove(asset);
            throw;
        }
        if (why != null) lock (_lock) _inFlight.Remove(asset);
        return why;
    }

    public const string Busy = "busy";

    /// <summary>Puts the original mesh back on the asset (now, or as soon as our last upload is acked). <paramref name="work"/> is
    /// the MeshX the asset must still hold for the restore to apply (if the engine replaced it, there is nothing of ours left).
    /// With <paramref name="partial"/>, the original's vertex streams are re-uploaded in place (GPU path; falls back to a full upload).</summary>
    public static void QueueRestore(FrooxEngine.Mesh asset, MeshX original, MeshX work, bool readable, bool highPriority, Action<bool> onDone = null,
                                    MeshUploadHint? partial = null, System.Runtime.CompilerServices.StrongBox<bool> recreated = null)
    {
        if (asset == null || original == null) return;
        lock (_lock)
        {
            _restores.RemoveAll(r => ReferenceEquals(r.Asset, asset));
            _restores.Add(new PendingRestore { Asset = asset, Original = original, Work = work, Readable = readable, HighPriority = highPriority, OnDone = onDone,
                                               Partial = partial, Recreated = recreated, Since = System.Diagnostics.Stopwatch.GetTimestamp() });
        }
        ProcessRestores();
    }

    /// <summary>Issues queued restores whose asset is idle. Safe to call often, from the world or a background thread.</summary>
    public static void ProcessRestores()
    {
        List<PendingRestore> ready = null;
        lock (_lock)
        {
            PurgeDead();
            for (int i = _restores.Count - 1; i >= 0; i--)
            {
                var r = _restores[i];
                if (r.Asset.LoadState != AssetLoadState.FullyLoaded) { _restores.RemoveAt(i); continue; } // unloaded or reloading: nothing of ours left on it
                if (_inFlight.ContainsKey(r.Asset))
                {
                    // An ack that never comes means the renderer dropped it; after 30 s give up rather than risk a double upload.
                    if (Elapsed(r.Since) > 30) { _restores.RemoveAt(i); SoftbodyRuntime.Warn("gave up restoring a mesh: renderer never acknowledged the last upload"); }
                    continue;
                }
                (ready ??= new()).Add(r);
                _restores.RemoveAt(i);
            }
        }
        if (ready == null) return;
        foreach (var r in ready)
        {
            try
            {
                if (r.Asset.LoadState != AssetLoadState.FullyLoaded) continue;           // unloaded/reloading: nothing of ours is left on it
                if (!ReferenceEquals(r.Asset.Data, r.Work)) continue;                     // the engine already replaced the data
                r.Asset.HighPriorityIntegration = r.HighPriority;
                if (r.Partial.HasValue && !(r.Recreated?.Value ?? false))
                {
                    // GPU path: the asset still holds the original MeshX; only its vertex streams were overwritten in the buffer
                    string why = TryUploadPartial(r.Asset, r.Original, r.Partial.Value, r.Asset.Bounds, r.OnDone);
                    if (why == null) continue;
                    if (why == Busy) { lock (_lock) _restores.Add(r); continue; }
                    SoftbodyRuntime.Warn("in-place restore not possible (" + why + "), restoring with a full upload");
                }
                var hint = default(MeshUploadHint);
                MeshUploadHintHelper.SetFromMesh(ref hint, r.Original);
                hint[MeshUploadHint.Flag.Readable] = r.Readable;
                TryUpload(r.Asset, r.Original, hint, null, r.OnDone);
            }
            catch (Exception ex) { SoftbodyRuntime.Warn("restoring original mesh failed: " + ex.Message); }
        }
    }

    public static int PendingRestoreCount { get { lock (_lock) return _restores.Count; } }

    static double Elapsed(long ts) => (System.Diagnostics.Stopwatch.GetTimestamp() - ts) / (double)System.Diagnostics.Stopwatch.Frequency;
}
