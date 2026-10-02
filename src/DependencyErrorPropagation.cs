using System;
using System.Collections.Generic;
using System.Reflection;
using AssetBundles;
using HarmonyLib;

namespace Quest3TriggerUI
{
    // Forward the dependency's existing terminal error without changing leases.
    internal static class DependencyErrorPropagation
    {
        private static Harmony _harmony;
        private static long _propagated;
        private static readonly FieldInfo Loaded = typeof(AssetBundleManager).GetField("m_LoadedAssetBundles", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo Dependencies = typeof(AssetBundleManager).GetField("m_Dependencies", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo Errors = typeof(AssetBundleManager).GetField("m_DownloadingErrors", BindingFlags.NonPublic | BindingFlags.Static);

        private static void AfterLookup(string assetBundleName, ref string error, ref LoadedAssetBundle __result)
        {
            if (error != null) return;
            var loaded = (Dictionary<string, LoadedAssetBundle>)Loaded.GetValue(null);
            LoadedAssetBundle parent;
            if (!loaded.TryGetValue(assetBundleName, out parent) || parent == null) return;
            var dependencies = (Dictionary<string, string[]>)Dependencies.GetValue(null);
            string[] names;
            if (!dependencies.TryGetValue(assetBundleName, out names)) return;
            var errors = (Dictionary<string, string>)Errors.GetValue(null);
            foreach (string name in names)
            {
                string dependencyError;
                if (!errors.TryGetValue(name, out dependencyError) || dependencyError == null) continue;
                error = dependencyError;
                __result = null;
                _propagated++;
                if (_propagated <= 4 || _propagated % 64 == 0)
                    Log("terminalDependencyError total=" + _propagated + " queueRemoval=native sharedCounts=unchanged countsNotBytes=True");
                return;
            }
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            var method = typeof(AssetBundleManager).GetMethod("GetLoadedAssetBundle", new[] { typeof(string), typeof(string).MakeByRefType() });
            if (Loaded == null || Loaded.FieldType != typeof(Dictionary<string, LoadedAssetBundle>) ||
                Dependencies == null || Dependencies.FieldType != typeof(Dictionary<string, string[]>) ||
                Errors == null || Errors.FieldType != typeof(Dictionary<string, string>) ||
                method == null || method.ReturnType != typeof(LoadedAssetBundle))
                throw new MissingMemberException("AssetBundle dependency error mapping changed");
            _harmony = new Harmony("quest3triggerui.dependencyerror." + typeof(DependencyErrorPropagation).Namespace);
            try
            {
                _harmony.Patch(method, postfix: new HarmonyMethod(typeof(DependencyErrorPropagation), "AfterLookup"));
                Log("installed lookupMethods=1 sharedCounts=unchanged parentErrors=preserved gcPolicy=unchanged");
            }
            catch { Shutdown(); throw; }
        }

        internal static void Shutdown()
        {
            if (_harmony == null) return;
            _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            _propagated = 0;
        }

        private static void Log(string value)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[dependency-error] " + value);
        }
    }
}
