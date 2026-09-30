using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Cross-generation handoff anchor. Lives as INSTANCE fields on a
    // DontDestroyOnLoad GameObject — byte-loaded payload assemblies have
    // broken static-field visibility across generations on this mono
    // (GetValue/Invoke read null), and AppDomain.SetData is a no-op here.
    // Instance fields on a live object are plain memory reads and work.
    internal sealed class GenAnchor : MonoBehaviour
    {
        internal Dictionary<string, object> Store =
            new Dictionary<string, object>(StringComparer.Ordinal);
        // Pending quarantine rows: { key, value, deadline(float), claimed(bool) }
        internal List<object[]> Pending = new List<object[]>();
    }

    // Two entry kinds:
    //   Publish    — permanent share (dictionaries, buffer pools). Lives on
    //                the anchor GameObject — i.e. until VaM exits.
    //   Quarantine — teardown handoff for Unity objects (thumbnails). A
    //                released collection survives TtlSeconds waiting for the
    //                next generation (or a reopen) to Claim it; unclaimed
    //                contents are Destroy()ed by Tick so a plain panel-close
    //                still frees VRAM exactly like before.
    internal static class GenBridge
    {
        private const float TtlSeconds = 120f;
        private const string AnchorName = "Q3GenBridgeAnchor";

        private static GenAnchor _mine;
        private static readonly object Gate = new object();

        private static GenAnchor Anchor
        {
            get
            {
                lock (Gate)
                {
                    EnsureAnchor();
                    return _mine;
                }
            }
        }

        private static void EnsureAnchor()
        {
            if (_mine != null) return;
            var go = GameObject.Find(AnchorName);
            if (go == null)
            {
                go = new GameObject(AnchorName);
                go.hideFlags = HideFlags.DontSave;
                UnityEngine.Object.DontDestroyOnLoad(go);
            }
            int adoptedStore = 0, adoptedPending = 0;
            List<IDictionary> storeMerges = null;
            List<object[]> pendingRows = null;
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                Type t = comp.GetType();
                if (t.Name != "GenAnchor" || t == typeof(GenAnchor)) continue;
                // Old generation's anchor: harvest its instance fields.
                try
                {
                    var dict = ReadField(comp, "Store") as IDictionary;
                    var rows = ReadField(comp, "Pending") as IEnumerable;
                    WardrobeJanitor.Log("gen-bridge anchor cand: " +
                        t.Assembly.GetName().Name +
                        " store=" + (dict == null ? -1 : dict.Count) +
                        " pending=" + (rows == null ? -1 :
                            (rows is ICollection ? ((ICollection)rows).Count : -2)));
                    if (dict != null)
                    {
                        if (storeMerges == null) storeMerges = new List<IDictionary>();
                        storeMerges.Add(dict);
                    }
                    if (rows != null)
                    {
                        if (pendingRows == null) pendingRows = new List<object[]>();
                        foreach (object row in rows)
                        {
                            var arr = row as object[];
                            if (arr != null) pendingRows.Add(arr);
                        }
                        // Transfer bookkeeping; destroyed old anchors can still
                        // be held by a byte-loaded generation's static _mine.
                        var oldRows = rows as IList;
                        if (oldRows != null) oldRows.Clear();
                    }
                }
                catch { }
                UnityEngine.Object.Destroy(comp);
            }
            _mine = go.GetComponent<GenAnchor>();
            if (_mine == null) _mine = go.AddComponent<GenAnchor>();
            if (storeMerges != null)
            {
                foreach (IDictionary dict in storeMerges)
                {
                    foreach (DictionaryEntry e in dict)
                    {
                        string k = e.Key as string;
                        if (k != null && e.Value != null &&
                            !_mine.Store.ContainsKey(k))
                            _mine.Store[k] = e.Value;
                    }
                    // Values now belong to the new anchor. Clear the old map,
                    // not the transferred values or their Unity resources.
                    dict.Clear();
                }
                adoptedStore = _mine.Store.Count;
            }
            if (pendingRows != null)
            {
                foreach (object[] arr in pendingRows)
                {
                    if (arr == null || arr.Length < 4 || (bool)arr[3]) continue;
                    _mine.Pending.Add(arr);
                    adoptedPending++;
                }
            }
            if (adoptedStore > 0 || adoptedPending > 0)
                WardrobeJanitor.Log("gen-bridge anchor adopted store=" +
                    adoptedStore + " pending=" + adoptedPending);
        }

        private static object ReadField(Component comp, string name)
        {
            var f = comp.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic |
                BindingFlags.Public);
            return f == null ? null : f.GetValue(comp);
        }

        internal static void Publish(string key, object value)
        {
            if (key == null || value == null) return;
            var a = Anchor;
            if (a == null) return;
            lock (a.Store) a.Store[key] = value;
        }

        // Permanent-share lookup. Does not remove the entry.

        // Removes a permanent-share entry so the next generation cannot adopt
        // it. Deliberately does NOT call DestroyContents: the caller owns the
        // lifetime (the pinyin tables are plain BCL dictionaries that the
        // engine releases itself), and clearing contents here would be wrong
        // for an entry a live generation still holds.
        internal static bool Drop(string key)
        {
            var a = Anchor;
            if (a == null) return false;
            lock (a.Store) return a.Store.Remove(key);
        }

        internal static object Take(string key)
        {
            var a = Anchor;
            if (a == null) return null;
            lock (a.Store)
            {
                object v;
                return a.Store.TryGetValue(key, out v) ? v : null;
            }
        }

        // Quarantined handoff lookup: marks the entry claimed so Tick will not
        // destroy its contents. Returns null when absent or already dead.
        internal static object Claim(string key)
        {
            var a = Anchor;
            if (a == null) return null;
            lock (a.Pending)
            {
                for (int i = 0; i < a.Pending.Count; i++)
                {
                    object[] p = a.Pending[i];
                    if (p == null || (bool)p[3] ||
                        !string.Equals(p[0] as string, key, StringComparison.Ordinal))
                        continue;
                    object value = p[1];
                    p[3] = true;
                    p[1] = null;
                    a.Pending.RemoveAt(i);
                    PruneDead(value);
                    return value;
                }
                return null;
            }
        }

        internal static void Quarantine(string key, object value)
        {
            if (key == null || value == null) return;
            var a = Anchor;
            if (a == null) return;
            object[] row = new object[]
            {
                key, value, Time.realtimeSinceStartup + TtlSeconds, false
            };
            lock (a.Pending)
            {
                for (int i = 0; i < a.Pending.Count; i++)
                {
                    object[] p = a.Pending[i];
                    if (p != null && string.Equals(p[0] as string, key,
                        StringComparison.Ordinal))
                    {
                        // Replace prior quarantine of the same key: destroy the
                        // old contents — they can no longer be claimed.
                        if (!(bool)p[3]) { try { DestroyContents(p[1]); } catch { } }
                        a.Pending[i] = row;
                        return;
                    }
                }
                a.Pending.Add(row);
            }
        }

        internal static void AdoptPreviousGeneration()
        {
            try
            {
                var a = Anchor;
                if (a != null)
                    WardrobeJanitor.Log("gen-bridge: store=" + a.Store.Count +
                        " quarantine=" + a.Pending.Count + " (go-anchored)");
            }
            catch (Exception e)
            {
                WardrobeJanitor.Log("gen-bridge adopt fail: " + e.GetType().Name);
            }
        }

        // Adopt a thumbnail dictionary handed over by the previous generation
        // (quarantined teardown or a live-published store entry) into this
        // generation's dict. Dead (fake-null) entries are skipped.
        internal static void AdoptThumbs(string key, Dictionary<string, Texture2D> target)
        {
            var prev = Claim(key) as Dictionary<string, Texture2D> ??
                Take(key) as Dictionary<string, Texture2D>;
            if (prev == null || target == null) return;
            foreach (KeyValuePair<string, Texture2D> kv in prev)
                if (kv.Value != null) target[kv.Key] = kv.Value;
        }

        // Destroy expired quarantine contents. Dictionary values are treated
        // as collections of Unity objects (thumbnail caches); anything else
        // that is itself a UnityEngine.Object is destroyed directly. Claimed
        // entries are skipped — the new owner manages their lifetime now.
        internal static void Tick()
        {
            var a = _mine;
            if (a == null) return;
            float now = Time.realtimeSinceStartup;
            List<object[]> expired = null;
            lock (a.Pending)
            {
                for (int i = a.Pending.Count - 1; i >= 0; i--)
                {
                    object[] p = a.Pending[i];
                    if (p == null || (bool)p[3])
                    {
                        // Legacy claimed rows must release bookkeeping only;
                        // their textures are owned by the claimant, not by us.
                        if (p != null) p[1] = null;
                        a.Pending.RemoveAt(i);
                        continue;
                    }
                    if (now < (float)p[2]) continue;
                    if (expired == null) expired = new List<object[]>();
                    expired.Add(p);
                    a.Pending.RemoveAt(i);
                }
            }
            if (expired == null) return;
            foreach (object[] p in expired)
            {
                try { DestroyContents(p[1]); } catch { }
            }
        }

        // Unity fake-null: entries whose textures were already destroyed
        // (scene unload, partial teardown) must not be handed to the new gen.
        private static void PruneDead(object value)
        {
            var dict = value as IDictionary;
            if (dict == null) return;
            var dead = new List<object>();
            foreach (DictionaryEntry e in dict)
            {
                // Only Unity-object values can be fake-null; value-type
                // entries (stamp dicts) are never dead.
                if (!(e.Value is UnityEngine.Object)) continue;
                var u = e.Value as UnityEngine.Object;
                if (u == null) dead.Add(e.Key);
            }
            foreach (object k in dead) dict.Remove(k);
        }

        private static void DestroyContents(object value)
        {
            var dict = value as IDictionary;
            if (dict != null)
            {
                foreach (DictionaryEntry e in dict)
                {
                    var u = e.Value as UnityEngine.Object;
                    if (u != null) UnityEngine.Object.Destroy(u);
                }
                dict.Clear();
                return;
            }
            var en = value as IEnumerable;
            if (en != null && !(value is string))
            {
                foreach (object o in en)
                {
                    var u = o as UnityEngine.Object;
                    if (u != null) UnityEngine.Object.Destroy(u);
                }
                return;
            }
            var single = value as UnityEngine.Object;
            if (single != null) UnityEngine.Object.Destroy(single);
        }
    }
}
