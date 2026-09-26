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
        // Which dock function owns the open browser session — tab clicks
        // re-open that same function pointed at the new tab's directory.
        private static int _pdBrowseKind; // 0 none, 1 新增, 2 读取, 3 保存
        private static readonly List<string>[] _pdSlots =
            new List<string>[PdTabCount];
        private static int _pdTab;
        private static bool _pdDirty = true, _pdLoaded, _pdPositionLogged;
        private static float _pdListHeight;
        private static GameObject _pdList;
        private static Atom _pdAtom;
        // Load-target row: null = auto (nearest woman to the view); a
        // manual pick sticks until that atom leaves the scene. Only
        // female persons are ever offered or applied to.
        private static Atom _pdLoadTarget;
        private static Text _pdTargetLabel;
        private static float _pdTargetRefresh;
        private static GameObject _pdTargetPopup;
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
            // The name strip's Image — raycastTarget flips on in save mode
            // so its Button wins clicks over the cell's own Button.
            internal Image NameHit;
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
                long shellT = Mark();
                EnsurePdSlots();
                long tSlots = ElapsedMs(shellT); shellT = Mark();
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
                long tGo = ElapsedMs(shellT); shellT = Mark();

                // Cells hug the dock's right edge (the side facing the ACE
                // list); the tab strip takes the outer left edge — the
                // mirror image of the favorites bar's [cells|tags] layout.
                CreatePdTabStrip();
                long tStrip = ElapsedMs(shellT); shellT = Mark();
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

                // Final footprint up front: TickPresetDock anchors the dock
                // off the list edge using rect.width — during the incremental
                // cell fill a still-default sizeDelta would slide the dock
                // halfway into the editor panel.
                _pdDock.sizeDelta = new Vector2(
                    FavTagGap + PdStripW + FavColW,
                    Mathf.Max(64f, Mathf.Max(PdGridH,
                        _pdTabStrip.sizeDelta.y + FavPad * 2f)));
                long tView = ElapsedMs(shellT); shellT = Mark();

                SuperController.singleton.AddCanvas(_pdCanvas);
                _pdDirty = true;
                Log("[ACE] dock shell: slots=" + tSlots + " go=" + tGo +
                    " strip=" + tStrip + " view=" + tView +
                    " addCanvas=" + ElapsedMs(shellT) + "ms");
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
            _pdSaveBrowsing = false;
            _pdBrowseKind = 0;
            _pdSaveOverlay = null;
            _pdSaveTag = null;
            _pdRenameOverlay = null;
            _pdRenameInput = null;
            _pdRenamePath = null;
            _pdScrollY = 0f;
            _pdContentH = 0f;
            _pdBuildSlots = null;
            _pdBuildIdx = 0;
            _pdBuildThumbs = false;
            _pdView = null;
            _pdScrollTrack = null;
            _pdScrollThumb = null;
            _pdAtom = null;
            _pdLoadTarget = null;
            _pdTargetLabel = null;
            _pdTargetRefresh = 0f;
            _pdTargetPopup = null;
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
            PresetPreheat.Label = null;
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
            _pdHzTag = null;
            _pdHzPanel = null;
            _pdHzImage = null;
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
            if (_pdDirty || _pdPreviewDirty || _pdBuildSlots != null)
            {
                // Consume the dirty edge instead of latching it as the
                // restart arg — otherwise every frame restarts the pump and
                // the fill never gets past the first batch.
                bool restart = _pdDirty || _pdPreviewDirty;
                if (restart)
                {
                    _pdBuildThumbs = _pdDirty;
                    _pdDirty = _pdPreviewDirty = false;
                }
                if (PumpPdCells(PdBuildPerTick, restart))
                    _pdBuildThumbs = false;
            }
            if (visible) TickPdThumbnails();
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
            TickDockScroll(false);
            TickPdHoverZoom();
            // Target label tracks the auto pick while unconfirmed; manual
            // picks keep their name until that atom leaves the scene.
            if (_pdTargetLabel != null &&
                Time.unscaledTime >= _pdTargetRefresh)
            {
                _pdTargetRefresh = Time.unscaledTime + 0.5f;
                Atom t = PdEffectiveTarget();
                _pdTargetLabel.text = t != null ? t.name : "无女性角色";
            }
        }

        // ---------- hover zoom: 1s rest on a thumbnail shows it 3x ----------

        private const float PdHoverZoomSeconds = 1f;
        private const float PdHoverZoomScale = 3f;
        private static PdSlotTag _pdHzTag;
        private static float _pdHzSince;
        private static RectTransform _pdHzPanel;
        private static RawImage _pdHzImage;

        private static PdSlotTag HoveredPdSlot()
        {
            for (int h = 0; h < 2; h++)
            {
                GameObject look =
                    VrPointerPresentation.CurrentLookTarget(h == 0);
                if (look == null) continue;
                // The 替换/外观 overlay floats above the slot — hits on it
                // belong to the mini buttons, not to hover-zoom.
                if (look.GetComponentInParent<PdOverlayTag>() != null)
                    return null;
                PdSlotTag tag =
                    look.GetComponentInParent<PdSlotTag>();
                if (tag != null && tag.Thumb != null &&
                    tag.Thumb.texture != null &&
                    tag.gameObject.activeInHierarchy)
                    return tag;
            }
            return null;
        }

        private static void TickPdHoverZoom()
        {
            bool busy =
                Quest3TriggerUIPlugin.ClothingDragCandidate != null ||
                Quest3TriggerUIPlugin.ClothingDragActive ||
                _favoriteDragList != null || _pdDragList != null ||
                _dockDeleteMode || _pdSaveBrowsing;
            PdSlotTag hit = busy ? null : HoveredPdSlot();
            if (hit != _pdHzTag)
            {
                SetPdZoom(false);
                _pdHzTag = hit;
                _pdHzSince = Time.unscaledTime;
                if (hit != null)
                    Log("Q3 pdzoom: hovering " + hit.Path);
            }
            // A destroyed tag compares equal to null in Unity — the early
            // return must still hide the panel or it floats loose forever.
            if (_pdHzTag == null)
            {
                SetPdZoom(false);
                return;
            }
            // The slot can be destroyed mid-hover by a rebuild, or masked out
            // by a scroll — both must drop the overlay instead of leaving a
            // floating copy where the cell used to be.
            if (_pdHzTag.Thumb == null)
            {
                _pdHzTag = null;
                SetPdZoom(false);
                return;
            }
            if (_pdHzPanel != null && _pdHzPanel.gameObject.activeSelf)
            {
                SyncPdZoom(_pdHzTag);
                return;
            }
            if (Time.unscaledTime - _pdHzSince >= PdHoverZoomSeconds)
            {
                Log("Q3 pdzoom: apply " + _pdHzTag.Path);
                ApplyPdZoom(_pdHzTag);
            }
        }

        private static void EnsurePdZoomPanel()
        {
            if (_pdHzPanel != null || _pdDock == null) return;
            GameObject go = new GameObject("PdHoverZoom",
                typeof(RectTransform));
            _pdHzPanel = (RectTransform)go.transform;
            _pdHzPanel.SetParent(_pdDock, false);
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.85f);
            bg.raycastTarget = false;
            GameObject imgGo = new GameObject("Thumb",
                typeof(RectTransform));
            RectTransform imgRect = (RectTransform)imgGo.transform;
            imgRect.SetParent(go.transform, false);
            imgRect.anchorMin = Vector2.zero;
            imgRect.anchorMax = Vector2.one;
            imgRect.offsetMin = new Vector2(4f, 4f);
            imgRect.offsetMax = new Vector2(-4f, -4f);
            _pdHzImage = imgGo.AddComponent<RawImage>();
            _pdHzImage.raycastTarget = false;
            go.SetActive(false);
        }

        private static void ApplyPdZoom(PdSlotTag tag)
        {
            EnsurePdZoomPanel();
            if (_pdHzPanel == null || tag.Thumb == null ||
                tag.Thumb.texture == null)
                return;
            _pdHzImage.texture = tag.Thumb.texture;
            _pdHzPanel.gameObject.SetActive(true);
            _pdHzPanel.SetAsLastSibling();
            SyncPdZoom(tag);
        }

        private static void SyncPdZoom(PdSlotTag tag)
        {
            RectTransform cell = tag.transform as RectTransform;
            if (cell == null || _pdDock == null)
            {
                SetPdZoom(false);
                return;
            }
            // A cell scrolled outside the viewport is masked but still sits at
            // a world position — the zoom must hide with it, not float loose.
            Vector3 cellCenter = _pdView != null
                ? _pdView.InverseTransformPoint(
                    cell.TransformPoint(cell.rect.center))
                : Vector3.zero;
            if (_pdView == null || !_pdView.rect.Contains(cellCenter))
            {
                SetPdZoom(false);
                return;
            }
            Vector3 local = _pdDock.InverseTransformPoint(
                cell.TransformPoint(cell.rect.center));
            _pdHzPanel.localPosition = new Vector3(local.x, local.y, 0f);
            _pdHzPanel.sizeDelta =
                cell.rect.size * PdHoverZoomScale;
        }

        private static void SetPdZoom(bool on)
        {
            if (_pdHzPanel != null)
                _pdHzPanel.gameObject.SetActive(on);
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
            // Load-target row: shows the female person presets apply to —
            // auto-tracks the nearest woman in view until the user picks
            // one manually via the popup.
            GameObject targetRow = CreatePdIoRow("对象…", y,
                new Color(0.20f, 0.20f, 0.35f, 1f), TogglePdTargetPopup);
            _pdTargetLabel = targetRow.GetComponentInChildren<Text>();
            if (_pdTargetLabel != null) _pdTargetLabel.fontSize = 12;
            y += FavTagRowH + FavTagGap;
            // 读取/保存: the radial 人物 sub-actions re-homed into the dock —
            // whichever tab is selected decides which preset section they
            // mean.
            CreatePdIoRow("读 取", y, new Color(0.13f, 0.24f, 0.32f, 1f),
                OpenDockPresetLoader);
            y += FavTagRowH + FavTagGap;
            // 保存 opens the save browser as before — while it (or any
            // dock-initiated browser session) is up, thumbnail clicks
            // overwrite-save and name-strip clicks rename; no mode toggle.
            CreatePdIoRow("保 存", y, new Color(0.30f, 0.22f, 0.12f, 1f),
                OpenDockPresetSaver);
            y += FavTagRowH + FavTagGap;
            GameObject deleteRow = CreatePdIoRow("删 除", y,
                new Color(0.28f, 0.15f, 0.15f, 1f), ToggleDockDeleteMode);
            _dockDeleteButton = deleteRow.GetComponent<Image>();
            _dockDeleteLabel = deleteRow.GetComponentInChildren<Text>();
            PaintDockDeleteMode();
            y += FavTagRowH + FavTagGap;
            // 预热: generate .vamcache disk files for every texture the
            // 人物 tab's presets reference, so a cold preset stops paying
            // the decode+compress cost on first load. Already-cached
            // presets are skipped.
            GameObject preheatRow = CreatePdIoRow("预 热", y,
                new Color(0.16f, 0.30f, 0.30f, 1f), TogglePresetPreheat);
            PresetPreheat.Label = preheatRow.GetComponentInChildren<Text>();
            y += FavTagRowH;
            _pdTabStrip.sizeDelta = new Vector2(PdStripW, y);
            PdPaintTabs();
        }

        private static void TogglePresetPreheat()
        {
            EnsurePdSlots();
            PresetPreheat.Toggle(_pdSlots[0]);
        }

        // cfg-triggered (one-shot flag) preheat uses the same entry point.
        internal static List<string> PersonPresetPaths()
        {
            EnsurePdSlots();
            return _pdSlots[0];
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
            // Wipe the old tab's cells immediately — the new tab fills a
            // blank panel instead of squeezing new thumbnails over old ones.
            ClearPdCellsNow();
            PdPaintTabs();
            _pdDirty = true;
            // A dock-initiated browser session follows the tab: same
            // function (新增/读取/保存), retargeted to the tab's directory.
            if (_pdPicking && VrPresetBrowser.IsOpen)
            {
                try
                {
                    switch (_pdBrowseKind)
                    {
                        case 1: OpenPresetDockPicker(); break;
                        case 2: OpenDockPresetLoader(); break;
                        case 3: OpenDockPresetSaver(); break;
                    }
                    VrPresetBrowser.NavigateIfOpen(PdTabDir(_pdTab));
                }
                catch (Exception e) { Error(e); }
            }
        }

        // Incremental build state: the first paint after the ACE editor opens
        // used to create ~90 cells (each ~5 GameObjects) in one frame — a
        // multi-hundred-ms spike on top of UIAssist's own open cost. The pump
        // below spreads creation over ticks; path-keyed reuse still applies.
        private static List<string> _pdBuildSlots;
        private static int _pdBuildIdx;
        private const int PdBuildPerTick = 12;
        private static int _pdBuildTicks;
        private static bool _pdBuildThumbs;

        // Full synchronous rebuild — used by the move/rename rebind path,
        // which must finish before stale thumbnails die at frame end.
        private static void RebuildPdCells()
        {
            _pdBuildThumbs = _pdDirty;
            PumpPdCells(int.MaxValue, true);
            _pdBuildThumbs = false;
            _pdDirty = false;
            _pdPreviewDirty = false;
        }

        private static bool PumpPdCells(int budget, bool restart)
        {
            if (restart) { _pdBuildSlots = null; _pdBuildTicks = 0; }
            if (_pdCells == null)
            {
                _pdBuildSlots = null;
                return true;
            }
            EnsurePdSlots();
            if (_pdBuildSlots == null)
            {
                _pdKeptCells.Clear();
                _pdCellPosition = 0;
                _pdBuildSlots = PdDisplaySlots();
                _pdBuildIdx = 0;
            }
            _pdBuildTicks++;
            int count = _pdBuildSlots.Count;
            // All entries get a cell — the viewport mask + scroll offset do
            // the slicing now, and path-keyed reuse keeps rebuilds cheap.
            // ~2ms per tick is the real budget (a loaded heap makes cell
            // cost vary wildly); the count cap is just a sanity bound.
            long tickStart = Mark();
            while (_pdBuildIdx < count && budget-- > 0)
            {
                CreatePdSlot(_pdBuildSlots[_pdBuildIdx++]);
                if (_pdBuildIdx < count && ElapsedMs(tickStart) >= 2)
                    break;
            }
            if (_pdBuildIdx < count) return false;
            _pdBuildSlots = null;
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
            if (_pdBuildTicks > 1)
                Log("预设栏格子分批填充完成：" + count + " 格/" +
                    _pdBuildTicks + " tick");
            return true;
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
            // Only clickable while the save browser is up — otherwise
            // name-strip taps fall through to the cell's own apply click.
            nbgi.raycastTarget = _pdSaveBrowsing;
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
            tag.NameHit = nbgi;
            _pdVisibleCells.Add(tag);
            PlacePdCell(tag);
            Button nameBtn = nameBg.AddComponent<Button>();
            nameBtn.targetGraphic = nbgi;
            nameBtn.transition = Selectable.Transition.None;
            PdSlotTag nameTag = tag;
            nameBtn.onClick.AddListener(delegate
            {
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                BeginDockRename(nameTag);
            });
            Button button = cell.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            string captured = path;
            button.onClick.AddListener(delegate
            {
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                if (DeletePresetOnClick(captured)) return;
                // Save mode: thumbnail click = overwrite-save to this preset
                // (拍照/覆盖/另存 overlay); the name strip's own button wins
                // clicks there and opens rename instead.
                if (SavePresetOnClick(tag, cr)) return;
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
            Atom target = PdEffectiveTarget();
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

        // ---------- load target: nearest woman, user-overridable ----------

        // Female-only: presets are only ever applied to women. Manual pick
        // wins while that atom is still in the scene; otherwise the row
        // tracks whichever woman is nearest the view.
        private static Atom PdEffectiveTarget()
        {
            SuperController sc = SuperController.singleton;
            Atom t = _pdLoadTarget;
            if (t != null)
            {
                bool alive = false;
                try
                {
                    alive = sc != null && sc.GetAtomByUid(t.uid) == t &&
                        SceneQuickActions.IsGender(t, "Female");
                }
                catch { }
                if (alive) return t;
                _pdLoadTarget = null;
            }
            return SceneQuickActions.FindClosestFemale();
        }

        private static void TogglePdTargetPopup()
        {
            if (_pdDock == null) return;
            if (_pdTargetPopup != null && _pdTargetPopup.activeSelf)
            {
                _pdTargetPopup.SetActive(false);
                return;
            }
            RebuildPdTargetPopup();
        }

        private static void RebuildPdTargetPopup()
        {
            if (_pdTargetPopup == null)
            {
                GameObject go = new GameObject("PdTargetPopup",
                    typeof(RectTransform));
                go.AddComponent<PdDockTag>();
                RectTransform rt = (RectTransform)go.transform;
                rt.SetParent(_pdDock, false);
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2(FavTagGap, -FavPad);
                Image bg = go.AddComponent<Image>();
                bg.color = new Color(0.08f, 0.10f, 0.14f, 0.97f);
                bg.raycastTarget = true;
                _pdTargetPopup = go;
            }
            RectTransform panel = (RectTransform)_pdTargetPopup.transform;
            // Old rows die with the panel's children.
            for (int i = panel.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(panel.GetChild(i).gameObject);
            Atom eff = PdEffectiveTarget();
            var women = new List<Atom>();
            SuperController sc = SuperController.singleton;
            if (sc != null)
            {
                List<Atom> atoms = sc.GetAtoms();
                for (int i = 0; i < atoms.Count; i++)
                {
                    Atom a = atoms[i];
                    try
                    {
                        if (a == null || a.type != "Person" ||
                            !SceneQuickActions.IsGender(a, "Female"))
                            continue;
                    }
                    catch { continue; }
                    women.Add(a);
                }
            }
            const float rowH = 26f, gap = 4f, pad = 6f;
            int rows = women.Count;
            float need = pad * 2f + Mathf.Max(1, rows) * rowH +
                Mathf.Max(0, rows - 1) * gap;
            panel.sizeDelta = new Vector2(FavColW,
                Mathf.Min(PdGridH, need));
            if (rows == 0)
            {
                Text none = new GameObject("None", typeof(RectTransform))
                    .AddComponent<Text>();
                RectTransform nr = (RectTransform)none.transform;
                nr.SetParent(panel, false);
                nr.anchorMin = Vector2.zero;
                nr.anchorMax = Vector2.one;
                nr.offsetMin = Vector2.zero;
                nr.offsetMax = Vector2.zero;
                none.text = "场景中没有女性角色";
                none.alignment = TextAnchor.MiddleCenter;
                none.fontSize = 14;
                none.color = new Color(1f, 1f, 1f, 0.7f);
                none.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                none.raycastTarget = false;
            }
            for (int i = 0; i < rows; i++)
            {
                Atom a = women[i];
                GameObject row = new GameObject("PdTarget " + a.name,
                    typeof(RectTransform));
                RectTransform rr = (RectTransform)row.transform;
                rr.SetParent(panel, false);
                rr.anchorMin = new Vector2(0f, 1f);
                rr.anchorMax = new Vector2(1f, 1f);
                rr.pivot = new Vector2(0.5f, 1f);
                rr.anchoredPosition = new Vector2(0f, -pad - i * (rowH + gap));
                rr.sizeDelta = new Vector2(-pad * 2f, rowH);
                Image ib = row.AddComponent<Image>();
                ib.color = a == eff
                    ? new Color(0.14f, 0.42f, 0.22f, 1f)
                    : new Color(0.16f, 0.18f, 0.24f, 1f);
                ib.raycastTarget = true;
                Button rb = row.AddComponent<Button>();
                rb.targetGraphic = ib;
                rb.transition = Selectable.Transition.None;
                Atom pick = a;
                rb.onClick.AddListener(delegate
                {
                    _pdLoadTarget = pick;
                    _pdTargetPopup.SetActive(false);
                    _pdTargetRefresh = 0f; // repaint the row label now
                    VrHaptics.Confirm();
                });
                Text rt = new GameObject("Label", typeof(RectTransform))
                    .AddComponent<Text>();
                RectTransform tr = (RectTransform)rt.transform;
                tr.SetParent(rr, false);
                tr.anchorMin = Vector2.zero;
                tr.anchorMax = Vector2.one;
                tr.offsetMin = new Vector2(6f, 0f);
                tr.offsetMax = new Vector2(-6f, 0f);
                rt.text = (a == eff ? "● " : "") + a.name;
                rt.alignment = TextAnchor.MiddleLeft;
                rt.fontSize = 13;
                rt.color = Color.white;
                rt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                rt.raycastTarget = false;
            }
            _pdTargetPopup.SetActive(true);
            _pdTargetPopup.transform.SetAsLastSibling();
            VrHaptics.Press();
        }

        private static void ApplyDockSlot(string path, Atom target = null)
        {
            target = target ?? PdEffectiveTarget();
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
