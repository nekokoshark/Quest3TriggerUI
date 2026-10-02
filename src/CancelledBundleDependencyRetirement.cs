using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using AssetBundles;
using HarmonyLib;

namespace Quest3TriggerUI
{
    // Defer dependency retirement until the original Update actually discards
    // a completed, still-zero-reference parent. Reacquisition stays native.
    internal static class CancelledBundleDependencyRetirement
    {
        private static Harmony _harmony;
        private static Action<string> _unloadDependencies;
        private static long _retired;

        private static bool RemoveCancelled(Dictionary<string, LoadedAssetBundle> tracked, string name)
        {
            LoadedAssetBundle parent;
            if (tracked.TryGetValue(name, out parent) && parent.m_ReferencedCount == 0)
            {
                _unloadDependencies(name);
                _retired++;
                if (_retired <= 4 || _retired % 64 == 0)
                    Log("completedCancelledParent total=" + _retired +
                        " dependencies=nativeUnloadDependencies activeConsumers=preserved countsNotBytes=True");
            }
            return tracked.Remove(name);
        }

        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var tracked = typeof(AssetBundleManager).GetField("m_TrackedAssetBundles", flags);
            var remove = typeof(Dictionary<string, LoadedAssetBundle>).GetMethod("Remove", new[] { typeof(string) });
            var replacement = typeof(CancelledBundleDependencyRetirement).GetMethod("RemoveCancelled", flags);
            int matches = 0;
            for (int i = 3; i < code.Count; i++)
            {
                if (!code[i].Calls(remove) || code[i - 3].opcode != OpCodes.Ldsfld ||
                    !Equals(code[i - 3].operand, tracked) ||
                    (code[i - 2].opcode != OpCodes.Ldloca && code[i - 2].opcode != OpCodes.Ldloca_S)) continue;
                var key = code[i - 1].operand as MethodInfo;
                if (code[i - 1].opcode != OpCodes.Call || key == null || key.Name != "get_Key" || key.ReturnType != typeof(string)) continue;
                // Keep the original labels/exception blocks and stack contract.
                code[i].opcode = OpCodes.Call;
                code[i].operand = replacement;
                matches++;
            }
            if (matches != 2) throw new InvalidOperationException("AssetBundleManager cancelled-parent anchors=" + matches);
            return code;
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var unload = typeof(AssetBundleManager).GetMethod("UnloadDependencies", flags, null, new[] { typeof(string) }, null);
            var tracked = typeof(AssetBundleManager).GetField("m_TrackedAssetBundles", flags);
            var update = typeof(AssetBundleManager).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
            if (unload == null || unload.ReturnType != typeof(void) || tracked == null ||
                tracked.FieldType != typeof(Dictionary<string, LoadedAssetBundle>) || update == null || update.ReturnType != typeof(void))
                throw new MissingMemberException("AssetBundleManager cancelled-parent ownership changed");
            _unloadDependencies = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), unload);
            _harmony = new Harmony("quest3triggerui.cancelledbundle." + typeof(CancelledBundleDependencyRetirement).Namespace);
            try
            {
                _harmony.Patch(update, transpiler: new HarmonyMethod(typeof(CancelledBundleDependencyRetirement), "Rewrite"));
                Log("installed completionAnchors=2 earlyCancellation=unchanged reacquisition=preserved gcPolicy=unchanged");
            }
            catch { Shutdown(); throw; }
        }

        internal static void Shutdown()
        {
            if (_harmony == null) return;
            _harmony.UnpatchAll(_harmony.Id); _harmony = null;
            _unloadDependencies = null; _retired = 0;
        }

        private static void Log(string value)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[cancelled-bundle-ret] " + value);
        }
    }
}
