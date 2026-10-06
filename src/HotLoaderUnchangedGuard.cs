using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace Quest3TriggerUI
{
    // Compatibility guard for the already-loaded 1.1 loader. New loader builds
    // perform the same one-shot check in Update; no process restart is required.
    [HarmonyPatch]
    internal static class HotLoaderUnchangedGuard
    {
        private static bool _seen;
        private static long _length, _ticks;
        private static MethodBase TargetMethod()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Split to avoid build_payload.py rewriting the loader's name
                // into the versioned payload namespace.
                Type type = assembly.GetType("Quest3Trigger" + "UI.HotLoader.HotUpdateLoaderPlugin");
                if (type != null) return AccessTools.Method(type, "TryLoadPayload");
            }
            throw new InvalidOperationException("Hot loader not found.");
        }
        internal static bool ShouldLoad(bool initialLoad, long length, long ticks)
        {
            if (!initialLoad && _seen && _length == length && _ticks == ticks) return false;
            _seen = true;
            _length = length;
            _ticks = ticks;
            return true;
        }
        private static bool Prefix(bool initialLoad, string ____payloadPath)
        {
            var file = new FileInfo(____payloadPath);
            return !file.Exists || ShouldLoad(initialLoad, file.Length, file.LastWriteTimeUtc.Ticks);
        }
    }
}
