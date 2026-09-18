using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using GPUTools.Physics.Scripts.Types.Dynamic;
using GPUTools.Hair.Scripts.Runtime.Render;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    // Manual physics-budget mode: point the right controller ray at a hair
    // group to select it (faint bounds-edge highlight), then tweak its sim
    // storables from a floating panel pinned beside the hair. Toggled from
    // the quick menu "物理降载/手动" button.
    internal static class HairDebugMode
    {
        private const float RescanSeconds = 0.6f;
        private const float DeselectSeconds = 0.5f;
        private const float PanelScale = 0.0009f;
        private const float PanelSideGap = 0.22f;

        internal static bool Active { get; private set; }

        private static HairSimControl[] _cache;
        private static float _nextScan;
        private static HairSimControl _selected;
        private static float _noHitSince = -1f;
        private sealed class Geometry
        {
            internal AsyncGPUReadbackRequest request;
            internal bool pending, valid;
            internal ComputeBuffer buffer;
            internal float nextRead, updated;
            internal int segments, count;
            internal Vector3[] points = new Vector3[0];
            internal Bounds bounds;
        }
        private static readonly Dictionary<HairSimControl, Geometry> _geometry =
            new Dictionary<HairSimControl, Geometry>();
        private static int _readCursor;
        private static bool _unsupportedLogged;
        private static float _nextReadBatch;
        private static readonly List<HairSimControl> _removed = new List<HairSimControl>();

        private static GameObject _canvasGo;
        private static Canvas _canvas;
        private static Text _title;
        private static Slider _curlSlider;
        private static Slider _densitySlider;
        private static Text _curlValue;
        private static Text _densityValue;
        private static Toggle _simToggle;
        private static Toggle _collideToggle;
        private static Font _font;
        private static bool _binding;

        private static GameObject _boxGo;
        private static LineRenderer _box;

        private static JSONStorableFloat _curlStorable;
        private static JSONStorableFloat _densityStorable;
        private static JSONStorableBool _simStorable;
        private static JSONStorableBool _collideStorable;

        internal static void Toggle()
        {
            Active = !Active;
            if (Active)
            {
                // Automatic LOD must not overwrite the values being edited.
                PhysicsBudget.SetLevelEntry(0);
                _nextScan = 0f;
            }
            if (!Active)
                Teardown();
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo(
                    "Q3 hair debug mode " + (Active ? "ON" : "OFF"));
        }

        internal static void Shutdown()
        {
            Active = false;
            Teardown();
        }

        private static float _nextExDiag;

        internal static void Tick()
        {
            if (!Active)
                return;
            try
            {
                TickInner();
            }
            catch (Exception e)
            {
                if (Quest3TriggerUIPlugin.Log != null &&
                    Time.unscaledTime >= _nextExDiag)
                {
                    _nextExDiag = Time.unscaledTime + 2f;
                    Quest3TriggerUIPlugin.Log.LogInfo(
                        "Q3 hairdbg EX: " + e.GetType().Name + " " + e.Message +
                        "\n" + e.StackTrace);
                }
                // Keep the mode alive on transient failures — drop the
                // current selection instead of forcing the user to re-arm.
                try { Unbind(); } catch (Exception) { }
                HideVisuals();
            }
        }

        private static void TickInner()
        {
            if (Time.unscaledTime >= _nextScan)
            {
                _cache = UnityEngine.Object.FindObjectsOfType<HairSimControl>();
                _nextScan = Time.unscaledTime + RescanSeconds;
                _removed.Clear();
                foreach (var pair in _geometry)
                    if (pair.Key == null || Array.IndexOf(_cache, pair.Key) < 0) _removed.Add(pair.Key);
                foreach (var key in _removed) _geometry.Remove(key);
            }
            if (_cache == null)
                return;

            UpdateGeometry();
            Ray ray;
            bool haveRay = TryGetHairRay(out ray);
            bool overPanel = PointerOverPanel();
            HairSimControl hit = null;
            float best = float.MaxValue;
            if (haveRay && !overPanel)
            {
                for (int i = 0; i < _cache.Length; i++)
                {
                    HairSimControl sc = _cache[i];
                    Geometry g;
                    if (sc == null || !sc.isActiveAndEnabled ||
                        !_geometry.TryGetValue(sc, out g) || !g.valid ||
                        Time.unscaledTime - g.updated > 1.5f) continue;
                    float depth;
                    Bounds padded = g.bounds; padded.Expand(0.05f);
                    if (!padded.IntersectRay(ray, out depth)) continue;
                    // Compare actual strand segments, not overlapping giant renderer bounds.
                    for (int j = 1; j < g.count; j++)
                    {
                        if (j % g.segments == 0) continue;
                        float distance = SegmentDistance(ray, g.points[j-1], g.points[j], out depth);
                        if (depth <= 0f || distance > 0.025f) continue;
                        float score = depth + distance * 2f;
                        if (score < best) { best = score; hit = sc; }
                    }
                }
            }
            if (overPanel) _noHitSince = -1f;

            if (hit != null)
            {
                _noHitSince = -1f;
                if (_selected != hit)
                    Bind(hit);
            }
            else if (!overPanel)
            {
                if (_noHitSince < 0f)
                    _noHitSince = Time.unscaledTime;
                else if (Time.unscaledTime - _noHitSince > DeselectSeconds)
                    Unbind();
            }

            if (_selected != null)
            {
                Bounds b;
                if (TryGetBounds(_selected, out b))
                {
                    UpdateBox(b);
                    if (!overPanel) PositionPanel(b);
                    SyncValues();
                    return;
                }
                Unbind();
            }
            HideVisuals();
        }

        private static bool TryGetHairRay(out Ray ray)
        {
            // referenceCamera is overwritten by VaM's monitor/UI pass; it is
            // not a persistent right-hand source, even when it returns a valid ray.
            SuperController sc = SuperController.singleton;
            Camera camera = sc == null ? null : sc.rightControllerCamera;
            ray = camera == null ? new Ray() :
                camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            return camera != null && ray.direction.sqrMagnitude > 0.5f;
        }

        private static bool PointerOverPanel()
        {
            if (_canvasGo == null)
                return false;
            GameObject right = VrPointerPresentation.CurrentLookTarget(true);
            GameObject left = VrPointerPresentation.CurrentLookTarget(false);
            return (right != null && right.transform.IsChildOf(_canvasGo.transform)) ||
                   (left != null && left.transform.IsChildOf(_canvasGo.transform));
        }

        // Closest distance between a forward ray and a finite strand segment.
        internal static float SegmentDistance(Ray ray, Vector3 a, Vector3 b, out float depth)
        {
            Vector3 v = b-a, w = ray.origin-a;
            float c = Vector3.Dot(v,v), dv = Vector3.Dot(ray.direction,v);
            float dw = Vector3.Dot(ray.direction,w), vw = Vector3.Dot(v,w);
            float denom = c-dv*dv;
            float u = denom > 0.00000001f ? Mathf.Clamp01((vw-dv*dw)/denom) : 0f;
            depth = Mathf.Max(0f, dv*u-dw);
            if (depth == 0f && c > 0f) u = Mathf.Clamp01(vw/c);
            return Vector3.Distance(ray.GetPoint(depth), a+v*u);
        }

        private static void UpdateGeometry()
        {
            int pending = 0;
            foreach (var pair in _geometry)
            {
                Geometry g = pair.Value;
                if (!g.pending) continue;
                if (!g.request.done) { pending++; continue; }
                g.pending = false;
                if (pair.Key == null || g.request.hasError) { g.valid = false; continue; }
                var settings = pair.Key.hairSettings;
                if (settings == null || settings.RuntimeData == null ||
                    settings.RuntimeData.Particles == null ||
                    settings.RuntimeData.Particles.ComputeBuffer != g.buffer)
                { g.valid = false; continue; }
                var data = g.request.GetData<GPParticle>();
                int strands = data.Length / g.segments;
                int step = Mathf.Max(1, Mathf.CeilToInt(strands * g.segments / 4096f));
                int needed = ((strands + step - 1) / step) * g.segments;
                if (g.points.Length != needed) g.points = new Vector3[needed];
                g.count = 0; g.valid = false;
                for (int strand = 0; strand < strands; strand += step)
                    for (int j = 0; j < g.segments; j++)
                    {
                        // BuildParticles transforms Position with GetToWorldMatrix;
                        // compute simulation continues in world coordinates.
                        Vector3 p = data[strand*g.segments+j].Position;
                        if (float.IsNaN(p.sqrMagnitude) || float.IsInfinity(p.sqrMagnitude))
                        { g.count = 0; g.valid = false; return; }
                        g.points[g.count++] = p;
                        if (!g.valid) { g.bounds = new Bounds(p, Vector3.zero); g.valid = true; }
                        else g.bounds.Encapsulate(p);
                    }
                g.updated = Time.unscaledTime;
            }
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                if (!_unsupportedLogged && Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogWarning("Hair manual mode requires async GPU readback.");
                _unsupportedLogged = true; return;
            }
            if (Time.unscaledTime < _nextReadBatch) return;
            _nextReadBatch = Time.unscaledTime + 1f / 30f;
            // At most two asynchronous requests in flight; never synchronous GPU GetData/Wait.
            for (int n = 0; n < _cache.Length && pending < 2; n++)
            {
                HairSimControl sc;
                if (n == 0 && _selected != null) sc = _selected;
                else { sc = _cache[_readCursor % _cache.Length]; _readCursor = (_readCursor + 1) % _cache.Length; }
                if (sc == null || !sc.isActiveAndEnabled || sc.hairSettings == null) continue;
                var settings = sc.hairSettings;
                if (settings.RuntimeData == null || settings.RuntimeData.Particles == null ||
                    settings.StandsSettings == null) continue;
                Geometry g;
                if (!_geometry.TryGetValue(sc, out g)) _geometry[sc] = g = new Geometry();
                if (g.pending || Time.unscaledTime < g.nextRead) continue;
                var buffer = settings.RuntimeData.Particles.ComputeBuffer;
                if (buffer == null || settings.StandsSettings.Segments < 2) continue;
                g.segments = settings.StandsSettings.Segments;
                g.buffer = buffer;
                g.request = AsyncGPUReadback.Request(buffer);
                g.pending = true; g.nextRead = Time.unscaledTime + 0.12f; pending++;
            }
        }

        private static bool TryGetBounds(HairSimControl sc, out Bounds b)
        {
            Geometry g;
            b = new Bounds();
            if (sc == null || !sc.isActiveAndEnabled || !_geometry.TryGetValue(sc, out g) ||
                !g.valid || Time.unscaledTime - g.updated > 1.5f) return false;
            b = g.bounds;
            return true;
        }

        private static void SyncValues()
        {
            _binding = true;
            BindSlider(_curlSlider, _curlStorable, _curlValue);
            BindSlider(_densitySlider, _densityStorable, _densityValue);
            BindToggle(_simToggle, _simStorable);
            BindToggle(_collideToggle, _collideStorable);
            _binding = false;
        }

        private static void Bind(HairSimControl sc)
        {
            Unbind();
            _selected = sc;
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("Hair manual selected: " + sc.name);
            _curlStorable = sc.GetFloatJSONParam("curveDensity");
            _densityStorable = sc.GetFloatJSONParam("hairMultiplier");
            _simStorable = sc.GetBoolJSONParam("simulationEnabled");
            _collideStorable = sc.GetBoolJSONParam("collisionEnabled");
            EnsurePanel();
            _binding = true;
            if (_title != null)
                _title.text = "头发调试: " + Describe(sc);
            BindSlider(_curlSlider, _curlStorable, _curlValue);
            BindSlider(_densitySlider, _densityStorable, _densityValue);
            BindToggle(_simToggle, _simStorable);
            BindToggle(_collideToggle, _collideStorable);
            _binding = false;
        }

        private static void Unbind()
        {
            _curlStorable = _densityStorable = null;
            _simStorable = _collideStorable = null;
            _selected = null;
        }

        private static string Describe(HairSimControl sc)
        {
            DAZHairGroupControl gc = sc.GetComponentInParent<DAZHairGroupControl>();
            DAZHairGroup item = gc != null ? gc.hairItem : null;
            string itemName = item != null && !string.IsNullOrEmpty(item.displayName)
                ? item.displayName
                : (item != null ? item.name : sc.transform.root.name);
            return itemName + " / " + sc.gameObject.name;
        }

        private static void BindSlider(Slider s, JSONStorableFloat st, Text valText)
        {
            if (s == null)
                return;
            if (st == null)
            {
                s.interactable = false;
                if (valText != null) valText.text = "-";
                return;
            }
            s.interactable = true;
            s.minValue = st.min;
            s.maxValue = st.max;
            s.value = st.val;
            if (valText != null)
                valText.text = st.val.ToString("F2");
        }

        private static void BindToggle(Toggle t, JSONStorableBool st)
        {
            if (t == null)
                return;
            if (st == null)
            {
                t.interactable = false;
                return;
            }
            t.interactable = true;
            t.isOn = st.val;
        }

        private static void OnSlider(JSONStorableFloat st, Text valText, float v)
        {
            if (_binding || st == null)
                return;
            st.val = v;
            if (valText != null)
                valText.text = v.ToString("F2");
        }

        private static void OnToggle(JSONStorableBool st, bool v)
        {
            if (_binding || st == null)
                return;
            st.val = v;
        }

        private static void EnsurePanel()
        {
            if (_canvasGo != null)
                return;
            _font = (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");
            _canvasGo = new GameObject("Q3 Hair Debug Panel");
            _canvas = _canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.pixelPerfect = false;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = 32758;
            _canvasGo.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1f;
            _canvasGo.AddComponent<GraphicRaycaster>();
            RectTransform root = _canvasGo.GetComponent<RectTransform>();
            root.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 620f);
            root.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 420f);
            _canvasGo.transform.localScale = Vector3.one * PanelScale;
            if (SuperController.singleton != null)
                SuperController.singleton.AddCanvas(_canvas);

            Image bg = _canvasGo.AddComponent<Image>();
            bg.color = new Color(0.10f, 0.11f, 0.14f, 0.92f);

            _title = MakeText(root, 16f, -10f, 588f, 44f, "头发调试", 30f,
                TextAnchor.MiddleLeft, new Color(0.95f, 0.80f, 0.35f));

            MakeText(root, 16f, -64f, 130f, 36f, "卷曲密度", 26f,
                TextAnchor.MiddleLeft, Color.white);
            _curlSlider = MakeSlider(root, 150f, -60f, 360f);
            _curlSlider.onValueChanged.AddListener(
                delegate(float v) { OnSlider(_curlStorable, _curlValue, v); });
            _curlValue = MakeText(root, 520f, -64f, 84f, 36f, "-", 24f,
                TextAnchor.MiddleRight, new Color(0.7f, 0.9f, 1f));

            MakeText(root, 16f, -124f, 130f, 36f, "发量乘数", 26f,
                TextAnchor.MiddleLeft, Color.white);
            _densitySlider = MakeSlider(root, 150f, -120f, 360f);
            _densitySlider.onValueChanged.AddListener(
                delegate(float v) { OnSlider(_densityStorable, _densityValue, v); });
            _densityValue = MakeText(root, 520f, -124f, 84f, 36f, "-", 24f,
                TextAnchor.MiddleRight, new Color(0.7f, 0.9f, 1f));

            _simToggle = MakeToggle(root, 30f, -190f, "模拟");
            _simToggle.onValueChanged.AddListener(
                delegate(bool v) { OnToggle(_simStorable, v); });
            _collideToggle = MakeToggle(root, 240f, -190f, "碰撞");
            _collideToggle.onValueChanged.AddListener(
                delegate(bool v) { OnToggle(_collideStorable, v); });

            MakeText(root, 16f, -330f, 588f, 60f,
                "光标指向另一片头发即可切换；再次点击「手动」退出调试。",
                22f, TextAnchor.UpperLeft, new Color(0.65f, 0.68f, 0.72f));

            _canvasGo.SetActive(false);
            EnsureBox();
        }

        private static Text MakeText(RectTransform parent, float x, float y,
            float w, float h, string text, float size, TextAnchor anchor, Color color)
        {
            GameObject go = new GameObject("T");
            go.transform.SetParent(parent, false);
            Text t = go.AddComponent<Text>();
            t.font = _font;
            t.text = text;
            t.fontSize = Mathf.RoundToInt(size);
            t.alignment = anchor;
            t.color = color;
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, w);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, h);
            return t;
        }

        private static Slider MakeSlider(RectTransform parent, float x, float y, float w)
        {
            GameObject go = new GameObject("Slider", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, w);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 44f);

            GameObject bgGo = new GameObject("Background");
            bgGo.transform.SetParent(rt, false);
            Image bgImg = bgGo.AddComponent<Image>();
            bgImg.color = new Color(0.22f, 0.24f, 0.28f, 1f);
            RectTransform bgRt = bgGo.GetComponent<RectTransform>();
            bgRt.anchorMin = new Vector2(0f, 0.5f);
            bgRt.anchorMax = new Vector2(1f, 0.5f);
            bgRt.pivot = new Vector2(0.5f, 0.5f);
            bgRt.sizeDelta = new Vector2(0f, 10f);

            GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(rt, false);
            RectTransform fillAreaRt = fillArea.GetComponent<RectTransform>();
            fillAreaRt.anchorMin = new Vector2(0f, 0.5f);
            fillAreaRt.anchorMax = new Vector2(1f, 0.5f);
            fillAreaRt.pivot = new Vector2(0.5f, 0.5f);
            fillAreaRt.sizeDelta = new Vector2(-18f, 10f);
            GameObject fill = new GameObject("Fill");
            fill.transform.SetParent(fillAreaRt, false);
            Image fillImg = fill.AddComponent<Image>();
            fillImg.color = new Color(0.25f, 0.55f, 0.90f, 1f);
            RectTransform fillRt = fill.GetComponent<RectTransform>();
            fillRt.anchorMin = new Vector2(0f, 0f);
            fillRt.anchorMax = new Vector2(0f, 1f);
            fillRt.pivot = new Vector2(0f, 0.5f);
            fillRt.sizeDelta = new Vector2(10f, 0f);

            GameObject handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(rt, false);
            RectTransform handleAreaRt = handleArea.GetComponent<RectTransform>();
            handleAreaRt.anchorMin = new Vector2(0f, 0f);
            handleAreaRt.anchorMax = new Vector2(1f, 1f);
            handleAreaRt.sizeDelta = new Vector2(-20f, 0f);
            GameObject handle = new GameObject("Handle");
            handle.transform.SetParent(handleAreaRt, false);
            Image handleImg = handle.AddComponent<Image>();
            handleImg.color = new Color(0.92f, 0.93f, 0.96f, 1f);
            RectTransform handleRt = handle.GetComponent<RectTransform>();
            handleRt.sizeDelta = new Vector2(22f, 34f);

            Slider s = go.AddComponent<Slider>();
            s.fillRect = fillRt;
            s.handleRect = handleRt;
            s.targetGraphic = handleImg;
            s.direction = Slider.Direction.LeftToRight;
            return s;
        }

        private static Toggle MakeToggle(RectTransform parent, float x, float y, string label)
        {
            GameObject go = new GameObject("Toggle_" + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 200f);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50f);

            GameObject boxGo = new GameObject("Box");
            boxGo.transform.SetParent(rt, false);
            Image boxImg = boxGo.AddComponent<Image>();
            boxImg.color = new Color(0.22f, 0.24f, 0.28f, 1f);
            RectTransform boxRt = boxGo.GetComponent<RectTransform>();
            boxRt.anchorMin = new Vector2(0f, 0.5f);
            boxRt.anchorMax = new Vector2(0f, 0.5f);
            boxRt.pivot = new Vector2(0f, 0.5f);
            boxRt.anchoredPosition = new Vector2(25f, 0f);
            boxRt.sizeDelta = new Vector2(40f, 40f);

            GameObject check = new GameObject("Check");
            check.transform.SetParent(boxRt, false);
            Image checkImg = check.AddComponent<Image>();
            checkImg.color = new Color(0.30f, 0.75f, 0.40f, 1f);
            RectTransform checkRt = check.GetComponent<RectTransform>();
            checkRt.anchorMin = new Vector2(0f, 0f);
            checkRt.anchorMax = new Vector2(1f, 1f);
            checkRt.sizeDelta = new Vector2(-10f, -10f);

            Text t = MakeText(rt, 60f, -8f, 140f, 36f, label, 26f,
                TextAnchor.MiddleLeft, Color.white);

            Toggle toggle = go.AddComponent<Toggle>();
            toggle.graphic = checkImg;
            toggle.targetGraphic = boxImg;
            return toggle;
        }

        private static void EnsureBox()
        {
            if (_boxGo != null)
                return;
            _boxGo = new GameObject("Q3 Hair Highlight");
            _box = _boxGo.AddComponent<LineRenderer>();
            _box.useWorldSpace = true;
            _box.positionCount = 16;
            _box.startWidth = _box.endWidth = 0.0022f;
            _box.startColor = _box.endColor = new Color(1f, 1f, 1f, 0.32f);
            Material m = new Material(Shader.Find("Sprites/Default"));
            m.color = new Color(1f, 1f, 1f, 0.32f);
            _box.material = m;
            _box.enabled = false;
        }

        private static void UpdateBox(Bounds b)
        {
            EnsureBox();
            Vector3 c = b.center;
            Vector3 e = b.extents;
            Vector3[] p = new Vector3[8];
            p[0] = c + new Vector3(-e.x, -e.y, -e.z);
            p[1] = c + new Vector3( e.x, -e.y, -e.z);
            p[2] = c + new Vector3( e.x, -e.y,  e.z);
            p[3] = c + new Vector3(-e.x, -e.y,  e.z);
            p[4] = c + new Vector3(-e.x,  e.y, -e.z);
            p[5] = c + new Vector3( e.x,  e.y, -e.z);
            p[6] = c + new Vector3( e.x,  e.y,  e.z);
            p[7] = c + new Vector3(-e.x,  e.y,  e.z);
            // Single stroke covering all 12 edges (some corners retraced).
            _box.SetPositions(new Vector3[] {
                p[0], p[1], p[2], p[3], p[0],
                p[4], p[5], p[6], p[7], p[4],
                p[5], p[1], p[2], p[6], p[7], p[3]
            });
            _box.enabled = true;
        }

        private static void PositionPanel(Bounds b)
        {
            if (_canvasGo == null)
                return;
            SuperController sc = SuperController.singleton;
            Transform head = sc != null && sc.centerCameraTarget != null
                ? sc.centerCameraTarget.transform : null;
            Vector3 right = head != null ? head.right : Vector3.right;
            Vector3 flat = new Vector3(right.x, 0f, right.z);
            if (flat.sqrMagnitude < 0.01f)
                flat = Vector3.right;
            flat.Normalize();
            float offset = Vector3.Dot(new Vector3(Mathf.Abs(flat.x), Mathf.Abs(flat.y), Mathf.Abs(flat.z)), b.extents) + 620f * PanelScale * 0.5f + PanelSideGap;
            _canvasGo.transform.position = b.center + flat * offset;
            if (head != null)
            {
                Vector3 fwd = _canvasGo.transform.position - head.position;
                if (fwd.sqrMagnitude > 0.001f)
                    _canvasGo.transform.rotation = Quaternion.LookRotation(fwd);
            }
            if (!_canvasGo.activeSelf)
                _canvasGo.SetActive(true);
        }

        private static void HideVisuals()
        {
            if (_box != null)
                _box.enabled = false;
            if (_canvasGo != null && _canvasGo.activeSelf)
                _canvasGo.SetActive(false);
        }

        private static void Teardown()
        {
            Unbind();
            if (_canvas != null && SuperController.singleton != null)
            {
                try { SuperController.singleton.RemoveCanvas(_canvas); }
                catch (Exception) { }
            }
            if (_canvasGo != null)
                UnityEngine.Object.Destroy(_canvasGo);
            if (_box != null && _box.sharedMaterial != null)
                UnityEngine.Object.Destroy(_box.sharedMaterial);
            if (_boxGo != null)
                UnityEngine.Object.Destroy(_boxGo);
            _canvasGo = null;
            _canvas = null;
            _boxGo = null;
            _box = null;
            _noHitSince = -1f;
            _geometry.Clear(); _removed.Clear(); _cache = null; _readCursor = 0; _nextReadBatch = 0f;
        }
    }
}
