using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
namespace Quest3TriggerUI
{
    // Only LoadCo's presence check is non-acquiring. Real consumers remain native.
    internal static class ScenePrefabPresence
    {
        private static readonly FieldInfo Cache = typeof(SuperController).GetField("assetBundleAssetNameToPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static Harmony _harmony;
        private static long _avoided;
        internal static GameObject Read(SuperController owner, string bundle, string asset)
        {
            var cache = (Dictionary<string, GameObject>)Cache.GetValue(owner);
            GameObject value;
            if (cache == null || !cache.TryGetValue(bundle + ":" + asset, out value)) return null;
            if (!System.Object.ReferenceEquals(value, null))
            {
                _avoided++;
                if (_avoided <= 4 || _avoided % 64 == 0)
                    Log("cachedPresence extraLeaseAvoided=" + _avoided + " realConsumers=native countsNotBytes=True");
            }
            return value;
        }

        private static MethodInfo Target()
        {
            var type = typeof(SuperController).GetNestedType("<LoadCo>d__1347", BindingFlags.NonPublic);
            return type == null ? null : type.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        }

        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var acquire = typeof(SuperController).GetMethod("GetCachedPrefab", new[] { typeof(string), typeof(string) });
            var equality = typeof(UnityEngine.Object).GetMethod("op_Equality", new[] { typeof(UnityEngine.Object), typeof(UnityEngine.Object) });
            var peek = typeof(ScenePrefabPresence).GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static);
            int matches = 0;
            for (int i = 0; i + 3 < code.Count; i++)
            {
                if (!code[i].Calls(acquire)) continue;
                if (code[i + 1].opcode != OpCodes.Ldnull || !code[i + 2].Calls(equality) ||
                    (code[i + 3].opcode != OpCodes.Brfalse_S && code[i + 3].opcode != OpCodes.Brfalse))
                    throw new InvalidOperationException("LoadCo cached prefab call no longer a presence check");
                code[i].opcode = OpCodes.Call;
                code[i].operand = peek;
                matches++;
            }
            if (matches != 1) throw new InvalidOperationException("LoadCo prefab presence anchors=" + matches);
            return code;
        }

        internal static int CheckAnchor()
        {
            var original = PatchProcessor.GetOriginalInstructions(Target(), (ILGenerator)null);
            var rewritten = new List<CodeInstruction>(Rewrite(original));
            var peek = typeof(ScenePrefabPresence).GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static);
            int count = 0;
            foreach (var instruction in rewritten) if (instruction.Calls(peek)) count++;
            return count;
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            var target = Target();
            if (Cache == null || Cache.FieldType != typeof(Dictionary<string, GameObject>) ||
                target == null || target.ReturnType != typeof(bool))
                throw new MissingMemberException("LoadCo prefab presence mapping changed");
            _harmony = new Harmony("quest3triggerui.sceneprefabpresence." + typeof(ScenePrefabPresence).Namespace);
            try
            {
                _harmony.Patch(target, transpiler: new HarmonyMethod(typeof(ScenePrefabPresence), "Rewrite"));
                Log("installed presenceAnchors=1 realConsumers=native gcPolicy=unchanged");
            }
            catch { Shutdown(); throw; }
        }

        internal static void Shutdown()
        {
            if (_harmony == null) return;
            _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            _avoided = 0;
        }

        private static void Log(string value)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[scene-prefab-presence] " + value);
        }
    }
}
