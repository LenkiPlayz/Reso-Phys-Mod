// A standalone player (not the game) that times the voxel camera's draw at different places in Unity's frame. In the game (GI test 1,
// 2026-11-14) the slab draw, run from RenderingManager.Update, held the main thread ~5.5 ms a frame while that thread was mostly
// idle, yet in the editor (GiSyncProbe) none of its calls waits for the render thread. The guess: the first camera draw of a frame
// waits for Unity's frame pacing (the previous frame reaching the screen), which normally happens later, when the frame renders.
// Each variant runs a few seconds with vsync on; the results go to pacing.txt next to the player. C# 7.3.
// The last three copy the renderer's frame (RenderingManager.HandleUpdate): Update tells an "engine" thread to start its frame (4 ms
// of work), then does "asset integration" only while it waits for that frame (as RunAssetIntegration does); the loading time a frame
// gets is the measure (opening a panel is mostly that integration). GI test 1 drew in Update before it; GI test 2 at the end.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

public class GiPacingProbe : MonoBehaviour
{
    public Shader voxelize;
    public Material white;

    const float Warmup = 3f, PerVariant = 4f;
    static readonly string[] Names =
    {
        "nothing drawn (the frame alone)",
        "Update: slab draw (as GI test 1)",
        "Update: a plugin event only (as the fluid and the asset uploads)",
        "LateUpdate: slab draw",
        "main camera's onPreCull: slab draw",
        "main camera's onPostRender: slab draw",
        "end of frame (WaitForEndOfFrame): slab draw",
        "Resonite-like frame, no GI",
        "Resonite-like frame, slab draw in Update before the loading (GI test 1)",
        "Resonite-like frame, slab draw at the end of the frame (GI test 2)",
    };
    const int FirstResoniteLike = 7;
    const double EngineMs = 4.0;

    // the "engine": starts a frame when Update says so, is done EngineMs later
    readonly AutoResetEvent _beginFrame = new AutoResetEvent(false);
    volatile bool _frameReady, _engineRunning = true;
    Thread _engine;
    readonly List<double> _loading = new List<double>();

    Camera _main, _vox;
    RenderTexture _rt;
    ComputeBuffer _buf;
    SkinnedMeshRenderer _skinned;
    int _variant = -1;
    float _t0;
    bool _drewThisFrame;
    readonly List<double> _draw = new List<double>(), _frame = new List<double>();
    readonly Stopwatch _frameWatch = new Stopwatch();
    StreamWriter _out;

    delegate void RenderEvent(int id);
    static readonly RenderEvent s_event = OnRenderEvent;
    static IntPtr s_pointer;
    [AOT.MonoPInvokeCallback(typeof(RenderEvent))] static void OnRenderEvent(int id) { }

    void Start()
    {
        QualitySettings.vSyncCount = 1;
        Application.targetFrameRate = -1;
        s_pointer = Marshal.GetFunctionPointerForDelegate(s_event);

        // a small world: a floor, 300 boxes, one skinned box, a sun with soft shadows, a camera on the screen
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.transform.position = new Vector3(0f, -0.1f, 8f);
        floor.transform.localScale = new Vector3(30f, 0.2f, 30f);
        floor.GetComponent<Renderer>().sharedMaterial = white;
        var rnd = new System.Random(1);
        for (int i = 0; i < 300; i++)
        {
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            b.transform.position = new Vector3((float)rnd.NextDouble() * 24f - 12f, 0.3f + (float)rnd.NextDouble() * 2f, (float)rnd.NextDouble() * 20f);
            b.transform.localScale = Vector3.one * 0.5f;
            b.GetComponent<Renderer>().sharedMaterial = white;
        }
        _skinned = MakeSkinned();
        var sun = new GameObject("sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.shadows = LightShadows.Soft;
        sun.transform.rotation = Quaternion.Euler(50f, 30f, 0f);
        QualitySettings.shadows = ShadowQuality.All;
        _main = new GameObject("main camera").AddComponent<Camera>();
        _main.transform.SetPositionAndRotation(new Vector3(0f, 3f, -6f), Quaternion.Euler(15f, 0f, 0f));

        // the voxel camera and its slab buffer, as GiUnity makes them
        _vox = new GameObject("voxel camera").AddComponent<Camera>();
        _vox.enabled = false;
        _vox.stereoTargetEye = StereoTargetEyeMask.None;
        _vox.orthographic = true;
        _vox.clearFlags = CameraClearFlags.Nothing;
        _vox.renderingPath = RenderingPath.Forward;
        _vox.allowHDR = false;
        _vox.allowMSAA = false;
        _vox.allowDynamicResolution = false;
        _vox.useOcclusionCulling = false;
        _vox.depthTextureMode = DepthTextureMode.None;
        _rt = new RenderTexture(256, 256, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear) { antiAliasing = 1 };
        _rt.Create();
        _vox.targetTexture = _rt;
        _vox.transform.SetPositionAndRotation(new Vector3(0f, 1.5f, 4f), Quaternion.identity);
        _vox.orthographicSize = 6.25f;
        _vox.aspect = 32.5f / 12.5f;
        _vox.nearClipPlane = 0.01f;
        _vox.farClipPlane = 2.5f;
        _buf = new ComputeBuffer(128 * 48 * 8 * 10, 4, ComputeBufferType.Structured);

        _out = new StreamWriter(Path.Combine(Application.dataPath, "..", "pacing.txt"));
        _out.WriteLine($"{SystemInfo.graphicsDeviceType}, multithreaded rendering {SystemInfo.graphicsMultiThreaded}, vsync {QualitySettings.vSyncCount}, "
                       + $"screen {Screen.currentResolution.refreshRate} Hz, Unity {Application.unityVersion}; each variant {PerVariant} s");
        _out.WriteLine("variant: slab draw ms (median / mean / 95th / max) | frame ms (median / mean / 95th)");
        _out.Flush();

        Camera.onPreCull += PreCull;
        Camera.onPostRender += PostRender;
        StartCoroutine(EndOfFrame());
        _engine = new Thread(Engine) { IsBackground = true, Name = "pacing engine" };
        _engine.Start();
    }

    void OnDestroy() { _engineRunning = false; _beginFrame.Set(); }

    /// <summary>The engine's frame: busy for EngineMs once Update starts it (a CPU-bound engine).</summary>
    void Engine()
    {
        while (_engineRunning)
        {
            if (!_beginFrame.WaitOne(200)) continue;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < EngineMs) { }
            _frameReady = true;
        }
    }

    /// <summary>RenderingManager.HandleUpdate in short: BeginFrame to the engine, the hook (GI test 1 drew here), then the asset
    /// integration, which works only until the engine's frame is there.</summary>
    void ResoniteLikeUpdate()
    {
        _frameReady = false;
        _beginFrame.Set();
        if (_variant == FirstResoniteLike + 1) Draw();
        var sw = Stopwatch.StartNew();
        while (!_frameReady && sw.ElapsedMilliseconds < 100)
        {
            var task = Stopwatch.StartNew();   // one integration task: 0.1 ms of work
            while (task.Elapsed.TotalMilliseconds < 0.1) { }
        }
        _loading.Add(sw.Elapsed.TotalMilliseconds);
    }

    void Update()
    {
        if (_frameWatch.IsRunning && _variant >= 0) _frame.Add(_frameWatch.Elapsed.TotalMilliseconds);
        _frameWatch.Restart();
        float t = Time.realtimeSinceStartup;
        if (_variant < 0) { if (t > Warmup) Next(t); return; }
        if (t - _t0 > PerVariant) { Next(t); if (_variant >= Names.Length) return; }
        _drewThisFrame = false;
        if (_variant == 1) Draw();
        if (_variant == 2)
        {
            var sw = Stopwatch.StartNew();
            GL.IssuePluginEvent(s_pointer, 0);
            _draw.Add(sw.Elapsed.TotalMilliseconds);
        }
        if (_variant >= FirstResoniteLike) ResoniteLikeUpdate();
    }

    void LateUpdate() { if (_variant == 3) Draw(); }
    void PreCull(Camera c) { if (_variant == 4 && c == _main && !_drewThisFrame) Draw(); }
    void PostRender(Camera c) { if (_variant == 5 && c == _main && !_drewThisFrame) Draw(); }

    IEnumerator EndOfFrame()
    {
        var wait = new WaitForEndOfFrame();
        while (true)
        {
            yield return wait;
            if ((_variant == 6 || _variant == FirstResoniteLike + 2) && !_drewThisFrame) Draw();
        }
    }

    /// <summary>GiUnity.RenderSlab's calls, in its order.</summary>
    void Draw()
    {
        _drewThisFrame = true;
        var sw = Stopwatch.StartNew();
        var shadows = QualitySettings.shadows;
        QualitySettings.shadows = ShadowQuality.Disable;
        _skinned.forceRenderingOff = true;
        Graphics.SetRandomWriteTarget(1, _buf, false);
        _vox.RenderWithShader(voxelize, "RenderType");
        Graphics.ClearRandomWriteTargets();
        _skinned.forceRenderingOff = false;
        QualitySettings.shadows = shadows;
        _draw.Add(sw.Elapsed.TotalMilliseconds);
    }

    void Next(float t)
    {
        if (_variant >= 0)
        {
            if (_variant == FirstResoniteLike)
                _out.WriteLine($"Resonite-like: slab draw ms | frame ms | loading ms a frame (median / mean / 95th / max); engine {EngineMs} ms a frame");
            _out.WriteLine($"{Names[_variant]}: {Stats(_draw)} | {Stats(_frame)}" + (_variant >= FirstResoniteLike ? " | " + Stats(_loading) : ""));
            _out.Flush();
        }
        _variant++;
        _t0 = t;
        _draw.Clear();
        _frame.Clear();
        _loading.Clear();
        if (_variant >= Names.Length)
        {
            _out.WriteLine("DONE");
            _out.Close();
            Application.Quit();
        }
    }

    static string Stats(List<double> v)
    {
        if (v.Count == 0) return "-";
        var s = new List<double>(v);
        s.Sort();
        double mean = 0;
        foreach (var x in s) mean += x;
        mean /= s.Count;
        return $"{s[s.Count / 2]:0.00} / {mean:0.00} / {s[(int)(s.Count * 0.95)]:0.00} / {s[s.Count - 1]:0.00} ({s.Count})";
    }

    SkinnedMeshRenderer MakeSkinned()
    {
        var src = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var mesh = Instantiate(src.GetComponent<MeshFilter>().sharedMesh);
        Destroy(src);
        var go = new GameObject("skinned box");
        go.transform.position = new Vector3(1.5f, 0.5f, 3f);
        var bone = new GameObject("bone").transform;
        bone.SetParent(go.transform, false);
        var weights = new BoneWeight[mesh.vertexCount];
        for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
        mesh.boneWeights = weights;
        mesh.bindposes = new[] { bone.worldToLocalMatrix * go.transform.localToWorldMatrix };
        var smr = go.AddComponent<SkinnedMeshRenderer>();
        smr.bones = new[] { bone };
        smr.sharedMesh = mesh;
        smr.sharedMaterial = white;
        return smr;
    }
}
