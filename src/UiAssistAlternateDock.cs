using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    internal static partial class UiAssistHudLink
    {
        private static bool _alternateExpanded = true;
        private static UIDynamicButton _alternateArrow;
        private static GameObject _alternateList;
        private static RectTransform _alternateDock;
        private static Canvas _alternateCanvas;
        private static Transform _borrowedRoot, _originalParent;
        private static Vector3 _originalPosition, _originalScale;
        private static Quaternion _originalRotation;
        private static int _originalSibling;
        private static object _borrowedSupport, _originalCanvas;
        private static Atom _alternateTarget;
        private static readonly Vector3[] _dockCorners = new Vector3[4];
        private static float _alternateRetry;

        private static void UpdateAlternateDock(Snapshot state, GameObject list)
        {
            if (_alternateList != list || _alternateArrow == null)
            {
                ClearAlternateDock();
                _alternateList = list;
                object canvas = Read(state.Editor.GetType(), state.Editor, "_uiButtonCanvas");
                _alternateArrow = CreatePresetButton(canvas, list, _alternateExpanded ? "◀" : "▶", -24f, delegate { });
                _alternateArrow.button.onClick.RemoveAllListeners();
                _alternateArrow.button.onClick.AddListener(delegate {
                    _alternateExpanded = !_alternateExpanded;
                    _alternateArrow.label = _alternateExpanded ? "◀" : "▶";
                    if (!_alternateExpanded) ReturnAlternatePanel();
                    _alternateRetry = 0f;
                    _nextPresetCheck = 0f;
                });
                RectTransform arrow = (RectTransform)_alternateArrow.transform;
                arrow.sizeDelta = new Vector2(32f, 36f);
                ((RectTransform)list.transform).GetWorldCorners(_dockCorners);
                arrow.position = _dockCorners[1] + list.transform.TransformVector(new Vector3(-22f, 20f, 0f));
            }
            // Hidden means no AlternateUI target lookup or per-character rebuild.
            if (!_alternateExpanded) return;
            if (state.Target == null) { ReturnAlternatePanel(); return; }
            if (_alternateTarget == state.Target && _borrowedRoot != null) return;
            if (Time.unscaledTime < _alternateRetry) return;
            ReturnAlternatePanel();
            _alternateRetry = Time.unscaledTime + 2f;
            try
            {
                object info = FindAlternateClothingInfo(state.Target);
                if (info == null) return;
                object root = Read(info.GetType(), info, "root_");
                if (root == null) return;
                object support = Read(root.GetType(), root, "RootSupport");
                Transform panel = Read(support.GetType(), support, "RootParent") as Transform;
                RectTransform parent = panel == null ? null : panel.parent as RectTransform;
                if (parent == null || parent.rect.width <= 0f || parent.rect.height <= 0f) return;

                GameObject go = new GameObject("Quest3 AlternateUI Clothing Dock", typeof(RectTransform));
                go.SetActive(false);
                _alternateDock = (RectTransform)go.transform;
                _alternateDock.sizeDelta = parent.rect.size;
                go.layer = panel.gameObject.layer;
                _alternateCanvas = go.AddComponent<Canvas>();
                _alternateCanvas.renderMode = RenderMode.WorldSpace;
                _alternateCanvas.worldCamera = list.GetComponentInParent<Canvas>().worldCamera;
                go.AddComponent<GraphicRaycaster>();
                _borrowedRoot = panel;
                _originalParent = parent;
                _originalPosition = panel.localPosition; _originalRotation = panel.localRotation;
                _originalScale = panel.localScale; _originalSibling = panel.GetSiblingIndex();
                _borrowedSupport = support;
                _originalCanvas = Read(support.GetType(), support, "canvas_");
                panel.SetParent(_alternateDock, false);
                panel.gameObject.SetActive(true);
                // VUI caches the canvas for pointer coordinates. Redirect that cache
                // with the visual panel, otherwise the laser and click positions differ.
                WriteInheritedField(support, "canvas_", _alternateCanvas);
                _alternateTarget = state.Target;
                SuperController.singleton.AddCanvas(_alternateCanvas);
                TickAlternateDock();
                Log("Alternate UI服装面板已停靠并同步角色：" + state.Target.uid);
            }
            catch (Exception e) { ReturnAlternatePanel(); _alternateRetry = Time.unscaledTime + 5f; Error(e); }
        }

        private static object FindAlternateClothingInfo(Atom target)
        {
            Type type = AssemblyCatalog.FindType("AUI.AlternateUI");
            if (type == null) return null;
            object instance = Read(type, null, "Instance");
            if ((instance as UnityEngine.Object) == null) return null;
            IEnumerable features = Read(type, instance, "features_") as IEnumerable;
            if (features == null) return null;
            foreach (object feature in features)
            {
                if (feature.GetType().FullName != "AUI.ClothingUI.ClothingUI") continue;
                object modifier = Read(feature.GetType(), feature, "uiMod_");
                foreach (object info in (IEnumerable)Read(modifier.GetType(), modifier, "Atoms"))
                    if (Read(info.GetType(), info, "Atom") as Atom == target) return info;
            }
            return null;
        }

        private static void WriteInheritedField(object instance, string name, object value)
        {
            for (Type t = instance.GetType(); t != null; t = t.BaseType)
            {
                System.Reflection.FieldInfo field = t.GetField(name, Flags | System.Reflection.BindingFlags.DeclaredOnly);
                if (field != null) { field.SetValue(instance, value); return; }
            }
            throw new MissingFieldException(instance.GetType().FullName, name);
        }

        private static void TickAlternateDock()
        {
            if (_alternateDock == null) return;
            bool visible = _alternateExpanded && _alternateList != null && _alternateList.activeInHierarchy &&
                SuperController.singleton != null && SuperController.singleton.MainHUDVisible && !_presetBrowsing;
            _alternateDock.gameObject.SetActive(visible);
            if (!visible) return;
            RectTransform list = (RectTransform)_alternateList.transform;
            list.GetWorldCorners(_dockCorners);
            float scale = list.rect.height * 1.4f / _alternateDock.rect.height;
            _alternateDock.rotation = list.rotation;
            _alternateDock.localScale = list.lossyScale * scale;
            Vector3 center = (_dockCorners[0] + _dockCorners[1]) * 0.5f;
            _alternateDock.position = center - list.right *
                (24f * list.lossyScale.x + _alternateDock.rect.width * _alternateDock.lossyScale.x * 0.5f);
            Camera viewer = SuperController.singleton.lookCamera;
            if (viewer != null)
            {
                // Unity Canvas fronts face local -Z: its +Z points away from the viewer.
                // Use head position rather than gaze rotation so merely looking around
                // does not spin the panel. The registered hit plane rotates with it.
                Vector3 away = _alternateDock.position - viewer.transform.position;
                if (away.sqrMagnitude > 0.0001f &&
                    Vector3.Cross(away, list.up).sqrMagnitude > 0.0001f)
                    _alternateDock.rotation = Quaternion.LookRotation(away, list.up);
            }
        }

        private static void ReturnAlternatePanel()
        {
            // Return the borrowed root before destroying our canvas, even when ACE
            // has rebuilt its own canvas. Never destroy AlternateUI-owned widgets.
            if (_borrowedRoot != null && _originalParent != null)
            {
                _borrowedRoot.SetParent(_originalParent, false);
                _borrowedRoot.localPosition = _originalPosition;
                _borrowedRoot.localRotation = _originalRotation;
                _borrowedRoot.localScale = _originalScale;
                _borrowedRoot.SetSiblingIndex(_originalSibling);
            }
            else if (_borrowedRoot != null)
            { _borrowedRoot.SetParent(null, false); _borrowedRoot.gameObject.SetActive(false); }
            if (_borrowedSupport != null) WriteInheritedField(_borrowedSupport, "canvas_", _originalCanvas);
            if (_alternateCanvas != null && SuperController.singleton != null) SuperController.singleton.RemoveCanvas(_alternateCanvas);
            if (_alternateDock != null) UnityEngine.Object.Destroy(_alternateDock.gameObject);
            _borrowedRoot = _originalParent = null; _borrowedSupport = _originalCanvas = null;
            _alternateDock = null; _alternateCanvas = null; _alternateTarget = null;
        }
        private static void ClearAlternateDock()
        {
            ReturnAlternatePanel();
            if (_alternateArrow != null) UnityEngine.Object.Destroy(_alternateArrow.gameObject);
            _alternateArrow = null; _alternateList = null;
        }
    }
}
