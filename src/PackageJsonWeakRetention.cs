using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using MVR.FileManagement;
using SimpleJSON;

namespace Quest3TriggerUI
{
    // FileManager keeps packages alive. A package must not also pin its entire
    // parsed metadata tree forever. Reuse weakly; retain the original disk loader.
    internal static class PackageJsonWeakRetention
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo Root = typeof(VarPackage).GetField("jsonCache", Fields);
        private static readonly FieldInfo Loaded = typeof(VarPackage).GetField("_jsonCacheLoaded", Fields);
        private static readonly FieldInfo ReloadPath = typeof(VarPackage).GetField("_jsonCachePath", Fields);
        // This Unity profile predates ConditionalWeakTable. Identity buckets
        // hold only weak owners/trees; incrementally prune metadata on insertion.
        private static readonly object TableGate = new object();
        private static readonly Dictionary<int, List<Entry>> _entries = new Dictionary<int, List<Entry>>();
        private static readonly Queue<int> Sweep = new Queue<int>();
        private static Harmony _harmony;
        private static volatile bool _enabled;
        private static long _detached, _hotHits, _coldLoads, _unreloadable;

        internal sealed class Entry
        {
            internal readonly WeakReference Owner;
            internal readonly WeakReference Tree = new WeakReference(null);
            internal int Depth;
            internal Entry(VarPackage package) { Owner = new WeakReference(package); }
        }

        internal struct Call
        {
            internal Entry Entry;
            internal bool Held;
        }

        private static Entry GetEntry(VarPackage package, bool create)
        {
            int id = RuntimeHelpers.GetHashCode(package);
            lock (TableGate)
            {
                List<Entry> bucket;
                if (_entries.TryGetValue(id, out bucket))
                    foreach (var entry in bucket)
                        if (object.ReferenceEquals(entry.Owner.Target, package)) return entry;
                if (!create) return null;
                if (bucket == null)
                {
                    bucket = new List<Entry>(1);
                    _entries.Add(id, bucket);
                    Sweep.Enqueue(id);
                }
                var added = new Entry(package);
                bucket.Add(added);
                for (int n = 0; n < 4 && Sweep.Count != 0; n++)
                {
                    int next = Sweep.Dequeue();
                    var scanned = _entries[next];
                    for (int i = scanned.Count - 1; i >= 0; i--)
                        if (!scanned[i].Owner.IsAlive) scanned.RemoveAt(i);
                    if (scanned.Count == 0) _entries.Remove(next);
                    else Sweep.Enqueue(next);
                }
                return added;
            }
        }

        private static void Restore(VarPackage package, Entry entry)
        {
            if (Root.GetValue(package) != null) return;
            var tree = entry.Tree.Target as JSONClass;
            if (tree == null) return;
            Root.SetValue(package, tree);
            Loaded.SetValue(package, true);
            Interlocked.Increment(ref _hotHits);
        }

        private static void Detach(VarPackage package, Entry entry, bool sync)
        {
            var tree = Root.GetValue(package) as JSONClass;
            if (tree == null)
            {
                // Original error/disabled/invalidated states win; never revive
                // an older tree after the native method has explicitly dropped it.
                entry.Tree.Target = null;
                return;
            }
            string path = ReloadPath.GetValue(package) as string;
            if (sync || string.IsNullOrEmpty(path))
            {
                // Both native regeneration branches omit _jsonCachePath.
                // Use their exact on-disk target, only after a completed write.
                string dir = CacheManager.GetPackageJSONCacheDir();
                if (!string.IsNullOrEmpty(dir))
                {
                    string generated = dir + "/" + package.Uid + ".vamcachejson";
                    if (File.Exists(generated))
                    {
                        path = generated;
                        ReloadPath.SetValue(package, path);
                    }
                    else if (sync) path = null;
                }
                else if (sync) path = null;
            }
            if (string.IsNullOrEmpty(path))
            {
                // An in-memory tree without a reload source keeps native ownership.
                entry.Tree.Target = null;
                Interlocked.Increment(ref _unreloadable);
                return;
            }
            entry.Tree.Target = tree;
            // Drop only the package root, not any nodes already handed to users.
            Root.SetValue(package, null);
            Loaded.SetValue(package, false);
            Interlocked.Increment(ref _detached);
        }

        private static void Prefix(VarPackage __instance, MethodBase __originalMethod, out Call __state)
        {
            __state = new Call();
            var entry = GetEntry(__instance, true);
            Monitor.Enter(entry);
            __state.Entry = entry;
            __state.Held = true;
            if (++entry.Depth != 1) return;
            Restore(__instance, entry);
            if (__originalMethod.Name == "GetJSONCache" && Root.GetValue(__instance) == null &&
                !(bool)Loaded.GetValue(__instance) && ReloadPath.GetValue(__instance) != null)
                Interlocked.Increment(ref _coldLoads);
        }

        private static Exception Finalizer(VarPackage __instance, MethodBase __originalMethod,
            ref Call __state, Exception __exception)
        {
            if (!__state.Held) return __exception;
            __state.Held = false;
            var entry = __state.Entry;
            try
            {
                if (entry.Depth == 1 && _enabled)
                    Detach(__instance, entry, __originalMethod.Name == "SyncJSONCache");
            }
            finally
            {
                entry.Depth--;
                Monitor.Exit(entry);
            }
            // In particular, do not swallow an exception from another patch.
            return __exception;
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            if (Root == null || Root.FieldType != typeof(JSONClass) || Loaded == null ||
                Loaded.FieldType != typeof(bool) || ReloadPath == null || ReloadPath.FieldType != typeof(string))
                throw new MissingFieldException("VarPackage JSON retention fields changed");
            var get = typeof(VarPackage).GetMethod("GetJSONCache", new[] { typeof(string) });
            var sync = typeof(VarPackage).GetMethod("SyncJSONCache", Type.EmptyTypes);
            var bulk = typeof(VarPackage).GetMethod("SyncJSONCache", new[] { typeof(HashSet<string>), typeof(HashSet<string>) });
            if (get == null || sync == null || bulk == null)
                throw new MissingMethodException("VarPackage JSON retention methods changed");
            _harmony = new Harmony("quest3triggerui.packagejson." + typeof(PackageJsonWeakRetention).Namespace);
            try
            {
                var prefix = new HarmonyMethod(typeof(PackageJsonWeakRetention), "Prefix");
                var finalizer = new HarmonyMethod(typeof(PackageJsonWeakRetention), "Finalizer");
                foreach (var method in new[] { get, sync, bulk })
                    _harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                _enabled = true;
                int seeded = 0, nodes = 0;
                var packages = FileManager.GetPackages();
                foreach (var package in packages)
                {
                    if (Root.GetValue(package) == null) continue;
                    var entry = GetEntry(package, true);
                    lock (entry)
                    {
                        var tree = Root.GetValue(package) as JSONClass;
                        if (tree == null) continue;
                        seeded++;
                        nodes += tree.Count;
                        Detach(package, entry, false);
                    }
                }
                Log("installed methods=3 packages=" + packages.Count + " seeded=" + seeded +
                    " topEntries=" + nodes + " detached=" + _detached + " unreloadable=" + _unreloadable +
                    " ownership=weak gcPolicy=unchanged");
            }
            catch
            {
                Shutdown();
                throw;
            }
        }

        internal static void Shutdown()
        {
            if (_harmony == null) return;
            _enabled = false;
            try
            {
                var snapshot = new List<Entry>();
                lock (TableGate)
                    foreach (var bucket in _entries.Values) snapshot.AddRange(bucket);
                foreach (var entry in snapshot)
                {
                    var package = entry.Owner.Target as VarPackage;
                    if (package == null) continue;
                    lock (entry) Restore(package, entry);
                }
            }
            finally
            {
                _harmony.UnpatchAll(_harmony.Id);
                _harmony = null;
                lock (TableGate) { _entries.Clear(); Sweep.Clear(); }
                Log("shutdown detached=" + _detached + " hotHits=" + _hotHits + " coldLoads=" + _coldLoads +
                    " unreloadable=" + _unreloadable + " nativeLazyLoad=restored");
            }
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[package-json] " + message);
        }
    }
}
