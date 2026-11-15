// The live bounce light's (GI) editor jobs, run by build_gi.ps1 in the Unity 2019.4.31 editor (windowed: batch mode is not licensed
// on this PC). C# 7.3.
//   Build: the voxelize shader's bundle (Softbody/GpuGI/Shaders/softbody_gi.bundle), built as the smoke's (VR with single-pass
//          stereo, linear colour, as the game's renderer project).
//   Check: the fork's own self-test with a bundle (after fix_d3d11_programs.py rewrote its programs): GiUnity.StartSelfTest.
//   Scene: a Cornell box drawn with the game's own PBS shader, before and after the bounce light, in both modes (Globals,
//          PerRenderer), with timings; and a check that GiUnity.PackAmbient packs the world's ambient as Unity uploads it.
//   Leak:  a thin wall lit only by a known ramp written into the volume: its lookup offset (GiLeak, a translation in
//          unity_ProbeVolumeWorldToObject) must move what the game's own shader reads, and FollowHead must turn it to the head.
// Logs SOFTBODY_GI_* lines. The GPU side runs on the editor's own Direct3D 11 device, on the editor's render thread (the editor
// renders on a thread of its own, "threaded=1" in its log), reached as the game reaches its own: a plugin event that calls back
// into C# (EditorRenderThread). Running it on the main thread instead raced Unity's render thread on one device context and
// crashed the driver (2026-11-14).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Softbody.GI;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using NVector4 = System.Numerics.Vector4;

public static class GiShaderBuild
{
    const string ShaderPath = "Assets/SoftbodyGI/Shaders/SoftbodyGIVoxelize.shader";
    const string SHCheckPath = "Assets/SoftbodyGI/Shaders/GiSHCheck.shader";
    const string VoxelizeName = "Hidden/Softbody/GIVoxelize";
    public const string BundleName = "softbody_gi.bundle";
    const int TestLayer = 31, CheckLayer = 30;

    static string Arg(string name, string fallback = null)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < a.Length; i++) if (a[i] == name) return a[i + 1];
        return fallback;
    }

    static string Need(string name)
    {
        string v = Arg(name);
        if (string.IsNullOrEmpty(v)) throw new ArgumentException(name + " is missing");
        return v;
    }

    // ------------------------------------------------------------------ the bundle

    public static void Build()
    {
        try
        {
            string outDir = Need("-giOut");
            Directory.CreateDirectory(outDir);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
#pragma warning disable 618
            PlayerSettings.virtualRealitySupported = true;
#pragma warning restore 618
            PlayerSettings.stereoRenderingPath = StereoRenderingPath.SinglePass;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null) throw new InvalidOperationException("no shader at " + ShaderPath);
            var messages = ShaderUtil.GetShaderMessages(shader);
            foreach (var m in messages) Debug.Log("SOFTBODY_GI_SHADER_MESSAGE " + m.severity + " " + m.platform + " line " + m.line + ": " + m.message);
            if (messages.Any(m => m.severity == ShaderCompilerMessageSeverity.Error)) throw new InvalidOperationException("the voxelize shader has errors");
            var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = new[] { ShaderPath } };
            var manifest = BuildPipeline.BuildAssetBundles(outDir, new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.DeterministicAssetBundle, BuildTarget.StandaloneWindows64);
            string file = Path.Combine(outDir, BundleName);
            if (manifest == null || !File.Exists(file)) throw new InvalidOperationException("the bundle was not built");
            Debug.Log("SOFTBODY_GI_BUILD OK " + file + " " + new FileInfo(file).Length + " bytes");
        }
        catch (Exception ex)
        {
            Debug.LogError("SOFTBODY_GI_BUILD FAILED " + ex);
            EditorApplication.Exit(1);
        }
    }

    // ------------------------------------------------------------------ the fork's self-test

    public static void Check()
    {
        AssetBundle bundle = null;
        GiUnity gi = null;
        SharpDX.Direct3D11.Device device = null;
        try
        {
            string path = Need("-giBundle");
            var bytecode = GiCompute.CompileAll(File.ReadAllText(Need("-giHlsl")));
            bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null) { Debug.Log("SOFTBODY_GI_CHECK FAIL " + path + " (the bundle did not load)"); Debug.Log("SOFTBODY_GI_CHECK DONE"); return; }
            var shader = bundle.LoadAllAssets<Shader>().FirstOrDefault(s => s != null && s.name == VoxelizeName);
            if (shader == null || !shader.isSupported)
            {
                Debug.Log("SOFTBODY_GI_CHECK FAIL " + path + " (" + (shader == null ? "no " + VoxelizeName : "not supported") + ")");
                Debug.Log("SOFTBODY_GI_CHECK DONE");
                return;
            }
            device = EditorDevice();
            var dev = device;
            gi = new GiUnity(new GiConfig(), shader, bytecode, () => dev, EditorRenderThread.Enqueue);
            gi.StartSelfTest(TestLayer);
            gi.WaitSelfTest();
            string result;
            bool? ok = gi.TestResult(out result);
            Debug.Log("SOFTBODY_GI_CHECK " + (ok == true ? "PASS" : "FAIL") + " " + path + " (" + (result ?? "no result") + ")");
            Debug.Log("SOFTBODY_GI_CHECK DONE");
        }
        catch (Exception ex)
        {
            Debug.LogError("SOFTBODY_GI_CHECK FAILED " + ex);
            EditorApplication.Exit(1);
        }
        finally
        {
            if (gi != null) gi.Dispose();
            EditorRenderThread.Sync();   // the render thread lets its half go before the device wrapper does
            EditorRenderThread.Release();
            GiShaderWatch.Stop();
            if (device != null) device.Dispose();
            if (bundle != null) bundle.Unload(true);
        }
    }

    /// <summary>The editor's own Direct3D 11 device (a wrapper with its own reference), through a texture's native pointer.</summary>
    static SharpDX.Direct3D11.Device EditorDevice()
    {
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) throw new InvalidOperationException("the editor draws with " + SystemInfo.graphicsDeviceType + ", not Direct3D 11");
        var t = new Texture2D(4, 4);
        t.Apply();
        IntPtr p = t.GetNativeTexturePtr();
        Marshal.AddRef(p);
        try
        {
            using (var tex = new SharpDX.Direct3D11.Texture2D(p)) return tex.Device;
        }
        finally { UnityEngine.Object.DestroyImmediate(t); }
    }

    // ------------------------------------------------------------------ the scene

    const int Width = 512, Height = 384, Frames = 360, TimedFrames = 120;

    public static void Scene()
    {
        var made = new List<UnityEngine.Object>();
        AssetBundle bundle = null, game = null;
        SharpDX.Direct3D11.Device device = null;
        try
        {
            string outDir = Need("-giOut");
            Directory.CreateDirectory(outDir);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var bytecode = GiCompute.CompileAll(File.ReadAllText(Need("-giHlsl")));
            bundle = AssetBundle.LoadFromFile(Need("-giBundle"));
            if (bundle == null) throw new InvalidOperationException("the GI bundle did not load");
            var voxelize = bundle.LoadAllAssets<Shader>().First(s => s != null && s.name == VoxelizeName);
            game = AssetBundle.LoadFromFile(Need("-giGameShader"));
            if (game == null) throw new InvalidOperationException("the game's shader bundle did not load");
            var pbs = game.LoadAllAssets<Shader>()[0];
            Debug.Log("SOFTBODY_GI_SCENE game shader " + pbs.name + " (supported " + pbs.isSupported + "); its properties: " + Properties(pbs));
            Debug.Log("SOFTBODY_GI_SCENE colour space " + QualitySettings.activeColorSpace + ", lights use linear intensity " + GraphicsSettings.lightsUseLinearIntensity
                      + ", device " + SystemInfo.graphicsDeviceType + " " + SystemInfo.graphicsDeviceName);
            device = EditorDevice();
            var dev = device;

            // the world's ambient: a dim blue sky, brighter from above, a little warm from one side (every band in use)
            var sky = new SphericalHarmonicsL2();
            sky.AddAmbientLight(new Color(0.035f, 0.045f, 0.07f));
            sky.AddDirectionalLight(new Vector3(0.2f, 1f, 0.1f).normalized, new Color(0.08f, 0.10f, 0.16f), 1f);
            sky.AddDirectionalLight(new Vector3(1f, 0.1f, -0.3f).normalized, new Color(0.05f, 0.03f, 0.01f), 1f);
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = sky;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.reflectionIntensity = 0f;
            RenderSettings.fog = false;

            CheckAmbient(sky, made);

            var renderers = BuildBox(pbs, made);
            var light = new GameObject("gi test light");
            made.Add(light);
            var l = light.AddComponent<Light>();
            l.type = LightType.Point;
            l.transform.position = new Vector3(0f, 2.65f, 2.2f);
            l.range = 7f;
            l.intensity = 1.6f;
            l.color = new Color(1f, 0.92f, 0.8f);
            l.shadows = LightShadows.Soft;
            var lights = new List<Light> { l };

            var camGo = new GameObject("gi test camera");
            made.Add(camGo);
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.transform.SetPositionAndRotation(new Vector3(0f, 1.5f, -3.6f), Quaternion.identity);
            cam.fieldOfView = 52f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.renderingPath = RenderingPath.Forward;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.cullingMask = ~((1 << TestLayer) | (1 << CheckLayer));
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            made.Add(rt);
            rt.Create();
            cam.targetTexture = rt;
            cam.aspect = (float)Width / Height;

            var before = Shot(cam, rt, made);
            Save(before, Path.Combine(outDir, "before.png"));
            Debug.Log("SOFTBODY_GI_SCENE before: " + Regions(before));

            // (one run: the volume goes in each renderer's block; the files keep the name they had when shader globals were tried too)
            foreach (var mode in new[] { "PerRenderer" })
            {
                GiUnity gi = null;
                try
                {
                    gi = new GiUnity(new GiConfig(), voxelize, bytecode, () => dev, EditorRenderThread.Enqueue);
                    gi.StartSelfTest(TestLayer);
                    gi.WaitSelfTest();
                    string test;
                    if (gi.TestResult(out test) != true) { Debug.Log("SOFTBODY_GI_SCENE " + mode + " FAIL self-test: " + test); continue; }
                    var stamp = new Dictionary<Renderer, int>();
                    double mainMs = 0, slabMs = 0, gpuThreadMs = 0;
                    var watch = new System.Diagnostics.Stopwatch();
                    int mask = ~((1 << TestLayer) | (1 << CheckLayer));
                    for (int f = 0; f < Frames; f++)
                    {
                        bool timed = f >= Frames - TimedFrames;
                        double v0 = gi.VoxelizeMs;
                        watch.Restart();
                        gi.Tick(cam.transform.position, mask, lights, null);
                        watch.Stop();
                        EditorRenderThread.Sync();   // as by the game's next frame: the render thread has run the job
                        if (timed) { mainMs += watch.Elapsed.TotalMilliseconds; gpuThreadMs += gi.RenderThreadMs; }
                        // the host's sweep, all at once (the thin walls read the volume in front of themselves, toward the head)
                        int epoch = gi.Epoch;
                        var head = cam.transform.position;
                        foreach (var r in renderers)
                        {
                            int e;
                            if (stamp.TryGetValue(r, out e) && e == epoch) continue;
                            gi.ApplyAt(r, 0, r.bounds, head);
                            stamp[r] = epoch;
                        }
                        gi.PassFinished(epoch);
                        gi.FollowHead(head);
                        if (timed) slabMs += gi.VoxelizeMs;
                    }
                    GL.Flush();
                    var g = gi.GpuStats;
                    Debug.Log("SOFTBODY_GI_SCENE " + mode + " timings over the last " + TimedFrames + " frames: main thread (Tick) " + (mainMs / TimedFrames).ToString("0.000")
                              + " ms a frame, of it the slab draw ~" + (slabMs / TimedFrames).ToString("0.000")
                              + " ms; render thread ~" + gi.RenderThreadMs.ToString("0.000") + " ms; GPU " + (g != null ? g.GpuMs.ToString("0.000") : "?")
                              + " ms (last " + (g != null ? g.LastGpuMs.ToString("0.000") : "?") + ", " + (g != null ? g.Frames : 0) + " frames timed); lights "
                              + gi.LightsUsed + " of " + gi.LightsSeen + "; on " + gi.On + ", volume " + gi.Shown
                              + "; thin renderers " + gi.ThinCount + ", glow rays " + gi.GlowRays);
                    // what the GPU side holds at a few places (read on the render thread)
                    uint[] vox = null;
                    NVector4[] probes = null;
                    gi.OnRenderThread(gc => { vox = gc.ReadVoxels(); probes = gc.ReadProbes(); });
                    EditorRenderThread.Sync();
                    if (vox != null && probes != null) foreach (var line in Inside(gi, vox, probes, sky)) Debug.Log("SOFTBODY_GI_SCENE " + mode + " " + line);
                    if (gi.Failure != null) Debug.Log("SOFTBODY_GI_SCENE " + mode + " FAIL the GPU side: " + gi.Failure);
                    if (EditorRenderThread.Failure != null) Debug.Log("SOFTBODY_GI_SCENE " + mode + " FAIL on the render thread: " + EditorRenderThread.Failure);
                    var after = Shot(cam, rt, made);
                    Save(after, Path.Combine(outDir, "after_" + mode + ".png"));
                    SaveSideBySide(before, after, Path.Combine(outDir, "compare_" + mode + ".png"), made);
                    Debug.Log("SOFTBODY_GI_SCENE " + mode + " after: " + Regions(after));
                    Debug.Log("SOFTBODY_GI_SCENE " + mode + " change from before: " + Difference(before, after));
                    var cornerOn = CornerShot(cam, rt, made);

                    // off again: back to the plain ambient (the sweep with the off epoch)
                    gi.SetWanted(false);
                    int offEpoch = gi.Epoch;
                    foreach (var r in renderers) { gi.ApplyAt(r, 0, r.bounds, cam.transform.position); stamp[r] = offEpoch; }
                    var off = Shot(cam, rt, made);
                    Debug.Log("SOFTBODY_GI_SCENE " + mode + " off again, change from before: " + Difference(before, off));
                    var cornerOff = CornerShot(cam, rt, made);
                    SaveSideBySide(cornerOff, cornerOn, Path.Combine(outDir, "compare_" + mode + "_corner.png"), made);
                    Debug.Log("SOFTBODY_GI_SCENE " + mode + " corner view (the glowing cube): change from plain " + Difference(cornerOff, cornerOn));
                }
                catch (Exception ex)
                {
                    Debug.Log("SOFTBODY_GI_SCENE " + mode + " FAIL " + ex);
                }
                finally
                {
                    if (gi != null) gi.Dispose();
                    EditorRenderThread.Sync();
                    foreach (var r in renderers) if (r != null) r.SetPropertyBlock(null);
                }
            }
            Debug.Log("SOFTBODY_GI_SCENE DONE");
        }
        catch (Exception ex)
        {
            Debug.LogError("SOFTBODY_GI_SCENE FAILED " + ex);
            EditorApplication.Exit(1);
        }
        finally
        {
            GiShaderWatch.Stop();
            EditorRenderThread.Sync();
            EditorRenderThread.Release();
            foreach (var o in made) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            if (device != null) device.Dispose();
            if (bundle != null) bundle.Unload(true);
            if (game != null) game.Unload(true);
        }
    }

    // ------------------------------------------------------------------ the lookup offset (GiLeak) through the game's shader

    /// <summary>
    /// Whether a thin renderer's lookup offset (GiLeak: a translation in unity_ProbeVolumeWorldToObject, in its block) moves what
    /// the game's own shader reads. A wall 0.2 x 2 x 2 m, no lights, a black sky. Once the bounce light is on, its volume is
    /// overwritten with a ramp along x (a0 only, grey: no direction, so only where the lookup lands matters) and nothing ticks
    /// again. The wall's faces are drawn with leak_offset 0 (each reads at itself), with 0.5 m and the head on the +x side (it
    /// reads 0.5 m further along +x) and, once FollowHead turned it to a head on the -x side, from there (0.5 m along -x). What
    /// each face shows must follow the ramp.
    /// </summary>
    public static void Leak()
    {
        var made = new List<UnityEngine.Object>();
        AssetBundle bundle = null, game = null;
        SharpDX.Direct3D11.Device device = null;
        GiUnity gi = null;
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var bytecode = GiCompute.CompileAll(File.ReadAllText(Need("-giHlsl")));
            bundle = AssetBundle.LoadFromFile(Need("-giBundle"));
            if (bundle == null) throw new InvalidOperationException("the GI bundle did not load");
            var voxelize = bundle.LoadAllAssets<Shader>().First(s => s != null && s.name == VoxelizeName);
            game = AssetBundle.LoadFromFile(Need("-giGameShader"));
            if (game == null) throw new InvalidOperationException("the game's shader bundle did not load");
            var pbs = game.LoadAllAssets<Shader>()[0];
            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.Copy3D) == 0) throw new InvalidOperationException("this device cannot copy 3D textures");
            device = EditorDevice();
            var dev = device;

            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = new SphericalHarmonicsL2();   // black
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.reflectionIntensity = 0f;
            RenderSettings.fog = false;

            var parts = new List<Renderer>();
            Part("thin wall", new Vector3(0.3f, 1.2f, 0.2f), new Vector3(0.2f, 2f, 2f), 0f, new Color(0.75f, 0.75f, 0.75f), Color.black, pbs, made, parts);
            var wall = parts[0];
            var bounds = wall.bounds;
            // the heads, and the camera: 1.7 m in front of each face, looking at its middle
            var plus = new Vector3(bounds.max.x + 1.7f, bounds.center.y, bounds.center.z);
            var minus = new Vector3(bounds.min.x - 1.7f, bounds.center.y, bounds.center.z);

            var camGo = new GameObject("gi leak camera");
            made.Add(camGo);
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 20f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.renderingPath = RenderingPath.Forward;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            int mask = ~((1 << TestLayer) | (1 << CheckLayer));
            cam.cullingMask = mask;
            var rt = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            made.Add(rt);
            rt.Create();
            cam.targetTexture = rt;
            cam.aspect = 1f;

            gi = new GiUnity(new GiConfig(), voxelize, bytecode, () => dev, EditorRenderThread.Enqueue);
            gi.StartSelfTest(TestLayer);
            gi.WaitSelfTest();
            string test;
            if (gi.TestResult(out test) != true) throw new InvalidOperationException("the self-test failed: " + test);
            var noLights = new List<Light>();
            for (int f = 0; f < 60 && !gi.On; f++) { gi.Tick(plus, mask, noLights, null); EditorRenderThread.Sync(); }
            if (gi.Failure != null) throw new InvalidOperationException("the GPU side failed: " + gi.Failure);
            if (EditorRenderThread.Failure != null) throw new InvalidOperationException("the render thread failed: " + EditorRenderThread.Failure);
            if (!gi.On) throw new InvalidOperationException("the bounce light did not come on in 60 frames");

            // the ramp, into the volume the renderers read (from here on nothing ticks: it stays)
            var c = gi.Config;
            float ps = c.ProbeSpacing, offset = c.LeakOffset;
            var o = gi.ShownOrigin;
            Func<float, float> index = x => (x - o.X) / ps - 0.5f;   // a world x as the lookup's probe index along x
            float xPlus = bounds.max.x, xMinus = bounds.min.x;
            int start = Mathf.FloorToInt(index(xMinus - offset));      // the ramp rises from here: every lookup below lands on its slope
            const float Base = 0.02f, Slope = 0.2f;
            Func<float, float> ramp = i => Base + Slope * Math.Max(0f, i - start);
            int W = c.ProbeX, H = c.ProbeY, D = c.ProbeZ;
            var tex = new Texture3D(4 * W, H, D, TextureFormat.RGBAHalf, false);
            made.Add(tex);
            var px = new Color[4 * W * H * D];
            for (int z = 0; z < D; z++)
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < 4 * W; x++)
                        px[(z * H + y) * 4 * W + x] = x < 3 * W ? new Color(0f, 0f, 0f, ramp(x % W)) : Color.white;
            tex.SetPixels(px);
            tex.Apply(false);
            int probeX = start + 2, probeY = H / 2, probeZ = D / 2;
            float setTo = tex.GetPixels()[(probeZ * H + probeY) * 4 * W + W + probeX].a;
            Graphics.CopyTexture(tex, gi.Volume(gi.Shown));
            var check = AsyncGPUReadback.Request(gi.Volume(gi.Shown), 0, W + probeX, 1, probeY, 1, probeZ, 1, TextureFormat.RGBAHalf);
            check.WaitForCompletion();
            float copied = check.hasError ? float.NaN : Mathf.HalfToFloat(check.GetData<ushort>()[3]);
            Debug.Log("SOFTBODY_GI_LEAK the ramp: a0 " + Base + " + " + Slope + " a probe from probe x " + start + " (the window's corner at x "
                      + o.X.ToString("0.00") + "); probe x " + probeX + " should hold " + ramp(probeX).ToString("0.000") + ": the texture has "
                      + setTo.ToString("0.000") + ", the volume " + copied.ToString("0.000"));
            if (!(Math.Abs(copied - ramp(probeX)) < 0.01f)) throw new InvalidOperationException("the ramp did not reach the volume");

            Func<Vector3, float> shoot = eye =>
            {
                cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(new Vector3(bounds.center.x, eye.y, eye.z) - eye));
                var t = Shot(cam, rt, made);
                var m = Mean(t, new Rect(0.35f, 0.35f, 0.3f, 0.3f));
                return Mathf.GammaToLinearSpace((m.x + m.y + m.z) / (3f * 255f));
            };

            // 1. leak_offset 0: each face reads at itself
            gi.LeakOffset = 0f;
            gi.ApplyAt(wall, 0, bounds, plus);
            float plainPlus = shoot(plus), plainMinus = shoot(minus);
            // 2. 0.5 m, the head on the +x side: the wall reads 0.5 m along +x
            gi.LeakOffset = offset;
            gi.ApplyAt(wall, 0, bounds, plus);
            gi.FollowHead(plus);
            int thin = gi.ThinCount, turnsBefore = gi.Turns;
            float movedPlus = shoot(plus);
            // 3. the head goes round to the -x side: FollowHead turns the wall, which now reads 0.5 m along -x
            gi.FollowHead(minus);
            int turned = gi.Turns - turnsBefore;
            float movedMinus = shoot(minus);

            float ePlainPlus = ramp(index(xPlus)), ePlainMinus = ramp(index(xMinus));
            float eMovedPlus = ramp(index(xPlus + offset)), eMovedMinus = ramp(index(xMinus - offset));
            float kPlus = plainPlus / ePlainPlus, kMinus = plainMinus / ePlainMinus;   // the shader's own scale (albedo, its BRDF)
            float rPlus = movedPlus / plainPlus, rMinus = movedMinus / plainMinus;
            float wantPlus = eMovedPlus / ePlainPlus, wantMinus = eMovedMinus / ePlainMinus;
            Debug.Log("SOFTBODY_GI_LEAK the +x face (x " + xPlus.ToString("0.00") + "): plain " + plainPlus.ToString("0.0000") + ", read 0.5 m in front "
                      + movedPlus.ToString("0.0000") + " (x " + rPlus.ToString("0.000") + "; the ramp's x " + wantPlus.ToString("0.000") + ")");
            Debug.Log("SOFTBODY_GI_LEAK the -x face (x " + xMinus.ToString("0.00") + "): plain " + plainMinus.ToString("0.0000") + ", read 0.5 m in front "
                      + movedMinus.ToString("0.0000") + " (x " + rMinus.ToString("0.000") + "; the ramp's x " + wantMinus.ToString("0.000") + ")");
            Debug.Log("SOFTBODY_GI_LEAK the shader's scale on the two faces " + kPlus.ToString("0.000") + " and " + kMinus.ToString("0.000")
                      + "; thin renderers " + thin + ", turned " + turned);
            bool ok = Math.Abs(rPlus / wantPlus - 1f) < 0.08f && Math.Abs(rMinus / wantMinus - 1f) < 0.08f && Math.Abs(kPlus / kMinus - 1f) < 0.08f
                      && thin == 1 && turned == 1;
            Debug.Log("SOFTBODY_GI_LEAK " + (ok ? "PASS the lookup offset moves what the game's shader reads, and turns with the head"
                                                : "FAIL the lookup offset does not move what the game's shader reads as it should"));
            Debug.Log("SOFTBODY_GI_LEAK DONE");
        }
        catch (Exception ex)
        {
            Debug.LogError("SOFTBODY_GI_LEAK FAILED " + ex);
            EditorApplication.Exit(1);
        }
        finally
        {
            if (gi != null) gi.Dispose();
            EditorRenderThread.Sync();
            EditorRenderThread.Release();
            GiShaderWatch.Stop();
            foreach (var x in made) if (x != null) UnityEngine.Object.DestroyImmediate(x);
            if (device != null) device.Dispose();
            if (bundle != null) bundle.Unload(true);
            if (game != null) game.Unload(true);
        }
    }

    static string Properties(Shader s)
    {
        var names = new List<string>();
        for (int i = 0; i < ShaderUtil.GetPropertyCount(s); i++) names.Add(ShaderUtil.GetPropertyName(s, i));
        return string.Join(" ", names);
    }

    /// <summary>A 4 x 3 x 4 m box open towards the camera: white floor, ceiling and back, red left wall, green right wall, two white
    /// blocks, and a small glowing orange cube.</summary>
    static List<Renderer> BuildBox(Shader pbs, List<UnityEngine.Object> made)
    {
        var list = new List<Renderer>();
        var white = new Color(0.75f, 0.75f, 0.75f);
        Part("floor", new Vector3(0f, -0.1f, 2f), new Vector3(4.4f, 0.2f, 4.4f), 0f, white, Color.black, pbs, made, list);
        Part("ceiling", new Vector3(0f, 3.1f, 2f), new Vector3(4.4f, 0.2f, 4.4f), 0f, white, Color.black, pbs, made, list);
        Part("back", new Vector3(0f, 1.5f, 4.1f), new Vector3(4.4f, 3.4f, 0.2f), 0f, white, Color.black, pbs, made, list);
        Part("left", new Vector3(-2.1f, 1.5f, 2f), new Vector3(0.2f, 3.4f, 4.4f), 0f, new Color(0.75f, 0.08f, 0.06f), Color.black, pbs, made, list);
        Part("right", new Vector3(2.1f, 1.5f, 2f), new Vector3(0.2f, 3.4f, 4.4f), 0f, new Color(0.08f, 0.6f, 0.1f), Color.black, pbs, made, list);
        Part("tall block", new Vector3(-0.75f, 0.9f, 2.7f), new Vector3(1f, 1.8f, 1f), 18f, white, Color.black, pbs, made, list);
        Part("short block", new Vector3(0.8f, 0.5f, 1.5f), new Vector3(1f, 1f, 1f), -16f, white, Color.black, pbs, made, list);
        Part("glow", new Vector3(1.5f, 0.15f, 3.4f), new Vector3(0.3f, 0.3f, 0.3f), 0f, new Color(1f, 0.6f, 0.3f), new Color(6f, 2.4f, 0.6f), pbs, made, list);
        return list;
    }

    static void Part(string name, Vector3 pos, Vector3 size, float turn, Color albedo, Color emission, Shader pbs, List<UnityEngine.Object> made, List<Renderer> list)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        made.Add(go);
        go.name = name;
        UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, turn, 0f));
        go.transform.localScale = size;
        var mat = new Material(pbs) { name = name };
        made.Add(mat);
        mat.SetColor("_Color", albedo);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.15f);
        if (emission.maxColorComponent > 0f && mat.HasProperty("_EmissionColor"))
        {
            mat.SetColor("_EmissionColor", emission);
            mat.EnableKeyword("_EMISSION");
        }
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.lightProbeUsage = LightProbeUsage.BlendProbes;
        r.shadowCastingMode = ShadowCastingMode.On;
        r.receiveShadows = true;
        list.Add(r);
    }

    // ------------------------------------------------------------------ inside the GPU side

    /// <summary>The voxels and probes at a few places of the box (the grid's corner is the shown window's: the head stays put).</summary>
    static List<string> Inside(GiUnity gi, uint[] vox, NVector4[] probes, SphericalHarmonicsL2 sky)
    {
        var c = gi.Config;
        var o = new Vector3(gi.ShownOrigin.X, gi.ShownOrigin.Y, gi.ShownOrigin.Z);
        float vs = c.VoxelSize, ps = c.ProbeSpacing;
        var lines = new List<string>();
        int filled = 0, oneWay = 0;
        double rad = 0;
        for (int i = 0; i < vox.Length; i += 4)
        {
            if ((vox[i] >> 24) == 0) continue;
            filled++;
            if ((vox[i + 1] >> 24) > 89) oneWay++;   // coherence over 0.35
            var r = Rgb9e5(vox[i + 3]);
            rad += (r.x + r.y + r.z) / 3f;
        }
        lines.Add("voxels: " + filled + " filled, " + oneWay + " facing one way, mean reflected light " + (filled > 0 ? rad / filled : 0).ToString("0.000")
                  + "; grid corner " + o.ToString("F2"));
        string[] names = { "back wall", "floor", "ceiling", "left wall", "tall block's front", "short block's front", "open air outside the box" };
        Vector3[] at = { new Vector3(0f, 1.5f, 4.0f), new Vector3(-1.0f, 0f, 1.0f), new Vector3(0f, 3.0f, 2f), new Vector3(-2.0f, 1.5f, 2f),
                         new Vector3(-0.6f, 0.9f, 2.2f), new Vector3(0.8f, 0.5f, 1.0f), new Vector3(0f, 1.5f, -2.5f) };
        Vector3[] outward = { Vector3.back, Vector3.up, Vector3.down, Vector3.right, Vector3.back, Vector3.back, Vector3.back };
        for (int k = 0; k < names.Length; k++)
        {
            // the first filled voxel coming in from the room side, and the probe half a metre out into the room
            string v = "no filled voxel";
            for (int step = 3; step >= -3; step--)
            {
                var p = at[k] + outward[k] * (step * vs * 0.5f);
                int x = Mathf.FloorToInt((p.x - o.x) / vs), y = Mathf.FloorToInt((p.y - o.y) / vs), z = Mathf.FloorToInt((p.z - o.z) / vs);
                if (x < 0 || y < 0 || z < 0 || x >= c.VoxX || y >= c.VoxY || z >= c.VoxZ) continue;
                int i = ((z * c.VoxY + y) * c.VoxX + x) * 4;
                if ((vox[i] >> 24) == 0) continue;
                uint a = vox[i], n = vox[i + 1];
                var nrm = new Vector3((n & 255) / 255f * 2f - 1f, ((n >> 8) & 255) / 255f * 2f - 1f, ((n >> 16) & 255) / 255f * 2f - 1f);
                v = "voxel albedo " + new Vector3((a & 255) / 255f, ((a >> 8) & 255) / 255f, ((a >> 16) & 255) / 255f).ToString("F2")
                    + " normal " + nrm.ToString("F2") + " one-way " + ((n >> 24) / 255f).ToString("F2") + " reflected " + Rgb9e5(vox[i + 3]).ToString("F3")
                    + " glow " + Rgb9e5(vox[i + 2]).ToString("F2");
                break;
            }
            var q = at[k] + outward[k] * 0.5f;
            int px = Mathf.RoundToInt((q.x - o.x) / ps - 0.5f), py = Mathf.RoundToInt((q.y - o.y) / ps - 0.5f), pz = Mathf.RoundToInt((q.z - o.z) / ps - 0.5f);
            string pr = "no probe";
            if (px >= 0 && py >= 0 && pz >= 0 && px < c.ProbeX && py < c.ProbeY && pz < c.ProbeZ)
            {
                int j = ((pz * c.ProbeY + py) * c.ProbeX + px) * 4;
                var n4 = new NVector4(outward[k].x, outward[k].y, outward[k].z, 1f);
                var e = new Vector3(NVector4.Dot(probes[j], n4), NVector4.Dot(probes[j + 1], n4), NVector4.Dot(probes[j + 2], n4));
                var skyAt = new Color[1];
                sky.Evaluate(new[] { outward[k] }, skyAt);
                pr = "probe valid " + probes[j + 3].X.ToString("0") + " mean " + new Vector3(probes[j].W, probes[j + 1].W, probes[j + 2].W).ToString("F3")
                     + " toward the surface's front " + e.ToString("F3") + " (the sky's own there: " + new Vector3(skyAt[0].r, skyAt[0].g, skyAt[0].b).ToString("F3") + ")";
            }
            lines.Add(names[k] + ": " + v + "; " + pr);
        }
        return lines;
    }

    static Vector3 Rgb9e5(uint v)
    {
        float scale = Mathf.Pow(2f, (int)(v >> 27) - 15 - 9);
        return new Vector3(v & 511u, (v >> 9) & 511u, (v >> 18) & 511u) * scale;
    }

    // ------------------------------------------------------------------ the ambient check

    /// <summary>Unity's constants for the ambient (as a renderer lit by it gets them), against GiUnity.PackAmbient; and the irradiance
    /// those constants give (Unity's ShadeSH9), against SphericalHarmonicsL2.Evaluate.</summary>
    static void CheckAmbient(SphericalHarmonicsL2 sh, List<UnityEngine.Object> made)
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(SHCheckPath);
        if (shader == null) { Debug.Log("SOFTBODY_GI_SCENE ambient check skipped: no " + SHCheckPath); return; }
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        made.Add(quad);
        UnityEngine.Object.DestroyImmediate(quad.GetComponent<Collider>());
        quad.layer = CheckLayer;
        quad.transform.position = new Vector3(0f, -1000f, 1f);
        quad.transform.localScale = new Vector3(40f, 4f, 1f);
        var mat = new Material(shader);
        made.Add(mat);
        var mr = quad.GetComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.lightProbeUsage = LightProbeUsage.BlendProbes;
        var camGo = new GameObject("gi ambient check camera");
        made.Add(camGo);
        var cam = camGo.AddComponent<Camera>();
        cam.enabled = false;
        cam.transform.SetPositionAndRotation(new Vector3(0f, -1000f, 0f), Quaternion.identity);
        cam.orthographic = true;
        cam.orthographicSize = 1f;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 5f;
        cam.cullingMask = 1 << CheckLayer;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.clear;
        cam.renderingPath = RenderingPath.Forward;
        cam.allowHDR = true;
        cam.allowMSAA = false;
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        var rt = new RenderTexture(8, 1, 16, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { antiAliasing = 1 };
        made.Add(rt);
        rt.Create();
        cam.targetTexture = rt;
        cam.aspect = 8f;
        cam.Render();
        var active = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(8, 1, TextureFormat.RGBAFloat, false, true);
        made.Add(tex);
        tex.ReadPixels(new Rect(0, 0, 8, 1), 0, 0, false);
        tex.Apply();
        RenderTexture.active = active;

        var mine = new NVector4[7];
        GiUnity.PackAmbient(sh, mine);
        var unity = new Vector4[7];
        float worst = 0f, scale = 0f;
        var names = new[] { "SHAr", "SHAg", "SHAb", "SHBr", "SHBg", "SHBb", "SHC" };
        for (int k = 0; k < 7; k++)
        {
            Color c = tex.GetPixel(k, 0);
            unity[k] = new Vector4(c.r, c.g, c.b, c.a);
            var m = new Vector4(mine[k].X, mine[k].Y, mine[k].Z, mine[k].W);
            worst = Mathf.Max(worst, (unity[k] - m).magnitude);
            scale = Mathf.Max(scale, unity[k].magnitude);
            Debug.Log("SOFTBODY_GI_SCENE ambient " + names[k] + ": Unity " + unity[k].ToString("F5") + ", ours " + m.ToString("F5"));
        }
        // the irradiance Unity's shaders compute from its constants, against Evaluate
        var dirs = new[] { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back, new Vector3(0.5f, 0.7f, -0.5f).normalized };
        var eval = new Color[dirs.Length];
        sh.Evaluate(dirs, eval);
        float worstEval = 0f;
        for (int i = 0; i < dirs.Length; i++)
        {
            var n = dirs[i];
            var v = new Vector4(n.x, n.y, n.z, 1f);
            var b = new Vector4(n.x * n.y, n.y * n.z, n.z * n.z, n.z * n.x);
            float c2 = n.x * n.x - n.y * n.y;
            var s = new Color(Vector4.Dot(unity[0], v) + Vector4.Dot(unity[3], b) + unity[6].x * c2,
                              Vector4.Dot(unity[1], v) + Vector4.Dot(unity[4], b) + unity[6].y * c2,
                              Vector4.Dot(unity[2], v) + Vector4.Dot(unity[5], b) + unity[6].z * c2);
            worstEval = Mathf.Max(worstEval, Mathf.Max(Mathf.Abs(s.r - eval[i].r), Mathf.Max(Mathf.Abs(s.g - eval[i].g), Mathf.Abs(s.b - eval[i].b))));
        }
        Debug.Log("SOFTBODY_GI_SCENE ambient constants: largest difference " + worst.ToString("0.000000") + " (of " + scale.ToString("0.0000") + ") "
                  + (worst <= 1e-4f + 1e-3f * scale ? "MATCH" : "DIFFER") + "; ShadeSH9 against Evaluate: largest difference " + worstEval.ToString("0.000000"));
    }

    // ------------------------------------------------------------------ pictures

    static Texture2D Shot(Camera cam, RenderTexture rt, List<UnityEngine.Object> made)
    {
        cam.Render();
        var active = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, false);
        made.Add(tex);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
        tex.Apply();
        RenderTexture.active = active;
        return tex;
    }

    static void Save(Texture2D tex, string path) => File.WriteAllBytes(path, tex.EncodeToPNG());

    // a second view, from up high on the left, into the back right corner: the glowing cube the first view hides behind the short block
    static readonly Vector3 CornerEye = new Vector3(-0.3f, 2.7f, 0.4f), CornerAt = new Vector3(1.3f, 0.4f, 3.1f);

    static Texture2D CornerShot(Camera cam, RenderTexture rt, List<UnityEngine.Object> made)
    {
        var pos = cam.transform.position;
        var rot = cam.transform.rotation;
        cam.transform.SetPositionAndRotation(CornerEye, Quaternion.LookRotation(CornerAt - CornerEye));
        try { return Shot(cam, rt, made); }
        finally { cam.transform.SetPositionAndRotation(pos, rot); }
    }

    static void SaveSideBySide(Texture2D a, Texture2D b, string path, List<UnityEngine.Object> made)
    {
        var both = new Texture2D(a.width + b.width + 8, Math.Max(a.height, b.height), TextureFormat.RGBA32, false, false);
        made.Add(both);
        var fill = new Color32[both.width * both.height];
        for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(255, 255, 255, 255);
        both.SetPixels32(fill);
        both.SetPixels32(0, 0, a.width, a.height, a.GetPixels32());
        both.SetPixels32(a.width + 8, 0, b.width, b.height, b.GetPixels32());
        both.Apply();
        Save(both, path);
    }

    // a few places in the picture (fractions of its width and height, from the bottom left): mean sRGB colour 0-255
    static readonly string[] RegionNames = { "ceiling", "back wall", "floor", "tall block's front", "short block's top", "left wall", "right wall", "under the short block" };
    static readonly Rect[] RegionRects =
    {
        new Rect(0.42f, 0.88f, 0.16f, 0.06f), new Rect(0.44f, 0.62f, 0.12f, 0.1f), new Rect(0.42f, 0.08f, 0.16f, 0.06f),
        new Rect(0.36f, 0.38f, 0.06f, 0.12f), new Rect(0.6f, 0.3f, 0.08f, 0.04f), new Rect(0.06f, 0.5f, 0.06f, 0.2f),
        new Rect(0.88f, 0.5f, 0.06f, 0.2f), new Rect(0.6f, 0.13f, 0.06f, 0.03f),
    };

    static string Regions(Texture2D t)
    {
        var parts = new List<string>();
        for (int i = 0; i < RegionRects.Length; i++)
        {
            var c = Mean(t, RegionRects[i]);
            parts.Add(RegionNames[i] + " " + (int)c.x + "," + (int)c.y + "," + (int)c.z);
        }
        return string.Join("; ", parts);
    }

    static Vector3 Mean(Texture2D t, Rect r)
    {
        int x0 = (int)(r.x * t.width), y0 = (int)(r.y * t.height), w = Math.Max(1, (int)(r.width * t.width)), h = Math.Max(1, (int)(r.height * t.height));
        var px = t.GetPixels32();
        double sr = 0, sg = 0, sb = 0;
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
            {
                var c = px[y * t.width + x];
                sr += c.r; sg += c.g; sb += c.b;
            }
        double n = w * h;
        return new Vector3((float)(sr / n), (float)(sg / n), (float)(sb / n));
    }

    static string Difference(Texture2D a, Texture2D b)
    {
        var pa = a.GetPixels32();
        var pb = b.GetPixels32();
        double sum = 0, brighter = 0;
        int most = 0;
        for (int i = 0; i < pa.Length; i++)
        {
            int d = Math.Abs(pa[i].r - pb[i].r) + Math.Abs(pa[i].g - pb[i].g) + Math.Abs(pa[i].b - pb[i].b);
            sum += d / 3.0;
            brighter += ((pb[i].r + pb[i].g + pb[i].b) - (pa[i].r + pa[i].g + pa[i].b)) / 3.0;
            most = Math.Max(most, d / 3);
        }
        return "mean " + (sum / pa.Length).ToString("0.00") + " of 255 a channel (brighter by " + (brighter / pa.Length).ToString("0.00") + "), most " + most;
    }
}

/// <summary>The editor's render thread, reached as the game's renderer reaches its own (AssetIntegrator: a plugin event that calls back
/// into C#): GL.IssuePluginEvent with a C# callback runs the queued actions on Unity's render thread, in order with the draws queued
/// before it. Sync waits until the render thread has run everything queued so far (a GPU read-back queued behind it).</summary>
public static class EditorRenderThread
{
    delegate void RenderEvent(int eventId);
    static readonly RenderEvent s_event = OnEvent;   // kept alive: the render thread calls it through a native thunk
    static readonly IntPtr s_pointer = Marshal.GetFunctionPointerForDelegate(s_event);
    static readonly ConcurrentQueue<Action> s_queue = new ConcurrentQueue<Action>();
    static ComputeBuffer s_sync;
    static volatile Exception s_failure;
    public static Exception Failure => s_failure;

    [AOT.MonoPInvokeCallback(typeof(RenderEvent))]
    static void OnEvent(int eventId)
    {
        Action a;
        while (s_queue.TryDequeue(out a))
        {
            try { a(); }
            catch (Exception ex) { if (s_failure == null) s_failure = ex; }
        }
    }

    public static void Enqueue(Action a)
    {
        s_queue.Enqueue(a);
        GL.IssuePluginEvent(s_pointer, 0);
    }

    public static void Sync()
    {
        if (s_sync == null) s_sync = new ComputeBuffer(1, 4);
        AsyncGPUReadback.Request(s_sync).WaitForCompletion();
    }

    public static void Release()
    {
        if (s_sync != null) { s_sync.Release(); s_sync = null; }
    }
}
