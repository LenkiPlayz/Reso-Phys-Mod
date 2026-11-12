// Builds the renderer fork's smoke shader bundle (Softbody/GpuFluid/Shaders/softbody_smoke.bundle): the shader, its 3D wisp noise and
// two materials (smoke, and spray/foam). Run by build.ps1 next to this project (Unity 2019.4.31f1 with its window: batch mode is not
// licensed on this PC). The game's renderer is Unity 2019.4.19f1 with single-pass (double-wide) stereo, so the build turns VR on with
// single-pass stereo (the shader then gets its UNITY_SINGLE_PASS_STEREO variants, as Resonite's own shaders) and keeps every fog mode.
// Preview(): renders a frame of smoke dumped by the offline tests (gas-preview) with the shader, to a PNG.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

public static class SoftbodyShaderBuild
{
    const string ShaderPath = "Assets/Softbody/SoftbodySmoke.shader";
    const string NoisePath = "Assets/Softbody/SmokeNoise.asset";
    const string SmokeMatPath = "Assets/Softbody/SoftbodySmoke.mat";
    const string FoamMatPath = "Assets/Softbody/SoftbodyFoam.mat";
    public const string BundleName = "softbody_smoke.bundle";

    static string Arg(string name, string fallback = null)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < a.Length; i++) if (a[i] == name) return a[i + 1];
        return fallback;
    }

    public static void Build()
    {
        try
        {
            string outDir = Arg("-softbodyOut") ?? throw new ArgumentException("-softbodyOut is missing");
            Directory.CreateDirectory(outDir);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // as the game's renderer project: VR with single-pass stereo, linear colour
            #pragma warning disable 618
            PlayerSettings.virtualRealitySupported = true;
            #pragma warning restore 618
            PlayerSettings.stereoRenderingPath = StereoRenderingPath.SinglePass;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            KeepAllFogModes();

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null) throw new InvalidOperationException("no shader at " + ShaderPath);
            var messages = ShaderUtil.GetShaderMessages(shader);
            foreach (var m in messages) Debug.Log($"SOFTBODY_SHADER_MESSAGE {m.severity} {m.platform} line {m.line}: {m.message}");
            if (messages.Any(m => m.severity == ShaderCompilerMessageSeverity.Error)) throw new InvalidOperationException("the shader has errors");

            var noise = MakeNoise(32);
            CreateOrReplace(noise, NoisePath);
            noise = AssetDatabase.LoadAssetAtPath<Texture3D>(NoisePath);

            var smoke = new Material(shader) { name = "Softbody smoke" };
            smoke.SetTexture("_NoiseTex", noise);
            CreateOrReplace(smoke, SmokeMatPath);
            var foam = new Material(shader) { name = "Softbody foam" };
            foam.SetTexture("_NoiseTex", noise);
            // spray, foam and bubbles: small, rounder, hardly wispy, no spin, a thin soft edge
            foam.SetFloat("_NoiseAmount", 0.15f); foam.SetFloat("_Edge", 1.1f); foam.SetFloat("_Density", 1.5f);
            foam.SetFloat("_Roundness", 0.8f); foam.SetFloat("_Spin", 0f); foam.SetFloat("_Forward", 0.3f); foam.SetFloat("_ForwardMix", 0.2f);
            foam.SetFloat("_SoftEdge", 0.3f); foam.SetFloat("_NearFade", 0.05f);
            CreateOrReplace(foam, FoamMatPath);
            AssetDatabase.SaveAssets();

            var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = new[] { ShaderPath, NoisePath, SmokeMatPath, FoamMatPath } };
            var manifest = BuildPipeline.BuildAssetBundles(outDir, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.DeterministicAssetBundle,
                BuildTarget.StandaloneWindows64);
            string file = Path.Combine(outDir, BundleName);
            if (manifest == null || !File.Exists(file)) throw new InvalidOperationException("the bundle was not built");
            Debug.Log($"SOFTBODY_BUILD OK {file} {new FileInfo(file).Length} bytes; variants compiled: {SmokeVariantLog.Count} (stereo {SmokeVariantLog.Stereo})");
        }
        catch (Exception ex)
        {
            Debug.LogError("SOFTBODY_BUILD FAILED " + ex);
            EditorApplication.Exit(1);
        }
    }

    static void CreateOrReplace(UnityEngine.Object asset, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(asset, path);
    }

    /// <summary>Keeps the fog variants (an asset bundle has no scene to tell which fog modes are used).</summary>
    static void KeepAllFogModes()
    {
        var gs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset").FirstOrDefault();
        if (gs == null) { Debug.LogWarning("SOFTBODY_BUILD no GraphicsSettings to keep the fog modes in"); return; }
        var so = new SerializedObject(gs);
        void Set(string name, int v) { var p = so.FindProperty(name); if (p != null) { if (p.propertyType == SerializedPropertyType.Boolean) p.boolValue = v != 0; else p.intValue = v; } }
        Set("m_FogStripping", 1); Set("m_FogKeepLinear", 1); Set("m_FogKeepExp", 1); Set("m_FogKeepExp2", 1);
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    /// <summary>Tileable value noise, two scales (r: 4 cells across, g: 8), each with one octave on top, 0..1.</summary>
    static Texture3D MakeNoise(int n)
    {
        var tex = new Texture3D(n, n, n, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None)
        {
            name = "Softbody smoke noise", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, anisoLevel = 0,
        };
        var rnd = new System.Random(1234);
        float[] lattice = new float[16 * 16 * 16];
        for (int i = 0; i < lattice.Length; i++) lattice[i] = (float)rnd.NextDouble();
        float Value(float x, float y, float z, int period)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y), z0 = (int)Math.Floor(z);
            float fx = x - x0, fy = y - y0, fz = z - z0;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
            float L(int a, int b, int c) => lattice[(((a % period) + period) % period) + 16 * ((((b % period) + period) % period) + 16 * (((c % period) + period) % period))];
            float x00 = Mathf.Lerp(L(x0, y0, z0), L(x0 + 1, y0, z0), fx), x10 = Mathf.Lerp(L(x0, y0 + 1, z0), L(x0 + 1, y0 + 1, z0), fx);
            float x01 = Mathf.Lerp(L(x0, y0, z0 + 1), L(x0 + 1, y0, z0 + 1), fx), x11 = Mathf.Lerp(L(x0, y0 + 1, z0 + 1), L(x0 + 1, y0 + 1, z0 + 1), fx);
            return Mathf.Lerp(Mathf.Lerp(x00, x10, fy), Mathf.Lerp(x01, x11, fy), fz);
        }
        var px = new Color32[n * n * n];
        for (int z = 0; z < n; z++)
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (float)x / n, v = (float)y / n, w = (float)z / n;
                    float r = 0.65f * Value(u * 4, v * 4, w * 4, 4) + 0.35f * Value(u * 8, v * 8, w * 8, 8);
                    float g = 0.65f * Value(u * 8 + 3.7f, v * 8 + 1.3f, w * 8 + 5.1f, 8) + 0.35f * Value(u * 16, v * 16, w * 16, 16);
                    px[x + n * (y + n * z)] = new Color32((byte)(Mathf.Clamp01(r) * 255), (byte)(Mathf.Clamp01(g) * 255), 0, 255);
                }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }

    // ------------------------------------------------------------------ the preview

    /// <summary>Renders a frame dumped by the offline tests (tests: gas-preview) with the smoke material: a camera where the head
    /// was, the main light the GPU shaded the puffs for, a sky, and a grey floor; to -softbodyPng.</summary>
    public static void Preview()
    {
        try
        {
            string frame = Arg("-softbodyFrame") ?? throw new ArgumentException("-softbodyFrame is missing");
            string png = Arg("-softbodyPng") ?? throw new ArgumentException("-softbodyPng is missing");
            int width = int.Parse(Arg("-softbodyWidth", "960")), height = int.Parse(Arg("-softbodyHeight", "720"));
            var mat = AssetDatabase.LoadAssetAtPath<Material>(Arg("-softbodyMaterial", SmokeMatPath)) ?? throw new InvalidOperationException("no material (build first)");
            using (var r = new BinaryReader(File.OpenRead(frame)))
            {
                // header: magic, quads, then head xyz, look-at xyz, light direction xyz (towards the light), light colour rgb, intensity,
                // sky top rgb, sky middle rgb, ground rgb, floor height, depth texture (0/1)
                if (r.ReadInt32() != 0x534D4B31) throw new InvalidOperationException("not a smoke frame");
                int quads = r.ReadInt32();
                Vector3 V() => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                Color C() => new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                Vector3 head = V(), look = V(), toLight = V();
                Color lightCol = C(); float intensity = r.ReadSingle();
                Color skyTop = C(), skyMid = C(), ground = C();
                float floorY = r.ReadSingle();
                bool depth = r.ReadInt32() != 0;
                var pos = new Vector3[quads * 4]; var nrm = new Vector3[quads * 4]; var col = new Color[quads * 4]; var uv = new List<Vector4>(quads * 4);
                for (int k = 0; k < quads * 4; k++)
                {
                    pos[k] = V(); nrm[k] = V(); col[k] = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    uv.Add(new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                }
                var idx = new int[quads * 6];
                for (int k = 0; k < idx.Length; k++) idx[k] = r.ReadInt32();

                var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = skyTop; RenderSettings.ambientEquatorColor = skyMid; RenderSettings.ambientGroundColor = ground;
                RenderSettings.skybox = null;
                RenderSettings.fog = false;

                var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                mesh.vertices = pos; mesh.normals = nrm; mesh.colors = col; mesh.SetUVs(0, uv); mesh.SetTriangles(idx, 0);
                mesh.bounds = new Bounds(look, Vector3.one * 100);
                var go = new GameObject("smoke"); go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat; mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;

                var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                floor.transform.position = new Vector3(look.x, floorY, look.z); floor.transform.localScale = Vector3.one * 3;
                var floorMat = new Material(Shader.Find("Standard")); floorMat.color = new Color(0.45f, 0.45f, 0.45f);
                floor.GetComponent<MeshRenderer>().sharedMaterial = floorMat;
                var back = GameObject.CreatePrimitive(PrimitiveType.Cube);
                back.transform.position = look + (look - head).normalized * 2.5f + Vector3.up * 0.5f; back.transform.localScale = new Vector3(3, 3, 0.1f);
                back.transform.rotation = Quaternion.LookRotation(look - head);
                var backMat = new Material(Shader.Find("Standard")); backMat.color = new Color(0.25f, 0.3f, 0.35f);
                back.GetComponent<MeshRenderer>().sharedMaterial = backMat;

                var lightGo = new GameObject("sun"); var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional; light.color = lightCol; light.intensity = intensity; light.shadows = LightShadows.None;
                lightGo.transform.rotation = Quaternion.LookRotation(-toLight);

                var camGo = new GameObject("camera"); var cam = camGo.AddComponent<Camera>();
                cam.transform.position = head; cam.transform.LookAt(look);
                cam.fieldOfView = 60; cam.nearClipPlane = 0.05f; cam.farClipPlane = 200;
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = skyMid;
                cam.renderingPath = RenderingPath.Forward; cam.allowMSAA = false; cam.allowHDR = false;
                if (depth) cam.depthTextureMode |= DepthTextureMode.Depth;
                Shader.SetGlobalFloat("_SoftbodyDepthOK", depth ? 1f : 0f);
                var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(png, tex.EncodeToPNG());
                RenderTexture.active = null;
                Debug.Log($"SOFTBODY_PREVIEW OK {png} ({quads} puffs)");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("SOFTBODY_PREVIEW FAILED " + ex);
            EditorApplication.Exit(1);
        }
    }
}

/// <summary>Logs every variant of the smoke shader the build compiles (to check the single-pass stereo ones are there).</summary>
class SmokeVariantLog : IPreprocessShaders
{
    public static int Count, Stereo;
    public int callbackOrder => 0;
    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
    {
        if (shader == null || shader.name != "Softbody/Smoke") return;
        foreach (var d in data)
        {
            #pragma warning disable 618
            var names = d.shaderKeywordSet.GetShaderKeywords().Select(k => k.GetKeywordName()).ToArray();
            #pragma warning restore 618
            Count++;
            if (names.Contains("UNITY_SINGLE_PASS_STEREO")) Stereo++;
            Debug.Log($"SOFTBODY_VARIANT {snippet.passName} {snippet.shaderType} {d.shaderCompilerPlatform} [{string.Join(" ", names)}]");
        }
    }
}
