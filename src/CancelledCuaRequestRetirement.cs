using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MeshVR;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal static class CancelledCuaRequestRetirement
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly FieldInfo Bundle = typeof(CustomUnityAssetLoader).GetField("assetBundle", All);
        private static readonly FieldInfo Url = typeof(CustomUnityAssetLoader).GetField("assetBundleUrl", All);
        private static readonly FieldInfo Request = typeof(CustomUnityAssetLoader).GetField("bundleRequest", All);
        private static readonly FieldInfo SceneRequest = typeof(CustomUnityAssetLoader).GetField("sceneRequest", All);
        private static readonly PropertyInfo Loading = typeof(CustomUnityAssetLoader).GetProperty("isLoading", All);
        private static readonly MethodInfo Remove = typeof(CustomUnityAssetLoader).GetMethod("Remove", All);
        private static readonly MethodInfo Clear = typeof(CustomUnityAssetLoader).GetMethod("ClearAssetBundle", All);
        private static readonly MethodInfo BundleCallback = typeof(CustomUnityAssetLoader).GetMethod("BundleRequestCallback", All);
        private static readonly MethodInfo SceneCallback = typeof(CustomUnityAssetLoader).GetMethod("SceneRequestCallback", All);
        private static readonly MethodInfo SceneQueue = typeof(AssetLoader).GetMethod("QueueLoadSceneIntoTransform", All);
        private static readonly FieldInfo Singleton = typeof(AssetLoader).GetField("singleton", All);
        private static readonly FieldInfo Queue = typeof(AssetLoader).GetField("sceneLoadIntoTransformQueue", All);
        private static readonly FieldInfo Busy = typeof(AssetLoader).GetField("_pendingSceneLoads", All);
        private static MethodInfo SceneMove;
        private static FieldInfo SceneArgument, SceneState, SceneCurrent;
        private static Harmony harmony;
        private static bool enabled;

        private sealed class SceneStatus
        {
            internal WeakReference Request, Owner, Bundle;
            internal string Url;
            internal bool Done;
        }
        private sealed class DeferredLease
        {
            internal string Url;
            internal readonly HashSet<AssetLoader.SceneLoadIntoTransformRequest> Requests = new HashSet<AssetLoader.SceneLoadIntoTransformRequest>();
            internal readonly HashSet<string> LegacyPaths = new HashSet<string>();
        }
        private static readonly List<SceneStatus> Status = new List<SceneStatus>();
        private static readonly List<DeferredLease> Leases = new List<DeferredLease>();
        private static readonly HashSet<string> LegacyPaths = new HashSet<string>();

        // The original dispatcher and duplicate-waiter path each deliver one owned URL lease.
        // The transferred callback retains no CUA owner and survives payload shutdown.
        private sealed class BundleRetirement
        {
            private readonly string path;
            private bool done;
            internal BundleRetirement(string value) { path = value; }
            internal void Complete(AssetLoader.AssetBundleFromFileRequest request)
            {
                if (done) return;
                done = true;
                try { AssetLoader.DoneWithAssetBundleFromFile(path); }
                finally { request.assetBundle = null; request.callback = null; }
            }
        }

        private static SceneStatus FindStatus(AssetLoader.SceneLoadIntoTransformRequest request)
        {
            SceneStatus found = null;
            for (int index = Status.Count - 1; index >= 0; index--)
            {
                object target = Status[index].Request.Target;
                if (target == null) { Status.RemoveAt(index); continue; }
                if (ReferenceEquals(target, request)) found = Status[index];
            }
            return found;
        }

        private static SceneStatus Observe(AssetLoader.SceneLoadIntoTransformRequest request)
        {
            var found = FindStatus(request);
            if (found != null || request.callback == null || request.callback.Method != SceneCallback) return found;
            var owner = request.callback.Target as CustomUnityAssetLoader;
            if (ReferenceEquals(owner, null)) return null;
            found = new SceneStatus { Request = new WeakReference(request), Owner = new WeakReference(owner),
                Bundle = new WeakReference(Bundle.GetValue(owner)), Url = Url.GetValue(owner) as string };
            Status.Add(found);
            return found;
        }

        private static void BeforeQueue(AssetLoader.SceneLoadIntoTransformRequest slr)
        {
            if (enabled) Observe(slr);
        }

        private static void Cancel(AssetLoader.SceneLoadIntoTransformRequest request)
        {
            request.requestCancelled = true;
            request.callback = null;
            request.transform = null;
        }

        private static void BeforeClear(CustomUnityAssetLoader __instance)
        {
            if (!enabled) return;
            var scene = SceneRequest.GetValue(__instance) as AssetLoader.SceneLoadIntoTransformRequest;
            var request = Request.GetValue(__instance) as AssetLoader.AssetBundleFromFileRequest;
            var bundle = Bundle.GetValue(__instance) as AssetBundle;
            string url = Url.GetValue(__instance) as string;
            var loader = Singleton.GetValue(null) as AssetLoader;
            var queue = ReferenceEquals(loader, null) ? null : Queue.GetValue(loader) as List<AssetLoader.SceneLoadIntoTransformRequest>;
            if (queue != null) foreach (var queued in queue) Observe(queued);
            var deferred = new DeferredLease { Url = url };
            bool changed = false;
            // SyncAssetName can replace sceneRequest while its cancelled predecessor still
            // runs. Preserve the owner's single bundle lease until ALL its requests end.
            foreach (var status in Status)
            {
                if (status.Done || !ReferenceEquals(status.Owner.Target, __instance)
                    || !ReferenceEquals(status.Bundle.Target, bundle) || status.Url != url) continue;
                var pending = status.Request.Target as AssetLoader.SceneLoadIntoTransformRequest;
                if (pending == null) continue;
                deferred.Requests.Add(pending);
                Cancel(pending);
                changed = true;
            }
            if (scene != null)
            {
                var status = FindStatus(scene);
                if (status != null && !status.Done) deferred.Requests.Add(scene);
                else if (queue != null && queue.Contains(scene)) deferred.Requests.Add(scene);
                Cancel(scene);
                SceneRequest.SetValue(__instance, null);
                changed = true;
            }
            if (!ReferenceEquals(bundle, null) && url != null)
            {
                // At hot installation, original workers have no request-identity observation
                // yet. Same-path serialization lets their terminal MoveNext drain these
                // bounded, conservative tickets, including replaced legacy scene requests.
                foreach (string path in LegacyPaths) deferred.LegacyPaths.Add(path);
                if (deferred.Requests.Count != 0 || deferred.LegacyPaths.Count != 0)
                {
                    Leases.Add(deferred);
                    Bundle.SetValue(__instance, null);
                    Url.SetValue(__instance, null);
                }
                Request.SetValue(__instance, null);
            }
            else if (ReferenceEquals(bundle, null) && request != null)
            {
                bool ours = request.callback != null && ReferenceEquals(request.callback.Target, __instance)
                    && request.callback.Method == BundleCallback;
                bool deliveredFailure = url == request.path && !(bool)Loading.GetValue(__instance, null)
                    && (ours || request.callback == null);
                if (deliveredFailure)
                {
                    Request.SetValue(__instance, null);
                    Url.SetValue(__instance, null);
                    request.callback = null;
                    request.assetBundle = null;
                    AssetLoader.DoneWithAssetBundleFromFile(request.path);
                }
                else if (ours)
                {
                    request.callback = new BundleRetirement(request.path).Complete;
                    Request.SetValue(__instance, null);
                    changed = true;
                }
            }
            if (changed) Loading.SetValue(__instance, false, null);
        }

        private static Exception AfterBundleCallback(CustomUnityAssetLoader __instance,
            AssetLoader.AssetBundleFromFileRequest abffr, Exception __exception)
        {
            // Completed queue entries must not retain the owner or its native bundle. The
            // CUA already adopted its lease; bundleRequest.path remains for original UI logic.
            if (abffr.callback != null && ReferenceEquals(abffr.callback.Target, __instance)
                && abffr.callback.Method == BundleCallback) abffr.callback = null;
            abffr.assetBundle = null;
            return __exception;
        }

        private static bool BeforeSceneCallback(AssetLoader.SceneLoadIntoTransformRequest slr) { return !slr.requestCancelled; }

        private static bool BeforeScene(object __instance, ref bool __result)
        {
            var request = (AssetLoader.SceneLoadIntoTransformRequest)SceneArgument.GetValue(__instance);
            if (enabled) Observe(request);
            if (request.requestCancelled && (int)SceneState.GetValue(__instance) == 0)
            {
                SceneState.SetValue(__instance, -1);
                SceneCurrent.SetValue(__instance, null);
                __result = false;
                return false;
            }
            return true;
        }

        private static void AfterScene(object __instance, bool __result)
        {
            if (!__result) FinishScene((AssetLoader.SceneLoadIntoTransformRequest)SceneArgument.GetValue(__instance));
        }

        private static Exception AfterSceneException(object __instance, Exception __exception)
        {
            if (__exception != null)
            {
                try { FinishScene((AssetLoader.SceneLoadIntoTransformRequest)SceneArgument.GetValue(__instance)); }
                catch (Exception retirementError)
                {
                    if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogError("[cua-exit] scene retirement error while preserving original exception: " + retirementError);
                }
            }
            return __exception;
        }

        private static void FinishScene(AssetLoader.SceneLoadIntoTransformRequest request)
        {
            var status = FindStatus(request);
            if (status != null)
            {
                status.Done = true; status.Owner = null; status.Bundle = null; status.Url = null;
                request.callback = null; request.transform = null;
            }
            LegacyPaths.Remove(request.scenePath);
            foreach (var lease in Leases)
            {
                lease.Requests.Remove(request);
                lease.LegacyPaths.Remove(request.scenePath);
            }
            // Remove before native release; reentrant Clear and terminal notifications cannot
            // double-return a ticket or invalidate an enumeration through this list.
            while (true)
            {
                DeferredLease finished = null;
                foreach (var lease in Leases)
                    if (lease.Requests.Count == 0 && lease.LegacyPaths.Count == 0) { finished = lease; break; }
                if (finished == null) break;
                Leases.Remove(finished);
                AssetLoader.DoneWithAssetBundleFromFile(finished.Url);
            }
            DrainShutdown();
        }

        private static void DrainShutdown()
        {
            if (enabled || Leases.Count != 0 || harmony == null) return;
            harmony.UnpatchAll(harmony.Id);
            harmony = null;
            Status.Clear();
            LegacyPaths.Clear();
        }

        internal static void Install()
        {
            if (enabled) return;
            if (harmony != null) throw new InvalidOperationException("old CUA scene retirement tail still draining");
            foreach (var field in new[] { Bundle, Url, Request, SceneRequest, Singleton, Queue, Busy })
                if (field == null) throw new MissingMemberException("CUA request ownership fields changed");
            if (Bundle.FieldType != typeof(AssetBundle) || Url.FieldType != typeof(string)
                || Request.FieldType != typeof(AssetLoader.AssetBundleFromFileRequest)
                || SceneRequest.FieldType != typeof(AssetLoader.SceneLoadIntoTransformRequest)
                || Queue.FieldType != typeof(List<AssetLoader.SceneLoadIntoTransformRequest>)
                || Busy.FieldType != typeof(Dictionary<string, bool>) || Loading == null
                || Remove == null || Clear == null || SceneQueue == null || SceneCallback == null || BundleCallback == null)
                throw new MissingMemberException("CUA request ownership types changed");
            Type iterator = null;
            foreach (var type in typeof(AssetLoader).GetNestedTypes(All))
                if (type.Name.StartsWith("<LoadSceneIntoTransformAsync>", StringComparison.Ordinal))
                { if (iterator != null) throw new InvalidOperationException("ambiguous scene iterator"); iterator = type; }
            if (iterator == null) throw new MissingMemberException("scene iterator missing");
            SceneMove = iterator.GetMethod("MoveNext", All);
            SceneArgument = iterator.GetField("slr", All);
            SceneState = iterator.GetField("<>1__state", All);
            SceneCurrent = iterator.GetField("<>2__current", All);
            if (SceneMove == null || SceneArgument == null || SceneArgument.FieldType != typeof(AssetLoader.SceneLoadIntoTransformRequest)
                || SceneState == null || SceneState.FieldType != typeof(int) || SceneCurrent == null || SceneCurrent.FieldType != typeof(object))
                throw new MissingMemberException("scene iterator layout changed");
            var loader = Singleton.GetValue(null) as AssetLoader;
            var busy = ReferenceEquals(loader, null) ? null : Busy.GetValue(loader) as Dictionary<string, bool>;
            if (busy != null) foreach (var pair in busy) if (pair.Value) LegacyPaths.Add(pair.Key);
            harmony = new Harmony("quest3triggerui.cuaexit." + typeof(CancelledCuaRequestRetirement).Namespace);
            try
            {
                harmony.Patch(Remove, prefix: new HarmonyMethod(typeof(CancelledCuaRequestRetirement), "BeforeClear"));
                harmony.Patch(Clear, prefix: new HarmonyMethod(typeof(CancelledCuaRequestRetirement), "BeforeClear"));
                harmony.Patch(SceneQueue, prefix: new HarmonyMethod(typeof(CancelledCuaRequestRetirement), "BeforeQueue"));
                harmony.Patch(BundleCallback, finalizer: new HarmonyMethod(typeof(CancelledCuaRequestRetirement), "AfterBundleCallback"));
                harmony.Patch(SceneCallback, prefix: new HarmonyMethod(typeof(CancelledCuaRequestRetirement), "BeforeSceneCallback"));
                harmony.Patch(SceneMove, prefix: new HarmonyMethod(typeof(CancelledCuaRequestRetirement), "BeforeScene"),
                    postfix: new HarmonyMethod(typeof(CancelledCuaRequestRetirement), "AfterScene"),
                    finalizer: new HarmonyMethod(typeof(CancelledCuaRequestRetirement), "AfterSceneException"));
                enabled = true;
                if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[cua-exit] installed actualConsumer=True bundleRetirement=onCallback sceneRetirement=allTerminated sharedLeases=native hotLegacyScene=True gcPolicy=unchanged");
            }
            catch { harmony.UnpatchAll(harmony.Id); harmony = null; LegacyPaths.Clear(); throw; }
        }

        internal static void Shutdown()
        {
            enabled = false;
            if (harmony == null) return;
            harmony.Unpatch(Remove, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(Clear, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(SceneQueue, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(BundleCallback, HarmonyPatchType.Finalizer, harmony.Id);
            harmony.Unpatch(SceneCallback, HarmonyPatchType.Prefix, harmony.Id);
            DrainShutdown();
        }
    }
}
