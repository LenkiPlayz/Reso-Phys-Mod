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
            try { onDone?.Invoke(changed); } catch (Exception ex) { SoftbodyMod.LogWarn("upload callback: " + ex.Message); }
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

    /// <summary>Puts the original mesh back on the asset (now, or as soon as our last upload is acked).</summary>
    public static void QueueRestore(FrooxEngine.Mesh asset, MeshX original, MeshX work, bool readable, bool highPriority)
    {
        if (asset == null || original == null) return;
        lock (_lock)
        {
            _restores.RemoveAll(r => ReferenceEquals(r.Asset, asset));
            _restores.Add(new PendingRestore { Asset = asset, Original = original, Work = work, Readable = readable, HighPriority = highPriority, Since = System.Diagnostics.Stopwatch.GetTimestamp() });
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
                    if (Elapsed(r.Since) > 30) { _restores.RemoveAt(i); SoftbodyMod.LogWarn("gave up restoring a mesh: renderer never acknowledged the last upload"); }
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
                var hint = default(MeshUploadHint);
                MeshUploadHintHelper.SetFromMesh(ref hint, r.Original);
                hint[MeshUploadHint.Flag.Readable] = r.Readable;
                TryUpload(r.Asset, r.Original, hint, null, null);
            }
            catch (Exception ex) { SoftbodyMod.LogWarn("restoring original mesh failed: " + ex.Message); }
        }
    }

    public static int PendingRestoreCount { get { lock (_lock) return _restores.Count; } }

    static double Elapsed(long ts) => (System.Diagnostics.Stopwatch.GetTimestamp() - ts) / (double)System.Diagnostics.Stopwatch.Frequency;
}
