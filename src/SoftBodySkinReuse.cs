using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal static class SoftBodySkinReuse
    {
        private const string Id = "Quest3TriggerUI.softbody-skin-reuse";
        private static Harmony harmony;
        private static volatile bool enabled;
        private static Accessors fields;
        private static WeakPlans plans;
        [ThreadStatic] private static Context frame;

        private sealed class Accessors
        {
            internal Func<DAZCharacterRun, DAZMergedSkinV2> Skin = Getter<DAZCharacterRun, DAZMergedSkinV2>("skinForThread");
            internal Func<DAZCharacterRun, Vector3[]> Original = Getter<DAZCharacterRun, Vector3[]>("mergedMeshMorphedUVVertices");
            internal Func<DAZCharacterRun, Vector3[]> Copy = Getter<DAZCharacterRun, Vector3[]>("mergedMeshMorphedUVVerticesCopy");
            internal Func<DAZSkinV2, bool> General = Getter<DAZSkinV2, bool>("_useGeneralWeights");
            internal Func<DAZSkinV2, bool> Changed = Getter<DAZSkinV2, bool>("postSkinVertsChangedThreaded");
            internal Func<DAZSkinV2, int[]> PostList = Getter<DAZSkinV2, int[]>("postSkinNeededVertsList");
            internal Func<DAZSkinV2, int> Count = Getter<DAZSkinV2, int>("numBaseVerts");
            internal Func<DAZSkinV2, Vector3[]> Working = Getter<DAZSkinV2, Vector3[]>("workingVerts");
        }

        private sealed class Context
        {
            internal int Depth;
            internal DAZCharacterRun Run;
            internal DAZSkinV2 Skin;
            internal Vector3[] Original, Raw;
            internal int[] PostList;
            internal bool Ready, Rebuild, AllowFull;
            internal void Clear()
            {
                Run = null; Skin = null; Original = null; Raw = null; PostList = null;
                Ready = Rebuild = AllowFull = false;
            }
        }

        private sealed class Plan
        {
            private bool[] basis;
            private int[] post;
            private int count;
            internal bool[] Remaining;
            internal int[] Reused;
            public Plan() { }

            internal bool Ensure(bool[] mask, int[] list, int n, bool rebuild)
            {
                if (!rebuild && ReferenceEquals(basis, mask) && ReferenceEquals(post, list) && count == n)
                    return Remaining != null;
                if (mask == null || list == null || n < 0 || mask.Length < n) return false;
                for (int i = 0; i < list.Length; i++)
                    if (list[i] < 0 || list[i] >= mask.Length) return false;
                var remaining = (bool[])mask.Clone();
                var selected = new List<int>();
                foreach (int vertex in list)
                {
                    // Post normals can require UV-only/range-external vertices.
                    if (vertex < n && remaining[vertex])
                    {
                        remaining[vertex] = false;
                        selected.Add(vertex);
                    }
                }
                basis = mask; post = list; count = n;
                Remaining = remaining; Reused = selected.ToArray();
                return true;
            }
        }

        // VaM's shipped mscorlib predates ConditionalWeakTable. Index by the
        // managed identity hash, verify collisions by reference, never by Unity ID.
        private sealed class WeakPlans
        {
            private sealed class Entry
            {
                internal WeakReference Owner;
                internal Plan Value;
            }
            private readonly Dictionary<int, List<Entry>> buckets = new Dictionary<int, List<Entry>>();
            internal Plan Get(DAZSkinV2 owner)
            {
                int key = RuntimeHelpers.GetHashCode(owner);
                lock (buckets)
                {
                    List<Entry> bucket;
                    if (buckets.TryGetValue(key, out bucket))
                        foreach (var entry in bucket)
                            if (ReferenceEquals(entry.Owner.Target, owner)) return entry.Value;
                    // Prune only on a cache miss/new owner, never scan owners each frame.
                    var empty = new List<int>();
                    foreach (var pair in buckets)
                    {
                        for (int i = pair.Value.Count - 1; i >= 0; i--)
                            if (pair.Value[i].Owner.Target == null) pair.Value.RemoveAt(i);
                        if (pair.Value.Count == 0) empty.Add(pair.Key);
                    }
                    foreach (int dead in empty) buckets.Remove(dead);
                    if (!buckets.TryGetValue(key, out bucket))
                    {
                        bucket = new List<Entry>(); buckets.Add(key, bucket);
                    }
                    var value = new Plan();
                    bucket.Add(new Entry { Owner = new WeakReference(owner), Value = value });
                    return value;
                }
            }
            internal void Remove(DAZSkinV2 owner)
            {
                int key = RuntimeHelpers.GetHashCode(owner);
                lock (buckets)
                {
                    List<Entry> bucket;
                    if (!buckets.TryGetValue(key, out bucket)) return;
                    for (int i = bucket.Count - 1; i >= 0; i--)
                        if (ReferenceEquals(bucket[i].Owner.Target, owner)) bucket.RemoveAt(i);
                    if (bucket.Count == 0) buckets.Remove(key);
                }
            }
        }

        private static Func<T, V> Getter<T, V>(string name)
        {
            FieldInfo field = AccessTools.Field(typeof(T), name);
            if (field == null || field.FieldType != typeof(V)) throw new InvalidOperationException("Field changed: " + name);
            var method = new DynamicMethod("skin_reuse_get_" + name, typeof(V), new[] { typeof(T) }, typeof(SoftBodySkinReuse), true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
            return (Func<T, V>)method.CreateDelegate(typeof(Func<T, V>));
        }

        internal static void Install()
        {
            if (harmony != null) return;
            try
            {
                string path = Path.Combine(Path.Combine(BepInEx.Paths.GameRootPath, "PerformancePatches"), "SkinMeshPartDLL.dll");
                string hash;
                using (var sha = SHA256.Create())
                using (var file = File.OpenRead(path)) hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                if (hash != "BE8893D9D73911AC35F68C1170E38E06F3A72B1CCF52B761D599A365391B5387")
                    throw new InvalidOperationException("Native skin kernel changed");
                fields = new Accessors();
                plans = new WeakPlans();
                harmony = new Harmony(Id);
                harmony.UnpatchAll(Id);
                harmony.Patch(AccessTools.Method(typeof(DAZSkinV2), "SkinMeshThreadedFast"),
                    transpiler: new HarmonyMethod(typeof(SoftBodySkinReuse), "RouteMask"));
                harmony.Patch(AccessTools.Method(typeof(DAZSkinV2), "SkinMeshPostVertsThreadedFast"),
                    prefix: new HarmonyMethod(typeof(SoftBodySkinReuse), "BeginPost"),
                    postfix: new HarmonyMethod(typeof(SoftBodySkinReuse), "FinishPost"));
                harmony.Patch(AccessTools.Method(typeof(DAZSkinV2), "OnDestroy"),
                    postfix: new HarmonyMethod(typeof(SoftBodySkinReuse), "RetireSkin"));
                harmony.Patch(AccessTools.Method(typeof(DAZCharacterRun), "RunThreaded"),
                    prefix: new HarmonyMethod(typeof(SoftBodySkinReuse), "BeginRun"),
                    finalizer: new HarmonyMethod(typeof(SoftBodySkinReuse), "EndRun"),
                    transpiler: new HarmonyMethod(typeof(SoftBodySkinReuse), "RouteFullCall"));
                enabled = true;
                Log("installed; methods=4 fullCall=1 nativeMask=1 nonGeneralOnly=True targetsEarly=preserved cache=weak ownerExit=release noProbe=True");
            }
            catch (Exception error) { Shutdown(); Log("not installed: " + error); }
        }

        private static IEnumerable<CodeInstruction> RouteFullCall(IEnumerable<CodeInstruction> input)
        {
            MethodInfo original = AccessTools.Method(typeof(DAZSkinV2), "SkinMeshThreadedFast");
            MethodInfo replacement = AccessTools.Method(typeof(SoftBodySkinReuse), "CallFull");
            int replaced = 0;
            foreach (var instruction in input)
            {
                if (instruction.Calls(original))
                {
                    instruction.opcode = OpCodes.Call; instruction.operand = replacement; replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1) throw new InvalidOperationException("Full skin call count=" + replaced);
        }

        private static IEnumerable<CodeInstruction> RouteMask(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input);
            FieldInfo mask = AccessTools.Field(typeof(DAZSkinV2), "isBaseVert");
            MethodInfo native = AccessTools.Method(typeof(NativeLibrary.Linked), "SkinMeshPartThreadedUnordered");
            MethodInfo selector = AccessTools.Method(typeof(SoftBodySkinReuse), "SelectMask");
            int replaced = 0;
            for (int i = 0; i < code.Count; i++)
            {
                yield return code[i];
                // Alter only the argument of this native call, not managed task masks.
                if (code[i].opcode == OpCodes.Ldfld && Equals(code[i].operand, mask)
                    && i + 2 < code.Count && code[i + 1].opcode == OpCodes.Ldnull && code[i + 2].Calls(native))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Call, selector);
                    replaced++;
                }
            }
            if (replaced != 1) throw new InvalidOperationException("Native mask anchor count=" + replaced);
        }

        private static void BeginRun(DAZCharacterRun __instance)
        {
            if (!enabled) return;
            if (frame == null) frame = new Context();
            frame.Depth++;
            frame.Clear();
            var access = fields;
            if (frame.Depth != 1 || access == null) return;
            frame.Run = __instance;
            frame.Skin = access.Skin(__instance);
            frame.Original = access.Original(__instance);
        }

        private static Exception EndRun(Exception __exception)
        {
            if (frame != null) { frame.Clear(); if (frame.Depth > 0) frame.Depth--; }
            return __exception;
        }

        private static void BeginPost(DAZSkinV2 __instance)
        {
            var c = frame; var access = fields;
            if (c == null) return;
            c.Ready = false;
            c.Rebuild = enabled && access != null && ReferenceEquals(c.Skin, __instance) && access.Changed(__instance);
        }

        private static void FinishPost(DAZSkinV2 __instance, Vector3[] verts)
        {
            var c = frame; var access = fields;
            if (!enabled || c == null || access == null || c.Depth != 1 || ReferenceEquals(c.Run, null)
                || !ReferenceEquals(c.Skin, __instance) || !ReferenceEquals(access.Copy(c.Run), verts)
                || ReferenceEquals(c.Original, verts) || access.General(__instance)) return;
            c.Raw = __instance.rawSkinnedWorkingVerts;
            c.PostList = access.PostList(__instance);
            c.Ready = true;
        }

        private static void CallFull(DAZSkinV2 skin, Vector3[] verts, bool forceSynchronous)
        {
            var c = frame;
            if (c != null) c.AllowFull = enabled && c.Ready && !forceSynchronous
                && ReferenceEquals(c.Skin, skin) && ReferenceEquals(c.Original, verts);
            try { skin.SkinMeshThreadedFast(verts, forceSynchronous); }
            finally { if (c != null) { c.AllowFull = c.Ready = false; c.Raw = null; c.PostList = null; } }
        }

        private static bool[] SelectMask(bool[] original, DAZSkinV2 skin, Vector3[] verts)
        {
            var c = frame; var access = fields; var cache = plans;
            if (!enabled || c == null || !c.AllowFull || !c.Ready || access == null || cache == null) return original;
            c.Ready = false;
            if (!ReferenceEquals(c.Skin, skin) || !ReferenceEquals(c.Original, verts)
                || access.General(skin) || !ReferenceEquals(access.Working(skin), verts)
                || !ReferenceEquals(c.Raw, skin.rawSkinnedWorkingVerts)
                || !ReferenceEquals(c.PostList, access.PostList(skin))) return original;
            int n = access.Count(skin);
            if (verts == null || c.Raw == null || n < 0 || verts.Length < n || c.Raw.Length < n
                || ReferenceEquals(verts, c.Raw)) return original;
            Plan plan = cache.Get(skin);
            if (!plan.Ensure(original, c.PostList, n, c.Rebuild) || plan.Reused.Length == 0) return original;
            foreach (int vertex in plan.Reused) verts[vertex] = c.Raw[vertex];
            return plan.Remaining;
        }

        private static void RetireSkin(DAZSkinV2 __instance)
        {
            var cache = plans;
            if (cache != null) cache.Remove(__instance);
        }

        internal static void Shutdown()
        {
            enabled = false;
            if (harmony != null) harmony.UnpatchAll(Id);
            harmony = null; plans = null; fields = null;
            if (frame != null) frame.Clear();
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[softbody-skin-reuse] " + message);
        }
    }
}
