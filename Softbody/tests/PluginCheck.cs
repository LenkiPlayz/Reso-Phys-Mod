using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Softbody.Core;

namespace Softbody.Tests;

/// <summary>
/// Offline checks of the built plugin DLL (bin/Release/net10.0/Softbody.dll), without starting Resonite:
///  1. Cecil: [assembly: DataModelAssembly(Core)], the Softbody component's base class, category, Sync fields,
///     status RawOutputs, nested enum, overridden callbacks, no ResoniteModLoader/Harmony dependency; the paint
///     dab sync object; the SoftbodyBrush tool (a FrooxEngine.Tool) with the overrides the brush relies on.
///  2. The engine's own startup weaver (FrooxEngine.Weaver.AssemblyPostProcessor.Process, which Resonite runs on
///     every -LoadAssembly DLL before loading it) run on a COPY in this test's bin folder; checks its output.
///  3. The engine's own registration code (FrooxEngine.AssemblyTypeRegistry, as EngineInitializer uses it):
///     which types it registers and the compatibility hash Resonite will log for the assembly.
/// The game folder is only read (DLLs are loaded from it); the weaver writes only the copy.
/// </summary>
static class PluginCheck
{
    static string Game, Dll;

    static readonly (string Name, string Type)[] ExpectedFields =
    {
        ("Renderer", "FrooxEngine.SyncRef`1<FrooxEngine.MeshRenderer>"),
        ("SoftnessChannel", "FrooxEngine.Sync`1<Softbody.Softbody/Channel>"),
        ("InvertSoftness", "FrooxEngine.Sync`1<System.Boolean>"),
        ("StartAs", "FrooxEngine.Sync`1<Softbody.Softbody/StartColor>"),
        ("Cells", "FrooxEngine.Sync`1<System.Int32>"),
        ("Pin", "FrooxEngine.Sync`1<System.Single>"),
        ("Radius", "FrooxEngine.Sync`1<System.Single>"),
        ("Stiffness", "FrooxEngine.Sync`1<System.Single>"),
        ("Volume", "FrooxEngine.Sync`1<System.Single>"),
        ("Damping", "FrooxEngine.Sync`1<System.Single>"),
        ("Follow", "FrooxEngine.Sync`1<System.Single>"),
        ("Firm", "FrooxEngine.Sync`1<System.Single>"),
        ("Mass", "FrooxEngine.Sync`1<System.Single>"),
        ("Gravity", "FrooxEngine.Sync`1<System.Single>"),
        ("Drag", "FrooxEngine.Sync`1<System.Single>"),
        ("Friction", "FrooxEngine.Sync`1<System.Single>"),
        ("Iterations", "FrooxEngine.Sync`1<System.Int32>"),
        ("Collide", "FrooxEngine.Sync`1<System.Boolean>"),
        ("Budget", "FrooxEngine.Sync`1<System.Single>"),
        ("DebugCells", "FrooxEngine.Sync`1<System.Boolean>"),
        ("DebugColliders", "FrooxEngine.Sync`1<System.Boolean>"),
        ("Red", "Softbody.Softbody/HueBand"),
        ("Orange", "Softbody.Softbody/HueBand"),
        ("Yellow", "Softbody.Softbody/HueBand"),
        ("Green", "Softbody.Softbody/HueBand"),
        ("Blue", "Softbody.Softbody/HueBand"),
        ("Indigo", "Softbody.Softbody/HueBand"),
        ("Violet", "Softbody.Softbody/HueBand"),
        ("Paint", "FrooxEngine.SyncList`1<Softbody.Softbody/PaintDab>"),
        ("Status", "FrooxEngine.RawOutput`1<System.String>"),
        ("Points", "FrooxEngine.RawOutput`1<System.Int32>"),
        ("Vertices", "FrooxEngine.RawOutput`1<System.Int32>"),
        ("MovingVertices", "FrooxEngine.RawOutput`1<System.Int32>"),
        ("SimMs", "FrooxEngine.RawOutput`1<System.Single>"),
        ("FrameMs", "FrooxEngine.RawOutput`1<System.Single>"),
        ("Resets", "FrooxEngine.RawOutput`1<System.Int32>"),
        ("Tunes", "FrooxEngine.RawOutput`1<System.Int32>"),
        ("Dabs", "FrooxEngine.RawOutput`1<System.Int32>"),
        ("Effective", "FrooxEngine.RawOutput`1<System.String>"),
        ("LastError", "FrooxEngine.RawOutput`1<System.String>"),
    };

    // 0.11.0: the skinned flesh component (tet shell + XPBD)
    // 0.15.0: the liquid emitter's members, in inspector order
    static readonly (string Name, string Type)[] ExpectedFluidFields =
    {
        ("Mode", "FrooxEngine.Sync`1<Softbody.FluidEmitter/EmitMode>"),
        ("Pouring", "FrooxEngine.Sync`1<System.Boolean>"),
        ("Rate", "FrooxEngine.Sync`1<System.Single>"),
        ("Speed", "FrooxEngine.Sync`1<System.Single>"),
        ("NozzleRadius", "FrooxEngine.Sync`1<System.Single>"),
        ("VolumeSize", "FrooxEngine.Sync`1<Elements.Core.float3>"),
        // 0.19.0
        ("RandomVelocity", "FrooxEngine.Sync`1<System.Single>"),
        ("MaxParticles", "FrooxEngine.Sync`1<System.Int32>"),
        ("Viscosity", "FrooxEngine.Sync`1<System.Single>"),
        // 0.27.0
        ("ThickLiquid", "FrooxEngine.Sync`1<System.Boolean>"),
        ("Cohesion", "FrooxEngine.Sync`1<System.Single>"),
        ("Adhesion", "FrooxEngine.Sync`1<System.Single>"),
        ("Friction", "FrooxEngine.Sync`1<System.Single>"),
        ("Density", "FrooxEngine.Sync`1<System.Single>"),
        ("Gravity", "FrooxEngine.Sync`1<System.Single>"),
        ("Damping", "FrooxEngine.Sync`1<System.Single>"),
        ("Lifetime", "FrooxEngine.Sync`1<System.Single>"),
        ("Color", "FrooxEngine.Sync`1<Elements.Core.colorX>"),
        // 0.27.0
        ("Foam", "FrooxEngine.Sync`1<System.Single>"),
        ("FoamLifetime", "FrooxEngine.Sync`1<System.Single>"),
        ("Collide", "FrooxEngine.Sync`1<System.Boolean>"),
        ("CollideHead", "FrooxEngine.Sync`1<System.Boolean>"),
        ("CollideBody", "FrooxEngine.Sync`1<System.Boolean>"),
        ("CollideHands", "FrooxEngine.Sync`1<System.Boolean>"),
        ("CullDistance", "FrooxEngine.Sync`1<System.Single>"),
        // 0.16.0
        ("Solver", "FrooxEngine.Sync`1<Softbody.FluidEmitter/SolverMode>"),
        // 0.17.0
        ("ParticleSize", "FrooxEngine.Sync`1<System.Single>"),
        ("SurfaceDetail", "FrooxEngine.Sync`1<System.Single>"),
        // 0.18.0
        ("DropletSize", "FrooxEngine.Sync`1<System.Single>"),
        ("RefillCount", "FrooxEngine.Sync`1<System.Int32>"),
        ("ClearCount", "FrooxEngine.Sync`1<System.Int32>"),
        ("Status", "FrooxEngine.RawOutput`1<System.String>"),
        ("Particles", "FrooxEngine.RawOutput`1<System.Int32>"),
        ("WorldParticles", "FrooxEngine.RawOutput`1<System.Int32>"),
        ("StepMs", "FrooxEngine.RawOutput`1<System.Single>"),
        ("SurfaceMs", "FrooxEngine.RawOutput`1<System.Single>"),
    };

    /// <summary>0.27.0: the compatibility hash the engine computes for this version (it changes only with the synced members).</summary>
    const string ExpectedHash = "0/uDXxcV+UVEn6+RnAsMVg==";

    // 0.26.0: the gas emitter's members, in inspector order
    static readonly (string Name, string Type)[] ExpectedGasFields =
    {
        ("Emitting", "FrooxEngine.Sync`1<System.Boolean>"),
        ("Rate", "FrooxEngine.Sync`1<System.Single>"),
        ("Speed", "FrooxEngine.Sync`1<System.Single>"),
        ("NozzleRadius", "FrooxEngine.Sync`1<System.Single>"),
        ("Spread", "FrooxEngine.Sync`1<System.Single>"),
        ("Lifetime", "FrooxEngine.Sync`1<System.Single>"),
        ("Buoyancy", "FrooxEngine.Sync`1<System.Single>"),
        ("Drag", "FrooxEngine.Sync`1<System.Single>"),
        ("Swirl", "FrooxEngine.Sync`1<System.Single>"),
        // 0.27.0
        ("Vortices", "FrooxEngine.Sync`1<System.Single>"),
        ("StartSize", "FrooxEngine.Sync`1<System.Single>"),
        ("EndSize", "FrooxEngine.Sync`1<System.Single>"),
        ("Color", "FrooxEngine.Sync`1<Elements.Core.colorX>"),
        ("Collide", "FrooxEngine.Sync`1<System.Boolean>"),
        ("CollideHead", "FrooxEngine.Sync`1<System.Boolean>"),
        ("CollideBody", "FrooxEngine.Sync`1<System.Boolean>"),
        ("CollideHands", "FrooxEngine.Sync`1<System.Boolean>"),
        ("MaxPuffs", "FrooxEngine.Sync`1<System.Int32>"),
        ("CullDistance", "FrooxEngine.Sync`1<System.Single>"),
        ("Status", "FrooxEngine.RawOutput`1<System.String>"),
        ("WorldPuffs", "FrooxEngine.RawOutput`1<System.Int32>"),
    };

    static readonly (string Name, string Type)[] ExpectedSkinnedFields =
    {
        ("Renderer", "FrooxEngine.SyncRef`1<FrooxEngine.SkinnedMeshRenderer>"),
        ("SoftnessChannel", "FrooxEngine.Sync`1<Softbody.Softbody/Channel>"),
        ("InvertSoftness", "FrooxEngine.Sync`1<System.Boolean>"),
        ("StartAs", "FrooxEngine.Sync`1<Softbody.Softbody/StartColor>"),
        ("Simulating", "FrooxEngine.Sync`1<System.Single>"),
        ("SimPoints", "FrooxEngine.Sync`1<System.Int32>"),
        ("Depth", "FrooxEngine.Sync`1<System.Single>"),
        ("Layers", "FrooxEngine.Sync`1<System.Int32>"),
        ("Stiffness", "FrooxEngine.Sync`1<System.Single>"),
        ("Volume", "FrooxEngine.Sync`1<System.Single>"),
        ("VolumePreservation", "FrooxEngine.Sync`1<System.Single>"),
        ("BoneDensity", "FrooxEngine.Sync`1<System.Single>"),
        ("Follow", "FrooxEngine.Sync`1<System.Single>"),
        ("Damping", "FrooxEngine.Sync`1<System.Single>"),
        ("Gravity", "FrooxEngine.Sync`1<System.Single>"),
        ("BlendshapeInertia", "FrooxEngine.Sync`1<System.Single>"),
        ("Substeps", "FrooxEngine.Sync`1<System.Int32>"),
        ("Collide", "FrooxEngine.Sync`1<System.Boolean>"),
        ("CollideSelf", "FrooxEngine.Sync`1<System.Boolean>"),
        ("SelfHead", "FrooxEngine.Sync`1<System.Boolean>"),
        ("SelfBody", "FrooxEngine.Sync`1<System.Boolean>"),
        ("SelfHands", "FrooxEngine.Sync`1<System.Boolean>"),
        ("CollideOthers", "FrooxEngine.Sync`1<System.Boolean>"),
        ("OthersHead", "FrooxEngine.Sync`1<System.Boolean>"),
        ("OthersBody", "FrooxEngine.Sync`1<System.Boolean>"),
        ("OthersHands", "FrooxEngine.Sync`1<System.Boolean>"),
        ("Budget", "FrooxEngine.Sync`1<System.Single>"),
        ("CullDistance", "FrooxEngine.Sync`1<System.Single>"),
        ("HueLayers", "FrooxEngine.SyncList`1<Softbody.SkinnedSoftbody/HueLayer>"),
        ("DebugTets", "FrooxEngine.Sync`1<System.Boolean>"),
        ("DebugColliders", "FrooxEngine.Sync`1<System.Boolean>"),
        ("Paint", "FrooxEngine.SyncList`1<Softbody.Softbody/PaintDab>"),
        ("Status", "FrooxEngine.RawOutput`1<System.String>"),
        ("Points", "FrooxEngine.RawOutput`1<System.Int32>"),
        ("Tets", "FrooxEngine.RawOutput`1<System.Int32>"),
        ("MovingVertices", "FrooxEngine.RawOutput`1<System.Int32>"),
        ("StepMs", "FrooxEngine.RawOutput`1<System.Single>"),
        ("FrameMs", "FrooxEngine.RawOutput`1<System.Single>"),
        ("Dabs", "FrooxEngine.RawOutput`1<System.Int32>"),
        ("LastError", "FrooxEngine.RawOutput`1<System.String>"),
    };

    static readonly string[] ExpectedOverrides =
        { "OnAwake", "OnAttach", "OnStart", "OnChanges", "OnCommonUpdate", "OnEnabled", "OnDisabled", "OnActivated", "OnDeactivated", "OnDestroy", "OnWorldDestroy", "OnFocusChanged" };

    static readonly (string Name, string Type)[] ExpectedDabFields =
    {
        ("Position", "FrooxEngine.Sync`1<Elements.Core.float3>"),
        ("Radius", "FrooxEngine.Sync`1<System.Single>"),
        ("Color", "FrooxEngine.Sync`1<Elements.Core.colorX>"),
        ("Strength", "FrooxEngine.Sync`1<System.Single>"),
        ("Hardness", "FrooxEngine.Sync`1<System.Single>"),
        ("Blur", "FrooxEngine.Sync`1<System.Boolean>"),
    };

    // public settings + RawOutput, then the protected visual drives (all sync members, in declaration order)
    static readonly (string Name, string Type)[] ExpectedBrushFields =
    {
        ("Color", "FrooxEngine.Sync`1<Elements.Core.colorX>"),
        ("Hue", "FrooxEngine.Sync`1<Softbody.SoftbodyBrush/PaintHue>"),
        ("Radius", "FrooxEngine.Sync`1<System.Single>"),
        ("Strength", "FrooxEngine.Sync`1<System.Single>"),
        ("Hardness", "FrooxEngine.Sync`1<System.Single>"),
        ("Mode", "FrooxEngine.Sync`1<Softbody.SoftbodyBrush/BrushMode>"),
        ("Spacing", "FrooxEngine.Sync`1<System.Single>"),
        ("Status", "FrooxEngine.RawOutput`1<System.String>"),
        ("_radiusDrives", "FrooxEngine.SyncList`1<FrooxEngine.FieldDrive`1<System.Single>>"),
        ("_fillDrives", "FrooxEngine.SyncList`1<FrooxEngine.FieldDrive`1<Elements.Core.colorX>>"),
        ("_rimDrives", "FrooxEngine.SyncList`1<FrooxEngine.FieldDrive`1<Elements.Core.colorX>>"),
        ("_markerPosition", "FrooxEngine.FieldDrive`1<Elements.Core.float3>"),
        ("_markerActive", "FrooxEngine.FieldDrive`1<System.Boolean>"),
    };

    // FrooxEngine.Tool / ITool members the brush overrides and relies on (see SoftbodyBrush and README)
    static readonly string[] ExpectedBrushOverrides =
    {
        "OnAwake", "OnAttach", "OnChanges", "OnCommonUpdate", "get_UsesLaser", "get_CanUseWhenHolding", "get_UsesSecondary",
        "get_PrimaryDescription", "get_SecondaryDescription", "OverrideTargetPoint", "Update", "OnPrimaryPress", "OnPrimaryHold",
        "OnPrimaryRelease", "OnSecondaryPress", "OnDequipped", "GenerateMenuItems",
    };

    public static void Run(Action<bool, string> check)
    {
        var meta = typeof(PluginCheck).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        Dll = Path.GetFullPath(meta["PluginDll"]);
        Game = meta["ResonitePath"];
        Console.WriteLine("TEST plugin DLL (offline): " + Dll);
        if (!File.Exists(Dll)) { check(false, "plugin DLL exists (dotnet build -c Release in Softbody/)"); return; }

        try { CecilChecks(check); }
        catch (Exception ex) { check(false, "Cecil checks ran: " + ex.Message); }

        string woven = null;
        try { woven = Weave(check); }
        catch (Exception ex) { check(false, "weaver dry run ran: " + (ex.InnerException ?? ex).Message); }

        try { RegistryChecks(woven, check); }
        catch (Exception ex) { check(false, "engine registration dry run ran: " + (ex.InnerException ?? ex)); }
    }

    // ------------------------------------------------------------------ 1. Cecil

    static void CecilChecks(Action<bool, string> check)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Game); // FrooxEngine & co. (read-only), to resolve the Tool base class
        using var asm = AssemblyDefinition.ReadAssembly(Dll, new ReaderParameters { AssemblyResolver = resolver });
        var module = asm.MainModule;
        Console.WriteLine($"  assembly {asm.Name.FullName}");

        var dma = asm.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "Elements.Data.DataModelAssemblyAttribute");
        check(dma != null && dma.ConstructorArguments.Count == 1 && Convert.ToInt32(dma.ConstructorArguments[0].Value) == 0,
              "[assembly: DataModelAssembly(DataModelAssemblyType.Core)]");
        check(asm.Name.Name == "Softbody" && asm.Name.Version == new Version(0, 27, 0, 0), "assembly name Softbody, version 0.27.0");
        var refs = module.AssemblyReferences.Select(r => r.Name).ToList();
        Console.WriteLine($"  references: {string.Join(", ", refs)}");
        check(!refs.Any(r => r.Contains("ResoniteModLoader", StringComparison.OrdinalIgnoreCase) || r.Contains("Harmony", StringComparison.OrdinalIgnoreCase)),
              "no ResoniteModLoader / Harmony dependency");

        var t = module.GetType("Softbody.Softbody");
        check(t != null && t.IsPublic && !t.IsAbstract && t.BaseType?.FullName == "FrooxEngine.Component", "public class Softbody.Softbody : FrooxEngine.Component");
        if (t == null) return;
        check(t.Methods.Any(m => m.IsConstructor && !m.IsStatic && m.IsPublic && m.Parameters.Count == 0), "has a public parameterless constructor");
        var cat = t.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "FrooxEngine.CategoryAttribute");
        var paths = cat?.ConstructorArguments[0].Value is CustomAttributeArgument[] arr ? arr.Select(x => (string)x.Value).ToArray() : Array.Empty<string>();
        Console.WriteLine($"  [Category({string.Join(", ", paths)})]");
        check(paths.SequenceEqual(new[] { "Physics" }), "[Category(\"Physics\")]");
        var uo = t.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "FrooxEngine.DefaultUpdateOrder");
        check(uo != null && Convert.ToInt32(uo.ConstructorArguments[0].Value) == 1000,
              "[DefaultUpdateOrder(1000)]: runs after default-order components (Animator) and VRIK (-5000), so a skinned cage sees this frame's pose");

        var fields = t.Fields.Where(f => !f.IsStatic && f.IsPublic).ToList();
        var got = fields.Select(f => (Name: f.Name, Type: f.FieldType.FullName)).ToList();
        bool same = got.SequenceEqual(ExpectedFields);
        if (!same)
            foreach (var e in ExpectedFields.Except(got).Concat(got.Except(ExpectedFields))) Console.WriteLine($"    mismatch: {e.Name} {e.Type}");
        Console.WriteLine($"  {fields.Count(f => f.FieldType.FullName.StartsWith("FrooxEngine.Sync"))} Sync fields, " +
                          $"{fields.Count(f => f.FieldType.FullName.StartsWith("FrooxEngine.RawOutput"))} RawOutput status fields");
        check(same, $"public fields are exactly the {ExpectedFields.Length} expected Sync/SyncRef/RawOutput members, in inspector order");
        check(fields.All(f => f.IsInitOnly), "all sync members are readonly (the weaver and WorkerInitializer require it)");

        var ch = t.NestedTypes.FirstOrDefault(n => n.Name == "Channel");
        var values = ch?.Fields.Where(f => f.IsStatic && f.HasConstant).Select(f => $"{f.Name}={f.Constant}").ToArray() ?? Array.Empty<string>();
        check(ch != null && ch.IsEnum && values.SequenceEqual(new[] { "Red=0", "Green=1", "Blue=2", "Alpha=3", "Value=4" }), "nested enum Softbody.Channel { Red, Green, Blue, Alpha, Value }");
        check(values.Select(v => v.Split('=')).All(kv => Enum.TryParse<SoftnessSource>(kv[0], out var src) && (int)src == int.Parse(kv[1])),
              "Channel values match Core.SoftnessSource one to one (the plugin casts between them)");

        var overrides = t.Methods.Where(m => m.IsVirtual && !m.IsNewSlot).Select(m => m.Name).ToHashSet();
        var missing = ExpectedOverrides.Where(o => !overrides.Contains(o)).ToList();
        check(missing.Count == 0, "overrides " + string.Join(", ", ExpectedOverrides) + (missing.Count > 0 ? " (missing " + string.Join(", ", missing) + ")" : ""));
        var guarded = new[] { "OnAttach", "OnStart", "OnChanges", "OnCommonUpdate", "Guarded", "Report", "ClearPaint" };
        var unguarded = guarded.Where(n => t.Methods.FirstOrDefault(m => m.Name == n) is not { HasBody: true } m || !m.Body.HasExceptionHandlers).ToList();
        check(unguarded.Count == 0, "engine callbacks that do work catch their exceptions (" + string.Join(", ", guarded) + ")" +
              (unguarded.Count > 0 ? " - no handler in " + string.Join(", ", unguarded) : ""));

        // ---- paint (0.4.0)
        var paint = t.Fields.FirstOrDefault(f => f.Name == "Paint");
        check(paint != null && paint.CustomAttributes.Any(a => a.AttributeType.FullName == "FrooxEngine.HideInInspectorAttribute"),
              "Paint list is [HideInInspector] (it can hold thousands of dabs; the Dabs status shows the count)");
        var clear = t.Methods.FirstOrDefault(m => m.Name == "ClearPaint" && m.Parameters.Count == 0 && m.IsPublic);
        check(clear != null && clear.CustomAttributes.Any(a => a.AttributeType.FullName == "FrooxEngine.SyncMethod"),
              "public [SyncMethod] ClearPaint() (inspector button, ResoniteLink callSyncMethod)");
        var dab = t.NestedTypes.FirstOrDefault(n => n.Name == "PaintDab");
        var dabFields = dab?.Fields.Where(f => !f.IsStatic).Select(f => (f.Name, f.FieldType.FullName)).ToList() ?? new();
        check(dab != null && dab.IsNestedPublic && dab.BaseType?.FullName == "FrooxEngine.SyncObject" && dabFields.SequenceEqual(ExpectedDabFields) &&
              dab.Fields.Where(f => !f.IsStatic).All(f => f.IsInitOnly && f.IsPublic),
              "nested public class PaintDab : SyncObject { readonly Sync Position (float3), Radius, Color (colorX), Strength, Hardness, Blur }");

        // ---- the debug views' mesh (0.10.0)
        var dm = module.GetType("Softbody.SoftbodyDebugMesh");
        check(dm != null && dm.IsPublic && !dm.IsAbstract && dm.BaseType?.FullName == "FrooxEngine.ProceduralMesh" &&
              dm.Methods.Any(m => m.IsConstructor && !m.IsStatic && m.IsPublic && m.Parameters.Count == 0),
              "public class Softbody.SoftbodyDebugMesh : FrooxEngine.ProceduralMesh, with a public parameterless constructor");
        var dmOver = dm?.Methods.Where(m => m.IsVirtual && !m.IsNewSlot).Select(m => m.Name).ToHashSet() ?? new HashSet<string>();
        check(new[] { "PrepareAssetUpdateData", "ClearMeshData", "UpdateMeshData" }.All(dmOver.Contains),
              "the debug mesh overrides PrepareAssetUpdateData, ClearMeshData and UpdateMeshData");
        check(dm != null && !dm.Fields.Any(f => !f.IsStatic && f.FieldType.FullName.StartsWith("FrooxEngine.")),
              "the debug mesh has no sync members of its own (it lives on a local slot; everything it shows is local state)");

        // ---- the brush tool
        var b = module.GetType("Softbody.SoftbodyBrush");
        check(b != null && b.IsPublic && !b.IsAbstract && b.BaseType?.FullName == "FrooxEngine.Tool", "public class Softbody.SoftbodyBrush : FrooxEngine.Tool");
        if (b == null) return;
        check(b.Methods.Any(m => m.IsConstructor && !m.IsStatic && m.IsPublic && m.Parameters.Count == 0), "the brush has a public parameterless constructor");
        var bcat = b.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "FrooxEngine.CategoryAttribute");
        var bpaths = bcat?.ConstructorArguments[0].Value is CustomAttributeArgument[] barr ? barr.Select(x => (string)x.Value).ToArray() : Array.Empty<string>();
        check(bpaths.SequenceEqual(new[] { "Tools" }), "[Category(\"Tools\")] on the brush");
        var bfields = b.Fields.Where(f => !f.IsStatic && (f.IsPublic || f.IsFamily)).Select(f => (Name: f.Name, Type: f.FieldType.FullName)).ToList();
        bool bsame = bfields.SequenceEqual(ExpectedBrushFields);
        if (!bsame) foreach (var e in ExpectedBrushFields.Except(bfields).Concat(bfields.Except(ExpectedBrushFields))) Console.WriteLine($"    brush mismatch: {e.Name} {e.Type}");
        check(bsame && b.Fields.Where(f => !f.IsStatic && (f.IsPublic || f.IsFamily)).All(f => f.IsInitOnly),
              $"brush sync members are exactly the {ExpectedBrushFields.Length} expected ones, readonly, in inspector order");
        var mode = b.NestedTypes.FirstOrDefault(n => n.Name == "BrushMode");
        var mvals = mode?.Fields.Where(f => f.IsStatic && f.HasConstant).Select(f => $"{f.Name}={f.Constant}").ToArray() ?? Array.Empty<string>();
        check(mode != null && mode.IsEnum && mvals.SequenceEqual(new[] { "Paint=0", "Erase=1", "Pick=2", "Blur=3" }), "nested enum SoftbodyBrush.BrushMode { Paint, Erase, Pick, Blur }");

        // every override must override a virtual of FrooxEngine.Tool (or its bases) with the same signature
        var toolDef = b.BaseType.Resolve();
        var bover = b.Methods.Where(m => m.IsVirtual && !m.IsNewSlot).ToList();
        var bmissing = ExpectedBrushOverrides.Where(o => !bover.Any(m => m.Name == o)).ToList();
        string Sig(MethodReference m) => m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) + ")";
        var baseVirtuals = new HashSet<string>();
        for (var bt = toolDef; bt != null; bt = bt.BaseType?.Resolve())
            foreach (var m in bt.Methods.Where(m => m.IsVirtual)) baseVirtuals.Add(Sig(m));
        var orphan = bover.Where(m => !baseVirtuals.Contains(Sig(m))).Select(m => m.Name).ToList();
        Console.WriteLine($"  brush overrides: {string.Join(", ", bover.Select(m => m.Name))}");
        check(bmissing.Count == 0 && orphan.Count == 0, "the brush overrides " + string.Join(", ", ExpectedBrushOverrides) +
              " and each one matches a virtual of FrooxEngine.Tool or its bases" +
              (bmissing.Count > 0 ? " (missing " + string.Join(", ", bmissing) + ")" : "") + (orphan.Count > 0 ? " (no base for " + string.Join(", ", orphan) + ")" : ""));
        var menu = new[] { "OnUndoStroke", "OnClearPaint", "OnSoftPreset", "OnHalfPreset", "OnFirmPreset" };
        check(menu.All(n => b.Methods.Any(m => m.Name == n && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "FrooxEngine.IButton" &&
                                                 m.CustomAttributes.Any(a => a.AttributeType.FullName == "FrooxEngine.SyncMethod"))),
              "context-menu handlers OnUndoStroke/OnClearPaint and the Soft/Half/Firm presets (0.13.0) are [SyncMethod](IButton, ButtonEventData), as ButtonRelay's SyncDelegate needs");
        var bguarded = new[] { "OnAttach", "OnChanges", "OnCommonUpdate", "Update", "OnPrimaryPress", "OnPrimaryHold", "OnSecondaryPress", "OverrideTargetPoint", "GenerateMenuItems", "OnUndoStroke", "OnClearPaint",
                               "OnSoftPreset", "OnHalfPreset", "OnFirmPreset" };
        var bun = bguarded.Where(n => b.Methods.FirstOrDefault(m => m.Name == n) is not { HasBody: true } m || !m.Body.HasExceptionHandlers).ToList();
        check(bun.Count == 0, "brush callbacks catch their exceptions" + (bun.Count > 0 ? " - no handler in " + string.Join(", ", bun) : ""));

        // 0.11.0: SkinnedSoftbody
        var k = module.GetType("Softbody.SkinnedSoftbody");
        check(k != null && k.IsPublic && !k.IsAbstract && k.BaseType?.FullName == "FrooxEngine.Component" && k.Methods.Any(m => m.IsConstructor && !m.IsStatic && m.IsPublic && m.Parameters.Count == 0),
              "public class Softbody.SkinnedSoftbody : FrooxEngine.Component, with a public parameterless constructor");
        if (k == null) return;
        var kcat = k.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "FrooxEngine.CategoryAttribute");
        var kpaths = kcat?.ConstructorArguments[0].Value is CustomAttributeArgument[] karr ? karr.Select(x => (string)x.Value).ToArray() : Array.Empty<string>();
        check(kpaths.SequenceEqual(new[] { "Physics" }), "SkinnedSoftbody is under Physics in the component browser");
        var kfields = k.Fields.Where(f => !f.IsStatic && f.IsPublic).Select(f => (Name: f.Name, Type: f.FieldType.FullName)).ToList();
        bool ksame = kfields.SequenceEqual(ExpectedSkinnedFields);
        if (!ksame) foreach (var e in ExpectedSkinnedFields.Except(kfields).Concat(kfields.Except(ExpectedSkinnedFields))) Console.WriteLine($"    skinned mismatch: {e.Name} {e.Type}");
        check(ksame && k.Fields.Where(f => !f.IsStatic && f.IsPublic).All(f => f.IsInitOnly),
              $"SkinnedSoftbody's public fields are exactly the {ExpectedSkinnedFields.Length} expected readonly Sync/SyncRef/RawOutput members, in inspector order");
        var kover = k.Methods.Where(m => m.IsVirtual).Select(m => m.Name).ToHashSet();
        var kmissing = ExpectedOverrides.Where(o => !kover.Contains(o)).ToList();
        check(kmissing.Count == 0, "SkinnedSoftbody overrides the same lifecycle callbacks as Softbody" + (kmissing.Count > 0 ? " (missing " + string.Join(", ", kmissing) + ")" : ""));
        var kguarded = new[] { "OnAttach", "OnStart", "OnChanges", "OnCommonUpdate", "ClearPaint" };
        var kun = kguarded.Where(n => k.Methods.FirstOrDefault(m => m.Name == n) is not { HasBody: true } m || !m.Body.HasExceptionHandlers).ToList();
        check(kun.Count == 0, "SkinnedSoftbody callbacks catch their exceptions" + (kun.Count > 0 ? " - no handler in " + string.Join(", ", kun) : ""));
        check(k.Methods.Any(m => m.Name == "ClearPaint" && m.CustomAttributes.Any(a => a.AttributeType.FullName == "FrooxEngine.SyncMethod")), "SkinnedSoftbody.ClearPaint is a [SyncMethod] (inspector button, ResoniteLink)");
        // 0.15.0: FluidEmitter
        var fl = module.GetType("Softbody.FluidEmitter");
        check(fl != null && fl.IsPublic && !fl.IsAbstract && fl.BaseType?.FullName == "FrooxEngine.Component" && fl.Methods.Any(m => m.IsConstructor && !m.IsStatic && m.IsPublic && m.Parameters.Count == 0),
              "public class Softbody.FluidEmitter : FrooxEngine.Component, with a public parameterless constructor");
        if (fl != null)
        {
            var flcat = fl.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "FrooxEngine.CategoryAttribute");
            var flpaths = flcat?.ConstructorArguments[0].Value is CustomAttributeArgument[] flarr ? flarr.Select(x => (string)x.Value).ToArray() : Array.Empty<string>();
            check(flpaths.SequenceEqual(new[] { "Physics" }), "FluidEmitter is under Physics in the component browser");
            var flfields = fl.Fields.Where(f => !f.IsStatic && f.IsPublic).Select(f => (Name: f.Name, Type: f.FieldType.FullName)).ToList();
            bool flsame = flfields.SequenceEqual(ExpectedFluidFields);
            if (!flsame) foreach (var e in ExpectedFluidFields.Except(flfields).Concat(flfields.Except(ExpectedFluidFields))) Console.WriteLine($"    fluid mismatch: {e.Name} {e.Type}");
            check(flsame && fl.Fields.Where(f => !f.IsStatic && f.IsPublic).All(f => f.IsInitOnly),
                  $"FluidEmitter's public fields are exactly the {ExpectedFluidFields.Length} expected readonly Sync/RawOutput members, in inspector order");
            var flguarded = new[] { "OnStart", "OnDestroy", "OnCommonUpdate", "Refill", "ClearLiquid" };
            var flun = flguarded.Where(n => fl.Methods.FirstOrDefault(m => m.Name == n) is not { HasBody: true } m || !m.Body.HasExceptionHandlers).ToList();
            check(flun.Count == 0, "FluidEmitter callbacks catch their exceptions" + (flun.Count > 0 ? " - no handler in " + string.Join(", ", flun) : ""));
            check(new[] { "Refill", "ClearLiquid" }.All(n => fl.Methods.Any(m => m.Name == n && m.Parameters.Count == 0 && m.CustomAttributes.Any(a => a.AttributeType.FullName == "FrooxEngine.SyncMethod"))),
                  "FluidEmitter.Refill and ClearLiquid are [SyncMethod]s with no arguments (inspector buttons, ResoniteLink)");
            check(fl.Methods.Any(m => m.Name == "OnAwake"), "FluidEmitter sets its defaults in OnAwake");
        }
        // 0.26.0: GasEmitter
        var ge = module.GetType("Softbody.GasEmitter");
        check(ge != null && ge.IsPublic && !ge.IsAbstract && ge.BaseType?.FullName == "FrooxEngine.Component" && ge.Methods.Any(m => m.IsConstructor && !m.IsStatic && m.IsPublic && m.Parameters.Count == 0),
              "public class Softbody.GasEmitter : FrooxEngine.Component, with a public parameterless constructor");
        if (ge != null)
        {
            var gecat = ge.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "FrooxEngine.CategoryAttribute");
            var gepaths = gecat?.ConstructorArguments[0].Value is CustomAttributeArgument[] gearr ? gearr.Select(x => (string)x.Value).ToArray() : Array.Empty<string>();
            check(gepaths.SequenceEqual(new[] { "Physics" }), "GasEmitter is under Physics in the component browser");
            var gefields = ge.Fields.Where(f => !f.IsStatic && f.IsPublic).Select(f => (Name: f.Name, Type: f.FieldType.FullName)).ToList();
            bool gesame = gefields.SequenceEqual(ExpectedGasFields);
            if (!gesame) foreach (var e in ExpectedGasFields.Except(gefields).Concat(gefields.Except(ExpectedGasFields))) Console.WriteLine($"    gas mismatch: {e.Name} {e.Type}");
            check(gesame && ge.Fields.Where(f => !f.IsStatic && f.IsPublic).All(f => f.IsInitOnly),
                  $"GasEmitter's public fields are exactly the {ExpectedGasFields.Length} expected readonly Sync/RawOutput members, in inspector order");
            var geguarded = new[] { "OnStart", "OnDestroy", "OnCommonUpdate" };
            var geun = geguarded.Where(n => ge.Methods.FirstOrDefault(m => m.Name == n) is not { HasBody: true } m || !m.Body.HasExceptionHandlers).ToList();
            check(geun.Count == 0, "GasEmitter callbacks catch their exceptions" + (geun.Count > 0 ? " - no handler in " + string.Join(", ", geun) : ""));
            check(ge.Methods.Any(m => m.Name == "OnAwake"), "GasEmitter sets its defaults in OnAwake");
        }
        // 0.26.2: no override runs an engine callback base that throws by design
        ForbiddenBaseCallChecks(check, module);
        // 0.26.5: a saved layer keeps its hue
        LayerHueChecks(check, module);
        // 0.16.0: the GPU fluid's channel is compiled in, internal (0.26.0: with the gas emitter's record)
        var chan = new[] { "FluidChannel", "FluidHeader", "FluidBack", "FluidEmitterRecord", "FluidColliderRecord", "GasEmitterRecord", "FluidChannelIO" }.Select(n => module.GetType("Softbody.FluidGpu." + n)).ToList();
        check(chan.All(t => t != null && !t.IsPublic), "the GPU fluid's channel types are in the plugin, all internal");
        // 0.11.3: a SpawnBrush button on both components, and the brush's colour picker menu item
        foreach (var name in new[] { "Softbody.Softbody", "Softbody.SkinnedSoftbody" })
        {
            var sb = module.GetType(name);
            var mth = sb?.Methods.FirstOrDefault(m => m.Name == "SpawnBrush");
            check(mth != null && mth.IsPublic && mth.Parameters.Count == 0 && mth.CustomAttributes.Any(a => a.AttributeType.FullName == "FrooxEngine.SyncMethod") &&
                  mth.HasBody && mth.Body.HasExceptionHandlers, name + ".SpawnBrush is a guarded [SyncMethod] with no arguments (an inspector button)");
        }
        var brushType = module.GetType("Softbody.SoftbodyBrush");
        var cc = brushType?.Methods.FirstOrDefault(m => m.Name == "OnChangeColor");
        check(cc != null && cc.Parameters.Count == 2 && cc.Parameters[0].ParameterType.FullName == "FrooxEngine.IButton" && cc.HasBody && cc.Body.HasExceptionHandlers &&
              cc.CustomAttributes.Any(a => a.AttributeType.FullName == "FrooxEngine.SyncMethod"), "the brush's Change color menu item is a guarded [SyncMethod](IButton, ButtonEventData)");
    }

    // ------------------------------------------------------------------ 2. the engine's startup weaver, on a copy

    static string Weave(Action<bool, string> check)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "weave-check");
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, "Softbody.dll");
        File.Copy(Dll, copy, true);
        var pdb = Path.ChangeExtension(copy, ".pdb");
        if (File.Exists(pdb)) File.Delete(pdb);

        InstallResolver();
        var weaver = Assembly.LoadFrom(Path.Combine(Game, "FrooxEngine.Weaver.dll"));
        var process = weaver.GetType("FrooxEngine.Weaver.AssemblyPostProcessor", true).GetMethod("Process", new[] { typeof(string), typeof(string) });
        var output = new StringWriter();
        var oldOut = Console.Out;
        var oldCwd = Environment.CurrentDirectory;
        bool processed;
        try
        {
            // Resonite runs this with the game folder as working directory (Cecil resolves FrooxEngine & co. from "."),
            // passing the folder that holds FrooxEngine.dll (Engine.ProcessStartupCommands).
            Environment.CurrentDirectory = Game;
            Console.SetOut(output);
            processed = (bool)process.Invoke(null, new object[] { copy, Game.TrimEnd('\\', '/') });
        }
        finally
        {
            Console.SetOut(oldOut);
            Environment.CurrentDirectory = oldCwd;
        }
        var weaverLines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in weaverLines) Console.WriteLine("  weaver: " + line.TrimEnd());
        check(processed, "the engine's startup weaver (AssemblyPostProcessor.Process) accepts and rewrites the plugin (on a copy)");
        check(!weaverLines.Any(l => l.Contains("Non-readonly")), "the weaver reports no non-readonly sync members");

        using var asm = AssemblyDefinition.ReadAssembly(copy);
        bool marked = asm.MainModule.CustomAttributes.Any(a => a.AttributeType.Name == "DescriptionAttribute" && (a.ConstructorArguments.FirstOrDefault().Value as string) == "FROOXENGINE_WEAVED");
        var t = asm.MainModule.GetType("Softbody.Softbody");
        var init = t?.Methods.FirstOrDefault(m => m.Name == "InitializeSyncMembers");
        var created = init?.Body.Instructions.Where(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld).Select(i => ((FieldReference)i.Operand).Name).ToHashSet() ?? new HashSet<string>();
        bool getMember = t?.Methods.Any(m => m.Name == "GetSyncMember" && m.Parameters.Count == 1) == true;
        bool newer = t?.Methods.Any(m => m.Name == "__New" && m.IsStatic) == true;
        Console.WriteLine($"  woven copy: version {asm.Name.Version} (the weaver stamps the weave time), FROOXENGINE_WEAVED {marked}, " +
                          $"InitializeSyncMembers creates {created.Count} members, GetSyncMember {getMember}, __New {newer}");
        check(marked && created.SetEquals(ExpectedFields.Select(f => f.Name)) && getMember && newer,
              $"woven: marked FROOXENGINE_WEAVED, generated InitializeSyncMembers (all {ExpectedFields.Length} members), GetSyncMember and __New");
        var hb = asm.MainModule.GetType("Softbody.Softbody").NestedTypes.FirstOrDefault(n => n.Name == "HueBand");
        var hbInit = hb?.Methods.FirstOrDefault(m => m.Name == "InitializeSyncMembers");
        var hbCreated = hbInit?.Body.Instructions.Where(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld).Select(i => ((FieldReference)i.Operand).Name).ToHashSet() ?? new HashSet<string>();
        Console.WriteLine($"  woven HueBand: InitializeSyncMembers creates [{string.Join(", ", hbCreated)}]");
        check(hbCreated.SetEquals(new[] { "Stiffness", "Damping", "Follow" }), "woven: the HueBand sync object gets its Stiffness/Damping/Follow members generated too");
        HashSet<string> Created(TypeDefinition type) =>
            type?.Methods.FirstOrDefault(m => m.Name == "InitializeSyncMembers")?.Body.Instructions.Where(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld)
                .Select(i => ((FieldReference)i.Operand).Name).ToHashSet() ?? new HashSet<string>();
        var hl = asm.MainModule.GetType("Softbody.SkinnedSoftbody")?.NestedTypes.FirstOrDefault(n => n.Name == "HueLayer");
        var hlCreated = Created(hl);
        var hlWant = new[] { "Hue", "Enabled", "Simulating", "SimPoints", "Depth", "Layers", "Stiffness", "Volume", "VolumePreservation", "BoneDensity", "Follow", "Damping",
                             "Gravity", "BlendshapeInertia", "Substeps", "Collide", "CollideSelf", "SelfHead", "SelfBody", "SelfHands", "CollideOthers",
                             "OthersHead", "OthersBody", "OthersHands" };
        Console.WriteLine($"  woven HueLayer (0.13.0): InitializeSyncMembers creates {hlCreated.Count} members");
        check(hlCreated.SetEquals(hlWant) && hl.Methods.Any(m => m.Name == "OnAwake"),
              $"woven: the HueLayer sync object gets all {hlWant.Length} of its members generated, and sets its defaults in OnAwake");
        var pd = asm.MainModule.GetType("Softbody.Softbody").NestedTypes.FirstOrDefault(n => n.Name == "PaintDab");
        var pdCreated = Created(pd);
        Console.WriteLine($"  woven PaintDab: InitializeSyncMembers creates [{string.Join(", ", pdCreated)}]");
        check(pdCreated.SetEquals(ExpectedDabFields.Select(f => f.Name)) && pd.Methods.Any(m => m.Name == "__New" && m.IsStatic),
              "woven: the PaintDab sync object gets Position/Radius/Color/Strength/Hardness/Blur generated, and __New");
        var br = asm.MainModule.GetType("Softbody.SoftbodyBrush");
        var brCreated = Created(br);
        Console.WriteLine($"  woven SoftbodyBrush: InitializeSyncMembers creates {brCreated.Count} members [{string.Join(", ", brCreated)}]");
        check(brCreated.SetEquals(ExpectedBrushFields.Select(f => f.Name)) && br.Methods.Any(m => m.Name == "GetSyncMember") && br.Methods.Any(m => m.Name == "__New" && m.IsStatic),
              $"woven: the brush gets all {ExpectedBrushFields.Length} of its members, GetSyncMember and __New (the Tool's own members stay in FrooxEngine)");
        var sk = asm.MainModule.GetType("Softbody.SkinnedSoftbody");
        var skCreated = Created(sk);
        check(skCreated.SetEquals(ExpectedSkinnedFields.Select(f => f.Name)) && sk.Methods.Any(m => m.Name == "__New" && m.IsStatic),
              $"woven: SkinnedSoftbody gets all {ExpectedSkinnedFields.Length} of its members generated, and __New");
        var dmw = asm.MainModule.GetType("Softbody.SoftbodyDebugMesh");
        check(dmw != null && dmw.Methods.Any(m => m.Name == "__New" && m.IsStatic), "woven: the debug mesh gets __New (the engine creates components through it)");
        // 0.26.0
        var gw = asm.MainModule.GetType("Softbody.GasEmitter");
        var gwCreated = gw != null ? Created(gw) : new HashSet<string>();
        check(gw != null && gwCreated.SetEquals(ExpectedGasFields.Select(f => f.Name)) && gw.Methods.Any(m => m.Name == "__New" && m.IsStatic),
              $"woven: GasEmitter gets all {ExpectedGasFields.Length} of its members generated, and __New");
        return copy;
    }

    // ------------------------------------------------------------------ 3. the engine's own registration / hash code

    static void RegistryChecks(string woven, Action<bool, string> check)
    {
        InstallResolver();
        var fe = Assembly.LoadFrom(Path.Combine(Game, "FrooxEngine.dll"));
        var registryType = fe.GetType("FrooxEngine.AssemblyTypeRegistry", true);
        var kindType = Assembly.LoadFrom(Path.Combine(Game, "Elements.Data.dll")).GetType("Elements.Data.DataModelAssemblyType", true);
        var results = new List<(string label, int count, string hash, string[] types)>();
        foreach (var (label, path) in new[] { ("as built", Dll), ("woven", woven) })
        {
            if (path == null) continue;
            var alc = new AssemblyLoadContext("softbody-check-" + label.Replace(' ', '-'));
            var asm = alc.LoadFromAssemblyPath(path);
            var types = asm.GetTypes();
            // new AssemblyTypeRegistry(assembly, Core, types, scanTypes: true, fastCompatibilityHash: false, debugDump: false)
            var reg = Activator.CreateInstance(registryType, asm, Enum.ToObject(kindType, 0), types, true, false, false);
            int count = (int)registryType.GetProperty("TypeCount").GetValue(reg);
            string hash = (string)registryType.GetProperty("CompatibilityHash").GetValue(reg);
            var names = ((IEnumerable<Type>)registryType.GetProperty("Types").GetValue(reg)).Select(x => x.FullName).ToArray();
            Console.WriteLine($"  {label,-8}: Data Model Assembly: {asm.GetName().Name}, Type Count: {count} (total: {types.Length}), Compatibility Hash: {hash}, Type: Core  [{string.Join(", ", names)}]");
            results.Add((label, count, hash, names));
        }
        var first = results[0];
        var expectedTypes = new[] { "Softbody.Softbody", "Softbody.Softbody+Channel", "Softbody.Softbody+HueBand", "Softbody.Softbody+PaintDab", "Softbody.Softbody+StartColor", "Softbody.SoftbodyBrush", "Softbody.SoftbodyBrush+BrushMode", "Softbody.SoftbodyBrush+PaintHue", "Softbody.SoftbodyDebugMesh", "Softbody.SkinnedSoftbody", "Softbody.SkinnedSoftbody+HueLayer", "Softbody.SkinnedSoftbody+LayerHue",
                                    "Softbody.FluidEmitter", "Softbody.FluidEmitter+EmitMode", "Softbody.FluidEmitter+SolverMode", "Softbody.FluidSurfaceMesh", "Softbody.GasEmitter" };
        check(first.count == expectedTypes.Length && expectedTypes.All(first.types.Contains),
              $"the engine registers exactly the four components, their enums, HueBand, PaintDab, HueLayer, the brush and the debug and fluid meshes (Type Count: {expectedTypes.Length})");
        Console.WriteLine($"  compatibility hash 0.11.0: {first.hash} (0.10.x was G9wMj2mxXLdUwCbCdpcpZw==, 0.7.0 to 0.9.3 WoM0oawSj2JVus+1HkcIzQ==, 0.3.0 QFTH7I5BN7vLyNKRjq74pw==)");
        check(first.hash != "QFTH7I5BN7vLyNKRjq74pw==" && first.hash != "WoM0oawSj2JVus+1HkcIzQ==", "the compatibility hash differs from 0.3.0 and 0.7.0-0.9.3");
        check(first.hash != "G9wMj2mxXLdUwCbCdpcpZw==", "0.11.0 adds the SkinnedSoftbody component, so the hash differs from 0.10.x: everyone in a session needs 0.11.0");
        Console.WriteLine($"  compatibility hash 0.11.3: {first.hash} (0.11.0 to 0.11.2 were ac1XUmq1SPmxIl/KxjAspg==)");
        check(first.hash != "ac1XUmq1SPmxIl/KxjAspg==" && first.hash != "av8ILA4eNKIB6oty9S34pw==",
              "0.12.0 adds SkinnedSoftbody fields (collision toggles, VolumePreservation, CullDistance): the hash differs from 0.11.x, everyone in a session needs 0.12.0");
        Console.WriteLine($"  compatibility hash 0.15.0: {first.hash} (0.13.0 and 0.14.0 were yzYN/NzZG2/y5sxvhmW6nw==)");
        check(first.hash != "yzYN/NzZG2/y5sxvhmW6nw==", "0.15.0 adds the FluidEmitter component: the hash differs from 0.14.0, everyone in a session needs 0.15.0");
        Console.WriteLine($"  compatibility hash 0.16.0: {first.hash} (0.15.0 to 0.15.2 were YL0Byl04USJdHANaxlmwzQ==)");
        check(first.hash != "YL0Byl04USJdHANaxlmwzQ==", "0.16.0 adds FluidEmitter.Solver: the hash differs from 0.15.x, everyone in a session needs 0.16.0");
        Console.WriteLine($"  compatibility hash 0.17.0: {first.hash} (0.16.0 was ZUoBYk1Br89NcgfNSCWPeA==)");
        check(first.hash != "ZUoBYk1Br89NcgfNSCWPeA==", "0.17.0 adds FluidEmitter.ParticleSize and SurfaceDetail: the hash differs from 0.16.0, everyone in a session needs 0.17.0");
        Console.WriteLine($"  compatibility hash 0.18.0: {first.hash} (0.17.0 was o7GPm4X8+tYjmudk1DQHig==)");
        check(first.hash != "o7GPm4X8+tYjmudk1DQHig==", "0.18.0 adds FluidEmitter.DropletSize: the hash differs from 0.17.0, everyone in a session needs 0.18.0");
        Console.WriteLine($"  compatibility hash 0.19.0: {first.hash} (0.18.0 was BlyxiP4q9VY6raCaKsxUHQ==)");
        check(first.hash != "BlyxiP4q9VY6raCaKsxUHQ==", "0.19.0 adds FluidEmitter.RandomVelocity: the hash differs from 0.18.0, everyone in a session needs 0.19.0");
        // (0.20.0 to 0.25.2 added no synced member: they kept 0.19.0's hash, J/MtZVeY7e3nbFO3WIKftg==, and could share a session)
        Console.WriteLine($"  compatibility hash 0.26.0: {first.hash} (0.19.0 to 0.25.2 were J/MtZVeY7e3nbFO3WIKftg==)");
        check(first.hash != "J/MtZVeY7e3nbFO3WIKftg==", "0.26.0 adds the GasEmitter component: the hash differs from 0.19.0-0.25.2, everyone in a session needs 0.26.0");
        // (0.26.1 to 0.26.5 added no synced member: they kept 0.26.0's hash, yqJCXkkC/dkxHMi0+m5P3g==, and could share a session)
        Console.WriteLine($"  compatibility hash 0.27.0: {first.hash} (0.26.0 to 0.26.5 were yqJCXkkC/dkxHMi0+m5P3g==)");
        check(first.hash != "yqJCXkkC/dkxHMi0+m5P3g==", "0.27.0 adds FluidEmitter.ThickLiquid, Foam and FoamLifetime and GasEmitter.Vortices: the hash differs from 0.26.0-0.26.5, everyone in a session needs 0.27.0");
        check(first.hash == ExpectedHash, $"the compatibility hash is 0.27.0's, {ExpectedHash}");
        check(first.hash != "FngJCj2v4KqIbOI76K4nzQ==",
              "0.13.0 adds the HueLayers list (HueLayer, LayerHue) and the brush's hue presets: the hash differs from 0.12.x, everyone in a session needs 0.13.0");
        check(!string.IsNullOrEmpty(first.hash), "the engine computes a compatibility hash for it");
        if (results.Count > 1) check(results[1].hash == first.hash, "weaving does not change the compatibility hash (every machine weaves its own copy at startup)");

        // 0.11.1: the engine members SkinnedSoftbody reads by reflection (users' colliders) and by name (collider shapes)
        try
        {
            var alc = new AssemblyLoadContext("softbody-check-members");
            var asm = alc.LoadFromAssemblyPath(Dll);
            var reader = asm.GetType("Softbody.UserColliderReader", true);
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(reader.TypeHandle);
            var why = (string)reader.GetField("Unavailable", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            check(why == null, "the users' collider accessors (DynamicBoneChainManager.colliders: position, radius, mask, owner.User) compile against the installed engine" +
                               (why != null ? " - " + why : ""));
            var worldType = fe.GetType("FrooxEngine.World", true);
            check(worldType.GetProperty("DynamicBones")?.PropertyType.FullName == "FrooxEngine.DynamicBoneChainManager", "World.DynamicBones is the dynamic bone manager");
            var colliderType = fe.GetType("FrooxEngine.ICollider", true);
            check(colliderType.GetProperty("CharacterCollider")?.PropertyType == typeof(bool), "colliders expose CharacterCollider");
            var side = fe.GetType("FrooxEngine.MeshColliderSidedness", true);
            check(Enum.GetNames(side).SequenceEqual(new[] { "Front", "DualSided" }), "MeshColliderSidedness is Front / DualSided");
            var mgr = fe.GetType("FrooxEngine.DynamicBoneChainManager", true);
            byte Flag(string n) => (byte)mgr.GetField(n, BindingFlags.Public | BindingFlags.Static).GetRawConstantValue();
            check(Flag("LEFT_HAND_COLLIDER_FLAG") == 2 && Flag("RIGHT_HAND_COLLIDER_FLAG") == 4, "the hand collider flags are 2 and 4 (the own user keeps only those)");
            // 0.11.2: avatars' bone colliders, through VRIKAvatar's list of the drives that enable them
            var rig = asm.GetType("Softbody.AvatarRigColliders", true);
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(rig.TypeHandle);
            var rigWhy = (string)rig.GetField("Unavailable", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            check(rigWhy == null, "VRIKAvatar._rigCollidersEnabledStates (the avatar's bone collider drives) is a SyncList<FieldDrive<bool>>" + (rigWhy != null ? " - " + rigWhy : ""));
            // 0.14.0: instancing, through StaticAssetProvider's private asset field
            var pm = asm.GetType("Softbody.PrivateMeshes", true);
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(pm.TypeHandle);
            var pmWhy = (string)pm.GetField("Unavailable", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            check(pmWhy == null, "instancing: StaticAssetProvider._asset and _lock, Asset's LoadState, AssetURL, AssetType and VariantManager setters, Asset<V>.relocatedAsset exist" + (pmWhy != null ? " - " + pmWhy : ""));
            var meshType = fe.GetType("FrooxEngine.Mesh", true);
            check(meshType.GetConstructor(Type.EmptyTypes) != null && meshType.GetMethod("InitializeDynamic") != null && meshType.GetMethod("SetFromMeshX") != null &&
                  fe.GetType("FrooxEngine.WorldAssetManager", true).GetMethod("QueueAssetCreated") != null,
                  "instancing: a dynamic Mesh can be made (new Mesh, InitializeDynamic, SetFromMeshX) and renderers re-bound (WorldAssetManager.QueueAssetCreated)");
            // 0.26.1: the copy stands in for the shared asset, on the engine's own code
            InstancingStandIn(check, fe, pm);
        }
        catch (Exception ex) { check(false, "engine member checks ran: " + (ex.InnerException ?? ex).Message); }
    }

    /// <summary>
    /// 0.26.2: the engine's ComponentBase methods that throw by design when they run (ShouldntExecute: "Only overriden versions
    /// must be executed"): an override must not call them. 0.4.0 to 0.26.1 called base.OnFocusChanged first, so the engine
    /// logged an error on every focus change and the handler itself never ran (17 times in Lenki's logs since 2026-11-09);
    /// base.OnWorldDestroy came last and threw after it. The forbidden set is read from the installed engine.
    /// </summary>
    static void ForbiddenBaseCallChecks(Action<bool, string> check, ModuleDefinition module)
    {
        using var fe = AssemblyDefinition.ReadAssembly(Path.Combine(Game, "FrooxEngine.dll"));
        var forbidden = fe.MainModule.GetTypes().Where(t => t.FullName == "FrooxEngine.ComponentBase`1").SelectMany(t => t.Methods)
            .Where(m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "ShouldntExecute"))
            .Select(m => m.Name).ToHashSet();
        check(forbidden.Contains("OnFocusChanged") && forbidden.Contains("OnWorldDestroy"),
              "the engine's ComponentBase callbacks that throw if their base runs: " + string.Join(", ", forbidden.OrderBy(n => n)));
        List<string> Offenders(ModuleDefinition m) => m.GetTypes().SelectMany(t => t.Methods.Where(x => x.HasBody).SelectMany(x => x.Body.Instructions
                .Where(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Call && i.Operand is MethodReference r && forbidden.Contains(r.Name) && r.DeclaringType.Namespace == "FrooxEngine")
                .Select(i => $"{t.Name}.{x.Name} calls {((MethodReference)i.Operand).DeclaringType.Name}.{((MethodReference)i.Operand).Name}"))).ToList();
        var bad = Offenders(module);
        check(bad.Count == 0, "no override calls one of them (base.OnFocusChanged, base.OnWorldDestroy, base.OnWorldSaved)" + (bad.Count > 0 ? ": " + string.Join("; ", bad) : ""));
        // the check itself: it finds the five calls in the installed-and-kept 0.26.1, when that copy is here
        var old = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Dll), "..", "..", "..", "..", "parked_addons", "Libraries", "Softbody.dll.0.26.1.installed"));
        if (File.Exists(old))
        {
            using var oldAsm = AssemblyDefinition.ReadAssembly(old);
            var found = Offenders(oldAsm.MainModule);
            check(found.Count == 5, $"the check finds the {found.Count} base calls in the kept 0.26.1 (expected 5: Softbody and SkinnedSoftbody OnFocusChanged and OnWorldDestroy, FluidEmitter OnWorldDestroy)");
        }
    }

    /// <summary>
    /// 0.26.5, Lenki's friend: an avatar saved with a second layer set to orange came back yellow. HueLayer.OnAwake picked
    /// the first hue the other layers did not use. OnAwake runs on every client that builds a layer, loading, copying or
    /// receiving it, before its saved or synced values arrive: read from the engine's IL, a list loads each element right
    /// after creating it (SyncElementList.InternalLoad: InternalInsert, then Load), but a client receiving the avatar
    /// creates all of them first and gets the values afterwards (InternalDecodeFull), so the pick saw other values there.
    /// Now OnAwake sets constants only, and a free hue is picked when a layer is added by hand, on that user's client.
    /// </summary>
    static void LayerHueChecks(Action<bool, string> check, ModuleDefinition module)
    {
        var ss = module.GetType("Softbody.SkinnedSoftbody");
        var hl = ss?.NestedTypes.FirstOrDefault(n => n.Name == "HueLayer");
        var awake = hl?.Methods.FirstOrDefault(m => m.Name == "OnAwake" && m.HasBody);
        var reads = awake == null ? new List<string> { "(no OnAwake)" } : awake.Body.Instructions
            .Where(i => i.Operand is MethodReference r && (r.Name == "get_Parent" || r.Name == "GetEnumerator" || r.Name == "get_Value" || r.Name == "get_Item" || r.Name == "get_Count"))
            .Select(i => ((MethodReference)i.Operand).DeclaringType.Name + "." + ((MethodReference)i.Operand).Name).Distinct().ToList();
        check(reads.Count == 0, "HueLayer.OnAwake sets constants only: it reads no other layer, no parent and no value (it runs on every client, before the saved or synced values)" +
                                (reads.Count > 0 ? " - it calls " + string.Join(", ", reads) : ""));
        bool Calls(MethodDefinition m, string name) => m != null && m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == name);
        var ssAwake = ss?.Methods.FirstOrDefault(m => m.Name == "OnAwake" && m.HasBody);
        var added = ss?.Methods.FirstOrDefault(m => m.Name == "LayersAdded" && m.HasBody);
        var start = ss?.Methods.FirstOrDefault(m => m.Name == "OnStart" && m.HasBody);
        check(Calls(ssAwake, "add_ElementsAdded") && added != null, "SkinnedSoftbody listens for layers being added (HueLayers.ElementsAdded -> LayersAdded)");
        check(Calls(added, "get_User") && Calls(added, "get_AllocationID") && Calls(added, "get_LocalUser") &&
              start != null && start.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_startedHere" && i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld),
              "a new layer's hue is picked only for layers this client's user added after the component started here (RefID.User == LocalUser.AllocationID)");
        check(added != null && added.Body.HasExceptionHandlers, "LayersAdded catches its exceptions");
    }

    /// <summary>
    /// 0.26.1, found in game (the first time instancing ran): 0.14.0 to 0.26.0 rebuilt every 2 s, because a StaticMesh hands
    /// out only an asset that counts as loaded, and a dynamic mesh never does (so the body vanished at the swap and the
    /// softbody saw "the renderer's mesh changed"). Here PrivateMeshes.StandIn and Retire run on real engine objects, built
    /// without a world: a shared Mesh with a URL, a variant manager and two requests, a dynamic copy, and a StaticMesh whose
    /// own getters decide what it hands out. The engine's own RemoveRequest on the copy (what a provider that lets go of its
    /// asset calls) must reach the shared asset instead of throwing.
    /// </summary>
    static void InstancingStandIn(Action<bool, string> check, Assembly fe, Type pm)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var meshType = fe.GetType("FrooxEngine.Mesh", true);
            var assetType = fe.GetType("FrooxEngine.Asset", true);
            var stateType = fe.GetType("FrooxEngine.AssetLoadState", true);
            var kindType = fe.GetType("FrooxEngine.AssetType", true);
            Type BaseNamed(Type t, string genericName) { for (; t != null; t = t.BaseType) if (t.IsGenericType && t.GetGenericTypeDefinition().Name == genericName) return t; return null; }
            var assetOfMesh = BaseNamed(meshType, "Asset`1");
            object Get(object o, string prop) => assetType.GetProperty(prop, Any).GetValue(o);
            void Set(object o, string prop, object v) => assetType.GetProperty(prop, Any).GetSetMethod(true).Invoke(o, new[] { v });
            object State(string n) => Enum.Parse(stateType, n);

            var url = new Uri("resdb:///softbody-instancing-check.meshx");
            var metadata = Activator.CreateInstance(Assembly.LoadFrom(Path.Combine(Game, "Elements.Assets.dll")).GetType("Elements.Assets.MeshMetadata", true));
            // the shared asset: static (a new Mesh is), loaded, with a URL, a variant manager and two requesters
            var shared = Activator.CreateInstance(meshType);
            Set(shared, "AssetURL", url);
            var vm = Activator.CreateInstance(fe.GetType("FrooxEngine.AssetVariantManager`1", true).MakeGenericType(meshType), url, null, metadata);
            Set(shared, "VariantManager", vm);
            Set(shared, "LoadState", State("FullyLoaded"));
            var requests = (System.Collections.IDictionary)assetOfMesh.GetField("requests", Any).GetValue(shared);
            var counter = assetOfMesh.GetField("variantCounter", Any).GetValue(shared);
            var desc = Activator.CreateInstance(fe.GetType("FrooxEngine.MeshVariantDescriptor", true), new object[] { false });
            var requesterType = fe.GetType("FrooxEngine.IAssetRequester", true);
            var r1 = DispatchProxy.Create(requesterType, typeof(NullRequester));
            var r2 = DispatchProxy.Create(requesterType, typeof(NullRequester));
            foreach (var r in new[] { r1, r2 }) { requests.Add(r, desc); counter.GetType().GetMethod("Increment").Invoke(counter, new[] { desc }); }

            // the copy, as PrivateMeshes.Create leaves it: a dynamic asset (Asset.InitializeDynamic's part; the renderer
            // registration and the upload need an engine)
            var copy = Activator.CreateInstance(meshType);
            Set(copy, "AssetType", Enum.Parse(kindType, "Dynamic"));
            check(Get(copy, "LoadState").Equals(State("Created")) && Get(copy, "AssetType").ToString() == "Dynamic",
                  "a dynamic Mesh never counts as loaded by itself (LoadState stays Created): why 0.14.0 to 0.26.0 rebuilt every 2 s");

            // a StaticMesh built without a world, holding the copy as PrivateMeshes.Swap leaves it: its own getters decide
            var provider = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(fe.GetType("FrooxEngine.StaticMesh", true));
            var sp = BaseNamed(provider.GetType(), "StaticAssetProvider`3");
            var urlField = sp.GetField("URL", Any);
            var sync = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(urlField.FieldType);
            BaseNamed(urlField.FieldType, "SyncField`1").GetField("_value", Any).SetValue(sync, url);
            urlField.SetValue(provider, sync);
            sp.GetField("_originalRequestedUrl", Any).SetValue(provider, url);
            sp.GetField("_requestedUrl", Any).SetValue(provider, url);
            sp.GetField("_asset", Any).SetValue(provider, copy);
            object HandedOut() => sp.GetProperty("Asset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).GetValue(provider);
            check(HandedOut() == null, "before the fix: a StaticMesh holding the plain dynamic copy hands out nothing (what made the body vanish and the softbody rebuild)");

            string threw = null;
            try { assetType.GetMethod("RemoveRequest", Any).Invoke(copy, new[] { r1 }); }
            catch (TargetInvocationException ex) { threw = ex.InnerException?.Message; }
            check(threw != null && threw.Contains("isn't Static") && requests.Count == 2,
                  "before the fix: the engine's RemoveRequest on the plain dynamic copy throws ('" + threw + "'), so a provider letting go of it would fail inside the engine");

            pm.GetMethod("StandIn", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { copy, shared });
            var relocated = assetOfMesh.GetField("relocatedAsset", Any);
            check(Get(copy, "LoadState").Equals(State("FullyLoaded")) && Equals(Get(copy, "AssetURL"), url) && Get(copy, "AssetType").ToString() == "Static" &&
                  ReferenceEquals(Get(copy, "VariantManager"), vm) && ReferenceEquals(relocated.GetValue(copy), shared),
                  "StandIn: the copy counts as fully loaded, carries the shared asset's URL and variant manager, and is static, relocated to the shared asset");
            check(ReferenceEquals(HandedOut(), copy), "the StaticMesh's own Asset getter hands the stood-in copy out");
            check(Equals(((Uri)Get(copy, "AssetURL")), url), "the provider's next asset update sees no URL change (it compares the held asset's URL), so it keeps the copy");
            check(ReferenceEquals(meshType.GetProperty("Metadata").GetValue(copy), metadata), "the copy's metadata (bones, bounds) is the shared asset's");
            check(Get(shared, "LoadState").Equals(State("FullyLoaded")) && requests.Count == 2, "StandIn leaves the shared asset as it was");

            // what StaticAssetProvider.FreeAsset calls on the asset it lets go of (destroyed, another URL or variant)
            assetType.GetMethod("RemoveRequest", Any).Invoke(copy, new[] { r1 });
            check(!requests.Contains(r1) && requests.Contains(r2) && requests.Count == 1,
                  "the engine's RemoveRequest on the copy removes that request from the shared asset (0.26.0 would have thrown 'Asset instance isn't Static' inside the engine)");

            pm.GetMethod("Retire", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { copy });
            check(Get(copy, "AssetType").ToString() == "Dynamic" && Get(copy, "VariantManager") == null && relocated.GetValue(copy) == null && Get(copy, "AssetURL") == null,
                  "Retire: the copy is a plain dynamic asset again (no URL, no variant manager, not relocated)");
            pm.GetMethod("Retire", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { copy });
            check(requests.Count == 1 && ReferenceEquals(Get(shared, "VariantManager"), vm) && Get(shared, "LoadState").Equals(State("FullyLoaded")) && Equals(Get(shared, "AssetURL"), url),
                  "retiring the copy (twice) leaves the shared asset and its other request alone");
        }
        catch (Exception ex) { check(false, "instancing stand-in on engine objects ran: " + (ex.InnerException ?? ex)); }
    }

    /// <summary>An IAssetRequester that does nothing (a key in an asset's requests).</summary>
    public class NullRequester : DispatchProxy
    {
        protected override object Invoke(MethodInfo targetMethod, object[] args) => null;
    }

    static bool _resolver;
    static void InstallResolver()
    {
        if (_resolver) return;
        _resolver = true;
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var p = Path.Combine(Game, name.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
    }
}
