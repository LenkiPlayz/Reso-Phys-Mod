using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Builds the Softbody fluid shader bundle, and runs the GPU checks, from the command line (batch mode).</summary>
public static class SoftbodyFluidBuild
{
    static string Out => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Bundle"));

    public static void Build()
    {
        Directory.CreateDirectory(Out);
        var assets = AssetDatabase.FindAssets("", new[] { "Assets/SoftbodyFluid/Shaders" })
            .Select(AssetDatabase.GUIDToAssetPath).Where(p => p.EndsWith(".compute") || p.EndsWith(".shader")).Distinct().ToArray();
        var build = new AssetBundleBuild { assetBundleName = "softbody_fluid", assetNames = assets };
        var manifest = BuildPipeline.BuildAssetBundles(Out, new[] { build }, BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);
        Debug.Log("[SoftbodyFluid] built " + (manifest != null ? "ok" : "FAILED") + ": " + string.Join(", ", assets) + " -> " + Out);
        if (manifest == null) EditorApplication.Exit(1);
    }

    /// <summary>Dispatches the probe kernel on this machine's GPU and checks the result.</summary>
    public static void Probe()
    {
        var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/SoftbodyFluid/Shaders/Probe.compute");
        var buf = new ComputeBuffer(256, 4);
        int k = cs.FindKernel("Probe");
        cs.SetBuffer(k, "_Out", buf);
        cs.Dispatch(k, 4, 1, 1);
        var data = new float[256];
        buf.GetData(data);
        buf.Release();
        bool ok = Enumerable.Range(0, 256).All(i => Math.Abs(data[i] - 2 * i) < 1e-3f);
        Debug.Log($"[SoftbodyFluid] probe on {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}): {(ok ? "ok" : "WRONG")} ({data[3]}, {data[255]})");
        if (!ok) EditorApplication.Exit(1);
    }
}
