using System;
using System.Collections.Generic;
using System.Reflection;
using AssetBundles;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Only explicit unloads and the original unload-on-disable policy cancel a
    // no-instance load. A pending operation keeps its lease until completion.
    internal static class InterruptedDynamicBundleRetirement
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo Instance = typeof(JSONStorableDynamic).GetField("instance", InstanceFlags);
        private static readonly FieldInfo Started = typeof(JSONStorableDynamic).GetField("didStartLoadFromBundleAsync", InstanceFlags);
        private static readonly FieldInfo Registered = typeof(JSONStorableDynamic).GetField("didRegisterBundle", InstanceFlags);
        private static readonly FieldInfo Request = typeof(JSONStorableDynamic).GetField("loadRequest", InstanceFlags);
        private static readonly MethodInfo ClearFlag = typeof(JSONStorableDynamic).GetMethod("ClearLoadFlag", InstanceFlags);
        private static readonly MethodInfo Unload = typeof(JSONStorableDynamic).GetMethod("UnloadInstance", InstanceFlags);
        private static readonly MethodInfo Disable = typeof(JSONStorableDynamic).GetMethod("OnDisable", InstanceFlags);
        private static readonly MethodInfo Update = typeof(AssetBundleManager).GetMethod("Update", InstanceFlags);
        private static readonly List<KeyValuePair<string, AssetBundleLoadAssetOperation>> Pending =
            new List<KeyValuePair<string, AssetBundleLoadAssetOperation>>();
        private static Harmony _harmony;
        private static bool _enabled;
        private static bool _draining;
        private static long _retired;

        private static void BeforeUnload(JSONStorableDynamic __instance)
        {
            CancelUninstantiated(__instance);
        }

        private static void BeforeDisable(JSONStorableDynamic __instance)
        {
            // Do not turn temporary deactivation, locked pooling or a warm-cache
            // preference into an unconditional unload.
            if (!Application.isPlaying || __instance.neverUnloadOnDisable || !__instance.unloadOnDisable)
                return;
            Atom atom = __instance.containingAtom;
            if (atom != null && atom.on && atom.gameObject.activeSelf)
                CancelUninstantiated(__instance);
        }

        private static void CancelUninstantiated(JSONStorableDynamic owner)
        {
            if (owner == null || (Transform)Instance.GetValue(owner) != null ||
                (bool)Registered.GetValue(owner) || !(bool)Started.GetValue(owner))
                return;
            var request = (AssetBundleLoadAssetOperation)Request.GetValue(owner);
            if (request == null) return;

            // enabled=false alone does not stop Unity coroutines. Stop first so
            // the old continuation cannot resume against a new request field.
            owner.StopAllCoroutines();
            ClearFlag.Invoke(owner, null);
            Pending.Add(new KeyValuePair<string, AssetBundleLoadAssetOperation>(owner.assetBundleName, request));
            Request.SetValue(owner, null);
            Started.SetValue(owner, false);
            Drain();
        }

        private static void Drain()
        {
            if (_draining) return;
            _draining = true;
            try
            {
                for (int i = Pending.Count - 1; i >= 0; i--)
                {
                    var item = Pending[i];
                    if (!item.Value.IsDone()) continue;
                    // Remove before releasing: native unload callbacks may reenter.
                    Pending.RemoveAt(i);
                    AssetBundleManager.UnloadAssetBundle(item.Key);
                    _retired++;
                    if (_retired <= 4 || _retired % 64 == 0)
                        Log("retired=" + _retired + " pending=" + Pending.Count +
                            " sharedConsumers=native noInstanceOnly=True countsNotBytes=True");
                }
            }
            finally { _draining = false; }
        }

        private static void AfterManagerUpdate()
        {
            Drain();
            FinishShutdown();
        }

        private static void FinishShutdown()
        {
            if (_enabled || Pending.Count != 0 || _harmony == null) return;
            _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
        }

        private static void CheckField(FieldInfo field, Type expected)
        {
            if (field == null || field.IsStatic || field.FieldType != expected)
                throw new MissingMemberException("JSONStorableDynamic interrupted-load field mapping changed");
        }

        internal static void Install()
        {
            if (_enabled) return;
            CheckField(Instance, typeof(Transform));
            CheckField(Started, typeof(bool));
            CheckField(Registered, typeof(bool));
            CheckField(Request, typeof(AssetBundleLoadAssetOperation));
            foreach (var method in new[] { ClearFlag, Unload, Disable, Update })
                if (method == null || method.IsStatic || method.ReturnType != typeof(void) || method.GetParameters().Length != 0)
                    throw new MissingMemberException("Interrupted-load lifecycle mapping changed");
            bool newPump = _harmony == null;
            if (newPump)
                _harmony = new Harmony("quest3triggerui.interrupted-dynamic." + typeof(InterruptedDynamicBundleRetirement).Namespace);
            try
            {
                if (newPump)
                    _harmony.Patch(Update, postfix: new HarmonyMethod(typeof(InterruptedDynamicBundleRetirement), "AfterManagerUpdate"));
                _harmony.Patch(Unload, prefix: new HarmonyMethod(typeof(InterruptedDynamicBundleRetirement), "BeforeUnload"));
                _harmony.Patch(Disable, prefix: new HarmonyMethod(typeof(InterruptedDynamicBundleRetirement), "BeforeDisable"));
                _enabled = true;
                Log("installed noInstanceOnly=True unloadPolicy=original pendingRelease=afterCompletion sharedConsumers=native gcPolicy=unchanged");
            }
            catch { Shutdown(); throw; }
        }

        internal static void Shutdown()
        {
            _enabled = false;
            if (_harmony == null) return;
            _harmony.Unpatch(Unload, HarmonyPatchType.Prefix, _harmony.Id);
            _harmony.Unpatch(Disable, HarmonyPatchType.Prefix, _harmony.Id);
            // Keep only the completion pump for already transferred leases.
            // It removes itself when drained; a new payload may install its own
            // pump without owning or releasing any of these operations twice.
            FinishShutdown();
        }

        private static void Log(string value)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[interrupted-dynamic] " + value);
        }
    }
}
