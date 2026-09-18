using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MVR.FileManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    // Modern VR file browser replacing the native preset dialog.
    // P1: persisted global tabs, file grid with .vap thumbnails (sidecar .jpg),
    // breadcrumb, search, sort, open/cancel, save mode, new folder.
    internal sealed class VrPresetBrowser
    {
        private const float CanvasWidth = 1900f;
        private const float CanvasHeight = 1100f;
        private const float PanelDistance = 1.15f;
        private const float PanelScale = 0.0009f;
        private const float CardW = 176f;
        private const float CardH = 216f;
        private const string TabsFileName = "Quest3TriggerUI.browser-tabs.txt";

        private static VrPresetBrowser _instance;
        private static VrPresetBrowser Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new VrPresetBrowser();
                return _instance;
            }
        }

        internal static bool IsOpen
        {
            get { return _instance != null && _instance._canvas != null
                     && _instance._canvas.gameObject.activeSelf; }
        }

        // True while the pointer rests on the panel — used to keep the
        // right-index long-press radial menu from hijacking box-select drags.
        internal static bool PointerInside
        {
            get { return IsOpen && _instance._pointerInside; }
        }

        // Transform the VR keyboard docks under while the browser is open
        // (the panel sits at the control panel's spot, so the keyboard
        // follows it instead of tracking the player's head).
        internal static Transform KeyboardDock
        {
            get { return IsOpen ? _instance._canvas.transform : null; }
        }

        // ---- post-load warmup --------------------------------------------
        // A hot-reloaded assembly has zero JIT-compiled methods and cold
        // caches, so the first real open used to stall for seconds. The
        // plugin Update drives this one step per frame (after a short delay),
        // building the canvas hidden, pre-listing the preset roots into
        // _listCache, and exercising the grid path. Aborts if the user opens
        // the panel for real mid-warmup.
        // -1 = idle/not started, -2 = finished or aborted. The driver calls
        // BeginWarmup once the delay has elapsed, then WarmupStep per frame.
        private static int _warmupStep = -1;
        private static readonly string[] WarmRoots = {
            "Custom", "Saves",
            "Custom/Atom/Person/Hair",
            "Custom/Atom/Person/Clothing",
            "Custom/Atom/Person/Appearance",
            "Custom/Atom/Person/Skin" };
        // Tab dirs + roots, deduped — tabs are what actually get opened.
        private static List<string> _warmDirs;

        internal static void BeginWarmup()
        {
            if (_warmupStep == -1) _warmupStep = 0;
        }

        internal static bool WarmupStep()
        {
            if (_warmupStep < 0) return false;
            if (IsOpen)
            {
                _warmupStep = -2;
                LogErr("Q3 browser warmup skipped: user opened panel");
                return false;
            }
            try
            {
                VrPresetBrowser inst = Instance;
                switch (_warmupStep)
                {
                    case 0:
                        if (inst._canvas == null)
                        {
                            inst.Build();
                            inst._canvas.gameObject.SetActive(false);
                        }
                        break;
                    case 1:
                        inst.LoadTabs();
                        if (inst._tabs.Count == 0)
                            inst._tabs.Add("Custom");
                        inst._activeTab = 0;
                        inst._dir = inst._tabs[0];
                        _warmDirs = new List<string>(inst._tabs);
                        foreach (string r in WarmRoots)
                            if (!_warmDirs.Contains(r)) _warmDirs.Add(r);
                        break;
                    case 2:
                        inst.RefreshGrid();   // cards + listing cache for _dir
                        break;
                    default:
                        int i = _warmupStep - 3;
                        if (i < _warmDirs.Count)
                        {
                            // One stale tab dir must not kill the whole
                            // warmup — skip and continue.
                            try { inst.WarmDir(_warmDirs[i]); }
                            catch (Exception dex)
                            {
                                LogErr("Q3 warm dir " + _warmDirs[i] +
                                    ": " + dex.Message);
                            }
                            break;
                        }
                        _warmupStep = -2;
                        _warmDirs = null;
                        LogErr("Q3 browser warmup done");
                        return false;
                }
                _warmupStep++;
                return true;
            }
            catch (Exception ex)
            {
                _warmupStep = -2;
                _warmDirs = null;
                LogErr("Q3 browser warmup aborted: " + ex);
                return false;
            }
        }

        private void WarmDir(string dir)
        {
            DirListing dl = ListDir(dir);
            dl.Expires = Time.unscaledTime + ListCacheSeconds;
            _listCache[dir] = dl;
        }

        // Entry used by our own callers (matches GetMediaPathDialog role).
        // filter e.g. "vap"; saveMode shows filename entry; callback gets full
        // VaM-relative slash path or "" on cancel.
        internal static void ShowDialog(
            string title, string suggestedDir, string filter,
            bool saveMode, string defaultSaveName,
            Action<string> onResult)
        {
            Instance.Open(title, suggestedDir, filter, saveMode,
                defaultSaveName, onResult, null);
        }

        internal static void ShowDialogFull(
            string title, string suggestedDir, string filter,
            bool saveMode, string defaultSaveName,
            Action<string, bool> onResult, bool dirPick = false)
        {
            Instance.Open(title, suggestedDir, filter, saveMode,
                defaultSaveName, null, onResult, dirPick);
        }

        // "vap|vab" / "*.jpg" / "vap,json" style filters → ext array.
        private static string[] ParseFilter(string filter)
        {
            if (string.IsNullOrEmpty(filter))
                return null;
            List<string> exts = new List<string>();
            foreach (string part in filter.Split(
                '|', ',', ';', ' '))
            {
                string e = part.Trim().TrimStart('*', '.')
                    .ToLowerInvariant();
                if (e.Length > 0 && !exts.Contains(e))
                    exts.Add(e);
            }
            return exts.Count == 0 ? null : exts.ToArray();
        }

        internal static void Shutdown()
        {
            if (_instance != null)
                _instance.Dispose();
            _instance = null;
        }

        // ---- state ----
        private Canvas _canvas;
        private Font _font;
        private readonly List<string> _tabs = new List<string>();
        private int _activeTab;
        private string _dir = "";
        private string _filter = "";
        // Multi-extension filters arrive through the native takeover path
        // (fileFormat can be "vap|vab"); _filter stays the primary ext for
        // save-mode suffixing.
        private string[] _filterExts;
        private bool _saveMode;
        // selectDirectory=true callers get "pick a folder" semantics —
        // files are hidden and 打开 commits the current directory.
        private bool _dirPickMode;
        private string _title = "";
        private Action<string> _cb;
        private Action<string, bool> _cbFull;
        private bool _sortByDate;
        private string _selectedPath = "";
        private string _selectedCardName = "";
        private bool _pointerInside;

        // ui refs
        private RectTransform _tabStrip;
        private RectTransform _gridContent;
        private ScrollRect _scroll;
        private InputField _searchInput;
        private InputField _fileNameInput;
        private Text _pathText;
        private RectTransform _pathBar;
        private bool _hudDragging;
        private bool _gestureRight;
        private Vector3 _hudHandPrev;
        private Text _statusText;
        private GameObject _saveRow;
        private GameObject _selRect;
        private GameObject _selBar;
        private Text _selCountText;
        private Button _selDeleteBtn;
        private bool _pickDest;
        private bool _pickCopy;
        private bool _dragging;
        private Vector2 _dragStart;
        private int _pressIdx = -1;
        private bool _moveDrag;
        private GameObject _dragGhost;
        private Text _ghostText;

        // sidebar: favourites list + lazy directory tree
        private const float SidebarW = 292f;
        private const string FavsFileName =
            "Quest3TriggerUI.browser-fav-dirs.txt";
        private GameObject _sideBody;
        private Text _sideTabText;
        private RectTransform _gridViewport;
        private GameObject _vscrollBar;
        private ScrollRect _favScroll;
        private ScrollRect _treeScroll;
        private RectTransform _favContent;
        private RectTransform _treeContent;
        private bool _sidebarOpen = true;
        private bool _treeForce = true;
        private string _treeBuiltForDir;
        private RectTransform _sidebarRect;
        private float _nextSibScan;
        private readonly List<string> _favs = new List<string>();
        private readonly HashSet<string> _treeExpanded =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Children listings are cached per dir — the tree used to rescan
        // every expanded node on each navigation, which stacked disk hits
        // as expansion accumulated and made every click hitch.
        private readonly Dictionary<string, List<string>> _treeKids =
            new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);
        // Foreign-canvas eligibility is static per canvas; the 0.5s sweep
        // caches it and only re-runs FindObjectsOfType + bounds checks.
        private readonly Dictionary<Canvas, bool> _foreignSkip =
            new Dictionary<Canvas, bool>();
        private float _batchDeleteArmUntil;
        private readonly HashSet<string> _selPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Texture2D> _thumbCache =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _jpgSet =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void Open(
            string title, string suggestedDir, string filter,
            bool saveMode, string defaultSaveName,
            Action<string> cb, Action<string, bool> cbFull,
            bool dirPick = false)
        {
            try
            {
                OpenInternal(title, suggestedDir, filter, saveMode,
                    defaultSaveName, cb, cbFull, dirPick);
            }
            catch (Exception ex)
            {
                LogErr("Q3 browser open FAILED: " + ex);
                try { if (_canvas != null) _canvas.gameObject.SetActive(false); }
                catch { }
            }
        }

        private void OpenInternal(
            string title, string suggestedDir, string filter,
            bool saveMode, string defaultSaveName,
            Action<string> cb, Action<string, bool> cbFull,
            bool dirPick)
        {
            if (_canvas == null)
                Build();
            // A native MediaFileBrowser left open (e.g. a cancel click that
            // missed) renders on top of this panel and swallows ray input —
            // dismiss it before showing ours.
            try
            {
                uFileBrowser.FileBrowser nb = SuperController.singleton != null
                    ? SuperController.singleton.mediaFileBrowserUI : null;
                if (nb != null && !nb.IsHidden())
                    nb.Hide();
            }
            catch { }
            LogErr("Q3 browser open: dir=" + suggestedDir +
                " save=" + saveMode);
            _title = string.IsNullOrEmpty(title) ? "文件浏览" : title;
            _filterExts = ParseFilter(filter);
            _filter = _filterExts != null ? _filterExts[0] : "";
            _saveMode = saveMode;
            _dirPickMode = dirPick;
            _cb = cb;
            _cbFull = cbFull;
            _selectedPath = "";
            _selectedCardName = "";
            // Gesture state can linger if the panel was hidden mid-drag.
            ResetGesture();
            _treeForce = true;   // dirs may have changed while closed

            LoadTabs();
            string req = ValidDir(suggestedDir)
                ? suggestedDir.Trim().Replace('\\', '/').TrimEnd('/')
                : null;
            if (req != null)
            {
                // Global tab-matching: prefer an existing tab sitting at or
                // inside the requested directory (deepest match wins — a tab
                // left in a subfolder of this category keeps its position).
                // Only when no tab covers the category do we open a fresh tab
                // at the requested root.
                int match = FindTabUnder(req);
                if (match >= 0)
                    _activeTab = match;
                else
                {
                    _tabs.Add(req);
                    _activeTab = _tabs.Count - 1;
                    SaveTabs();
                }
            }
            else if (_tabs.Count == 0)
            {
                _tabs.Add("Custom");
                _activeTab = 0;
            }
            if (_activeTab < 0 || _activeTab >= _tabs.Count)
                _activeTab = 0;
            _dir = _tabs[_activeTab];
            if (!ValidDir(_dir))
                _dir = req ?? "Custom";

            if (_saveRow != null)
                _saveRow.SetActive(_saveMode);
            if (_saveMode && _fileNameInput != null)
                _fileNameInput.text = defaultSaveName ?? "";
            if (_dirPickMode)
                SetStatus("选择目录：进入目标文件夹后点「打开」");
            if (_searchInput != null)
                _searchInput.text = "";
            _sortByDate = false;

            Recenter();
            _canvas.gameObject.SetActive(true);
            // Earlier builds restored stale values onto VaM's OWN
            // CanvasGroups (faded icons, dead clicks that persist for the
            // whole session). Repair every sibling CanvasGroup on our
            // ancestor path back to healthy defaults before hiding — VaM
            // re-applies its own values on the next UI transition anyway.
            // Leftover hide-tags and pre-tag orphans from dead payload
            // generations would keep their alpha-0 groups on the HUD
            // forever — destroy/reset them before hiding.
            CleanupHudState();
            // Hide the control panel's other visuals while the browser
            // occupies its spot. Sibling-of-ancestors hiding leaves our own
            // branch (and the input modules) untouched.
            if (HideHudWhileOpen)
                HideHudAround();
            RebuildTabs();
            RefreshGrid();
            LogErr("Q3 browser shown: dir=" + _dir);
        }

        private static bool ValidDir(string dir)
        {
            return !string.IsNullOrEmpty(dir) &&
                   FileManager.DirectoryExists(dir, false, false);
        }

        // Index of the tab equal to req or located inside it; deepest
        // (longest path) match wins. -1 when no tab covers the directory.
        private int FindTabUnder(string req)
        {
            int best = -1;
            int bestLen = -1;
            string prefix = req + "/";
            for (int i = 0; i < _tabs.Count; i++)
            {
                string t = _tabs[i];
                bool hit = string.Equals(t, req,
                    StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                if (hit && t.Length > bestLen)
                {
                    best = i;
                    bestLen = t.Length;
                }
            }
            return best;
        }

        // ---------- persistence ----------
        private static string TabsPath()
        {
            return Path.Combine(BepInEx.Paths.ConfigPath, TabsFileName);
        }

        private void LoadTabs()
        {
            _tabs.Clear();
            _activeTab = 0;
            try
            {
                string f = TabsPath();
                if (!File.Exists(f))
                    return;
                string[] lines = File.ReadAllLines(f);
                if (lines.Length == 0)
                    return;
                int.TryParse(lines[0], out _activeTab);
                for (int i = 1; i < lines.Length && _tabs.Count < 12; i++)
                {
                    string d = lines[i].Trim().Replace('\\', '/');
                    if (d.Length > 0 && ValidDir(d) && !_tabs.Contains(d))
                        _tabs.Add(d);
                }
                if (_activeTab >= _tabs.Count)
                    _activeTab = 0;
            }
            catch (Exception ex)
            {
                LogErr("Q3 browser tabs load: " + ex.Message);
            }
        }

        // Tab writes are debounced: every NavTo/SwitchTab used to hit the
        // disk synchronously mid-gesture — a fixed hitch on each click.
        // The file is written 0.6s after the last change and flushed on
        // Close, so nothing is lost.
        private float _tabsDirtyAt = -1f;

        private void SaveTabs()
        {
            _tabsDirtyAt = Time.unscaledTime + 0.6f;
        }

        private void FlushTabs()
        {
            _tabsDirtyAt = -1f;
            try
            {
                List<string> lines = new List<string> { _activeTab.ToString() };
                lines.AddRange(_tabs);
                File.WriteAllLines(TabsPath(), lines.ToArray());
            }
            catch (Exception ex)
            {
                LogErr("Q3 browser tabs save: " + ex.Message);
            }
        }

        // ---------- canvas ----------
        private void Build()
        {
            // Hot reloads leave orphan canvases from previous payloads alive
            // (their statics are gone but the GameObjects persist and render).
            // FindObjectsOfType skips INACTIVE objects, so use
            // FindObjectsOfTypeAll and kill leftovers by name — 20+
            // inactive orphans had accumulated and stayed registered in
            // VaM's allCanvases list.
            foreach (GameObject orphan in
                Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (orphan != null && orphan.scene.IsValid() &&
                    orphan.name == "Quest3 Preset Browser")
                    UnityEngine.Object.Destroy(orphan);
            }
            GameObject go = new GameObject("Quest3 Preset Browser");
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.pixelPerfect = false;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = 32760;
            go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1f;
            go.AddComponent<GraphicRaycaster>();
            RectTransform root = go.GetComponent<RectTransform>();
            root.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, CanvasWidth);
            root.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, CanvasHeight);
            SuperController.singleton.AddCanvas(_canvas);
            _font = (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");

            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0.10f, 0.11f, 0.14f, 0.97f);

            BuildHeader(root);
            LogErr("Q3 browser build: header ok");
            BuildPathBar(root);
            LogErr("Q3 browser build: pathbar ok");
            BuildGrid(root);
            LogErr("Q3 browser build: grid ok");
            BuildSidebar(root);
            BuildFooter(root);
            BuildSelBar(root);
            BuildDragGhost(root);
            LogErr("Q3 browser build: footer ok");
            PanelHoverTracker hover = go.AddComponent<PanelHoverTracker>();
            hover.Owner = this;
            BrowserTicker ticker = go.AddComponent<BrowserTicker>();
            ticker.Owner = this;
            SetLayerRecursively(go, ResolveUiLayer());
            go.SetActive(false);
        }

        // Scroll wheel / VR thumbstick while the pointer rests anywhere on
        // the panel — VaM routes wheel through its own Widget pipeline, so
        // plain Unity UI never receives IScrollHandler callbacks.
        private bool _navSuppressed;
        private bool _navPrevDisableAll;

        private void Tick()
        {
            // While the pointer rests on this panel, suppress VaM scene
            // navigation so the same thumbstick/wheel input that scrolls the
            // list doesn't also fly the player around.
            SuperController sc = SuperController.singleton;
            if (sc != null && HideHudWhileOpen)
            {
                if (_pointerInside && !_navSuppressed)
                {
                    _navPrevDisableAll = sc.disableAllNavigation;
                    sc.disableAllNavigation = true;
                    _navSuppressed = true;
                }
                else if (!_pointerInside && _navSuppressed)
                {
                    RestoreNavigation();
                }
            }
            // Debounced tab persistence — see SaveTabs.
            if (_tabsDirtyAt > 0f && Time.unscaledTime >= _tabsDirtyAt)
                FlushTabs();
            // Column count depends on the realised viewport width, which is
            // only valid after the first layout pass — re-check cheaply.
            UpdateVisibleCards();
            // The rename field must NOT be re-activated every frame:
            // ActivateInputField selects the whole text, and the text bridge
            // captures that selection — the next keystroke then replaces
            // everything. The bridge re-focuses itself on every Send, so
            // focus loss is harmless (onEndEdit no longer commits anyway).
            if (_renameView != null &&
                (!_renameView.RenameInput.gameObject.activeSelf ||
                 _renameView.ItemIndex < 0))
                _renameView = null;
            // Manual click-vs-box-select gesture. VaM's input module never
            // gives plain Unity UI reliable pointerDown/Up/drag — and its
            // reference-camera ray isn't reachable either — so instead reuse
            // the hit info VaM itself maintains: CurrentLookTarget (the object
            // under the pointer) + CurrentCursor (the laser dot sitting on
            // the hit point). Trigger state comes from the plugin's own
            // state machine (has an OVRInput fallback).
            float itr, rgv, lgv, abv;
            Vector2 stv;
            bool hasIn = OpenVrInputBridge.TryGetInput(
                out itr, out rgv, out lgv, out abv, out stv);
            bool held = Input.GetMouseButton(0) ||
                (hasIn && (itr > 0.55f || abv > 0.5f)) ||
                (Quest3TriggerUIPlugin.Trigger != null &&
                 Quest3TriggerUIPlugin.Trigger.Pressed);
            // VaM re-activates its HUD children when the panel is grabbed —
            // CanvasGroup hiding survives that, and re-running the sweep
            // each frame also catches newly created siblings.
            if (HideHudWhileOpen)
                EnforceHudHidden();
            // Third-party panels parked around the control panel (CUA
            // Manager's helper bar, our own pinned-tiles list, floating
            // shortcut labels) overlap the docked keyboard and swallow its
            // raycasts. Sweep periodically and fade out any foreign canvas
            // whose rect intersects the panel+keyboard zone.
            if (HideHudWhileOpen && Time.unscaledTime >= _nextForeignScan)
            {
                _nextForeignScan = Time.unscaledTime + 0.5f;
                ScanForeignPanels();
            }
            GameObject lookR = VrPointerPresentation.CurrentLookTarget(true);
            GameObject lookL = VrPointerPresentation.CurrentLookTarget(false);
            GameObject lookT = lookR != null ? lookR : lookL;
            bool lookRight = lookR != null;
            // Inside/onGrid/onBar from the live look target — more reliable
            // than IPointerEnter, which VaM's module dispatches unevenly.
            if (_canvas != null)
                _pointerInside = lookT != null &&
                    lookT.transform.IsChildOf(_canvas.transform);
            bool onGrid = _pointerInside && _gridContent != null &&
                (lookT == _gridContent.gameObject ||
                 lookT.transform.IsChildOf(_gridContent));
            // The viewport's background image catches hits on space that
            // isn't covered by cards or the (masked-off) content — treat it
            // as grid-blank too so taps there can clear the selection.
            bool onViewport = onGrid ||
                (_pointerInside && _scroll != null &&
                 _scroll.viewport != null && lookT != null &&
                 (lookT == _scroll.viewport.gameObject ||
                  lookT.transform.IsChildOf(_scroll.viewport)));
            bool onBar = _pointerInside && _pathBar != null &&
                (lookT == _pathBar.gameObject ||
                 lookT.transform.IsChildOf(_pathBar));
            Vector2 cur;
            Vector3 curW;
            bool surfOk = _pressActive
                ? SurfacePoint(_gestureRight, out cur, out curW)
                : SurfacePoint(lookRight, out cur, out curW);
            if (held && !_trigHeldPrev)
            {
                LogErr("Q3 press edge: grid=" + onGrid + " bar=" + onBar +
                    " surf=" + surfOk +
                    " target=" + (lookT != null ? lookT.name : "null"));
                if (onViewport && surfOk)
                {
                    // Presses on a card's ops buttons (Btn_*/Input) belong to
                    // that button's own click — don't start a selection.
                    // Same for the scrollbar: it floats over the last card
                    // column, so without this a scrollbar drag would pick up
                    // a card as a move-drag instead.
                    bool onScrollbar = _vscrollBar != null &&
                        lookT != null &&
                        lookT.transform.IsChildOf(_vscrollBar.transform);
                    if (!IsCardOps(lookT) && !onScrollbar)
                    {
                        _pressActive = true;
                        _gestureRight = lookRight;
                        _dragStart = cur;
                        _pressIdx = ItemIndexAt(cur);
                    }
                }
                else if (onBar)
                {
                    // The path bar doubles as the title bar — dragging it
                    // moves THIS panel. mainHUD writes get overwritten by
                    // VaM's HUD anchoring, so we move our own canvas (still
                    // parented under the control panel, so it keeps
                    // following; the offset persists until recenter).
                    Transform h = VrPointerPresentation.MotionController(
                        SuperController.singleton, lookRight);
                    if (h != null)
                    {
                        _hudDragging = true;
                        _gestureRight = lookRight;
                        _hudHandPrev = h.position;
                    }
                }
            }
            if (_hudDragging)
            {
                if (!held)
                    _hudDragging = false;
                else
                {
                    Transform h = VrPointerPresentation.MotionController(
                        SuperController.singleton, _gestureRight);
                    if (h != null && _canvas != null)
                    {
                        _canvas.transform.position +=
                            h.position - _hudHandPrev;
                        _hudHandPrev = h.position;
                    }
                }
            }
            if (_pressActive)
            {
                if (held && surfOk)
                {
                    // 30px in content units — VR hand jitter on a tap easily
                    // exceeds 12px and would turn every click into a
                    // micro-drag, swallowing the release-click entirely.
                    if (!_dragging && !_moveDrag &&
                        (cur - _dragStart).sqrMagnitude > 900f)
                    {
                        // Press on an item card + drag = move that item (or
                        // the whole selection it belongs to). Press on blank
                        // grid space + drag = box-select.
                        if (_pressIdx >= 0 && _pressIdx < _items.Count &&
                            !_items[_pressIdx].IsParent)
                        {
                            _moveDrag = true;
                            string sp = _items[_pressIdx].Path;
                            if (!_selPaths.Contains(sp))
                            {
                                _selPaths.Clear();
                                _selPaths.Add(sp);
                                RefreshSelectionVisuals();
                            }
                            UpdateSelBar();
                            if (_dragGhost != null)
                                _dragGhost.SetActive(true);
                            LogErr("Q3 drag-move start: " + sp +
                                " sel=" + _selPaths.Count);
                        }
                        else
                        {
                            _dragging = true;
                            _selRect.SetActive(true);
                            _selRect.transform.SetAsLastSibling();
                            ApplySelRect(_dragStart, cur);
                            LogErr("Q3 browser box-select drag start");
                        }
                    }
                    if (_dragging)
                    {
                        ApplySelRect(_dragStart, cur);
                        UpdateBoxSelection(_dragStart, cur);
                    }
                    else if (_moveDrag)
                    {
                        UpdateDragGhost(cur, curW, onViewport, lookT);
                    }
                }
                if (!held)
                {
                    _pressActive = false;
                    if (_dragging)
                    {
                        _dragging = false;
                        _clickSuppressUntil = Time.unscaledTime + 0.3f;
                        if (_selRect != null)
                            _selRect.SetActive(false);
                        UpdateSelBar();
                    }
                    else if (_moveDrag)
                    {
                        _moveDrag = false;
                        _clickSuppressUntil = Time.unscaledTime + 0.3f;
                        if (_dragGhost != null)
                            _dragGhost.SetActive(false);
                        // Drop target: a dir card under the cursor, or a
                        // sidebar row (tree node / favourite) carrying a
                        // DirDropRef.
                        string dest = DragDestAt(cur, onViewport, lookT);
                        LogErr("Q3 drag-move end: dest=" +
                            (dest ?? "null") + " sel=" + _selPaths.Count);
                        if (dest != null)
                            DoBatchMove(dest);
                        else
                            SetStatus("已取消移动");
                    }
                    else
                    {
                        int idx = (onViewport && surfOk)
                            ? ItemIndexAt(cur) : -999;
                        LogErr("Q3 release: vp=" + onViewport +
                            " surf=" + surfOk + " idx=" + idx +
                            " sel=" + _selPaths.Count + " lt=" +
                            (lookT != null ? lookT.name : "null"));
                        if (onViewport && surfOk)
                        {
                            if (idx < 0)
                            {
                                // Click on empty grid space exits
                                // box-select mode and clears the batch
                                // selection.
                                if (_selPaths.Count > 0)
                                {
                                    _selPaths.Clear();
                                    UpdateSelBar();
                                    RefreshSelectionVisuals();
                                }
                            }
                            else if (!IsCardOps(lookT))
                            {
                                CardView v = FindView(idx);
                                if (v != null)
                                    OnCardClicked(v);
                            }
                        }
                    }
                    _pressIdx = -1;
                }
            }
            _trigHeldPrev = held;
            if (!_pointerInside)
                return;
            // Route wheel/stick scroll to whichever list the pointer is
            // over — grid, directory tree or favourites.
            ScrollRect scr = _scroll;
            if (lookT != null)
            {
                if (_treeScroll != null &&
                    lookT.transform.IsChildOf(_treeScroll.transform))
                    scr = _treeScroll;
                else if (_favScroll != null &&
                    lookT.transform.IsChildOf(_favScroll.transform))
                    scr = _favScroll;
            }
            if (scr == null)
                return;
            float d = Input.mouseScrollDelta.y;
            if (Mathf.Abs(d) > 0.001f)
                scr.verticalNormalizedPosition = Mathf.Clamp01(
                    scr.verticalNormalizedPosition + d * 0.08f);
            if (hasIn && Mathf.Abs(stv.y) > 0.15f)
            {
                scr.verticalNormalizedPosition = Mathf.Clamp01(
                    scr.verticalNormalizedPosition +
                    stv.y * Time.deltaTime * 1.5f);
            }
        }

        // Pointer-enter propagates up the hierarchy, so a tracker on the root
        // sees the ray enter/leave the whole panel including every child.
        private sealed class PanelHoverTracker : MonoBehaviour,
            IPointerEnterHandler, IPointerExitHandler
        {
            internal VrPresetBrowser Owner;
            public void OnPointerEnter(PointerEventData e)
            { if (Owner != null) Owner._pointerInside = true; }
            public void OnPointerExit(PointerEventData e)
            { if (Owner != null) Owner._pointerInside = false; }
        }

        // Per-frame driver — VrPresetBrowser is a plain class, so a tiny
        // MonoBehaviour on the canvas pumps Tick() while the panel lives.
        private void RestoreNavigation()
        {
            if (!_navSuppressed)
                return;
            _navSuppressed = false;
            try
            {
                SuperController sc = SuperController.singleton;
                if (sc != null)
                    sc.disableAllNavigation = _navPrevDisableAll;
            }
            catch { }
        }

        // Drag/press state must not survive a hide — the next open would
        // resolve a phantom release or keep a dead ghost on screen.
        private void ResetGesture()
        {
            _pressActive = false;
            _pressIdx = -1;
            _dragging = false;
            _moveDrag = false;
            _hudDragging = false;
            _pickDest = false;
            _pickCopy = false;
            if (_dragGhost != null)
                _dragGhost.SetActive(false);
            if (_selRect != null)
                _selRect.SetActive(false);
        }

        private sealed class BrowserTicker : MonoBehaviour
        {
            internal VrPresetBrowser Owner;
            private void Update() { if (Owner != null) Owner.Tick(); }
            // The canvas deactivates when its host (control panel) hides —
            // Update stops running, so release the navigation lock here or
            // the player stays frozen in place.
            private void OnDisable()
            {
                if (Owner == null)
                    return;
                Owner._pointerInside = false;
                Owner.ResetGesture();
                Owner.RestoreNavigation();
                Owner.RestoreHud();
                Owner.LogHudDiag("disable");
                if (Quest3TriggerUIPlugin.Instance != null)
                    Quest3TriggerUIPlugin.Instance.StartCoroutine(
                        Owner.HudDiagLater());
            }
        }

        private void BuildHeader(RectTransform root)
        {
            // tab strip row
            GameObject strip = NewObj("TabStrip", root);
            _tabStrip = strip.GetComponent<RectTransform>();
            Anchor(_tabStrip, 8f, CanvasHeight - 48f, 310f, 4f);
            HorizontalLayoutGroup hlg = strip.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 4f;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childControlWidth = false;
            hlg.childControlHeight = true;

            // search box (top-right overlay)
            _searchInput = NewInput(root, "🔍 搜索本目录…");
            PlaceRight(_searchInput.GetComponent<RectTransform>(), 8f, 6f, 300f, 36f);
            _searchInput.onValueChanged.AddListener(_ => RefreshGrid());
        }

        private void BuildPathBar(RectTransform root)
        {
            GameObject bar = NewObj("PathBar", root);
            _pathBar = bar.GetComponent<RectTransform>();
            Anchor(_pathBar, 8f, CanvasHeight - 104f, 8f, 56f);
            Image bi = bar.AddComponent<Image>();
            bi.color = new Color(0.14f, 0.16f, 0.20f, 1f);

            Button up = NewButton(bar.transform, "⬆ 上级");
            Place(up.GetComponent<RectTransform>(), 8f, 4f, 140f, 40f);
            up.onClick.AddListener(GoParent);

            _pathText = NewText(bar.transform, "", 24, TextAnchor.MiddleLeft);
            Anchor(_pathText.rectTransform, 156f, 4f, 160f, 4f);

            Button sort = NewButton(bar.transform, "排序:名称");
            sort.name = "SortBtn";
            PlaceRight(sort.GetComponent<RectTransform>(), 10f, 4f, 140f, 40f);
            sort.onClick.AddListener(() =>
            {
                _sortByDate = !_sortByDate;
                sort.GetComponentInChildren<Text>().text =
                    _sortByDate ? "排序:时间" : "排序:名称";
                RefreshGrid();
            });
        }

        private void BuildGrid(RectTransform root)
        {
            GameObject view = NewObj("GridViewport", root);
            _gridViewport = view.GetComponent<RectTransform>();
            Anchor(_gridViewport, SidebarW + 12f, 120f, 8f, 112f);
            view.AddComponent<Image>().color = new Color(0.07f, 0.08f, 0.10f, 1f);
            view.AddComponent<RectMask2D>();
            _scroll = view.AddComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 30f;
            _scroll.inertia = false;

            // Vertical scrollbar — ScrollRect drag is unreliable under the
            // VR ray; a grabbable bar is the guaranteed fallback.
            GameObject sbGo = NewObj("VScroll", view.transform);
            _vscrollBar = sbGo;
            Image sbBg = sbGo.AddComponent<Image>();
            sbBg.color = new Color(0.05f, 0.06f, 0.08f, 1f);
            Scrollbar sb = sbGo.AddComponent<Scrollbar>();
            sb.direction = Scrollbar.Direction.BottomToTop;
            RectTransform sbRect = sbGo.GetComponent<RectTransform>();
            PlaceRight(sbRect, 0f, 0f, 26f, 0f);
            sbRect.anchorMin = new Vector2(1f, 0f);
            sbRect.anchorMax = new Vector2(1f, 1f);
            sbRect.pivot = new Vector2(1f, 1f);
            sbRect.anchoredPosition = Vector2.zero;
            sbRect.sizeDelta = new Vector2(26f, 0f);

            GameObject handleGo = NewObj("Handle", sbGo.transform);
            Image handleImg = handleGo.AddComponent<Image>();
            handleImg.color = new Color(0.30f, 0.55f, 0.80f, 1f);
            RectTransform hr = handleGo.GetComponent<RectTransform>();
            hr.anchorMin = Vector2.zero;
            hr.anchorMax = Vector2.one;
            hr.offsetMin = new Vector2(2f, 2f);
            hr.offsetMax = new Vector2(-2f, -2f);
            sb.targetGraphic = handleImg;
            sb.handleRect = hr;
            _scroll.verticalScrollbar = sb;
            _scroll.verticalScrollbarVisibility =
                ScrollRect.ScrollbarVisibility.AutoHide;

            GameObject content = NewObj("GridContent", view.transform);
            _gridContent = content.GetComponent<RectTransform>();
            _gridContent.anchorMin = new Vector2(0f, 1f);
            _gridContent.anchorMax = new Vector2(1f, 1f);
            _gridContent.pivot = new Vector2(0.5f, 1f);
            _gridContent.anchoredPosition = Vector2.zero;
            // No GridLayoutGroup: cards are pooled and positioned manually so
            // opening a huge folder instantiates only the ~40 visible cards
            // instead of hundreds of GameObjects (that was the multi-hitch).
            // A transparent raycast Image turns empty grid space into a drag
            // surface — card children still get their clicks first.
            Image dragSurface = content.AddComponent<Image>();
            dragSurface.color = new Color(0f, 0f, 0f, 0f);
            GridDragHandler drag = content.AddComponent<GridDragHandler>();
            drag.Owner = this;

            // Selection rectangle overlay (lives in content space so it
            // scrolls together with the cards it covers).
            _selRect = NewObj("SelRect", content.transform);
            Image sr = _selRect.AddComponent<Image>();
            sr.color = new Color(0.3f, 0.6f, 1f, 0.22f);
            sr.raycastTarget = false;
            _selRect.SetActive(false);

            _scroll.content = _gridContent;
            _scroll.viewport = view.GetComponent<RectTransform>();
            _scroll.onValueChanged.AddListener(_ => UpdateVisibleCards());
        }

        private void ScrollPages(int pages) { }

        private void BuildFooter(RectTransform root)
        {
            // save row: filename input + save button
            _saveRow = NewObj("SaveRow", root);
            Anchor(_saveRow.GetComponent<RectTransform>(), 8f, 64f, 8f, CanvasHeight - 116f);
            _fileNameInput = NewInput(_saveRow.transform, "文件名");
            Place(_fileNameInput.GetComponent<RectTransform>(), 8f, 4f, 1090f, 44f);
            Button save = NewButton(_saveRow.transform, "保存到此");
            PlaceRight(save.GetComponent<RectTransform>(), 310f, 4f, 150f, 44f);
            save.onClick.AddListener(OnSavePressed);
            _statusText = NewText(_saveRow.transform, "", 20, TextAnchor.MiddleLeft);
            PlaceRight(_statusText.rectTransform, 8f, 4f, 290f, 44f);

            // bottom action row
            GameObject row = NewObj("ActionRow", root);
            Anchor(row.GetComponent<RectTransform>(), 8f, 8f, 8f, CanvasHeight - 60f);

            Button open = NewButton(row.transform, "打开");
            Place(open.GetComponent<RectTransform>(), 8f, 6f, 150f, 40f);
            open.onClick.AddListener(OnOpenPressed);

            Button newDir = NewButton(row.transform, "新建文件夹");
            Place(newDir.GetComponent<RectTransform>(), 168f, 6f, 160f, 40f);
            newDir.onClick.AddListener(OnNewFolder);

            Button refresh = NewButton(row.transform, "刷新");
            Place(refresh.GetComponent<RectTransform>(), 338f, 6f, 130f, 40f);
            refresh.onClick.AddListener(RefreshGrid);

            Button cancel = NewButton(row.transform, "取消");
            PlaceRight(cancel.GetComponent<RectTransform>(), 8f, 6f, 140f, 40f);
            cancel.onClick.AddListener(Close);
        }

        // ---------- tabs ----------
        // Tab buttons are pooled too — RebuildTabs runs on every NavTo
        // (tab label tracks the dir), so destroy+recreate churned ~40
        // GameObjects per click.
        private sealed class TabView
        {
            public GameObject Go;
            public Button Btn;
            public Text Txt;
            public int Index;
        }
        private readonly List<TabView> _tabPool = new List<TabView>();
        private Button _tabPlus;

        private TabView CreateTabView()
        {
            TabView v = new TabView();
            v.Btn = NewButton(_tabStrip, "");
            v.Go = v.Btn.gameObject;
            v.Txt = v.Btn.GetComponentInChildren<Text>();
            if (v.Txt != null)
            {
                v.Txt.fontSize = 18;
                v.Txt.resizeTextForBestFit = true;
                v.Txt.resizeTextMinSize = 12;
                v.Txt.resizeTextMaxSize = 18;
            }
            LayoutElement le = v.Go.AddComponent<LayoutElement>();
            le.preferredWidth = 220f;
            le.preferredHeight = 44f;
            v.Btn.onClick.AddListener(() => SwitchTab(v.Index));
            // close button overlaid on the tab
            Button x = NewButton(v.Go.transform, "×");
            PlaceRight(x.GetComponent<RectTransform>(), 2f, 4f, 32f, 36f);
            x.onClick.AddListener(() => CloseTab(v.Index));
            return v;
        }

        private void RebuildTabs()
        {
            int used = 0;
            for (int i = 0; i < _tabs.Count; i++)
            {
                TabView v;
                if (used < _tabPool.Count)
                    v = _tabPool[used];
                else
                {
                    v = CreateTabView();
                    _tabPool.Add(v);
                }
                used++;
                v.Index = i;
                string d = _tabs[i];
                string name = d.Length == 0 ? "根目录"
                    : d.TrimEnd('/').Substring(d.LastIndexOf('/') + 1);
                v.Txt.text = (i == _activeTab ? "● " : "") + name;
                // Keep HLG order matching tab order after pool reuse.
                v.Go.transform.SetSiblingIndex(i);
                if (!v.Go.activeSelf)
                    v.Go.SetActive(true);
            }
            for (int i = used; i < _tabPool.Count; i++)
                if (_tabPool[i].Go.activeSelf)
                    _tabPool[i].Go.SetActive(false);
            if (_tabPlus == null)
            {
                _tabPlus = NewButton(_tabStrip, "＋ 新标签");
                LayoutElement ple =
                    _tabPlus.gameObject.AddComponent<LayoutElement>();
                ple.preferredWidth = 140f;
                ple.preferredHeight = 44f;
                _tabPlus.onClick.AddListener(() =>
                {
                    if (_tabs.Count >= 12)
                        return;
                    _tabs.Add(_dir);          // duplicate paths allowed —
                    _activeTab = _tabs.Count - 1; // navigate inside the new tab
                    SaveTabs();
                    RebuildTabs();
                    RefreshGrid();
                });
            }
            _tabPlus.transform.SetAsLastSibling();
        }

        private void SwitchTab(int idx)
        {
            if (idx < 0 || idx >= _tabs.Count || idx == _activeTab)
                return;
            _tabs[_activeTab] = _dir;
            _activeTab = idx;
            _dir = _tabs[idx];
            if (!ValidDir(_dir))
            {
                _tabs.RemoveAt(idx);
                if (_tabs.Count == 0)
                    _tabs.Add("Custom");
                _activeTab = 0;
                _dir = _tabs[0];
            }
            _selectedPath = "";
            SaveTabs();
            RebuildTabs();
            RefreshGrid();
        }

        private void CloseTab(int idx)
        {
            if (idx < 0 || idx >= _tabs.Count)
                return;
            _tabs.RemoveAt(idx);
            if (_tabs.Count == 0)
                _tabs.Add(_dir);
            if (_activeTab >= _tabs.Count)
                _activeTab = _tabs.Count - 1;
            _dir = _tabs[_activeTab];
            SaveTabs();
            RebuildTabs();
            RefreshGrid();
        }

        // Directory listings are cached per dir — FileManager.GetFiles/
        // GetDirectories merge the on-disk folder with .var package
        // contents, so each call costs real IO work; caching makes
        // tree-expand / nav / tab-switch repeat visits instant. Entries
        // expire after ListCacheSeconds and are dropped wholesale whenever
        // a file op flags _treeForce.
        private sealed class DirListing
        {
            public float Expires;
            public List<string> Dirs;
            public List<string> Files;
            public List<string> Jpgs;
        }
        private const float ListCacheSeconds = 8f;
        private readonly Dictionary<string, DirListing> _listCache =
            new Dictionary<string, DirListing>(
                StringComparer.OrdinalIgnoreCase);

        private DirListing ListDir(string dir)
        {
            DirListing dl = new DirListing
            {
                Dirs = new List<string>(),
                Files = new List<string>(),
                Jpgs = new List<string>()
            };
            try
            {
                string[] ds = dir.Length == 0
                    ? new[] { "Custom", "Saves" }
                    : FileManager.GetDirectories(dir, "*", false);
                foreach (string d in ds)
                {
                    string dn = d.Replace('\\', '/').TrimEnd('/');
                    string leaf = dn.Substring(dn.LastIndexOf('/') + 1);
                    if (!leaf.StartsWith("."))
                        dl.Dirs.Add(dn);
                }
                if (dir.Length > 0)
                {
                    // One "*.*" listing feeds both the card list and the
                    // sidecar-jpg set — the old code made a second
                    // FileManager round-trip per refresh just for jpgs.
                    foreach (string f in
                        FileManager.GetFiles(dir, "*.*", false))
                    {
                        string fn = f.Replace('\\', '/');
                        dl.Files.Add(fn);
                        if (fn.EndsWith(".jpg",
                                StringComparison.OrdinalIgnoreCase))
                            dl.Jpgs.Add(fn);
                    }
                }
            }
            catch (Exception ex)
            {
                LogErr("Q3 browser list " + dir + ": " + ex.Message);
            }
            return dl;
        }

        // ---------- grid ----------
        private void RefreshGrid() { RefreshGrid(false); }

        // keepScroll preserves the scroll offset for in-place operations
        // (rename/delete/batch) so the view doesn't jump to the top.
        private void RefreshGrid(bool keepScroll)
        {
            float keepY = keepScroll && _gridContent != null
                ? _gridContent.anchoredPosition.y : 0f;
            _thumbJobs.Clear();
            _gridGen++;
            _pathText.text = "  " + (_dir.Length == 0 ? "(根目录)" : _dir);
            _tabs[_activeTab] = _dir;

            string search = _searchInput != null ? _searchInput.text : "";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Mutating file ops flag _treeForce — the same signal means any
            // cached listing is stale, so drop the whole cache.
            if (_treeForce)
                _listCache.Clear();
            DirListing dl;
            if (!_listCache.TryGetValue(_dir, out dl) ||
                Time.unscaledTime > dl.Expires)
            {
                dl = ListDir(_dir);
                dl.Expires = Time.unscaledTime + ListCacheSeconds;
                _listCache[_dir] = dl;
            }
            List<string> dirs = new List<string>(dl.Dirs);
            // Cache holds ALL files; the extension filter is applied per
            // use so a filter change never needs a rescan. Directory-pick
            // mode hides files entirely.
            List<string> files = new List<string>();
            if (!_dirPickMode)
            {
                foreach (string f in dl.Files)
                {
                    if (_filterExts == null)
                    {
                        files.Add(f);
                        continue;
                    }
                    for (int e = 0; e < _filterExts.Length; e++)
                    {
                        if (f.EndsWith("." + _filterExts[e],
                                StringComparison.OrdinalIgnoreCase))
                        {
                            files.Add(f);
                            break;
                        }
                    }
                }
            }
            _jpgSet.Clear();
            _jpgSet.UnionWith(dl.Jpgs);
            long msList = sw.ElapsedMilliseconds;

            if (_sortByDate)
            {
                // Prefetch mtimes once — FileLastWriteTime stats the file on
                // every call, so per-comparison calls are O(n·log n) disk hits.
                Dictionary<string, DateTime> mt =
                    new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                foreach (string d in dirs)
                    mt[d] = FileManager.DirectoryLastWriteTime(d, false, false);
                foreach (string f in files)
                    mt[f] = FileManager.FileLastWriteTime(f, false, false);
                Comparison<string> dcmp = (a, b) => mt[b].CompareTo(mt[a]);
                dirs.Sort(dcmp);
                files.Sort(dcmp);
            }
            else
            {
                dirs.Sort(string.CompareOrdinal);
                files.Sort(string.CompareOrdinal);
            }
            if (search.Length > 0)
            {
                string s = search.ToLowerInvariant();
                dirs.RemoveAll(d => !d.ToLowerInvariant().Contains(s));
                files.RemoveAll(f => !f.ToLowerInvariant().Contains(s));
            }

            // Build the item list only — card GameObjects are created lazily
            // for the visible window by UpdateVisibleCards().
            _items.Clear();
            if (_dir.Length > 0)
                _items.Add(new GridItem
                {
                    Path = _dir.Contains("/")
                        ? _dir.Substring(0, _dir.LastIndexOf('/')) : "",
                    Label = ".. (上级目录)", IsDir = true, IsParent = true
                });
            foreach (string d in dirs)
                _items.Add(new GridItem
                {
                    Path = d,
                    Label = d.Substring(d.LastIndexOf('/') + 1), IsDir = true
                });
            foreach (string f in files)
            {
                string leaf = f.Substring(f.LastIndexOf('/') + 1);
                string shown = leaf;
                if (shown.EndsWith(".vap", StringComparison.OrdinalIgnoreCase))
                    shown = shown.Substring(0, shown.Length - 4);
                if (shown.StartsWith("Preset_",
                        StringComparison.OrdinalIgnoreCase))
                    shown = shown.Substring(7);
                _items.Add(new GridItem
                    { Path = f, Label = shown, FileName = leaf });
            }

            // Content is sized explicitly; the pool only materialises the
            // rows that fit the viewport.
            float contentW = Mathf.Max(100f,
                _scroll.viewport.rect.width - 30f);
            _cols = Mathf.Max(1,
                (int)((contentW - PadL - PadR + ColGap) / ColW));
            int rows = (_items.Count + _cols - 1) / _cols;
            _gridContent.sizeDelta = new Vector2(
                -30f, PadT + rows * RowH + PadB);
            if (keepScroll)
            {
                float maxY = Mathf.Max(0f,
                    _gridContent.rect.height - _scroll.viewport.rect.height);
                _gridContent.anchoredPosition = new Vector2(
                    0f, Mathf.Min(keepY, maxY));
            }
            else
                _scroll.verticalNormalizedPosition = 1f;
            // Sidebar tree follows navigation — rebuilt only when the dir
            // changed or a file op flagged _treeForce, so per-keystroke
            // search refreshes skip the directory scans.
            if (_treeForce || !string.Equals(_dir, _treeBuiltForDir,
                    StringComparison.OrdinalIgnoreCase))
            {
                EnsureAncestorsExpanded(_dir);
                RebuildTree();
                _treeForce = false;
            }
            _layoutDirty = true;
            UpdateVisibleCards();
            PumpThumbs();
            sw.Stop();
            if (sw.ElapsedMilliseconds > 40 &&
                Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogWarning(
                    "Q3 nav perf: " + sw.ElapsedMilliseconds +
                    "ms (list=" + msList + "ms) dir=" + _dir);
        }

        // ---------- virtualised card pool ----------
        private sealed class GridItem
        {
            internal string Path;
            internal string Label;
            internal string FileName;
            internal bool IsDir;
            internal bool IsParent;
            internal string DateText;
            internal bool Queued;
        }

        private sealed class CardView
        {
            internal GameObject Go;
            internal Image Bg;
            internal Text Icon;
            internal RawImage Thumb;
            internal Text Name;
            internal Text Date;
            internal Button RenameBtn;
            internal Button DeleteBtn;
            internal Text DeleteLabel;
            internal Button ConfirmBtn;
            internal Button CancelBtn;
            internal InputField RenameInput;
            internal float DeleteArmUntil;
            internal int ItemIndex = -1;
        }

        private readonly List<GridItem> _items = new List<GridItem>();
        private readonly List<CardView> _pool = new List<CardView>();
        private int _cols = 1;
        private int _lastFirst = -1;
        private int _lastCols = -1;
        private bool _layoutDirty;
        private const float PadL = 10f, PadT = 10f, PadR = 14f, PadB = 10f;
        private const float ColGap = 10f;
        private static readonly float ColW = CardW + ColGap;
        private static readonly float RowH = CardH + ColGap;

        private void UpdateVisibleCards()
        {
            if (_gridContent == null || _scroll == null ||
                _scroll.viewport == null)
                return;
            float scrollY = Mathf.Max(0f, _gridContent.anchoredPosition.y);
            float viewH = _scroll.viewport.rect.height;
            int firstRow = Mathf.Max(0,
                Mathf.FloorToInt((scrollY - PadT) / RowH) - 1);
            int rowCount = Mathf.CeilToInt(viewH / RowH) + 2;
            int first = firstRow * _cols;
            int needed = Mathf.Min(rowCount * _cols, _items.Count - first);
            if (!_layoutDirty && first == _lastFirst && _cols == _lastCols)
                return;
            _layoutDirty = false;
            _lastFirst = first;
            _lastCols = _cols;
            while (_pool.Count < needed)
                _pool.Add(CreateCardView());
            for (int i = 0; i < _pool.Count; i++)
            {
                int idx = first + i;
                if (i < needed && idx < _items.Count)
                    BindCard(_pool[i], idx);
                else
                {
                    _pool[i].ItemIndex = -1;
                    if (_pool[i].Go.activeSelf)
                        _pool[i].Go.SetActive(false);
                }
            }
        }

        private CardView CreateCardView()
        {
            CardView v = new CardView();
            v.Go = NewObj("Card", _gridContent);
            v.Bg = v.Go.AddComponent<Image>();
            Button b = v.Go.AddComponent<Button>();
            b.targetGraphic = v.Bg;
            // VaM's click must not self-fire here: Tick owns the press-drag-
            // release gesture and decides click vs box-select on release.
            b.transition = Selectable.Transition.None;
            b.interactable = false;
            v.Go.AddComponent<VrHoverFeedback>();
            CardView self = v;

            v.Icon = NewText(v.Go.transform, "📁", 48,
                TextAnchor.MiddleCenter);
            v.Icon.raycastTarget = false;
            Place(v.Icon.rectTransform, 0f, 16f, CardW, 120f);

            GameObject io = NewObj("Thumb", v.Go.transform);
            v.Thumb = io.AddComponent<RawImage>();
            v.Thumb.raycastTarget = false;
            Place(io.GetComponent<RectTransform>(), 6f, 8f,
                CardW - 12f, 138f);

            v.Name = NewText(v.Go.transform, "", 18,
                TextAnchor.MiddleCenter);
            v.Name.raycastTarget = false;
            v.Name.horizontalOverflow = HorizontalWrapMode.Wrap;
            v.Name.verticalOverflow = VerticalWrapMode.Truncate;
            v.Name.resizeTextForBestFit = true;
            v.Name.resizeTextMinSize = 12;
            v.Name.resizeTextMaxSize = 18;
            Place(v.Name.rectTransform, 4f, 148f, CardW - 8f, 42f);

            v.Date = NewText(v.Go.transform, "", 16,
                TextAnchor.MiddleCenter);
            v.Date.raycastTarget = false;
            v.Date.color = new Color(0.7f, 0.72f, 0.8f, 1f);
            Place(v.Date.rectTransform, 0f, 190f, CardW, 22f);

            // Per-card ops (native-browser style): rename edits in place on
            // the card; delete is armed once then confirmed by a second tap.
            v.RenameBtn = NewButton(v.Go.transform, "✎");
            v.RenameBtn.GetComponentInChildren<Text>().fontSize = 17;
            Place(v.RenameBtn.GetComponent<RectTransform>(),
                CardW - 78f, 4f, 36f, 30f);
            v.RenameBtn.onClick.AddListener(() => BeginRename(self));

            v.DeleteBtn = NewButton(v.Go.transform, "✕");
            v.DeleteLabel = v.DeleteBtn.GetComponentInChildren<Text>();
            v.DeleteLabel.fontSize = 15;
            v.DeleteBtn.targetGraphic.color = new Color(0.55f, 0.16f, 0.14f, 0.98f);
            Place(v.DeleteBtn.GetComponent<RectTransform>(),
                CardW - 40f, 4f, 36f, 30f);
            v.DeleteBtn.onClick.AddListener(() => OnDeleteClicked(self));

            v.RenameInput = NewInput(v.Go.transform, "");
            v.RenameInput.GetComponentInChildren<Text>().fontSize = 16;
            Place(v.RenameInput.GetComponent<RectTransform>(),
                4f, 146f, CardW - 8f, 46f);
            // No onEndEdit auto-commit: opening the VR keyboard steals focus,
            // which fires onEndEdit and would kill the rename after one key.
            // Commit happens only via the ✓ button (or cancel via ✗).
            v.RenameInput.gameObject.SetActive(false);

            v.ConfirmBtn = NewButton(v.Go.transform, "✓");
            v.ConfirmBtn.GetComponentInChildren<Text>().fontSize = 17;
            v.ConfirmBtn.targetGraphic.color =
                new Color(0.14f, 0.45f, 0.20f, 0.98f);
            Place(v.ConfirmBtn.GetComponent<RectTransform>(),
                CardW - 78f, 4f, 36f, 30f);
            v.ConfirmBtn.onClick.AddListener(() => CommitRename(self));
            v.ConfirmBtn.gameObject.SetActive(false);

            v.CancelBtn = NewButton(v.Go.transform, "✗");
            v.CancelBtn.GetComponentInChildren<Text>().fontSize = 15;
            Place(v.CancelBtn.GetComponent<RectTransform>(),
                CardW - 40f, 4f, 36f, 30f);
            v.CancelBtn.onClick.AddListener(() => CancelRename(self));
            v.CancelBtn.gameObject.SetActive(false);
            return v;
        }

        private CardView _renameView;

        private void BeginRename(CardView v)
        {
            if (v.ItemIndex < 0 || v.ItemIndex >= _items.Count)
                return;
            GridItem it = _items[v.ItemIndex];
            if (_renameView != null && _renameView != v)
                CancelRename(_renameView);
            _renameView = v;
            v.Name.gameObject.SetActive(false);
            v.RenameBtn.gameObject.SetActive(false);
            v.DeleteBtn.gameObject.SetActive(false);
            v.ConfirmBtn.gameObject.SetActive(true);
            v.CancelBtn.gameObject.SetActive(true);
            v.RenameInput.gameObject.SetActive(true);
            v.RenameInput.text = it.IsDir
                ? it.Path.Substring(it.Path.LastIndexOf('/') + 1)
                : it.FileName;
            v.RenameInput.ActivateInputField();
            v.RenameInput.Select();
            // Point the VR keyboard's text bridge at this field — the ✎
            // button click never goes through InputField.OnPointerClick, so
            // without this the bridge still targets the previous field
            // (search box) and keystrokes land there or nowhere.
            VrTextInputBridge.Select(v.RenameInput);
        }

        private void CancelRename(CardView v)
        {
            if (v == null)
                return;
            if (_renameView == v)
                _renameView = null;
            v.RenameInput.gameObject.SetActive(false);
            v.ConfirmBtn.gameObject.SetActive(false);
            v.CancelBtn.gameObject.SetActive(false);
            v.Name.gameObject.SetActive(true);
            v.RenameBtn.gameObject.SetActive(true);
            v.DeleteBtn.gameObject.SetActive(true);
        }

        private void CommitRename(CardView v)
        {
            if (v.ItemIndex < 0 || v.ItemIndex >= _items.Count)
            {
                CancelRename(v);
                return;
            }
            GridItem it = _items[v.ItemIndex];
            string nn = v.RenameInput.text.Trim()
                .Replace('/', '-').Replace('\\', '-');
            string oldLeaf = it.IsDir
                ? it.Path.Substring(it.Path.LastIndexOf('/') + 1)
                : it.FileName;
            CancelRename(v);
            if (nn.Length == 0 || nn == oldLeaf)
                return;
            if (!it.IsDir &&
                !nn.EndsWith(".vap", StringComparison.OrdinalIgnoreCase))
                nn += ".vap";
            string dst = (_dir.Length > 0 ? _dir + "/" : "") + nn;
            try
            {
                if (it.IsDir)
                    FileManager.MoveDirectory(it.Path, dst);
                else
                {
                    FileManager.MoveFile(it.Path, dst, false);
                    MoveSidecarJpg(it.Path, dst);
                }
                SetStatus("已重命名: " + nn);
            }
            catch (Exception ex)
            {
                SetStatus("重命名失败: " + ex.Message);
            }
            _treeForce = true;
            RefreshGrid(true);
        }

        private void OnDeleteClicked(CardView v)
        {
            if (v.ItemIndex < 0 || v.ItemIndex >= _items.Count)
                return;
            GridItem it = _items[v.ItemIndex];
            if (Time.unscaledTime < v.DeleteArmUntil)
            {
                try
                {
                    if (it.IsDir)
                        FileManager.DeleteDirectory(it.Path, false);
                    else
                        FileManager.DeleteFile(it.Path);
                    SetStatus("已删除: " + it.Label);
                }
                catch (Exception ex)
                {
                    SetStatus("删除失败: " + ex.Message);
                }
                _treeForce = true;
                RefreshGrid(true);
            }
            else
            {
                v.DeleteArmUntil = Time.unscaledTime + 4f;
                v.DeleteLabel.text = "确认?";
                SetStatus("再点一次 ✕ 确认删除: " + it.Label);
            }
        }

        private void BindCard(CardView v, int idx)
        {
            GridItem it = _items[idx];
            v.ItemIndex = idx;
            v.Name.text = it.Label;
            v.Bg.color = _selPaths.Contains(it.Path)
                ? new Color(0.24f, 0.45f, 0.80f, 1f)
                : it.IsDir
                    ? new Color(0.20f, 0.30f, 0.24f, 1f)
                    : new Color(0.16f, 0.18f, 0.24f, 1f);
            v.Icon.text = it.IsDir ? "📁" : "📄";
            v.Date.text = it.DateText ?? "";

            CancelRename(v);
            // Per-card ops are meaningless on the ".." parent card.
            bool ops = !it.IsParent;
            if (v.RenameBtn.gameObject.activeSelf != ops)
                v.RenameBtn.gameObject.SetActive(ops);
            if (v.DeleteBtn.gameObject.activeSelf != ops)
                v.DeleteBtn.gameObject.SetActive(ops);
            v.DeleteArmUntil = 0f;
            v.DeleteLabel.text = "✕";

            bool showThumb = false;
            if (!it.IsDir)
            {
                Texture2D cached;
                bool known = _thumbCache.TryGetValue(it.Path, out cached);
                if (known && cached != null)
                {
                    v.Thumb.texture = cached;
                    v.Thumb.color = Color.white;
                    showThumb = true;
                }
                else if (!known)
                {
                    string jpg = it.Path.Substring(0, it.Path.Length -
                        Path.GetExtension(it.Path).Length) + ".jpg";
                    if (_jpgSet.Contains(jpg))
                    {
                        v.Thumb.texture = null;
                        v.Thumb.color = new Color(0.12f, 0.13f, 0.17f, 1f);
                        showThumb = true;
                    }
                }
            }
            if (v.Thumb.gameObject.activeSelf != showThumb)
                v.Thumb.gameObject.SetActive(showThumb);

            // Position in the manually laid-out grid.
            int row = idx / _cols;
            int col = idx % _cols;
            RectTransform rt = v.Go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(
                PadL + col * ColW, -(PadT + row * RowH));
            rt.sizeDelta = new Vector2(CardW, CardH);
            if (!v.Go.activeSelf)
                v.Go.SetActive(true);

            // Queue lazy work (mtime + jpg decode) once per item; kick the
            // pump here too — jobs arriving after the initial drain (scrolling
            // to new rows) must restart it or thumbs never load.
            if (!it.IsDir && !it.Queued)
            {
                it.Queued = true;
                _thumbJobs.Add(new ThumbJob
                    { Path = it.Path, ItemIndex = idx, Gen = _gridGen });
                PumpThumbs();
            }
        }

        private void OnCardClicked(CardView v)
        {
            if (v.ItemIndex < 0 || v.ItemIndex >= _items.Count)
                return;
            if (Time.unscaledTime < _clickSuppressUntil)
                return;
            GridItem it = _items[v.ItemIndex];
            if (it.IsDir)
            {
                if (_pickDest)
                {
                    if (_pickCopy)
                        DoBatchCopy(it.Path);
                    else
                        DoBatchMove(it.Path);
                    return;
                }
                NavTo(it.Path);
            }
            else if (_saveMode)
            {
                _selectedPath = it.Path;
                _selectedCardName = it.FileName;
                if (_fileNameInput != null)
                    _fileNameInput.text = it.FileName;
                SetStatus("将覆盖: " + it.FileName);
            }
            else
            {
                Commit(it.Path);
            }
        }

        // ---------- drag box-select + batch ops ----------
        // Drag capture lives on GridContent (deeper than the ScrollRect, so
        // box-select wins over drag-scroll; wheel/scrollbar still scroll).
        private sealed class GridDragHandler : MonoBehaviour,
            IPointerDownHandler, IPointerUpHandler, IBeginDragHandler,
            IDragHandler, IEndDragHandler
        {
            internal VrPresetBrowser Owner;
            public void OnPointerDown(PointerEventData e)
            { if (Owner != null) Owner.GridPointerDown(e); }
            public void OnPointerUp(PointerEventData e)
            { if (Owner != null) Owner.GridPointerUp(e); }
            public void OnBeginDrag(PointerEventData e)
            { if (Owner != null) Owner.GridBeginDrag(e); }
            public void OnDrag(PointerEventData e)
            { if (Owner != null) Owner.GridDragMove(e); }
            public void OnEndDrag(PointerEventData e)
            { if (Owner != null) Owner.GridDragEnd(e); }
        }

        // Intersect the live pointer ray with the grid plane — works under
        // VaM's VR pointer where Unity drag events never reach plain UI.
        private bool TryRayLocal(out Vector2 p)
        {
            p = Vector2.zero;
            if (_gridContent == null)
                return false;
            Ray ray;
            if (!VrPointerPresentation.TryGetPointerRay(out ray))
                return false;
            Vector3 n = _gridContent.forward;
            Plane plane = new Plane(-n, _gridContent.position);
            float t;
            if (!plane.Raycast(ray, out t))
            {
                plane = new Plane(n, _gridContent.position);
                if (!plane.Raycast(ray, out t))
                    return false;
            }
            if (t <= 0f || t > 20f)
                return false;
            p = _gridContent.InverseTransformPoint(ray.GetPoint(t));
            return true;
        }

        private bool TryLocalPoint(PointerEventData e, out Vector2 p)
        {
            p = Vector2.zero;
            if (_gridContent == null)
                return false;
            // Raycast world position is reliable under VaM's VR pointer where
            // event cameras can be null.
            Vector3 w = e.pointerCurrentRaycast.worldPosition;
            if (w == Vector3.zero && e.pressEventCamera != null)
                return RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _gridContent, e.position, e.pressEventCamera, out p);
            if (w == Vector3.zero)
                return false;
            p = _gridContent.InverseTransformPoint(w);
            return true;
        }

        private bool _pressActive;
        private bool _trigHeldPrev;
        private float _clickSuppressUntil;

        // VaM's own pointer hit info: the object under the UI ray and the
        // laser cursor sitting on it. Reliable where the reference-camera
        // ray and Unity pointer events aren't.
        private static Vector3 CursorPos(bool right)
        {
            RectTransform c = VrPointerPresentation.CurrentCursor(right);
            return c != null ? c.position : Vector3.zero;
        }

        // True surface point on the grid plane: the laser cursor can float
        // off the surface, so the hand→cursor ray is intersected with the
        // content plane instead of trusting the cursor position verbatim.
        // Falls back to the hand's forward ray when the cursor is gone
        // (pointer off any UI mid-drag), keeping drags alive at the edges.
        // ---------- sidebar: favourites + lazy directory tree ----------

        // Rows in the tree/favourites lists carry the dir they represent so
        // a drag-move release can resolve the drop target from the look ray.
        private sealed class DirDropRef : MonoBehaviour
        {
            internal string Dir;
        }

        private void BuildSidebar(RectTransform root)
        {
            GameObject side = NewObj("Sidebar", root);
            _sidebarRect = side.GetComponent<RectTransform>();
            _sidebarRect.anchorMin = new Vector2(0f, 0f);
            _sidebarRect.anchorMax = new Vector2(0f, 1f);
            _sidebarRect.offsetMin = new Vector2(8f, 120f);
            _sidebarRect.offsetMax = new Vector2(8f + SidebarW, -112f);
            Image bg = side.AddComponent<Image>();
            bg.color = new Color(0.09f, 0.10f, 0.13f, 1f);

            // Collapse tab stays visible when the body hides — it is the
            // only way back without reopening the browser.
            Button tab = NewButton(side.transform, "◀");
            _sideTabText = tab.GetComponentInChildren<Text>();
            _sideTabText.fontSize = 18;
            RectTransform tr = tab.GetComponent<RectTransform>();
            tr.anchorMin = new Vector2(0f, 0f);
            tr.anchorMax = new Vector2(0f, 1f);
            tr.offsetMin = new Vector2(2f, 4f);
            tr.offsetMax = new Vector2(28f, -4f);
            tab.targetGraphic.color = new Color(0.12f, 0.14f, 0.18f, 1f);
            tab.onClick.AddListener(() =>
            {
                _sidebarOpen = !_sidebarOpen;
                ApplySidebar();
            });

            _sideBody = NewObj("SideBody", side.transform);
            RectTransform br = _sideBody.GetComponent<RectTransform>();
            br.anchorMin = Vector2.zero;
            br.anchorMax = Vector2.one;
            br.offsetMin = new Vector2(32f, 0f);
            br.offsetMax = Vector2.zero;

            Text favH = NewText(_sideBody.transform, "★ 收藏夹", 20,
                TextAnchor.MiddleLeft);
            favH.raycastTarget = false;
            Place(favH.rectTransform, 8f, 6f, 120f, 32f);
            Button addFav = NewButton(_sideBody.transform, "＋收藏当前");
            addFav.GetComponentInChildren<Text>().fontSize = 17;
            PlaceRight(addFav.GetComponent<RectTransform>(), 6f, 6f, 118f, 32f);
            addFav.onClick.AddListener(AddCurrentFav);

            GameObject fv = NewObj("FavView", _sideBody.transform);
            RectTransform fvr = fv.GetComponent<RectTransform>();
            Place(fvr, 4f, 44f, SidebarW - 44f, 200f);
            fv.AddComponent<Image>().color =
                new Color(0.06f, 0.07f, 0.10f, 1f);
            fv.AddComponent<RectMask2D>();
            _favScroll = fv.AddComponent<ScrollRect>();
            SetupSideScroll(_favScroll);
            _favContent = MakeListContent(fv.transform);
            _favScroll.content = _favContent;
            _favScroll.viewport = fvr;

            Text treeH = NewText(_sideBody.transform, "📂 目录树", 20,
                TextAnchor.MiddleLeft);
            treeH.raycastTarget = false;
            Place(treeH.rectTransform, 8f, 252f, 140f, 32f);
            Button reload = NewButton(_sideBody.transform, "⟳");
            reload.GetComponentInChildren<Text>().fontSize = 18;
            PlaceRight(reload.GetComponent<RectTransform>(), 6f, 252f,
                44f, 32f);
            reload.onClick.AddListener(() =>
            {
                // Full manual refresh: _treeForce drops the listing cache
                // and the tree kid cache, so the grid + tree both rescan
                // disk — the escape hatch for changes made outside the
                // browser while it was open.
                _treeForce = true;
                RebuildTree();
                RebuildFavs();
                RefreshGrid(true);
            });

            GameObject tv = NewObj("TreeView", _sideBody.transform);
            RectTransform tvr = tv.GetComponent<RectTransform>();
            Anchor(tvr, 4f, 4f, 4f, 288f);
            tv.AddComponent<Image>().color =
                new Color(0.06f, 0.07f, 0.10f, 1f);
            tv.AddComponent<RectMask2D>();
            _treeScroll = tv.AddComponent<ScrollRect>();
            SetupSideScroll(_treeScroll);
            _treeContent = MakeListContent(tv.transform);
            _treeScroll.content = _treeContent;
            _treeScroll.viewport = tvr;

            LoadFavs();
            RebuildFavs();
        }

        private static void SetupSideScroll(ScrollRect s)
        {
            s.horizontal = false;
            s.movementType = ScrollRect.MovementType.Clamped;
            s.scrollSensitivity = 30f;
            s.inertia = false;
        }

        // No layout group: rows are fixed-height and positioned manually —
        // a VerticalLayoutGroup re-lays out on every child add, which made
        // tree rebuilds quadratic and was a big part of the stutter.
        private RectTransform MakeListContent(Transform parent)
        {
            GameObject c = NewObj("Content", parent);
            RectTransform r = c.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0f, 1f);
            r.anchorMax = new Vector2(1f, 1f);
            r.pivot = new Vector2(0.5f, 1f);
            r.anchoredPosition = Vector2.zero;
            return r;
        }

        // Fixed-height row stretched to content width, stacked from the top.
        private static void PlaceListRow(RectTransform r, int index,
            float rowH, float gap)
        {
            r.anchorMin = new Vector2(0f, 1f);
            r.anchorMax = new Vector2(1f, 1f);
            r.pivot = new Vector2(0.5f, 1f);
            r.sizeDelta = new Vector2(0f, rowH);
            r.anchoredPosition = new Vector2(0f, -index * (rowH + gap));
        }

        private void ApplySidebar()
        {
            if (_sideBody != null && _sideBody.activeSelf != _sidebarOpen)
                _sideBody.SetActive(_sidebarOpen);
            if (_sideTabText != null)
                _sideTabText.text = _sidebarOpen ? "◀" : "▶";
            // Shrink the sidebar rect itself when collapsed — the full-width
            // background otherwise keeps covering (and truncating) the
            // grid's leftmost card column.
            if (_sidebarRect != null)
                _sidebarRect.offsetMax = new Vector2(
                    8f + (_sidebarOpen ? SidebarW : 32f), -112f);
            if (_gridViewport != null)
                _gridViewport.offsetMin = new Vector2(
                    _sidebarOpen ? SidebarW + 12f : 44f, 120f);
            if (_tabs.Count > 0)
                RefreshGrid(true);
        }

        // ---------- favourites ----------
        private static string FavsPath()
        {
            return Path.Combine(BepInEx.Paths.ConfigPath, FavsFileName);
        }

        private void LoadFavs()
        {
            _favs.Clear();
            try
            {
                string f = FavsPath();
                if (!File.Exists(f))
                    return;
                foreach (string line in File.ReadAllLines(f))
                {
                    string d = line.Trim().Replace('\\', '/').TrimEnd('/');
                    if (d.Length > 0 && ValidDir(d) &&
                        !_favs.Exists(x => string.Equals(x, d,
                            StringComparison.OrdinalIgnoreCase)))
                        _favs.Add(d);
                }
            }
            catch (Exception ex)
            {
                LogErr("Q3 favs load: " + ex.Message);
            }
        }

        private void SaveFavs()
        {
            try
            {
                File.WriteAllLines(FavsPath(), _favs.ToArray());
            }
            catch (Exception ex)
            {
                LogErr("Q3 favs save: " + ex.Message);
            }
        }

        private void AddCurrentFav()
        {
            if (_dir.Length == 0)
            {
                SetStatus("根目录无需收藏");
                return;
            }
            if (_favs.Exists(x => string.Equals(x, _dir,
                    StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus("已在收藏夹: " + _dir);
                return;
            }
            _favs.Add(_dir);
            SaveFavs();
            RebuildFavs();
            SetStatus("已收藏: " + _dir);
        }

        private void RebuildFavs()
        {
            if (_favContent == null)
                return;
            for (int i = _favContent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(
                    _favContent.GetChild(i).gameObject);
            int row = 0;
            if (_favs.Count == 0)
            {
                Text e = NewText(_favContent, "（点上方 ＋收藏当前）", 17,
                    TextAnchor.MiddleCenter);
                e.color = new Color(0.6f, 0.63f, 0.7f, 1f);
                e.raycastTarget = false;
                PlaceListRow(e.rectTransform, row++, 34f, 2f);
            }
            foreach (string fav in _favs)
            {
                string dir = fav;
                GameObject rowGo = NewObj("FavRow", _favContent);
                PlaceListRow(rowGo.GetComponent<RectTransform>(),
                    row++, 38f, 2f);
                rowGo.AddComponent<DirDropRef>().Dir = dir;

                Button b = NewButton(rowGo.transform, "");
                Text t = b.GetComponentInChildren<Text>();
                string leaf = dir.TrimEnd('/');
                leaf = leaf.Length == 0 ? "(根目录)"
                    : leaf.Substring(leaf.LastIndexOf('/') + 1);
                t.text = "★ " + leaf;
                t.fontSize = 18;
                t.alignment = TextAnchor.MiddleLeft;
                // Long leaf names used to wrap and get clipped by the row —
                // shrink-to-fit keeps them on one readable line.
                t.resizeTextForBestFit = true;
                t.resizeTextMinSize = 12;
                t.resizeTextMaxSize = 18;
                t.verticalOverflow = VerticalWrapMode.Truncate;
                SetInsets(t.rectTransform, 10f, 0f);
                RectTransform brt = b.GetComponent<RectTransform>();
                brt.anchorMin = Vector2.zero;
                brt.anchorMax = Vector2.one;
                brt.offsetMin = Vector2.zero;
                brt.offsetMax = new Vector2(-42f, 0f);
                b.targetGraphic.color = new Color(0.14f, 0.18f, 0.26f, 1f);
                b.onClick.AddListener(() =>
                {
                    if (Time.unscaledTime < _clickSuppressUntil)
                        return;
                    if (_pickDest)
                    {
                        if (_pickCopy) DoBatchCopy(dir);
                        else DoBatchMove(dir);
                        return;
                    }
                    NavTo(dir);
                });

                Button x = NewButton(rowGo.transform, "✕");
                x.GetComponentInChildren<Text>().fontSize = 15;
                x.targetGraphic.color = new Color(0.45f, 0.16f, 0.14f, 0.98f);
                RectTransform xr = x.GetComponent<RectTransform>();
                xr.anchorMin = new Vector2(1f, 0f);
                xr.anchorMax = new Vector2(1f, 1f);
                xr.offsetMin = new Vector2(-38f, 2f);
                xr.offsetMax = new Vector2(-2f, -2f);
                x.onClick.AddListener(() =>
                {
                    _favs.RemoveAll(f => string.Equals(f, dir,
                        StringComparison.OrdinalIgnoreCase));
                    SaveFavs();
                    RebuildFavs();
                });
            }
            _favContent.sizeDelta = new Vector2(0f, row * 40f);
        }

        // ---------- directory tree ----------
        // Every ancestor of _dir stays expanded so the current location is
        // always visible in the tree; _dir itself expands only when its row
        // is tapped (collapse must stick across refreshes).
        private void EnsureAncestorsExpanded(string dir)
        {
            if (string.IsNullOrEmpty(dir))
                return;
            string d = dir.Replace('\\', '/').TrimEnd('/');
            int i = d.IndexOf('/');
            while (i > 0)
            {
                _treeExpanded.Add(d.Substring(0, i));
                i = d.IndexOf('/', i + 1);
            }
        }

        private void RebuildTree()
        {
            if (_treeContent == null)
                return;
            // A forced rebuild means dirs on disk may have changed — drop
            // the cached kid listings so this pass rescans them.
            if (_treeForce)
            {
                _treeKids.Clear();
                _treeForce = false;
            }
            _treeBuiltForDir = _dir;
            _treeRowUsed = 0;
            _treeCurrentRow = -1;
            try
            {
                int n = 0;
                n = AddTreeRows("Custom", 0, n);
                n = AddTreeRows("Saves", 0, n);
                _treeContent.sizeDelta = new Vector2(0f, n * 36f);
            }
            catch (Exception ex)
            {
                LogErr("Q3 tree: " + ex.Message);
            }
            // Deactivate surplus pool rows — no Destroy/Instantiate churn
            // per expand/collapse/navigate.
            for (int i = _treeRowUsed; i < _treePool.Count; i++)
                if (_treePool[i].Go.activeSelf)
                    _treePool[i].Go.SetActive(false);
            CenterTreeOnCurrent();
        }

        // Scroll the tree so the highlighted current-dir row sits in the
        // middle of the viewport instead of forcing a manual drag to find
        // it. Recorded per rebuild in _treeCurrentRow.
        private void CenterTreeOnCurrent()
        {
            if (_treeScroll == null || _treeContent == null ||
                _treeCurrentRow < 0)
                return;
            float viewH = _treeScroll.viewport != null
                ? _treeScroll.viewport.rect.height : 0f;
            if (viewH <= 0f)
                return;
            float contentH = _treeContent.sizeDelta.y;
            float target = _treeCurrentRow * 36f + 17f - viewH * 0.5f;
            target = Mathf.Clamp(target, 0f,
                Mathf.Max(0f, contentH - viewH));
            _treeContent.anchoredPosition = new Vector2(
                _treeContent.anchoredPosition.x, target);
        }

        // Kid listings are cached in _treeKids — without the cache every
        // rebuild rescanned each expanded dir, and rebuilds happen on every
        // navigation, so the scans stacked up into visible hitches.
        private List<string> TreeKids(string dir)
        {
            List<string> kids;
            if (_treeKids.TryGetValue(dir, out kids))
                return kids;
            kids = new List<string>();
            try
            {
                foreach (string k in
                    FileManager.GetDirectories(dir, "*", false))
                {
                    string kn = k.Replace('\\', '/').TrimEnd('/');
                    string leaf = kn.Substring(kn.LastIndexOf('/') + 1);
                    if (!leaf.StartsWith("."))
                        kids.Add(kn);
                }
            }
            catch { }
            kids.Sort(string.CompareOrdinal);
            _treeKids[dir] = kids;
            return kids;
        }

        private int AddTreeRows(string dir, int depth, int row)
        {
            if (depth > 9)
                return row;
            List<string> kids = TreeKids(dir);
            bool expanded = _treeExpanded.Contains(dir);
            BindTreeRow(dir, depth, kids.Count > 0, expanded, row++);
            if (expanded)
                foreach (string k in kids)
                    row = AddTreeRows(k, depth + 1, row);
            return row;
        }

        // Rows are pooled like the grid cards — the tree used to Destroy +
        // Instantiate every row GameObject on each rebuild, which was the
        // visible hitch on expand/collapse/navigate.
        private sealed class TreeRowView
        {
            public GameObject Go;
            public Button Btn;
            public Text Txt;
            public DirDropRef Drop;
            public string Dir;
            public bool HasKids;
        }
        private readonly List<TreeRowView> _treePool =
            new List<TreeRowView>();
        private int _treeRowUsed;
        private int _treeCurrentRow = -1;

        private TreeRowView CreateTreeRowView()
        {
            TreeRowView v = new TreeRowView();
            v.Go = NewObj("TreeRow", _treeContent);
            v.Drop = v.Go.AddComponent<DirDropRef>();
            v.Btn = NewButton(v.Go.transform, "");
            v.Txt = v.Btn.GetComponentInChildren<Text>();
            v.Txt.fontSize = 18;
            v.Txt.alignment = TextAnchor.MiddleLeft;
            // Deep levels used to push the label past the row edge — shrink
            // to fit instead of clipping mid-character.
            v.Txt.resizeTextForBestFit = true;
            v.Txt.resizeTextMinSize = 12;
            v.Txt.resizeTextMaxSize = 18;
            v.Txt.verticalOverflow = VerticalWrapMode.Truncate;
            RectTransform brt = v.Btn.GetComponent<RectTransform>();
            brt.anchorMin = Vector2.zero;
            brt.anchorMax = Vector2.one;
            brt.offsetMin = Vector2.zero;
            brt.offsetMax = Vector2.zero;
            // Listener reads v.Dir/v.HasKids at click time — bound once,
            // never re-allocated on rebind.
            v.Btn.onClick.AddListener(() =>
            {
                if (Time.unscaledTime < _clickSuppressUntil)
                    return;
                TreeRowClicked(v.Dir, v.HasKids);
            });
            return v;
        }

        private void BindTreeRow(string dir, int depth,
            bool hasKids, bool expanded, int row)
        {
            TreeRowView v;
            if (_treeRowUsed < _treePool.Count)
                v = _treePool[_treeRowUsed];
            else
            {
                v = CreateTreeRowView();
                _treePool.Add(v);
            }
            _treeRowUsed++;
            v.Dir = dir;
            v.HasKids = hasKids;
            v.Drop.Dir = dir;
            string leaf = dir.Substring(dir.LastIndexOf('/') + 1);
            v.Txt.text = (hasKids ? (expanded ? "▾ " : "▸ ") : "   ") + leaf;
            SetInsets(v.Txt.rectTransform, 6f + depth * 20f, 0f);
            v.Btn.targetGraphic.color = string.Equals(dir, _dir,
                    StringComparison.OrdinalIgnoreCase)
                ? new Color(0.22f, 0.40f, 0.70f, 1f)
                : new Color(0.11f, 0.13f, 0.17f, 1f);
            PlaceListRow(v.Go.GetComponent<RectTransform>(), row, 34f, 2f);
            if (string.Equals(dir, _dir, StringComparison.OrdinalIgnoreCase))
                _treeCurrentRow = row;
            if (!v.Go.activeSelf)
                v.Go.SetActive(true);
        }

        private void TreeRowClicked(string dir, bool hasKids)
        {
            if (_pickDest)
            {
                if (_pickCopy) DoBatchCopy(dir);
                else DoBatchMove(dir);
                return;
            }
            if (string.Equals(dir, _dir,
                    StringComparison.OrdinalIgnoreCase))
            {
                // Tapping the current dir's row toggles it — the only
                // collapse control, since navigation re-expands ancestors.
                if (hasKids)
                {
                    if (_treeExpanded.Contains(dir))
                        _treeExpanded.Remove(dir);
                    else
                        _treeExpanded.Add(dir);
                    RebuildTree();
                }
                return;
            }
            _treeExpanded.Add(dir);   // drill-down: expand on navigate
            NavTo(dir);
        }

        // ---------- drag-move ghost + drop targets ----------
        private void BuildDragGhost(RectTransform root)
        {
            _dragGhost = NewObj("DragGhost", root);
            Image gi = _dragGhost.AddComponent<Image>();
            gi.color = new Color(0.10f, 0.30f, 0.14f, 0.95f);
            gi.raycastTarget = false;
            RectTransform gr = _dragGhost.GetComponent<RectTransform>();
            gr.pivot = new Vector2(0f, 0.5f);
            gr.sizeDelta = new Vector2(420f, 46f);
            _ghostText = NewText(_dragGhost.transform, "", 22,
                TextAnchor.MiddleLeft);
            _ghostText.raycastTarget = false;
            SetInsets(_ghostText.rectTransform, 14f, 0f);
            _dragGhost.SetActive(false);
        }

        private string DragDestAt(Vector2 cur, bool onViewport,
            GameObject lookT)
        {
            if (onViewport)
            {
                int idx = ItemIndexAt(cur);
                if (idx >= 0 && idx < _items.Count && _items[idx].IsDir)
                    return _items[idx].Path;
            }
            if (lookT != null)
            {
                DirDropRef r = lookT.GetComponentInParent<DirDropRef>();
                if (r != null && ValidDir(r.Dir))
                    return r.Dir;
            }
            return null;
        }

        private void UpdateDragGhost(Vector2 cur, Vector3 curW,
            bool onViewport, GameObject lookT)
        {
            string dest = DragDestAt(cur, onViewport, lookT);
            if (_dragGhost == null)
                return;
            _dragGhost.transform.position = curW +
                _canvas.transform.right * 0.05f +
                _canvas.transform.up * 0.03f;
            string leaf = dest == null ? null
                : dest.Length == 0 ? "(根目录)"
                : dest.Substring(dest.LastIndexOf('/') + 1);
            _ghostText.text = "⇒ 移动 " + _selPaths.Count + " 项" +
                (leaf != null ? " → " + leaf : "");
        }

        private bool SurfacePoint(bool right, out Vector2 local,
            out Vector3 world)
        {
            local = Vector2.zero;
            world = Vector3.zero;
            if (_gridContent == null)
                return false;
            SuperController sc = SuperController.singleton;
            Transform hand = VrPointerPresentation.MotionController(sc, right);
            Vector3 cursorW = CursorPos(right);
            Vector3 org, dir;
            if (hand != null && cursorW != Vector3.zero)
            {
                org = hand.position;
                dir = cursorW - org;
                if (dir.sqrMagnitude < 1e-6f)
                    return false;
                dir.Normalize();
            }
            else if (hand != null)
            {
                org = hand.position;
                dir = hand.forward;
            }
            else if (cursorW != Vector3.zero)
            {
                world = cursorW;
                local = _gridContent.InverseTransformPoint(world);
                local.x += _gridContent.rect.width * 0.5f;
                return true;
            }
            else
                return false;
            Vector3 n = _gridContent.forward;
            Plane plane = new Plane(-n, _gridContent.position);
            float t;
            if (!plane.Raycast(new Ray(org, dir), out t))
            {
                plane = new Plane(n, _gridContent.position);
                if (!plane.Raycast(new Ray(org, dir), out t))
                    return false;
            }
            if (t <= 0f || t > 20f)
                return false;
            world = new Ray(org, dir).GetPoint(t);
            local = _gridContent.InverseTransformPoint(world);
            // Content pivot is (0.5, 1) — local x is centred, while the grid
            // layout math (ItemIndexAt/UpdateBoxSelection/ApplySelRect) uses
            // a left-edge origin. Shift it here so every consumer agrees.
            local.x += _gridContent.rect.width * 0.5f;
            return true;
        }

        // Card ops controls are named Btn_* / Input — a press on them belongs
        // to the button's own click, not the selection gesture.
        private static bool IsCardOps(GameObject t)
        {
            if (t == null)
                return false;
            string n = t.name;
            return n.StartsWith("Btn_") || n.StartsWith("Input");
        }

        private int ItemIndexAt(Vector2 p)
        {
            if (p.x < PadL || p.y > -PadT)
                return -1;
            int col = Mathf.FloorToInt((p.x - PadL) / ColW);
            int row = Mathf.FloorToInt((-p.y - PadT) / RowH);
            if (col < 0 || col >= _cols || row < 0)
                return -1;
            int idx = row * _cols + col;
            if (idx >= _items.Count)
                return -1;
            float cx0 = PadL + col * ColW;
            float cy1 = -(PadT + row * RowH);
            if (p.x > cx0 + CardW || p.y < cy1 - CardH)
                return -1;
            return idx;
        }

        private CardView FindView(int idx)
        {
            foreach (CardView v in _pool)
                if (v.ItemIndex == idx && v.Go.activeSelf)
                    return v;
            return null;
        }

        // VaM DOES dispatch Unity pointer events here, but only
        // intermittently — and a stray pointerUp racing the trigger-release
        // frame used to wipe _pressActive/_dragging before Tick resolved
        // the click (blank-tap-clear dead, clicks eaten). The manual
        // trigger-poll path in Tick is the single owner of gesture state;
        // these callbacks only feed it a fallback press start and never
        // resolve or cancel anything.
        internal void GridPointerDown(PointerEventData e)
        {
            if (_pressActive)
                return;
            Vector2 p;
            // EventData raycast info is unreliable under VaM's pointer —
            // fall back to intersecting the live pointer ray ourselves.
            if (!TryLocalPoint(e, out p) && !TryRayLocal(out p))
                return;
            _dragStart = p;
            _pressIdx = ItemIndexAt(p);
            _pressActive = true;
            LogErr("Q3 browser grid press @" + p);
        }

        internal void GridPointerUp(PointerEventData e)
        {
            // Intentionally inert — release resolution lives in Tick.
        }

        internal void GridBeginDrag(PointerEventData e)
        {
            // Intentionally inert — Tick owns drag activation.
        }

        internal void GridDragMove(PointerEventData e)
        {
            // Intentionally inert — Tick owns drag tracking.
        }

        internal void GridDragEnd(PointerEventData e)
        {
            // Intentionally inert — Tick owns drag release.
        }

        private void ApplySelRect(Vector2 a, Vector2 b)
        {
            RectTransform rt = _selRect.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(
                Mathf.Min(a.x, b.x), Mathf.Max(a.y, b.y));
            rt.sizeDelta = new Vector2(
                Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
        }

        private void UpdateBoxSelection(Vector2 a, Vector2 b)
        {
            float x0 = Mathf.Min(a.x, b.x), x1 = Mathf.Max(a.x, b.x);
            float y0 = Mathf.Min(a.y, b.y), y1 = Mathf.Max(a.y, b.y);
            // A real box drag must swallow the release-click so letting go
            // over a card doesn't open it.
            if (x1 - x0 > 20f || y1 - y0 > 20f)
                _clickSuppressUntil = Time.unscaledTime + 0.3f;
            for (int i = 0; i < _items.Count; i++)
            {
                GridItem it = _items[i];
                if (it.IsParent)
                    continue;
                int row = i / _cols, col = i % _cols;
                float cx0 = PadL + col * ColW, cx1 = cx0 + CardW;
                float cy1 = -(PadT + row * RowH), cy0 = cy1 - CardH;
                bool hit = cx0 < x1 && cx1 > x0 && cy0 < y1 && cy1 > y0;
                if (hit)
                    _selPaths.Add(it.Path);
                else
                    _selPaths.Remove(it.Path);
            }
            RefreshSelectionVisuals();
        }

        private void RefreshSelectionVisuals()
        {
            foreach (CardView v in _pool)
            {
                if (v.ItemIndex < 0 || !v.Go.activeSelf)
                    continue;
                bool sel = _selPaths.Contains(_items[v.ItemIndex].Path);
                if (sel)
                    v.Bg.color = new Color(0.24f, 0.45f, 0.80f, 1f);
                else
                    v.Bg.color = _items[v.ItemIndex].IsDir
                        ? new Color(0.20f, 0.30f, 0.24f, 1f)
                        : new Color(0.16f, 0.18f, 0.24f, 1f);
            }
        }

        private void UpdateSelBar()
        {
            if (_selBar == null)
                return;
            bool on = _selPaths.Count > 0 || _pickDest;
            if (_selBar.activeSelf != on)
                _selBar.SetActive(on);
            if (_selDeleteBtn != null)
                _selDeleteBtn.GetComponentInChildren<Text>().text = "删除所选";
            if (_selCountText != null)
                _selCountText.text = _pickDest
                    ? (_pickCopy ? "复制:点击目标文件夹…"
                                 : "移动:点击目标文件夹…")
                    : "已选 " + _selPaths.Count + " 项";
        }

        private void BuildSelBar(RectTransform root)
        {
            _selBar = NewObj("SelBar", root);
            // Floating toolbar over the grid's top edge.
            Anchor(_selBar.GetComponent<RectTransform>(),
                8f, CanvasHeight - 160f, 8f, 114f);
            Image bg = _selBar.AddComponent<Image>();
            bg.color = new Color(0.08f, 0.16f, 0.30f, 0.95f);

            _selCountText = NewText(_selBar.transform, "", 20,
                TextAnchor.MiddleLeft);
            Place(_selCountText.rectTransform, 12f, 4f, 300f, 38f);

            _selDeleteBtn = NewButton(_selBar.transform, "删除所选");
            Place(_selDeleteBtn.GetComponent<RectTransform>(),
                340f, 4f, 150f, 38f);
            _selDeleteBtn.targetGraphic.color =
                new Color(0.55f, 0.16f, 0.14f, 0.98f);
            _selDeleteBtn.onClick.AddListener(OnBatchDelete);

            Button mv = NewButton(_selBar.transform, "移动到…");
            Place(mv.GetComponent<RectTransform>(), 500f, 4f, 150f, 38f);
            mv.onClick.AddListener(() =>
            {
                _pickDest = true;
                _pickCopy = false;
                UpdateSelBar();
                SetStatus("点击目标文件夹卡片，移动所选到该处");
            });

            Button cp = NewButton(_selBar.transform, "复制到…");
            Place(cp.GetComponent<RectTransform>(), 660f, 4f, 150f, 38f);
            cp.onClick.AddListener(() =>
            {
                _pickDest = true;
                _pickCopy = true;
                UpdateSelBar();
                SetStatus("点击目标文件夹卡片，复制所选到该处");
            });

            Button clear = NewButton(_selBar.transform, "取消选择");
            Place(clear.GetComponent<RectTransform>(), 820f, 4f, 150f, 38f);
            clear.onClick.AddListener(() =>
            {
                _selPaths.Clear();
                _pickDest = false;
                _pickCopy = false;
                RefreshSelectionVisuals();
                UpdateSelBar();
            });
            _selBar.SetActive(false);
        }

        private void OnBatchDelete()
        {
            if (Time.unscaledTime < _batchDeleteArmUntil)
            {
                int done = 0;
                foreach (string p in new List<string>(_selPaths))
                {
                    try
                    {
                        // paths under _dir ending without extension ambiguity:
                        // GridItem knows kind — look it up.
                        GridItem it = _items.Find(
                            x => x.Path == p);
                        if (it != null && it.IsDir)
                            FileManager.DeleteDirectory(p, false);
                        else
                            FileManager.DeleteFile(p);
                        done++;
                    }
                    catch { }
                }
                SetStatus("已删除 " + done + " 项");
                _selPaths.Clear();
                _batchDeleteArmUntil = 0f;
                UpdateSelBar();
                _treeForce = true;
                RefreshGrid(true);
            }
            else
            {
                _batchDeleteArmUntil = Time.unscaledTime + 4f;
                _selDeleteBtn.GetComponentInChildren<Text>().text =
                    "确认删除?";
            }
        }

        private void DoBatchMove(string destDir)
        {
            _pickDest = false;
            _pickCopy = false;
            if (string.Equals(destDir, _dir,
                    StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("已在该目录，无需移动");
                UpdateSelBar();
                return;
            }
            int done = 0;
            foreach (string p in new List<string>(_selPaths))
            {
                try
                {
                    // A dir can't move into itself or its own subtree.
                    if (string.Equals(p, destDir,
                            StringComparison.OrdinalIgnoreCase) ||
                        destDir.StartsWith(p + "/",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    string leaf = p.Substring(p.LastIndexOf('/') + 1);
                    string dst = destDir.Length > 0
                        ? destDir + "/" + leaf : leaf;
                    GridItem it = _items.Find(x => x.Path == p);
                    if (it != null && it.IsDir)
                        FileManager.MoveDirectory(p, dst);
                    else
                    {
                        FileManager.MoveFile(p, dst, false);
                        MoveSidecarJpg(p, dst);
                    }
                    done++;
                }
                catch (Exception ex)
                {
                    LogErr("Q3 move " + p + ": " + ex.Message);
                }
            }
            SetStatus("已移动 " + done + "/" + _selPaths.Count +
                " 项 → " + destDir);
            _selPaths.Clear();
            UpdateSelBar();
            _treeForce = true;
            RefreshGrid(true);
        }

        private void DoBatchCopy(string destDir)
        {
            _pickDest = false;
            _pickCopy = false;
            if (string.Equals(destDir, _dir,
                    StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("已在该目录，无需复制");
                UpdateSelBar();
                return;
            }
            int done = 0;
            foreach (string p in new List<string>(_selPaths))
            {
                try
                {
                    if (string.Equals(p, destDir,
                            StringComparison.OrdinalIgnoreCase) ||
                        destDir.StartsWith(p + "/",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    string leaf = p.Substring(p.LastIndexOf('/') + 1);
                    string dst = destDir.Length > 0
                        ? destDir + "/" + leaf : leaf;
                    GridItem it = _items.Find(x => x.Path == p);
                    if (it != null && it.IsDir)
                        CopyDirRecursive(p, dst);
                    else
                    {
                        FileManager.CopyFile(p, dst, false);
                        CopySidecarJpg(p, dst);
                    }
                    done++;
                }
                catch (Exception ex)
                {
                    LogErr("Q3 copy " + p + ": " + ex.Message);
                }
            }
            SetStatus("已复制 " + done + "/" + _selPaths.Count +
                " 项 → " + destDir);
            // Selection stays armed after a copy — a second destination is
            // a common follow-up. Move clears it (the items are gone).
            UpdateSelBar();
            _treeForce = true;
            RefreshGrid(true);
        }

        // FileManager has no CopyDirectory — recurse it ourselves. Files
        // keep their sidecar .jpgs automatically since *.* matches all.
        private void CopyDirRecursive(string src, string dst)
        {
            FileManager.CreateDirectory(dst);
            foreach (string d in FileManager.GetDirectories(src, "*", false))
            {
                string dn = d.Replace('\\', '/').TrimEnd('/');
                string leaf = dn.Substring(dn.LastIndexOf('/') + 1);
                if (!leaf.StartsWith("."))
                    CopyDirRecursive(dn, dst + "/" + leaf);
            }
            foreach (string f in FileManager.GetFiles(src, "*.*", false))
            {
                string fn = f.Replace('\\', '/');
                string leaf = fn.Substring(fn.LastIndexOf('/') + 1);
                FileManager.CopyFile(fn, dst + "/" + leaf, false);
            }
        }

        // The companion .jpg must follow its .vap or the thumbnail breaks.
        private void MoveSidecarJpg(string src, string dst)
        {
            try
            {
                string srcJpg = src.Substring(0, src.Length -
                    Path.GetExtension(src).Length) + ".jpg";
                string dstJpg = dst.Substring(0, dst.Length -
                    Path.GetExtension(dst).Length) + ".jpg";
                Texture2D tex;
                if (_thumbCache.TryGetValue(src, out tex))
                {
                    _thumbCache.Remove(src);
                    _thumbCache[dst] = tex;
                }
                if (FileManager.FileExists(srcJpg, false, false))
                    FileManager.MoveFile(srcJpg, dstJpg, false);
            }
            catch { }
        }

        // Copy counterpart — source keeps its jpg AND its cache entry.
        private void CopySidecarJpg(string src, string dst)
        {
            try
            {
                string srcJpg = src.Substring(0, src.Length -
                    Path.GetExtension(src).Length) + ".jpg";
                string dstJpg = dst.Substring(0, dst.Length -
                    Path.GetExtension(dst).Length) + ".jpg";
                Texture2D tex;
                if (_thumbCache.TryGetValue(src, out tex))
                    _thumbCache[dst] = tex;
                if (FileManager.FileExists(srcJpg, false, false))
                    FileManager.CopyFile(srcJpg, dstJpg, false);
            }
            catch { }
        }

        // ---- async thumbnails (sidecar .jpg next to the .vap) ----
        private class ThumbJob
        {
            internal string Path;
            internal int ItemIndex;
            internal int Gen;
        }
        private readonly List<ThumbJob> _thumbJobs = new List<ThumbJob>();
        private int _gridGen;
        private bool _pumping;

        private void PumpThumbs()
        {
            if (_pumping || _thumbJobs.Count == 0 ||
                Quest3TriggerUIPlugin.Instance == null)
                return;
            _pumping = true;
            int gen = _gridGen;
            Quest3TriggerUIPlugin.Instance.StartCoroutine(Pump(gen));
        }

        private System.Collections.IEnumerator Pump(int gen)
        {
            while (_thumbJobs.Count > 0 && gen == _gridGen)
            {
                // A few jobs per frame keeps opening snappy while labels and
                // thumbnails stream in without a multi-second stall.
                for (int i = 0; i < 4 && _thumbJobs.Count > 0; i++)
                {
                    ThumbJob job = _thumbJobs[0];
                    _thumbJobs.RemoveAt(0);
                    if (job.Gen != gen || job.ItemIndex < 0 ||
                        job.ItemIndex >= _items.Count)
                        continue;
                    GridItem it = _items[job.ItemIndex];
                    if (it.Path != job.Path)
                        continue;
                    try
                    {
                        if (it.DateText == null)
                            it.DateText = FileManager.FileLastWriteTime(
                                job.Path, false, false)
                                .ToString("MM-dd HH:mm");
                        if (!_thumbCache.ContainsKey(job.Path))
                        {
                            string jpg = job.Path.Substring(0, job.Path.Length -
                                Path.GetExtension(job.Path).Length) + ".jpg";
                            if (_jpgSet.Contains(jpg))
                            {
                                byte[] bytes = File.ReadAllBytes(
                                    FileManager.GetFullPath(jpg));
                                Texture2D t = new Texture2D(2, 2,
                                    TextureFormat.RGBA32, false);
                                if (t.LoadImage(bytes))
                                    _thumbCache[job.Path] = t;
                                else
                                {
                                    UnityEngine.Object.Destroy(t);
                                    _thumbCache[job.Path] = null;
                                }
                            }
                            else
                                _thumbCache[job.Path] = null;
                        }
                        // Reflect into whichever pooled view currently shows
                        // this item (it may have been recycled already).
                        foreach (CardView v in _pool)
                        {
                            if (v.ItemIndex != job.ItemIndex || !v.Go.activeSelf)
                                continue;
                            v.Date.text = it.DateText ?? "";
                            Texture2D tex;
                            if (_thumbCache.TryGetValue(job.Path, out tex) &&
                                tex != null)
                            {
                                v.Thumb.texture = tex;
                                v.Thumb.color = Color.white;
                                if (!v.Thumb.gameObject.activeSelf)
                                    v.Thumb.gameObject.SetActive(true);
                            }
                            break;
                        }
                    }
                    catch { _thumbCache[job.Path] = null; }
                }
                yield return null;
            }
            _pumping = false;
            if (_thumbJobs.Count > 0 && gen == _gridGen)
                PumpThumbs();
        }

        // ---------- actions ----------
        // Single navigation path for every caller (grid card, tree row,
        // favourite, breadcrumb-ish up button): updates the dir, mirrors it
        // into the active tab and rebuilds the tab strip so its label tracks.
        private void NavTo(string dir)
        {
            if (dir.Length > 0 && !ValidDir(dir))
            {
                SetStatus("目录不存在: " + dir);
                return;
            }
            _dir = dir;
            _selectedPath = "";
            if (_activeTab >= 0 && _activeTab < _tabs.Count &&
                _dir.Length > 0)
            {
                _tabs[_activeTab] = _dir;
                SaveTabs();
                RebuildTabs();
            }
            RefreshGrid();
        }

        private void GoParent()
        {
            if (_dir.Length == 0)
                return;
            NavTo(_dir.Contains("/")
                ? _dir.Substring(0, _dir.LastIndexOf('/'))
                : "");
        }

        private void OnOpenPressed()
        {
            // Directory-pick mode: 打开 commits the directory being viewed
            // (navigate into the target first), matching the native
            // selectDirectory semantics.
            if (_dirPickMode)
            {
                Commit(_dir);
                return;
            }
            if (_selectedPath.Length > 0)
                Commit(_selectedPath);
        }

        private void OnSavePressed()
        {
            string name = _fileNameInput != null
                ? _fileNameInput.text.Trim() : "";
            if (name.Length == 0)
            {
                SetStatus("请输入文件名");
                return;
            }
            string ext = _filter.Length > 0 ? "." + _filter : ".vap";
            if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                name += ext;
            string leaf = name;
            if (leaf.IndexOf('/') >= 0)
                leaf = leaf.Substring(leaf.LastIndexOf('/') + 1);
            Commit(_dir + "/" + leaf);
        }

        private void OnNewFolder()
        {
            string name = "新建文件夹";
            string target = _dir + "/" + name;
            int n = 1;
            while (FileManager.DirectoryExists(target, false, false))
                target = _dir + "/" + name + " " + (++n);
            try
            {
                FileManager.CreateDirectory(target);
            }
            catch (Exception ex)
            {
                LogErr("Q3 mkdir " + target + ": " + ex.Message);
            }
            _treeForce = true;
            RefreshGrid();
        }

        private void Commit(string path)
        {
            Action<string> cb = _cb;
            Action<string, bool> cbf = _cbFull;
            _cb = null;
            _cbFull = null;
            Close();
            if (cbf != null)
                cbf(path, true);
            else if (cb != null)
                cb(path);
        }

        private void Close()
        {
            _tabs[_activeTab] = _dir;
            FlushTabs();
            _pointerInside = false;
            ResetGesture();
            RestoreNavigation();
            RestoreHud();
            LogHudDiag("close");
            if (Quest3TriggerUIPlugin.Instance != null)
                Quest3TriggerUIPlugin.Instance.StartCoroutine(HudDiagLater());
            if (_canvas != null)
            {
                if (Quest3TriggerUIPlugin.Instance != null)
                    Quest3TriggerUIPlugin.Instance.UndockTextKeyboard(
                        _canvas.transform);
                _canvas.gameObject.SetActive(false);
            }
            Action<string> cb = _cb;
            Action<string, bool> cbf = _cbFull;
            _cb = null;
            _cbFull = null;
            if (_statusText != null)
                _statusText.text = "";
            if (cbf != null)
                cbf("", true);
            else if (cb != null)
                cb("");
        }

        private void SetStatus(string s)
        {
            if (_statusText != null)
                _statusText.text = s;
        }

        private sealed class HiddenSibling
        {
            internal GameObject Go;
            internal CanvasGroup Cg;
        }

        // BISECT SWITCH: false = never hide the control panel or suppress
        // navigation while the browser is open — isolates whether the
        // grey-icons corruption comes from the hide machinery at all.
        // BISECT RESULT: hiding IS the trigger — specifically
        // interactable=false on the injected group. OnCanvasGroupChanged
        // broadcasts it down the tree and VaM's VUI widgets apply a
        // 'disabled' style that never reverts — permanent grey icons.
        // alpha=0 + blocksRaycasts=false already achieves the hide; keep
        // interactable TRUE so no disabled-style propagation happens.
        private const bool HideHudWhileOpen = true;
        private readonly List<HiddenSibling> _hudHidden =
            new List<HiddenSibling>();
        private float _nextForeignScan;

        private void Recenter()
        {
            // Dock where the native media browser lives (HUD browsers area,
            // above the control panel). Parenting under the window's
            // container is required — that chain carries mirrored/negative
            // scale, so copying only position+rotation to a scene-rooted
            // canvas renders it flipped. The container stays active when the
            // window itself is hidden.
            uFileBrowser.FileBrowser nb = SuperController.singleton != null
                ? SuperController.singleton.mediaFileBrowserUI : null;
            RectTransform win = nb != null && nb.window != null
                ? nb.window.GetComponent<RectTransform>() : null;
            if (win != null)
            {
                Transform host = win.parent != null ? win.parent : win;
                Vector3[] corners = new Vector3[4];
                win.GetWorldCorners(corners);
                Vector3 center = (corners[0] + corners[2]) * 0.5f;
                _canvas.transform.SetParent(host, false);
                _canvas.transform.position = center;
                _canvas.transform.rotation = win.rotation;
                float ps = host.lossyScale.x;
                float ls = ps > 1e-6f ? PanelScale / ps : PanelScale;
                _canvas.transform.localScale = new Vector3(ls, ls, ls);
                return;
            }
            if (SuperController.singleton == null ||
                SuperController.singleton.centerCameraTarget == null)
                return;
            Transform anchor = SuperController.singleton.centerCameraTarget.transform;
            _canvas.transform.SetParent(anchor, false);
            _canvas.transform.localPosition = new Vector3(0f, 0f, PanelDistance);
            _canvas.transform.localRotation = Quaternion.identity;
            _canvas.transform.localScale = Vector3.one * PanelScale;
        }

        // Hide the control panel visuals around this panel: deactivate every
        // sibling of each ancestor on the path from our canvas up to
        // mainHUD, leaving only the branch that contains us (the file
        // browser area). Objects hosting an EventSystem/input module are
        // skipped so pointer input keeps working.
        private void HideHudAround()
        {
            EnforceHudHidden();
        }

        // Hide control-panel visuals via CanvasGroup instead of SetActive:
        // VaM re-activates its HUD children on grab/interaction, but an
        // alpha-0 group stays invisible (and raycast-dead) no matter how
        // often the objects are re-enabled. Re-run each frame so newly
        // created siblings are covered too.
        private void EnforceHudHidden()
        {
            try
            {
                SuperController sc = SuperController.singleton;
                if (sc == null || sc.mainHUD == null || _canvas == null)
                    return;
                Transform top = sc.mainHUD;
                // The sibling walk runs GetComponentInChildren on every
                // sibling subtree — hundreds of HUD objects — so scanning
                // every frame was the constant-stutter source. New siblings
                // appear rarely; 0.4s cadence still catches them.
                if (Time.unscaledTime >= _nextSibScan)
                {
                    _nextSibScan = Time.unscaledTime + 0.4f;
                    Transform path = _canvas.transform;
                    while (path != null && path != top)
                    {
                        Transform p = path.parent;
                        if (p == null)
                            break;
                        for (int i = 0; i < p.childCount; i++)
                        {
                            Transform c = p.GetChild(i);
                            if (c == path)
                                continue;
                            if (c.GetComponentInChildren<EventSystem>(true) !=
                                null)
                                continue;
                            if (c.GetComponentInChildren<BaseInputModule>(true) !=
                                null)
                                continue;
                            HideSibling(c.gameObject);
                        }
                        path = p;
                    }
                }
                for (int i = 0; i < _hudHidden.Count; i++)
                {
                    HiddenSibling h = _hudHidden[i];
                    if (h.Go == null)
                        continue;
                    if (h.Cg == null)
                        h.Cg = EnsureHideGroup(h.Go);
                    if (h.Cg.alpha != 0f || h.Cg.blocksRaycasts)
                    {
                        h.Cg.alpha = 0f;
                        h.Cg.blocksRaycasts = false;
                    }
                }
            }
            catch { }
        }

        // Fade out foreign world-space canvases overlapping the panel or
        // the docked keyboard below it. Uses the same CanvasGroup mechanism
        // as HUD siblings (own component, destroyed on restore).
        private void ScanForeignPanels()
        {
            RectTransform ourRt = _canvas != null
                ? _canvas.transform as RectTransform : null;
            if (ourRt == null)
                return;
            Plane plane = new Plane(ourRt.forward, ourRt.position);
            float ourWorldW = ourRt.rect.width * ourRt.lossyScale.x;
            foreach (Canvas c in GameObject.FindObjectsOfType<Canvas>())
            {
                if (c == null || c == _canvas || !c.isActiveAndEnabled)
                    continue;
                // Render mode / EventSystem / InputModule / name are all
                // static per canvas — cache the verdict so the periodic
                // sweep doesn't rescan every subtree each time.
                bool skip;
                if (!_foreignSkip.TryGetValue(c, out skip))
                {
                    string n = c.gameObject.name;
                    // Keep our own essentials usable: the radial menu and
                    // an undocked keyboard must stay interactive.
                    skip = c.renderMode != RenderMode.WorldSpace ||
                        c.transform.IsChildOf(_canvas.transform) ||
                        n == "Quest3 Radial Quick Menu" ||
                        n == "Quest3 Full VR Keyboard" ||
                        c.GetComponentInChildren<EventSystem>(true) != null ||
                        c.GetComponentInChildren<BaseInputModule>(true) !=
                            null;
                    _foreignSkip[c] = skip;
                }
                if (skip)
                    continue;
                RectTransform rt = c.transform as RectTransform;
                if (rt == null)
                    continue;
                Vector3[] corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                float diag = (corners[2] - corners[0]).magnitude;
                // Skip tiny laser-dot canvases and giant backdrop overlays.
                if (diag < 0.12f || diag > ourWorldW * 1.8f)
                    continue;
                for (int k = 0; k <= 4; k++)
                {
                    Vector3 w = k < 4 ? corners[k]
                        : (corners[0] + corners[2]) * 0.5f;
                    if (Mathf.Abs(plane.GetDistanceToPoint(w)) > 0.6f)
                        continue;
                    Vector3 l = ourRt.InverseTransformPoint(w);
                    if (l.x > -1500f && l.x < 1500f &&
                        l.y > -1400f && l.y < 500f)
                    {
                        HideSibling(c.gameObject);
                        break;
                    }
                }
            }
        }

        // Marker paired with every CanvasGroup we inject. Looked up by type
        // NAME (not assembly identity) so cleanup still finds tags left
        // behind by a previous payload generation — a hot reload used to
        // orphan them, leaving alpha-0 groups on the HUD forever (the
        // grey-icons bug).
        private sealed class Q3HideTag : MonoBehaviour
        {
            public CanvasGroup Cg;
        }

        private static CanvasGroup FindTaggedGroup(GameObject go)
        {
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != "Q3HideTag")
                    continue;
                System.Reflection.FieldInfo f = c.GetType().GetField("Cg");
                CanvasGroup cg = f != null
                    ? f.GetValue(c) as CanvasGroup : null;
                if (cg != null)
                    return cg;
            }
            return null;
        }

        private static CanvasGroup EnsureHideGroup(GameObject go)
        {
            CanvasGroup cg = FindTaggedGroup(go);
            if (cg == null)
            {
                Q3HideTag tag = go.AddComponent<Q3HideTag>();
                cg = go.AddComponent<CanvasGroup>();
                tag.Cg = cg;
            }
            return cg;
        }

        private void HideSibling(GameObject go)
        {
            if (go == null)
                return;
            for (int i = 0; i < _hudHidden.Count; i++)
                if (_hudHidden[i].Go == go)
                    return;
            HiddenSibling h = new HiddenSibling();
            h.Go = go;
            // Always use OUR OWN CanvasGroup (they stack multiplicatively)
            // and only ever destroy that one on restore — VaM's own groups
            // are never touched, so no stale value can leak back. Adopt a
            // leftover tag instead of stacking another group on the object.
            h.Cg = EnsureHideGroup(go);
            h.Cg.alpha = 0f;
            h.Cg.blocksRaycasts = false;
            _hudHidden.Add(h);
        }

        // One-shot cleanup for leftovers from previous payload generations
        // — runs at payload start (mainHUD may not exist yet at Awake, so
        // the plugin calls this lazily once the HUD appears) and again on
        // every browser open/close. Two passes: tagged groups are destroyed
        // outright; pre-tag orphans are recognised by signature — extra
        // CanvasGroups past the first on a GO are always injections, and a
        // single group pinned at alpha=0/non-interactable on an ACTIVE HUD
        // object can only be ours (VaM hides its panels by deactivating
        // them, not via dead groups).
        internal static void CleanupHudState()
        {
            SweepHideTags();
            try
            {
                SuperController sc = SuperController.singleton;
                if (sc == null || sc.mainHUD == null)
                    return;
                for (Transform a = sc.mainHUD; a != null; a = a.parent)
                    ResetOrphanedGroups(a.gameObject);
                foreach (Transform t in
                    sc.mainHUD.GetComponentsInChildren<Transform>(true))
                    ResetOrphanedGroups(t.gameObject);
                foreach (Canvas c in
                    Resources.FindObjectsOfTypeAll<Canvas>())
                    if (c != null && c.gameObject.scene.IsValid())
                        ResetOrphanedGroups(c.gameObject);
                // CanvasGroup state is clean now, so if the icons are still
                // grey+dead the Selectables themselves were disabled —
                // count and revive them (VaM re-syncs on its next UI
                // transition anyway, so forcing live is harmless).
                int dead = 0, total = 0;
                Transform ours = _instance != null &&
                    _instance._canvas != null
                    ? _instance._canvas.transform : null;
                foreach (Selectable s in sc.mainHUD
                    .GetComponentsInChildren<Selectable>(true))
                {
                    total++;
                    if (!s.interactable)
                    {
                        // Our own card Buttons are interactable=false BY
                        // DESIGN (manual gesture owns activation) — never
                        // revive anything inside our canvas.
                        if (ours != null &&
                            s.transform.IsChildOf(ours))
                            continue;
                        dead++;
                        s.interactable = true;
                    }
                }
                LogErr("Q3 hud cleanup: deadSel=" + dead + "/" + total);
                // Purge stale canvas registrations — every hot reload used
                // to leave one orphan "Quest3 Preset Browser" canvas in
                // VaM's allCanvases list (20+ observed).
                FieldInfo acf = typeof(SuperController).GetField(
                    "allCanvases", BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic);
                System.Collections.IList ac = acf != null
                    ? acf.GetValue(sc) as System.Collections.IList : null;
                if (ac != null)
                    for (int i = ac.Count - 1; i >= 0; i--)
                    {
                        Component c = ac[i] as Component;
                        bool stale = c == null || c.gameObject == null;
                        bool isOurs = !stale &&
                            c.gameObject.name == "Quest3 Preset Browser" &&
                            (_instance == null || _instance._canvas == null ||
                             c != _instance._canvas);
                        if (stale || isOurs)
                            ac.RemoveAt(i);
                    }
                // A stuck 'disable all navigation' widget state greys every
                // HUD button even when the flag reads false — cycle the
                // property so VaM's SyncDisableAllNavigation re-applies the
                // enabled state to its nav widgets.
                bool nav = sc.disableAllNavigation;
                sc.disableAllNavigation = !nav;
                sc.disableAllNavigation = nav;
                // Orphan browser canvases from dead payload generations
                // (inactive, so the Build-time name search never saw them).
                foreach (GameObject orphan in
                    Resources.FindObjectsOfTypeAll<GameObject>())
                    if (orphan != null && orphan.scene.IsValid() &&
                        orphan.name == "Quest3 Preset Browser" &&
                        (_instance == null || _instance._canvas == null ||
                         orphan != _instance._canvas.gameObject))
                        UnityEngine.Object.Destroy(orphan);
                Instance.LogHudDiag("boot");
            }
            catch { }
        }

        private static bool IsHiddenSignature(CanvasGroup cg)
        {
            // Covers both generations: old groups pinned all three flags,
            // new ones leave interactable alone (it greys VaM widgets
            // permanently via OnCanvasGroupChanged).
            return cg != null && cg.alpha <= 0.01f && !cg.blocksRaycasts;
        }

        // badCG=0 in the clean state proved VaM never hides mainHUD
        // children via dead CanvasGroups — so any alpha-0/non-interactable
        // group found there is injected residue, even on INACTIVE objects
        // (a stale group on a closed panel = "icon clicks open nothing").
        private static void ResetOrphanedGroups(GameObject go)
        {
            if (go == null)
                return;
            CanvasGroup[] cgs = go.GetComponents<CanvasGroup>();
            for (int i = 0; i < cgs.Length; i++)
            {
                if (!IsHiddenSignature(cgs[i]))
                    continue;
                if (i > 0)
                    UnityEngine.Object.Destroy(cgs[i]);   // injected extra
                else
                {
                    cgs[i].alpha = 1f;
                    cgs[i].interactable = true;
                    cgs[i].blocksRaycasts = true;
                }
            }
        }

        // Destroy every tagged CanvasGroup we ever injected, anywhere —
        // tracked list or not. Covers the HUD subtree, scene roots (the
        // scene-root era hid mainHUD's siblings up there) and foreign
        // canvases. Runs on open (clean slate) and on every close path.
        private static void SweepHideTags()
        {
            try
            {
                SuperController sc = SuperController.singleton;
                if (sc != null && sc.mainHUD != null)
                {
                    for (Transform a = sc.mainHUD; a != null; a = a.parent)
                        SweepTagsOn(a.gameObject);
                    foreach (Transform t in
                        sc.mainHUD.GetComponentsInChildren<Transform>(true))
                        SweepTagsOn(t.gameObject);
                }
                foreach (GameObject r in
                    UnityEngine.SceneManagement.SceneManager
                        .GetActiveScene().GetRootGameObjects())
                    if (r != null)
                        SweepTagsOn(r.gameObject);
                foreach (Canvas c in
                    Resources.FindObjectsOfTypeAll<Canvas>())
                    if (c != null && c.gameObject.scene.IsValid())
                        SweepTagsOn(c.gameObject);
            }
            catch { }
        }

        private static void SweepTagsOn(GameObject go)
        {
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != "Q3HideTag")
                    continue;
                try
                {
                    System.Reflection.FieldInfo f =
                        c.GetType().GetField("Cg");
                    CanvasGroup cg = f != null
                        ? f.GetValue(c) as CanvasGroup : null;
                    if (cg != null)
                        UnityEngine.Object.Destroy(cg);
                }
                catch { }
                UnityEngine.Object.Destroy(c);
            }
        }

        private void RestoreHud()
        {
            _hudHidden.Clear();
            SweepHideTags();
        }

        // Dump the VaM UI state that could leave the control panel grey and
        // unclickable — the grey-icons bug survived two CanvasGroup repairs,
        // so log real state instead of guessing.
        private IEnumerator HudDiagLater()
        {
            yield return new WaitForSeconds(1.5f);
            LogHudDiag("close+1.5s");
            // The grey-icons symptom appears only when the user reopens the
            // control panel — poll until mainHUD is live again (max ~30s),
            // then dump state plus every world canvas overlapping the HUD
            // (an invisible overlay would explain dead icon clicks).
            for (int i = 0; i < 60; i++)
            {
                yield return new WaitForSeconds(0.5f);
                SuperController sc = SuperController.singleton;
                if (sc == null || sc.mainHUD == null)
                    yield break;
                if (!sc.mainHUD.gameObject.activeInHierarchy)
                    continue;
                LogHudDiag("hud-reopen");
                RectTransform hud = sc.mainHUD as RectTransform;
                if (hud == null)
                    yield break;
                Plane plane = new Plane(hud.forward, hud.position);
                foreach (Canvas c in GameObject.FindObjectsOfType<Canvas>())
                {
                    if (c == null || !c.isActiveAndEnabled ||
                        c.renderMode != RenderMode.WorldSpace)
                        continue;
                    RectTransform rt = c.transform as RectTransform;
                    if (rt == null)
                        continue;
                    Vector3[] co = new Vector3[4];
                    rt.GetWorldCorners(co);
                    for (int k = 0; k <= 4; k++)
                    {
                        Vector3 w = k < 4 ? co[k] : (co[0] + co[2]) * 0.5f;
                        if (Mathf.Abs(plane.GetDistanceToPoint(w)) > 1.5f)
                            continue;
                        Vector3 l = hud.InverseTransformPoint(w);
                        if (Mathf.Abs(l.x) < hud.rect.width * 0.7f &&
                            Mathf.Abs(l.y) < hud.rect.height * 0.9f)
                        {
                            CanvasGroup cg = c.GetComponent<CanvasGroup>();
                            LogErr("Q3 hud overlap: " + c.gameObject.name +
                                " order=" + c.sortingOrder +
                                " sort=" + c.sortingLayerName +
                                " cg=" + (cg != null
                                    ? cg.alpha.ToString("F2") : "none"));
                            break;
                        }
                    }
                }
                yield break;
            }
        }

        private void LogHudDiag(string tag)
        {
            try
            {
                SuperController sc = SuperController.singleton;
                if (sc == null || sc.mainHUD == null)
                {
                    LogErr("Q3 hud diag[" + tag + "]: no controller");
                    return;
                }
                string sel = "?";
                FieldInfo sf = typeof(SuperController).GetField("selectMode",
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic);
                if (sf != null)
                {
                    object sv = sf.GetValue(sc);
                    sel = sv != null ? sv.ToString() : "null";
                }
                // Monitor-rig anchoring makes the HUD render in VR but be
                // unreachable by VR pointers — a plausible 'grey+dead' mode.
                string rig = "?";
                FieldInfo[] rfs = {
                    typeof(SuperController).GetField("MonitorRigActive",
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic),
                    typeof(SuperController).GetField("isMonitorOnly",
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic),
                    typeof(SuperController).GetField("_mainHUDAnchoredOnMonitor",
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic)
                };
                rig = "";
                foreach (FieldInfo f in rfs)
                {
                    if (f == null) { rig += "null,"; continue; }
                    object v = f.GetValue(sc);
                    rig += (v != null ? v.ToString() : "null") + ",";
                }
                string inputMod = "";
                try
                {
                    if (EventSystem.current != null)
                        foreach (BaseInputModule im in EventSystem.current
                            .GetComponents<BaseInputModule>())
                        {
                            if (im == null)
                                continue;
                            inputMod += "[" + im.GetType().Name +
                                (im.enabled ? "+en" : "-dis");
                            foreach (FieldInfo f in im.GetType().GetFields(
                                BindingFlags.Instance | BindingFlags.Public |
                                BindingFlags.NonPublic))
                            {
                                string fn = f.Name.ToLowerInvariant();
                                if (!fn.Contains("drag") &&
                                    !fn.Contains("press") &&
                                    !fn.Contains("click") &&
                                    !fn.Contains("down"))
                                    continue;
                                object v = f.GetValue(im);
                                if (v is bool || v is float || v is int)
                                    inputMod += " " + f.Name + "=" + v;
                            }
                            inputMod += "]";
                        }
                    else
                        inputMod = "noEventSystem";
                }
                catch { }
                string gso = "";
                try
                {
                    object g = MeshVR.GlobalSceneOptions.singleton;
                    if (g != null)
                        foreach (FieldInfo f in g.GetType().GetFields(
                            BindingFlags.Instance | BindingFlags.Public))
                            if (f.FieldType == typeof(bool) &&
                                f.Name.StartsWith("disable"))
                            {
                                object v = f.GetValue(g);
                                if (v is bool && (bool)v)
                                    gso += "[" + f.Name + "=TRUE]";
                            }
                }
                catch { }
                string canvasReg = "";
                FieldInfo acf = typeof(SuperController).GetField("allCanvases",
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic);
                if (acf != null)
                {
                    System.Collections.IList ac =
                        acf.GetValue(sc) as System.Collections.IList;
                    if (ac != null)
                        foreach (object o in ac)
                        {
                            Component cc = o as Component;
                            if (cc != null)
                                canvasReg += "[" + cc.gameObject.name +
                                    (cc.gameObject.activeInHierarchy
                                        ? "+a" : "-a") + "]";
                        }
                }
                int bad = 0;
                string badPaths = "";
                foreach (CanvasGroup cg in sc.mainHUD
                    .GetComponentsInChildren<CanvasGroup>(true))
                {
                    if (cg.alpha < 0.999f || !cg.interactable ||
                        !cg.blocksRaycasts)
                    {
                        bad++;
                        if (badPaths.Length < 400)
                            badPaths += " [" + cg.gameObject.name +
                                " a=" + cg.alpha.ToString("F2") +
                                " i=" + cg.interactable +
                                " b=" + cg.blocksRaycasts + "]";
                    }
                }
                // Deep-dive the actual icon button VaM's pointer hit:
                // ButtonMainMenu's components, its VUI.Widget bool state
                // (VaM widgets aren't Unity Selectables — deadSel can't see
                // them) and the icon Image's tint.
                string btnDump = "";
                try
                {
                    Transform btn = null;
                    foreach (Transform t in sc.mainHUD
                        .GetComponentsInChildren<Transform>(true))
                        if (t.name == "ButtonMainMenu") { btn = t; break; }
                    if (btn != null)
                    {
                        System.Text.StringBuilder sb =
                            new System.Text.StringBuilder();
                        foreach (Component c in btn.GetComponents<Component>())
                        {
                            if (c == null) { sb.Append("null,"); continue; }
                            sb.Append(c.GetType().Name);
                            Image img = c as Image;
                            if (img != null)
                                sb.Append("(c=" + img.color + ",ray=" +
                                    img.raycastTarget + ")");
                            sb.Append(",");
                            // VUI widget: dump its bool fields/props
                            if (c.GetType().Name == "MouseCallbacks")
                            {
                                object w = c.GetType().GetField("Widget")
                                    != null ? c.GetType().GetField("Widget")
                                    .GetValue(c) : null;
                                if (w == null)
                                {
                                    PropertyInfo wp = c.GetType()
                                        .GetProperty("Widget");
                                    if (wp != null) w = wp.GetValue(c, null);
                                }
                                if (w != null)
                                {
                                    sb.Append("<widget:");
                                    foreach (FieldInfo f in w.GetType()
                                        .GetFields(BindingFlags.Instance |
                                            BindingFlags.Public))
                                        if (f.FieldType == typeof(bool))
                                            sb.Append(f.Name + "=" +
                                                f.GetValue(w) + ";");
                                    foreach (PropertyInfo p in w.GetType()
                                        .GetProperties(BindingFlags.Instance |
                                            BindingFlags.Public))
                                        if (p.PropertyType == typeof(bool) &&
                                            p.CanRead &&
                                            (p.Name.ToLowerInvariant()
                                                .Contains("enable") ||
                                             p.Name.ToLowerInvariant()
                                                .Contains("interact") ||
                                             p.Name.ToLowerInvariant()
                                                .Contains("visible") ||
                                             p.Name.ToLowerInvariant()
                                                .Contains("click")))
                                            try {
                                                sb.Append(p.Name + "=" +
                                                    p.GetValue(w, null) + ";");
                                            } catch { }
                                    sb.Append(">");
                                }
                            }
                        }
                        btnDump = sb.ToString();
                    }
                    // The icon grid renders dimmed + eats clicks while its
                    // CanvasGroups are clean — classic symptom of a dark
                    // overlay Image sibling drawn AFTER the toolbar inside
                    // the same canvas. Dump every Image under the Scene
                    // Control Canvas whose rect covers the toolbar area.
                    Transform tb = null;
                    foreach (Transform t in sc.mainHUD
                        .GetComponentsInChildren<Transform>(true))
                        if (t.name == "Toolbar") { tb = t; break; }
                    if (tb != null)
                    {
                        Rect tbRect = (tb as RectTransform).rect;
                        Vector3[] tc = new Vector3[4];
                        (tb as RectTransform).GetWorldCorners(tc);
                        Vector2 tbMin = new Vector2(
                            Mathf.Min(tc[0].x, tc[2].x),
                            Mathf.Min(tc[0].y, tc[2].y));
                        Vector2 tbMax = new Vector2(
                            Mathf.Max(tc[0].x, tc[2].x),
                            Mathf.Max(tc[0].y, tc[2].y));
                        Canvas host = tb.GetComponentInParent<Canvas>();
                        if (host != null)
                            foreach (Image im2 in host
                                .GetComponentsInChildren<Image>(true))
                            {
                                RectTransform irt =
                                    im2.transform as RectTransform;
                                if (irt == null || irt == tb)
                                    continue;
                                Vector3[] ic = new Vector3[4];
                                irt.GetWorldCorners(ic);
                                Vector2 imin = new Vector2(
                                    Mathf.Min(ic[0].x, ic[2].x),
                                    Mathf.Min(ic[0].y, ic[2].y));
                                Vector2 imax = new Vector2(
                                    Mathf.Max(ic[0].x, ic[2].x),
                                    Mathf.Max(ic[0].y, ic[2].y));
                                // overlaps the toolbar horizontally?
                                if (imin.x > tbMax.x || imax.x < tbMin.x ||
                                    imin.y > tbMax.y || imax.y < tbMin.y)
                                    continue;
                                if (!im2.raycastTarget &&
                                    im2.color.a < 0.01f)
                                    continue;
                                btnDump += " OVL:" + im2.name +
                                    " c=" + im2.color +
                                    " ray=" + im2.raycastTarget +
                                    " sib=" + im2.transform
                                        .GetSiblingIndex() +
                                    "/" + (im2.transform.parent != null
                                        ? im2.transform.parent
                                            .childCount : -1) +
                                    " act=" + im2.gameObject
                                        .activeInHierarchy;
                            }
                    }
                }
                catch (Exception ex) { btnDump = "err:" + ex.Message; }
                int dead = 0, total = 0;
                string deadPaths = "";
                foreach (Selectable s in sc.mainHUD
                    .GetComponentsInChildren<Selectable>(true))
                {
                    total++;
                    if (!s.interactable)
                    {
                        dead++;
                        if (deadPaths.Length < 300)
                            deadPaths += " [" + s.gameObject.name + "]";
                    }
                }
                LogErr("Q3 hud diag[" + tag + "]: selectMode=" + sel +
                    " activeUI=" + sc.activeUI +
                    " worldUIActivated=" + sc.worldUIActivated +
                    " disableAllNav=" + sc.disableAllNavigation +
                    " hudActive=" + sc.mainHUD.gameObject.activeSelf +
                    " rig=" + rig +
                    " canvases=" + canvasReg +
                    " gso=" + (gso.Length > 0 ? gso : "none") +
                    " uiDisabled=" + sc.UIDisabled +
                    " im=" + inputMod +
                    " badCG=" + bad + badPaths +
                    " deadSel=" + dead + "/" + total + deadPaths +
                    " btn=" + btnDump);
            }
            catch (Exception ex)
            {
                LogErr("Q3 hud diag failed: " + ex.Message);
            }
        }

        private void Dispose()
        {
            foreach (var kv in _thumbCache)
                if (kv.Value != null)
                    UnityEngine.Object.Destroy(kv.Value);
            _thumbCache.Clear();
            if (_canvas != null)
            {
                if (SuperController.singleton != null)
                    SuperController.singleton.RemoveCanvas(_canvas);
                UnityEngine.Object.Destroy(_canvas.gameObject);
                _canvas = null;
            }
        }

        // ---------- ui helpers ----------
        private GameObject NewObj(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private Text NewText(Transform parent, string value,
            int size, TextAnchor anchor)
        {
            GameObject go = NewObj("Text", parent);
            Text t = go.AddComponent<Text>();
            t.font = _font;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = Color.white;
            t.text = value;
            return t;
        }

        private Button NewButton(Transform parent, string label)
        {
            GameObject go = NewObj("Btn_" + label, parent);
            Image img = go.AddComponent<Image>();
            img.color = new Color(0.12f, 0.35f, 0.57f, 0.98f);
            Button b = go.AddComponent<Button>();
            b.targetGraphic = img;
            ColorBlock cb = b.colors;
            cb.highlightedColor = new Color(1f, 0.72f, 0.55f, 1f);
            cb.pressedColor = new Color(1f, 0.48f, 0.30f, 1f);
            cb.colorMultiplier = 1f;
            b.colors = cb;
            Text t = NewText(go.transform, label, 22, TextAnchor.MiddleCenter);
            t.raycastTarget = false;
            return b;
        }

        private InputField NewInput(Transform parent, string placeholder)
        {
            GameObject go = NewObj("Input", parent);
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0.17f, 0.19f, 0.24f, 0.98f);
            Text text = NewText(go.transform, "", 22, TextAnchor.MiddleLeft);
            // A 22pt line (~26px) is taller than the ~16-24px box left after
            // vertical insets — default Truncate overflow clips the whole
            // first line, so typed text was invisible though input worked.
            text.verticalOverflow = VerticalWrapMode.Overflow;
            SetInsets(text.rectTransform, 14f, 2f);
            Text ph = NewText(go.transform, placeholder, 22, TextAnchor.MiddleLeft);
            ph.color = new Color(0.75f, 0.78f, 0.84f, 0.85f);
            ph.verticalOverflow = VerticalWrapMode.Overflow;
            SetInsets(ph.rectTransform, 14f, 2f);
            InputField input = go.AddComponent<InputField>();
            input.targetGraphic = bg;
            input.textComponent = text;
            input.placeholder = ph;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = 200;
            return input;
        }

        private static void SetInsets(RectTransform r, float x, float y)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(x, y);
            r.offsetMax = new Vector2(-x, -y);
        }

        // All four args are POSITIVE insets from the parent's edges.
        private static void Anchor(RectTransform r,
            float l, float b, float rt, float t)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(l, b);
            r.offsetMax = new Vector2(-rt, -t);
        }

        // Fixed-size rect, x/top measured from the parent's top-left corner.
        private static void Place(RectTransform r,
            float x, float top, float w, float h)
        {
            r.anchorMin = r.anchorMax = new Vector2(0f, 1f);
            r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = new Vector2(x, -top);
            r.sizeDelta = new Vector2(w, h);
        }

        // Fixed-size rect anchored to the parent's top-right corner.
        private static void PlaceRight(RectTransform r,
            float rightInset, float top, float w, float h)
        {
            r.anchorMin = r.anchorMax = new Vector2(1f, 1f);
            r.pivot = new Vector2(1f, 1f);
            r.anchoredPosition = new Vector2(-rightInset, -top);
            r.sizeDelta = new Vector2(w, h);
        }

        private static int ResolveUiLayer()
        {
            int layer = LayerMask.NameToLayer("UI");
            return layer < 0 ? 5 : layer;
        }

        private static void LogErr(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogError(message);
        }
        private static void SetLayerRecursively(GameObject root, int layer)
        {
            root.layer = layer;
            foreach (Transform child in root.transform)
                SetLayerRecursively(child.gameObject, layer);
        }
    }

    // =====================================================================
    // P3: Harmony takeover of the native uFileBrowser. GetMediaPathDialog
    // writes all of its config into the FileBrowser's public fields before
    // calling Show, and JSONStorableUrl.Browse / third-party plugins call
    // Show directly too — so prefixing Show catches every native-browser
    // path at once (VaM's own callers AND plugin callers). Modes our panel
    // cannot represent return true and fall through to the native code.
    // =====================================================================
    internal static class FileBrowserTakeover
    {
        // Runtime kill-switch from [Browser]TakeoverNative config.
        internal static bool Enabled = true;

        // SetTextEntry(true) marks save mode; the flag it drives is
        // private, so the postfix below remembers the last call per
        // browser instance and Show reads it back.
        internal static readonly
            Dictionary<uFileBrowser.FileBrowser, bool> TextEntry =
                new Dictionary<uFileBrowser.FileBrowser, bool>();

        internal static bool TryTakeOver(uFileBrowser.FileBrowser fb,
            uFileBrowser.FileBrowserFullCallback cbf,
            uFileBrowser.FileBrowserCallback cb, bool changeDirectory)
        {
            try
            {
                if (fb == null || !Enabled)
                    return true;
                // Modes our panel cannot represent — let the native
                // browser handle them.
                if (fb.forceOnlyShowTemplates)
                { Pass("templates"); return true; }
                // changeDirectory=false keeps the browser's current dir;
                // mirror that so the panel lands where the caller expects.
                string dir = changeDirectory
                    ? fb.defaultPath : fb.CurrentPath;
                if (string.IsNullOrEmpty(dir))
                    dir = fb.defaultPath;
                if (string.IsNullOrEmpty(dir))
                    dir = fb.CurrentPath;
                // Absolute paths: VaM dialogs may hand us a resolved
                // filesystem path; convert back to VaM-relative when it
                // sits inside the install dir, bail only when outside.
                if (!string.IsNullOrEmpty(dir) &&
                    (dir.IndexOf(':') >= 0 || dir.StartsWith("/") ||
                     dir.StartsWith("\\")))
                {
                    dir = TryVamRelative(dir);
                    if (dir == null)
                    { Pass("rooted path"); return true; }
                }
                // browseVarFilesAsDirectories normally lets you drill into
                // .var packages as dirs. Our ListDir already merges package
                // contents into real dirs, so preset browsing works — only
                // bail when the dialog actually roots at a package path
                // where drilling into .var files is the point.
                if (fb.browseVarFilesAsDirectories &&
                    (dir.IndexOf(".var/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     dir.EndsWith(".var", StringComparison.OrdinalIgnoreCase) ||
                     dir.StartsWith("AddonPackages",
                         StringComparison.OrdinalIgnoreCase)))
                { Pass("var-dir browse " + dir); return true; }
                bool saveMode;
                if (!TextEntry.TryGetValue(fb, out saveMode))
                    saveMode = false;
                string title = fb.titleText != null
                    ? fb.titleText.text : null;
                string saveName = saveMode && fb.fileEntryField != null
                    ? fb.fileEntryField.text : null;
                if (string.IsNullOrEmpty(dir) ||
                    !FileManager.DirectoryExists(dir, false, false))
                    dir = "Custom";
                VrPresetBrowser.ShowDialogFull(title, dir, fb.fileFormat,
                    saveMode, saveName,
                    (path, didClose) =>
                    {
                        if (cbf != null)
                            cbf(path, didClose);
                        else if (cb != null)
                            cb(path);
                    },
                    fb.selectDirectory);
                return false;   // native Show skipped — window never opens
            }
            catch (Exception ex)
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogError(
                        "Q3 takeover: " + ex);
                return true;    // fail-open: native browser still works
            }
        }

        private static void Pass(string reason)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogError(
                    "Q3 takeover pass-through: " + reason);
        }

        // Absolute filesystem path → VaM-relative ("Custom/…"), or null when
        // it points outside the install dir.
        private static string TryVamRelative(string abs)
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(
                    UnityEngine.Application.dataPath, ".."))
                    .Replace('\\', '/').TrimEnd('/');
                string full = Path.GetFullPath(abs)
                    .Replace('\\', '/').TrimEnd('/');
                if (string.Equals(full, root,
                        StringComparison.OrdinalIgnoreCase))
                    return "";
                if (full.StartsWith(root + "/",
                        StringComparison.OrdinalIgnoreCase))
                    return full.Substring(root.Length + 1);
            }
            catch { }
            return null;
        }
    }

    [HarmonyPatch(typeof(uFileBrowser.FileBrowser), "SetTextEntry")]
    internal static class FileBrowserSetTextEntryPatch
    {
        [HarmonyPostfix]
        private static void Postfix(uFileBrowser.FileBrowser __instance,
            bool b)
        {
            FileBrowserTakeover.TextEntry[__instance] = b;
        }
    }

    [HarmonyPatch(typeof(uFileBrowser.FileBrowser), "Show",
        new Type[] { typeof(uFileBrowser.FileBrowserCallback),
            typeof(bool) })]
    internal static class FileBrowserShowCbPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(uFileBrowser.FileBrowser __instance,
            uFileBrowser.FileBrowserCallback callback,
            bool changeDirectory)
        {
            return FileBrowserTakeover.TryTakeOver(__instance, null,
                callback, changeDirectory);
        }
    }

    [HarmonyPatch(typeof(uFileBrowser.FileBrowser), "Show",
        new Type[] { typeof(uFileBrowser.FileBrowserFullCallback),
            typeof(bool) })]
    internal static class FileBrowserShowFullPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(uFileBrowser.FileBrowser __instance,
            uFileBrowser.FileBrowserFullCallback fullCallback,
            bool changeDirectory)
        {
            return FileBrowserTakeover.TryTakeOver(__instance,
                fullCallback, null, changeDirectory);
        }
    }
}
