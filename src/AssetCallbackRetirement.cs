using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MeshVR;
using UnityEngine;

namespace Quest3TriggerUI
{
    // The dispatcher advances its cursor before calling each completed request
    // but keeps the delivered prefix until >256 entries. Retire that prefix
    // after it yields, not the requests/targets still owned by consumers.
    internal static class AssetCallbackRetirement
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Queue = typeof(AssetLoader).GetField("_completionQueue", Instance);
        private static readonly FieldInfo Cursor = typeof(AssetLoader).GetField("_nextCallbackIndex", Instance);
        private static readonly FieldInfo Singleton = typeof(AssetLoader).GetField("singleton", BindingFlags.Static | BindingFlags.NonPublic);
        private static FieldInfo _dispatcherOwner;
        private static Harmony _harmony;
        private static long _retired, _deadTargets;

        private static int RetireDelivered(AssetLoader loader)
        {
            var queue = (List<AssetLoader.AssetBundleFromFileRequest>)Queue.GetValue(loader);
            int delivered = (int)Cursor.GetValue(loader);
            if (delivered == 0) return 0;
            if (queue == null || delivered < 0 || delivered > queue.Count)
                throw new InvalidOperationException("AssetLoader delivered cursor is outside completion queue");
            int deadTargets = 0;
            for (int n = 0; n < delivered; n++)
            {
                var callback = queue[n].callback;
                var target = callback == null ? null : callback.Target as UnityEngine.Object;
                if (!ReferenceEquals(target, null) && target == null) deadTargets++;
            }
            // Only the dispatcher-owned references disappear. Callback, bundle,
            // path, sequence and external copies of each request remain intact.
            queue.RemoveRange(0, delivered);
            Cursor.SetValue(loader, 0);
            _retired += delivered;
            _deadTargets += deadTargets;
            if (deadTargets != 0)
                Log("delivered-prefix retired=" + delivered + " nativeDeadTargets=" + deadTargets +
                    " remaining=" + queue.Count + " totalRetired=" + _retired + " countsNotUniqueBytes=True");
            return delivered;
        }

        private static void AfterDispatcher(object __instance)
        {
            // Native MoveNext drains ready callbacks, catches callback errors,
            // and returns at its normal wait. No interleaving occurs inside a
            // synchronous callback. Undelivered suffix order remains unchanged.
            var owner = (AssetLoader)_dispatcherOwner.GetValue(__instance);
            RetireDelivered(owner);
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            if (Queue == null || Cursor == null || Singleton == null ||
                Queue.FieldType != typeof(List<AssetLoader.AssetBundleFromFileRequest>) || Cursor.FieldType != typeof(int))
                throw new MissingFieldException("AssetLoader completion queue/cursor changed");
            Type dispatcher = null;
            foreach (var type in typeof(AssetLoader).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (type.Name.StartsWith("<CallbackDispatcher>", StringComparison.Ordinal))
                {
                    if (dispatcher != null) throw new InvalidOperationException("Multiple AssetLoader callback dispatchers");
                    dispatcher = type;
                }
            if (dispatcher == null) throw new MissingMemberException("AssetLoader.CallbackDispatcher state machine");
            _dispatcherOwner = dispatcher.GetField("<>4__this", Instance);
            var moveNext = dispatcher.GetMethod("MoveNext", Instance);
            if (_dispatcherOwner == null || _dispatcherOwner.FieldType != typeof(AssetLoader) ||
                moveNext == null || moveNext.ReturnType != typeof(bool))
                throw new MissingMemberException("AssetLoader callback dispatcher shape changed");
            _harmony = new Harmony("quest3triggerui.assetcallbacks." + typeof(AssetCallbackRetirement).Namespace);
            try
            {
                _harmony.Patch(moveNext, postfix: new HarmonyMethod(typeof(AssetCallbackRetirement), "AfterDispatcher"));
                var loader = (AssetLoader)Singleton.GetValue(null);
                int existing = loader != null ? RetireDelivered(loader) : 0;
                Log("installed dispatcher=1 existingDeliveredRetired=" + existing +
                    " completedHistory=0 pendingOrder=preserved requestFields=preserved bundleCounts=unchanged gcPolicy=unchanged");
            }
            catch { Shutdown(); throw; }
        }

        internal static void Shutdown()
        {
            if (_harmony == null) return;
            _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            _dispatcherOwner = null;
            Log("shutdown retired=" + _retired + " nativeDeadTargets=" + _deadTargets + " countsNotUniqueBytes=True");
            _retired = _deadTargets = 0;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[asset-callback-ret] " + message);
        }
    }
}
