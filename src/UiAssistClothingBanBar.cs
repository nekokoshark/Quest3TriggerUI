using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    internal sealed class AceBanBarTag : MonoBehaviour { }
    internal sealed class AceBanSlotTag : MonoBehaviour
    {
        internal string Uid;
        internal RawImage Thumb;
        internal RawImage Dim;
    }

    internal static partial class UiAssistHudLink
    {
        // Horizontal strip docked to the TOP edge of the ACE list. Entries are
        // DAZClothingItem uids stored per Person atom: a banned item cannot be
        // worn by any path (editor click, trigger, plugin, scene restore).
        private const float BanCellW = 96f;
        private const float BanCellH = 112f;
        private const float BanSpacing = 6f;
        private const float BanPad = 8f;

        private static GameObject _banList;
        private static Canvas _banCanvas;
        private static RectTransform _banDock;
        private static RectTransform _banCells;
        private static Image _banBackground;
        private static Atom _banAtom;
        private static bool _banDirty = true;
        private static bool _banPositionLogged;
        private static int _banColumns = 4;
        private static readonly Dictionary<string, Texture2D> _banThumbs =
            new Dictionary<string, Texture2D>();
        // atomUid -> entries; loaded lazily so the veto works before the bar
        // ever opens (scene scripts can auto-wear while the editor is closed).
        private static readonly Dictionary<string, List<string[]>> _banStore =
            new Dictionary<string, List<string[]>>();
        private static readonly Dictionary<string, float> _banLogAt =
            new Dictionary<string, float>();
        private static readonly Vector3[] _panelCorners = new Vector3[4];
        private static RectTransform _banPanel;

        private static string BanPathFor(string atomUid)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                atomUid = atomUid.Replace(c, '_');
            return Path.Combine(Paths.ConfigPath,
                "Quest3TriggerUI.clothing-ban." + atomUid + ".txt");
        }

        private static List<string[]> BanEntriesFor(string atomUid)
        {
            if (string.IsNullOrEmpty(atomUid)) return null;
            List<string[]> entries;
            if (_banStore.TryGetValue(atomUid, out entries)) return entries;
            entries = new List<string[]>();
            try
            {
                string path = BanPathFor(atomUid);
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string[] parts = line.Split('|');
                        if (parts.Length >= 1 && parts[0].Length > 0)
                            entries.Add(new string[] {
                                parts[0],
                                parts.Length > 1 ? parts[1] : "",
                                parts.Length > 2 ? parts[2] : "" });
                    }
                }
            }
            catch (Exception e) { Error(e); }
            _banStore[atomUid] = entries;
            return entries;
        }

        private static List<string[]> CurrentBanEntries()
        {
            return _banAtom == null ? null : BanEntriesFor(_banAtom.uid);
        }

        private static void SaveBans(string atomUid)
        {
            try
            {
                List<string[]> entries = BanEntriesFor(atomUid);
                List<string> lines = new List<string>();
                if (entries != null)
                    foreach (string[] f in entries)
                        lines.Add(f[0] + "|" + f[1] + "|" + f[2]);
                File.WriteAllLines(BanPathFor(atomUid), lines.ToArray());
            }
            catch (Exception e) { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogWarning("Clothing ban save failed: " + e.Message); }
        }

        // Veto entry point for the Harmony patches below. Runs only when
        // something tries to WEAR an item -- a rare call, never per-frame.
        internal static bool IsClothingBanned(Atom atom, string uid)
        {
            if (atom == null || string.IsNullOrEmpty(atom.uid)) return false;
            List<string[]> entries = BanEntriesFor(atom.uid);
            if (entries == null || entries.Count == 0) return false;
            string path = BanKey(uid);
            for (int i = 0; i < entries.Count; i++)
                if (entries[i][0] == uid || BanKey(entries[i][0]) == path)
                    return true;
            return false;
        }

        // uid format is "<packageUid>:<vam path>"; the same clothing file can
        // be referenced under a different package alias (e.g. a scene bundle
        // vs the creator's own package), so the asset path is the real key.
        private static string BanKey(string uid)
        {
            int i = uid.IndexOf(':');
            return i >= 0 ? uid.Substring(i + 1) : uid;
        }

        internal static void ReportBlockedWear(Atom atom, DAZClothingItem item)
        {
            try
            {
                string key = (atom != null ? atom.uid : "?") + "/" + item.uid;
                float last;
                float now = Time.unscaledTime;
                if (_banLogAt.TryGetValue(key, out last) && now - last < 10f) return;
                _banLogAt[key] = now;
                Log("已拦截禁用服装的自动/手动穿戴：" +
                    (item.displayName ?? item.uid) + "（从禁用栏移除后才能穿戴）");
            }
            catch { }
        }

        private static void ClearBanBar()
        {
            if (_banCanvas != null && SuperController.singleton != null)
                SuperController.singleton.RemoveCanvas(_banCanvas);
            if (_banDock != null) UnityEngine.Object.Destroy(_banDock.gameObject);
            _banDock = null;
            _banCells = null;
            _banBackground = null;
            _banCanvas = null;
            _banList = null;
            _banAtom = null;
            _banPanel = null;
            _banPositionLogged = false;
        }

        private static void UpdateBanBar(Snapshot state, GameObject list)
        {
            if (_banList != list || _banDock == null)
            {
                ClearBanBar();
                _banList = list;
                _banAtom = state.Target;
                _banPanel = FindBanPanel(state, list);
                CreateBanBar(list);
            }
            if (_banAtom != state.Target)
            {
                _banAtom = state.Target;
                _banDirty = true; // ban list is per-character
            }
            if (_banDirty)
            {
                _banDirty = false;
                RebuildBanCells();
            }
        }

        private static RectTransform FindBanPanel(Snapshot state, GameObject list)
        {
            try
            {
                // The editor is not a nested window: the scroll list is a
                // direct child of a small shared canvas, and every editor
                // element (header buttons, window background, scrollbar) is
                // a sibling. Dock to the sibling with the highest top edge
                // that horizontally overlaps the list — the window frame /
                // header row — so the bar clears all editor buttons.
                RectTransform listRect = (RectTransform)list.transform;
                Canvas parentCanvas = list.GetComponentInParent<Canvas>();
                if (parentCanvas == null) return null;
                Rect local = listRect.rect;
                float listTop = local.yMax;
                RectTransform best = null;
                float bestTop = listTop + 30f;
                Vector3[] wc = new Vector3[4];
                Transform root = parentCanvas.transform;
                for (int i = 0; i < root.childCount; i++)
                {
                    RectTransform c = root.GetChild(i) as RectTransform;
                    if (c == null || c == listRect ||
                        !c.gameObject.activeInHierarchy) continue;
                    if (c.GetComponent<AceBanBarTag>() != null ||
                        c.GetComponent<AceLockBarTag>() != null ||
                        c.GetComponent<AceFavBarTag>() != null) continue;
                    c.GetWorldCorners(wc);
                    float minX = float.MaxValue, maxX = float.MinValue,
                          top = float.MinValue;
                    for (int k = 0; k < 4; k++)
                    {
                        Vector3 lp = listRect.InverseTransformPoint(wc[k]);
                        if (lp.x < minX) minX = lp.x;
                        if (lp.x > maxX) maxX = lp.x;
                        if (lp.y > top) top = lp.y;
                    }
                    if (maxX < local.xMin || minX > local.xMax) continue;
                    if (top > bestTop && top < listTop + 500f)
                    { bestTop = top; best = c; }
                }
                if (best != null)
                    Log("禁用栏锚点选定：" + best.name +
                        " top=" + bestTop.ToString("0"));
                return best;
            }
            catch (Exception e) { Error(e); return null; }
        }

        private static void CreateBanBar(GameObject list)
        {
            try
            {
                RectTransform listRect = (RectTransform)list.transform;
                _banColumns = Mathf.Clamp(Mathf.FloorToInt(
                    (listRect.rect.width - BanPad * 2f + BanSpacing) /
                    (BanCellW + BanSpacing)), 2, 10);

                GameObject go = new GameObject("Quest3 ACE Ban Bar", typeof(RectTransform));
                go.SetActive(false);
                go.AddComponent<AceBanBarTag>();
                go.layer = list.layer;
                _banDock = (RectTransform)go.transform;
                _banDock.sizeDelta = new Vector2(listRect.rect.width,
                    BanPad * 2f + BanCellH);
                _banDock.pivot = new Vector2(0.5f, 0f);
                _banCanvas = go.AddComponent<Canvas>();
                _banCanvas.renderMode = RenderMode.WorldSpace;
                Canvas parentCanvas = list.GetComponentInParent<Canvas>();
                if (parentCanvas != null) _banCanvas.worldCamera = parentCanvas.worldCamera;
                go.AddComponent<GraphicRaycaster>();
                _banBackground = go.AddComponent<Image>();
                _banBackground.color = new Color(0.4f, 0f, 0f, 0.22f);
                _banBackground.raycastTarget = true;

                GameObject cellsGo = new GameObject("Cells", typeof(RectTransform));
                _banCells = (RectTransform)cellsGo.transform;
                _banCells.SetParent(_banDock, false);
                _banCells.anchorMin = new Vector2(0f, 0f);
                _banCells.anchorMax = new Vector2(1f, 1f);
                _banCells.pivot = new Vector2(0.5f, 0f);
                _banCells.anchoredPosition = new Vector2(0f, BanPad);
                GridLayoutGroup grid = cellsGo.AddComponent<GridLayoutGroup>();
                grid.cellSize = new Vector2(BanCellW, BanCellH);
                grid.spacing = new Vector2(BanSpacing, BanSpacing);
                grid.padding = new RectOffset((int)BanPad, (int)BanPad, 0, (int)BanPad);
                grid.childAlignment = TextAnchor.UpperCenter;
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                grid.constraintCount = _banColumns;
                ContentSizeFitter fitter = cellsGo.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

                SuperController.singleton.AddCanvas(_banCanvas);
                _banDirty = true;
                Log("服装禁用栏已创建，等待定位。");
            }
            catch (Exception e) { ClearBanBar(); Error(e); }
        }

        private static void TickBanBar()
        {
            if (_banDock == null || _banList == null) return;
            SuperController sc = SuperController.singleton;
            bool visible = _banList.activeInHierarchy && sc != null &&
                sc.MainHUDVisible && !_presetBrowsing;
            if (_banDock.gameObject.activeSelf != visible)
                _banDock.gameObject.SetActive(visible);
            if (!visible) return;
            if (!_banPositionLogged)
            {
                _banPositionLogged = true;
                Log("禁用栏可见，位置已锁定于列表顶缘。");
            }

            RectTransform list = (RectTransform)_banList.transform;
            _banDock.rotation = list.rotation;
            _banDock.localScale = list.lossyScale;
            // Dock to the top edge of the whole editor window (the parent
            // canvas), not the scroll list — the header row with the editor's
            // own buttons sits directly above the list and must stay clear.
            // Pivot is bottom-center so extra rows grow upward away from it.
            list.GetWorldCorners(_dockCorners);
            Vector3 topCenter = (_dockCorners[1] + _dockCorners[2]) * 0.5f;
            // Lift to the selected sibling's top edge, keeping the list's
            // own horizontal center (the sibling may be an off-center
            // header button, not a full-width window frame).
            RectTransform panel = _banPanel;
            if (panel != null && panel != list)
            {
                panel.GetWorldCorners(_panelCorners);
                Vector3 panelTop = (_panelCorners[1] + _panelCorners[2]) * 0.5f;
                float extra = Vector3.Dot(panelTop - topCenter, list.up);
                if (extra > 0f) topCenter += list.up * extra;
            }
            _banDock.position = topCenter + list.up *
                (12f * list.lossyScale.x);
            // Same facing as the editor; nudge along the panel normal toward
            // the viewer so it renders in front without z-fighting.
            _banDock.position -= list.forward *
                (12f * list.lossyScale.x);
        }

        private static void RebuildBanCells()
        {
            if (_banCells == null) return;
            foreach (Transform child in _banCells)
                UnityEngine.Object.Destroy(child.gameObject);
            List<string[]> entries = CurrentBanEntries();
            int count = entries == null ? 0 : entries.Count;
            if (entries != null)
                foreach (string[] f in entries)
                    CreateBanSlot(f[0]);
            if (count == 0)
                CreateBanHintSlot();
            int rows = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(1, count) /
                (float)_banColumns));
            _banDock.sizeDelta = new Vector2(_banDock.sizeDelta.x,
                BanPad * 2f + rows * (BanCellH + BanSpacing));
        }

        private static void CreateBanHintSlot()
        {
            GameObject slot = new GameObject("BanHint", typeof(RectTransform));
            slot.transform.SetParent(_banCells, false);
            Image bg = slot.AddComponent<Image>();
            bg.color = new Color(1f, 0.35f, 0.3f, 0.16f);
            bg.raycastTarget = true;
            slot.AddComponent<AceBanBarTag>();
            GameObject textGo = new GameObject("Hint", typeof(RectTransform));
            RectTransform textRect = (RectTransform)textGo.transform;
            textRect.SetParent(slot.transform, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            Text hint = textGo.AddComponent<Text>();
            hint.text = "禁用";
            hint.alignment = TextAnchor.MiddleCenter;
            hint.fontSize = 22;
            hint.color = new Color(1f, 1f, 1f, 0.55f);
            hint.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            hint.raycastTarget = false;
        }

        private static void CreateBanSlot(string uid)
        {
            GameObject slot = new GameObject("BanSlot", typeof(RectTransform));
            slot.transform.SetParent(_banCells, false);
            Image bg = slot.AddComponent<Image>();
            bg.color = new Color(0.35f, 0f, 0f, 0.4f);
            bg.raycastTarget = true;
            Button button = slot.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;

            GameObject thumbGo = new GameObject("Thumb", typeof(RectTransform));
            RectTransform thumbRect = (RectTransform)thumbGo.transform;
            thumbRect.SetParent(slot.transform, false);
            thumbRect.anchorMin = Vector2.zero;
            thumbRect.anchorMax = Vector2.one;
            thumbRect.offsetMin = new Vector2(3f, 3f);
            thumbRect.offsetMax = new Vector2(-3f, -3f);
            RawImage thumb = thumbGo.AddComponent<RawImage>();
            thumb.raycastTarget = false;

            GameObject dimGo = new GameObject("Dim", typeof(RectTransform));
            RectTransform dimRect = (RectTransform)dimGo.transform;
            dimRect.SetParent(slot.transform, false);
            dimRect.anchorMin = Vector2.zero;
            dimRect.anchorMax = Vector2.one;
            dimRect.offsetMin = Vector2.zero;
            dimRect.offsetMax = Vector2.zero;
            RawImage dim = dimGo.AddComponent<RawImage>();
            dim.color = new Color(0f, 0f, 0f, 0.65f);
            dim.raycastTarget = false;

            AceBanSlotTag tag = slot.AddComponent<AceBanSlotTag>();
            tag.Uid = uid;
            tag.Thumb = thumb;
            tag.Dim = dim;
            string captured = uid;
            button.onClick.AddListener(delegate { ReportBanSlot(captured); });
            RefreshBanSlotVisual(tag);
        }

        private static void ReportBanSlot(string uid)
        {
            VrHaptics.Press();
            List<string[]> entries = CurrentBanEntries();
            if (entries != null)
                foreach (string[] f in entries)
                    if (f[0] == uid)
                    {
                        Log("禁用中的服装：" + (f[1].Length > 0 ? f[1] : uid) +
                            "（拖出禁用栏可解除）");
                        return;
                    }
        }

        private static void RefreshBanSlotVisual(AceBanSlotTag tag)
        {
            Texture2D tex;
            _banThumbs.TryGetValue(tag.Uid, out tex);
            if (tex != null)
            {
                tag.Thumb.texture = tex;
                tag.Thumb.color = Color.white;
            }
            else
            {
                tag.Thumb.texture = null;
                tag.Thumb.color = new Color(0.3f, 0.22f, 0.22f, 1f);
                FetchBanThumb(tag);
            }
            tag.Dim.gameObject.SetActive(ResolveClothingItem(tag.Uid, _banAtom) == null);
        }

        private static void FetchBanThumb(AceBanSlotTag tag)
        {
            DAZClothingItem item = ResolveClothingItem(tag.Uid, _banAtom);
            if (item == null) item = ResolveClothingItemAnywhere(tag.Uid);
            if (item == null) return;
            AceBanSlotTag captured = tag;
            item.GetThumbnail(delegate(Texture2D tex)
            {
                if (tex == null) return;
                _banThumbs[tag.Uid] = tex;
                if (captured != null && captured.Thumb != null && captured.Uid == tag.Uid)
                {
                    captured.Thumb.texture = tex;
                    captured.Thumb.color = Color.white;
                }
            });
        }

        // ---- ban list mutation (called from the shared drag drop handler) ----

        private static bool AddBan(AceFavDragSource source)
        {
            List<string[]> entries = CurrentBanEntries();
            if (entries == null || _banAtom == null) return false;
            string key = BanKey(source.Uid);
            foreach (string[] f in entries)
                if (f[0] == source.Uid || BanKey(f[0]) == key) return false;
            // Three-way exclusion: banning also clears lock + favorite.
            RemoveLock(source.Uid);
            RemoveFavorite(source.Uid, false);
            entries.Add(new string[] {
                source.Uid,
                source.DisplayName ?? "",
                source.CreatorName ?? "" });
            if (source.Texture != null) _banThumbs[source.Uid] = source.Texture;
            else if (source.Item != null)
            {
                DAZClothingItem item = source.Item;
                item.GetThumbnail(delegate(Texture2D tex)
                { if (tex != null) _banThumbs[item.uid] = tex; });
            }
            SaveBans(_banAtom.uid);
            _banDirty = true;
            Log("已禁用服装：" + (source.DisplayName ?? source.Uid) +
                "（该角色任何方式都无法穿上）");
            // A currently worn item is stripped immediately.
            if (source.Item != null && source.Item.active)
            {
                try
                {
                    source.Item.characterSelector.SetActiveClothingItem(
                        source.Item, false, false);
                    Log("已脱下：" + (source.DisplayName ?? source.Uid));
                }
                catch (Exception e) { Error(e); }
            }
            return true;
        }

        private static bool RemoveBan(string uid)
        {
            List<string[]> entries = CurrentBanEntries();
            if (entries == null) return false;
            string key = BanKey(uid);
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i][0] != uid && BanKey(entries[i][0]) != key) continue;
                Log("已解除禁用：" + (entries[i][1].Length > 0 ? entries[i][1] : uid));
                entries.RemoveAt(i);
                _banThumbs.Remove(uid);
                SaveBans(_banAtom.uid);
                _banDirty = true;
                return true;
            }
            return false;
        }

        private static bool PointerOverBanBar()
        {
            GameObject target =
                VrPointerPresentation.CurrentLookTarget(DragRight());
            return target != null &&
                target.GetComponentInParent<AceBanBarTag>() != null;
        }
    }

    // ---- wear-path vetoes ----
    // Every wear path in VaM funnels into SetActiveClothingItem (the string
    // overload, SyncClothingItem, ToggleClothingItem, SetActiveDynamicItem and
    // all plugin/trigger callers delegate to it); InitClothingItems is the one
    // exception that sets DAZDynamicItem.active directly, so the setter is
    // patched as well. Vetoing here also suppresses the exclusive-region
    // undress of sibling items that would otherwise still run.

    [HarmonyPatch(typeof(DAZCharacterSelector), "SetActiveClothingItem",
        new Type[] { typeof(DAZClothingItem), typeof(bool), typeof(bool) })]
    internal static class BanClothingWearPatch
    {
        // The selector's containingAtom is authoritative -- a lazily loaded
        // item may not have characterSelector assigned yet.
        private static bool Prefix(DAZCharacterSelector __instance,
            DAZClothingItem item, bool active)
        {
            if (item == null) return true;
            Atom atom = __instance != null ? __instance.containingAtom : null;
            if (active)
            {
                if (!UiAssistHudLink.IsClothingBanned(atom, item.uid)) return true;
                UiAssistHudLink.ReportBlockedWear(atom, item);
                return false;
            }
            if (!UiAssistHudLink.IsClothingLocked(atom, item.uid)) return true;
            UiAssistHudLink.ReportBlockedUndress(atom, item);
            return false;
        }
    }

    [HarmonyPatch(typeof(DAZDynamicItem), "set_active")]
    internal static class BanClothingActivePatch
    {
        private static bool Prefix(DAZDynamicItem __instance, bool value)
        {
            DAZClothingItem item = __instance as DAZClothingItem;
            if (item == null) return true;
            Atom atom = item.containingAtom;
            if (value)
            {
                if (__instance.active) return true;
                if (!UiAssistHudLink.IsClothingBanned(atom, item.uid)) return true;
                UiAssistHudLink.ReportBlockedWear(atom, item);
                return false;
            }
            if (!__instance.active) return true;
            if (!UiAssistHudLink.IsClothingLocked(atom, item.uid)) return true;
            UiAssistHudLink.ReportBlockedUndress(atom, item);
            return false;
        }
    }
}
