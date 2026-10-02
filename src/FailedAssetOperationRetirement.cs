using System;
using System.Reflection;
using AssetBundles;
using HarmonyLib;

namespace Quest3TriggerUI
{
    // IsDone already treats a failed download as terminal. Align only Update's
    // queue-retention result; leave errors, requests and bundle leases intact.
    internal static class FailedAssetOperationRetirement
    {
        private static Harmony _harmony;
        private static readonly Type[] Types = {
            typeof(AssetBundleLoadAssetOperationFull),
            typeof(AssetBundleLoadLevelOperation),
            typeof(AssetBundleLoadManifestOperation)
        };
        private static readonly FieldInfo[] Errors = new FieldInfo[3];
        private static readonly FieldInfo[] Requests = new FieldInfo[3];
        private static long _retired;

        private static void AfterUpdate(object __instance, MethodBase __originalMethod, ref bool __result)
        {
            if (!__result || __instance.GetType() != __originalMethod.DeclaringType) return;
            for (int i = 0; i < Types.Length; i++)
            {
                if (Types[i] != __originalMethod.DeclaringType) continue;
                if (Requests[i].GetValue(__instance) != null || Errors[i].GetValue(__instance) == null) return;
                __result = false;
                _retired++;
                if (_retired <= 4 || _retired % 64 == 0)
                    Log("terminalFailure type=" + Types[i].Name + " total=" + _retired +
                        " queueRemoval=native errors=preserved bundleReferences=unchanged countsNotBytes=True");
                return;
            }
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var methods = new MethodInfo[3];
            for (int i = 0; i < Types.Length; i++)
            {
                Errors[i] = Types[i].GetField("m_DownloadingError", flags);
                Requests[i] = Types[i].GetField("m_Request", flags);
                methods[i] = Types[i].GetMethod("Update", flags | BindingFlags.DeclaredOnly);
                if (Errors[i] == null || Errors[i].FieldType != typeof(string) || Requests[i] == null ||
                    Requests[i].FieldType.FullName != (i == 1 ? "UnityEngine.AsyncOperation" : "UnityEngine.AssetBundleRequest") ||
                    methods[i] == null || methods[i].ReturnType != typeof(bool) || methods[i].GetParameters().Length != 0)
                    throw new MissingMemberException(Types[i].Name + " failure ownership changed");
            }
            _harmony = new Harmony("quest3triggerui.failedassets." + typeof(FailedAssetOperationRetirement).Namespace);
            try
            {
                foreach (var method in methods)
                    _harmony.Patch(method, postfix: new HarmonyMethod(typeof(FailedAssetOperationRetirement), "AfterUpdate"));
                Log("installed terminalMethods=3 activeRequests=preserved bundleReferences=unchanged gcPolicy=unchanged");
            }
            catch { Shutdown(); throw; }
        }

        internal static void Shutdown()
        {
            if (_harmony == null) return;
            _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            _retired = 0;
        }

        private static void Log(string value)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[failed-asset-ret] " + value);
        }
    }
}
