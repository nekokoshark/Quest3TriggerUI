using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    internal sealed class QuickActionDefinition
    {
        internal QuickActionDefinition(string label, Action action)
            : this(label, action, null)
        {
        }

        internal QuickActionDefinition(string label, Action action, Func<bool> activeState)
            : this(label, action, activeState, null)
        {
        }

        internal QuickActionDefinition(
            string label, Action action, Func<bool> activeState,
            IList<QuickActionDefinition> children)
            : this(label, label, action, activeState, children) { }

        internal QuickActionDefinition(string id, string label, Action action)
            : this(id, label, action, null, null) { }
        internal QuickActionDefinition(string id, string label, Action action,
            Func<bool> activeState)
            : this(id, label, action, activeState, null) { }

        internal QuickActionDefinition(string id, string label, Action action,
            Func<bool> activeState, IList<QuickActionDefinition> children)
        {
            Id = id;
            Label = label;
            Action = action;
            ActiveState = activeState;
            Children = children;
        }

        internal string Id { get; private set; }
        internal string Label { get; private set; }
        internal Action Action { get; private set; }
        internal Func<bool> ActiveState { get; private set; }
        internal IList<QuickActionDefinition> Children { get; private set; }
        internal bool HasChildren
        {
            get { return Children != null && Children.Count > 0; }
        }
    }

    internal sealed class VrRadialMenu
    {
        private const float CanvasSize = 1000f;
        // Wedge-ring geometry (canvas units). Main ring tiles seamlessly —
        // buttons can never overlap; submenu is a second ring subdividing the
        // parent's angular span. Sprite textures cover [-MaxR,+MaxR]².
        private const float WedgeInnerR = 150f;
        private const float WedgeOuterR = 430f;
        private const float SubInnerR = 437f;
        private const float SubOuterR = 650f;
        private const float Sub2InnerR = 657f;
        private const float Sub2OuterR = 860f;
        private const float WedgeGapDeg = 2f;
        private IList<QuickActionDefinition> _actions;
        private readonly float _scale;
        private readonly float _distance;
        private readonly float _opacity;
        private readonly List<Image> _buttonImages = new List<Image>();
        private readonly List<Button> _buttons = new List<Button>();
        private readonly List<GameObject> _submenuRoots = new List<GameObject>();
        private readonly List<Image> _submenuImages = new List<Image>();
        private readonly List<Button> _submenuButtons = new List<Button>();
        private readonly List<int> _submenuParents = new List<int>();
        private readonly List<int> _submenuChildren = new List<int>();
        private readonly List<GameObject> _sub2Roots = new List<GameObject>();
        private readonly List<Image> _sub2Images = new List<Image>();
        private readonly List<Button> _sub2Buttons = new List<Button>();
        private readonly List<int> _sub2Parents = new List<int>();
        private readonly List<int> _sub2Children = new List<int>();
        private readonly List<int> _sub2Grands = new List<int>();
        private static readonly Color HoverColor =
            new Color(0.96f, 0.62f, 0.10f, 1f);
        private Canvas _canvas;
        private Font _font;
        private int _hoveredIndex = -1;
        private int _submenuParentIndex = -1;
        private int _submenuHoveredIndex = -1;
        private int _sub2ChildIndex = -1;
        private int _sub2HoveredIndex = -1;
        private bool _pinMode;
        private Text _centerText;
        private Image _centerImage;
        private VrPinnedActionTiles _pinnedTiles;
        private static Sprite _discSprite;
        private static readonly Dictionary<string, Sprite> _wedgeSprites =
            new Dictionary<string, Sprite>();

        // Programmatic sprites: soft-edged disc for the menu base and center
        // button, per-span wedge for ring buttons. Wedges tile the ring so
        // buttons cannot overlap, and alphaHitTestMinimumThreshold keeps the
        // transparent wedge margins from eating neighbour rays.
        private static Sprite DiscSprite
        {
            get
            {
                if (_discSprite == null)
                {
                    const int n = 256;
                    float half = n * 0.5f;
                    var px = new Color32[n * n];
                    for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = x + 0.5f - half, dy = y + 0.5f - half;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp01((half - 2f - d) / 3f);
                        px[y * n + x] = new Color32(255, 255, 255,
                            (byte)(a * 255f));
                    }
                    var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
                    tex.SetPixels32(px);
                    tex.Apply();
                    _discSprite = Sprite.Create(tex, new Rect(0, 0, n, n),
                        new Vector2(0.5f, 0.5f), 100f);
                }
                return _discSprite;
            }
        }

        // Wedge pointing at +Y, spanning `spanDeg` around it, in the ring
        // [innerR, outerR]. The texture is cropped to the wedge's own
        // bounding box and the sprite pivot sits at the ring's circle centre,
        // so the owning RectTransform covers only its wedge — this is what
        // keeps 1400-unit quads from stacking (overdraw) and lets raycasts
        // reach the button actually under the ray.
        private static Sprite WedgeSprite(
            float spanDeg, float innerR, float outerR,
            out Vector2 pivot, out Vector2 sizeUnits)
        {
            float halfSpanDeg = Mathf.Min((spanDeg - WedgeGapDeg) * 0.5f, 179f);
            float hs = halfSpanDeg * Mathf.Deg2Rad;
            float xMax = outerR * Mathf.Sin(Mathf.Min(hs, Mathf.PI * 0.5f));
            float yMin = hs < Mathf.PI * 0.5f
                ? innerR * Mathf.Cos(hs) : outerR * Mathf.Cos(hs);
            sizeUnits = new Vector2(2f * xMax, outerR - yMin);
            pivot = new Vector2(0.5f, -yMin / sizeUnits.y);
            string key = Mathf.RoundToInt(spanDeg * 10f) + ":" +
                Mathf.RoundToInt(innerR) + ":" + Mathf.RoundToInt(outerR);
            Sprite cached;
            if (_wedgeSprites.TryGetValue(key, out cached)) return cached;
            const float ppu = 0.55f; // px per canvas unit
            int w = Mathf.Max(4, Mathf.CeilToInt(sizeUnits.x * ppu));
            int h = Mathf.Max(4, Mathf.CeilToInt(sizeUnits.y * ppu));
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float wx = (x + 0.5f) / ppu - xMax;
                float wy = yMin + (y + 0.5f) / ppu;
                float r = Mathf.Sqrt(wx * wx + wy * wy);
                float ang = Mathf.Atan2(wx, wy) * Mathf.Rad2Deg;
                float edgeR = Mathf.Min(outerR - r, r - innerR);
                float edgeA = (halfSpanDeg - Mathf.Abs(ang)) * Mathf.Deg2Rad * r;
                float a = Mathf.Clamp01(Mathf.Min(edgeR, edgeA) / 1.5f);
                px[y * w + x] = new Color32(255, 255, 255,
                    (byte)(a * 255f));
            }
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.SetPixels32(px);
            t.Apply();
            cached = Sprite.Create(t, new Rect(0, 0, w, h), pivot, ppu);
            _wedgeSprites[key] = cached;
            return cached;
        }

        internal VrRadialMenu(
            IList<QuickActionDefinition> actions, float scale, float distance,
            float opacity)
        {
            _actions = actions;
            if (_pinnedTiles != null) _pinnedTiles.Refresh();
            _scale = scale;
            _distance = Mathf.Min(distance, 0.78f);
            _opacity = Mathf.Clamp(opacity, 0.2f, 1f);
        }

        internal bool PinMode { get { return _pinMode; } }
        internal void SetPinnedTiles(VrPinnedActionTiles tiles) { _pinnedTiles = tiles; }
        internal QuickActionDefinition FindAction(string id) { return FindAction(_actions, id == "clothing" ? "undress" : id); }
        private static QuickActionDefinition FindAction(IList<QuickActionDefinition> actions, string id)
        { if (actions == null) return null; for (int i=0;i<actions.Count;i++) { QuickActionDefinition d=actions[i]; if (d.Id == id) return d; QuickActionDefinition child=FindAction(d.Children,id); if(child!=null)return child; } return null; }

        internal bool Visible
        {
            get { return _canvas != null && _canvas.gameObject.activeSelf; }
        }

        internal void RefreshActions(IList<QuickActionDefinition> actions)
        {
            if (actions == null)
                return;
            bool wasVisible = Visible;
            if (_canvas != null)
                Dispose();
            _actions = actions;
            if (_pinnedTiles != null) _pinnedTiles.Refresh();
            if (!wasVisible)
                return;

            Build();
            _canvas.gameObject.SetActive(true);
            VrPointerPresentation.EnsureVisible();
            SyncButtonColors();
        }

        internal void ShowHold()
        {
            if (_canvas == null)
                Build();

            _hoveredIndex = -1;
            HideSubmenu();
            Recenter();
            _canvas.gameObject.SetActive(true);
            VrPointerPresentation.EnsureVisible();
            SyncButtonColors();
        }

        internal string ReleaseAndSelect()
        {
            if (_pinMode) return null;
            if (_hoveredIndex == -2) { VrHaptics.Press(); _pinMode = true; UpdateCenter(); SyncButtonColors(); return "固定模式已开启"; }
            QuickActionDefinition selected = SelectedDefinition();
            string label = SelectedLabel(selected);
            Hide();
            if (selected != null && selected.Action != null) { VrHaptics.Press(); selected.Action(); }
            return label;
        }
        internal string FixedTapSelect()
        {
            if (!_pinMode) return null;
            if (_hoveredIndex == -2) { Hide(); return "固定模式已退出"; }
            QuickActionDefinition selected = SelectedDefinition();
            if (selected == null) return null;
            VrHaptics.Press();
            bool added = _pinnedTiles != null && _pinnedTiles.Pin(selected);
            return added ? "已固定：" + SelectedLabel(selected) : "磁贴已存在：" + SelectedLabel(selected);
        }
        private QuickActionDefinition SelectedDefinition()
        { if (_submenuParentIndex >= 0 && _sub2ChildIndex >= 0 && _sub2HoveredIndex >= 0 && _sub2ChildIndex < _actions[_submenuParentIndex].Children.Count && _sub2HoveredIndex < _actions[_submenuParentIndex].Children[_sub2ChildIndex].Children.Count) return _actions[_submenuParentIndex].Children[_sub2ChildIndex].Children[_sub2HoveredIndex]; if (_submenuParentIndex >= 0 && _submenuHoveredIndex >= 0 && _submenuHoveredIndex < _actions[_submenuParentIndex].Children.Count) return _actions[_submenuParentIndex].Children[_submenuHoveredIndex]; return _hoveredIndex >= 0 && _hoveredIndex < _actions.Count ? _actions[_hoveredIndex] : null; }
        private string SelectedLabel(QuickActionDefinition selected)
        { return selected == null ? null : (_submenuParentIndex >= 0 && _submenuHoveredIndex >= 0 ? (_submenuParentIndex >= 0 && _sub2ChildIndex >= 0 && _sub2HoveredIndex >= 0 ? _actions[_submenuParentIndex].Label + "/" + _actions[_submenuParentIndex].Children[_sub2ChildIndex].Label + "/" + selected.Label : _actions[_submenuParentIndex].Label + "/" + selected.Label) : selected.Label); }

        internal void Hide()
        {
            _hoveredIndex = -1;
            _pinMode = false;
            UpdateCenter();
            HideSubmenu();
            if (_canvas != null)
                _canvas.gameObject.SetActive(false);
        }

        internal void SetHovered(int index, bool hovered)
        {
            if (!Visible)
                return;

            if (hovered)
            {
                if (_hoveredIndex != index) VrHaptics.Hover();
                _hoveredIndex = index;
                if (index >= 0 && index < _actions.Count &&
                    _actions[index].HasChildren)
                    OpenSubmenu(index);
                else
                    HideSubmenu();
            }
            else if (_hoveredIndex == index)
                _hoveredIndex = -1;
            SyncButtonColors();
        }

        internal void SetSubmenuHovered(
            int parentIndex, int childIndex, bool hovered)
        {
            if (!Visible || _submenuParentIndex != parentIndex)
                return;
            _hoveredIndex = parentIndex;
            if (hovered)
            {
                if (_submenuHoveredIndex != childIndex) VrHaptics.Hover();
                _submenuHoveredIndex = childIndex;
                QuickActionDefinition child =
                    _actions[parentIndex].Children[childIndex];
                if (child.HasChildren) OpenSub2(parentIndex, childIndex);
                else HideSub2();
            }
            else if (_submenuHoveredIndex == childIndex)
                _submenuHoveredIndex = -1;
            SyncButtonColors();
        }

        internal void SetSub2Hovered(
            int parentIndex, int childIndex, int grandIndex, bool hovered)
        {
            if (!Visible || _submenuParentIndex != parentIndex ||
                _sub2ChildIndex != childIndex)
                return;
            _hoveredIndex = parentIndex;
            _submenuHoveredIndex = childIndex;
            if (hovered)
            {
                if (_sub2HoveredIndex != grandIndex) VrHaptics.Hover();
                _sub2HoveredIndex = grandIndex;
            }
            else if (_sub2HoveredIndex == grandIndex)
                _sub2HoveredIndex = -1;
            SyncButtonColors();
        }

        internal void Tick()
        {
            if (!Visible)
                return;

            VrPointerPresentation.EnsureVisible();
            GameObject target = VrPointerPresentation.CurrentLookTarget();
            RadialPinHoverTarget pinTarget = target == null ? null : target.GetComponentInParent<RadialPinHoverTarget>();
            if (pinTarget != null && pinTarget.BelongsTo(this)) { if (_hoveredIndex != -2) { _hoveredIndex = -2; VrHaptics.Hover(); HideSubmenu(); SyncButtonColors(); } return; }
            RadialSub2HoverTarget sub2Target = target == null
                ? null
                : target.GetComponentInParent<RadialSub2HoverTarget>();
            if (sub2Target != null && sub2Target.BelongsTo(this) &&
                sub2Target.ParentIndex == _submenuParentIndex &&
                sub2Target.ChildIndex == _sub2ChildIndex)
            {
                if (_hoveredIndex != sub2Target.ParentIndex ||
                    _submenuHoveredIndex != sub2Target.ChildIndex ||
                    _sub2HoveredIndex != sub2Target.GrandIndex)
                {
                    if (_sub2HoveredIndex != sub2Target.GrandIndex)
                        VrHaptics.Hover();
                    _hoveredIndex = sub2Target.ParentIndex;
                    _submenuHoveredIndex = sub2Target.ChildIndex;
                    _sub2HoveredIndex = sub2Target.GrandIndex;
                    SyncButtonColors();
                }
                return;
            }

            RadialSubmenuHoverTarget submenuTarget = target == null
                ? null
                : target.GetComponentInParent<RadialSubmenuHoverTarget>();
            if (submenuTarget != null && submenuTarget.BelongsTo(this) &&
                submenuTarget.ParentIndex == _submenuParentIndex)
            {
                QuickActionDefinition child =
                    _actions[submenuTarget.ParentIndex]
                        .Children[submenuTarget.ChildIndex];
                if (child.HasChildren)
                    OpenSub2(submenuTarget.ParentIndex, submenuTarget.ChildIndex);
                else if (_sub2ChildIndex != -1)
                    HideSub2();
                if (_hoveredIndex != submenuTarget.ParentIndex ||
                    _submenuHoveredIndex != submenuTarget.ChildIndex)
                {
                    if (_submenuHoveredIndex != submenuTarget.ChildIndex)
                        VrHaptics.Hover();
                    _hoveredIndex = submenuTarget.ParentIndex;
                    _submenuHoveredIndex = submenuTarget.ChildIndex;
                    _sub2HoveredIndex = -1;
                    SyncButtonColors();
                }
                return;
            }

            RadialHoverTarget hoverTarget = target == null
                ? null
                : target.GetComponentInParent<RadialHoverTarget>();
            int hoveredIndex = hoverTarget != null && hoverTarget.BelongsTo(this)
                ? hoverTarget.Index
                : -1;
            if (hoveredIndex >= 0 && hoveredIndex < _actions.Count)
            {
                if (_actions[hoveredIndex].HasChildren)
                    OpenSubmenu(hoveredIndex);
                else
                    HideSubmenu();
            }

            if (hoveredIndex == _hoveredIndex && _submenuHoveredIndex < 0)
                return;

            if (hoveredIndex != _hoveredIndex && hoveredIndex >= 0)
                VrHaptics.Hover();
            _hoveredIndex = hoveredIndex;
            _submenuHoveredIndex = -1;
            SyncButtonColors();
        }

        internal void Dispose()
        {
            if (_canvas == null)
                return;

            if (SuperController.singleton != null)
                SuperController.singleton.RemoveCanvas(_canvas);
            UnityEngine.Object.Destroy(_canvas.gameObject);
            _canvas = null;
            _buttonImages.Clear();
            _buttons.Clear();
            _submenuRoots.Clear();
            _submenuImages.Clear();
            _submenuButtons.Clear();
            _submenuParents.Clear();
            _submenuChildren.Clear();
            _sub2Roots.Clear();
            _sub2Images.Clear();
            _sub2Buttons.Clear();
            _sub2Parents.Clear();
            _sub2Children.Clear();
            _sub2Grands.Clear();
        }

        private void Build()
        {
            GameObject canvasObject = new GameObject("Quest3 Radial Quick Menu");
            _canvas = canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.pixelPerfect = false;
            _canvas.overrideSorting = false;
            _canvas.sortingOrder = 0;
            canvasObject.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1f;
            canvasObject.AddComponent<GraphicRaycaster>();
            CanvasGroup menuGroup = canvasObject.AddComponent<CanvasGroup>();
            menuGroup.alpha = _opacity;

            RectTransform root = canvasObject.GetComponent<RectTransform>();
            root.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, CanvasSize);
            root.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, CanvasSize);
            SuperController.singleton.AddCanvas(_canvas);
            _font = (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");

            GameObject background = CreateUiObject("Radial background", root);
            RectTransform backgroundRect = background.GetComponent<RectTransform>();
            SetCentered(backgroundRect, Vector2.zero, 860f, 860f);
            Image backgroundImage = background.AddComponent<Image>();
            backgroundImage.sprite = DiscSprite;
            backgroundImage.color = new Color(0.025f, 0.035f, 0.050f, 0.90f);
            backgroundImage.raycastTarget = false;

            GameObject center = CreateUiObject("Radial title", root);
            RectTransform centerRect = center.GetComponent<RectTransform>();
            SetCentered(centerRect, Vector2.zero, 230f, 230f);
            Image centerImage = center.AddComponent<Image>();
            _centerImage = centerImage;
            Button centerButton = center.AddComponent<Button>();
            centerButton.targetGraphic = centerImage;
            centerButton.transition = Selectable.Transition.None;
            centerImage.sprite = DiscSprite;
            centerImage.color = new Color(0.07f, 0.18f, 0.25f, 0.98f);
            centerImage.raycastTarget = true;
            RadialPinHoverTarget pin = center.AddComponent<RadialPinHoverTarget>(); pin.Configure(this);
            _centerText = AddText(center.transform, "固定\n选中后松开扳机", 31);

            for (int i = 0; i < _actions.Count; i++)
            {
                CreateWedgeButton(root, i, _actions.Count, _actions[i]);
                CreateSubmenuWedges(root, i, _actions[i]);
            }

            SetLayerRecursively(canvasObject, ResolveUiLayer());
            Recenter();
            canvasObject.SetActive(false);
        }

        // Submenu: an outer-ring wedge cluster centred on the parent's
        // direction — children stay adjacent to their parent (short ray
        // travel, no risk of the menu closing mid-flight over a childless
        // neighbour) while each still gets a comfortably wide wedge.
        private void CreateSubmenuWedges(
            RectTransform parent, int parentIndex,
            QuickActionDefinition definition)
        {
            if (!definition.HasChildren)
                return;

            float parentSpan = 360f / _actions.Count;
            float parentEuler = -parentSpan * parentIndex;
            float childSpan = Mathf.Clamp(parentSpan, 26f, 60f);
            for (int childIndex = 0;
                 childIndex < definition.Children.Count; childIndex++)
            {
                QuickActionDefinition child = definition.Children[childIndex];
                float euler = parentEuler +
                    (childIndex - (definition.Children.Count - 1) * 0.5f) * childSpan;
                GameObject go = CreateUiObject(
                    "Radial " + definition.Label + " / " + child.Label, parent);
                RectTransform rect = go.GetComponent<RectTransform>();
                Vector2 pivot, size;
                Sprite wedge = WedgeSprite(
                    childSpan, SubInnerR, SubOuterR, out pivot, out size);
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = pivot;
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = size;
                rect.localEulerAngles = new Vector3(0f, 0f, euler);
                Image image = go.AddComponent<Image>();
                image.sprite = wedge;
                image.alphaHitTestMinimumThreshold = 0.5f;
                image.color = NormalColor(child);
                Button button = go.AddComponent<Button>();
                button.targetGraphic = image;
                button.transition = Selectable.Transition.ColorTint;
                ConfigureButtonColors(button, child);
                RadialSubmenuHoverTarget hover =
                    go.AddComponent<RadialSubmenuHoverTarget>();
                hover.Configure(this, parentIndex, childIndex);
                AddWedgeText(go.transform, child.Label, 27,
                    (pivot.y - 0.5f) * size.y + (SubInnerR + SubOuterR) * 0.5f,
                    -euler);
                _submenuRoots.Add(go);
                _submenuImages.Add(image);
                _submenuButtons.Add(button);
                _submenuParents.Add(parentIndex);
                _submenuChildren.Add(childIndex);
                go.SetActive(false);
                if (child.HasChildren)
                    CreateSub2Wedges(
                        parent, parentIndex, childIndex, child, euler);
            }
        }

        // Third ring: same wedge cluster trick one ring further out, centred
        // on the ring-2 child that owns it. Only built for children that
        // themselves have children.
        private void CreateSub2Wedges(
            RectTransform parent, int parentIndex, int childIndex,
            QuickActionDefinition child, float childEuler)
        {
            int count = child.Children.Count;
            float grandSpan = 42f;
            for (int grandIndex = 0; grandIndex < count; grandIndex++)
            {
                QuickActionDefinition grand = child.Children[grandIndex];
                float euler = childEuler +
                    (grandIndex - (count - 1) * 0.5f) * grandSpan;
                GameObject go = CreateUiObject(
                    "Radial " + child.Label + " / " + grand.Label, parent);
                RectTransform rect = go.GetComponent<RectTransform>();
                Vector2 pivot, size;
                Sprite wedge = WedgeSprite(
                    grandSpan, Sub2InnerR, Sub2OuterR, out pivot, out size);
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = pivot;
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = size;
                rect.localEulerAngles = new Vector3(0f, 0f, euler);
                Image image = go.AddComponent<Image>();
                image.sprite = wedge;
                image.alphaHitTestMinimumThreshold = 0.5f;
                image.color = NormalColor(grand);
                Button button = go.AddComponent<Button>();
                button.targetGraphic = image;
                button.transition = Selectable.Transition.ColorTint;
                ConfigureButtonColors(button, grand);
                RadialSub2HoverTarget hover =
                    go.AddComponent<RadialSub2HoverTarget>();
                hover.Configure(this, parentIndex, childIndex, grandIndex);
                AddWedgeText(go.transform, grand.Label, 25,
                    (pivot.y - 0.5f) * size.y + (Sub2InnerR + Sub2OuterR) * 0.5f,
                    -euler);
                _sub2Roots.Add(go);
                _sub2Images.Add(image);
                _sub2Buttons.Add(button);
                _sub2Parents.Add(parentIndex);
                _sub2Children.Add(childIndex);
                _sub2Grands.Add(grandIndex);
                go.SetActive(false);
            }
        }

        private void OpenSub2(int parentIndex, int childIndex)
        {
            if (_sub2ChildIndex == childIndex &&
                _submenuParentIndex == parentIndex && Sub2Open)
                return;
            _sub2ChildIndex = childIndex;
            _sub2HoveredIndex = -1;
            for (int i = 0; i < _sub2Roots.Count; i++)
                _sub2Roots[i].SetActive(
                    _sub2Parents[i] == parentIndex &&
                    _sub2Children[i] == childIndex);
        }

        private bool Sub2Open
        {
            get
            {
                for (int i = 0; i < _sub2Roots.Count; i++)
                    if (_sub2Roots[i].activeSelf) return true;
                return false;
            }
        }

        private void HideSub2()
        {
            for (int i = 0; i < _sub2Roots.Count; i++)
                _sub2Roots[i].SetActive(false);
            _sub2ChildIndex = -1;
            _sub2HoveredIndex = -1;
        }

        private void OpenSubmenu(int parentIndex)
        {
            if (_submenuParentIndex == parentIndex)
                return;
            _submenuParentIndex = parentIndex;
            _submenuHoveredIndex = -1;
            HideSub2();
            for (int i = 0; i < _submenuRoots.Count; i++)
                _submenuRoots[i].SetActive(_submenuParents[i] == parentIndex);
        }

        private void HideSubmenu()
        {
            for (int i = 0; i < _submenuRoots.Count; i++)
                _submenuRoots[i].SetActive(false);
            _submenuParentIndex = -1;
            _submenuHoveredIndex = -1;
            HideSub2();
        }

        private void CreateWedgeButton(
            RectTransform parent, int index, int count,
            QuickActionDefinition definition)
        {
            float span = 360f / count;
            GameObject go = CreateUiObject("Radial " + definition.Label, parent);
            RectTransform rect = go.GetComponent<RectTransform>();
            Vector2 pivot, size;
            Sprite wedge = WedgeSprite(
                span, WedgeInnerR, WedgeOuterR, out pivot, out size);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = pivot;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
            rect.localEulerAngles = new Vector3(0f, 0f, -span * index);
            Image image = go.AddComponent<Image>();
            image.sprite = wedge;
            image.alphaHitTestMinimumThreshold = 0.5f;
            image.color = NormalColor(definition);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            ConfigureButtonColors(button, definition);
            int buttonIndex = _buttonImages.Count;
            RadialHoverTarget hoverTarget = go.AddComponent<RadialHoverTarget>();
            hoverTarget.Configure(this, buttonIndex);
            AddWedgeText(go.transform, definition.Label, 29,
                (pivot.y - 0.5f) * size.y + (WedgeInnerR + WedgeOuterR) * 0.5f,
                span * index);
            _buttonImages.Add(image);
            _buttons.Add(button);
        }

        // Label sits on the wedge centroid (centerY is measured from the
        // button rect's centre to the ring-centre plus mid-radius),
        // counter-rotated so it stays horizontal at any ring position.
        private Text AddWedgeText(
            Transform parent, string value, int fontSize, float centerY,
            float unrotDeg)
        {
            GameObject textObject = CreateUiObject(
                "Text", parent as RectTransform);
            RectTransform rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, centerY);
            rect.sizeDelta = new Vector2(220f, 80f);
            rect.localEulerAngles = new Vector3(0f, 0f, unrotDeg);
            Text text = textObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 14;
            text.resizeTextMaxSize = fontSize;
            text.raycastTarget = false;
            text.text = value;
            return text;
        }

        private void SyncButtonColors()
        {
            UpdateCenter();
            int count = Math.Min(_actions.Count, _buttonImages.Count);
            for (int i = 0; i < count; i++)
            {
                ConfigureButtonColors(_buttons[i], _actions[i]);
                _buttonImages[i].color = i == _hoveredIndex
                    ? HoverColor
                    : NormalColor(_actions[i]);
            }
            for (int i = 0; i < _submenuImages.Count; i++)
            {
                QuickActionDefinition definition =
                    _actions[_submenuParents[i]].Children[_submenuChildren[i]];
                ConfigureButtonColors(_submenuButtons[i], definition);
                _submenuImages[i].color =
                    _submenuParents[i] == _submenuParentIndex &&
                    _submenuChildren[i] == _submenuHoveredIndex
                    ? HoverColor
                    : NormalColor(definition);
            }
            for (int i = 0; i < _sub2Images.Count; i++)
            {
                QuickActionDefinition definition =
                    _actions[_sub2Parents[i]].Children[_sub2Children[i]]
                        .Children[_sub2Grands[i]];
                ConfigureButtonColors(_sub2Buttons[i], definition);
                _sub2Images[i].color =
                    _sub2Parents[i] == _submenuParentIndex &&
                    _sub2Children[i] == _sub2ChildIndex &&
                    _sub2Grands[i] == _sub2HoveredIndex
                    ? HoverColor
                    : NormalColor(definition);
            }
        }

        private static void ConfigureButtonColors(
            Button button, QuickActionDefinition definition)
        {
            ColorBlock colors = button.colors;
            colors.normalColor = NormalColor(definition);
            colors.highlightedColor = HoverColor;
            colors.pressedColor = HoverColor;
            colors.disabledColor = new Color(0.10f, 0.10f, 0.10f, 0.75f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.03f;
            button.colors = colors;
        }

        private static Color NormalColor(QuickActionDefinition definition)
        {
            return definition.ActiveState != null && definition.ActiveState()
                ? new Color(0.08f, 0.68f, 0.36f, 1f)
                : new Color(0.11f, 0.38f, 0.48f, 1f);
        }

        private void UpdateCenter() { if (_centerImage != null) _centerImage.color = _hoveredIndex == -2 ? HoverColor : (_pinMode ? new Color(0.08f, 0.68f, 0.36f, 1f) : new Color(0.11f, 0.38f, 0.48f, 1f)); if (_centerText != null) _centerText.text = _pinMode ? "退出固定\n点选按钮可复制磁贴" : "固定\n选中后松开扳机"; }

        private void Recenter()
        {
            if (_canvas == null || SuperController.singleton == null ||
                SuperController.singleton.centerCameraTarget == null)
                return;

            Transform anchor = SuperController.singleton.centerCameraTarget.transform;
            _canvas.transform.SetParent(anchor, false);
            _canvas.transform.localPosition = new Vector3(0f, 0f, _distance);
            _canvas.transform.localRotation = Quaternion.identity;
            _canvas.transform.localScale = Vector3.one * _scale;
        }

        private Text AddText(Transform parent, string value, int fontSize)
        {
            GameObject textObject = CreateUiObject("Text", parent as RectTransform);
            RectTransform rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(8f, 4f);
            rect.offsetMax = new Vector2(-8f, -4f);
            Text text = textObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 16;
            text.resizeTextMaxSize = fontSize;
            text.raycastTarget = false;
            text.text = value;
            return text;
        }

        private static GameObject CreateUiObject(string name, RectTransform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            if (parent != null)
                go.transform.SetParent(parent, false);
            return go;
        }

        private static void SetCentered(
            RectTransform rect, Vector2 position, float width, float height)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(width, height);
        }

        private static int ResolveUiLayer()
        {
            int layer = LayerMask.NameToLayer("UI");
            return layer < 0 ? 5 : layer;
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            root.layer = layer;
            Transform transform = root.transform;
            for (int i = 0; i < transform.childCount; i++)
                SetLayerRecursively(transform.GetChild(i).gameObject, layer);
        }
    }

    internal static class VrPointerPresentation
    {
        // Compiled accessors: these members are read every frame, so plain
        // FieldInfo.GetValue/MethodInfo.Invoke was pure per-frame overhead.
        private static readonly Func<LookInputModule, GameObject> CurrentLookRightRef =
            LookField<GameObject>("currentLookRight");
        private static readonly Func<LookInputModule, GameObject> CurrentLookLeftRef =
            LookField<GameObject>("currentLook");
        private static readonly Func<LookInputModule, Camera> ReferenceCameraRef =
            LookField<Camera>("referenceCamera");
        private static readonly Func<LookInputModule, RectTransform> CursorRightRef =
            LookField<RectTransform>("cursorRight");
        private static readonly Func<LookInputModule, RectTransform> CursorLeftRef =
            LookField<RectTransform>("cursor");
        private static readonly Func<SuperController, Transform> MotionLeft =
            PropertyGetter<Transform>("motionControllerLeft");
        private static readonly Func<SuperController, Transform> MotionRight =
            PropertyGetter<Transform>("motionControllerRight");

        private static Func<LookInputModule, T> LookField<T>(string name)
        {
            FieldInfo field = AccessTools.Field(typeof(LookInputModule), name);
            if (field == null)
                return null;
            try
            {
                ParameterExpression instance =
                    Expression.Parameter(typeof(LookInputModule), "module");
                return Expression.Lambda<Func<LookInputModule, T>>(
                    Expression.Field(instance, field), instance).Compile();
            }
            catch { return null; }
        }
        private static Func<SuperController, T> PropertyGetter<T>(string name)
        {
            MethodInfo getter = AccessTools.PropertyGetter(typeof(SuperController), name);
            if (getter == null)
                return null;
            try
            {
                return (Func<SuperController, T>)Delegate.CreateDelegate(
                    typeof(Func<SuperController, T>), getter);
            }
            catch { return null; }
        }
        internal static Transform MotionController(SuperController controller, bool right)
        {
            Func<SuperController, Transform> getter = right ? MotionRight : MotionLeft;
            return controller == null || getter == null ? null : getter(controller);
        }

        internal static void EnsureVisible()
        {
            LookInputModule module = LookInputModule.singleton;
            if (module == null)
                return;

            module.useCursor = true;
        }

        internal static GameObject CurrentLookTarget()
        {
            return CurrentLookTarget(true);
        }

        internal static GameObject CurrentLookTarget(bool right)
        {
            LookInputModule module = LookInputModule.singleton;
            if (module == null)
                return null;

            Func<LookInputModule, GameObject> field = right
                ? CurrentLookRightRef
                : CurrentLookLeftRef;
            return field == null ? null : field(module);
        }

        internal static RectTransform CurrentCursor(bool right)
        {
            LookInputModule module = LookInputModule.singleton;
            if (module == null)
                return null;
            Func<LookInputModule, RectTransform> field = right
                ? CursorRightRef
                : CursorLeftRef;
            return field == null ? null : field(module);
        }

        internal static bool TryGetPointerRay(out Ray ray)
        {
            ray = new Ray();
            LookInputModule module = LookInputModule.singleton;
            Camera camera = module == null || ReferenceCameraRef == null
                ? null
                : ReferenceCameraRef(module);
            // VaM keeps its controller UI ray camera disabled for rendering and
            // still uses it as the coordinate source for EventSystem raycasts.
            if (camera == null)
                return false;

            ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            return ray.direction.sqrMagnitude > 0.5f;
        }

        internal static bool TryGetPointerDirection(
            Transform hand, bool right, out Vector3 direction)
        {
            direction = hand == null ? Vector3.forward : hand.forward;
            if (hand == null)
                return false;

            LookInputModule module = LookInputModule.singleton;
            GameObject target = CurrentLookTarget(right);
            Func<LookInputModule, RectTransform> cursorField = right
                ? CursorRightRef
                : CursorLeftRef;
            RectTransform cursor = module == null || cursorField == null
                ? null
                : cursorField(module);
            if (target == null || cursor == null)
                return false;

            Vector3 towardCursor = cursor.position - hand.position;
            if (towardCursor.sqrMagnitude <= 0.0001f)
                return false;
            direction = towardCursor.normalized;
            return true;
        }
    }

    internal sealed class RadialPinHoverTarget : MonoBehaviour, IPointerEnterHandler, IPointerDownHandler { private VrRadialMenu _owner; internal void Configure(VrRadialMenu owner) { _owner = owner; } internal bool BelongsTo(VrRadialMenu owner) { return ReferenceEquals(_owner, owner); } public void OnPointerEnter(PointerEventData eventData) { } public void OnPointerDown(PointerEventData eventData) { } }

    internal sealed class RadialHoverTarget : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler
    {
        private VrRadialMenu _owner;
        private int _index;

        internal int Index
        {
            get { return _index; }
        }

        internal bool BelongsTo(VrRadialMenu owner)
        {
            return ReferenceEquals(_owner, owner);
        }

        internal void Configure(VrRadialMenu owner, int index)
        {
            _owner = owner;
            _index = index;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_owner != null)
                _owner.SetHovered(_index, true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (_owner != null)
                _owner.SetHovered(_index, false);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_owner != null)
                _owner.SetHovered(_index, true);
        }
    }

    internal sealed class RadialSubmenuHoverTarget : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler
    {
        private VrRadialMenu _owner;
        private int _parentIndex;
        private int _childIndex;

        internal int ParentIndex
        {
            get { return _parentIndex; }
        }

        internal int ChildIndex
        {
            get { return _childIndex; }
        }

        internal bool BelongsTo(VrRadialMenu owner)
        {
            return ReferenceEquals(_owner, owner);
        }

        internal void Configure(
            VrRadialMenu owner, int parentIndex, int childIndex)
        {
            _owner = owner;
            _parentIndex = parentIndex;
            _childIndex = childIndex;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_owner != null)
                _owner.SetSubmenuHovered(_parentIndex, _childIndex, true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (_owner != null)
                _owner.SetSubmenuHovered(_parentIndex, _childIndex, false);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_owner != null)
                _owner.SetSubmenuHovered(_parentIndex, _childIndex, true);
        }
    }

    internal sealed class RadialSub2HoverTarget : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler
    {
        private VrRadialMenu _owner;
        private int _parentIndex;
        private int _childIndex;
        private int _grandIndex;

        internal int ParentIndex
        {
            get { return _parentIndex; }
        }

        internal int ChildIndex
        {
            get { return _childIndex; }
        }

        internal int GrandIndex
        {
            get { return _grandIndex; }
        }

        internal bool BelongsTo(VrRadialMenu owner)
        {
            return ReferenceEquals(_owner, owner);
        }

        internal void Configure(
            VrRadialMenu owner, int parentIndex, int childIndex,
            int grandIndex)
        {
            _owner = owner;
            _parentIndex = parentIndex;
            _childIndex = childIndex;
            _grandIndex = grandIndex;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_owner != null)
                _owner.SetSub2Hovered(
                    _parentIndex, _childIndex, _grandIndex, true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (_owner != null)
                _owner.SetSub2Hovered(
                    _parentIndex, _childIndex, _grandIndex, false);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_owner != null)
                _owner.SetSub2Hovered(
                    _parentIndex, _childIndex, _grandIndex, true);
        }
    }
}













