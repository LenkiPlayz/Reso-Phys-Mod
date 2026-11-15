// Builds the pacing probe player (Assets/SoftbodyGI/PacingProbe: where in Unity's frame the voxel camera's draw waits). The project's
// player settings are changed for this build only (no VR, a window) and put back after. C# 7.3.
using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class GiPacingBuild
{
    const string Dir = "Assets/SoftbodyGI/PacingProbe";

    public static void Build()
    {
        try
        {
            string exe = null;
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < a.Length; i++) if (a[i] == "-giPacingOut") exe = a[i + 1];
            if (string.IsNullOrEmpty(exe)) throw new ArgumentException("-giPacingOut is missing");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var probe = new GameObject("pacing probe").AddComponent<GiPacingProbe>();
            probe.voxelize = AssetDatabase.LoadAssetAtPath<Shader>("Assets/SoftbodyGI/Shaders/SoftbodyGIVoxelize.shader");
            if (probe.voxelize == null) throw new InvalidOperationException("no voxelize shader in the project (run build_gi.ps1's copy step)");
            string matPath = Dir + "/PacingWhite.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard")) { color = new Color(0.75f, 0.75f, 0.75f) };
                AssetDatabase.CreateAsset(mat, matPath);
            }
            probe.white = mat;
            string scenePath = Dir + "/PacingProbe.unity";
            EditorSceneManager.SaveScene(scene, scenePath);

#pragma warning disable 618
            bool vr = PlayerSettings.virtualRealitySupported;
#pragma warning restore 618
            var mode = PlayerSettings.fullScreenMode;
            int w = PlayerSettings.defaultScreenWidth, h = PlayerSettings.defaultScreenHeight;
            bool background = PlayerSettings.runInBackground;
            try
            {
#pragma warning disable 618
                PlayerSettings.virtualRealitySupported = false;
#pragma warning restore 618
                PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
                PlayerSettings.defaultScreenWidth = 1280;
                PlayerSettings.defaultScreenHeight = 720;
                PlayerSettings.runInBackground = true;
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scenePath },
                    locationPathName = exe,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None,
                });
                if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("the player build " + report.summary.result);
            }
            finally
            {
#pragma warning disable 618
                PlayerSettings.virtualRealitySupported = vr;
#pragma warning restore 618
                PlayerSettings.fullScreenMode = mode;
                PlayerSettings.defaultScreenWidth = w;
                PlayerSettings.defaultScreenHeight = h;
                PlayerSettings.runInBackground = background;
                AssetDatabase.SaveAssets();
            }
            Debug.Log("SOFTBODY_GI_PACING_BUILD OK " + exe);
        }
        catch (Exception ex)
        {
            Debug.LogError("SOFTBODY_GI_PACING_BUILD FAILED " + ex);
            EditorApplication.Exit(1);
        }
    }
}
