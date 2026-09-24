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
        // (the five fixed tabs 替换/外观/发型/服装/皮肤 plus a ＋新增 row),
        // a two-column cell grid, and a bottom nav row that only appears when
        // the tab overflows one page. A slot click applies only the matching
        // section of a person preset — the same section loaders the radial
        // menu uses — so a slot never nukes the rest of the person. The 新增
        // row opens the preset browser in pick mode: every .vap click files
        // it under the active tab without closing (batch saving); presets
        // whose type does not match the tab are skipped silently. Dragging
        // a slot thumbnail out of the dock removes it.
        private static readonly string[] PdTabNames =
            { "替换", "外观", "发型", "服装", "皮肤" };
        // Per-tab browse root for 新增 — same mapping as the radial
        // 人物 sub-buttons: 替换/外观 share the person-preset library,
        // 发型/服装/皮肤 open their own manager dirs.
        private static string PdTabDir(int tab)
        {
            switch (tab)
            {
                case 2: return PluginPaths.HairPresetDir;
                case 3: return PluginPaths.ClothingPresetDir;
                case 4: return PluginPaths.SkinPresetDir;
                default: return PluginPaths.AppearancePresetDir;
            }
        }
        private const int PdTabCount = 5;
        private const float PdStripW = 96f;
        private const float PdNameH = 18f;

        private static RectTransform _pdDock, _pdCells, _pdTabStrip;
        private static Canvas _pdCanvas;
        private static GameObject _pdNav;
        private static Text _pdPageText;
        private static readonly Image[] _pdTabBgs = new Image[PdTabCount];
        private static readonly RectTransform[] _pdTabRects =
            new RectTransform[PdTabCount];
        // While the dock's own pick browser is open the dock must survive —
        // _presetBrowsing would otherwise clear every side bar.
        private static bool _pdPicking;
        private static readonly List<string>[] _pdSlots =
            new List<string>[PdTabCount];
        private static int _pdTab, _pdPage, _pdPages;
        private static bool _pdDirty = true, _pdLoaded, _pdPositionLogged;
        private static float _pdListHeight;
        private static GameObject _pdList;
        private static Atom _pdAtom;
        private static SceneQuickActions _pdQuick;
        private static readonly Dictionary<string, Texture2D> _pdThumbs =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

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
        }

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
                GameObject cellsGo = new GameObject("Cells",
                    typeof(RectTransform));
                _pdCells = (RectTransform)cellsGo.transform;
                _pdCells.SetParent(_pdDock, false);
                _pdCells.anchorMin = new Vector2(0f, 1f);
                _pdCells.anchorMax = new Vector2(0f, 1f);
                _pdCells.pivot = new Vector2(0f, 1f);
                _pdCells.anchoredPosition = new Vector2(
                    FavTagGap + PdStripW, -FavPad);
                GridLayoutGroup grid = cellsGo.AddComponent<GridLayoutGroup>();
                grid.cellSize = new Vector2(FavCellW, FavCellH);
                grid.spacing = new Vector2(FavSpacing, FavSpacing);
                grid.padding = new RectOffset((int)FavPad, (int)FavPad,
                    0, (int)FavPad);
                grid.childAlignment = TextAnchor.UpperCenter;
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                grid.constraintCount = 2;
                cellsGo.AddComponent<PdDockTag>();
                CreatePdNav();

                SuperController.singleton.AddCanvas(_pdCanvas);
                _pdDirty = true;
            }
            catch (Exception e) { ClearPresetDock(); Error(e); }
        }

        private static void ClearPresetDock()
        {
            ClearPdReorder();
            _pdPageTargets.Clear();
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
            _pdNav = null;
            _pdPageText = null;
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
            // this dock), and seeing slots land live is the point. Picking
            // mode also ignores MainHUDVisible: the browser deactivates the
            // main HUD branch that would otherwise flip every side bar off.
            bool visible = sc != null &&
                (_pdPicking ||
                 (_pdList.activeInHierarchy && sc.MainHUDVisible));
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
            if (_pdDirty)
            {
                _pdDirty = false;
                RebuildPdCells();
            }
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
            GameObject row = new GameObject("PdAdd", typeof(RectTransform));
            RectTransform rect = (RectTransform)row.transform;
            rect.SetParent(_pdTabStrip, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(PdStripW, FavTagRowH);
            Image bg = row.AddComponent<Image>();
            bg.color = new Color(0.13f, 0.30f, 0.20f, 1f);
            bg.raycastTarget = true;
            Button button = row.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                // Dropping a dragged slot on this row must not pop the
                // browser on top of the drop commit.
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                VrHaptics.Press();
                OpenPresetDockPicker();
            });
            Text text = new GameObject("Label", typeof(RectTransform))
                .AddComponent<Text>();
            RectTransform tr = (RectTransform)text.transform;
            tr.SetParent(rect, false);
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            text.text = "＋ 新增";
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = 14;
            text.color = Color.white;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.raycastTarget = false;
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
        // (tracks mid-drag), look target as the fallback.
        private static int PresetDockTabUnderPointer()
        {
            Vector2 local;
            for (int i = 0; i < PdTabCount; i++)
            {
                RectTransform row = _pdTabRects[i];
                if (row != null && PointerOnRect(row, out local))
                    return i;
            }
            GameObject t = VrPointerPresentation.CurrentLookTarget(true);
            PdTabButtonTag tag = t != null
                ? t.GetComponentInParent<PdTabButtonTag>() : null;
            if (tag == null)
            {
                t = VrPointerPresentation.CurrentLookTarget(false);
                tag = t != null
                    ? t.GetComponentInParent<PdTabButtonTag>() : null;
            }
            return tag != null ? tag.Index : -1;
        }

        private static bool PointerOverPresetDock()
        {
            GameObject t = VrPointerPresentation.CurrentLookTarget(true);
            if (t != null && t.GetComponentInParent<PdDockTag>() != null)
                return true;
            t = VrPointerPresentation.CurrentLookTarget(false);
            if (t != null && t.GetComponentInParent<PdDockTag>() != null)
                return true;
            // Preview rebuilds replace slot objects — hit the persistent
            // dock plane so a stale look target cannot turn a reorder into
            // a removal (same fallback as the favorites bar).
            Vector2 p;
            return PointerOnRect(_pdDock, out p);
        }

        private static void PdSelectTab(int idx)
        {
            if (idx == _pdTab) return;
            _pdTab = idx;
            _pdPage = 0;
            PdPaintTabs();
            _pdDirty = true;
        }

        // Bottom ◀ n/m ▶ row centered under the cells column, only visible
        // when the tab overflows one page — same as the favorites bar's
        // FavNav.
        private static void CreatePdNav()
        {
            GameObject nav = new GameObject("PdNav", typeof(RectTransform));
            RectTransform navRect = (RectTransform)nav.transform;
            navRect.SetParent(_pdDock, false);
            navRect.anchorMin = new Vector2(1f, 0f);
            navRect.anchorMax = new Vector2(1f, 0f);
            navRect.pivot = new Vector2(0.5f, 0f);
            navRect.anchoredPosition = new Vector2(-FavColW * 0.5f, 4f);
            navRect.sizeDelta = new Vector2(190f, FavNavH);
            nav.AddComponent<PdDockTag>();

            CreatePdNavButton(navRect, "◀", -72f, -1,
                delegate { PdPageStep(-1); });
            CreatePdNavButton(navRect, "▶", 72f, 1,
                delegate { PdPageStep(1); });

            GameObject textGo = new GameObject("Page", typeof(RectTransform));
            RectTransform textRect = (RectTransform)textGo.transform;
            textRect.SetParent(navRect, false);
            textRect.anchorMin = new Vector2(0.5f, 0.5f);
            textRect.anchorMax = new Vector2(0.5f, 0.5f);
            textRect.anchoredPosition = Vector2.zero;
            textRect.sizeDelta = new Vector2(90f, FavNavH);
            _pdPageText = textGo.AddComponent<Text>();
            _pdPageText.alignment = TextAnchor.MiddleCenter;
            _pdPageText.fontSize = 20;
            _pdPageText.color = new Color(1f, 1f, 1f, 0.8f);
            _pdPageText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _pdPageText.raycastTarget = false;
            _pdPageText.text = "1/1";

            _pdNav = nav;
            nav.SetActive(false);
        }

        private static void CreatePdNavButton(
            RectTransform parent, string label, float x, int dir,
            UnityEngine.Events.UnityAction action)
        {
            GameObject go = new GameObject("Nav " + label,
                typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(x, 0f);
            rect.sizeDelta = new Vector2(46f, FavNavH - 4f);
            FavoritePageTarget target = go.AddComponent<FavoritePageTarget>();
            target.Direction = dir;
            _pdPageTargets.Add(target);
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0.11f, 0.38f, 0.48f, 1f);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = bg;
            button.onClick.AddListener(delegate
            {
                // A drag release landing on the button must not also fire
                // a page step on top of the drop commit.
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
            text.fontSize = 20;
            text.color = Color.white;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.raycastTarget = false;
        }

        private static void PdPageStep(int delta)
        {
            if (_pdPages <= 1) return;
            int page = Mathf.Clamp(_pdPage + delta, 0, _pdPages - 1);
            if (page == _pdPage) return;
            _pdPage = page;
            _pdDirty = true;
        }

        // Same capacity formula as the favorites bar: the nav row sits below
        // the cells (the dock grows one nav-row taller when paging) instead
        // of stealing a cell row.
        private static int PdPageCapacity
        {
            get
            {
                float h = _pdListHeight > 0f ? _pdListHeight : 400f;
                return Mathf.Max(1, Mathf.FloorToInt(
                    (h - FavPad * 2f + FavSpacing) /
                    (FavCellH + FavSpacing))) * 2;
            }
        }

        private static void RebuildPdCells()
        {
            if (_pdCells == null) return;
            EnsurePdSlots();
            foreach (Transform child in _pdCells)
                UnityEngine.Object.Destroy(child.gameObject);
            List<string> slots = PdDisplaySlots();
            int count = slots.Count;
            int capacity = PdPageCapacity;
            bool nav = count > capacity;
            _pdPages = Mathf.Max(1,
                Mathf.CeilToInt(count / (float)capacity));
            _pdPage = Mathf.Clamp(_pdPage, 0, _pdPages - 1);
            int start = _pdPage * capacity;
            int end = Mathf.Min(count, start + capacity);
            for (int i = start; i < end; i++)
                CreatePdSlot(slots[i]);
            if (count == 0)
                CreatePdHintSlot();
            if (_pdNav != null && _pdNav.activeSelf != nav)
                _pdNav.SetActive(nav);
            if (nav && _pdPageText != null)
                _pdPageText.text = (_pdPage + 1) + "/" + _pdPages;
            int rowsShown = Mathf.Max(1,
                Mathf.CeilToInt(Mathf.Max(1, end - start) / 2f));
            float cellsH = FavPad * 2f + rowsShown *
                (FavCellH + FavSpacing) - FavSpacing;
            _pdCells.sizeDelta = new Vector2(FavColW, cellsH);
            // Dock: [left-edge tab strip][cells] horizontally, cells + nav row
            // vertically — the exact mirror of the favorites bar.
            float stripNeed = _pdTabStrip == null ? 0f
                : _pdTabStrip.sizeDelta.y + FavPad * 2f;
            _pdDock.sizeDelta = new Vector2(FavTagGap + PdStripW + FavColW,
                Mathf.Max(64f, Mathf.Max(
                    cellsH + (nav ? FavNavH + 4f : 0f), stripNeed)));
        }

        // Empty-tab placeholder, mirroring the favorites bar's FavHint cell.
        private static void CreatePdHintSlot()
        {
            GameObject slot = new GameObject("PdHint", typeof(RectTransform));
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
            tr.offsetMax = new Vector2(0f, -PdNameH);
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
            Button button = cell.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            string captured = path;
            button.onClick.AddListener(delegate
            {
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                ApplyDockSlot(captured);
            });
            ApplyPdThumb(tag);
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

        private static void ApplyPdThumb(PdSlotTag tag)
        {
            Texture2D tex;
            if (!_pdThumbs.TryGetValue(tag.Path, out tex))
            {
                tex = null;
                try
                {
                    string jpg = tag.Path.Substring(0, tag.Path.Length -
                        Path.GetExtension(tag.Path).Length) + ".jpg";
                    string full = FileManager.GetFullPath(jpg);
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
                foreach (string line in File.ReadAllLines(file))
                {
                    int sep = line.IndexOf('|');
                    if (sep <= 0) continue;
                    int tab;
                    if (!int.TryParse(line.Substring(0, sep), out tab) ||
                        tab < 0 || tab >= PdTabCount) continue;
                    string path = line.Substring(sep + 1).Trim();
                    if (path.Length > 0 && !_pdSlots[tab].Contains(path))
                        _pdSlots[tab].Add(path);
                }
            }
            catch (Exception e) { Error(e); }
        }

        private static void SavePdSlots()
        {
            try
            {
                List<string> lines = new List<string>();
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
                case 0: // 替换: whole-person replacement needs a person preset
                    return kind == 2;
                case 1: // 外观: person preset or an appearance-only preset
                    return kind >= 1;
                case 2: // 发型: person preset or a hair preset
                    return kind == 2 ||
                        SceneQuickActions.ClassifyPresetFile(path, "hair") == 1;
                case 3: // 服装: person preset or a clothing preset
                    return kind == 2 ||
                        SceneQuickActions.ClassifyPresetFile(
                            path, "clothing") == 1;
                case 4: // 皮肤: person preset or anything carrying skin data
                    return kind == 2 || PresetHasSkinStorable(path);
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
                // Jump to the page holding the new slot so the user sees
                // it land.
                _pdPage = Mathf.Max(0,
                    Mathf.CeilToInt(slots.Count / (float)PdPageCapacity) - 1);
            }
            _pdDirty = true;
            return true;
        }

        private static void ApplyDockSlot(string path)
        {
            Atom target = _pdAtom;
            if (target == null) return;
            if (_pdQuick == null)
                _pdQuick = new SceneQuickActions(Quest3TriggerUIPlugin.Instance);
            try
            {
                switch (_pdTab)
                {
                    case 0: // 替换: full person preset, clothing+hair included
                    {
                        JSONStorable ap =
                            target.GetStorableByID("AppearancePresets");
                        if (ap != null)
                            _pdQuick.LoadFullAppearancePreset(target, ap, path);
                        break;
                    }
                    case 1: // 外观: same preset minus the clothing section
                    {
                        JSONStorable ap =
                            target.GetStorableByID("AppearancePresets");
                        if (ap != null)
                            _pdQuick.LoadAppearanceWithoutClothing(
                                target, ap, path);
                        break;
                    }
                    case 2: // 发型
                        _pdQuick.LoadExtractedPreset(target, path,
                            "HairPresets", "Hair preset",
                            delegate(JSONClass src)
                            {
                                return SceneQuickActions.ExtractSectionPreset(
                                    src, "hair");
                            }, true);
                        break;
                    case 3: // 服装 (replaces, never merges)
                        _pdQuick.LoadExtractedPreset(target, path,
                            "ClothingPresets", "Clothing preset",
                            delegate(JSONClass src)
                            {
                                return SceneQuickActions.ExtractSectionPreset(
                                    src, "clothing");
                            });
                        break;
                    case 4: // 皮肤
                        _pdQuick.LoadExtractedPreset(target, path,
                            "AppearancePresets", "Skin preset",
                            SceneQuickActions.ExtractSkinPreset);
                        break;
                }
                VrHaptics.Press();
            }
            catch (Exception e) { Error(e); }
        }
    }
}
