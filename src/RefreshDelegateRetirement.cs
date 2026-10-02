using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MVR.FileManagement;

namespace Quest3TriggerUI
{
    // Retire only historical forward links in this known static registry.
    // Every current prev-chain subscriber, including duplicates, is retained.
    internal static class RefreshDelegateRetirement
    {
        private static readonly FieldInfo Previous = typeof(MulticastDelegate)
            .GetField("prev", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Next = typeof(MulticastDelegate)
            .GetField("kpm_next", BindingFlags.Instance | BindingFlags.NonPublic);
        private static Harmony _harmony;
        private static long _rewrites, _forwardLinks;

        private static void AfterUnregister(ref MVR.FileManagementSecure.OnRefresh ___onRefreshHandlers)
        {
            var observed = ___onRefreshHandlers;
            if (observed == null) return;
            int links = 0, subscribers = 0;
            for (Delegate node = observed; node != null;
                node = Previous.GetValue(node) as Delegate)
            {
                subscribers++;
                if (Next.GetValue(node) != null) links++;
            }
            if (links == 0) return;
            Delegate head = null, tail = null;
            for (Delegate node = observed; node != null;
                node = Previous.GetValue(node) as Delegate)
            {
                Delegate copy = (Delegate)node.Clone();
                Previous.SetValue(copy, null);
                Next.SetValue(copy, null);
                if (tail == null) head = copy;
                else Previous.SetValue(tail, copy);
                tail = copy;
            }
            var replacement = (MVR.FileManagementSecure.OnRefresh)head;
            // Do not overwrite a concurrently replaced field or mutate aliases.
            if (!ReferenceEquals(Interlocked.CompareExchange(
                ref ___onRefreshHandlers, replacement, observed), observed)) return;
            _rewrites++;
            _forwardLinks += links;
            Log("normalized subscribers=" + subscribers + " historicalForwardLinks=" + links +
                " activeCallbacks=preserved originalNodes=unchanged countsNotBytes=True");
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            if (Previous == null || Next == null)
                throw new MissingFieldException("Legacy Mono delegate links changed");
            var field = typeof(FileManager).GetField("onRefreshHandlers", BindingFlags.Static | BindingFlags.NonPublic);
            var method = typeof(FileManager).GetMethod("UnregisterRefreshHandler", BindingFlags.Static | BindingFlags.Public);
            if (field == null || field.FieldType != typeof(MVR.FileManagementSecure.OnRefresh) || method == null)
                throw new MissingMemberException("FileManager refresh registry changed");
            _harmony = new Harmony("quest3triggerui.refreshdelegates." + typeof(RefreshDelegateRetirement).Namespace);
            try
            {
                _harmony.Patch(method, postfix: new HarmonyMethod(typeof(RefreshDelegateRetirement), "AfterUnregister"));
                // Native Remove(null) leaves subscriptions unchanged. The postfix
                // normalizes existing history through a real static-field ref.
                FileManager.UnregisterRefreshHandler(null);
                Log("installed unregister=1 existingRewrites=" + _rewrites +
                    " existingHistoricalForwardLinks=" + _forwardLinks + " callbacksNotInvoked=True gcPolicy=unchanged");
            }
            catch { Shutdown(); throw; }
        }

        internal static void Shutdown()
        {
            if (_harmony == null) return;
            _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            Log("shutdown rewrites=" + _rewrites + " historicalForwardLinks=" + _forwardLinks + " countsNotBytes=True");
            _rewrites = _forwardLinks = 0;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[refresh-delegate-ret] " + message);
        }
    }
}
