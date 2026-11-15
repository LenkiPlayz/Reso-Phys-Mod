// Live bounce light (GI) inside the Softbody renderer fork (Renderite.Unity, Unity 2019.4, Direct3D 11): what is Resonite's.
// Compiled into the fork only, and only in its GI build (SOFTBODY_GI: Renderite.Unity.csproj's ReleaseGI configuration); the
// engine-free parts (GiUnity, GiCompute, GiPlanner) are tested offline and in the SoftbodyShaders editor project.
//
// Main thread, once a frame (RenderingManager.HandleUpdate calls Update, next to the GPU fluid): finds the world the user is in
// (the active space that is not an overlay), reads the settings, and sweeps over the world's renderers a few hundred at a time,
// giving each the block that makes Unity's lit shaders read the bounce light (the volume) instead of the plain ambient.
// MeshRendererManager calls PrepareShared before it sets one of the world's own MaterialPropertyBlocks on a renderer (those
// override the renderer's own block).
// At the end of the frame, after its cameras (EndOfFrame, a coroutine on the RenderingManager): the head and the world's lights go
// to GiUnity, which draws a slab of the world into voxels and queues the GPU's share, which the render thread runs in the next
// frame's asset integration. GI test 1 drew from Update: the frame's first camera draw takes the wait for the last frame to reach the
// screen (~5.5 ms at 144 Hz in the game), and there that wait came right before the asset integration, which works only while the
// renderer waits for the engine's frame, so loading starved (panels stalled ~5 s). At the end of the frame the same draw takes
// ~0.1 ms (SoftbodyShaders\build_gi.ps1 -Pacing, 2026-11-14).
//
// Settings: Renderite.Renderer_Data/StreamingAssets/Softbody/gi.ini, read again within a second of a change (see ReadSettings).
// The voxelize shader: softbody_gi.bundle next to it (SoftbodyShaders/build_gi.ps1 builds it; make_softbody_renderer.py --gi
// copies both). It is used only once it has drawn a test square here (GiUnity.StartSelfTest).
#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Renderite.Unity;
using UnityEngine;

namespace Softbody.GI
{
    public static class SoftbodyGIHost
    {
        public const string BundleName = "softbody_gi.bundle";
        public const string SettingsName = "gi.ini";
        public const string ShaderName = "Hidden/Softbody/GIVoxelize";
        const string Tag = GiUnity.Tag;

        static bool _started, _failed, _testStarted, _ready, _gpuFailed;
        static Task<Dictionary<string, byte[]>> _compile;
        static Dictionary<string, byte[]> _bytecode;
        static Shader _voxelize;
        static GiUnity _gi;
        static int _testFrames;

        // settings (gi.ini)
        static string _settingsPath;
        static DateTime _settingsTime;
        static float _settingsTimer;
        static bool _enabled = true, _avatars;
        static int _voxelizeEvery = 1;
        static float _statsEvery = 10f, _statsTimer;
        static readonly GiConfig Defaults = new GiConfig();
        static int _glowRays = Defaults.GlowRays;
        static float _leakOffset = Defaults.LeakOffset;

        // the head, for the thin renderers' lookup offsets (the sweep and FollowHead)
        static Vector3 _head;
        static bool _hasHead;

        // the world GI lights, and its lights
        static RenderSpace _space;
        static readonly List<Light> _lights = new List<Light>();
        static readonly List<Renderer> _hidden = new List<Renderer>();

        // the end of the frame: Update asks for a tick (_tickDue), EndOfFrame runs it
        static bool _tickDue;
        static Coroutine _endOfFrame;
        static int _tickMask, _endOfFrameSeen, _endOfFrameRestarts;
        static double _updateMs;

        // the sweep over the world's renderers
        const int SweepChecks = 4000, SweepApplies = 300;
        static int _sweepList, _sweepIndex, _passEpoch = -1, _passes, _applied;

        /// <summary>Main thread, once a frame (in RenderingManager.HandleUpdate, before the asset integration: keep it short).</summary>
        public static void Update()
        {
            if (_failed) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { UpdateInner(); }
            catch (Exception ex) { Fail("stopped: " + ex); }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _updateMs = _updateMs <= 0 ? ms : 0.95 * _updateMs + 0.05 * ms;
        }

        /// <summary>Main thread: one of a world's MaterialPropertyBlocks, just before the fork sets it on <paramref name="renderer"/>
        /// of <paramref name="space"/> (the renderer keeps a copy; a thin one's gets its lookup offset).</summary>
        public static void PrepareShared(RenderSpace space, MaterialPropertyBlock block, Renderer renderer)
        {
            if (_gi == null || block == null || space == null || space != _space) return;
            try { _gi.PrepareShared(block, renderer); }
            catch (Exception ex) { Fail("stopped (preparing a block): " + ex); }
        }

        public static void PrepareShared(RenderSpace space, MaterialPropertyBlock block) => PrepareShared(space, block, null);

        static void Fail(string why)
        {
            _failed = true;
            Debug.LogError(Tag + why);
            // the renderers keep whatever block they have: the last light the volume held (it no longer changes)
            try { ShowAvatars(); } catch { }
        }

        static void UpdateInner()
        {
            _tickDue = false;
            if (!_started)
            {
                _started = true;
                if (RenderingManager.Instance == null || AssetIntegrator._dx11device == null || !RenderingManager.Instance.AssetIntegrator.RenderThreadProcessingEnabled)
                {
                    Debug.Log(Tag + "not Direct3D 11: the bounce light stays off");
                    _failed = true;
                    return;
                }
                string folder = Path.Combine(Application.streamingAssetsPath, "Softbody");
                _settingsPath = Path.Combine(folder, SettingsName);
                ReadSettings(true);
                Debug.Log(Tag + $"settings: {Describe()} ({(File.Exists(_settingsPath) ? _settingsPath : "no " + _settingsPath + ": the defaults")})");
                _voxelize = LoadShader(Path.Combine(folder, BundleName));
                if (_voxelize == null) { _failed = true; return; }
                string hlsl = Hlsl();
                _compile = Task.Run(() => GiCompute.CompileAll(hlsl));
                Debug.Log(Tag + "compiling the GPU side's kernels");
            }
            ReadSettings(false);
            if (_bytecode == null)
            {
                if (!_compile.IsCompleted) return;
                if (_compile.IsFaulted) { Fail("compiling failed: " + _compile.Exception); return; }
                _bytecode = _compile.Result;
                Debug.Log(Tag + "kernels compiled");
            }
            if (_gi == null)
            {
                var rm = RenderingManager.Instance;
                _gi = new GiUnity(new GiConfig(), _voxelize, _bytecode, () => AssetIntegrator._dx11device,
                                  a => rm.AssetIntegrator.EnqueueRenderThreadProcessing(a));
                Debug.Log(Tag + $"made (ambient {RenderSettings.ambientMode} x{RenderSettings.ambientIntensity:0.##}, "
                          + $"lights use linear intensity {UnityEngine.Rendering.GraphicsSettings.lightsUseLinearIntensity}, {QualitySettings.activeColorSpace} colour); drawing a test square");
            }
            if (!_ready)
            {
                if (!_testStarted)
                {
                    _testStarted = true;
                    _gi.StartSelfTest(LayerMask.NameToLayer(RenderHelper.TEMP_LAYER));
                    return;
                }
                var result = _gi.TestResult(out string test);
                if (result == null)
                {
                    if (++_testFrames > 600) Fail("the test square's read-back never came");
                    return;
                }
                if (result == false) { Fail("the voxelize shader does not work on this renderer (" + test + "): no bounce light"); return; }
                _ready = true;
                Debug.Log(Tag + "voxelize shader works (" + test + "); bounce light on");
            }
            if (GiShaderWatch.Tripped) { Fail("the renderer could not make one of the voxelize shader's programs (\"" + GiShaderWatch.TrippedBy + "\"): no bounce light"); return; }
            if (_gi.Failure != null && !_gpuFailed)
            {
                // the GPU side stopped: the renderers go back to the plain ambient (the sweep goes on)
                _gpuFailed = true;
                _gi.SetWanted(false);
                Debug.LogError(Tag + "the GPU side failed; the bounce light goes off: " + _gi.Failure);
            }

            var space = FindSpace();
            if (space != _space)
            {
                ShowAvatars();
                _space = space;
                _gi.Restart();
                _sweepList = 0; _sweepIndex = 0; _passEpoch = -1;
                Debug.Log(Tag + (space != null ? "now lighting space " + space.Id : "no world to light"));
            }
            if (_space == null) return;
            _gi.SetWanted(_enabled && !_gpuFailed);
            _gi.VoxelizeEvery = _voxelizeEvery;
            _gi.GlowRays = _glowRays;
            _gi.LeakOffset = _leakOffset;   // (a change: a new epoch, so the sweep gives every renderer its block again)
            if (TryHead(out var head)) { _head = head; _hasHead = true; }

            // the voxel camera's draw and the GPU's share: at the end of this frame (EndOfFrame)
            _tickMask = _space.IsPrivate ? RenderHelper.PRIVATE_RENDER_MASK : RenderHelper.PUBLIC_RENDER_MASK;
            _tickDue = !_gpuFailed;
            KeepEndOfFrame();
            Sweep();
            if (_hasHead) _gi.FollowHead(_head);
            Stats();
        }

        /// <summary>Starts EndOfFrame on the RenderingManager (once), and again (replacing it) should it stop running.</summary>
        static void KeepEndOfFrame()
        {
            if (_endOfFrame != null && Time.frameCount - _endOfFrameSeen < 60) return;
            var rm = RenderingManager.Instance;
            if (_endOfFrame != null)
            {
                rm.StopCoroutine(_endOfFrame);
                if (++_endOfFrameRestarts <= 3) Debug.LogWarning(Tag + "the end-of-frame step did not run for 60 frames; starting it again");
            }
            _endOfFrameSeen = Time.frameCount;
            _endOfFrame = rm.StartCoroutine(EndOfFrame());
            if (_endOfFrame == null) Fail("could not start the end-of-frame step");
        }

        /// <summary>The end of every frame, after its cameras drew: the tick Update asked for (the voxel camera's draw does not wait
        /// for the screen here).</summary>
        static IEnumerator EndOfFrame()
        {
            var wait = new WaitForEndOfFrame();
            while (!_failed)
            {
                yield return wait;
                _endOfFrameSeen = Time.frameCount;
                if (!_tickDue || _failed) continue;
                _tickDue = false;
                try { TickNow(); }
                catch (Exception ex) { Fail("stopped (end of the frame): " + ex); }
            }
        }

        static void TickNow()
        {
            if (_gi == null || _space == null || _gpuFailed || !TryHead(out var head)) return;
            _lights.Clear();
            var lights = _space.Lights;
            for (int i = 0; i < lights.RenderableCount; i++)
            {
                var l = lights[i]?.Light;
                if (l != null) _lights.Add(l);
            }
            _gi.Tick(head, _tickMask, _lights, _avatars ? null : (Action<bool>)HideAvatars);
        }

        // ------------------------------------------------------------------ setup

        static Shader LoadShader(string path)
        {
            try
            {
                if (!File.Exists(path)) { Debug.Log(Tag + "no " + path + ": the bounce light stays off"); return null; }
                var bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null) { Debug.LogWarning(Tag + "could not load " + path + ": the bounce light stays off"); return null; }
                Shader found = null;
                foreach (var s in bundle.LoadAllAssets<Shader>()) if (s != null && s.name == ShaderName) found = s;
                if (found == null) { Debug.LogWarning(Tag + "the bundle has no " + ShaderName + ": the bounce light stays off"); return null; }
                if (!found.isSupported) { Debug.LogWarning(Tag + ShaderName + " is not supported on this renderer: the bounce light stays off"); return null; }
                return found;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(Tag + "the voxelize shader failed to load (the bounce light stays off): " + ex.Message);
                return null;
            }
        }

        static string Hlsl()
        {
            using (var s = typeof(SoftbodyGIHost).Assembly.GetManifestResourceStream("Softbody.GI.SoftbodyGI.hlsl"))
            {
                if (s == null) throw new InvalidOperationException("the SoftbodyGI.hlsl resource is missing");
                using (var r = new StreamReader(s)) return r.ReadToEnd();
            }
        }

        /// <summary>
        /// gi.ini, one "key = value" a line ('#' starts a comment):
        ///   enabled = 1           0: off (the renderers go back to the plain ambient)
        ///   avatars = 0          1: avatars (skinned meshes) block and bounce light too (they smear as they move)
        ///   voxelize_every = 1    draw a slab of the world every this many frames (2: half the CPU, half as quick to follow changes)
        ///   stats = 10            seconds between "[Softbody GI]" lines in the renderer's log (0: none)
        ///   glow_rays = 4         shadow rays a probe aims at glowing things (0 to 8; 0: only its rays find them, which flickers)
        ///   leak_offset = 0.5     how far walls and floors read the light in front of themselves, toward you (m; 0: off)
        /// </summary>
        static void ReadSettings(bool first)
        {
            if (!first)
            {
                _settingsTimer -= Time.unscaledDeltaTime;
                if (_settingsTimer > 0f) return;
            }
            _settingsTimer = 1f;
            try
            {
                if (!File.Exists(_settingsPath)) return;
                var t = File.GetLastWriteTimeUtc(_settingsPath);
                if (!first && t == _settingsTime) return;
                _settingsTime = t;
                foreach (var raw in File.ReadAllLines(_settingsPath))
                {
                    string line = raw;
                    int hash = line.IndexOf('#');
                    if (hash >= 0) line = line.Substring(0, hash);
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant(), val = line.Substring(eq + 1).Trim().ToLowerInvariant();
                    switch (key)
                    {
                        case "enabled": _enabled = val != "0" && val != "false" && val != "off"; break;
                        case "avatars": _avatars = val == "1" || val == "true" || val == "on"; break;
                        case "voxelize_every": if (int.TryParse(val, out int e)) _voxelizeEvery = Math.Max(1, Math.Min(e, 60)); break;
                        case "stats": if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s)) _statsEvery = Math.Max(0f, s); break;
                        case "glow_rays": if (int.TryParse(val, out int g)) _glowRays = Math.Max(0, Math.Min(g, 8)); break;
                        case "leak_offset": if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float lo)) _leakOffset = Math.Max(0f, Math.Min(lo, 2f)); break;
                    }
                }
                if (!first) Debug.Log(Tag + "settings changed: " + Describe());
            }
            catch (Exception ex) { Debug.LogWarning(Tag + "could not read " + _settingsPath + ": " + ex.Message); }
        }

        static string Describe() => $"enabled {(_enabled ? 1 : 0)}, avatars {(_avatars ? 1 : 0)}, voxelize_every {_voxelizeEvery}, stats {_statsEvery:0.#}, "
                                    + $"glow_rays {_glowRays}, leak_offset {_leakOffset.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}";

        // ------------------------------------------------------------------ each frame

        /// <summary>The world the user is in: the active space that is not an overlay.</summary>
        static RenderSpace FindSpace()
        {
            foreach (var s in RenderingManager.Instance.RenderSpaces)
                if (s != null && s.IsActive && !s.IsOverlay) return s;
            return null;
        }

        static bool TryHead(out Vector3 head)
        {
            head = default;
            var rm = RenderingManager.Instance;
            var o = rm.VROutput != null && rm.VROutput.gameObject.activeInHierarchy ? rm.VROutput : rm.ScreenOutput;
            if (o == null || o.cameras == null || o.cameras.Count == 0 || o.cameras[0] == null) return false;
            head = o.cameras[0].transform.position;
            return !(float.IsNaN(head.x) || float.IsNaN(head.y) || float.IsNaN(head.z) || float.IsInfinity(head.x + head.y + head.z));
        }

        /// <summary>Hides the world's avatars (skinned meshes) from the voxel camera (true), or shows them again (false).</summary>
        static void HideAvatars(bool hide)
        {
            if (!hide) { ShowAvatars(); return; }
            _hidden.Clear();
            if (_space == null) return;
            var sk = _space.SkinnedMeshes;
            for (int i = 0; i < sk.RenderableCount; i++)
            {
                var r = sk[i]?.Renderer;
                if (r != null && !r.forceRenderingOff) { r.forceRenderingOff = true; _hidden.Add(r); }
            }
        }

        static void ShowAvatars()
        {
            for (int i = 0; i < _hidden.Count; i++) if (_hidden[i] != null) _hidden[i].forceRenderingOff = false;
            _hidden.Clear();
        }

        /// <summary>A few hundred of the world's renderers a frame get this epoch's block (each once an epoch: Renderable's
        /// SoftbodyGIEpoch). Renderers come and go (Resonite moves the last one into a removed one's place), so a window switch counts
        /// as done after two whole passes at its epoch.</summary>
        static void Sweep()
        {
            if (_space == null || _gi == null) return;
            int epoch = _gi.Epoch;
            if (epoch != _passEpoch) { _passEpoch = epoch; _passes = 0; _sweepList = 0; _sweepIndex = 0; }
            int checks = SweepChecks, applies = SweepApplies;
            var meshes = _space.Meshes;
            var skinned = _space.SkinnedMeshes;
            while (checks > 0 && applies > 0)
            {
                int count = _sweepList == 0 ? meshes.RenderableCount : skinned.RenderableCount;
                if (_sweepIndex >= count)
                {
                    _sweepIndex = 0;
                    if (++_sweepList < 2) continue;
                    _sweepList = 0;
                    if (++_passes == 2) _gi.PassFinished(epoch);
                    break;   // (a pass a frame at most)
                }
                checks--;
                Renderable r;
                Renderer renderer;
                int blocks;
                if (_sweepList == 0)
                {
                    var m = meshes[_sweepIndex];
                    r = m; renderer = m?.Renderer; blocks = m != null ? m.LastPropertyBlockCount : 0;
                }
                else
                {
                    var s = skinned[_sweepIndex];
                    r = s; renderer = s?.Renderer; blocks = s != null ? s.LastPropertyBlockCount : 0;
                }
                _sweepIndex++;
                if (r == null || r.SoftbodyGIEpoch == epoch) continue;
                r.SoftbodyGIEpoch = epoch;
                if (renderer == null) continue;
                if (_hasHead) _gi.ApplyAt(renderer, blocks, renderer.bounds, _head);
                else _gi.Apply(renderer, blocks, _gi.Covers(renderer.bounds));
                applies--;
                _applied++;
            }
        }

        static void Stats()
        {
            if (!(_statsEvery > 0f)) return;
            _statsTimer -= Time.unscaledDeltaTime;
            if (_statsTimer > 0f) return;
            _statsTimer = _statsEvery;
            var g = _gi.GpuStats;
            var o = _gi.ShownOrigin;
            Debug.Log(Tag + $"update {_updateMs:0.00} ms; end of frame {_gi.MainMs:0.00} ms (slab {_gi.VoxelizeMs:0.00}), render thread {_gi.RenderThreadMs:0.00} ms, "
                      + $"GPU {(g != null ? g.GpuMs : 0):0.00} ms over {(g != null ? g.Frames : 0)} frames; lights {_gi.LightsUsed} of {_gi.LightsSeen}; "
                      + $"renderers {_space?.Meshes.RenderableCount ?? 0} + {_space?.SkinnedMeshes.RenderableCount ?? 0} skinned, {_applied} set since the last line, "
                      + $"{_gi.ThinCount} thin ({_gi.Turns} turned so far); glow rays {_gi.GlowRays}, leak offset {_gi.LeakOffset:0.##} m; "
                      + $"{(_gi.On ? "on" : "off")}, volume {_gi.Shown} at ({o.X:0.#}, {o.Y:0.#}, {o.Z:0.#}){(_gi.Migrating ? ", moving renderers over" : "")}");
            _applied = 0;
        }
    }
}
