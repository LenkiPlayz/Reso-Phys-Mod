using System;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace SoftbodyRenderer
{
    /// <summary>
    /// SoftbodyRenderer: lets the Softbody plugin keep an avatar's mesh GPU-skinned while it wobbles. The engine-side plugin
    /// writes bind-space positions and normals into its private mesh copy and sends a true partial upload (vertex streams
    /// only); this patch makes Renderite's MeshAsset.Upload apply such an upload to the existing skinned Unity mesh in place
    /// (see <see cref="MeshUploadPatch"/>), so Unity keeps its bind poses, bone weights and blendshapes and skins the new base
    /// on the GPU.
    ///
    /// While the patch is active the plugin holds a named event open (<see cref="UploadPatchSpec.HandshakeName"/>): the
    /// engine-side plugin checks for it before it sends any vertex-only upload on a skinned mesh, so without this plugin
    /// the stock renderer never receives one.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "softbody.renderer";
        public const string Name = "SoftbodyRenderer";
        public const string Version = "0.9.2";

        internal static ManualLogSource Log;
        static EventWaitHandle _handshake;   // kept open for the renderer process's lifetime

        void Awake()
        {
            Log = Logger;
            var enabled = Config.Bind("General", "Enabled", true,
                "Patch MeshAsset.Upload so Softbody can update GPU-skinned meshes in place. Off: the stock renderer, and Softbody uses CPU skinning.");
            var notify = Config.Bind("General", "NotifyRenderers", true,
                "After an in-place update, call Mesh.MarkModified so SkinnedMeshRenderers re-skin even when no bone moved.");
            if (!enabled.Value)
            {
                Log.LogInfo($"{Name} {Version}: disabled in the config, MeshAsset.Upload not patched");
                return;
            }
            try
            {
                string why = MeshUploadPatch.Install(new Harmony(Guid), notify.Value, out var detail);
                if (why != null)
                {
                    Log.LogWarning($"{Name} {Version}: {why}{(detail != null ? " [" + detail + "]" : "")}. Softbody will use CPU skinning.");
                    return;
                }
                Log.LogInfo($"{Name} {Version} patched MeshAsset.Upload");
                Log.LogInfo($"  verified {detail}; MarkModified after in-place updates: {notify.Value}");
                try
                {
                    _handshake = new EventWaitHandle(false, EventResetMode.ManualReset, UploadPatchSpec.HandshakeName);
                    Log.LogInfo($"  handshake event '{UploadPatchSpec.HandshakeName}' open: Softbody's GPU path is enabled for this renderer");
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"  could not open the handshake event ({ex.GetType().Name}: {ex.Message}); Softbody will not use its GPU path " +
                                   "unless the engine side is started with SOFTBODY_GPU_SKIN=probe");
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"{Name} {Version}: patching MeshAsset.Upload failed, the stock renderer runs: {ex}");
            }
        }
    }
}
