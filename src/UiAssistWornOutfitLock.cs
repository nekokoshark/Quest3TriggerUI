using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using BepInEx;
namespace Quest3TriggerUI
{
    // Primitive garment keys only; no retained Atom/item/renderer graph.
    internal sealed class WornOutfitLockSet
    {
        internal bool Enabled;
        internal readonly HashSet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal bool Contains(string key) { return Enabled && Keys.Contains(key); }
        internal bool BlocksAddition(string key) { return Enabled && !Keys.Contains(key); }
        internal void Set(bool enabled, IEnumerable<string> worn)
        {
            Keys.Clear(); Enabled = enabled;
            if (enabled) foreach (string key in worn) if (!string.IsNullOrEmpty(key)) Keys.Add(key);
        }
    }
    internal static partial class UiAssistHudLink
    {
        private static RectTransform _outfitLockRect;
        private static Toggle _outfitLockToggle;
        private static bool _updatingOutfitToggle;
        private static readonly Dictionary<string, WornOutfitLockSet> _outfitLocks = new Dictionary<string, WornOutfitLockSet>();
        private static string OutfitLockPath(string uid)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) uid = uid.Replace(c, '_');
            return Path.Combine(Paths.ConfigPath, "Quest3TriggerUI.outfit-lock." + uid + ".txt");
        }
        private static WornOutfitLockSet OutfitLocks(string uid)
        {
            WornOutfitLockSet set;
            if (_outfitLocks.TryGetValue(uid, out set)) return set;
            set = new WornOutfitLockSet();
            try
            {
                string path = OutfitLockPath(uid);
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path);
                    if (lines.Length > 0 && lines[0] == "!enabled")
                    {
                        set.Enabled = true;
                        for (int i = 1; i < lines.Length; i++) if (!string.IsNullOrEmpty(lines[i])) set.Keys.Add(lines[i]);
                    }
                }
            }
            catch (Exception e) { Error(e); }
            _outfitLocks.Add(uid, set); return set;
        }
        private static bool IsOutfitLocked(Atom atom, string uid)
        {
            return atom != null && !string.IsNullOrEmpty(atom.uid) && OutfitLocks(atom.uid).Contains(BanKey(uid));
        }
        internal static bool IsOutfitWearBlocked(Atom atom, string uid)
        {
            return atom != null && !string.IsNullOrEmpty(atom.uid) &&
                OutfitLocks(atom.uid).BlocksAddition(BanKey(uid));
        }
        internal static void ReportOutfitAdditionBlocked(Atom atom, DAZClothingItem item)
        {
            string key = "outfit-add/" + atom.uid + "/" + item.uid;
            float previous;
            if (_lockLogAt.TryGetValue(key, out previous) && Time.unscaledTime - previous < 10f) return;
            _lockLogAt[key] = Time.unscaledTime;
            Log("整套服装已锁定，已拦截新衣服穿上：" + (item.displayName ?? item.uid));
        }
        private static void SetCurrentOutfitLock(bool enabled)
        {
            if (_updatingOutfitToggle || _lockAtom == null) return;
            DAZCharacterSelector selector = _lockAtom.GetStorableByID("geometry") as DAZCharacterSelector;
            if (enabled && (selector == null || selector.clothingItems == null)) return;
            var keys = new List<string>();
            if (enabled)
                foreach (DAZClothingItem item in selector.clothingItems)
                    if (item != null && item.active) keys.Add(BanKey(item.uid));
            WornOutfitLockSet set = OutfitLocks(_lockAtom.uid);
            set.Set(enabled, keys);
            try
            {
                var lines = new List<string>();
                if (enabled) { lines.Add("!enabled"); lines.AddRange(set.Keys); }
                File.WriteAllLines(OutfitLockPath(_lockAtom.uid), lines.ToArray());
            }
            catch (Exception e) { Error(e); }
            _lockDirty = true;
            Log("服装整套锁定：" + _lockAtom.uid + (enabled ? "，已锁定当前 " + set.Keys.Count + " 件衣服。" : "，已解除；原逐件锁定保留。"));
        }
        private static void UpdateOutfitLockOption()
        {
            if (_lockDock == null || _lockAtom == null) return;
            if (_outfitLockRect == null)
            {
                var go = new GameObject("ACE Worn Outfit Lock", typeof(RectTransform));
                _outfitLockRect = (RectTransform)go.transform;
                _outfitLockRect.SetParent(_lockDock, false);
                _outfitLockRect.pivot = new Vector2(0f, 1f);
                _outfitLockRect.sizeDelta = new Vector2(136f, 32f);
                Image background = go.AddComponent<Image>();
                background.color = new Color(0.78f, 0.78f, 0.78f, 1f);
                _outfitLockToggle = go.AddComponent<Toggle>();
                _outfitLockToggle.targetGraphic = background;
                var box = new GameObject("Check", typeof(RectTransform));
                RectTransform br = (RectTransform)box.transform;
                br.SetParent(_outfitLockRect, false); br.anchorMin = br.anchorMax = new Vector2(0f, 0.5f);
                br.anchoredPosition = new Vector2(18f, 0f); br.sizeDelta = new Vector2(20f, 20f);
                Image checkBox = box.AddComponent<Image>(); checkBox.color = new Color(0.96f, 0.96f, 0.96f, 1f); checkBox.raycastTarget = false;
                Outline border = box.AddComponent<Outline>(); border.effectColor = new Color(0.45f, 0.45f, 0.45f, 1f); border.effectDistance = new Vector2(1f, -1f);
                Text check = new GameObject("Checkmark", typeof(RectTransform)).AddComponent<Text>();
                RectTransform cr = (RectTransform)check.transform; cr.SetParent(br, false);
                cr.anchorMin = Vector2.zero; cr.anchorMax = Vector2.one; cr.offsetMin = cr.offsetMax = Vector2.zero;
                check.text = "✓"; check.font = Resources.GetBuiltinResource<Font>("Arial.ttf"); check.fontSize = 18;
                check.alignment = TextAnchor.MiddleCenter; check.color = new Color(0.25f, 0.25f, 0.25f, 1f); check.raycastTarget = false;
                _outfitLockToggle.graphic = check;
                Text text = new GameObject("Label", typeof(RectTransform)).AddComponent<Text>();
                RectTransform tr = (RectTransform)text.transform; tr.SetParent(_outfitLockRect, false);
                tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = new Vector2(36f, 0f); tr.offsetMax = Vector2.zero;
                text.text = "锁定"; text.font = Resources.GetBuiltinResource<Font>("Arial.ttf"); text.fontSize = 16;
                text.alignment = TextAnchor.MiddleLeft; text.color = Color.black; text.raycastTarget = false;
                _outfitLockToggle.onValueChanged.AddListener(SetCurrentOutfitLock);
            }
            bool value = OutfitLocks(_lockAtom.uid).Enabled;
            if (_outfitLockToggle.isOn != value)
            {
                _updatingOutfitToggle = true;
                try { _outfitLockToggle.isOn = value; }
                finally { _updatingOutfitToggle = false; }
            }
        }
        private static void PositionOutfitLockOption(RectTransform list)
        {
            if (_outfitLockRect == null) return;
            list.GetWorldCorners(_lockCorners);
            // A top-left pivot outside the editor's right edge leaves the
            // bottom Select All footer and the HUD toolbar completely free.
            _outfitLockRect.position = _lockCorners[3] + list.right * (24f * list.lossyScale.x);
            _outfitLockRect.rotation = list.rotation;
        }
    }
}
