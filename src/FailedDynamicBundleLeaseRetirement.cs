using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using AssetBundles;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // The original coroutine clears its lease marker immediately after GetAsset.
    // Retire a failed retrieval before that marker becomes unavailable to exit.
    internal static class FailedDynamicBundleLeaseRetirement
    {
        private static Harmony _harmony;
        private static readonly FieldInfo Started = typeof(JSONStorableDynamic).GetField("didStartLoadFromBundleAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo Registered = typeof(JSONStorableDynamic).GetField("didRegisterBundle", BindingFlags.NonPublic | BindingFlags.Instance);
        private static long _retired;

        internal static GameObject GetAssetResult(AssetBundleLoadAssetOperation request, JSONStorableDynamic owner)
        {
            GameObject result = request.GetAsset<GameObject>();
            if (result == null && owner != null && (bool)Started.GetValue(owner) && !(bool)Registered.GetValue(owner))
            {
                AssetBundleManager.UnloadAssetBundle(owner.assetBundleName);
                _retired++;
                if (_retired <= 4 || _retired % 64 == 0)
                    Log("missingGameObject leaseRetired=" + _retired + " sharedConsumers=preserved errors=unchanged countsNotBytes=True");
            }
            return result;
        }

        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var read = typeof(AssetBundleLoadAssetOperation).GetMethod("GetAsset").MakeGenericMethod(typeof(GameObject));
            var loadRequest = typeof(JSONStorableDynamic).GetField("loadRequest", BindingFlags.NonPublic | BindingFlags.Instance);
            var replacement = typeof(FailedDynamicBundleLeaseRetirement).GetMethod("GetAssetResult", BindingFlags.NonPublic | BindingFlags.Static);
            int matches = 0;
            for (int i = 2; i < code.Count; i++)
            {
                if (!code[i].Calls(read) || code[i - 1].opcode != OpCodes.Ldfld || !Equals(code[i - 1].operand, loadRequest) ||
                    code[i - 2].opcode != OpCodes.Ldloc_1) continue;
                // Preserve incoming labels/blocks: they must enter before the new owner argument.
                var owner = new CodeInstruction(OpCodes.Ldloc_1);
                owner.labels.AddRange(code[i].labels); code[i].labels.Clear();
                owner.blocks.AddRange(code[i].blocks); code[i].blocks.Clear();
                code.Insert(i, owner); i++;
                code[i].opcode = OpCodes.Call; code[i].operand = replacement;
                matches++;
            }
            if (matches != 1) throw new InvalidOperationException("JSONStorableDynamic asset retrieval anchors=" + matches);
            return code;
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            var iterator = typeof(JSONStorableDynamic).GetNestedType("<LoadFromBundleAsync>d__37", BindingFlags.NonPublic);
            var move = iterator == null ? null : iterator.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (Started == null || Started.FieldType != typeof(bool) || Registered == null || Registered.FieldType != typeof(bool) ||
                move == null || move.ReturnType != typeof(bool)) throw new MissingMemberException("JSONStorableDynamic bundle lease mapping changed");
            _harmony = new Harmony("quest3triggerui.dynamicbundlefailure." + typeof(FailedDynamicBundleLeaseRetirement).Namespace);
            try
            {
                _harmony.Patch(move, transpiler: new HarmonyMethod(typeof(FailedDynamicBundleLeaseRetirement), "Rewrite"));
                Log("installed failureAnchors=1 success=unchanged sharedConsumers=preserved gcPolicy=unchanged");
            }
            catch { Shutdown(); throw; }
        }

        internal static void Shutdown()
        {
            if (_harmony == null) return;
            _harmony.UnpatchAll(_harmony.Id); _harmony = null; _retired = 0;
        }

        private static void Log(string value)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[dynamic-bundle-failure] " + value);
        }
    }
}
