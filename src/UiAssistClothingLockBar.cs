using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    internal sealed class AceLockBarTag : MonoBehaviour { }
    internal sealed class AceLockSlotTag : MonoBehaviour
    {
        internal string Uid;
        internal RawImage Thumb;
        internal RawImage Dim;
    }

    internal static partial class UiAssistHudLink
    {
        // Horizontal strip stacked ABOVE the ban bar on the ACE editor window.
        // Entries are DAZClothingItem uids stored per Person atom: a locked
        // item cannot be taken off by any path (editor click, trigger,
        // plugin, scene restore). Dragging an unworn item in wears it first.
        private const float LockCellW = 96f;
        private const float LockCellH = 112f;
        private const float LockSpacing = 6f;
        private const float LockPad = 8f;

        private static GameObject _lockList;
        private static Canvas _lockCanvas;
        private static RectTransform _lockDock;
        private static RectTransform _lockCells;
        private static Image _lockBackground;
        private static Atom _lockAtom;
        private static bool _lockDirty = true;
        private static bool _lockPositionLogged;
        private static int _lockColumns = 4;
        private static RectTransform _lockPanel;
        private static readonly Dictionary<string, Texture2D> _lockThumbs =
            new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, List<string[]>> _lockStore =
            new Dictionary<string, List<string[]>>();
        private static readonly Dictionary<string, float> _lockLogAt =
            new Dictionary<string, float>();
        private static readonly Vector3[] _lockCorners = new Vector3[4];

        private static string LockPathFor(string atomUid)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                atomUid = atomUid.Replace(c, '_');
            return Path.Combine(Paths.ConfigPath,
                "Quest3TriggerUI.clothing-lock." + atomUid + ".txt");
        }

        private static List<string[]> LockEntriesFor(string atomUid)
        {
            if (string.IsNullOrEmpty(atomUid)) return null;
            List<string[]> entries;
            if (_lockStore.TryGetValue(atomUid, out entries)) return entries;
            entries = new List<string[]>();
            try
            {
                string path = LockPathFor(atomUid);
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
            _lockStore[atomUid] = entries;
            return entries;
        }

        private static List<string[]> CurrentLockEntries()
        {
            return _lockAtom == null ? null : LockEntriesFor(_lockAtom.uid);
        }

        private static void SaveLocks(string atomUid)
        {
            try
            {
                List<string[]> entries = LockEntriesFor(atomUid);
                List<string> lines = new List<string>();
                if (entries != null)
                    foreach (string[] f in entries)
                        lines.Add(f[0] + "|" + f[1] + "|" + f[2]);
                File.WriteAllLines(LockPathFor(atomUid), lines.ToArray());
            }
            catch (Exception e) { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogWarning("Clothing lock save failed: " + e.Message); }
        }

        // Veto entry point for the Harmony patches in UiAssistClothingBanBar.
        // Runs only when something tries to UNDRESS an item.
        internal static bool IsClothingLocked(Atom atom, string uid)
        {
            if (atom == null || string.IsNullOrEmpty(atom.uid)) return false;
            List<string[]> entries = LockEntriesFor(atom.uid);
            if (entries == null || entries.Count == 0) return false;
            string path = BanKey(uid);
            for (int i = 0; i < entries.Count; i++)
                if (entries[i][0] == uid || BanKey(entries[i][0]) == path)
                    return true;
            return false;
        }

        internal static void ReportBlockedUndress(Atom atom, DAZClothingItem item)
        {
            try
            {
                string key = (atom != null ? atom.uid : "?") + "/" + item.uid;
                float last;
                float now = Time.unscaledTime;
                if (_lockLogAt.TryGetValue(key, out last) && now - last < 10f) return;
                _lockLogAt[key] = now;
                Log("已拦截锁定服装的脱下：" +
                    (item.displayName ?? item.uid) + "（从锁定栏移除后才能脱下）");
            }
            catch { }
        }

        private static void ClearLockBar()
        {
            if (_lockCanvas != null && SuperController.singleton != null)
                SuperController.singleton.RemoveCanvas(_lockCanvas);
            if (_lockDock != null) UnityEngine.Object.Destroy(_lockDock.gameObject);
            _lockDock = null;
            _lockCells = null;
            _lockBackground = null;
            _lockCanvas = null;
            _lockList = null;
            _lockAtom = null;
            _lockPanel = null;
            _lockPositionLogged = false;
        }

        private static void UpdateLockBar(Snapshot state, GameObject list)
        {
            if (_lockList != list || _lockDock == null)
            {
                ClearLockBar();
                _lockList = list;
                _lockAtom = state.Target;
                _lockPanel = FindBanPanel(state, list);
                CreateLockBar(list);
            }
            if (_lockAtom != state.Target)
            {
                _lockAtom = state.Target;
                _lockDirty = true; // lock list is per-character
            }
            if (_lockDirty)
            {
                _lockDirty = false;
                RebuildLockCells();
            }
        }

        private static void CreateLockBar(GameObject list)
        {
            try
            {
                RectTransform listRect = (RectTransform)list.transform;
                _lockColumns = Mathf.Clamp(Mathf.FloorToInt(
                    (listRect.rect.width - LockPad * 2f + LockSpacing) /
                    (LockCellW + LockSpacing)), 2, 10);

                GameObject go = new GameObject("Quest3 ACE Lock Bar", typeof(RectTransform));
                go.SetActive(false);
                go.AddComponent<AceLockBarTag>();
                go.layer = list.layer;
                _lockDock = (RectTransform)go.transform;
                _lockDock.sizeDelta = new Vector2(listRect.rect.width,
                    LockPad * 2f + LockCellH);
                _lockDock.pivot = new Vector2(0.5f, 0f);
                _lockCanvas = go.AddComponent<Canvas>();
                _lockCanvas.renderMode = RenderMode.WorldSpace;
                Canvas parentCanvas = list.GetComponentInParent<Canvas>();
                if (parentCanvas != null) _lockCanvas.worldCamera = parentCanvas.worldCamera;
                go.AddComponent<GraphicRaycaster>();
                _lockBackground = go.AddComponent<Image>();
                _lockBackground.color = new Color(0f, 0.28f, 0.08f, 0.22f);
                _lockBackground.raycastTarget = true;

                GameObject cellsGo = new GameObject("Cells", typeof(RectTransform));
                _lockCells = (RectTransform)cellsGo.transform;
                _lockCells.SetParent(_lockDock, false);
                _lockCells.anchorMin = new Vector2(0f, 0f);
                _lockCells.anchorMax = new Vector2(1f, 1f);
                _lockCells.pivot = new Vector2(0.5f, 0f);
                _lockCells.anchoredPosition = new Vector2(0f, LockPad);
                GridLayoutGroup grid = cellsGo.AddComponent<GridLayoutGroup>();
                grid.cellSize = new Vector2(LockCellW, LockCellH);
                grid.spacing = new Vector2(LockSpacing, LockSpacing);
                grid.padding = new RectOffset((int)LockPad, (int)LockPad, 0, (int)LockPad);
                grid.childAlignment = TextAnchor.UpperCenter;
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                grid.constraintCount = _lockColumns;
                ContentSizeFitter fitter = cellsGo.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

                SuperController.singleton.AddCanvas(_lockCanvas);
                _lockDirty = true;
                Log("服装锁定栏已创建，等待定位。");
            }
            catch (Exception e) { ClearLockBar(); Error(e); }
        }

        private static void TickLockBar()
        {
            if (_lockDock == null || _lockList == null) return;
            SuperController sc = SuperController.singleton;
            bool visible = _lockList.activeInHierarchy && sc != null &&
                sc.MainHUDVisible && !_presetBrowsing;
            if (_lockDock.gameObject.activeSelf != visible)
                _lockDock.gameObject.SetActive(visible);
            if (!visible) return;
            if (!_lockPositionLogged)
            {
                _lockPositionLogged = true;
                Log("锁定栏可见，位置已锁定于禁用栏上方。");
            }

            RectTransform list = (RectTransform)_lockList.transform;
            _lockDock.rotation = list.rotation;
            _lockDock.localScale = list.lossyScale;
            // Stack directly above the ban bar's top edge. If the ban bar is
            // missing, fall back to the same panel-top anchor it would use.
            Vector3 baseTop;
            if (_banDock != null)
            {
                _banDock.GetWorldCorners(_lockCorners);
                baseTop = (_lockCorners[1] + _lockCorners[2]) * 0.5f;
            }
            else
            {
                list.GetWorldCorners(_lockCorners);
                baseTop = (_lockCorners[1] + _lockCorners[2]) * 0.5f;
                RectTransform panel = _lockPanel;
                if (panel != null && panel != list)
                {
                    panel.GetWorldCorners(_panelCorners);
                    Vector3 panelTop = (_panelCorners[1] + _panelCorners[2]) * 0.5f;
                    float extra = Vector3.Dot(panelTop - baseTop, list.up);
                    if (extra > 0f) baseTop += list.up * extra;
                }
                baseTop += list.up * ((12f + LockPad * 2f + LockCellH) *
                    list.lossyScale.x);
            }
            _lockDock.position = baseTop + list.up *
                (8f * list.lossyScale.x);
            _lockDock.position -= list.forward *
                (12f * list.lossyScale.x);
        }

        private static void RebuildLockCells()
        {
            if (_lockCells == null) return;
            foreach (Transform child in _lockCells)
                UnityEngine.Object.Destroy(child.gameObject);
            List<string[]> entries = CurrentLockEntries();
            int count = entries == null ? 0 : entries.Count;
            if (entries != null)
                foreach (string[] f in entries)
                    CreateLockSlot(f[0]);
            if (count == 0)
                CreateLockHintSlot();
            int rows = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(1, count) /
                (float)_lockColumns));
            _lockDock.sizeDelta = new Vector2(_lockDock.sizeDelta.x,
                LockPad * 2f + rows * (LockCellH + LockSpacing));
        }

        private static void CreateLockHintSlot()
        {
            GameObject slot = new GameObject("LockHint", typeof(RectTransform));
            slot.transform.SetParent(_lockCells, false);
            Image bg = slot.AddComponent<Image>();
            bg.color = new Color(0.35f, 1f, 0.45f, 0.14f);
            bg.raycastTarget = true;
            slot.AddComponent<AceLockBarTag>();
            GameObject textGo = new GameObject("Hint", typeof(RectTransform));
            RectTransform textRect = (RectTransform)textGo.transform;
            textRect.SetParent(slot.transform, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            Text hint = textGo.AddComponent<Text>();
            hint.text = "锁定";
            hint.alignment = TextAnchor.MiddleCenter;
            hint.fontSize = 22;
            hint.color = new Color(1f, 1f, 1f, 0.55f);
            hint.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            hint.raycastTarget = false;
        }

        private static void CreateLockSlot(string uid)
        {
            GameObject slot = new GameObject("LockSlot", typeof(RectTransform));
            slot.transform.SetParent(_lockCells, false);
            Image bg = slot.AddComponent<Image>();
            bg.color = new Color(0f, 0.3f, 0.08f, 0.4f);
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

            AceLockSlotTag tag = slot.AddComponent<AceLockSlotTag>();
            tag.Uid = uid;
            tag.Thumb = thumb;
            tag.Dim = dim;
            string captured = uid;
            button.onClick.AddListener(delegate { ReportLockSlot(captured); });
            RefreshLockSlotVisual(tag);
        }

        private static void ReportLockSlot(string uid)
        {
            VrHaptics.Press();
            List<string[]> entries = CurrentLockEntries();
            if (entries != null)
                foreach (string[] f in entries)
                    if (f[0] == uid)
                    {
                        Log("锁定中的服装：" + (f[1].Length > 0 ? f[1] : uid) +
                            "（拖出锁定栏可解除）");
                        return;
                    }
        }

        private static void RefreshLockSlotVisual(AceLockSlotTag tag)
        {
            Texture2D tex;
            _lockThumbs.TryGetValue(tag.Uid, out tex);
            if (tex != null)
            {
                tag.Thumb.texture = tex;
                tag.Thumb.color = Color.white;
            }
            else
            {
                tag.Thumb.texture = null;
                tag.Thumb.color = new Color(0.2f, 0.3f, 0.2f, 1f);
                FetchLockThumb(tag);
            }
            tag.Dim.gameObject.SetActive(ResolveClothingItem(tag.Uid, _lockAtom) == null);
        }

        private static void FetchLockThumb(AceLockSlotTag tag)
        {
            DAZClothingItem item = ResolveClothingItem(tag.Uid, _lockAtom);
            if (item == null) item = ResolveClothingItemAnywhere(tag.Uid);
            if (item == null) return;
            AceLockSlotTag captured = tag;
            item.GetThumbnail(delegate(Texture2D tex)
            {
                if (tex == null) return;
                _lockThumbs[tag.Uid] = tex;
                if (captured != null && captured.Thumb != null && captured.Uid == tag.Uid)
                {
                    captured.Thumb.texture = tex;
                    captured.Thumb.color = Color.white;
                }
            });
        }

        // ---- lock list mutation (called from the shared drag drop handler) ----

        private static bool AddLock(AceFavDragSource source)
        {
            List<string[]> entries = CurrentLockEntries();
            if (entries == null || _lockAtom == null) return false;
            string key = BanKey(source.Uid);
            foreach (string[] f in entries)
                if (f[0] == source.Uid || BanKey(f[0]) == key) return false;
            // Lock and ban contradict each other (never wear vs never
            // undress); lock and favorite coexist — a favorite click wears
            // the item, which is exactly what lock allows.
            RemoveBan(source.Uid);
            entries.Add(new string[] {
                source.Uid,
                source.DisplayName ?? "",
                source.CreatorName ?? "" });
            if (source.Texture != null) _lockThumbs[source.Uid] = source.Texture;
            else if (source.Item != null)
            {
                DAZClothingItem item = source.Item;
                item.GetThumbnail(delegate(Texture2D tex)
                { if (tex != null) _lockThumbs[item.uid] = tex; });
            }
            SaveLocks(_lockAtom.uid);
            _lockDirty = true;
            Log("已锁定服装：" + (source.DisplayName ?? source.Uid) +
                "（该角色任何方式都无法脱下）");
            // Locked means kept on: wear it now if it is not worn yet.
            if (source.Item != null && !source.Item.active)
            {
                try
                {
                    source.Item.characterSelector.SetActiveClothingItem(
                        source.Item, true, false);
                    Log("已穿上：" + (source.DisplayName ?? source.Uid));
                }
                catch (Exception e) { Error(e); }
            }
            return true;
        }

        private static bool RemoveLock(string uid)
        {
            List<string[]> entries = CurrentLockEntries();
            if (entries == null) return false;
            string key = BanKey(uid);
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i][0] != uid && BanKey(entries[i][0]) != key) continue;
                Log("已解除锁定：" + (entries[i][1].Length > 0 ? entries[i][1] : uid));
                entries.RemoveAt(i);
                _lockThumbs.Remove(uid);
                SaveLocks(_lockAtom.uid);
                _lockDirty = true;
                return true;
            }
            return false;
        }

        private static bool PointerOverLockBar()
        {
            GameObject target =
                VrPointerPresentation.CurrentLookTarget(DragRight());
            return target != null &&
                target.GetComponentInParent<AceLockBarTag>() != null;
        }
    }
}
