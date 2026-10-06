using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Optional cold service. No reference to its DLL is emitted into the UI assembly.
    internal static class MemoryRuntimeLink
    {
        private static Assembly runtime;
        private static object owner;
        private static float nextRetry;
        private static readonly Dictionary<string, MethodInfo> methods = new Dictionary<string, MethodInfo>();
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        internal static void Attach(object token)
        { owner = token; Connect(); }
        internal static void Tick()
        {
            if (runtime != null || owner == null || Time.unscaledTime < nextRetry) return;
            nextRetry = Time.unscaledTime + 2f;
            Connect();
        }
        private static void Connect()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == "VaM.Memory") { runtime = assembly; break; }
            if (runtime == null) return;
            runtime.GetType("VaM.Memory.UiBridge", true).GetMethod("Attach", Static).Invoke(null,
                new object[] { owner, new Func<bool>(() => SceneLoadAccelerator.SceneLoadActive),
                    new Func<bool>(VrActivity), new Action(UiAssistHudLink.ReportPdMemory) });
        }
        private static bool VrActivity()
        {
            float trigger, rightGrip, leftGrip, button; Vector2 right, left;
            bool active = OpenVrInputBridge.TryGetInput(out trigger, out rightGrip, out leftGrip, out button, out right) &&
                (trigger > 0.25f || rightGrip > 0.5f || leftGrip > 0.5f || button > 0.5f);
            return active || (OpenVrInputBridge.TryGetSticks(out right, out left) &&
                (right.sqrMagnitude > 0.09f || left.sqrMagnitude > 0.09f));
        }
        internal static void Detach(object token)
        {
            if (runtime != null) runtime.GetType("VaM.Memory.UiBridge", true).GetMethod("Detach", Static).Invoke(null, new[] { token });
            if (!ReferenceEquals(owner, token)) return;
            owner = null; runtime = null; methods.Clear();
        }
        private static MethodInfo Method(string type, string name)
        {
            if (runtime == null) return null;
            string key = type + "." + name;
            MethodInfo method;
            if (!methods.TryGetValue(key, out method))
            {
                method = runtime.GetType("Quest3TriggerUI." + type, true).GetMethod(name, Static);
                if (method == null) throw new MissingMethodException(key);
                methods.Add(key, method);
            }
            return method;
        }
        internal static void Call(string type, string name, params object[] args)
        { MethodInfo method = Method(type, name); if (method != null) method.Invoke(null, args); }
        internal static T Value<T>(string type, string name, T missing)
        { MethodInfo method = Method(type, name); return method == null ? missing : (T)method.Invoke(null, null); }
        internal static void Field<T>(string type, string name, T value)
        {
            if (runtime == null) return;
            runtime.GetType("Quest3TriggerUI." + type, true).GetField(name, Static).SetValue(null, value);
        }
    }

    internal static class WardrobeJanitor
    {
        private static readonly FieldInfo RealQueuedImages = typeof(ImageLoaderThreaded).GetField("numRealQueuedImages", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        internal static bool ImagesBusy()
        {
            ImageLoaderThreaded loader = ImageLoaderThreaded.singleton;
            return loader != null && (RealQueuedImages == null || (int)RealQueuedImages.GetValue(loader) > 0);
        }
        internal static void Log(string text)
        { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[wardrobe] " + text); }
    }
    internal static class SceneOrphanSweep
    {
        internal static void OnSceneLoaded() { MemoryRuntimeLink.Call("SceneOrphanSweep", "OnSceneLoaded"); }
        // UI hot-generation cleanup still works when memory optimization is uninstalled.
        internal static bool HostHasAlienScript(GameObject go)
        {
            Component[] comps = null;
            try { comps = go.GetComponents<Component>(); } catch { }
            if (comps == null) return false;
            foreach (Component comp in comps)
            {
                MonoBehaviour mb = comp as MonoBehaviour;
                if (mb == null) continue;
                string ns = mb.GetType().Namespace;
                if (ns != null && ns.StartsWith("Quest3TriggerUI")) continue;
                string name = null;
                try { name = mb.GetType().Assembly.GetName().Name; } catch { }
                if (name != null && (name.StartsWith("UnityEngine") || name.StartsWith("Unity."))) continue;
                return true;
            }
            return false;
        }
    }
    internal static class CatalogueRetirement
    { internal static void Tick() { } }
    internal static class TextureCacheWriteBudget
    { internal static long PendingBytes() { return MemoryRuntimeLink.Value<long>("TextureCacheWriteBudget", "PendingBytes", 0); } }
    internal static class MemoryProbe
    {
        internal static void Dump() { MemoryRuntimeLink.Call("MemoryProbe", "Dump"); }
        internal static void Snapshot(string tag) { MemoryRuntimeLink.Call("MemoryProbe", "Snapshot", tag); }
        internal static void ProcessBytes(out long working, out long commit)
        {
            object[] args = new object[] { 0L, 0L };
            MemoryRuntimeLink.Call("MemoryProbe", "ProcessBytes", args);
            working = (long)args[0]; commit = (long)args[1];
        }
        internal static System.Collections.IEnumerator SnapshotDelayed(MonoBehaviour host, string tag, float seconds)
        { if (host == null) yield break; yield return new WaitForSecondsRealtime(seconds); Snapshot(tag); }
    }
    internal static class MemoryRetentionReport
    {
        internal static bool GameScan { set { MemoryRuntimeLink.Field("MemoryRetentionReport", "GameScan", value); } }
        internal static void Report(string tag) { MemoryRuntimeLink.Call("MemoryRetentionReport", "Report", tag); }
        internal static void DeepReport(string tag) { MemoryRuntimeLink.Call("MemoryRetentionReport", "DeepReport", tag); }
    }
    internal static class AudioCacheJanitor
    { internal static void SweepNow() { MemoryRuntimeLink.Call("AudioCacheJanitor", "SweepNow"); } }
    internal static class LoadWindow
    { internal static bool PresetBusy { get { return MemoryRuntimeLink.Value<bool>("LoadWindow", "get_PresetBusy", false); } } }
    internal static class TextureCacheEstimate
    { internal static Action<int, long, int, string> Probe { set { MemoryRuntimeLink.Field("TextureCacheEstimate", "Probe", value); } } }
}
