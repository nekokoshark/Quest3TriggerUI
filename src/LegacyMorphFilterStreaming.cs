using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using DynamicCSharp;
using HarmonyLib;
using MVR.FileManagement;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Runtime coverage follows audited code, not the location/version of a VAR.
    // No morph/bank/vertex is placed in a cache or persistent registry here.
    internal static class LegacyMorphFilterStreaming
    {
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal const int BufferBytes = 32768;
        private static readonly object Gate = new object();
        private static readonly Queue<Assembly> Pending = new Queue<Assembly>();
        private static readonly HashSet<Assembly> Queued = new HashSet<Assembly>();
        // These assemblies already have AppDomain lifetime on VaM's Mono.
        // Only identities are stored; no script instance/type/bank is cached.
        private static readonly HashSet<Assembly> Scanned = new HashSet<Assembly>();
        private static Harmony _harmony;
        private static int _methods, _files;
        private static bool _doubleSum;

        internal static bool Audited(MethodInfo method)
        {
            if (method == null || method.IsGenericMethod || method.ReturnType != typeof(bool) ||
                method.DeclaringType.Name != "BreastMorphListener") return false;
            string ns = method.DeclaringType.Namespace;
            if (ns != "TittyMagic" && (ns == null || !ns.EndsWith(".TittyMagic", StringComparison.Ordinal))) return false;
            ParameterInfo[] args = method.GetParameters();
            if (args.Length != 3 || args[0].ParameterType != typeof(DAZMorph) || args[2].ParameterType != typeof(float) ||
                (args[1].ParameterType != typeof(HashSet<int>) && args[1].ParameterType != typeof(ICollection<int>))) return false;
            string key = MorphFilterMethodAudit.Fingerprint(method);
            return key == "618B0A17865AB6CF03FB3B498054927D0F530DF65B3E17668FE6EC9CAAA9D54C" ||
                   key == "0FBACF42D48D319AE6D0BAA796C291F75D86251950C2EE8E7FB6FF38ED235DEA" ||
                   key == "CBD078F930D09025CB4C56C5466E2AD54D0C118893D70CADF35B5A1982117B04" ||
                   key == "D1728659218143D11B3AC8EC5FF23508D82222F093AAA6D0587BB06C1BDFAEC7";
        }

        private static void Enqueue(Assembly assembly)
        {
            lock (Gate)
            {
                if (_harmony == null || assembly == null || assembly is AssemblyBuilder) return;
                if (!Scanned.Contains(assembly) && Queued.Add(assembly)) Pending.Enqueue(assembly);
            }
        }

        private static void AssemblyLoaded(object sender, AssemblyLoadEventArgs args) { Enqueue(args.LoadedAssembly); }

        // The native compiler loads bytes then constructs this wrapper before
        // instantiating any script. Tick alone would miss initialization in the
        // same frame. This managed-only boundary also covers async completions.
        private static void Wrapped(ScriptAssembly __instance)
        {
            if (__instance != null) Inspect(__instance.RawAssembly);
        }

        internal static void Inspect(Assembly assembly)
        {
            lock (Gate)
            {
                if (_harmony == null || assembly == null || assembly is AssemblyBuilder) return;
                if (Scanned.Contains(assembly)) return;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException) { return; }
                foreach (Type type in types)
                {
                    if (type.Name != "BreastMorphListener") continue;
                    foreach (MethodInfo method in type.GetMethods(All | BindingFlags.DeclaredOnly))
                    {
                        try
                        {
                            if (!Audited(method)) continue;
                            var info = Harmony.GetPatchInfo(method);
                            // Existing source-only fixes have separately audited
                            // fingerprints. Other runtime instrumentation remains.
                            if (info != null && info.Owners.Count != 0) continue;
                            _harmony.Patch(method, prefix: new HarmonyMethod(typeof(LegacyMorphFilterStreaming).GetMethod("FilterPrefix", All)));
                            _methods++;
                            Log("patched assembly=" + assembly.GetName().Name + " method=" + type.FullName + "." + method.Name +
                                " fingerprint=" + MorphFilterMethodAudit.Fingerprint(method));
                        }
                        catch (Exception e) { Log("original retained method=" + type.FullName + "." + method.Name + " reason=" + e.GetType().Name); }
                    }
                }
                Scanned.Add(assembly);
            }
        }

        internal static void Tick()
        {
            // Only a load event creates work. Empty ticks do not enumerate
            // assemblies/types/scene objects or allocate collections.
            for (int i = 0; i < 8; i++)
            {
                Assembly assembly;
                lock (Gate)
                {
                    if (_harmony == null || Pending.Count == 0) return;
                    assembly = Pending.Dequeue(); Queued.Remove(assembly);
                }
                Inspect(assembly);
            }
        }

        private static bool FilterPrefix(DAZMorph __0, ICollection<int> __1, float __2, ref bool __result)
        {
            if (__0 == null || __0.deltas != null || __0.deltasLoaded || string.IsNullOrEmpty(__0.deltasLoadPath)) return true;
            var vertices = __1 as HashSet<int>;
            // A custom collection/comparer can have observable Contains side
            // effects. The original short circuit remains for those inputs.
            if (vertices == null || vertices.GetType() != typeof(HashSet<int>) ||
                !ReferenceEquals(vertices.Comparer, EqualityComparer<int>.Default)) return true;
            bool value;
            int count;
            try { value = Classify(__0.deltasLoadPath, vertices, __2, out count); }
            catch (Exception) { return true; }
            // Another original worker may have activated the morph while IO
            // ran. Prefer that real resident array, without clearing anything.
            if (__0.deltas != null || __0.deltasLoaded) return true;
            __result = value;
            if (Interlocked.Increment(ref _files) == 1)
                Log("first classification vertices=" + count + " residentDeltaArraysCreated=0 bufferBytes=" + BufferBytes + " realActivation=native");
            return false;
        }

        internal static bool Classify(string path, HashSet<int> vertices, float strength, out int count)
        {
            // One forward pass works on non-seekable VAR streams. Magnitudes
            // are nonnegative: the final matching sum reaches the original
            // threshold iff some matching prefix reaches it. NaN makes the
            // original total/threshold NaN as well. Keep the installed selector
            // Sum's accumulator precision and the original float hit rounding.
            using (FileEntryStream file = FileManager.OpenStream(path, true))
            using (var buffered = new BufferedStream(file.Stream, BufferBytes))
            using (var reader = new BinaryReader(buffered))
            {
                count = reader.ReadInt32();
                if (count < 0) throw new InvalidDataException("Negative morph delta count");
                double total = 0;
                float singleTotal = 0;
                float hit = 0;
                bool matched = false;
                for (int i = 0; i < count; i++)
                {
                    int vertex = reader.ReadInt32();
                    var delta = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    float magnitude = delta.magnitude;
                    if (_doubleSum) total += magnitude;
                    else singleTotal += magnitude;
                    if (vertices.Contains(vertex)) { matched = true; hit += magnitude; }
                }
                return matched && hit >= (_doubleSum ? (float)total : singleTotal) * strength;
            }
        }

        internal static void Install()
        {
            lock (Gate)
            {
                if (_harmony != null) return;
                try
                {
                    // Probe the exact selector overload and class specialization.
                    // This local data never enters a game morph or bank.
                    float sumProbe = new[] {
                        new DAZMorphVertex { delta = new Vector3(16777216f, 0, 0) },
                        new DAZMorphVertex { delta = new Vector3(1f, 0, 0) },
                        new DAZMorphVertex { delta = new Vector3(1f, 0, 0) }
                    }.Sum(x => x.delta.x);
                    if (sumProbe != 16777216f && sumProbe != 16777218f) throw new InvalidOperationException("Different selector Sum<float> rounding");
                    _doubleSum = sumProbe == 16777218f;
                    ConstructorInfo wrap = typeof(ScriptAssembly).GetConstructor(All, null, new[] { typeof(ScriptDomain), typeof(Assembly) }, null);
                    if (wrap == null) throw new MissingMethodException("ScriptAssembly wrapper constructor");
                    _harmony = new Harmony("Quest3TriggerUI.legacy-morph-filter." + typeof(LegacyMorphFilterStreaming).Namespace);
                    _harmony.Patch(wrap, postfix: new HarmonyMethod(typeof(LegacyMorphFilterStreaming).GetMethod("Wrapped", All)));
                    AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) Inspect(assembly);
                    Log("installed fingerprints=4 existingPatchedMethods=" + _methods + " futureWrapper=True sumRounding=PASS accumulator=" +
                        (_doubleSum ? "double" : "float") + " bufferBytes=" + BufferBytes);
                }
                catch (Exception e) { Shutdown(); Log("not installed: " + e.GetType().Name + ": " + e.Message); }
            }
        }

        internal static void Shutdown()
        {
            lock (Gate)
            {
                AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded;
                if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
                _harmony = null;
                Pending.Clear(); Queued.Clear();
                Scanned.Clear();
                _methods = _files = 0;
            }
        }

        private static void Log(string message)
        { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[legacy-morph-stream] " + message); }
    }
}
