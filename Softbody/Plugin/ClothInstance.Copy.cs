using System.Diagnostics;
using System.Numerics;
using Elements.Assets;
using FrooxEngine;
using Softbody.Core.Cloth;

namespace Softbody;

/// <summary>
/// 0.33.0: a world cloth left behind as a copy (Cloth.DuplicateOnMend). When its Cloth is mended or turned off, the running
/// simulation is not torn down but handed to a <see cref="ClothDebris"/> on a local slot (this client only, never synced or
/// saved), with the loose pieces only: every piece that no pinned or anchored point holds. The held pieces are hidden,
/// since the original comes back whole in their place.
///
/// The copy's slot has its own StaticMesh of the same mesh. Once that has loaded the shared asset (a frame or two: it is
/// already loaded), the copy's StaticMesh takes the private copy of the mesh and the original's StaticMesh gets the shared
/// one back, in the same frame, so there is no gap and no doubled cloth. Until then the original renderer still shows the
/// torn cloth, standing still, and its Cloth waits (<see cref="CopyHandingOver"/>): a cloth built then would read the torn
/// copy as its mesh.
///
/// From then on the copy is on its own. It reads nothing of the Cloth (no tears, blasts, paint or settings), steps in its
/// slot's space (pinned points stay where they are), and stops once its pieces have settled, or after
/// <see cref="CopyMaxSimSeconds"/> of simulation; the ClothDebris takes it away when CopyLifetime is up.
/// </summary>
sealed partial class ClothInstance
{
    /// <summary>Seconds a copy may wait for its StaticMesh to load the mesh before it is dropped.</summary>
    public const double CopyTakeOverSeconds = 5.0;
    /// <summary>Seconds of simulation a copy gets at most (then it stays as it is).</summary>
    public const float CopyMaxSimSeconds = 20f;
    /// <summary>m/s: a copy whose loose points all move slower than this for <see cref="CopySettleSeconds"/> has settled.</summary>
    public const float CopySettleSpeed = 0.05f;
    public const float CopySettleSeconds = 1f;

    Slot _copySlot;
    StaticMesh _copyProvider;
    MeshRenderer _copyRenderer;
    string _copyName;
    volatile bool _copyShown;
    long _copySince;
    bool[] _keepSim;
    int[] _looseParticles;
    bool _forceFull, _copyPlayers, _copyUploadDue, _settled;
    float _simSeconds, _stillSeconds;
    public int CopyPieces { get; private set; }
    public int CopyPoints => _looseParticles?.Length ?? 0;

    /// <summary>Left behind as a copy (no longer its Cloth's).</summary>
    public bool IsCopy => _copySlot != null;
    /// <summary>A copy whose StaticMesh has not taken over yet: the original's StaticMesh still holds the private copy.</summary>
    public bool CopyHandingOver => _copySlot != null && !_copyShown && _private != null;
    public bool CopySettled => _settled;

    /// <summary>The slot whose space the output is in: the renderer's, or a copy's own.</summary>
    Slot FrameSlot => _copySlot ?? Anchor;

    /// <summary>The StaticMesh handing out the private copy now.</summary>
    StaticMesh Shower => _copyShown ? _copyProvider : Provider;

    /// <summary>False, with why, when this cloth cannot leave a copy (avatar cloth, or not yet showing its own copy).</summary>
    internal bool CanLeaveCopy(out string why)
    {
        why = null;
        if (_sk != null) { why = "an avatar cloth leaves no copy (world cloth only)"; return false; }
        if (_copySlot != null) { why = "it is a copy already"; return false; }
        if (_private == null || !_swapped || _mesh == null || _solver == null) { why = "it was not showing yet"; return false; }
        if (Renderer.IsRemoved || Anchor.IsRemoved || Provider.IsRemoved) { why = "its renderer, mesh or slot is gone"; return false; }
        if (Asset.LoadState != AssetLoadState.FullyLoaded) { why = "its mesh is not loaded"; return false; }
        if (!ReferenceEquals(PrivateMeshes.Held(Provider), _private)) { why = "the renderer is not showing the cloth's copy of the mesh"; return false; }
        return true;
    }

    /// <summary>
    /// Which simulated triangles are in a loose piece: a piece (triangles joined by shared points) with no pinned or anchored
    /// point (see <see cref="ClothPieces"/>). False when nothing is loose, so there is nothing to leave behind. World thread,
    /// between steps.
    /// </summary>
    internal bool FindLoose(out bool[] keepSim, out int[] looseParticles, out int pieces)
    {
        keepSim = null; looseParticles = null; pieces = 0;
        if (_sk != null || _mesh == null || _solver == null) return false;
        WaitStep();
        return ClothPieces.FindLoose(_mesh, i => _solver.Kind(i), out keepSim, out looseParticles, out pieces);
    }

    /// <summary>
    /// Becomes a copy on <paramref name="slot"/>, with its own StaticMesh and (still disabled) MeshRenderer: the loose pieces
    /// stay, and it no longer reads its Cloth. The original renderer shows it, standing still, until the copy's StaticMesh
    /// takes over (<see cref="CopyFrame"/>); the held pieces are hidden then.
    /// </summary>
    internal void BecomeCopy(Slot slot, StaticMesh provider, MeshRenderer renderer, bool[] keepSim, int[] looseParticles, int pieces)
    {
        WaitStep();
        string name = "?";
        try { name = Anchor.Name; } catch { }
        bool players = true;
        try { players = Owner.CollidePlayers.Value; } catch { }
        // the pieces are left to fall: no more tears, and no MaxDistance tying them to the original's place
        var s = _solver.Settings.Clone();
        s.Tearable = false;
        s.MaxDistance = 0f;
        _solver.SetSettings(s);
        _solver.TearCandidates.Clear();
        _pendingKicks.Clear();
        _copyName = name;
        _copyPlayers = players;
        _keepSim = keepSim;
        _looseParticles = looseParticles;
        CopyPieces = pieces;
        _settled = false;
        _simSeconds = 0f; _stillSeconds = 0f;
        _copySince = Stopwatch.GetTimestamp();
        _copyProvider = provider;
        _copyRenderer = renderer;
        _copySlot = slot;
    }

    internal enum CopyResult { Ok, Gone, Failed }

    /// <summary>
    /// A copy's frame (world thread, Gate held): takes over from the original once its StaticMesh is ready, shows the last
    /// step, and steps again until the pieces settle. Gone or Failed (with why): take it away.
    /// </summary>
    internal CopyResult CopyFrame(World world, float dt, out string why)
    {
        why = null;
        if (_private == null) { why = "its mesh copy was let go"; return CopyResult.Gone; }
        if (_copySlot.IsRemoved || _copyProvider.IsRemoved || _copyRenderer.IsRemoved) { WaitStep(); why = "its slot, StaticMesh or renderer was removed"; return CopyResult.Gone; }
        if (!_copyShown)
        {
            if (!TakeOver())
            {
                if (Elapsed(_copySince) > CopyTakeOverSeconds) { why = $"its StaticMesh did not load the cloth's mesh within {CopyTakeOverSeconds:0} s"; return CopyResult.Failed; }
                return CopyResult.Ok;
            }
        }
        else if (!ReferenceEquals(PrivateMeshes.Held(_copyProvider), _private)) { WaitStep(); why = "its StaticMesh let go of the cloth's copy of the mesh"; return CopyResult.Gone; }

        bool stepped = _step != null;
        WaitStep();
        if (stepped) { Settle(dt); _copyUploadDue = true; }
        if (_copyUploadDue)
        {
            long before = Uploads;
            Upload();
            if (Uploads != before) _copyUploadDue = false;
        }
        if (_settled) return CopyResult.Ok;
        if (world.Focus != FrooxEngine.World.WorldFocus.Focused) { _resumeNext = true; return CopyResult.Ok; }
        _gainIn = 1f;
        if (Culled(world)) return CopyResult.Ok;
        var m = SkinMatrices.ToNumerics(_copySlot.LocalToGlobal);
        if (!Finite(m) || !Matrix4x4.Invert(m, out var inv)) { _resumeNext = true; return CopyResult.Ok; }
        var f = _frame;
        f.KickCount = 0;
        f.Dt = dt;
        f.RendererToWorld = m;
        f.Resumed = _resumeNext;
        _resumeNext = false;
        // the held pieces are hidden: their anchors no longer move them (held where they are)
        for (int a = 0; a < f.AnchorValid.Length; a++) f.AnchorValid[a] = false;
        f.Time = WorldTime(world);
        f.FindTears = false;
        GatherColliders(world, dt);
        GatherWinds(world);
        StartStep(f, inv, 1f);
        _simSeconds += dt;
        return CopyResult.Ok;
    }

    /// <summary>
    /// The copy's StaticMesh has loaded the shared mesh: it takes the private copy, and the original's StaticMesh gets the
    /// shared mesh back, in the same frame. Then only the loose pieces show (the held ones are the original's, whole again).
    /// </summary>
    bool TakeOver()
    {
        if (Asset.LoadState != AssetLoadState.FullyLoaded) return false;
        if (!ReferenceEquals(PrivateMeshes.Held(_copyProvider), Asset) || !_copyProvider.IsAssetAvailable) return false;
        if (!PrivateMeshes.Swap(_copyProvider, Asset, _private)) return false;
        _copyShown = true;
        try { if (PrivateMeshes.Swap(Provider, _private, Asset)) _assetManager?.QueueAssetCreated(Provider); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{Name}': giving the renderer its shared mesh back: {ex.Message}"); }
        try { _assetManager?.QueueAssetCreated(_copyProvider); } catch { }
        try { _copyRenderer.Enabled = true; } catch { }
        HideHeld();
        _forceFull = true;
        _copyUploadDue = true;
        return true;
    }

    /// <summary>The held pieces' triangles in the work mesh become empty (all three corners one vertex), so only the loose
    /// pieces show. Brings the work mesh up to the cloth's topology first.</summary>
    void HideHeld()
    {
        if (_keepSim == null) return;
        if (_mesh.Version != _workVersion) ApplyTopology();
        var rtSim = _mesh.RenderTriangleSim;
        int r = 0;
        foreach (var sub in _work.Submeshes)
        {
            if (sub is not TriangleSubmesh ts) continue;
            int cnt = ts.IndicieCount;
            ClothPieces.HideHeld(rtSim, _keepSim, ts.RawIndicies.AsSpan(0, cnt), r);
            r += cnt / 3;
        }
    }

    /// <summary>After each step: settled once every loose point has moved slower than <see cref="CopySettleSpeed"/> for
    /// <see cref="CopySettleSeconds"/>, or after <see cref="CopyMaxSimSeconds"/> of simulation.</summary>
    void Settle(float dt)
    {
        float max2 = 0f;
        var v = _solver.V;
        foreach (int p in _looseParticles)
        {
            float s2 = v[p].LengthSquared();
            if (!(s2 <= max2)) max2 = float.IsFinite(s2) ? s2 : float.MaxValue;
        }
        if (max2 < CopySettleSpeed * CopySettleSpeed) _stillSeconds += dt;
        else _stillSeconds = 0f;
        if (_stillSeconds >= CopySettleSeconds || _simSeconds >= CopyMaxSimSeconds) _settled = true;
    }

    /// <summary>A copy's status.</summary>
    internal string CopyState =>
        !_copyShown ? "waiting for its StaticMesh to load the mesh" :
        _settled ? "settled (no longer simulated)" :
        _culled ? $"paused: further than CullDistance ({_solver.Settings.CullDistance:0.#} m) from you" :
        $"falling ({_simSeconds:0.#} s simulated)";
}
