using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace Quest3TriggerUI
{
    // Only immutable target contents are shared. Formula objects and arrays stay private.
    internal static class MorphTargetStringReuse
    {
        private const int MaxEntries = 4096;
        private const int MaxLength = 512;
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly object Gate = new object();
        // Integer keys and weak values never keep a target string alive by themselves.
        private static readonly Dictionary<int, List<WeakReference>> Buckets = new Dictionary<int, List<WeakReference>>();
        private static readonly Queue<int> Sweep = new Queue<int>();
        private static readonly FieldInfo Target = typeof(DAZMorphFormula).GetField("target");
        private static Harmony harmony;
        private static volatile bool enabled;
        private static int entries;
        private static long hits, misses, refused;

        internal static string Status
        {
            get
            {
                lock (Gate)
                    return "[morph-target-reuse] enabled=" + enabled + " weakEntries=" + entries +
                        " hits=" + hits + " misses=" + misses + " refused=" + refused +
                        " maxEntries=" + MaxEntries + " maxUTF16=" + MaxLength + " formulaObjects=private";
            }
        }

        private static void Prune(List<WeakReference> bucket)
        {
            for (int i = bucket.Count - 1; i >= 0; i--)
                if (!bucket[i].IsAlive) { bucket.RemoveAt(i); entries--; }
        }

        private static void SweepSome()
        {
            for (int i = 0; i < 4 && Sweep.Count != 0; i++)
            {
                int key = Sweep.Dequeue();
                var bucket = Buckets[key];
                Prune(bucket);
                if (bucket.Count == 0) Buckets.Remove(key);
                else Sweep.Enqueue(key);
            }
        }

        internal static string Canonical(string value)
        {
            if (!enabled || value == null || value.Length == 0 || value.Length > MaxLength) return value;
            int hash = StringComparer.Ordinal.GetHashCode(value);
            lock (Gate)
            {
                if (!enabled) return value;
                List<WeakReference> bucket;
                if (Buckets.TryGetValue(hash, out bucket))
                    foreach (var weak in bucket)
                    {
                        var found = weak.Target as string;
                        if (found != null && string.Equals(found, value, StringComparison.Ordinal))
                        {
                            hits++;
                            return found;
                        }
                    }
                misses++;
                SweepSome();
                if (entries == MaxEntries) { refused++; return value; }
                if (!Buckets.TryGetValue(hash, out bucket))
                {
                    bucket = new List<WeakReference>(1);
                    Buckets.Add(hash, bucket);
                    Sweep.Enqueue(hash);
                }
                bucket.Add(new WeakReference(value));
                entries++;
                return value;
            }
        }

        private static bool OwnCanonical(CodeInstruction code)
        {
            var method = code.operand as MethodInfo;
            return code.opcode == OpCodes.Call && method != null && method.Name == "Canonical" &&
                method.DeclaringType.Name == "MorphTargetStringReuse" && method.DeclaringType.Namespace != null &&
                (method.DeclaringType.Namespace == "Quest3TriggerUI" || method.DeclaringType.Namespace.StartsWith("Quest3TriggerUI.v", StringComparison.Ordinal));
        }

        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var received = new List<CodeInstruction>(instructions);
            var codes = received.ConvertAll(code => new CodeInstruction(code));
            int expected = __originalMethod.Name == "ProcessFormula" ? 2 : 1;
            int anchors = 0;
            var canonical = typeof(MorphTargetStringReuse).GetMethod("Canonical", All);
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode != OpCodes.Stfld || !Target.Equals(codes[i].operand)) continue;
                if (codes[i].blocks.Count != 0) throw new InvalidDataException("Target store exception boundary changed");
                if (i > 0 && OwnCanonical(codes[i - 1])) codes[i - 1].operand = canonical;
                else
                {
                    var call = new CodeInstruction(OpCodes.Call, canonical);
                    call.labels.AddRange(codes[i].labels);
                    codes[i].labels.Clear();
                    codes.Insert(i++, call);
                }
                anchors++;
            }
            if (anchors != expected) throw new InvalidDataException("Target store anchors changed: " + __originalMethod.Name + "=" + anchors);
            return codes;
        }

        private static void Require(MethodInfo method, string expected)
        {
            var body = method.GetMethodBody();
            var text = new StringBuilder(BitConverter.ToString(body.GetILAsByteArray()));
            text.Append('|').Append(body.InitLocals);
            foreach (var local in body.LocalVariables) text.Append('|').Append(local.LocalType.FullName).Append(':').Append(local.IsPinned);
            foreach (var clause in body.ExceptionHandlingClauses)
            {
                text.Append('|').Append(clause.Flags).Append(':').Append(clause.TryOffset).Append(':').Append(clause.TryLength)
                    .Append(':').Append(clause.HandlerOffset).Append(':').Append(clause.HandlerLength);
                if (clause.Flags == ExceptionHandlingClauseOptions.Clause) text.Append(':').Append(clause.CatchType.FullName);
            }
            using (var hash = SHA256.Create())
                if (BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "") != expected)
                    throw new InvalidDataException("Morph method fingerprint changed: " + method.Name);
        }

        internal static void Install()
        {
            if (harmony != null) return;
            var methods = new[] {
                typeof(DAZMorph).GetMethod("LoadMetaFromJSON", All),
                typeof(DAZMorph).GetMethod("Import", All),
                typeof(DAZMorph).GetMethod("ProcessFormula", All)
            };
            var fingerprints = new[] {
                "6CDB090D6875CA1267842F80AEE6AED42BEC6D1D4A69A3F6BA2EAA77F10EC9FF",
                "5E56E79559B01E58B6BDBDCC42EAA39134E7444CD66C44D44846C18993EB9CDB",
                "D06152192D72A3C1B8626F72759193B107BE59FD0499E8A4CF6AD61707607DBD"
            };
            if (Target == null || Target.FieldType != typeof(string)) throw new MissingFieldException("Formula.target changed");
            for (int i = 0; i < methods.Length; i++) Require(methods[i], fingerprints[i]);
            harmony = new Harmony("quest3triggerui.morphtargetreuse." + typeof(MorphTargetStringReuse).Namespace);
            try
            {
                foreach (var method in methods)
                    harmony.Patch(method, transpiler: new HarmonyMethod(typeof(MorphTargetStringReuse).GetMethod("Rewrite", All)));
                enabled = true;
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo(Status + " installedMethods=3 targetStores=4 gcPolicy=unchanged");
            }
            catch { Shutdown(); throw; }
        }

        internal static void Shutdown()
        {
            enabled = false;
            try { if (harmony != null) harmony.UnpatchAll(harmony.Id); }
            finally
            {
                harmony = null;
                lock (Gate) { Buckets.Clear(); Sweep.Clear(); entries = 0; }
            }
        }
    }
}
