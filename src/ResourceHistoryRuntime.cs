using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using MVR.FileManagement;

namespace Quest3TriggerUI
{
    internal static class ResourceHistoryRuntime
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private sealed class Prepared { internal WeakReference Manager; internal string Path; }
        private sealed class Pending { internal WeakReference Atom; internal ResourceHistoryStore.Record Record; }
        private static readonly List<Prepared> Prepares = new List<Prepared>();
        private static readonly List<string> Superseded = new List<string>();
        private static readonly Dictionary<string, Pending> PendingLoads = new Dictionary<string, Pending>();
        private static readonly FieldInfo ManagerField = typeof(MeshVR.PresetManagerControl).GetField("pm", All);
        private static ResourceHistoryWorker _worker;
        private static Harmony _harmony;
        private static bool On { get { return ResourceLedger.Enabled == null || ResourceLedger.Enabled.Value; } }
        private static HarmonyMethod Hook(string name) { return new HarmonyMethod(typeof(ResourceHistoryRuntime).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)); }
        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                _harmony = new Harmony("Quest3TriggerUI.resource-history"); _harmony.UnpatchAll(_harmony.Id);
                var type = typeof(MeshVR.PresetManager);
                _harmony.Patch(type.GetMethod("LoadPresetPre", All), prefix: Hook("BeforePre"), postfix: Hook("AfterPre"));
                _harmony.Patch(type.GetMethod("LoadPresetPreFromJSON", All), prefix: Hook("ClearPrepared"));
                _harmony.Patch(type.GetMethod("LoadPresetPost", All), postfix: Hook("AfterPost"));
                StartWorker();
                Log("installed; persistent content metadata + session reference reconciliation; no history-only UUA skip");
            }
            catch (Exception e) { Shutdown(); Log("install failed: " + e.Message); }
        }
        private static void StartWorker()
        {
            if (_worker == null && On) _worker = new ResourceHistoryWorker(Path.Combine(BepInEx.Paths.ConfigPath, "Quest3TriggerUI.resource-history"));
        }
        private static string Field(MeshVR.PresetManager pm, string name)
        { var f = typeof(MeshVR.PresetManager).GetField(name, All); return f == null ? null : f.GetValue(pm) as string; }
        private static void BeforePre(MeshVR.PresetManager __instance, out string __state)
        {
            __state = null; ClearPrepared(__instance);
            if (!On) return;
            try
            {
                // Exact LoadPresetPre IL concatenation, not a guessed UI label.
                string folder = (string)typeof(MeshVR.PresetManager).GetMethod("GetStoreFolderPath", All).Invoke(__instance, new object[] { false });
                if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(Field(__instance, "storeName"))) return;
                __state = Field(__instance, "presetPackagePath") + folder + Field(__instance, "presetSubPath") + Field(__instance, "storeName") + "_" + Field(__instance, "presetSubName") + ".vap";
            }
            catch (Exception e) { Log("preset identity unknown: " + e.Message); }
        }
        private static void AfterPre(MeshVR.PresetManager __instance, string __state, bool __result)
        {
            if (!On || !__result || string.IsNullOrEmpty(__state)) return;
            if (Prepares.Count >= 32) Prepares.RemoveAt(0);
            Prepares.Add(new Prepared { Manager = new WeakReference(__instance), Path = __state });
        }
        private static void ClearPrepared(MeshVR.PresetManager __instance)
        { for (int i = Prepares.Count - 1; i >= 0; i--) if (!Prepares[i].Manager.IsAlive || ReferenceEquals(Prepares[i].Manager.Target, __instance)) Prepares.RemoveAt(i); }
        private static void AfterPost(MeshVR.PresetManager __instance, bool __result)
        {
            string path = null;
            foreach (var p in Prepares) if (ReferenceEquals(p.Manager.Target, __instance)) { path = p.Path; break; }
            ClearPrepared(__instance);
            if (__result && path != null) PresetLoaded(__instance, path);
        }
        internal static void PresetLoaded(MeshVR.PresetManager manager, string path)
        {
            if (!On) return;
            try
            {
                Atom atom = WardrobeJanitor.OwnerOf(manager);
                if (atom == null || atom.type != "Person" || atom.isPreparingToPutBackInPool || ManagerField == null) return;
                string kind = null;
                foreach (var control in atom.presetManagerControls)
                    if (control != null && ReferenceEquals(ManagerField.GetValue(control), manager))
                    { if (control.name == "AppearancePresets") kind = "appearance"; else if (control.name == "ClothingPresets") kind = "clothing-preset"; }
                if (kind == null) return;
                var record = Resolve(kind, path); if (record == null) return;
                record.Status = "load-observed-unsettled";
                StartWorker(); if (_worker == null) return;
                _worker.Queue(Copy(record));
                // A newer restore of the SAME person supersedes the old unfinished
                // graph. Its initial content entry remains in persistent history.
                Superseded.Clear();
                foreach (var previous in PendingLoads) if (ReferenceEquals(previous.Value.Atom.Target, atom)) Superseded.Add(previous.Key);
                foreach (string key in Superseded) PendingLoads.Remove(key);
                Superseded.Clear();
                PendingLoads[atom.GetInstanceID() + ":" + kind] = new Pending { Atom = new WeakReference(atom), Record = record };
            }
            catch (Exception e) { Log("preset observation failed: " + e.Message); }
        }
        internal static ResourceHistoryStore.Record Resolve(string kind, string source)
        {
            if (string.IsNullOrEmpty(source)) return null;
            string normalized = source.Replace('\\', '/');
            var record = new ResourceHistoryStore.Record { Kind = kind, Source = normalized, Physical = "", Package = "", Status = "observed" };
            if (normalized.StartsWith("builtin:", StringComparison.Ordinal)) return record;
            // GetFileEntry/GetPackage resolve registered .latest/mapped packages.
            // Registration disappearance by itself is never treated as deletion.
            FileEntry entry = FileManager.GetFileEntry(source, false);
            var packaged = entry as VarFileEntry;
            if (packaged != null && packaged.Package != null)
            {
                record.Package = packaged.Package.Uid;
                record.Source = record.Package + ":/" + packaged.InternalSlashPath;
                record.Physical = packaged.Package.FullPath;
            }
            else if (entry != null) { record.Source = entry.FullPath; record.Physical = entry.FullPath; }
            else if (normalized.IndexOf(":/", StringComparison.Ordinal) <= 1)
            { record.Physical = Path.GetFullPath(Path.IsPathRooted(source) ? source : Path.Combine(BepInEx.Paths.GameRootPath, source)); record.Source = record.Physical; }
            return record;
        }
        internal static ResourceHistoryStore.Record Clothing(JSONStorableDynamic item)
        {
            var clothing = item as DAZClothingItem; if (clothing == null) return null;
            string source = clothing.dynamicRuntimeLoadPath;
            if (string.IsNullOrEmpty(source)) source = clothing.uid;
            if (string.IsNullOrEmpty(source)) return null;
            if (source.IndexOf('/') < 0 && source.IndexOf('\\') < 0) source = "builtin:clothing/" + source;
            return Resolve("clothing", source);
        }
        internal static void Observed(ResourceHistoryStore.Record identity, IEnumerable<ResourceLedgerIndex.Asset> assets)
        {
            if (!On || identity == null) return;
            var record = Copy(identity);
            var descriptors = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asset in assets) descriptors.Add(asset.Kind + "\t" + asset.Name);
            record.Assets = new List<string>(descriptors).ToArray(); Array.Sort(record.Assets, StringComparer.Ordinal);
            record.Status = "observed"; StartWorker(); if (_worker != null) _worker.Queue(record);
        }
        internal static ResourceHistoryStore.Record Copy(ResourceHistoryStore.Record r)
        { return new ResourceHistoryStore.Record { Kind = r.Kind, Source = r.Source, Physical = r.Physical, Package = r.Package, Status = r.Status }; }
        internal static bool HasPending { get { return PendingLoads.Count != 0; } }
        internal static void FinishPending(Func<Atom, bool, string[][]> snapshot)
        {
            if (!On) { StopWorker(); return; }
            StartWorker();
            foreach (var pending in PendingLoads.Values)
            {
                Atom atom = pending.Atom.Target as Atom; if (atom == null || atom.isPreparingToPutBackInPool) continue;
                string[][] data = snapshot(atom, pending.Record.Kind == "clothing-preset");
                var record = Copy(pending.Record); record.Dependencies = data[0]; record.Assets = data[1]; record.Status = "observed";
                _worker.Queue(record);
            }
            PendingLoads.Clear();
        }
        internal static string Summary()
        { return _worker == null ? "history=disabled" : "history=" + _worker.Count + " ready=" + _worker.Ready + " metadataHits=" + _worker.Hits + " deleted=" + _worker.Deleted + " invalidated=" + _worker.Invalidated + " ioErrors=" + _worker.Errors + " lastError=" + _worker.LastError; }
        internal static void Suspend() { StopWorker(); }
        internal static void ClearSession() { Prepares.Clear(); PendingLoads.Clear(); }
        private static void StopWorker() { if (_worker != null) _worker.Stop(); _worker = null; }
        internal static void Shutdown()
        { if (_harmony != null) _harmony.UnpatchAll(_harmony.Id); _harmony = null; ClearSession(); StopWorker(); }
        private static void Log(string message) { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[resource-history] " + message); }
    }
}
