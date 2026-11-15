// 0.29.2 (fork, 2026-11-13): checks that the smoke shader really draws on the renderer it runs on. The 0.27.0 bundle loaded, and
// Unity called its shader supported, but on the game's renderer (Unity 2019.4.19) Direct3D refused every one of its programs. Unity
// 2019.4.31, which builds the bundle, writes them with a newer header that 2019.4.19 does not know. The log then filled with
// "ShaderProgram is unsupported" (1.2 million lines in one evening), and no smoke showed. SoftbodyShaders/fix_d3d11_programs.py now
// rewrites the header. On top of that, SoftbodyFluidHost uses the smoke shader only after it has drawn a test puff here (Run). It goes
// back to the plugin's Unlit discs if the renderer later logs that it could not make one of the shader's programs (Watch, Tripped).
//
// Plain UnityEngine and C# 7.3, written to need no nullable annotations: the SoftbodyShaders project (build.ps1 -Check) runs the
// same test in the Unity 2019.4.31 editor, whose compiler knows no #nullable, while the fork builds with nullable checks on.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Softbody.FluidGpu
{
    public static class SmokeSelfTest
    {
        /// <summary>The render texture's side, in pixels.</summary>
        const int Size = 32;
        /// <summary>The test puff's opacity. Its material copy draws it with no opacity boost, no wisps and no fade near the eye, so the
        /// middle of the picture holds about this much.</summary>
        public const float PuffAlpha = 0.5f;
        /// <summary>Far below any world, so that nothing else is in front of the test camera.</summary>
        static readonly Vector3 Far = new Vector3(0f, -20000f, 0f);

        static volatile bool _tripped;
        static volatile string _trippedBy = "";
        static bool _hooked;

        /// <summary>Set (from any thread) once the renderer logs that it could not make a shader program, after Watch.</summary>
        public static bool Tripped => _tripped;
        /// <summary>The log line that set Tripped.</summary>
        public static string TrippedBy => _trippedBy;

        /// <summary>Watches the log for the renderer's "could not make this shader program" lines, from now on (clears Tripped). These
        /// lines do not name the shader, but the game's own shaders are built for its renderer, so in practice they come from ours.</summary>
        public static void Watch()
        {
            _trippedBy = "";
            _tripped = false;
            if (_hooked) return;
            _hooked = true;
            Application.logMessageReceivedThreaded += OnLog;
        }

        public static void StopWatching()
        {
            if (!_hooked) return;
            _hooked = false;
            Application.logMessageReceivedThreaded -= OnLog;
        }

        // any thread: no Unity calls here
        static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (_tripped || condition == null) return;
            if (condition.IndexOf("D3D shader create error", StringComparison.Ordinal) < 0
                && condition.IndexOf("ShaderProgram is unsupported", StringComparison.Ordinal) < 0) return;
            _trippedBy = condition.Length > 160 ? condition.Substring(0, 160) : condition.Trim();
            _tripped = true;
        }

        /// <summary>Main thread. Draws one puff with a copy of the material into a small render texture and reads it back. The puff is
        /// 2 m in front of a camera of its own, far below the world, which sees only the given layer (in the renderer: its Temp
        /// layer, which no other camera draws), and only this camera draws the puff. The middle of the picture should hold the puff's
        /// opacity, the corners nothing, and the log no "could not make the program" line meanwhile (it starts Watch; the
        /// caller keeps watching after a pass, or calls StopWatching).</summary>
        public static bool Run(Material material, int layer, out string result)
        {
            if (material == null || material.shader == null) { result = "no material"; return false; }
            if (layer < 0 || layer > 31) layer = 31;
            var made = new List<UnityEngine.Object>();   // all destroyed at the end
            var active = RenderTexture.active;
            Watch();
            try
            {
                var go = new GameObject("Softbody smoke self-test") { hideFlags = HideFlags.HideAndDontSave };
                made.Add(go);
                go.transform.SetPositionAndRotation(Far, Quaternion.identity);
                var cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.cullingMask = 1 << layer;
                cam.orthographic = false;
                cam.fieldOfView = 60f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 5f;
                cam.renderingPath = RenderingPath.Forward;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.useOcclusionCulling = false;
                cam.depthTextureMode = DepthTextureMode.None;
                var rt = new RenderTexture(Size, Size, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    name = "Softbody smoke self-test", hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1,
                };
                made.Add(rt);
                rt.Create();
                cam.targetTexture = rt;
                cam.aspect = 1f;

                // one puff, as the GPU writes them: 4 vertices at its centre; normal x its radius, y the light reaching it, z the sky;
                // the colour's alpha its opacity; texcoord0 xy the corner, z its random number, w its age
                var mesh = new Mesh { name = "Softbody smoke self-test", hideFlags = HideFlags.HideAndDontSave };
                made.Add(mesh);
                mesh.vertices = new[] { Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero };
                var n = new Vector3(0.5f, 1f, 1f);
                mesh.normals = new[] { n, n, n, n };
                var c = new Color(1f, 1f, 1f, PuffAlpha);
                mesh.colors = new[] { c, c, c, c };
                mesh.SetUVs(0, new List<Vector4>
                {
                    new Vector4(-1f, -1f, 0.37f, 0.5f), new Vector4(1f, -1f, 0.37f, 0.5f),
                    new Vector4(1f, 1f, 0.37f, 0.5f), new Vector4(-1f, 1f, 0.37f, 0.5f),
                });
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                mesh.bounds = new Bounds(Vector3.zero, new Vector3(2f, 2f, 2f));

                var mat = new Material(material) { name = "Softbody smoke self-test", hideFlags = HideFlags.HideAndDontSave };
                made.Add(mat);
                mat.SetFloat("_Density", 1f);
                mat.SetFloat("_NoiseAmount", 0f);
                mat.SetFloat("_NearFade", 0f);
                Shader.SetGlobalFloat("_SoftbodyDepthOK", 0f);   // no depth texture here (the host sets it again before each camera)

                Graphics.DrawMesh(mesh, Matrix4x4.TRS(Far + new Vector3(0f, 0f, 2f), Quaternion.identity, Vector3.one), mat, layer, cam, 0, null,
                    ShadowCastingMode.Off, false, null, LightProbeUsage.Off, null);
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
                made.Add(tex);
                tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0, false);
                var px = tex.GetPixels32();
                int h = Size / 2;
                float middle = Mathf.Max(Mathf.Max(px[(h - 1) * Size + h - 1].a, px[(h - 1) * Size + h].a), Mathf.Max(px[h * Size + h - 1].a, px[h * Size + h].a)) / 255f;
                float corner = Mathf.Max(Mathf.Max(px[0].a, px[Size - 1].a), Mathf.Max(px[(Size - 1) * Size].a, px[Size * Size - 1].a)) / 255f;
                bool ok = middle > PuffAlpha - 0.2f && middle < PuffAlpha + 0.2f && corner < 0.05f && !_tripped;
                result = "test puff: " + middle.ToString("0.00") + " in the middle (" + PuffAlpha.ToString("0.00") + " expected), "
                         + corner.ToString("0.00") + " in the corners" + (_tripped ? "; the renderer logged \"" + _trippedBy + "\"" : "");
                return ok;
            }
            catch (Exception ex)
            {
                result = "the test failed: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                RenderTexture.active = active;
                foreach (var o in made)
                {
                    if (o is RenderTexture r) r.Release();
                    Dispose(o);
                }
            }
        }

        static void Dispose(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
