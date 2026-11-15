// Which of the voxel camera's calls make the main thread wait for Unity's render thread. In the game (GI test 1, 2026-11-14) the
// slab draw held the renderer's main thread about 5.5 ms a frame while that thread was mostly idle: a wait, not work. It sits just
// before the renderer's asset integration, which then barely ran, so panels took seconds to open.
// Here the render thread is kept busy (a plugin event that sleeps BusyMs) and each call is timed: one that waits for the render
// thread takes about BusyMs, one that only queues its work takes well under a millisecond. Two calls Unity documents as waiting
// (GetNativeTexturePtr, GetNativeBufferPtr) show that the test sees a wait. Logs SOFTBODY_GI_SYNC lines. C# 7.3.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Softbody.GI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

public static class GiSyncProbe
{
    const int BusyMs = 40, Rounds = 5;
    const string VoxelizeName = "Hidden/Softbody/GIVoxelize";

    static string Need(string name)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < a.Length; i++) if (a[i] == name) return a[i + 1];
        throw new ArgumentException(name + " is missing");
    }

    public static void Run()
    {
        var made = new List<UnityEngine.Object>();
        AssetBundle bundle = null;
        GiUnity gi = null;
        SharpDX.Direct3D11.Device device = null;
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            bundle = AssetBundle.LoadFromFile(Need("-giBundle"));
            if (bundle == null) throw new InvalidOperationException("the GI bundle did not load");
            var voxelize = bundle.LoadAllAssets<Shader>().First(s => s != null && s.name == VoxelizeName);
            var bytecode = GiCompute.CompileAll(File.ReadAllText(Need("-giHlsl")));
            device = EditorDevice();
            var dev = device;

            // a small world: a floor and a few boxes, one skinned box, a directional light with soft shadows
            var mat = new Material(Shader.Find("Standard")) { name = "probe white" };
            made.Add(mat);
            var boxes = new List<MeshRenderer>();
            for (int i = 0; i < 6; i++)
            {
                var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
                made.Add(b);
                UnityEngine.Object.DestroyImmediate(b.GetComponent<Collider>());
                b.transform.position = i == 0 ? new Vector3(0f, -0.1f, 2f) : new Vector3(-2f + i * 0.8f, 0.5f, 2f + (i % 2));
                b.transform.localScale = i == 0 ? new Vector3(8f, 0.2f, 8f) : Vector3.one * 0.6f;
                var mr = b.GetComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                boxes.Add(mr);
            }
            var skinned = MakeSkinned(mat, made);
            var lightGo = new GameObject("probe light");
            made.Add(lightGo);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(50f, 30f, 0f);
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowDistance = 50f;

            // the voxel camera as GiUnity sets it up, around a slab over the boxes
            var camGo = new GameObject("probe voxel camera");
            made.Add(camGo);
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.Nothing;
            cam.renderingPath = RenderingPath.Forward;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.depthTextureMode = DepthTextureMode.None;
            var rt = new RenderTexture(256, 256, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear) { antiAliasing = 1 };
            made.Add(rt);
            rt.Create();
            cam.targetTexture = rt;
            camGo.transform.SetPositionAndRotation(new Vector3(0f, 1.5f, 0.5f), Quaternion.identity);
            cam.orthographicSize = 6f;
            cam.aspect = 2.6f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 4f;
            var buf = new ComputeBuffer(128 * 48 * 8 * 10, 4, ComputeBufferType.Structured);
            var voxelMat = new Material(voxelize) { name = "probe voxelize" };
            made.Add(voxelMat);
            int slabMin = Shader.PropertyToID("_SoftbodyGIProbeMin");

            Debug.Log("SOFTBODY_GI_SYNC " + SystemInfo.graphicsDeviceType + ", multithreaded rendering " + SystemInfo.graphicsMultiThreaded
                      + "; each call timed " + Rounds + " times while the render thread sleeps " + BusyMs + " ms (min / median / max)");

            // the method: calls that must wait, and one that must not
            Probe("(nothing: the test's own cost)", () => { });
            Probe("Texture2D.GetNativeTexturePtr on a new texture (Unity: waits)", () =>
            {
                var t = new Texture2D(4, 4);
                t.Apply();
                t.GetNativeTexturePtr();
                UnityEngine.Object.DestroyImmediate(t);
            });
            Probe("ComputeBuffer.GetNativeBufferPtr (Unity: waits)", () => buf.GetNativeBufferPtr());

            // the calls in GiUnity.RenderSlab, one by one
            Probe("Shader.SetGlobalVector", () => Shader.SetGlobalVector(slabMin, new Vector4(1f, 2f, 3f, 4f)));
            Probe("QualitySettings.shadows off and back", () =>
            {
                var s = QualitySettings.shadows;
                QualitySettings.shadows = ShadowQuality.Disable;
                QualitySettings.shadows = s;
            });
            Probe("forceRenderingOff on the skinned box, on and off", () =>
            {
                skinned.forceRenderingOff = true;
                skinned.forceRenderingOff = false;
            });
            Probe("Graphics.SetRandomWriteTarget + ClearRandomWriteTargets", () =>
            {
                Graphics.SetRandomWriteTarget(1, buf, false);
                Graphics.ClearRandomWriteTargets();
            });
            Probe("Camera.RenderWithShader (shadows on)", () => cam.RenderWithShader(voxelize, "RenderType"));
            Probe("Camera.RenderWithShader (shadows off around it)", () =>
            {
                var s = QualitySettings.shadows;
                QualitySettings.shadows = ShadowQuality.Disable;
                cam.RenderWithShader(voxelize, "RenderType");
                QualitySettings.shadows = s;
            });
            Probe("Camera.Render (no replacement)", () => cam.Render());

            // other ways to keep the voxel camera's draw free of shadows, and a draw without a camera
            Probe("QualitySettings.shadowDistance 0 and back", () =>
            {
                float d = QualitySettings.shadowDistance;
                QualitySettings.shadowDistance = 0f;
                QualitySettings.shadowDistance = d;
            });
            Probe("Light.shadows off and back", () =>
            {
                light.shadows = LightShadows.None;
                light.shadows = LightShadows.Soft;
            });
            var cb = new CommandBuffer { name = "probe voxelize" };
            cb.SetRenderTarget(rt);
            cb.SetViewProjectionMatrices(cam.worldToCameraMatrix, cam.projectionMatrix);
            cb.SetRandomWriteTarget(1, buf);
            foreach (var mr in boxes) cb.DrawRenderer(mr, voxelMat, 0, 0);
            cb.ClearRandomWriteTargets();
            Probe("Graphics.ExecuteCommandBuffer (DrawRenderer for each box)", () => Graphics.ExecuteCommandBuffer(cb));

            // the whole frame as the game runs it: GiUnity.Tick, with and without the avatars hidden
            gi = new GiUnity(new GiConfig(), voxelize, bytecode, () => dev, EditorRenderThread.Enqueue);
            gi.StartSelfTest(30);
            gi.WaitSelfTest();
            string test;
            if (gi.TestResult(out test) != true) throw new InvalidOperationException("the self-test failed: " + test);
            var lights = new List<Light> { light };
            Action<bool> hide = h => skinned.forceRenderingOff = h;
            int mask = ~(1 << 30);
            for (int i = 0; i < 40; i++) { gi.Tick(Vector3.zero, mask, lights, hide); EditorRenderThread.Sync(); }   // past its start
            Probe("GiUnity.Tick (avatars hidden, as in the game)", () => gi.Tick(Vector3.zero, mask, lights, hide));
            Probe("GiUnity.Tick (avatars not hidden)", () => gi.Tick(Vector3.zero, mask, lights, null));

            cb.Release();
            buf.Release();
            Debug.Log("SOFTBODY_GI_SYNC DONE");
        }
        catch (Exception ex)
        {
            Debug.LogError("SOFTBODY_GI_SYNC FAILED " + ex);
            EditorApplication.Exit(1);
        }
        finally
        {
            if (gi != null) gi.Dispose();
            EditorRenderThread.Sync();
            EditorRenderThread.Release();
            GiShaderWatch.Stop();
            foreach (var o in made) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            if (device != null) device.Dispose();
            if (bundle != null) bundle.Unload(true);
        }
    }

    /// <summary>Times <paramref name="call"/> while the render thread sleeps BusyMs (after everything before it has run).</summary>
    static void Probe(string name, Action call)
    {
        var times = new List<double>();
        for (int r = 0; r < Rounds; r++)
        {
            EditorRenderThread.Sync();
            EditorRenderThread.Enqueue(() => Thread.Sleep(BusyMs));
            var sw = Stopwatch.StartNew();
            call();
            times.Add(sw.Elapsed.TotalMilliseconds);
            EditorRenderThread.Sync();
        }
        times.Sort();
        bool waits = times[Rounds / 2] > BusyMs * 0.5;
        Debug.Log("SOFTBODY_GI_SYNC " + name + ": " + times[0].ToString("0.00") + " / " + times[Rounds / 2].ToString("0.00") + " / "
                  + times[Rounds - 1].ToString("0.00") + " ms" + (waits ? "  WAITS for the render thread" : ""));
    }

    static SkinnedMeshRenderer MakeSkinned(Material mat, List<UnityEngine.Object> made)
    {
        var src = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var mesh = UnityEngine.Object.Instantiate(src.GetComponent<MeshFilter>().sharedMesh);
        UnityEngine.Object.DestroyImmediate(src);
        made.Add(mesh);
        var go = new GameObject("probe skinned box");
        made.Add(go);
        go.transform.position = new Vector3(1.5f, 0.5f, 3f);
        var bone = new GameObject("probe bone").transform;
        bone.SetParent(go.transform, false);
        made.Add(bone.gameObject);
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, mesh.vertexCount).ToArray();
        mesh.bindposes = new[] { bone.worldToLocalMatrix * go.transform.localToWorldMatrix };
        var smr = go.AddComponent<SkinnedMeshRenderer>();
        smr.bones = new[] { bone };
        smr.sharedMesh = mesh;
        smr.sharedMaterial = mat;
        return smr;
    }

    /// <summary>The editor's own Direct3D 11 device (a wrapper with its own reference), as GiShaderBuild gets it.</summary>
    static SharpDX.Direct3D11.Device EditorDevice()
    {
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
}
