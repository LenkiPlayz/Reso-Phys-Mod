using System.Diagnostics;
using System.Globalization;
using System.Text;
using FrooxEngine;
using ResoniteModLoader;
using Softbody.Core;

namespace Softbody;

/// <summary>
/// Selective softbody test mod. Any slot tagged "softbody" with a plain MeshRenderer + StaticMesh gets a
/// lattice cage simulated in a private Bepu simulation; the mesh's vertices follow the cage, weighted by
/// the red channel of the vertex colour (0 = firm, 1 = squishy). Local only: nothing is added to the world.
///
/// Optional settings, read from DynamicValueVariable components on the tagged slot itself
/// (float or int for numbers, bool for switches; re-read every second, changes rebuild the cage):
///   softbody/cells       int   4     lattice cells per axis (1..8); points = (cells+1)^3
///   softbody/stiffness   float 6     Hz, edge springs in squishy regions
///   softbody/volume      float 0     Hz, volume springs per tet (0 = 2 x stiffness)
///   softbody/damping     float 0.15  damping ratio of squishy springs (lower = wobblier)
///   softbody/follow      float 2.5   Hz, pull of squishy points back to their place on the slot
///   softbody/firm        float 30    Hz, springs/servos for nearly-firm points
///   softbody/pin         float 0.02  points with softness at or below this are pinned to the slot
///   softbody/mass        float 2     kg, whole cage
///   softbody/gravity     float 1     gravity multiplier
///   softbody/drag        float 0.2   linear damping, 1/s
///   softbody/friction    float 0.8
///   softbody/radius      float 0.2   collision sphere radius per cage point, fraction of a cell
///   softbody/iterations  int   8     solver iterations
///   softbody/collide     bool  true  collide with the ground ray / nearby Box and Sphere colliders
///   softbody/enabled     bool  true
/// Environment: SOFTBODY_PORT (default 23801), SOFTBODY_BUDGET_MS (default 3).
/// </summary>
public class SoftbodyMod : ResoniteMod
{
    public override string Name => "Softbody";
    public override string Author => "Claude (for Lenki)";
    public override string Version => "0.1.0";

    public const string Tag = "softbody";
    const int MaxTotalPoints = 4000;
    const double StatsInterval = 5.0;

    static readonly object Gate = new();
    static World _hooked, _requested;
    static bool _hookQueued;
    static Action _postUpdate;
    static readonly Dictionary<Slot, SoftbodyInstance> _instances = new();
    static readonly Dictionary<Slot, (string key, string reason)> _blocked = new();
    static readonly Dictionary<Slot, string> _waiting = new();
    static float _scanTimer;
    static double _statsTimer;
    static double _budgetMs = 3.0;
    static string _lastWorldName;
    static double _lastScanMs;
    static int _lastScanSlots;
    static bool _warnedSlowScan;
    static readonly Dictionary<Slot, string> _snapshots = new();

    public override void OnEngineInit()
    {
        if (double.TryParse(Environment.GetEnvironmentVariable("SOFTBODY_BUDGET_MS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0)
            _budgetMs = b;
        try
        {
            Engine.Current.WorldManager.WorldRemoved += OnWorldRemoved;
            Engine.Current.OnShutdown += () => { lock (Gate) TeardownAll("engine shutdown", restore: false); StatsServer.Stop(); };
        }
        catch (Exception ex) { Error("Softbody could not subscribe to engine events: " + ex); }
        StatsServer.Start(StatusJson);
        Task.Run(FocusLoop);
        Msg($"Softbody 0.1.0 ready: tag slots '{Tag}' (MeshRenderer + StaticMesh). Budget {_budgetMs} ms per softbody per frame.");
    }

    internal static void LogInfo(string s) => Msg(s);
    internal static void LogWarn(string s) => Warn(s);

    // ------------------------------------------------------------------ focus tracking (background thread)

    static async Task FocusLoop()
    {
        while (true)
        {
            await Task.Delay(500).ConfigureAwait(false);
            try
            {
                var focused = Engine.Current?.WorldManager?.FocusedWorld;
                World toUnhook = null, toHook = null;
                lock (Gate)
                {
                    if (focused != _requested) { toUnhook = _hooked; _requested = focused; toHook = focused; }
                    else if (focused != null && _hooked != focused && !_hookQueued) toHook = focused; // retry a lost hook
                    if (toHook != null) _hookQueued = true;
                }
                if (toUnhook != null)
                {
                    if (!toUnhook.IsDestroyed) toUnhook.RunSynchronously(() => Unhook(toUnhook, "world lost focus"));
                    else lock (Gate) Unhook(toUnhook, "world closed");
                }
                if (toHook != null)
                {
                    if (!toHook.IsDestroyed) toHook.RunSynchronously(() => { lock (Gate) _hookQueued = false; Hook(toHook); });
                    else lock (Gate) _hookQueued = false;
                }
                // Restores can be waiting for a renderer ack after their world was unhooked.
                if (AssetUploads.PendingRestoreCount > 0) lock (Gate) AssetUploads.ProcessRestores();
            }
            catch (Exception ex) { Warn("Softbody focus loop: " + ex.Message); }
        }
    }

    static void Hook(World world)
    {
        lock (Gate)
        {
            if (_hooked == world || world.IsDestroyed || _requested != world) return;
            if (_hooked != null) Unhook(_hooked, "switching worlds");
            _hooked = world;
            _postUpdate = () => OnPostUpdate(world);
            world.Physics.PostUpdate += _postUpdate;
            _scanTimer = 0;
            _lastWorldName = world.Name;
            Msg($"Softbody watching world '{world.Name}'");
        }
    }

    static void Unhook(World world, string why)
    {
        lock (Gate)
        {
            if (world == null || _hooked != world) return;
            try { world.Physics.PostUpdate -= _postUpdate; } catch { }
            TeardownAll(why, restore: true);
            _hooked = null; _postUpdate = null;
            Msg($"Softbody stopped watching '{world.Name}' ({why})");
        }
    }

    static void OnWorldRemoved(World world)
    {
        try
        {
            lock (Gate)
            {
                if (_requested == world) _requested = null;
                if (_hooked == world) Unhook(world, "world closed");
            }
        }
        catch (Exception ex) { Warn("Softbody world removed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ per frame (world thread, PhysicsManager.PostUpdate)

    static void OnPostUpdate(World world)
    {
        // Must never throw: PhysicsManager.Update does not catch around PostUpdate.
        try
        {
            lock (Gate)
            {
                if (_hooked != world) return;
                float dt = world.Time.Delta;
                if (!float.IsFinite(dt) || dt <= 0) return;
                dt = MathF.Min(dt, 0.1f);

                _scanTimer -= dt;
                if (_scanTimer <= 0) { _scanTimer = 1f; Scan(world); }

                List<(Slot, string, bool)> drop = null;
                foreach (var (slot, inst) in _instances)
                {
                    try
                    {
                        var r = inst.Frame(world, dt);
                        if (r == SoftbodyInstance.FrameResult.Gone) (drop ??= new()).Add((slot, "slot or renderer removed", false));
                        else if (r == SoftbodyInstance.FrameResult.Rebuild) (drop ??= new()).Add((slot, "mesh, asset or scale changed: rebuilding", false));
                        else if (inst.TotalFrames > inst.Warmup && inst.EmaTotalMs > _budgetMs) (drop ??= new()).Add((slot, $"over budget: {inst.EmaTotalMs:0.00} ms/frame average > {_budgetMs} ms", true));
                        else if (inst.TotalFrames > inst.Warmup && inst.LastFrameMs > 100) (drop ??= new()).Add((slot, $"single frame took {inst.LastFrameMs:0} ms", true));
                    }
                    catch (Exception ex)
                    {
                        (drop ??= new()).Add((slot, "error: " + ex.GetType().Name + ": " + ex.Message, true));
                        Warn($"Softbody '{inst.Name}' threw: {ex}");
                    }
                }
                if (drop != null)
                    foreach (var (slot, why, block) in drop)
                    {
                        if (!_instances.Remove(slot, out var inst)) continue;
                        inst.Teardown(restore: true);
                        _snapshots.Remove(slot);
                        if (block) { _blocked[slot] = (inst.SettingsKey, why); Warn($"Softbody '{inst.Name}' disabled: {why}. Change a softbody/* setting or re-tag the slot to retry."); }
                        else Msg($"Softbody '{inst.Name}' stopped: {why}");
                    }

                AssetUploads.ProcessRestores();

                _statsTimer += dt;
                if (_statsTimer >= StatsInterval) { LogStats(_statsTimer); _statsTimer = 0; }
            }
        }
        catch (Exception ex)
        {
            try { Warn("Softbody frame: " + ex); } catch { }
        }
    }

    // ------------------------------------------------------------------ discovery

    static readonly List<Slot> _found = new();
    static readonly Stack<Slot> _stack = new();

    static void Scan(World world)
    {
        var sw = Stopwatch.StartNew();
        _found.Clear(); _stack.Clear();
        int visited = 0;
        _stack.Push(world.RootSlot);
        while (_stack.Count > 0)
        {
            var s = _stack.Pop();
            visited++;
            var children = s.Children;
            for (int i = 0; i < children.Count; i++)
            {
                var c = children[i];
                if (c == null) continue;
                var tag = c.Tag;
                if (tag != null && tag.Trim().Equals(Tag, StringComparison.OrdinalIgnoreCase)) _found.Add(c);
                _stack.Push(c);
            }
        }
        _lastScanMs = sw.Elapsed.TotalMilliseconds;
        _lastScanSlots = visited;
        if (_lastScanMs > 5 && !_warnedSlowScan) { _warnedSlowScan = true; Warn($"Softbody: scanning {visited} slots for the tag took {_lastScanMs:0.0} ms"); }

        var foundSet = new HashSet<Slot>(_found);
        // Tag removed or slot gone: tear down and forget blocks.
        foreach (var slot in _instances.Keys.ToList())
            if (!foundSet.Contains(slot) || slot.IsRemoved)
            {
                var inst = _instances[slot];
                _instances.Remove(slot);
                _snapshots.Remove(slot);
                inst.Teardown(restore: true);
                Msg($"Softbody '{inst.Name}' stopped: {(slot.IsRemoved ? "slot removed" : "tag removed")}");
            }
        foreach (var slot in _blocked.Keys.ToList()) if (!foundSet.Contains(slot) || slot.IsRemoved) _blocked.Remove(slot);
        foreach (var slot in _waiting.Keys.ToList()) if (!foundSet.Contains(slot) || slot.IsRemoved) _waiting.Remove(slot);

        int totalPoints = _instances.Values.Sum(i => i.PointCount);
        foreach (var slot in _found)
        {
            if (slot.IsRemoved) continue;
            var (settings, enabled) = ReadSettings(slot);
            string key = settings + (enabled ? "" : " disabled");

            if (_instances.TryGetValue(slot, out var existing))
            {
                if (!enabled || existing.SettingsKey != key)
                {
                    _instances.Remove(slot);
                    _snapshots.Remove(slot);
                    existing.Teardown(restore: true);
                    totalPoints -= existing.PointCount;
                    Msg($"Softbody '{existing.Name}' settings changed{(enabled ? ": rebuilding" : ": disabled")}");
                }
                else continue;
            }
            if (!enabled) continue;
            if (_blocked.TryGetValue(slot, out var blk))
            {
                if (blk.key == key) continue;
                _blocked.Remove(slot); // settings changed: retry
            }

            int pts = (settings.Cells + 1) * (settings.Cells + 1) * (settings.Cells + 1);
            if (totalPoints + pts > MaxTotalPoints)
            {
                _blocked[slot] = (key, $"body cap: {totalPoints} + {pts} points > {MaxTotalPoints}");
                Warn($"Softbody '{slot.Name}' not started: body cap ({MaxTotalPoints} points total)");
                continue;
            }

            SoftbodyInstance.CreateResult res;
            SoftbodyInstance inst;
            string reason;
            try { res = SoftbodyInstance.TryCreate(slot, settings, key, out inst, out reason); }
            catch (Exception ex) { res = SoftbodyInstance.CreateResult.Fail; inst = null; reason = "create threw " + ex.GetType().Name + ": " + ex.Message; }

            switch (res)
            {
                case SoftbodyInstance.CreateResult.Created:
                    _instances[slot] = inst;
                    _waiting.Remove(slot);
                    totalPoints += inst.PointCount;
                    Msg($"Softbody '{slot.Name}' started: {inst.Describe()}; settings {settings}");
                    break;
                case SoftbodyInstance.CreateResult.Wait:
                    if (!_waiting.TryGetValue(slot, out var prev) || prev != reason) Msg($"Softbody '{slot.Name}' waiting: {reason}");
                    _waiting[slot] = reason;
                    break;
                default:
                    _blocked[slot] = (key, reason);
                    _waiting.Remove(slot);
                    Warn($"Softbody '{slot.Name}' cannot start: {reason}");
                    break;
            }
        }
    }

    static (SoftbodySettings, bool enabled) ReadSettings(Slot slot)
    {
        var s = new SoftbodySettings();
        bool enabled = true;
        foreach (var c in slot.Components)
        {
            string name; double? num = null; bool? flag = null;
            switch (c)
            {
                case DynamicValueVariable<float> f: name = f.VariableName.Value; num = f.Value.Value; break;
                case DynamicValueVariable<double> d: name = d.VariableName.Value; num = d.Value.Value; break;
                case DynamicValueVariable<int> i: name = i.VariableName.Value; num = i.Value.Value; break;
                case DynamicValueVariable<bool> b: name = b.VariableName.Value; flag = b.Value.Value; break;
                default: continue;
            }
            if (string.IsNullOrEmpty(name)) continue;
            int slash = name.IndexOf('/');
            if (slash < 0 || !name.AsSpan(0, slash).Trim().Equals(Tag, StringComparison.OrdinalIgnoreCase)) continue;
            var key = name.Substring(slash + 1).Trim().ToLowerInvariant();
            if (num is double v && double.IsFinite(v))
            {
                float x = (float)v;
                switch (key)
                {
                    case "cells": s.Cells = (int)Math.Round(v); break;
                    case "stiffness": s.Stiffness = x; break;
                    case "volume": s.VolumeStiffness = x; break;
                    case "damping": s.Damping = x; break;
                    case "follow": s.Follow = x; break;
                    case "firm": s.RigidFrequency = x; break;
                    case "pin": s.RigidThreshold = x; break;
                    case "mass": s.Mass = x; break;
                    case "gravity": s.Gravity = x; break;
                    case "drag": s.LinearDamping = x; break;
                    case "friction": s.Friction = x; break;
                    case "radius": s.PointRadiusFactor = x; break;
                    case "iterations": s.SolverIterations = (int)Math.Round(v); break;
                }
            }
            else if (flag is bool on)
            {
                if (key == "collide") s.Collide = on;
                else if (key == "enabled") enabled = on;
            }
        }
        s.Clamp();
        return (s, enabled);
    }

    static void TeardownAll(string why, bool restore)
    {
        foreach (var (slot, inst) in _instances)
        {
            try { inst.Teardown(restore); } catch { }
            Msg($"Softbody '{inst.Name}' stopped: {why}");
        }
        _instances.Clear();
        _snapshots.Clear();
        _waiting.Clear();
        _blocked.Clear();
    }

    // ------------------------------------------------------------------ stats

    static void LogStats(double seconds)
    {
        foreach (var (slot, inst) in _instances)
        {
            var w = inst.Window;
            long acks = inst.TakeAcks();
            int f = Math.Max(1, w.Frames);
            string line =
                $"Softbody '{inst.Name}': {inst.PointCount} points, {inst.VertexCount} verts ({inst.MovingVerts} moving) | per frame: " +
                $"sim {w.SimMs / f:0.000} ms ({(double)w.Substeps / f:0.0} substeps), world queries {w.QueryMs / f:0.000} ms, " +
                $"deform+normals {w.DeformMs / f:0.000} ms, upload {w.UploadMs / f:0.000} ms, max frame {w.MaxTotalMs:0.00} ms, avg {inst.EmaTotalMs:0.000} ms | " +
                $"{w.Frames} frames / {seconds:0.0} s: {w.Uploads} uploads, {acks} acked (last ack {inst.LastAckLatencyMs:0.0} ms), " +
                $"{w.Busy} skipped (renderer busy), {w.Idle} idle | path: {inst.UploadPath} | " +
                $"ground {(inst.HasGround ? "yes" : "no")}, proxies {inst.ProxyCount}, resets {inst.Resets}{(inst.Resets > 0 ? $" ({inst.LastResetReason})" : "")}, " +
                $"cage displacement {inst.LastDisplacement * 1000:0.0} mm";
            Msg(line);
            _snapshots[slot] = Snapshot(inst, w, acks, seconds);
            w.Clear();
        }
    }

    static string Snapshot(SoftbodyInstance inst, SoftbodyInstance.Stats w, long acks, double seconds)
    {
        int f = Math.Max(1, w.Frames);
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("{");
        sb.Append("\"name\":").Append(StatsServer.Json(inst.Name));
        sb.Append(",\"refId\":").Append(StatsServer.Json(inst.Slot.ReferenceID.ToString()));
        sb.Append(",\"points\":").Append(inst.PointCount);
        sb.Append(",\"verts\":").Append(inst.VertexCount);
        sb.Append(",\"movingVerts\":").Append(inst.MovingVerts);
        sb.Append(",\"simMs\":").Append((w.SimMs / f).ToString("0.000", ic));
        sb.Append(",\"queryMs\":").Append((w.QueryMs / f).ToString("0.000", ic));
        sb.Append(",\"deformMs\":").Append((w.DeformMs / f).ToString("0.000", ic));
        sb.Append(",\"uploadMs\":").Append((w.UploadMs / f).ToString("0.000", ic));
        sb.Append(",\"avgFrameMs\":").Append(inst.EmaTotalMs.ToString("0.000", ic));
        sb.Append(",\"maxFrameMs\":").Append(w.MaxTotalMs.ToString("0.00", ic));
        sb.Append(",\"substepsPerFrame\":").Append(((double)w.Substeps / f).ToString("0.00", ic));
        sb.Append(",\"windowSeconds\":").Append(seconds.ToString("0.0", ic));
        sb.Append(",\"frames\":").Append(w.Frames);
        sb.Append(",\"uploads\":").Append(w.Uploads);
        sb.Append(",\"acks\":").Append(acks);
        sb.Append(",\"lastAckMs\":").Append(inst.LastAckLatencyMs.ToString("0.0", ic));
        sb.Append(",\"busySkips\":").Append(w.Busy);
        sb.Append(",\"idleSkips\":").Append(w.Idle);
        sb.Append(",\"uploadPath\":").Append(StatsServer.Json(inst.UploadPath));
        sb.Append(",\"ground\":").Append(inst.HasGround ? "true" : "false");
        sb.Append(",\"proxies\":").Append(inst.ProxyCount);
        sb.Append(",\"resets\":").Append(inst.Resets);
        sb.Append(",\"displacementMm\":").Append((inst.LastDisplacement * 1000).ToString("0.0", ic));
        sb.Append(",\"settings\":").Append(StatsServer.Json(inst.SettingsKey));
        sb.Append('}');
        return sb.ToString();
    }

    static string StatusJson()
    {
        lock (Gate)
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("{\"ok\":true,\"mod\":\"Softbody 0.1.0\"");
            sb.Append(",\"world\":").Append(StatsServer.Json(_hooked != null ? _lastWorldName : null));
            sb.Append(",\"budgetMs\":").Append(_budgetMs.ToString(ic));
            sb.Append(",\"lastScanMs\":").Append(_lastScanMs.ToString("0.00", ic));
            sb.Append(",\"lastScanSlots\":").Append(_lastScanSlots);
            sb.Append(",\"pendingRestores\":").Append(AssetUploads.PendingRestoreCount);
            sb.Append(",\"softbodies\":[");
            bool first = true;
            foreach (var (slot, inst) in _instances)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append(_snapshots.TryGetValue(slot, out var snap) ? snap
                    : "{\"name\":" + StatsServer.Json(inst.Name) + ",\"points\":" + inst.PointCount + ",\"verts\":" + inst.VertexCount + ",\"note\":\"first stats after 5 s\"}");
            }
            sb.Append("],\"waiting\":[");
            first = true;
            foreach (var (slot, why) in _waiting) { if (!first) sb.Append(','); first = false; sb.Append("{\"name\":").Append(StatsServer.Json(slot.Name)).Append(",\"reason\":").Append(StatsServer.Json(why)).Append('}'); }
            sb.Append("],\"disabled\":[");
            first = true;
            foreach (var (slot, b) in _blocked) { if (!first) sb.Append(','); first = false; sb.Append("{\"name\":").Append(StatsServer.Json(slot.Name)).Append(",\"reason\":").Append(StatsServer.Json(b.reason)).Append('}'); }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
