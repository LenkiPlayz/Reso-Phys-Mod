using System.Reflection;
using SRE = System.Reflection.Emit;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SoftbodyRenderer;

namespace Softbody.Tests;

/// <summary>
/// Offline checks of the renderer plugin (Renderer/SoftbodyRenderer.dll, BepInEx 5, net472) against the installed renderer
/// (read-only): it was built against the real renderer DLLs and every member it uses exists there; the patch target
/// (MeshAsset's upload coroutine, its fields, the hint flags) is what the patch expects; the coroutine's IL skeleton has the
/// hash the patch requires, read both with Cecil and as Harmony sees it (System.Reflection operands); and the IL facts
/// the in-place path relies on hold. Unity itself cannot run offline: what Unity does with the in-place
/// SetVertexBufferData on a skinned mesh needs an in-game run (README checklist).
/// </summary>
static class RendererCheck
{
    static string Dll, Game, Managed, BepCore;

    public static void Run(Action<bool, string> check)
    {
        var meta = typeof(RendererCheck).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        Dll = Path.GetFullPath(meta["RendererDll"]);
        Game = meta["ResonitePath"];
        Managed = Path.Combine(Game, @"Renderer\Renderite.Renderer_Data\Managed\");
        BepCore = Path.Combine(Game, @"Renderer\BepInEx\core\");
        // the BepisLoader stack may be parked outside the game (Testresolink\parked_addons, same relative paths)
        var parked = Path.GetFullPath(Path.Combine(meta["TestDataDir"], @"..\..\..\parked_addons\Renderer\BepInEx\core\"));
        if (!Directory.Exists(BepCore) && Directory.Exists(parked)) BepCore = parked;
        Console.WriteLine("TEST renderer plugin (offline): " + Dll);
        if (!File.Exists(Dll)) { check(false, "renderer plugin DLL exists (dotnet build -c Release Renderer)"); return; }

        try { PluginDll(check); }
        catch (Exception ex) { check(false, "renderer plugin Cecil checks ran: " + ex.Message); }
        List<string> cecilSkeleton = null;
        try { cecilSkeleton = Target(check); }
        catch (Exception ex) { check(false, "patch target checks ran: " + ex.Message); }
        try { ReflectionView(check, cecilSkeleton); }
        catch (Exception ex) { check(false, "reflection view of the coroutine ran: " + (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message); }
    }

    static DefaultAssemblyResolver Resolver()
    {
        var r = new DefaultAssemblyResolver();
        r.AddSearchDirectory(Managed);
        r.AddSearchDirectory(BepCore);
        return r;
    }

    // ------------------------------------------------------------------ the built plugin

    static void PluginDll(Action<bool, string> check)
    {
        using var asm = AssemblyDefinition.ReadAssembly(Dll, new ReaderParameters { AssemblyResolver = Resolver() });
        var mod = asm.MainModule;
        var tfm = asm.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "TargetFrameworkAttribute")?.ConstructorArguments[0].Value as string;
        var refs = mod.AssemblyReferences.Select(r => r.Name).ToList();
        Console.WriteLine($"  {asm.Name.FullName}, {tfm}; references {string.Join(", ", refs)}");
        check(asm.Name.Name == "SoftbodyRenderer" && asm.Name.Version == new Version(0, 9, 2, 0) && tfm == ".NETFramework,Version=v4.7.2",
              "renderer plugin: SoftbodyRenderer 0.9.2, .NET Framework 4.7.2 (the renderer's Unity 2019.4 Mono)");
        var expected = new[] { "Renderite.Unity", "Renderite.Shared", "UnityEngine.CoreModule", "BepInEx", "0Harmony" };
        var allowed = new HashSet<string>(expected) { "mscorlib", "netstandard", "System", "System.Core", "UnityEngine", "System.Memory" };
        check(expected.All(refs.Contains) && refs.All(allowed.Contains), "it references the renderer's own Renderite.Unity / Renderite.Shared / UnityEngine.CoreModule and BepInEx 5 + HarmonyX, nothing else");
        var outDir = Path.GetDirectoryName(Dll);
        var extra = Directory.GetFiles(outDir, "*.dll").Select(Path.GetFileName).Where(f => f != "SoftbodyRenderer.dll").ToList();
        check(extra.Count == 0, "nothing else is copied next to it (references are Private=false)" + (extra.Count > 0 ? ": " + string.Join(", ", extra) : ""));

        // the BepInEx entry point
        var plugin = mod.Types.FirstOrDefault(t => t.FullName == "SoftbodyRenderer.Plugin");
        var bep = plugin?.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "BepInEx.BepInPlugin");
        var bepArgs = bep?.ConstructorArguments.Select(a => a.Value as string).ToArray();
        bool isUnityPlugin = false;
        for (var b = plugin?.BaseType; b != null; b = b.Resolve()?.BaseType) if (b.FullName == "BepInEx.BaseUnityPlugin") { isUnityPlugin = true; break; }
        Console.WriteLine($"  [BepInPlugin({string.Join(", ", bepArgs ?? Array.Empty<string>())})] {plugin?.FullName} : {plugin?.BaseType?.FullName}");
        check(isUnityPlugin && bepArgs != null && bepArgs[0] == "softbody.renderer" && bepArgs[1] == "SoftbodyRenderer" && bepArgs[2] == "0.9.2" &&
              plugin.Methods.Any(m => m.Name == "Awake" && !m.IsStatic),
              "a BepInEx 5 BaseUnityPlugin with [BepInPlugin(\"softbody.renderer\", \"SoftbodyRenderer\", \"0.9.2\")] and an Awake");
        var strings = mod.Types.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                         .Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToHashSet();
        check(strings.Contains("SoftbodyRenderer 0.9.2 patched MeshAsset.Upload"), "it logs \"SoftbodyRenderer 0.9.2 patched MeshAsset.Upload\" through the BepInEx logger");

        // every member it uses from the renderer, BepInEx and Harmony exists in the installed DLLs (binds at run time)
        var scopes = new HashSet<string>(expected) { "UnityEngine" };
        int members = 0; var missing = new List<string>();
        foreach (var t in mod.Types)
            foreach (var m in t.Methods.Where(m => m.HasBody))
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.Operand is not MemberReference mr || mr is TypeDefinition || mr is MethodDefinition || mr is FieldDefinition) continue;
                    var scope = (mr as TypeReference ?? mr.DeclaringType)?.Scope?.Name;
                    if (scope == null || !scopes.Contains(scope)) continue;
                    members++;
                    object resolved = mr switch { MethodReference x => x.Resolve(), FieldReference x => x.Resolve(), TypeReference x => x.Resolve(), _ => null };
                    if (resolved == null) missing.Add(mr.FullName);
                }
        Console.WriteLine($"  {members} references into the renderer / BepInEx / Harmony DLLs, {missing.Count} unresolved{(missing.Count > 0 ? ": " + string.Join("; ", missing.Distinct().Take(5)) : "")}");
        check(members > 20 && missing.Count == 0, "every renderer, Unity, BepInEx and Harmony member the plugin uses resolves against the installed DLLs");

        // the constants built into the DLL are the ones these tests verify (VerifiedSkeletonHash is inlined where it is used)
        var spec = mod.Types.FirstOrDefault(t => t.FullName == "SoftbodyRenderer.UploadPatchSpec");
        object Const(string name) => spec?.Fields.FirstOrDefault(f => f.Name == name && f.HasConstant)?.Constant;
        var builtHash = Const(nameof(UploadPatchSpec.VerifiedSkeletonHash)); var builtName = Const(nameof(UploadPatchSpec.HandshakeName));
        Console.WriteLine($"  built-in: VerifiedSkeletonHash {(builtHash is ulong bh ? bh.ToString("x16") : "-")}, HandshakeName {builtName}");
        check(builtHash is ulong h && h == UploadPatchSpec.VerifiedSkeletonHash && (string)builtName == UploadPatchSpec.HandshakeName,
              "the DLL was built with the verified skeleton hash and handshake name these tests check");

        // Harmony binds patch arguments by name
        var patch = mod.Types.FirstOrDefault(t => t.Name == "MeshUploadPatch");
        string Sig(string name) { var mm = patch?.Methods.FirstOrDefault(x => x.Name == name); return mm == null ? "-" : string.Join(",", mm.Parameters.Select(p => p.ParameterType.Name + " " + p.Name)); }
        string pre = Sig("Prefix"), post = Sig("Postfix"), fin = Sig("Finalizer");
        Console.WriteLine($"  Prefix({pre}) Postfix({post}) Finalizer({fin})");
        check(pre == "Object __instance,Boolean& __result,MeshAsset& __state" && post == "MeshAsset __state" && fin == "MeshAsset __state",
              "the Harmony prefix / postfix / finalizer take __instance, __result and __state (bound by name)");
    }

    // ------------------------------------------------------------------ the patch target in the installed Renderite.Unity.dll

    static string Entry(Instruction ins) => ins.Operand switch
    {
        FieldReference f => "F " + f.DeclaringType.Name + "::" + f.Name,
        MethodReference m => "M " + m.DeclaringType.Name + "::" + m.Name,
        _ => UploadPatchSpec.Entry(ins.OpCode.Name, ins.Operand),
    };

    static int? IntConst(Instruction i) => i.OpCode.Code switch
    {
        Code.Ldc_I4 => (int)i.Operand, Code.Ldc_I4_S => (sbyte)i.Operand, Code.Ldc_I4_M1 => -1, Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2,
        Code.Ldc_I4_3 => 3, Code.Ldc_I4_4 => 4, Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7, Code.Ldc_I4_8 => 8, _ => null,
    };

    static bool IsCall(Instruction i, string type, string name) =>
        (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt || i.OpCode.Code == Code.Newobj) && i.Operand is MethodReference m && m.Name == name && m.DeclaringType.Name == type;
    static bool IsField(Instruction i, Code op, string name) => i.OpCode.Code == op && i.Operand is FieldReference f && f.Name == name;

    static List<string> Target(Action<bool, string> check)
    {
        var path = Path.Combine(Managed, "Renderite.Unity.dll");
        using var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = Resolver() });
        var info = asm.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "AssemblyInformationalVersionAttribute")?.ConstructorArguments[0].Value;
        Console.WriteLine($"  installed {path}: {asm.Name.FullName}, {info}");
        var meshAsset = asm.MainModule.GetType(UploadPatchSpec.MeshAssetType);
        FieldDefinition Field(string n) => meshAsset?.Fields.FirstOrDefault(f => f.Name == n && !f.IsStatic);
        var fields = new (string name, string type)[]
        {
            (UploadPatchSpec.LastIsSkinnedField, "System.Boolean"), (UploadPatchSpec.FirstUploadCompletedField, "System.Boolean"),
            (UploadPatchSpec.MeshField, "UnityEngine.Mesh"), (UploadPatchSpec.UploadDataField, "Renderite.Shared.MeshUploadData"),
            (UploadPatchSpec.MeshBufferField, "Renderite.Shared.MeshBuffer"),
        };
        var badFields = fields.Where(f => Field(f.name)?.FieldType.FullName != f.type).Select(f => f.name).ToList();
        var coroutines = meshAsset?.NestedTypes.Where(t => t.Name.StartsWith(UploadPatchSpec.CoroutinePrefix) && t.Interfaces.Any(i => i.InterfaceType.FullName == "System.Collections.IEnumerator")).ToList();
        var co = coroutines?.Count == 1 ? coroutines[0] : null;
        var moveNext = co?.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        var st = co?.Fields.FirstOrDefault(f => f.Name == UploadPatchSpec.StateField);
        var th = co?.Fields.FirstOrDefault(f => f.Name == UploadPatchSpec.ThisField);
        Console.WriteLine($"  patch target: {meshAsset?.FullName}/{co?.Name}::MoveNext ({moveNext?.Body.Instructions.Count} instructions); fields " +
                          string.Join(", ", fields.Select(f => $"{f.name}: {Field(f.name)?.FieldType.Name ?? "MISSING"}")));
        check(meshAsset != null && badFields.Count == 0 && co != null && moveNext != null && st?.FieldType.FullName == "System.Int32" && th?.FieldType.FullName == UploadPatchSpec.MeshAssetType,
              $"the installed Renderite.Unity.dll has MeshAsset with lastIsSkinned, firstUploadCompleted, mesh, uploadData and meshBuffer, and one <Upload> coroutine ({co?.Name}) with MoveNext, <>1__state and <>4__this" +
              (badFields.Count > 0 ? " - wrong: " + string.Join(", ", badFields) : ""));
        // the renderer's own Renderite.Shared: the flag values the patch and the engine plugin use
        using var shared = AssemblyDefinition.ReadAssembly(Path.Combine(Managed, "Renderite.Shared.dll"));
        var flagEnum = shared.MainModule.GetType("Renderite.Shared.MeshUploadHint").NestedTypes.First(t => t.Name == "Flag");
        var flagValues = flagEnum.Fields.Where(f => f.HasConstant).ToDictionary(f => f.Name, f => Convert.ToInt32(f.Constant));
        var mine = Enum.GetValues<Renderite.Shared.MeshUploadHint.Flag>().ToDictionary(f => f.ToString(), f => (int)f);
        bool flagsSame = flagValues.Count == mine.Count && flagValues.All(kv => mine.TryGetValue(kv.Key, out var v) && v == kv.Value);
        Console.WriteLine($"  renderer's MeshUploadHint.Flag: VertexLayout={flagValues["VertexLayout"]}, Positions={flagValues["Positions"]}, Normals={flagValues["Normals"]}, " +
                          $"BindPoses=0x{flagValues["BindPoses"]:x}, BoneWeights=0x{flagValues["BoneWeights"]:x}, Blendshapes=0x{flagValues["Blendshapes"]:x}, Dynamic=0x{flagValues["Dynamic"]:x}");
        check(flagsSame, "the renderer's MeshUploadHint flags have the same names and values as the engine's (BindPoses 0x8000, BoneWeights 0x10000, Blendshapes 0x20000, ...)");
        if (moveNext == null) return null;

        // the skeleton the patch pins
        var code = moveNext.Body.Instructions;
        var skeleton = code.Select(Entry).Where(e => e != null).ToList();
        ulong hash = UploadPatchSpec.Hash(skeleton);
        Console.WriteLine($"  IL skeleton: {skeleton.Count} entries (member references + int constants), hash {hash:x16} (the patch requires {UploadPatchSpec.VerifiedSkeletonHash:x16})");
        check(hash == UploadPatchSpec.VerifiedSkeletonHash, "the coroutine's IL is the build the patch was verified against (skeleton hash matches; the plugin refuses to patch otherwise)");

        // IL facts the in-place path relies on
        // (a) lastIsSkinned is read once, in "mesh != null && (!mesh.isReadable || lastIsSkinned || hint[BindPoses])", whose true branch destroys the mesh
        var reads = code.Where(i => IsField(i, Code.Ldfld, UploadPatchSpec.LastIsSkinnedField)).ToList();
        bool destroyCheck = false;
        if (reads.Count == 1)
        {
            var r = reads[0];
            var readable = r.Previous?.Previous; var readableCall = readable?.Previous;
            var branch = r.Next;
            var bindPoses = branch?.Next?.Next?.Next; var getItem = bindPoses?.Next;
            var target = branch?.Operand as Instruction;
            bool targetDestroys = false;
            for (var t = target; t != null && t.Offset < target.Offset + 24; t = t.Next) if (IsCall(t, "Object", "Destroy")) { targetDestroys = true; break; }
            destroyCheck = readableCall != null && IsCall(readableCall, "Mesh", "get_isReadable") && (branch.OpCode.Code == Code.Brtrue || branch.OpCode.Code == Code.Brtrue_S) &&
                           IntConst(bindPoses) == 0x8000 && getItem != null && IsCall(getItem, "MeshUploadHint", "get_Item") && targetDestroys;
        }
        // (b) stores: 0 once right after that check (before instanceChanged = false), 1 only after a BoneWeights or Blendshapes flag test
        var stores = code.Where(i => IsField(i, Code.Stfld, UploadPatchSpec.LastIsSkinnedField)).ToList();
        int clears = stores.Count(s => IntConst(s.Previous) == 0), sets = stores.Count(s => IntConst(s.Previous) == 1);
        int? FlagBefore(Instruction at)
        {
            for (var p = at.Previous; p != null; p = p.Previous)
            {
                if (IsCall(p, "MeshUploadHint", "get_AnyVertexStreams")) return -1;
                if (IsCall(p, "MeshUploadHint", "get_Item")) return IntConst(p.Previous);
            }
            return null;
        }
        bool setsGuarded = stores.Where(s => IntConst(s.Previous) == 1).All(s => FlagBefore(s) is 0x10000 or 0x20000);
        // (c) every step that changes more than stream 0 sits behind its own flag test (nearest preceding hint test)
        var guards = new (string type, string name, int[] flags)[]
        {
            ("MeshAsset", "UpdateVertexLayout", new[] { 1 }), ("MeshAsset", "UploadVertexBuffer", new[] { -1 }), ("MeshAsset", "UpdateSubmeshLayout", new[] { 2 }),
            ("MeshAsset", "UploadIndexBuffer", new[] { 2, 4 }), ("MeshAsset", "UploadBonesBuffer", new[] { 0x10000 }), ("Mesh", "set_bindposes", new[] { 0x20000 }),
            ("Mesh", "SetBoneWeights", new[] { 0x20000 }), ("Mesh", "ClearBlendShapes", new[] { 0x20000 }), ("Mesh", "AddBlendShapeFrame", new[] { 0x20000 }),
            ("Mesh", "MarkDynamic", new[] { 0x40000 }),
        };
        var unguarded = new List<string>(); int guarded = 0;
        foreach (var g in guards)
            foreach (var call in code.Where(i => IsCall(i, g.type, g.name)))
            {
                var f = FlagBefore(call);
                if (f.HasValue && g.flags.Contains(f.Value)) guarded++; else unguarded.Add($"{g.name} (after flag {f})");
            }
        var allCalls = code.Where(i => i.Operand is MethodReference mr && (mr.DeclaringType.Name == "Mesh" || mr.DeclaringType.Name == "MeshAsset") && i.OpCode.Code != Code.Newobj)
                           .Select(i => ((MethodReference)i.Operand).Name).Distinct().OrderBy(x => x).ToList();
        Console.WriteLine($"  lastIsSkinned: {reads.Count} read (the destroy check: {destroyCheck}), {clears} clear, {sets} sets (after BoneWeights/Blendshapes tests: {setsGuarded}); " +
                          $"{guarded} flag-guarded mesh steps{(unguarded.Count > 0 ? ", NOT guarded: " + string.Join(", ", unguarded) : "")}");
        Console.WriteLine($"  Mesh/MeshAsset calls in the coroutine: {string.Join(", ", allCalls)}");
        check(reads.Count == 1 && destroyCheck, "IL: lastIsSkinned is read once, in the check \"!mesh.isReadable || lastIsSkinned || hint[BindPoses]\" that leads to Object.Destroy (the patch clears it for exactly that check)");
        check(clears == 1 && sets == 2 && setsGuarded, "IL: it is cleared once at the start and set again only behind a BoneWeights (0x10000) or Blendshapes (0x20000) test (the finalizer restores it after an in-place upload)");
        check(unguarded.Count == 0 && guarded >= 11,
              "IL: every step that touches the layout, indices, submeshes, bind poses, bone weights or blendshapes is behind its flag; stream 0 is behind AnyVertexStreams (a vertex-only hint runs only that, plus bounds and the submesh/index-format sanitizing)");

        // (d) how the data lands in Unity: stream 0 only, and skin weights through SetBoneWeights (not a vertex stream the renderer declares)
        var upload = meshAsset.Methods.First(m => m.Name == "UploadVertexBuffer").Body.Instructions;
        var setData = upload.FirstOrDefault(i => IsCall(i, "Mesh", "SetVertexBufferData"));
        bool stream0 = setData != null && IntConst(setData.Previous) == 15 && IntConst(setData.Previous.Previous) == 0;
        var layout = meshAsset.Methods.First(m => m.Name == "UpdateVertexLayout").Body.Instructions;
        var ctor = layout.FirstOrDefault(i => i.OpCode.Code == Code.Newobj && i.Operand is MethodReference mr && mr.DeclaringType.Name == "VertexAttributeDescriptor");
        bool layoutStream0 = ctor != null && IntConst(ctor.Previous) == 0;
        var bones = meshAsset.Methods.First(m => m.Name == "UploadBonesBuffer").Body.Instructions;
        bool weightsApart = bones.Any(i => IsCall(i, "Mesh", "SetBoneWeights")) && bones.Any(i => IsCall(i, "Mesh", "set_bindposes"));
        Console.WriteLine($"  UploadVertexBuffer: SetVertexBufferData(data, 0, 0, length, stream 0, flags 15) {stream0}; UpdateVertexLayout declares every attribute in stream 0 {layoutStream0}; " +
                          $"bone weights via Mesh.SetBoneWeights + bindposes, never a declared vertex attribute {weightsApart}");
        check(stream0 && layoutStream0 && weightsApart,
              "IL: vertex data goes to stream 0 only (SetVertexBufferData, flags 15 = no validation, no bone-bounds reset, no notify, no bounds recalc); bone weights and bind poses are set apart (SetBoneWeights, bindposes), not as declared vertex attributes");
        return skeleton;
    }

    // ------------------------------------------------------------------ the coroutine as Harmony sees it (System.Reflection)

    sealed class RendererContext : AssemblyLoadContext
    {
        readonly string _dir;
        public RendererContext(string dir) : base("renderer-view", isCollectible: true) { _dir = dir; }
        protected override Assembly Load(AssemblyName name)
        {
            var n = name.Name ?? "";
            if (n.StartsWith("Renderite.") || n.StartsWith("UnityEngine") || n.StartsWith("Unity."))
            {
                var p = Path.Combine(_dir, n + ".dll");
                if (File.Exists(p)) return LoadFromAssemblyPath(p);
            }
            return null;   // framework assemblies come from this runtime
        }
    }

    static readonly Dictionary<short, SRE.OpCode> Ops = typeof(SRE.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (SRE.OpCode)f.GetValue(null)).ToDictionary(o => o.Value);

    /// <summary>Decodes a method body into (opcode, operand) pairs with System.Reflection operands, as Harmony's
    /// PatchProcessor.GetOriginalInstructions hands them to the plugin (FieldInfo, MethodBase, sbyte/int constants).</summary>
    static List<KeyValuePair<string, object>> Decode(MethodInfo method)
    {
        var il = method.GetMethodBody().GetILAsByteArray();
        var mod = method.Module;
        Type[] ta = method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
        var list = new List<KeyValuePair<string, object>>();
        int i = 0;
        while (i < il.Length)
        {
            short v = il[i++];
            if (v == 0xFE) v = unchecked((short)(0xFE00 | il[i++]));
            var op = Ops[v];
            object operand = null;
            switch (op.OperandType)
            {
                case SRE.OperandType.InlineNone: break;
                case SRE.OperandType.ShortInlineI: operand = op == SRE.OpCodes.Ldc_I4_S ? (object)(sbyte)il[i] : il[i]; i += 1; break;
                case SRE.OperandType.ShortInlineBrTarget: case SRE.OperandType.ShortInlineVar: i += 1; break;
                case SRE.OperandType.InlineVar: i += 2; break;
                case SRE.OperandType.InlineI: operand = BitConverter.ToInt32(il, i); i += 4; break;
                case SRE.OperandType.InlineBrTarget: case SRE.OperandType.InlineString: case SRE.OperandType.InlineSig: case SRE.OperandType.ShortInlineR: i += 4; break;
                case SRE.OperandType.InlineI8: case SRE.OperandType.InlineR: i += 8; break;
                case SRE.OperandType.InlineSwitch: i += 4 + 4 * BitConverter.ToInt32(il, i); break;
                case SRE.OperandType.InlineField: operand = mod.ResolveField(BitConverter.ToInt32(il, i), ta, null); i += 4; break;
                case SRE.OperandType.InlineMethod: operand = mod.ResolveMethod(BitConverter.ToInt32(il, i), ta, null); i += 4; break;
                case SRE.OperandType.InlineTok: case SRE.OperandType.InlineType: operand = mod.ResolveMember(BitConverter.ToInt32(il, i), ta, null); i += 4; break;
                default: throw new InvalidOperationException("operand type " + op.OperandType);
            }
            list.Add(new KeyValuePair<string, object>(op.Name, operand));
        }
        return list;
    }

    static void ReflectionView(Action<bool, string> check, List<string> cecilSkeleton)
    {
        var ctx = new RendererContext(Managed);
        try
        {
            var asm = ctx.LoadFromAssemblyPath(Path.Combine(Managed, "Renderite.Unity.dll"));
            var meshAsset = asm.GetType(UploadPatchSpec.MeshAssetType, true);
            var co = meshAsset.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
                              .Single(t => t.Name.StartsWith(UploadPatchSpec.CoroutinePrefix) && typeof(System.Collections.IEnumerator).IsAssignableFrom(t));
            var moveNext = co.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var code = Decode(moveNext);
            var skeleton = UploadPatchSpec.Skeleton(code);
            ulong hash = UploadPatchSpec.Hash(skeleton);
            int firstDiff = -1;
            if (cecilSkeleton != null)
                for (int k = 0; k < Math.Max(skeleton.Count, cecilSkeleton.Count); k++)
                    if (k >= skeleton.Count || k >= cecilSkeleton.Count || skeleton[k] != cecilSkeleton[k]) { firstDiff = k; break; }
            Console.WriteLine($"  as Harmony sees it (System.Reflection operands, {code.Count} instructions): skeleton {skeleton.Count} entries, hash {hash:x16}" +
                              (firstDiff >= 0 ? $"; first difference at {firstDiff}: '{(firstDiff < skeleton.Count ? skeleton[firstDiff] : "-")}' vs Cecil '{(firstDiff < cecilSkeleton.Count ? cecilSkeleton[firstDiff] : "-")}'" : ""));
            check(hash == UploadPatchSpec.VerifiedSkeletonHash && firstDiff < 0,
                  "the patch's own skeleton code (UploadPatchSpec.Entry on reflection operands, what it runs on Harmony's instructions in the renderer) gives the same hash as Cecil");
            // the fields the plugin reads through Harmony's FieldRefAccess, with their reflection types
            var ok = new[] { UploadPatchSpec.LastIsSkinnedField, UploadPatchSpec.FirstUploadCompletedField, UploadPatchSpec.MeshField, UploadPatchSpec.UploadDataField, UploadPatchSpec.MeshBufferField }
                .All(n => meshAsset.GetField(n, BindingFlags.Instance | BindingFlags.NonPublic) != null) &&
                co.GetField(UploadPatchSpec.StateField, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.FieldType == typeof(int) &&
                co.GetField(UploadPatchSpec.ThisField, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.FieldType == meshAsset;
            check(ok, "reflection finds every field the patch binds (MeshAsset's five, the coroutine's <>1__state and <>4__this)");
        }
        finally { ctx.Unload(); }
    }
}
