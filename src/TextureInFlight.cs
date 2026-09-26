using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;

namespace Quest3TriggerUI
{
    // Worker-only sharing of immutable, completely decoded buffers. Each request
    // still owns its native completion, callback and admission reservation.
    internal static class TextureInFlight
    {
        internal static ConfigEntry<bool> Enabled;
        private static Harmony _harmony;
        private static readonly object Sync = new object();
        private static readonly Dictionary<Key, Entry> Active = new Dictionary<Key, Entry>();
        private static int _mainThread;
        private static volatile bool _accepting;
        private static long _hits, _timeouts;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly FieldInfo WebRequest = typeof(ImageLoaderThreaded.QueuedImage).GetField("webRequest", All);
        private struct Key : IEquatable<Key>
        {
            internal string path;
            internal int flags, width, height;
            internal float bump;
            public bool Equals(Key b) { return path == b.path && flags == b.flags && width == b.width && height == b.height && bump.Equals(b.bump); }
            public override bool Equals(object o) { return o is Key && Equals((Key)o); }
            public override int GetHashCode() { unchecked { return (((path.GetHashCode() * 397 ^ flags) * 397 ^ width) * 397 ^ height) * 397 ^ bump.GetHashCode(); } }
        }
        private sealed class Entry
        {
            internal Key key;
            internal bool done, valid;
            internal byte[] raw;
            internal int width, height;
            internal UnityEngine.TextureFormat format;
            internal bool preprocessed;
        }
        private static bool Eligible(ImageLoaderThreaded.QueuedImage q)
        {
            return q != null && !q.processed && !q.finished && !q.cancel && !q.hadError &&
                !q.forceReload && !q.skipCache && !q.isThumbnail && !q.isPreload && !q.useWebCache &&
                WebRequest != null && WebRequest.GetValue(q) == null && ReferenceEquals(q.rawImageToLoad, null) && q.raw == null && ReferenceEquals(q.tex, null) &&
                !string.IsNullOrEmpty(q.imgPath) && q.imgPath != "NULL" &&
                q.imgPath.IndexOf("://", StringComparison.Ordinal) < 0 &&
                q.imgPath.IndexOf(".latest:", StringComparison.Ordinal) < 0;
        }
        private static Key MakeKey(ImageLoaderThreaded.QueuedImage q)
        {
            return new Key { path = q.imgPath, width = q.width, height = q.height, bump = q.bumpStrength,
                flags = (q.compress ? 1 : 0) | (q.linear ? 2 : 0) | (q.createMipMaps ? 4 : 0) |
                (q.isNormalMap ? 8 : 0) | (q.createAlphaFromGrayscale ? 16 : 0) |
                (q.createNormalFromBump ? 32 : 0) | (q.invert ? 64 : 0) |
                (q.setSize ? 128 : 0) | (q.fillBackground ? 256 : 0) };
        }
        private static bool Before(ImageLoaderThreaded.QueuedImage __instance, out Entry __state)
        {
            __state = null;
            var q = __instance;
            if (!_accepting || (Enabled != null && !Enabled.Value) ||
                Thread.CurrentThread.ManagedThreadId == _mainThread || !Eligible(q)) return true;
            Key key = MakeKey(q);
            Entry entry;
            lock (Sync)
            {
                if (!_accepting) return true;
                if (!Active.TryGetValue(key, out entry))
                {
                    if (Active.Count >= 64) return true;
                    entry = new Entry { key = key };
                    Active.Add(key, entry); __state = entry;
                    return true;
                }
            }
            // The leader is already executing on another native worker; never wait
            // on the Unity thread. Bounded wait prevents I/O from pinning all workers.
            var clock = Stopwatch.StartNew();
            lock (entry)
            {
                while (!entry.done && clock.ElapsedMilliseconds < 250)
                    Monitor.Wait(entry, Math.Max(1, 250 - (int)clock.ElapsedMilliseconds));
                if (!entry.done) { Interlocked.Increment(ref _timeouts); return true; }
                if (!entry.valid || !_accepting || (Enabled != null && !Enabled.Value) || q.cancel || !Eligible(q) || !key.Equals(MakeKey(q))) return true;
                q.width = entry.width; q.height = entry.height;
                q.textureFormat = entry.format; q.preprocessed = entry.preprocessed;
                q.raw = entry.raw; q.processed = true;
                if (Interlocked.Increment(ref _hits) == 1)
                    Quest3TriggerUIPlugin.Log.LogInfo("[texture-inflight] first shared decode completed; each request retains native completion");
                return false;
            }
        }
        private static Exception After(ImageLoaderThreaded.QueuedImage __instance, Entry __state, Exception __exception)
        {
            if (__state == null) return __exception;
            lock (Sync) { Entry current; if (Active.TryGetValue(__state.key, out current) && ReferenceEquals(current, __state)) Active.Remove(__state.key); }
            lock (__state)
            {
                var q = __instance;
                __state.valid = _accepting && __exception == null && q.processed && !q.hadError && !q.cancel && !q.finished && q.raw != null;
                if (__state.valid)
                {
                    __state.raw = q.raw; __state.width = q.width; __state.height = q.height;
                    __state.format = q.textureFormat; __state.preprocessed = q.preprocessed;
                }
                __state.done = true; Monitor.PulseAll(__state);
            }
            return __exception;
        }
        internal static void Install()
        {
            if (_harmony != null) return;
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            try
            {
                _harmony = new Harmony("Quest3TriggerUI.texture-inflight");
                _harmony.Patch(typeof(ImageLoaderThreaded.QueuedImage).GetMethod("Process", All),
                    prefix: new HarmonyMethod(typeof(TextureInFlight).GetMethod("Before", All)),
                    finalizer: new HarmonyMethod(typeof(TextureInFlight).GetMethod("After", All)));
                _accepting = true;
                Quest3TriggerUIPlugin.Log.LogInfo("[texture-inflight] installed; worker-only same-request decoding; bounded 250ms wait; native callbacks/budgets retained");
            }
            catch (Exception e) { Shutdown(); Quest3TriggerUIPlugin.Log.LogInfo("[texture-inflight] not installed: " + e.Message); }
        }
        internal static void Shutdown()
        {
            Entry[] entries;
            lock (Sync) { _accepting = false; entries = new List<Entry>(Active.Values).ToArray(); Active.Clear(); }
            foreach (var e in entries) lock (e) { e.valid = false; e.done = true; Monitor.PulseAll(e); }
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
        }
    }
}
