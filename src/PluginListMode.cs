using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    // Point-at-person plugin panel: hovering the right-hand ray over a Person
    // atom pops a semi-transparent panel listing that atom's plugins. Each
    // row toggles enable/disable or borrows that plugin's native VaM page
    // (MVRScriptController.customUI reparented into our panel frame, so the
    // atom sidebar/control panel stay out). Mutually exclusive with the
    // clothing/hair hover modes.
    internal static class PluginListMode
    {
        private const float RowHeight = 44f;
        private const float RowGap = 4f;
        private const float PanelRouteGrace = 1.2f;
        private const float PageAlpha = .88f;

        private sealed class PluginItem
        {
            internal Atom atom;
            internal string id;
            internal string display;
            internal MVRScript script;
        }

        internal static bool Active { get; private set; }

        private static Atom _atom;
        private static Vector3 _anchor;
        private static RectTransform _root;
        private static Canvas _canvas;
        private static Text _title;
        private static RectTransform _cells;
        private static ScrollRect _scroll;
        private static Scrollbar _scrollbar;
        private static GameObject _viewportGo;
        private static GameObject _scrollAreaGo;
        private static RectTransform _editHost;
        private static Font _font;
        private static float _miss;
        private static float _panelRouteUntil;
        private static string _message = string.Empty;

        private static readonly List<PluginItem> _plugins = new List<PluginItem>();
        private static MVRScript _edit;
        private static string _editName;
        private static Transform _editPage;
        private static Transform _editPageParent;
        private static int _editPageSibling;
        private static Vector2 _editAnchorMin, _editAnchorMax;
        private static Vector2 _editOffsetMin, _editOffsetMax;
        private static Vector2 _editPivot;
        private static Vector3 _editLocalScale;
        private static CanvasGroup _editAlpha;
        private static bool _editAlphaOwned;
        private static float _editAlphaWas;
        private static GameObject _editCloseBtn;
        private static bool _editCloseBtnWas;
        private static GameObject _backButton;
        private static bool _viewWasEdit;
        private static ScrollRect _pageScroll;

        // While the pointer rests on this panel, locomotion is suppressed via
        // a GetFreeNavigateVector postfix that zeroes the nav vector — the
        // thumbstick then scrolls the active ScrollRect instead of moving
        // the user in space.
        internal static bool ConsumeNavigation
        {
            get { return PointerInside; }
        }

        internal static bool PointerInside
        {
            get
            {
                var go = VrPointerPresentation.CurrentLookTarget(true);
                return Active && _root != null && _root.gameObject.activeSelf &&
                    go != null && go.transform.IsChildOf(_root);
            }
        }

        internal static void Toggle()
        {
            if (Active) { Shutdown(); return; }
            ClothingRegionMode.Shutdown();
            HairDebugMode.Shutdown();
            Active = true; _edit = null; _miss = Time.unscaledTime;
            _message = "指向角色弹出插件面板";
        }

        internal static void Shutdown()
        {
            Active = false; _atom = null; _plugins.Clear();
            _panelRouteUntil = 0f;
            ReleasePage();
            _edit = null;
            if (_canvas != null && SuperController.singleton != null)
                SuperController.singleton.RemoveCanvas(_canvas);
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
            _canvas = null; _root = null; _cells = null; _title = null;
            _viewportGo = null; _scrollAreaGo = null; _editHost = null;
        }

        internal static void Tick()
        {
            if (!Active) return;
            try
            {
                if (PointerInside)
                {
                    _miss = Time.unscaledTime;
                    ArmPanelRoute();
                    ScrollFromStick();
                    return;
                }
                if (_root != null && _root.gameObject.activeSelf &&
                    Time.unscaledTime < _panelRouteUntil)
                {
                    _miss = Time.unscaledTime;
                    return;
                }
                var sc = SuperController.singleton;
                var cam = sc == null ? null : sc.rightControllerCamera;
                if (cam == null) return;
                var ray = cam.ViewportPointToRay(new Vector3(.5f, .5f, 0));
                Vector3 anchor;
                Atom atom = PickPerson(ray, out anchor);
                if (atom != null)
                {
                    _miss = Time.unscaledTime;
                    bool reopening = _root == null || !_root.gameObject.activeSelf;
                    if (atom != _atom || reopening)
                    {
                        ReleasePage();
                        _atom = atom; _anchor = anchor;
                        _edit = null;
                        Ensure(); Rebuild(); Place();
                        _root.gameObject.SetActive(true);
                        ArmPanelRoute();
                    }
                    return;
                }
                if (_root != null && Time.unscaledTime - _miss > .8f)
                    _root.gameObject.SetActive(false);
            }
            catch (Exception e)
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogError("插件感应：" + e);
                Shutdown();
            }
        }

        private static Atom PickPerson(Ray ray, out Vector3 anchor)
        {
            anchor = Vector3.zero;
            Atom hit = null; float nearest = float.MaxValue;
            foreach (var h in Physics.RaycastAll(ray, 8f))
            {
                var atom = h.collider.GetComponentInParent<Atom>();
                if (atom == null || !atom.on) continue;
                if (!(atom.GetStorableByID("geometry") is DAZCharacterSelector))
                    continue;
                if (h.distance < nearest) { nearest = h.distance; hit = atom; }
            }
            if (hit != null) anchor = ray.GetPoint(nearest);
            return hit;
        }

        private static void ArmPanelRoute()
        {
            _panelRouteUntil = Time.unscaledTime + PanelRouteGrace;
        }

        private static void ScrollFromStick()
        {
            // VaM's stick state rides OpenVR actions here — OVRInput reports
            // zeros on this stack, so the bridge is the primary source.
            float y = 0f;
            Vector2 right, left;
            if (OpenVrInputBridge.TryGetSticks(out right, out left))
                y = Mathf.Abs(right.y) >= Mathf.Abs(left.y) ? right.y : left.y;
            else
            {
                float ry = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick,
                    OVRInput.Controller.Touch).y;
                float ly = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick,
                    OVRInput.Controller.Touch).y;
                y = Mathf.Abs(ry) >= Mathf.Abs(ly) ? ry : ly;
            }
            if (Mathf.Abs(y) < .15f) return;
            var sr = _edit != null && _editPage != null ? _pageScroll : _scroll;
            if (sr == null) return;
            sr.verticalNormalizedPosition = Mathf.Clamp01(
                sr.verticalNormalizedPosition + y * .9f * Time.unscaledDeltaTime);
        }

        private static void Ensure()
        {
            if (_root != null) return;
            _font = (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");
            _root = new GameObject("Q3 Atom Plugins", typeof(RectTransform))
                .GetComponent<RectTransform>();
            _root.sizeDelta = new Vector2(720, 560);
            _root.localScale = Vector3.one * .00142f;
            _canvas = _root.gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = 32757;
            _root.gameObject.AddComponent<GraphicRaycaster>();
            _root.gameObject.AddComponent<Image>().color =
                new Color(.06f, .08f, .12f, .92f);
            SuperController.singleton.AddCanvas(_canvas);
            _title = Label(_root, "", 8, 5, 704, 52, 20);

            // Scrollable list: masked viewport + content, scrollbar on the
            // right edge — drag the thumb like the native plugin panel.
            var viewport = Rect(_root, "View", 8, 62, 672, 428);
            _viewportGo = viewport.gameObject;
            viewport.gameObject.AddComponent<RectMask2D>();
            _cells = Rect(viewport, "Content", 0, 0, 672, 428);
            _scroll = viewport.gameObject.AddComponent<ScrollRect>();
            _scroll.content = _cells;
            _scroll.viewport = viewport;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 30f;

            var scrollArea = Rect(_root, "Scroll", 686, 62, 26, 428);
            _scrollAreaGo = scrollArea.gameObject;
            var scrollImg = scrollArea.gameObject.AddComponent<Image>();
            scrollImg.color = new Color(.10f, .14f, .19f, .98f);
            var handle = Rect(scrollArea, "Handle", 2, 0, 22, 60);
            var handleImg = handle.gameObject.AddComponent<Image>();
            handleImg.color = new Color(.30f, .42f, .52f, 1f);
            _scrollbar = scrollArea.gameObject.AddComponent<Scrollbar>();
            _scrollbar.handleRect = handle;
            _scrollbar.targetGraphic = handleImg;
            _scrollbar.direction = Scrollbar.Direction.BottomToTop;
            _scroll.verticalScrollbar = _scrollbar;
            _scroll.verticalScrollbarVisibility =
                ScrollRect.ScrollbarVisibility.AutoHide;

            // Borrowed native plugin page lives here, same size as our panel
            // content area (mask clips overflow while it is reparented).
            _editHost = Rect(_root, "EditHost", 8, 62, 704, 428);
            _editHost.gameObject.AddComponent<Image>().color =
                new Color(.05f, .07f, .10f, .85f);
            _editHost.gameObject.AddComponent<RectMask2D>();
            _editHost.gameObject.SetActive(false);

            _backButton = Button(_root, "◀ 返回列表", 458, 500, 120, Back);
            Button(_root, "退出感应", 592, 500, 120, Shutdown);
        }

        private static void Rebuild()
        {
            if (_root == null) return;
            bool editing = _edit != null && _editPage != null;
            bool sameView = _viewWasEdit == editing;
            float keepPos = sameView && !editing && _scroll != null
                ? _scroll.verticalNormalizedPosition : 1f;
            _viewWasEdit = editing;
            if (_viewportGo != null) _viewportGo.SetActive(!editing);
            if (_scrollAreaGo != null) _scrollAreaGo.SetActive(!editing);
            if (_editHost != null) _editHost.gameObject.SetActive(editing);
            if (!editing)
            {
                foreach (Transform child in _cells)
                {
                    child.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(child.gameObject);
                }
                RebuildList();
                float contentH = Mathf.Max(428f,
                    _plugins.Count * (RowHeight + RowGap));
                _cells.sizeDelta = new Vector2(672f, contentH);
                _cells.anchoredPosition = Vector2.zero;
                if (_scrollbar != null)
                    _scrollbar.size = Mathf.Clamp(428f / contentH, 0.08f, 1f);
                if (_scroll != null)
                    _scroll.verticalNormalizedPosition = keepPos;
            }
            if (_backButton != null) _backButton.SetActive(editing);
            UpdateTitle();
        }

        private static void RebuildList()
        {
            _plugins.Clear();
            if (_atom != null && _atom.on)
            {
                foreach (string id in _atom.GetStorableIDs())
                {
                    var storable = _atom.GetStorableByID(id);
                    var script = storable as MVRScript;
                    if (script == null) continue;
                    _plugins.Add(new PluginItem
                    {
                        atom = _atom, id = id, script = script,
                        display = PluginDisplay(id)
                    });
                }
            }
            for (int index = 0; index < _plugins.Count; index++)
            {
                var item = _plugins[index];
                float y = index * (RowHeight + RowGap);
                var row = Rect(_cells, "Plugin", 0, y, 672, RowHeight);
                row.gameObject.AddComponent<Image>().color =
                    new Color(.14f, .18f, .24f, .96f);
                Label(row, item.display, 8, 0, 350, RowHeight, 16)
                    .alignment = TextAnchor.MiddleLeft;
                var enabled = item.script.enabledJSON;
                bool on = enabled != null && enabled.val;
                var toggle = Rect(row, "Enable", 362, 5, 110, 34);
                var tImg = toggle.gameObject.AddComponent<Image>();
                tImg.color = on
                    ? new Color(.08f, .5f, .28f, 1f)
                    : new Color(.30f, .12f, .12f, 1f);
                var tBtn = toggle.gameObject.AddComponent<UnityEngine.UI.Button>();
                tBtn.targetGraphic = tImg;
                var captured = item;
                tBtn.onClick.AddListener(delegate { TogglePlugin(captured); });
                Label(toggle, on ? "启用" : "停用", 0, 0, 110, 34, 16);
                var edit = Rect(row, "Edit", 478, 5, 120, 34);
                var eImg = edit.gameObject.AddComponent<Image>();
                eImg.color = new Color(.11f, .38f, .48f, 1f);
                var eBtn = edit.gameObject.AddComponent<UnityEngine.UI.Button>();
                eBtn.targetGraphic = eImg;
                eBtn.onClick.AddListener(delegate { OpenEditor(captured); });
                Label(edit, "打开 ▶", 0, 0, 120, 34, 16);
            }
            if (_plugins.Count == 0)
                Label(_cells, _atom == null ? "" : "该角色没有加载插件",
                    0, 80, 660, 80, 20);
        }

        private static string PluginDisplay(string id)
        {
            // Storable ids look like "plugin#3_AcidBubbles.Timeline.326".
            int u = id.IndexOf('_');
            string s = u >= 0 ? id.Substring(u + 1) : id;
            int dot = s.LastIndexOf('.');
            if (dot > 0)
            {
                bool digits = true;
                for (int i = dot + 1; i < s.Length; i++)
                    if (!char.IsDigit(s[i])) { digits = false; break; }
                if (digits && s.Length - dot - 1 >= 3)
                    s = s.Substring(0, dot);
            }
            return s;
        }

        private static void TogglePlugin(PluginItem item)
        {
            if (item == null || item.script == null) return;
            var enabled = item.script.enabledJSON;
            if (enabled != null) enabled.val = !enabled.val;
            Rebuild();
        }

        private static void OpenEditor(PluginItem item)
        {
            if (item == null || item.script == null) return;
            ReleasePage();
            var page = FindPluginPage(item.script);
            if (page == null)
            {
                _message = "找不到该插件的原生页面";
                UpdateTitle();
                return;
            }
            _edit = item.script;
            _editName = item.display;
            BorrowPage(page);
            Rebuild();
        }

        // The plugin's own UI page: MVRScript.InitUI locates its MVRScriptUI
        // under the script's UITransform (each storable has its own UI host).
        // Mirror that exactly — the page is normally inactive.
        private static Transform FindPluginPage(MVRScript script)
        {
            var host = script.UITransform;
            if (host == null) return null;
            var ui = host.GetComponentInChildren<MVRScriptUI>(true);
            return ui != null ? ui.transform : null;
        }

        private static void BorrowPage(Transform page)
        {
            _editPage = page;
            _editPageParent = page.parent;
            _editPageSibling = page.GetSiblingIndex();
            var prt = page as RectTransform;
            if (prt != null)
            {
                _editAnchorMin = prt.anchorMin;
                _editAnchorMax = prt.anchorMax;
                _editOffsetMin = prt.offsetMin;
                _editOffsetMax = prt.offsetMax;
                _editPivot = prt.pivot;
                _editLocalScale = prt.localScale;
            }
            var ui = page.GetComponent<MVRScriptUI>();
            if (ui != null && ui.closeButton != null)
            {
                _editCloseBtn = ui.closeButton.gameObject;
                _editCloseBtnWas = _editCloseBtn.activeSelf;
                _editCloseBtn.SetActive(false);
            }
            _editAlpha = page.GetComponent<CanvasGroup>();
            _editAlphaOwned = _editAlpha == null;
            if (_editAlphaOwned)
                _editAlpha = page.gameObject.AddComponent<CanvasGroup>();
            _editAlphaWas = _editAlpha.alpha;
            _editAlpha.alpha = PageAlpha;
            // Keep the page's native size/layout and uniformly scale it to
            // fit our host — center-anchored, RectMask2D clips any overflow.
            Vector2 native = Vector2.zero;
            if (prt != null)
            {
                native = prt.rect.size;
                if (native.x < 10f || native.y < 10f)
                    native = prt.sizeDelta;
            }
            // The native page may host several ScrollRects — drive the one
            // with the tallest content (the main vertical scroller).
            _pageScroll = null;
            var srs = page.GetComponentsInChildren<ScrollRect>(true);
            float bestH = 0f;
            foreach (var sr in srs)
            {
                float h = sr != null && sr.content != null
                    ? sr.content.rect.height : 0f;
                if (h > bestH) { bestH = h; _pageScroll = sr; }
            }
            page.SetParent(_editHost, false);
            float fit = 1f;
            if (prt != null)
            {
                prt.anchorMin = prt.anchorMax = new Vector2(.5f, .5f);
                prt.pivot = new Vector2(.5f, .5f);
                prt.anchoredPosition = Vector2.zero;
                if (native.x > 10f && native.y > 10f)
                {
                    prt.sizeDelta = native;
                    // Fill host height fully; stretch horizontally ~1.35x so
                    // the page uses more of the panel width without heavy
                    // distortion (native pages are portrait ~1080x1300).
                    float fitY = Mathf.Min(428f / native.y, 1f);
                    float fitX = Mathf.Min(fitY * 1.35f, 704f / native.x);
                    fit = fitY;
                    prt.localScale = new Vector3(fitX, fitY, 1f);
                }
            }
            page.gameObject.SetActive(true);
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("插件页: " + _editName +
                    " native=" + native.x + "x" + native.y +
                    " fit=" + fit.ToString("0.###"));
        }

        private static void ReleasePage()
        {
            var page = _editPage;
            _editPage = null;
            _pageScroll = null;
            if (page == null) return;
            page.gameObject.SetActive(false);
            var ui = page.GetComponent<MVRScriptUI>();
            if (ui != null && ui.closeButton != null)
                ui.closeButton.gameObject.SetActive(_editCloseBtnWas);
            _editCloseBtn = null;
            if (_editAlpha != null)
            {
                _editAlpha.alpha = _editAlphaWas;
                if (_editAlphaOwned)
                    UnityEngine.Object.Destroy(_editAlpha);
                _editAlpha = null;
            }
            page.SetParent(_editPageParent, false);
            page.SetSiblingIndex(_editPageSibling);
            var prt = page as RectTransform;
            if (prt != null)
            {
                prt.anchorMin = _editAnchorMin;
                prt.anchorMax = _editAnchorMax;
                prt.offsetMin = _editOffsetMin;
                prt.offsetMax = _editOffsetMax;
                prt.pivot = _editPivot;
                prt.localScale = _editLocalScale;
            }
            _editPageParent = null;
        }

        private static void UpdateTitle()
        {
            if (_title == null) return;
            if (_edit != null && _editPage != null)
                _title.text = "◀ " + _editName + " · 原生页面  " + _message;
            else
                _title.text = (_atom != null ? _atom.uid : "") + " · " +
                    _plugins.Count + " 个插件  " + _message;
        }

        private static void Place()
        {
            var sc = SuperController.singleton;
            Transform head = sc.centerCameraTarget != null
                ? sc.centerCameraTarget.transform : sc.lookCamera.transform;
            Vector3 right = head.right; right.y = 0; right.Normalize();
            _root.position = _anchor + right * .88f;
            _root.rotation = Quaternion.LookRotation(_root.position - head.position);
        }

        // Back to the plugin list from the editor: reuse 上一页 row area via a
        // dedicated back button drawn over the title when editing.
        private static GameObject Button(Transform parent, string label,
            float x, float y, float width, Action action)
        {
            return Button(parent, label, x, y, width, action, 42);
        }

        private static GameObject Button(Transform parent, string label,
            float x, float y, float width, Action action, float height)
        {
            var rt = Rect(parent, label, x, y, width, height);
            var image = rt.gameObject.AddComponent<Image>();
            image.color = new Color(.16f, .23f, .3f, .96f);
            var button = rt.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(delegate { action(); });
            Label(rt, label, 0, 0, width, height, 20);
            return rt.gameObject;
        }

        private static void Back()
        {
            ReleasePage();
            _edit = null;
            Rebuild();
        }

        private static RectTransform Rect(Transform parent, string name,
            float x, float y, float width, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(width, height);
            return rt;
        }

        private static Text Label(Transform parent, string text,
            float x, float y, float width, float height, int size)
        {
            var rt = Rect(parent, "Label", x, y, width, height);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = _font; t.text = text; t.fontSize = size;
            t.color = Color.white; t.raycastTarget = false;
            t.alignment = TextAnchor.MiddleCenter;
            return t;
        }
    }
}
