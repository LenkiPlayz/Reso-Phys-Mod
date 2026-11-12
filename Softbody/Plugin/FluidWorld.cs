using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Elements.Core;
using FrooxEngine;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody;

/// <summary>
/// 0.15.0: the liquid of one world on this client. Every <see cref="FluidEmitter"/> in the world adds its particles to the
/// same simulation (<see cref="FluidSim"/>), so liquids from different emitters meet, mix and push each other. Local only:
/// the emitters and their settings are synced, but each client simulates its own splashes and writes nothing synced.
///
/// It runs itself once per world update while any emitter exists (<see cref="World.RunInUpdates"/>), so the water keeps
/// flowing when an emitter is disabled. Each update: take the last step's result (the surface goes to a local mesh), read
/// the emitters (liquids are live; Volume emitters fill on start and on Refill, every emitter clears on Clear), search for
/// colliders around the liquid, then start the next step (pouring, the simulation, the surface) on the thread pool, one
/// frame behind like the softbodies. Only the focused world simulates.
///
/// 0.15.1: the world thread never waits for a step. A step still running when the next update comes is left to finish
/// and that update shows nothing new (in 0.15.0 the world thread waited, and a step slowed by water that had fallen
/// through a mesh floor froze the game). Emitters joining or leaving are applied between steps. Liquid further than 50 m
/// from every emitter (20 m below the lowest) is removed, and the surface is rebuilt less often while steps run long.
///
/// 0.16.0: when this client's renderer is the Softbody renderer fork with its GPU fluid (and no emitter asks for the CPU),
/// the focused world's liquid is simulated on the graphics card instead: each update sends the emitters and the colliders
/// near the liquid through <see cref="FluidGpuBridge"/>, and the renderer draws the surface with this world's fluid
/// renderer's materials. If the renderer stops answering, the liquid goes back to the CPU (and stays there for 30 s).
///
/// 0.26.0: the world's <see cref="GasEmitter"/>s too (on the GPU only): their records go with the liquid's frame, and the
/// renderer draws the puffs with this world's gas renderer's unlit material. A world with only gas uses the GPU as well.
/// </summary>
sealed class FluidWorld
{
    static readonly ConditionalWeakTable<World, FluidWorld> _all = new();

    public static FluidWorld For(World world)
    {
        lock (_all) return _all.GetValue(world, w => new FluidWorld(w));
    }

    readonly World _world;
    FluidSim _sim;
    FluidDirector _director;
    readonly FluidSurface _surface = new();
    readonly HashSet<FluidEmitter> _emitters = new();
    readonly List<FluidEmitterInput> _inputs = new();
    readonly FluidColliders _colliders = new();
    Task _step;
    long _stepStarted;
    bool _surfacePending, _ticking, _culled, _surfaceThisStep;
    int _lastFrame = -1, _surfaceEvery = 1, _frameNo, _skipped;
    double _workerMs;
    Slot _slot;
    FluidSurfaceMesh _mesh;
    // 0.16.0: the GPU fluid
    bool _onGpu;
    int _owner;
    long _gpuOkAt, _gpuFailedUntil;
    readonly List<FleshCollider> _gpuShapes = new();
    static readonly FluidSurface Empty = new();
    // 0.26.0: the gas (DESIGN-gas.md): its emitters, where each was last update (its puffs take on its motion), its renderer
    readonly HashSet<GasEmitter> _gas = new();
    Dictionary<GasEmitter, Vector3> _gasAt = new(), _gasAtNow = new();
    readonly GasEmitterRecord[] _gasRecords = new GasEmitterRecord[FluidChannel.MaxGasEmitters];
    Slot _gasSlot;
    FluidSurfaceMesh _gasMesh;
    string _gasState = "starting";
    int _gasPuffs;

    public double StepMs { get; private set; }
    public double SurfaceMs { get; private set; }
    public int Count => _sim.Count;
    public string State { get; private set; } = "starting";
    public string Colliders => $"{_colliders.WorldShapes} world, {_colliders.UserShapes} users' bone colliders, {_colliders.UserSpheres} users' spheres, {_sim.ShapeCount} near the liquid";
    public bool OnGpu => _onGpu;

    FluidWorld(World world)
    {
        _world = world;
        _sim = new FluidSim(new FluidSettings { MaxParticles = SoftbodyRuntime.MaxFluidParticles });
        _director = new FluidDirector(_sim);
    }

    // ------------------------------------------------------------------ emitters (world thread)

    /// <summary>World thread: the emitter takes part from the next update (it never touches the simulation itself, so it
    /// never waits for a step).</summary>
    public void Register(FluidEmitter e)
    {
        _emitters.Add(e);
        if (!_ticking) { _ticking = true; _world.RunInUpdates(0, Tick); }
    }

    /// <summary>World thread: the emitter (and at the next update its liquid) is gone.</summary>
    public void Unregister(FluidEmitter e) => _emitters.Remove(e);

    /// <summary>The stable id of an emitter (from its reference id).</summary>
    internal static int IdOf(FluidEmitter e) { ulong id = (ulong)e.ReferenceID; return (int)(id ^ (id >> 32)); }

    public int CountOf(FluidEmitter e) => _director.CountOf(IdOf(e));

    /// <summary>0.26.0, world thread: a gas emitter takes part from the next update.</summary>
    public void RegisterGas(GasEmitter e)
    {
        _gas.Add(e);
        if (!_ticking) { _ticking = true; _world.RunInUpdates(0, Tick); }
    }

    /// <summary>0.26.0, world thread: the gas emitter is gone (its puffs live out their lives).</summary>
    public void UnregisterGas(GasEmitter e) { _gas.Remove(e); _gasAt.Remove(e); }

    /// <summary>0.26.0: the stable id of a gas emitter (from its reference id).</summary>
    internal static int IdOf(GasEmitter e) { ulong id = (ulong)e.ReferenceID; return (int)(id ^ (id >> 32)); }

    void Shutdown()
    {
        if (_onGpu) { FluidGpuBridge.Release(this, _owner); _onGpu = false; }
        DropLiquidRender();
        var gasSlot = _gasSlot;
        _gasSlot = null; _gasMesh = null; _gasAt.Clear();
        try { if (gasSlot != null && !gasSlot.IsRemoved && !gasSlot.IsDisposed) gasSlot.Destroy(); } catch { }
        State = "no emitters";
    }

    /// <summary>0.26.0: the liquid's renderer goes (a world with only gas has none: its refraction would cost every frame).</summary>
    void DropLiquidRender()
    {
        _surfacePending = false;
        var slot = _slot;
        _slot = null; _mesh = null;
        try { if (slot != null && !slot.IsRemoved && !slot.IsDisposed) slot.Destroy(); } catch { }
    }

    // ------------------------------------------------------------------ per update

    void Tick()
    {
        if (_world.IsDestroyed) { _ticking = false; if (_onGpu) { FluidGpuBridge.Release(this, _owner); _onGpu = false; } return; }
        if (_emitters.Count == 0 && _gas.Count == 0 && (_step == null || _step.IsCompleted))
        {
            // the last emitter is gone: its liquid with it, and the surface (0.26.0: and the gas's renderer)
            WaitStep();
            _inputs.Clear();
            _director.Sync(_inputs, 0f);
            Shutdown();
            _ticking = false;
            return;
        }
        try { Frame(); }
        catch (Exception ex) { State = "error: " + ex.Message; SoftbodyRuntime.LogOnce("fluid-frame:" + ex.Message, "fluid update threw: " + ex); }
        _world.RunInUpdates(1, Tick);
    }

    void WaitStep()
    {
        var t = _step;
        if (t == null) return;
        _step = null;
        try { t.Wait(); }
        catch (AggregateException ex) { _surfacePending = false; SoftbodyRuntime.Warn("fluid step failed: " + ex.InnerException?.Message); }
    }

    void Frame()
    {
        int frame = _world.TotalUpdates;
        if (frame == _lastFrame) return;
        _lastFrame = frame;
        float dt = (float)_world.Time.Delta;
        if (!float.IsFinite(dt) || dt <= 0f) return;
        dt = MathF.Min(dt, 0.1f);

        // the last step's result, if it is done: the world thread never waits for it
        if (_step != null)
        {
            double running = (Stopwatch.GetTimestamp() - _stepStarted) * 1000.0 / Stopwatch.Frequency;
            if (!_step.IsCompleted)
            {
                _skipped++;
                if (running > 3000 && _skipped % 300 == 1)
                    SoftbodyRuntime.Warn($"fluid step still running after {running / 1000:0.0} s ({_sim.Count} particles, {_sim.ShapeCount} shapes near the liquid); the world goes on without it");
                State = $"behind: the last step has run {running:0} ms ({_sim.Count} particles)";
                Status();
                return;
            }
            WaitStep();
            _workerMs = running;
            StepMs = _sim.StepMs; SurfaceMs = _surface.BuildMs;
            if (_surfacePending && _surfaceThisStep)
            {
                EnsureRender();
                _mesh?.Submit(_surface);
            }
            _surfacePending = false;
            // rebuild the surface less often while steps run long (the simulation itself keeps every frame)
            double cost = StepMs + SurfaceMs;
            _surfaceEvery = cost > 60 ? 4 : cost > 30 ? 2 : 1;
        }
        if (_world.Focus != World.WorldFocus.Focused) { State = _gasState = "paused: world not focused (only the focused world simulates)"; Status(); return; }

        _emitters.RemoveWhere(e => e.IsRemoved || e.Slot == null || e.Slot.IsRemoved);
        _gas.RemoveWhere(g => g.IsRemoved || g.Slot == null || g.Slot.IsRemoved);

        // 0.16.0: on the GPU when the renderer has it (0.26.0: so is a world with only gas: the gas runs nowhere else)
        bool gpu = (_emitters.Count > 0 || _gas.Count > 0) && WantGpu();
        if (gpu != _onGpu) SwitchSolver(gpu);
        if (_onGpu) { GpuFrame(dt); return; }
        _gasState = GasWithoutGpu(); _gasPuffs = 0;

        ApplySizeAndDetail();

        // the emitters, read into plain inputs, then the director (engine-free, tested offline) does what they ask
        _inputs.Clear();
        foreach (var e in _emitters)
        {
            var scale = V(e.Slot.GlobalScale);
            _inputs.Add(new FluidEmitterInput
            {
                Id = IdOf(e),
                Mode = e.Mode.Value == FluidEmitter.EmitMode.Volume ? FluidEmitMode.Volume : FluidEmitMode.Pour,
                Active = e.Enabled && e.Slot.IsActive,
                Pouring = e.Pouring.Value,
                Position = V(e.Slot.GlobalPosition),
                Rotation = Q(e.Slot.GlobalRotation),
                VolumeHalf = Vector3.Abs(V(e.VolumeSize.Value) * 0.5f * scale),
                Rate = e.Rate.Value, Speed = e.Speed.Value, NozzleRadius = e.NozzleRadius.Value,
                RandomVelocity = float.IsFinite(e.RandomVelocity.Value) ? Math.Clamp(e.RandomVelocity.Value, 0f, 1000f) : 0f,
                MaxParticles = Math.Clamp(e.MaxParticles.Value, 1, SoftbodyRuntime.MaxFluidParticles),
                RefillCount = e.RefillCount.Value, ClearCount = e.ClearCount.Value,
                Liquid = e.ReadLiquid(),
            });
        }
        _director.Sync(_inputs, dt);
        if (_emitters.Count == 0) { DropLiquidRender(); Status(); return; }

        // distance culling: far from every emitter, nothing moves (what is there stays shown)
        _culled = Culled();
        if (_culled) { State = "culled: you are further than every emitter's CullDistance"; Status(); return; }

        // colliders around the liquid and the emitters
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        for (int i = 0; i < _sim.Count; i++) { lo = Vector3.Min(lo, _sim.X[i]); hi = Vector3.Max(hi, _sim.X[i]); }
        foreach (var input in _inputs) { lo = Vector3.Min(lo, input.Position); hi = Vector3.Max(hi, input.Position); }
        if (lo.X <= hi.X) _colliders.Gather(_world, lo, hi, dt, _director.CollideMask, _sim.Colliders);
        else _sim.Colliders.Clear();
        UpdateLod(lo, hi);   // 0.24.0: for the next surface

        // the next step, on a worker
        var sim = _sim; var surf = _surface; var director = _director; float stepDt = dt;
        bool buildSurface = (_frameNo++ % _surfaceEvery) == 0;
        _surfaceThisStep = buildSurface;
        _stepStarted = Stopwatch.GetTimestamp();
        _skipped = 0;
        _step = Task.Run(() =>
        {
            director.Step(stepDt);
            // 0.21.0: not when nothing moved (all asleep) and the surface's settings are the same
            _surfaceThisStep = buildSurface && (sim.Moved || surf.SettingsChanged(sim.Spacing));
            if (_surfaceThisStep) surf.Build(sim.X, sim.Src, sim.Count, sim.Liquids, sim.Spacing);
        });
        _surfacePending = true;
        State = $"running: {_sim.Count} particles in this world, step {StepMs:0.00} ms, surface {SurfaceMs:0.00} ms ({_surface.VertexCount} vertices" +
                (_surfaceEvery > 1 ? $", every {_surfaceEvery} frames" : "") + (_surface.Truncated ? ", part of the liquid not shown" : "") + ") on a worker" +
                (_cpuDetail > 0f && _cpuDetail < ViewDetail() - 0.01f ? $"; surface detail lowered to {_cpuDetail:0.0#} to fit" : "") +
                (_lod < 1f ? $"; surface detail {_lod:0.0#}x for your distance" : "") +
                (_sim.Settings.Classic ? "; classic solver" : "") +
                (_emitters.Any(e => e.Foam.Value > 0f || e.ThickLiquid.Value) ? "; Foam and ThickLiquid need the GPU (the Softbody renderer fork)" : "");
        Status();
    }

    // ------------------------------------------------------------------ 0.16.0: the GPU fluid

    // ------------------------------------------------------------------ 0.17.0: particle size and surface detail

    float _sizeApplied = -1f, _sizeWanted = -1f, _cpuDetail = -1f, _cpuRequested = -1f;
    long _sizeSince;
    int _surfaceBuildsSeen;

    /// <summary>
    /// The world's particle size: the largest of its emitters' (one size per world). 0.19.0: a new size applies once it has
    /// stayed the same for half a second, and 0 (a field being typed into) keeps the last one: typing "0.5" went through 0
    /// (the default 2.5 cm) and every key restarted the liquid.
    /// </summary>
    float WorldParticleSize()
    {
        float s = 0f;
        foreach (var e in _emitters) { float v = e.ParticleSize.Value; if (float.IsFinite(v)) s = MathF.Max(s, v); }
        if (!(s > 0f)) return _sizeApplied > 0f ? _sizeApplied : 0.025f;
        s = Math.Clamp(s, SoftbodyRuntime.MinParticleSize, SoftbodyRuntime.MaxParticleSize);
        long now = Stopwatch.GetTimestamp();
        if (_sizeApplied < 0f) { _sizeApplied = _sizeWanted = s; _sizeSince = now; return s; }
        if (MathF.Abs(s - _sizeWanted) > 1e-6f) { _sizeWanted = s; _sizeSince = now; }
        if (MathF.Abs(s - _sizeApplied) > 1e-6f && now - _sizeSince > Stopwatch.Frequency / 2) _sizeApplied = s;
        return _sizeApplied;
    }

    /// <summary>The world's surface detail: the largest of its emitters'.</summary>
    float WorldSurfaceDetail()
    {
        float d = 0f;
        foreach (var e in _emitters) { float v = e.SurfaceDetail.Value; if (float.IsFinite(v)) d = MathF.Max(d, v); }
        return d > 0f ? Math.Clamp(d, SoftbodyRuntime.MinSurfaceDetail, SoftbodyRuntime.MaxSurfaceDetail) : 1f;
    }

    /// <summary>0.24.0: the surface's detail as drawn for the local user: the world's, lowered with their distance from the liquid
    /// (whole: within 5 m; 0.7 of it to 10 m; half beyond), in steps with half a metre of give, so walking about does not keep
    /// rebuilding it.</summary>
    float ViewDetail() => Math.Clamp(WorldSurfaceDetail() * _lod, SoftbodyRuntime.MinSurfaceDetail, SoftbodyRuntime.MaxSurfaceDetail);

    float _lod = 1f;

    /// <summary>0.24.0: the distance step for the liquid's bounds <paramref name="lo"/>..<paramref name="hi"/> (see ViewDetail).</summary>
    void UpdateLod(Vector3 lo, Vector3 hi)
    {
        if (!(lo.X <= hi.X)) return;
        float3? head = null;
        try { head = _world.LocalUser?.Root?.HeadPosition; } catch { }
        if (head == null) return;
        var h = V(head.Value);
        float d = Vector3.Distance(h, Vector3.Clamp(h, lo, hi));
        const float give = 0.5f;
        float near = _lod >= 1f ? 5f + give : 5f - give, far = _lod >= 0.7f ? 10f + give : 10f - give;
        _lod = d < near ? 1f : d < far ? 0.7f : 0.5f;
    }

    /// <summary>0.25.0: the room the renderer makes for this world's particles: its emitters' MaxParticles together, rounded up to
    /// 65536, 131072, 262144 or 524288 (a new step starts the liquid over, as a new particle size does). 0.16.0 to 0.24.0 always
    /// made room for the most (262144), a few hundred megabytes of graphics memory for a cup of water.</summary>
    int GpuCapacity()
    {
        long want = 0;
        foreach (var e in _emitters) want += Math.Clamp(e.MaxParticles.Value, 1, SoftbodyRuntime.MaxGpuFluidParticles);
        int cap = 65536;
        while (cap < want && cap < SoftbodyRuntime.MaxGpuFluidParticles) cap *= 2;
        return cap;
    }

    /// <summary>0.25.0: the classic solver (0.24.0's), this client's own choice (see SoftbodyRuntime.ClassicSolver).</summary>
    static bool WorldClassic() => SoftbodyRuntime.ClassicSolver;

    /// <summary>The world's droplet size: the largest of its emitters'.</summary>
    float WorldDropletSize()
    {
        float d = 0f;
        foreach (var e in _emitters) { float v = e.DropletSize.Value; if (float.IsFinite(v)) d = MathF.Max(d, v); }
        return d > 0f ? Math.Clamp(d, SoftbodyRuntime.MinDropletSize, SoftbodyRuntime.MaxDropletSize) : 1.5f;
    }

    /// <summary>CPU: a new particle size makes a new simulation (the liquid starts over: Volume emitters fill again); the
    /// surface's detail is live. Only between steps.</summary>
    void ApplySizeAndDetail()
    {
        float size = WorldParticleSize();
        if (MathF.Abs(size - _sim.Settings.ParticleSize) > 1e-5f)
        {
            _sim = new FluidSim(new FluidSettings { MaxParticles = SoftbodyRuntime.MaxFluidParticles, ParticleSize = size, Classic = WorldClassic() });
            _director = new FluidDirector(_sim);
            _mesh?.Submit(Empty);
            SoftbodyRuntime.Log($"fluid in '{_world.Name}': particle size {size * 100:0.#} cm (the liquid starts over)");
        }
        float detail = ViewDetail();
        // 0.19.0: built at a lower detail (just enough) while the asked one runs out of blocks, back up as it fits again
        // (a new asked-for detail applies at once; only a lack of room lowers it)
        if (_cpuDetail < 0f || MathF.Abs(detail - _cpuRequested) > 1e-4f) { _cpuDetail = _cpuRequested = detail; _surfaceBuildsSeen = _surface.Builds; }
        else if (_surface.Builds != _surfaceBuildsSeen) { _surfaceBuildsSeen = _surface.Builds; _cpuDetail = _surface.NextDetail(detail, _cpuDetail); }
        _surface.CellScale = 0.8f / _cpuDetail;
        _surface.DropletSize = WorldDropletSize();
        // 0.25.0: the classic solver, live (the liquid wakes up and carries on)
        bool classic = WorldClassic();
        if (_sim.Settings.Classic != classic) { _sim.Settings.Classic = classic; _sim.WakeAll(); }
        _surface.Classic = classic;
        // more blocks for a finer grid (the same liquid needs detail^3 as many), up to 4x
        _surface.MaxBricks = (int)(24000 * Math.Clamp(detail * detail * detail, 1f, 4f));
    }

    bool WantGpu()
    {
        if (Stopwatch.GetTimestamp() < _gpuFailedUntil) return false;
        foreach (var e in _emitters) if (e.Solver.Value == FluidEmitter.SolverMode.CPU) return false;
        return FluidGpuBridge.Available;
    }

    void SwitchSolver(bool toGpu)
    {
        if (toGpu)
        {
            // the CPU's liquid goes (the GPU starts from the emitters: Volume ones fill again)
            _inputs.Clear();
            _director.Sync(_inputs, 0f);
            _owner = Random.Shared.Next(1, int.MaxValue);
            _gpuOkAt = Stopwatch.GetTimestamp();
            if (_emitters.Count > 0) { EnsureRender(); _mesh?.Submit(Empty); }
            else DropLiquidRender();
            SoftbodyRuntime.Log($"fluid in '{_world.Name}': simulated on the GPU by the renderer fork");
        }
        else
        {
            FluidGpuBridge.Release(this, _owner);
            SoftbodyRuntime.Log($"fluid in '{_world.Name}': simulated on the CPU");
        }
        _onGpu = toGpu;
    }

    void GpuFrame(float dt)
    {
        long now = Stopwatch.GetTimestamp();
        FluidGpuBridge.ReadBack(_owner);
        var b = FluidGpuBridge.Back;
        bool ok = FluidGpuBridge.HaveBack && b.Owner == _owner && b.Ready == 1 && b.Error == 0;
        if (ok) _gpuOkAt = now;
        else if ((FluidGpuBridge.HaveBack && b.Error != 0) || now - _gpuOkAt > 5 * Stopwatch.Frequency)
        {
            _gpuFailedUntil = now + 30 * Stopwatch.Frequency;
            SoftbodyRuntime.Warn($"the GPU fluid did not answer (ready {b.Ready}, error {b.Error}, owner {(b.Owner == _owner ? "this world" : "another")}): back on the CPU for 30 s");
            SwitchSolver(false);
            State = "the GPU fluid stopped answering: on the CPU for now";
            _gasState = GasWithoutGpu(); _gasPuffs = 0;
            Status();
            return;
        }

        // distance culling: nothing new is sent, so the renderer holds the liquid still (and keeps showing it)
        _culled = Culled();
        if (_culled) { State = _gasState = "culled: you are further than every emitter's CullDistance (GPU)"; Status(); return; }

        // the emitters as records
        var em = FluidGpuBridge.Emitters(_emitters.Count);
        int n = 0;
        uint mask = 0;
        bool foamOn = false, thickCapped = false;   // 0.27.0
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var e in _emitters)
        {
            if (n >= FluidChannel.MaxEmitters) break;
            var pos = V(e.Slot.GlobalPosition); var rot = Q(e.Slot.GlobalRotation);
            var half = Vector3.Abs(V(e.VolumeSize.Value) * 0.5f * V(e.Slot.GlobalScale));
            var l = e.ReadLiquid();
            mask |= l.CollideMask;
            lo = Vector3.Min(lo, pos); hi = Vector3.Max(hi, pos);
            em[n++] = new FluidEmitterRecord
            {
                Id = IdOf(e), Mode = e.Mode.Value == FluidEmitter.EmitMode.Volume ? 1 : 0, Pouring = e.Pouring.Value ? 1 : 0,
                RefillCount = e.RefillCount.Value, ClearCount = e.ClearCount.Value,
                MaxParticles = Math.Clamp(e.MaxParticles.Value, 1, SoftbodyRuntime.MaxGpuFluidParticles),
                Rate = e.Rate.Value, Speed = e.Speed.Value, NozzleRadius = e.NozzleRadius.Value,
                PosX = pos.X, PosY = pos.Y, PosZ = pos.Z, RotX = rot.X, RotY = rot.Y, RotZ = rot.Z, RotW = rot.W,
                HalfX = half.X, HalfY = half.Y, HalfZ = half.Z,
                Viscosity = l.Viscosity, Cohesion = l.Cohesion, Adhesion = l.Adhesion, Friction = l.Friction, Density = l.Density,
                Gravity = l.Gravity, Damping = l.Damping, Lifetime = l.Lifetime, CollideMask = l.CollideMask,
                ColR = l.Color.X, ColG = l.Color.Y, ColB = l.Color.Z, ColA = l.Color.W,
                Active = e.Enabled && e.Slot.IsActive ? 1 : 0,
                RandomVelocity = float.IsFinite(e.RandomVelocity.Value) ? Math.Clamp(e.RandomVelocity.Value, 0f, 1000f) : 0f,
                // 0.27.0
                Foam = float.IsFinite(e.Foam.Value) ? Math.Clamp(e.Foam.Value, 0f, 10f) : 0f,
                FoamLifetime = float.IsFinite(e.FoamLifetime.Value) ? Math.Clamp(e.FoamLifetime.Value, 0.1f, 60f) : 3f,
                Thick = e.ThickLiquid.Value ? 1 : 0,
            };
            if (em[n - 1].Foam > 0f && em[n - 1].Active != 0) foamOn = true;
            if (em[n - 1].Thick != 0 && l.Viscosity > FluidChannel.ThickViscosityCap) thickCapped = true;
        }

        // the colliders around the liquid (its bounds as the renderer last reported them) and the emitters
        if (ok && b.Particles > 0)
        {
            lo = Vector3.Min(lo, new Vector3(b.BoundsMinX, b.BoundsMinY, b.BoundsMinZ));
            hi = Vector3.Max(hi, new Vector3(b.BoundsMaxX, b.BoundsMaxY, b.BoundsMaxZ));
        }
        UpdateLod(lo, hi);   // 0.24.0 (0.26.0: on the liquid's own box, not the gas's)

        // 0.26.0: the gas emitters as records, with how fast each moves (its new puffs take that on; not across a jump)
        int ng = 0;
        foreach (var g in _gas)
        {
            if (ng >= FluidChannel.MaxGasEmitters) break;
            var at = V(g.Slot.GlobalPosition);
            var vel = Vector3.Zero;
            if (_gasAt.TryGetValue(g, out var was) && Vector3.DistanceSquared(at, was) < 0.25f) vel = (at - was) / dt;
            _gasAtNow[g] = at;
            var r = g.Record(IdOf(g), vel);
            mask |= r.CollideMask;
            lo = Vector3.Min(lo, at); hi = Vector3.Max(hi, at);
            _gasRecords[ng++] = r;
        }
        (_gasAt, _gasAtNow) = (_gasAtNow, _gasAt);
        _gasAtNow.Clear();
        if (ok && b.GasPuffs > 0 && b.GasMinX <= b.GasMaxX)   // and the gas's bounds
        {
            lo = Vector3.Min(lo, new Vector3(b.GasMinX, b.GasMinY, b.GasMinZ));
            hi = Vector3.Max(hi, new Vector3(b.GasMaxX, b.GasMaxY, b.GasMaxZ));
        }
        var pad = new Vector3(0.5f);
        if (mask != 0 && lo.X <= hi.X) _colliders.Gather(_world, lo, hi, dt, mask, _gpuShapes);
        else _gpuShapes.Clear();
        int meshId = -1, gasMeshId = -1;
        if (_emitters.Count > 0) { EnsureRender(); try { meshId = _mesh?.Asset?.AssetId ?? -1; } catch { } }
        else DropLiquidRender();
        // 0.26.0: the gas's renderer, kept once made (the last puffs live out their lives after their emitters go). 0.27.0: the
        // spray, foam and bubbles draw with its material too, so a liquid with foam makes it as well
        if (_gas.Count > 0 || _gasSlot != null || foamOn) { EnsureGasRender(); try { gasMeshId = _gasMesh?.Asset?.AssetId ?? -1; } catch { } }
        var head = Vector3.Zero;
        try { var hp = _world.LocalUser?.Root?.HeadPosition; if (hp != null) head = V(hp.Value); } catch { }
        float size = WorldParticleSize(), detail = ViewDetail();
        bool classic = WorldClassic();
        FluidGpuBridge.Write(this, _owner, dt, meshId, size, detail, WorldDropletSize(), classic, GpuCapacity(), n, _gpuShapes, lo - pad, hi + pad, out int colliders, out bool truncated,
                             _gasRecords, ng, gasMeshId, head);
        _gasPuffs = ok ? b.GasPuffs : 0;
        _gasState = !ok ? "GPU: waiting for the renderer" :
            $"running on the GPU: {b.GasPuffs} puffs in this world" +
            (b.Vortices > 0 ? $", {b.Vortices} vortex particles" : "") +
            (_gas.Count > FluidChannel.MaxGasEmitters ? $"; {FluidChannel.MaxGasEmitters} gas emitters in a world at most (the rest add nothing)" : "") +
            (b.GasPuffs > 0 && b.GasDrawing == 0 ? "; not drawn: the renderer has not found this world's gas renderer yet" : "");
        StepMs = b.StepMs; SurfaceMs = b.CpuMs;
        State = !ok ? "GPU: waiting for the renderer" :
            $"running on the GPU: {b.Particles} particles in this world, GPU {b.StepMs:0.00} ms, render thread {b.CpuMs:0.00} ms ({b.SurfaceVertices} vertices), {colliders} collider shapes" +
            (truncated ? " (more were left out)" : "") + $", {b.ParticleSize * 100:0.#} cm particles" +
            (b.SurfaceDetailUsed > 0f && b.SurfaceDetailUsed < detail - 0.01f ? $"; surface detail lowered to {b.SurfaceDetailUsed:0.0#} to fit" : "") +
            (_lod < 1f ? $"; surface detail {_lod:0.0#}x for your distance" : "") +
            (b.SurfaceTruncated != 0 ? "; part of the surface not shown" : "") +
            (b.Drawing == 0 ? "; not drawn: the renderer has not found this world's fluid renderer yet" : "") +
            // 0.27.0
            (b.FoamParticles > 0 ? $"; {b.FoamParticles} spray, foam and bubble particles" + (b.FoamDrawing == 0 ? " (not drawn yet: the renderer has not found the gas renderer)" : "") : "") +
            (b.ThickIterations > 0 ? $"; thick liquid: up to {b.ThickIterations} solver iterations" + (b.ThickUnconverged > 0 ? " (some stopped at the cap)" : "") : "") +
            (thickCapped ? $"; ThickLiquid holds Viscosity to {FluidChannel.ThickViscosityCap:0}" : "") +
            (classic ? "; classic solver" : "");
        Status();
    }

    bool Culled()
    {
        float3? head = null;
        try { head = _world.LocalUser?.Root?.HeadPosition; } catch { }
        if (head == null) return false;
        var h = V(head.Value);
        foreach (var e in _emitters) if (Near(e.Slot, e.CullDistance.Value, h)) return false;
        foreach (var g in _gas) if (Near(g.Slot, g.CullDistance.Value, h)) return false;   // 0.26.0
        return true;
    }

    /// <summary>Within an emitter's CullDistance (0: always), with a tenth of it to spare once culled.</summary>
    bool Near(Slot slot, float cull, Vector3 head) =>
        !(cull > 0f) || (slot != null && Vector3.Distance(V(slot.GlobalPosition), head) <= (_culled ? 0.9f * cull : cull));

    /// <summary>0.26.0: why the gas does not run (it runs on the GPU only).</summary>
    string GasWithoutGpu()
    {
        if (Stopwatch.GetTimestamp() < _gpuFailedUntil) return "off: the GPU fluid stopped answering (tried again within 30 s)";
        foreach (var e in _emitters) if (e.Solver.Value == FluidEmitter.SolverMode.CPU) return "off: a FluidEmitter in this world asks for the CPU solver, and the gas runs on the GPU only";
        return "off: needs the Softbody renderer fork (the gas runs on the GPU only)";
    }

    void Status()
    {
        foreach (var g in _gas) g.SetStatus(_gasState, _gasPuffs);
        if (_onGpu) { foreach (var e in _emitters) e.SetStatus(State, FluidGpuBridge.CountOf(IdOf(e)), FluidGpuBridge.Back.Particles, (float)StepMs, (float)SurfaceMs); return; }
        foreach (var e in _emitters) e.SetStatus(State, _director.CountOf(IdOf(e)), _sim.Count, (float)StepMs, (float)SurfaceMs);
    }

    void EnsureRender()
    {
        if (_slot != null && !_slot.IsRemoved && _mesh != null && !_mesh.IsRemoved) return;
        var slot = _world.RootSlot.AddLocalSlot("Softbody fluid (local)", false);
        var mesh = slot.AttachComponent<FluidSurfaceMesh>();
        // refraction through the water, then a glossy layer in each liquid's colour (and opacity) over it
        var refract = slot.AttachComponent<RefractMaterial>();
        refract.RefractionStrength.Value = 0.04f;
        var gloss = slot.AttachComponent<PBS_VertexColorMetallic>();
        gloss.AlphaHandling.Value = AlphaHandling.AlphaBlend;
        gloss.Smoothness.Value = 0.95f;
        gloss.Metallic.Value = 0f;
        gloss.AlbedoColor.Value = colorX.White;
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Materials.Add(refract);
        renderer.Materials.Add(gloss);
        _slot = slot; _mesh = mesh;
    }

    /// <summary>0.26.0: the gas's placeholder: an empty mesh the renderer fork finds by its asset (as the liquid's), drawn with
    /// this unlit material: each puff's vertex colours (its colour and opacity, clear at its rim), blended over what is behind
    /// it without hiding it, from both sides.</summary>
    void EnsureGasRender()
    {
        if (_gasSlot != null && !_gasSlot.IsRemoved && _gasMesh != null && !_gasMesh.IsRemoved) return;
        var slot = _world.RootSlot.AddLocalSlot("Softbody gas (local)", false);
        var mesh = slot.AttachComponent<FluidSurfaceMesh>();
        var unlit = slot.AttachComponent<UnlitMaterial>();
        unlit.TintColor.Value = colorX.White;
        unlit.UseVertexColors.Value = true;
        unlit.BlendMode.Value = BlendMode.Alpha;
        unlit.ZWrite.Value = ZWrite.Off;
        unlit.Sidedness.Value = Sidedness.Double;
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Materials.Add(unlit);
        mesh.Submit(Empty);
        _gasSlot = slot; _gasMesh = mesh;
    }

    static Vector3 V(in float3 f) => new(f.x, f.y, f.z);
    static Quaternion Q(in floatQ q) => new(q.x, q.y, q.z, q.w);
}

/// <summary>
/// 0.15.0: what the liquid collides with, found in a box around it: solid world colliders (CharacterCollider on, Static or
/// Active), users' bone colliders (VRIKAvatar's, recognised exactly through its list of them) tagged by the part of the
/// player they are on, and the engine's dynamic-bone spheres for users without bone colliders. One way: the water is
/// pushed out of them and never pushes back. Searched 10 times a second, shapes rebuilt from their slots every update.
/// </summary>
sealed class FluidColliders
{
    float _timer;
    readonly List<ICollider> _overlap = new();
    readonly List<Collider> _solids = new(), _bones = new();
    readonly Dictionary<FrooxEngine.FinalIK.VRIKAvatar, HashSet<Collider>> _rigSets = new();
    readonly HashSet<User> _withRig = new();
    readonly Dictionary<Collider, PlayerPart> _partOf = new();
    // 0.19.0: where each world collider was last update, for its velocity (a moving one is pushed out of before the still
    // ones, so a floor always has the last say: a grabbed box pressed into a puddle pushed water through the floor)
    Dictionary<Collider, Vector3> _lastAt = new(), _nowAt = new(), _boneLast = new(), _boneNow = new();
    public int WorldShapes, UserShapes, UserSpheres;

    public void Gather(World world, Vector3 lo, Vector3 hi, float dt, uint mask, List<FleshCollider> into)
    {
        into.Clear();
        WorldShapes = UserShapes = UserSpheres = 0;
        if (mask == 0) return;
        var center = 0.5f * (lo + hi);
        var size = (hi - lo) + new Vector3(1.0f);
        _timer -= dt;
        if (_timer <= 0f)
        {
            _timer = 0.1f;
            _overlap.Clear(); _solids.Clear(); _bones.Clear(); _rigSets.Clear(); _withRig.Clear();
            var o = new float3(center.X, center.Y, center.Z); var rot = floatQ.Identity; var fs = new float3(size.X, size.Y, size.Z);
            world.Physics.BoxOverlap(in o, in rot, in fs, _overlap, IsCandidate, false);
            foreach (var c in _overlap)
            {
                if (_solids.Count + _bones.Count >= 128) break;
                if (c is not Collider col) continue;
                if (c.CharacterCollider) { if (!_solids.Contains(col)) _solids.Add(col); }
                else if (IsUsersBoneCollider(col) && !_bones.Contains(col)) _bones.Add(col);
            }
        }
        if ((mask & FluidSim.CategoryWorld) != 0)
            foreach (var col in _solids)
            {
                if (!Usable(col)) continue;
                int first = into.Count;
                if (!FleshInstance.AddShape(col, SkinMatrices.ToNumerics(col.Slot.LocalToGlobal), 0, null, into)) continue;
                WorldShapes++;
                var at = new Vector3(col.Slot.GlobalPosition.x, col.Slot.GlobalPosition.y, col.Slot.GlobalPosition.z);
                _nowAt[col] = at;
                if (dt > 0f && _lastAt.TryGetValue(col, out var was) && Vector3.DistanceSquared(at, was) < 0.25f)
                {
                    var v = (at - was) / dt;
                    for (int k = first; k < into.Count; k++) { var sh = into[k]; sh.Velocity = v; into[k] = sh; }
                }
            }
        (_lastAt, _nowAt) = (_nowAt, _lastAt);
        _nowAt.Clear();
        const uint users = FluidSim.CategoryHead | FluidSim.CategoryBody | FluidSim.CategoryHands;
        if ((mask & users) == 0) return;
        foreach (var col in _bones)
        {
            if (!Usable(col)) continue;
            byte cat = Category(_partOf.GetValueOrDefault(col, PlayerPart.Body));
            if ((mask & cat) == 0) continue;
            int first = into.Count;
            if (!FleshInstance.AddShape(col, SkinMatrices.ToNumerics(col.Slot.LocalToGlobal), cat, null, into)) continue;
            UserShapes++;
            // 0.25.0: a user's bone moves too: its velocity (for friction and for waking sleeping liquid)
            var at = new Vector3(col.Slot.GlobalPosition.x, col.Slot.GlobalPosition.y, col.Slot.GlobalPosition.z);
            _boneNow[col] = at;
            if (dt > 0f && _boneLast.TryGetValue(col, out var was) && Vector3.DistanceSquared(at, was) < 0.25f)
            {
                var v = (at - was) / dt;
                for (int k = first; k < into.Count; k++) { var sh = into[k]; sh.Velocity = v; into[k] = sh; }
            }
        }
        (_boneLast, _boneNow) = (_boneNow, _boneLast);
        _boneNow.Clear();
        var half = 0.5f * size;
        UserSpheres = UserColliderReader.CollectParts(world, _withRig, center - half, center + half, into, part => (mask & Category(part)) != 0 ? Category(part) : (byte)0);
    }

    static byte Category(PlayerPart part) => part switch
    {
        PlayerPart.Head => (byte)FluidSim.CategoryHead,
        PlayerPart.Hands => (byte)FluidSim.CategoryHands,
        _ => (byte)FluidSim.CategoryBody,
    };

    static bool Usable(Collider col) => !col.IsRemoved && col.Enabled && col.Slot != null && col.Slot.IsActive;

    static bool IsCandidate(ICollider c)
    {
        if (c == null || c.IsRemoved) return false;
        var type = c.ColliderType;
        if (type != ColliderType.Static && type != ColliderType.Active) return false;
        if (c is not Component comp || comp.Slot == null) return false;
        return c.CharacterCollider || comp.Slot.ActiveUserRoot != null;
    }

    bool IsUsersBoneCollider(Collider col)
    {
        var av = col.Slot.GetComponentInParents<FrooxEngine.FinalIK.VRIKAvatar>();
        if (av == null) return false;
        if (!_rigSets.TryGetValue(av, out var set))
        {
            var list = new List<Collider>();
            AvatarRigColliders.Collect(av, list);
            set = new HashSet<Collider>(list);
            _rigSets[av] = set;
            if (set.Count > 0) { try { var u = av.Slot.ActiveUser; if (u != null) _withRig.Add(u); } catch { } }
        }
        if (!set.Contains(col)) return false;
        if (!_partOf.ContainsKey(col)) _partOf[col] = PartOf(col, av);
        if (_partOf.Count > 2048) _partOf.Clear();
        return true;
    }

    static PlayerPart PartOf(Collider col, FrooxEngine.FinalIK.VRIKAvatar av)
    {
        try
        {
            var refs = av?.IK.Target?.Solver?.BoneReferences;
            Slot head = refs?.head.Target, left = refs?.leftHand.Target, right = refs?.rightHand.Target;
            for (var s = col.Slot; s != null; s = s.Parent)
            {
                if (head != null && ReferenceEquals(s, head)) return PlayerPart.Head;
                if ((left != null && ReferenceEquals(s, left)) || (right != null && ReferenceEquals(s, right))) return PlayerPart.Hands;
                if (av != null && ReferenceEquals(s, av.Slot)) break;
            }
        }
        catch { }
        return PlayerPart.Body;
    }
}
