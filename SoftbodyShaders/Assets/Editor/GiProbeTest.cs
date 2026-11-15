// Feasibility test for live bounce light (GI) in the renderer fork, run in the 2019.4 editor on one of the game's own cached
// shader bundles (RuntimeData\PreCache): can a renderer get its probe light from us, without changing Resonite's shaders?
//   per object : LightProbeUsage.CustomProvided + MaterialPropertyBlock SH (with and without a per-material block, as Resonite sets)
//   per pixel  : the shaders' light probe proxy volume path (unity_ProbeVolumeSH), fed from a MaterialPropertyBlock or globals
// Logs SOFTBODY_GI <case> left/middle/right colours, then SOFTBODY_GI DONE.
//   Unity.exe -quit -projectPath . -executeMethod GiProbeTest.Run -logFile gi.log -giBundle <bundle>
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class GiProbeTest
{
    const int Size = 64;
    static Camera _cam;
    static RenderTexture _rt;
    static Texture2D _read;

    public static void Run()
    {
        try
        {
            string path = Arg("-giBundle");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null) throw new Exception("the bundle did not load: " + path);
            var shaders = bundle.LoadAllAssets<Shader>();
            if (shaders.Length == 0) throw new Exception("no shader in " + path);
            Shader shader = shaders[0];
            Debug.Log($"SOFTBODY_GI shader {shader.name} supported {shader.isSupported} passes {shader.passCount} color space {PlayerSettings.colorSpace}");

            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = new SphericalHarmonicsL2();
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.reflectionIntensity = 0f;
            RenderSettings.fog = false;

            var camGo = new GameObject("gi test camera");
            _cam = camGo.AddComponent<Camera>();
            _cam.orthographic = true;
            _cam.orthographicSize = 1f;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 10f;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = Color.black;
            _cam.renderingPath = RenderingPath.Forward;
            _cam.allowHDR = true;
            _cam.allowMSAA = false;
            _cam.stereoTargetEye = StereoTargetEyeMask.None;
            _rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            _cam.targetTexture = _rt;
            _read = new Texture2D(Size, Size, TextureFormat.RGBAHalf, false, true);

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);   // faces -z, toward the camera
            UnityEngine.Object.DestroyImmediate(quad.GetComponent<Collider>());
            quad.transform.position = new Vector3(0f, 0f, 2f);
            quad.transform.localScale = new Vector3(2f, 2f, 1f);           // x and y from -1 to 1
            var mat = new Material(shader);
            Set(mat, "_Color", Color.white);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", Texture2D.whiteTexture);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
            Set(mat, "_EmissionColor", Color.black);
            var r = quad.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            Debug.Log($"SOFTBODY_GI RenderType '{mat.GetTag("RenderType", false)}' queue {mat.renderQueue}");

            var green = new SphericalHarmonicsL2(); green.AddAmbientLight(Color.green);
            var red = new SphericalHarmonicsL2(); red.AddAmbientLight(Color.red);

            // C0: nothing (black ambient)
            r.lightProbeUsage = LightProbeUsage.BlendProbes;
            Shot("C0 black ambient (control)", r);
            // C1: the scene's ambient probe red: the shader reads Unity's SH at all
            RenderSettings.ambientProbe = red;
            Shot("C1 ambient probe red", r);
            RenderSettings.ambientProbe = new SphericalHarmonicsL2();

            // C2: per object, CustomProvided + renderer block SH green
            var mpb = new MaterialPropertyBlock();
            mpb.CopySHCoefficientArraysFrom(new[] { green });
            r.lightProbeUsage = LightProbeUsage.CustomProvided;
            r.SetPropertyBlock(mpb);
            Shot("C2 CustomProvided renderer block green", r);

            // C3: plus a per-material block without SH (what Resonite sets when a world uses MaterialPropertyBlocks)
            var perMat = new MaterialPropertyBlock();
            perMat.SetColor("_Color", Color.white);
            r.SetPropertyBlock(perMat, 0);
            Shot("C3 + per-material block (no SH)", r);
            // C3b: the per-material block carries the SH too
            perMat.CopySHCoefficientArraysFrom(new[] { green });
            r.SetPropertyBlock(perMat, 0);
            Shot("C3b per-material block with SH", r);
            r.SetPropertyBlock(null, 0);
            r.SetPropertyBlock(null);

            // the volume: 2x2x2 cells over x,y -1..1, z 1..3; left cells red, right cells blue (L0 only)
            const int W = 2, H = 2, D = 2;
            var vol = new Texture3D(4 * W, H, D, TextureFormat.RGBAHalf, false);
            vol.wrapMode = TextureWrapMode.Clamp;
            vol.filterMode = FilterMode.Bilinear;
            var px = new Color[4 * W * H * D];
            for (int z = 0; z < D; z++)
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int row = (z * H + y) * 4 * W;
                        px[row + x] = new Color(0, 0, 0, x == 0 ? 1f : 0f);              // SHAr: red on the left
                        px[row + W + x] = new Color(0, 0, 0, 0);                           // SHAg
                        px[row + 2 * W + x] = new Color(0, 0, 0, x == 1 ? 1f : 0f);        // SHAb: blue on the right
                        px[row + 3 * W + x] = new Color(1, 1, 1, 1);                       // occlusion
                    }
            vol.SetPixels(px);
            vol.Apply(false);
            var pvParams = new Vector4(1f, 0f, 1f / (4 * W), 0f);
            var pvMin = new Vector4(-1f, -1f, 1f, 0f);
            var pvSizeInv = new Vector4(0.5f, 0.5f, 0.5f, 0f);

            // C4: per pixel from the renderer block (CustomProvided, SH zero)
            var vb = new MaterialPropertyBlock();
            vb.CopySHCoefficientArraysFrom(new[] { new SphericalHarmonicsL2() });
            FillVolume(vb, vol, pvParams, pvMin, pvSizeInv);
            r.lightProbeUsage = LightProbeUsage.CustomProvided;
            r.SetPropertyBlock(vb);
            Shot("C4 volume in renderer block, CustomProvided", r);
            // C5: the same block with lightProbeUsage Off
            r.lightProbeUsage = LightProbeUsage.Off;
            Shot("C5 volume in renderer block, Off", r);
            // C5b: BlendProbes
            r.lightProbeUsage = LightProbeUsage.BlendProbes;
            Shot("C5b volume in renderer block, BlendProbes", r);
            r.SetPropertyBlock(null);

            // C6: globals
            Shader.SetGlobalVector("unity_ProbeVolumeParams", pvParams);
            Shader.SetGlobalMatrix("unity_ProbeVolumeWorldToObject", Matrix4x4.identity);
            Shader.SetGlobalVector("unity_ProbeVolumeMin", pvMin);
            Shader.SetGlobalVector("unity_ProbeVolumeSizeInv", pvSizeInv);
            Shader.SetGlobalTexture("unity_ProbeVolumeSH", vol);
            r.lightProbeUsage = LightProbeUsage.CustomProvided;
            var zero = new MaterialPropertyBlock();
            zero.CopySHCoefficientArraysFrom(new[] { new SphericalHarmonicsL2() });
            r.SetPropertyBlock(zero);
            Shot("C6 volume as globals, CustomProvided", r);
            r.lightProbeUsage = LightProbeUsage.BlendProbes;
            r.SetPropertyBlock(null);
            Shot("C6b volume as globals, BlendProbes", r);
            Shader.SetGlobalVector("unity_ProbeVolumeParams", Vector4.zero);

            // C7: a real proxy volume component (it samples the scene's light probes: none) plus our block on top
            var lppv = quad.AddComponent<LightProbeProxyVolume>();
            lppv.boundingBoxMode = LightProbeProxyVolume.BoundingBoxMode.Custom;
            lppv.originCustom = new Vector3(0f, 0f, 0f);
            lppv.sizeCustom = new Vector3(2f, 2f, 2f);
            lppv.resolutionMode = LightProbeProxyVolume.ResolutionMode.Custom;
            lppv.gridResolutionX = W; lppv.gridResolutionY = H; lppv.gridResolutionZ = D;
            lppv.refreshMode = LightProbeProxyVolume.RefreshMode.ViaScripting;
            lppv.Update();
            r.lightProbeUsage = LightProbeUsage.UseProxyVolume;
            Shot("C7 LPPV component alone", r);
            r.SetPropertyBlock(vb);
            Shot("C7b LPPV component + our block", r);
            r.SetPropertyBlock(null);
            UnityEngine.Object.DestroyImmediate(lppv);
            r.lightProbeUsage = LightProbeUsage.BlendProbes;

            // C8: only the switch (unity_ProbeVolumeParams) per renderer; the volume, its corner, size and matrix as globals. If this
            // works, moving the window is a few global sets, with nothing to redo per renderer.
            Shader.SetGlobalVector("unity_ProbeVolumeParams", Vector4.zero);
            Shader.SetGlobalMatrix("unity_ProbeVolumeWorldToObject", Matrix4x4.identity);
            Shader.SetGlobalVector("unity_ProbeVolumeMin", pvMin);
            Shader.SetGlobalVector("unity_ProbeVolumeSizeInv", pvSizeInv);
            Shader.SetGlobalTexture("unity_ProbeVolumeSH", vol);
            var onlyParams = new MaterialPropertyBlock();
            onlyParams.SetVector("unity_ProbeVolumeParams", pvParams);
            r.SetPropertyBlock(onlyParams);
            Shot("C8 params in renderer block, the rest global", r);
            // C8b: the same switch in a per-material block (what a world's MaterialPropertyBlock would carry)
            r.SetPropertyBlock(null);
            var perMatParams = new MaterialPropertyBlock();
            perMatParams.SetColor("_Color", Color.white);
            perMatParams.SetVector("unity_ProbeVolumeParams", pvParams);
            r.SetPropertyBlock(perMatParams, 0);
            Shot("C8b params in per-material block, the rest global", r);
            r.SetPropertyBlock(null, 0);
            // C8c: the window moved (globals only): the corner shifted by one cell to the left, so the red cell now covers the middle
            r.SetPropertyBlock(onlyParams);
            Shader.SetGlobalVector("unity_ProbeVolumeMin", new Vector4(-2f, -1f, 1f, 0f));
            Shot("C8c globals moved half the volume left (expect left mostly blue, middle and right blue)", r);
            Shader.SetGlobalVector("unity_ProbeVolumeMin", pvMin);
            // C8d: params, corner, size and matrix per renderer, only the texture global
            var noTex = new MaterialPropertyBlock();
            noTex.SetVector("unity_ProbeVolumeParams", pvParams);
            noTex.SetMatrix("unity_ProbeVolumeWorldToObject", Matrix4x4.identity);
            noTex.SetVector("unity_ProbeVolumeMin", pvMin);
            noTex.SetVector("unity_ProbeVolumeSizeInv", pvSizeInv);
            r.SetPropertyBlock(noTex);
            Shot("C8d all but the texture in renderer block, texture global", r);
            r.SetPropertyBlock(null);

            // C9: the volume as a 3D render texture (the kind the fork writes from compute), copied from the Texture3D
            var rt3 = new RenderTexture(4 * W, H, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Tex3D, volumeDepth = D, enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, useMipMap = false, autoGenerateMips = false,
            };
            rt3.Create();
            Graphics.CopyTexture(vol, rt3);
            Debug.Log($"SOFTBODY_GI rt3 created {rt3.IsCreated()} native {rt3.GetNativeTexturePtr() != IntPtr.Zero} format {rt3.graphicsFormat}");
            Shader.SetGlobalTexture("unity_ProbeVolumeSH", rt3);
            r.SetPropertyBlock(onlyParams);
            Shot("C9 3D render texture as the global volume", r);
            r.SetPropertyBlock(null);
            Shader.SetGlobalTexture("unity_ProbeVolumeSH", vol);

            // C10: a renderer without our block is untouched by the globals (the switch is off per draw)
            Shot("C10 no block, globals set (should be black)", r);

            // C12: whose SH a BlendProbes renderer uses when its block carries SH too: Unity's ambient (red) or the block's (green)
            Shader.SetGlobalVector("unity_ProbeVolumeParams", Vector4.zero);
            RenderSettings.ambientProbe = red;
            r.lightProbeUsage = LightProbeUsage.BlendProbes;
            var shBlock = new MaterialPropertyBlock();
            shBlock.CopySHCoefficientArraysFrom(new[] { green });
            r.SetPropertyBlock(shBlock);
            Shot("C12 ambient red, BlendProbes, block SH green (green: the block's SH wins)", r);
            // C12b/c: the volume plus a block SH that is only a quadratic part: sh[c, 6] = 0.25 adds 0.75 white facing -z (unity_SHB.z),
            // on top of the volume's red (left) and blue (right) when the block's quadratic part is used; Unity's own (red ambient) adds 0
            var l2 = new SphericalHarmonicsL2();
            for (int c = 0; c < 3; c++) l2[c, 6] = 0.25f;
            var l2Block = new MaterialPropertyBlock();
            l2Block.CopySHCoefficientArraysFrom(new[] { l2 });
            FillVolume(l2Block, vol, pvParams, pvMin, pvSizeInv);
            r.SetPropertyBlock(l2Block);
            Shot("C12b volume + block quadratic SH, BlendProbes (left 1.75 0.75 0.75: the block's L2 is used; left 1 0 0: Unity's)", r);
            r.lightProbeUsage = LightProbeUsage.CustomProvided;
            Shot("C12c volume + block quadratic SH, CustomProvided", r);
            // C12d/e: only the four quadratic vectors set (what the fork's GI sets), with and without the volume
            var l2Only = new MaterialPropertyBlock();
            var white3 = new Vector4(0f, 0f, 0.75f, 0f);
            l2Only.SetVector("unity_SHBr", white3); l2Only.SetVector("unity_SHBg", white3); l2Only.SetVector("unity_SHBb", white3);
            l2Only.SetVector("unity_SHC", new Vector4(0f, 0f, 0f, 1f));
            FillVolume(l2Only, vol, pvParams, pvMin, pvSizeInv);
            r.lightProbeUsage = LightProbeUsage.BlendProbes;
            r.SetPropertyBlock(l2Only);
            Shot("C12d volume + only unity_SHB/SHC in the block, BlendProbes (expect as C12b)", r);
            var l2NoVol = new MaterialPropertyBlock();
            l2NoVol.SetVector("unity_SHBr", white3); l2NoVol.SetVector("unity_SHBg", white3); l2NoVol.SetVector("unity_SHBb", white3);
            l2NoVol.SetVector("unity_SHC", new Vector4(0f, 0f, 0f, 1f));
            r.SetPropertyBlock(l2NoVol);
            Shot("C12e only unity_SHB/SHC in the block, no volume (expect Unity's red L0 + 0.75 white: 1.75 0.75 0.75)", r);
            r.SetPropertyBlock(null);
            Shot("C12f block removed again (expect red 1 0 0)", r);
            r.lightProbeUsage = LightProbeUsage.BlendProbes;
            RenderSettings.ambientProbe = new SphericalHarmonicsL2();

            // the APIs the fork's GI glue uses
            var probe = new MaterialPropertyBlock();
            r.GetPropertyBlock(probe, 0);
            Debug.Log($"SOFTBODY_GI api: isEmpty {probe.isEmpty} hasBlock {r.HasPropertyBlock()} forceRenderingOff {r.forceRenderingOff}");

            // C11: what one SetPropertyBlock costs (the fork sets one per renderer when a world loads)
            var many = new MeshRenderer[2000];
            var sharedMesh = quad.GetComponent<MeshFilter>().sharedMesh;
            for (int i = 0; i < many.Length; i++)
            {
                var g = new GameObject("r" + i);
                g.AddComponent<MeshFilter>().sharedMesh = sharedMesh;
                many[i] = g.AddComponent<MeshRenderer>();
                many[i].sharedMaterial = mat;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < many.Length; i++) many[i].SetPropertyBlock(onlyParams);
            double setMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            for (int i = 0; i < many.Length; i++) { many[i].GetPropertyBlock(probe, 0); }
            double getMs = sw.Elapsed.TotalMilliseconds;
            Debug.Log($"SOFTBODY_GI C11 SetPropertyBlock x{many.Length}: {setMs:0.00} ms; GetPropertyBlock(m) x{many.Length}: {getMs:0.00} ms");
            foreach (var m in many) UnityEngine.Object.DestroyImmediate(m.gameObject);

            Debug.Log("SOFTBODY_GI DONE");
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogError("SOFTBODY_GI FAILED " + ex);
            EditorApplication.Exit(1);
        }
    }

    static void FillVolume(MaterialPropertyBlock b, Texture3D vol, Vector4 pvParams, Vector4 pvMin, Vector4 pvSizeInv)
    {
        b.SetVector("unity_ProbeVolumeParams", pvParams);
        b.SetMatrix("unity_ProbeVolumeWorldToObject", Matrix4x4.identity);
        b.SetVector("unity_ProbeVolumeMin", pvMin);
        b.SetVector("unity_ProbeVolumeSizeInv", pvSizeInv);
        b.SetTexture("unity_ProbeVolumeSH", vol);
    }

    static void Shot(string name, Renderer r)
    {
        _cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = _rt;
        _read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
        _read.Apply(false);
        RenderTexture.active = prev;
        Color L = _read.GetPixel(Size / 8, Size / 2), M = _read.GetPixel(Size / 2, Size / 2), R = _read.GetPixel(Size - Size / 8, Size / 2);
        Debug.Log($"SOFTBODY_GI {name}: left {Fmt(L)} middle {Fmt(M)} right {Fmt(R)}");
    }

    static string Fmt(Color c) { return $"({c.r:0.000} {c.g:0.000} {c.b:0.000})"; }

    static void Set(Material m, string prop, Color c) { if (m.HasProperty(prop)) m.SetColor(prop, c); }

    static string Arg(string name)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        throw new Exception("missing " + name);
    }
}
