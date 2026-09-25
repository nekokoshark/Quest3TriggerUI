using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using MVR.FileManagement;
using SimpleJSON;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    internal static partial class UiAssistHudLink
    {
        // Preset dock pinned to the LEFT edge of the ACE list — same anatomy
        // as the right favorites bar: a vertical tag strip on the outer edge
        // (人物/发型/服装/皮肤/化妆 plus ＋新增/读取/保存 rows), a two-column
        // cell grid, and a bottom nav row that only appears when the tab
        // overflows one page. The merged 人物 tab pops 替换/外观 mini
        // buttons on a clicked thumbnail — full replace vs. same preset
        // minus clothing. Other tabs apply only their own section of a
        // person preset — the same section loaders the radial menu uses —
        // so a slot never nukes the rest of the person. The 新增 row opens
        // the preset browser in pick mode: every .vap click files it under
        // the active tab without closing (batch saving); presets whose type
        // does not match the tab are skipped silently. Dragging a slot
        // thumbnail out of the dock removes it.
        private static readonly string[] PdTabNames =
            { "人物", "发型", "服装", "皮肤", "化妆" };
        // Per-tab browse root — 人物 uses the person-preset library,
        // 发型/服装/皮肤 open their own manager dirs; 化妆 is a clothing
        // preset subfolder.
        private static string PdTabDir(int tab)
        {
            switch (tab)
            {
                case 1: return PluginPaths.HairPresetDir;
                case 2: return PluginPaths.ClothingPresetDir;
                case 3: return PluginPaths.SkinPresetDir;
                case 4: return PluginPaths.ClothingPresetDir + "/化妆";
                default: return PluginPaths.AppearancePresetDir;
            }
        }
        private const int PdTabCount = 5;
        private const float PdStripW = 96f;
        private const float PdNameH = 18f;
        // The translucent caption overlays the thumbnail's bottom edge, so
        // cells stay one name-strip shorter than the favorites grid.
        private const float PdCellH = FavCellH - PdNameH;
        private const float PdGridH = PdCellH * FavRows + FavSpacing * (FavRows - 1) + FavPad * 2f;

        private static RectTransform _pdDock, _pdCells, _pdTabStrip;
        private static Canvas _pdCanvas;
        private static readonly Image[] _pdTabBgs = new Image[PdTabCount];
        private static readonly RectTransform[] _pdTabRects =
            new RectTransform[PdTabCount];
        // While the dock's own pick browser is open the dock must survive —
        // _presetBrowsing would otherwise clear every side bar.
        private static bool _pdPicking;
        private static readonly List<string>[] _pdSlots =
            new List<string>[PdTabCount];
        private static int _pdTab;
        private static bool _pdDirty = true, _pdLoaded, _pdPositionLogged;
        private static float _pdListHeight;
        private static GameObject _pdList;
        private static Atom _pdAtom;
        // Per-atom record of what the 化妆 tab last put on: the preset path
        // and the internalIds of the items it added. Clicking the same
        // preset again removes exactly those items (not the whole outfit);
        // loading a different makeup preset swaps them out first.
        private sealed class MakeupApplied
        {
            internal string Path;
            internal List<string> ItemIds;
        }
        private static readonly Dictionary<string, MakeupApplied>
            _makeupApplied = new Dictionary<string, MakeupApplied>();
        private static SceneQuickActions _pdQuick;
        private static readonly Dictionary<string, Texture2D> _pdThumbs =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        // Sidecar .jpg write-tick per slot path — same freshness-stamp
        // scheme as the browser's _thumbStamp: an overwritten preset's
        // re-shot thumbnail bumps the mtime and gets re-decoded instead
        // of keeping the stale image forever.
        private static readonly Dictionary<string, long> _pdThumbStamp =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        private static string PdSlotsPath
        {
            get { return Path.Combine(Paths.ConfigPath,
                "Quest3TriggerUI.preset-dock.txt"); }
        }

        // Own marker tags: AceFavBarTag would make PointerOverFavoritesBar
        // claim our dock and misfile clothing drops.
        private sealed class PdDockTag : MonoBehaviour { }
        private sealed class PdTabButtonTag : MonoBehaviour
        {
            internal int Index;
        }
        // Click = apply via a Button wired in CreatePdSlot; drags are driven
        // by the plugin's own press pipeline (long-press → AceFavDragSource),
        // not Unity drag events — VaM's LookInputModule never fires
        // IBeginDragHandler here.
        private sealed class PdSlotTag : MonoBehaviour
        {
            internal string Path;
            internal RawImage Thumb;
            // Slot background — scroll visibility toggles this off so a
            // masked cell outside the viewport can't catch a laser hit.
            internal Image Bg;
        }
        // Marks the 替换/外观 mini-button overlay so the press pipeline's
        // drag-source resolver does not treat a tap on it as grabbing the
        // underlying slot.
        private sealed class PdOverlayTag : MonoBehaviour { }
        // One shared overlay hops between cells of the 人物 tab.
        private static GameObject _pdPersonOverlay;
        private static string _pdOverlayPath;

        // Called from UpdatePresetButtons while the ACE editor is alive.
        private static void UpdatePresetDock(Snapshot state, GameObject list)
        {
            if (list == null) { ClearPresetDock(); return; }
            _pdList = list;
            _pdListHeight = ((RectTransform)list.transform).rect.height;
            _pdAtom = state != null ? state.Target : null;
            if (_pdDock != null) return;
            try
            {
                EnsurePdSlots();
                GameObject go = new GameObject("Quest3 Preset Dock",
                    typeof(RectTransform));
                go.SetActive(false);
                go.AddComponent<PdDockTag>();
                go.layer = list.layer;
                _pdDock = (RectTransform)go.transform;
                _pdDock.pivot = new Vector2(0.5f, 0.5f);
                _pdCanvas = go.AddComponent<Canvas>();
                _pdCanvas.renderMode = RenderMode.WorldSpace;
                Canvas parentCanvas = list.GetComponentInParent<Canvas>();
                if (parentCanvas != null)
                    _pdCanvas.worldCamera = parentCanvas.worldCamera;
                go.AddComponent<GraphicRaycaster>();
                Image bg = go.AddComponent<Image>();
                bg.color = new Color(0f, 0f, 0f, 0.32f);
                bg.raycastTarget = true;

                // Cells hug the dock's right edge (the side facing the ACE
                // list); the tab strip takes the outer left edge — the
                // mirror image of the favorites bar's [cells|tags] layout.
                CreatePdTabStrip();
                // Cells live in a masked viewport — same scroll model as
                // the favorites bar (anchoredPosition.y is the offset).
                GameObject viewGo = new GameObject("View",
                    typeof(RectTransform));
                _pdView = (RectTransform)viewGo.transform;
                _pdView.SetParent(_pdDock, false);
                _pdView.anchorMin = new Vector2(0f, 1f);
                _pdView.anchorMax = new Vector2(0f, 1f);
                _pdView.pivot = new Vector2(0f, 1f);
                _pdView.anchoredPosition = new Vector2(
                    FavTagGap + PdStripW, -FavPad);
                _pdView.sizeDelta = new Vector2(FavColW, PdGridH);
                viewGo.AddComponent<RectMask2D>();
                GameObject cellsGo = new GameObject("Cells",
                    typeof(RectTransform));
                _pdCells = (RectTransform)cellsGo.transform;
                _pdCells.SetParent(_pdView, false);
                _pdCells.anchorMin = new Vector2(0f, 1f);
                _pdCells.anchorMax = new Vector2(0f, 1f);
                _pdCells.pivot = new Vector2(0f, 1f);
                _pdCells.anchoredPosition = Vector2.zero;
                GridLayoutGroup grid = cellsGo.AddComponent<GridLayoutGroup>();
                grid.cellSize = new Vector2(FavCellW, PdCellH);
                grid.spacing = new Vector2(FavSpacing, FavSpacing);
                grid.padding = new RectOffset((int)FavPad, (int)FavPad,
                    0, (int)FavPad);
                grid.childAlignment = TextAnchor.UpperCenter;
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                grid.constraintCount = FavColumns;
                cellsGo.AddComponent<PdDockTag>();
                CreateDockScrollbar(_pdDock,
                    FavTagGap + PdStripW + FavColW, -FavPad, PdGridH,
                    out _pdScrollTrack, out _pdScrollThumb);

                SuperController.singleton.AddCanvas(_pdCanvas);
                _pdDirty = true;
            }
            catch (Exception e) { ClearPresetDock(); Error(e); }
        }

        private static void ClearPresetDock()
        {
            ClearPdReorder();
            _pdVisibleCells.Clear();
            _pdKeptCells.Clear();
            _pdHintCell = null;
            _pdPreviewDirty = false;
            _pdThumbQueue.Clear();
            _pdThumbQueued.Clear();
            _dockDeleteMode = false;
            _dockDeleteButton = null;
            _dockDeleteLabel = null;
            _pdScrollY = 0f;
            _pdContentH = 0f;
            _pdView = null;
            _pdScrollTrack = null;
            _pdScrollThumb = null;
            _pdAtom = null;
            _pdList = null;
            _pdPositionLogged = false;
            _pdDirty = true;
            if (SuperController.singleton != null && _pdCanvas != null)
            {
                try { SuperController.singleton.RemoveCanvas(_pdCanvas); }
                catch { }
            }
            _pdCanvas = null;
            _pdTabStrip = null;
            for (int i = 0; i < PdTabCount; i++)
            {
                _pdTabBgs[i] = null;
                _pdTabRects[i] = null;
            }
            if (_pdDock != null)
            {
                UnityEngine.Object.Destroy(_pdDock.gameObject);
                _pdDock = null;
                _pdCells = null;
            }
            foreach (Texture2D t in _pdThumbs.Values)
                if (t != null) UnityEngine.Object.Destroy(t);
            _pdThumbs.Clear();
            _pdThumbStamp.Clear();
        }

        private static void TickPresetDock()
        {
            // _pdList is our own reference to the ACE list — the favorites
            // bar nulls _favList when it clears, which must not take this
            // dock's visibility down with it.
            if (_pdDock == null || _pdList == null) return;
            SuperController sc = SuperController.singleton;
            // Unlike the favorites bar this stays up while its own pick
            // browser is open — the browser sits over the editor (right of
            // this dock), and seeing slots land live is the point (the
            // browser only alpha-hides the HUD, so activeInHierarchy stays
            // true anyway). But MainHUDVisible must gate BOTH branches:
            // hiding the control panel while browsing used to leave the
            // dock alive, riding the vanishing list's corners downward —
            // the "dock slides off like it's falling" bug.
            bool visible = sc != null && sc.MainHUDVisible &&
                (_pdPicking || _pdList.activeInHierarchy);
            if (_pdDock.gameObject.activeSelf != visible)
                _pdDock.gameObject.SetActive(visible);
            if (!visible) return;
            if (!_pdPositionLogged)
            {
                _pdPositionLogged = true;
                Log("预设收藏栏可见，位置已锁定于列表左缘。");
            }

            RectTransform list = (RectTransform)_pdList.transform;
            list.GetWorldCorners(_dockCorners);
            _pdDock.rotation = list.rotation;
            _pdDock.localScale = list.lossyScale;
            // Left edge of the ACE list, offset outward by half our width.
            Vector3 leftCenter = (_dockCorners[0] + _dockCorners[1]) * 0.5f;
            _pdDock.position = leftCenter - list.right *
                (24f * list.lossyScale.x +
                 _pdDock.rect.width * _pdDock.lossyScale.x * 0.5f);
            Camera viewer = sc.lookCamera;
            if (viewer != null)
            {
                Vector3 away = _pdDock.position - viewer.transform.position;
                _pdDock.position -= away.normalized *
                    (12f * list.lossyScale.x);
                if (away.sqrMagnitude > 0.0001f &&
                    Vector3.Cross(away, list.up).sqrMagnitude > 0.0001f)
                    _pdDock.rotation =
                        Quaternion.LookRotation(away, list.up);
            }
            if (_pdDirty || _pdPreviewDirty)
            {
                RebuildPdCells();
                _pdDirty = false;
            }
            TickDockScroll(false);
            TickPdThumbnails();
        }

        // Vertical tab strip on the dock's outer (left) edge — same idiom as
        // the favorites bar's FavTagStrip: caption, one row per fixed tab,
        // and the ＋新增 row at the bottom (the TagCreate counterpart).
        private static void CreatePdTabStrip()
        {
            GameObject strip = new GameObject("PdTabStrip",
                typeof(RectTransform));
            _pdTabStrip = (RectTransform)strip.transform;
            _pdTabStrip.SetParent(_pdDock, false);
            _pdTabStrip.anchorMin = new Vector2(0f, 1f);
            _pdTabStrip.anchorMax = new Vector2(0f, 1f);
            _pdTabStrip.pivot = new Vector2(0f, 1f);
            _pdTabStrip.anchoredPosition = new Vector2(FavTagGap, -FavPad);
            float y = 0f;
            AddPdStripCaption("预设标签", y);
            y += TagCaptionH + FavTagGap;
            for (int i = 0; i < PdTabCount; i++)
            {
                CreatePdTabRow(i, y);
                y += FavTagRowH + FavTagGap;
            }
            CreatePdAddRow(y);
            y += FavTagRowH + FavTagGap;
            // 读取/保存: the radial 人物 sub-actions re-homed into the dock —
            // whichever tab is selected decides which preset section they
            // mean.
            CreatePdIoRow("读 取", y, new Color(0.13f, 0.24f, 0.32f, 1f),
                OpenDockPresetLoader);
            y += FavTagRowH + FavTagGap;
            CreatePdIoRow("保 存", y, new Color(0.30f, 0.22f, 0.12f, 1f),
                OpenDockPresetSaver);
            y += FavTagRowH + FavTagGap;
            GameObject deleteRow = CreatePdIoRow("删 除", y,
                new Color(0.28f, 0.15f, 0.15f, 1f), ToggleDockDeleteMode);
            _dockDeleteButton = deleteRow.GetComponent<Image>();
            _dockDeleteLabel = deleteRow.GetComponentInChildren<Text>();
            PaintDockDeleteMode();
            y += FavTagRowH;
            _pdTabStrip.sizeDelta = new Vector2(PdStripW, y);
            PdPaintTabs();
        }

        private static void AddPdStripCaption(string value, float y)
        {
            GameObject go = new GameObject("PdCaption", typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(_pdTabStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(PdStripW, TagCaptionH);
            Text text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 14;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = new Color(1f, 1f, 1f, 0.55f);
            text.raycastTarget = false;
            text.text = value;
        }

        private static void CreatePdTabRow(int index, float y)
        {
            int idx = index;
            GameObject row = new GameObject("Tab " + PdTabNames[index],
                typeof(RectTransform));
            RectTransform rect = (RectTransform)row.transform;
            rect.SetParent(_pdTabStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(PdStripW, FavTagRowH);
            _pdTabRects[index] = rect;
            Image bg = row.AddComponent<Image>();
            bg.raycastTarget = true;
            _pdTabBgs[index] = bg;
            row.AddComponent<PdTabButtonTag>().Index = index;
            Button button = row.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                // A drag release landing on a tab is a cross-tab file,
                // not a tab switch on top of the drop commit.
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                VrHaptics.Press();
                PdSelectTab(idx);
            });
            Text text = new GameObject("Label", typeof(RectTransform))
                .AddComponent<Text>();
            RectTransform tr = (RectTransform)text.transform;
            tr.SetParent(rect, false);
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            text.text = PdTabNames[index];
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = 15;
            text.color = Color.white;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.raycastTarget = false;
        }

        // "＋新增" — the preset-dock counterpart of the favorites strip's
        // TagCreate row: opens the preset browser in pick mode.
        private static void CreatePdAddRow(float y)
        {
            CreatePdIoRow("＋ 新增", y, new Color(0.13f, 0.30f, 0.20f, 1f),
                OpenPresetDockPicker);
        }

        private static GameObject CreatePdIoRow(string label, float y, Color bg,
            Action action)
        {
            GameObject row = new GameObject("PdIo " + label,
                typeof(RectTransform));
            RectTransform rect = (RectTransform)row.transform;
            rect.SetParent(_pdTabStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(PdStripW, FavTagRowH);
            Image image = row.AddComponent<Image>();
            image.color = bg;
            image.raycastTarget = true;
            Button button = row.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                // Dropping a dragged slot on this row must not pop the
                // browser on top of the drop commit.
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                VrHaptics.Press();
                action();
            });
            Text text = new GameObject("Label", typeof(RectTransform))
                .AddComponent<Text>();
            RectTransform tr = (RectTransform)text.transform;
            tr.SetParent(rect, false);
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            text.text = label;
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = 14;
            text.color = Color.white;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.raycastTarget = false;
            return row;
        }

        private static void PdPaintTabs()
        {
            for (int i = 0; i < PdTabCount; i++)
            {
                if (_pdTabBgs[i] == null) continue;
                _pdTabBgs[i].color = i == _pdTab
                    ? new Color(0.11f, 0.38f, 0.48f, 1f)
                    : new Color(0.16f, 0.16f, 0.2f, 1f);
            }
        }

        // Which tab row (if any) the pointer is over — cursor-plane first
        // (tracks mid-drag), look target as the fallback. Dock drags answer
        // to the hand that started them.
        private static int PresetDockTabUnderPointer()
        {
            return PresetDockTabUnderPointer(DragRight());
        }

        private static int PresetDockTabUnderPointer(bool right)
        {
            Vector2 local;
            for (int i = 0; i < PdTabCount; i++)
            {
                RectTransform row = _pdTabRects[i];
                if (row != null && PointerOnRect(row, out local))
                    return i;
            }
            GameObject t = VrPointerPresentation.CurrentLookTarget(right);
            PdTabButtonTag tag = t != null
                ? t.GetComponentInParent<PdTabButtonTag>() : null;
            return tag != null ? tag.Index : -1;
        }

        private static bool PointerOverPresetDock()
        {
            return PointerOverPresetDock(DragRight());
        }

        private static bool PointerOverPresetDock(bool right)
        {
            GameObject t = VrPointerPresentation.CurrentLookTarget(right);
            if (t != null && t.GetComponentInParent<PdDockTag>() != null)
                return true;
            // Preview rebuilds replace slot objects — hit the persistent
            // dock plane so a stale look target cannot turn a reorder into
            // a removal (same fallback as the favorites bar).
            Vector2 p;
            return PointerOnRect(_pdDock, out p);
        }

        // Drag-to-file entry for the preset browser: the tab row under the
        // pointer wins, else the active tab when over the dock body, else
        // -1 (pointer not on the dock at all). The caller names the hand
        // that owns its gesture — the pointer that pressed, not whichever
        // laser happens to rest on the dock.
        internal static int PresetDockTabAtPointer(bool right)
        {
            if (_pdDock == null || !_pdDock.gameObject.activeSelf)
                return -1;
            int tab = PresetDockTabUnderPointer(right);
            if (tab >= 0) return tab;
            return PointerOverPresetDock(right) ? _pdTab : -1;
        }

        // Batch variant of FileDockPreset — one save + one rebuild for the
        // whole drop. Paths already present or of an incompatible preset
        // type are silently skipped (returns how many actually landed).
        internal static int FileDockPresets(int tab,
            IEnumerable<string> paths)
        {
            EnsurePdSlots();
            if (tab < 0 || tab >= PdTabCount || paths == null) return 0;
            List<string> slots = _pdSlots[tab];
            int added = 0;
            foreach (string p in paths)
            {
                if (string.IsNullOrEmpty(p) || slots.Contains(p)) continue;
                if (!DockAccepts(tab, p)) continue;
                slots.Add(p);
                added++;
            }
            if (added == 0) return 0;
            SavePdSlots();
            // Newly filed entries land at the end — scroll to them.
            if (tab == _pdTab)
                _pdScrollY = float.MaxValue;
            // Dropped onto another tab's row — switch to it so the result
            // is visible (PdSelectTab no-ops when it is the active tab).
            PdSelectTab(tab);
            _pdDirty = true;
            return added;
        }

        private static void PdSelectTab(int idx)
        {
            if (idx == _pdTab) return;
            _pdTab = idx;
            _pdScrollY = 0f;
            PdPaintTabs();
            _pdDirty = true;
        }

        private static void RebuildPdCells()
        {
            if (_pdCells == null) return;
            EnsurePdSlots();
            _pdKeptCells.Clear();
            _pdCellPosition = 0;
            List<string> slots = PdDisplaySlots();
            int count = slots.Count;
            // All entries get a cell — the viewport mask + scroll offset do
            // the slicing now, and path-keyed reuse keeps rebuilds cheap.
            for (int i = 0; i < count; i++)
                CreatePdSlot(slots[i]);
            if (count == 0)
                CreatePdHintSlot();
            FinishPdCells();
            int rows = Mathf.CeilToInt(count / (float)FavColumns);
            _pdContentH = rows > 0
                ? rows * PdCellH + (rows - 1) * FavSpacing + FavPad
                : FavPad;
            _pdCells.sizeDelta = new Vector2(FavColW, _pdContentH);
            ApplyDockScroll(false, _pdScrollY);
            // Dock: [left-edge tab strip][cells viewport] — fixed grid height,
            // the scroll offset replaces the old bottom nav row.
            float stripNeed = _pdTabStrip == null ? 0f
                : _pdTabStrip.sizeDelta.y + FavPad * 2f;
            _pdDock.sizeDelta = new Vector2(FavTagGap + PdStripW + FavColW,
                Mathf.Max(64f, Mathf.Max(PdGridH, stripNeed)));
        }

        // Empty-tab placeholder, mirroring the favorites bar's FavHint cell.
        private static void CreatePdHintSlot()
        {
            if (_pdHintCell != null) return;
            GameObject slot = new GameObject("PdHint", typeof(RectTransform));
            _pdHintCell = slot;
            slot.transform.SetParent(_pdCells, false);
            Image bg = slot.AddComponent<Image>();
            bg.color = new Color(0.3f, 0.5f, 1f, 0.18f);
            bg.raycastTarget = true;
            slot.AddComponent<PdDockTag>();
            GameObject textGo = new GameObject("Hint", typeof(RectTransform));
            RectTransform textRect = (RectTransform)textGo.transform;
            textRect.SetParent(slot.transform, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            Text hint = textGo.AddComponent<Text>();
            hint.text = "预设";
            hint.alignment = TextAnchor.MiddleCenter;
            hint.fontSize = 22;
            hint.color = new Color(1f, 1f, 1f, 0.55f);
            hint.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            hint.raycastTarget = false;
        }

        private static void CreatePdSlot(string path)
        {
            if (ReusePdCell(path)) return;
            GameObject cell = new GameObject("PdSlot", typeof(RectTransform));
            RectTransform cr = (RectTransform)cell.transform;
            cr.SetParent(_pdCells, false);
            Image bg = cell.AddComponent<Image>();
            bg.color = new Color(0.16f, 0.16f, 0.2f, 1f);

            GameObject thumbGo = new GameObject("Thumb",
                typeof(RectTransform));
            RectTransform tr = (RectTransform)thumbGo.transform;
            tr.SetParent(cr, false);
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            RawImage thumb = thumbGo.AddComponent<RawImage>();
            thumb.raycastTarget = false;

            GameObject nameBg = new GameObject("NameBg",
                typeof(RectTransform));
            RectTransform nbr = (RectTransform)nameBg.transform;
            nbr.SetParent(cr, false);
            nbr.anchorMin = new Vector2(0f, 0f);
            nbr.anchorMax = new Vector2(1f, 0f);
            nbr.offsetMin = Vector2.zero;
            nbr.offsetMax = new Vector2(0f, PdNameH);
            Image nbgi = nameBg.AddComponent<Image>();
            nbgi.color = new Color(0f, 0f, 0f, 0.55f);
            nbgi.raycastTarget = false;
            Text name = new GameObject("Label", typeof(RectTransform))
                .AddComponent<Text>();
            RectTransform ntr = (RectTransform)name.transform;
            ntr.SetParent(nbr, false);
            ntr.anchorMin = Vector2.zero;
            ntr.anchorMax = Vector2.one;
            ntr.offsetMin = new Vector2(3f, 0f);
            ntr.offsetMax = new Vector2(-3f, 0f);
            name.text = PdDisplayName(path);
            name.alignment = TextAnchor.MiddleCenter;
            name.fontSize = 12;
            name.color = new Color(1f, 1f, 1f, 0.9f);
            name.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            name.raycastTarget = false;

            PdSlotTag tag = cell.AddComponent<PdSlotTag>();
            tag.Path = path;
            tag.Thumb = thumb;
            tag.Bg = bg;
            _pdVisibleCells.Add(tag);
            PlacePdCell(tag);
            Button button = cell.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            string captured = path;
            button.onClick.AddListener(delegate
            {
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                if (DeletePresetOnClick(captured)) return;
                // 人物 tab: a click pops the 替换/外观 choice overlay on
                // this thumbnail instead of applying immediately.
                if (_pdTab == 0)
                    TogglePdPersonOverlay(cr, captured);
                else
                    ApplyDockSlot(captured);
            });
            ApplyPdThumb(tag);
        }

        // ---------- 人物 tab: 替换/外观 mini-button overlay ----------

        private static void TogglePdPersonOverlay(
            RectTransform cell, string path)
        {
            if (_pdPersonOverlay != null &&
                _pdPersonOverlay.activeSelf &&
                _pdPersonOverlay.transform.parent == cell)
            {
                _pdPersonOverlay.SetActive(false);
                return;
            }
            EnsurePdPersonOverlay();
            _pdOverlayPath = path;
            RectTransform rt = (RectTransform)_pdPersonOverlay.transform;
            rt.SetParent(cell, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            // Cover the thumbnail, leave the name strip visible.
            rt.offsetMin = new Vector2(0f, PdNameH);
            rt.offsetMax = Vector2.zero;
            rt.SetAsLastSibling();
            _pdPersonOverlay.SetActive(true);
            VrHaptics.Press();
        }

        private static void EnsurePdPersonOverlay()
        {
            if (_pdPersonOverlay != null) return;
            GameObject go = new GameObject("PdPersonOverlay",
                typeof(RectTransform));
            go.AddComponent<PdOverlayTag>();
            go.AddComponent<PdDockTag>();
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.72f);
            bg.raycastTarget = true;
            RectTransform rt = (RectTransform)go.transform;
            CreatePdOverlayButton(rt, "替 换", -1f,
                new Color(0.45f, 0.16f, 0.16f, 1f),
                delegate { ApplyPdPersonChoice(true); });
            CreatePdOverlayButton(rt, "外 观", 1f,
                new Color(0.13f, 0.30f, 0.20f, 1f),
                delegate { ApplyPdPersonChoice(false); });
            _pdPersonOverlay = go;
            go.SetActive(false);
        }

        private static void CreatePdOverlayButton(RectTransform parent,
            string label, float side, Color color,
            UnityEngine.Events.UnityAction action)
        {
            GameObject go = new GameObject("PdChoice " + label,
                typeof(RectTransform));
            go.AddComponent<PdOverlayTag>();
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(
                side * (FavCellW * 0.25f + 1f), 0f);
            rect.sizeDelta = new Vector2(FavCellW * 0.5f - 8f, 34f);
            Image bg = go.AddComponent<Image>();
            bg.color = color;
            bg.raycastTarget = true;
            Button button = go.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                VrHaptics.Press();
                action();
            });
            Text text = new GameObject("Label", typeof(RectTransform))
                .AddComponent<Text>();
            RectTransform tr = (RectTransform)text.transform;
            tr.SetParent(rect, false);
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            text.text = label;
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = 13;
            text.color = Color.white;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.raycastTarget = false;
        }

        private static void ApplyPdPersonChoice(bool full)
        {
            if (_pdPersonOverlay != null)
                _pdPersonOverlay.SetActive(false);
            string path = _pdOverlayPath;
            if (string.IsNullOrEmpty(path)) return;
            Atom target = _pdAtom;
            if (target == null) return;
            if (_pdQuick == null)
                _pdQuick = new SceneQuickActions(
                    Quest3TriggerUIPlugin.Instance);
            try
            {
                JSONStorable ap =
                    target.GetStorableByID("AppearancePresets");
                if (ap == null) return;
                if (full)
                    _pdQuick.LoadFullAppearancePreset(target, ap, path);
                else
                    _pdQuick.LoadAppearanceWithoutClothing(
                        target, ap, path);
            }
            catch (Exception e) { Error(e); }
        }

        private static string PdDisplayName(string path)
        {
            string file = path;
            int slash = file.LastIndexOf('/');
            if (slash >= 0) file = file.Substring(slash + 1);
            if (file.EndsWith(".vap", StringComparison.OrdinalIgnoreCase))
                file = file.Substring(0, file.Length - 4);
            if (file.StartsWith("Preset_", StringComparison.OrdinalIgnoreCase))
                file = file.Substring("Preset_".Length);
            return file;
        }

        private static void LoadPdThumb(PdSlotTag tag)
        {
            string jpg = tag.Path.Substring(0, tag.Path.Length -
                Path.GetExtension(tag.Path).Length) + ".jpg";
            string full = FileManager.GetFullPath(jpg);
            long stamp = -1L;
            try
            {
                if (File.Exists(full))
                    stamp = File.GetLastWriteTimeUtc(full).Ticks;
            }
            catch { }
            Texture2D tex;
            long cachedStamp;
            if (!_pdThumbStamp.TryGetValue(tag.Path, out cachedStamp) ||
                cachedStamp != stamp)
            {
                Texture2D old;
                if (_pdThumbs.TryGetValue(tag.Path, out old))
                {
                    _pdThumbs.Remove(tag.Path);
                    if (old != null) UnityEngine.Object.Destroy(old);
                }
                _pdThumbStamp[tag.Path] = stamp;
            }
            if (!_pdThumbs.TryGetValue(tag.Path, out tex))
            {
                tex = null;
                try
                {
                    if (File.Exists(full))
                    {
                        byte[] bytes = File.ReadAllBytes(full);
                        Texture2D t = new Texture2D(2, 2,
                            TextureFormat.RGBA32, false);
                        if (t.LoadImage(bytes)) tex = t;
                        else UnityEngine.Object.Destroy(t);
                    }
                }
                catch { tex = null; }
                _pdThumbs[tag.Path] = tex;
            }
            tag.Thumb.texture = tex;
            tag.Thumb.color = tex != null
                ? Color.white
                : new Color(0.25f, 0.25f, 0.3f, 1f);
        }

        // ---------- slots persistence ----------

        // Called only after a local file operation succeeds. A favorite is
        // a preset reference, not a copy: both loading and its sidecar must
        // follow the move. Update in place to preserve tab membership/order.
        internal static void NotifyDockPresetMoved(string source,
            string destination, bool directory)
        {
            try
            {
                EnsurePdSlots();
                bool changed = false;
                for (int tab = 0; tab < PdTabCount; tab++)
                {
                    List<string> slots = _pdSlots[tab];
                    for (int i = 0; i < slots.Count; i++)
                    {
                        string oldPath = slots[i];
                        string newPath = RebaseDockPresetPath(oldPath,
                            source, destination, directory);
                        if (oldPath == newPath) continue;
                        slots[i] = newPath;
                        Texture2D old;
                        if (_pdThumbs.TryGetValue(oldPath, out old))
                        {
                            _pdThumbs.Remove(oldPath);
                            if (old != null) UnityEngine.Object.Destroy(old);
                        }
                        _pdThumbStamp.Remove(oldPath);
                        changed = true;
                    }
                }
                if (!changed) return;
                _pdOverlayPath = RebaseDockPresetPath(_pdOverlayPath,
                    source, destination, directory);
                foreach (MakeupApplied applied in _makeupApplied.Values)
                    applied.Path = RebaseDockPresetPath(applied.Path,
                        source, destination, directory);
                ClearPdReorder();
                if (_pdPersonOverlay != null)
                    _pdPersonOverlay.SetActive(false);
                SavePdSlots();
                _pdDirty = true;
                // Rebind live cells before old textures are destroyed at
                // frame end; do not leave visible cells pointing at them.
                if (_pdCells != null)
                {
                    RebuildPdCells();
                    _pdDirty = false;
                }
                Log("预设收藏引用已同步移动/重命名。");
            }
            catch (Exception e) { Error(e); }
        }

        private static string RebaseDockPresetPath(string path,
            string source, string destination, bool directory)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string current = NormalizeDockLocalPath(path);
            string from = NormalizeDockLocalPath(source);
            string to = NormalizeDockLocalPath(destination);
            if (current == null || from == null || to == null) return path;
            if (string.Equals(current, from, StringComparison.OrdinalIgnoreCase))
                return to;
            if (directory && current.StartsWith(from + "/",
                    StringComparison.OrdinalIgnoreCase))
                return to + current.Substring(from.Length);
            return path;
        }

        private static string NormalizeDockLocalPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            path = path.Replace('\\', '/');
            // Package addresses are not local files and must not be guessed.
            if (path.IndexOf(":/", StringComparison.Ordinal) > 1) return null;
            string root = Path.GetFullPath(Paths.GameRootPath)
                .Replace('\\', '/').TrimEnd('/');
            string full = Path.GetFullPath(Path.IsPathRooted(path)
                ? path : Path.Combine(Paths.GameRootPath, path))
                .Replace('\\', '/').TrimEnd('/');
            return full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length + 1) : full;
        }

        private static void EnsurePdSlots()
        {
            if (_pdLoaded) return;
            _pdLoaded = true;
            for (int i = 0; i < PdTabCount; i++)
                _pdSlots[i] = new List<string>();
            try
            {
                string file = PdSlotsPath;
                if (!File.Exists(file)) return;
                string[] lines = File.ReadAllLines(file);
                // "#v2" header = current five-tab layout. Anything else is
                // the legacy six-tab file — merge 替换+外观 into 人物 and
                // shift the rest down one index.
                bool v2 = lines.Length > 0 && lines[0].Trim() == "#v2";
                bool migrated = false;
                foreach (string line in lines)
                {
                    int sep = line.IndexOf('|');
                    if (sep <= 0) continue;
                    int tab;
                    if (!int.TryParse(line.Substring(0, sep), out tab))
                        continue;
                    if (!v2)
                    {
                        if (tab < 0 || tab > 5) continue;
                        tab = tab <= 1 ? 0 : tab - 1;
                        migrated = true;
                    }
                    if (tab < 0 || tab >= PdTabCount) continue;
                    string path = line.Substring(sep + 1).Trim();
                    if (path.Length > 0 && !_pdSlots[tab].Contains(path))
                        _pdSlots[tab].Add(path);
                }
                if (migrated) SavePdSlots();
            }
            catch (Exception e) { Error(e); }
        }

        private static void SavePdSlots()
        {
            try
            {
                List<string> lines = new List<string> { "#v2" };
                for (int i = 0; i < PdTabCount; i++)
                {
                    if (_pdSlots[i] == null) continue;
                    foreach (string path in _pdSlots[i])
                        lines.Add(i + "|" + path);
                }
                File.WriteAllLines(PdSlotsPath, lines.ToArray());
            }
            catch (Exception e) { Error(e); }
        }

        // ---------- live reorder (phone-icon style) ----------
        // The drag-state, grid math and page-flip plumbing is shared with
        // the favorites bar — see UiAssistFavoriteReorder.cs (BeginPdReorder,
        // TickPdReorder, PdDropIndex, CommitPdReorder, PdDisplaySlots).
        // RebuildPdCells renders PdDisplaySlots() so the previewed order is
        // what shows while a slot is dragged.

        private static bool RemoveDockSlot(int tab, string path)
        {
            if (tab < 0 || tab >= PdTabCount) return false;
            List<string> slots = _pdSlots[tab];
            if (slots == null || !slots.Remove(path)) return false;
            SavePdSlots();
            _pdDirty = true;
            return true;
        }

        // ---------- preset acceptance + application ----------

        // A tab files a preset only when its section content matches; any
        // other preset type is skipped without a peep.
        private static bool DockAccepts(int tab, string path)
        {
            int kind = SceneQuickActions.ClassifyPresetFile(path, "character");
            switch (tab)
            {
                case 0: // 人物: person preset or an appearance-only preset
                    return kind >= 1;
                case 1: // 发型: person preset or a hair preset
                    return kind == 2 ||
                        SceneQuickActions.ClassifyPresetFile(path, "hair") == 1;
                case 2: // 服装: person preset or a clothing preset
                    return kind == 2 ||
                        SceneQuickActions.ClassifyPresetFile(
                            path, "clothing") == 1;
                case 3: // 皮肤: person preset or anything carrying skin data
                    return kind == 2 || PresetHasSkinStorable(path);
                case 4: // 化妆: clothing presets only — person presets
                        // must not land here even though they carry a
                        // clothing section
                    return SceneQuickActions.ClassifyPresetFile(
                            path, "clothing") == 1;
            }
            return false;
        }

        private static bool PresetHasSkinStorable(string path)
        {
            try
            {
                JSONNode root = JSON.Parse(File.ReadAllText(
                    FileManager.GetFullPath(path)));
                if (root == null) return false;
                JSONClass cls = root.AsObject;
                if (cls == null) return false;
                JSONArray storables = cls["storables"].AsArray;
                if (storables == null) return false;
                for (int i = 0; i < storables.Count; i++)
                {
                    string id = storables[i]["id"];
                    if (id == "skin" || id == "skin2" ||
                        id == "skinTextureStore" ||
                        id == "textures" || id == "textures2")
                        return true;
                }
            }
            catch { }
            return false;
        }

        // Files a preset under `tab`; returns true when it was actually
        // added (accepted type and not a duplicate). Callers use _pdTab for
        // browser picks and the drop target tab for cross-tab drags.
        private static bool FileDockPreset(int tab, string path)
        {
            EnsurePdSlots();
            if (tab < 0 || tab >= PdTabCount) return false;
            if (!DockAccepts(tab, path)) return false;   // silent by design
            List<string> slots = _pdSlots[tab];
            if (slots.Contains(path)) return false;
            slots.Add(path);
            SavePdSlots();
            if (tab == _pdTab)
            {
                // Scroll to the bottom so the user sees the new slot land.
                _pdScrollY = float.MaxValue;
            }
            _pdDirty = true;
            return true;
        }

        private static void ApplyDockSlot(string path, Atom target = null)
        {
            target = target ?? _pdAtom;
            if (target == null) return;
            if (_pdQuick == null)
                _pdQuick = new SceneQuickActions(Quest3TriggerUIPlugin.Instance);
            try
            {
                switch (_pdTab)
                {
                    case 0: // 人物 via the 读取 browser — its header
                            // dropdown (VrPresetBrowser.PersonApplyMode)
                            // picks 替换 (full person incl. clothing) or
                            // 外观 (appearance only, keeps clothing).
                    {
                        JSONStorable ap =
                            target.GetStorableByID("AppearancePresets");
                        if (ap != null)
                        {
                            if (VrPresetBrowser.PersonApplyMode == 0)
                                _pdQuick.LoadFullAppearancePreset(
                                    target, ap, path);
                            else
                                _pdQuick.LoadAppearanceWithoutClothing(
                                    target, ap, path);
                        }
                        break;
                    }
                    case 1: // 发型
                        _pdQuick.LoadExtractedPreset(target, path,
                            "HairPresets", "Hair preset",
                            delegate(JSONClass src)
                            {
                                return SceneQuickActions.ExtractSectionPreset(
                                    src, "hair");
                            }, true);
                        break;
                    case 2: // 服装 (replaces, never merges)
                        _pdQuick.LoadExtractedPreset(target, path,
                            "ClothingPresets", "Clothing preset",
                            delegate(JSONClass src)
                            {
                                return SceneQuickActions.ExtractSectionPreset(
                                    src, "clothing");
                            });
                        break;
                    case 4: // 化妆: merges — swaps only the preset's own
                            // makeup items; clicking the applied preset
                            // again removes exactly those items
                    {
                        MakeupApplied state;
                        _makeupApplied.TryGetValue(target.uid, out state);
                        bool same = state != null && state.Path == path;
                        List<string> remove = state == null
                            ? null : state.ItemIds;
                        bool add = !same;
                        var applied = new List<string>();
                        bool built = false;
                        _pdQuick.LoadExtractedPreset(target, path,
                            "ClothingPresets", "化妆",
                            delegate(JSONClass src)
                            {
                                JSONClass merged = SceneQuickActions
                                    .BuildMakeupClothingPreset(src, target,
                                        remove, add, applied);
                                built = merged != null;
                                return merged;
                            });
                        if (built)
                        {
                            if (add)
                            {
                                if (state == null)
                                    state = new MakeupApplied();
                                state.Path = path;
                                state.ItemIds = applied;
                                _makeupApplied[target.uid] = state;
                            }
                            else _makeupApplied.Remove(target.uid);
                        }
                        break;
                    }
                    case 3: // 皮肤
                        _pdQuick.LoadSkinPreset(target, path);
                        break;
                }
                VrHaptics.Press();
            }
            catch (Exception e) { Error(e); }
        }
    }
}
