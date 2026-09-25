using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    internal static partial class UiAssistHudLink
    {
        private static readonly EditorPanelOrbitState PanelGesture = new EditorPanelOrbitState();
        private static object _panelCanvasOwner;
        private static Transform _panelRoot;
        private static CanvasGroup _panelGroup;
        private static Canvas _panelControlCanvas;
        private static bool _panelBorrowedGroup, _panelHidden, _panelOrbit;
        private static float _panelAlpha;
        private static bool _panelInteractable, _panelRaycasts;
        private static Vector3 _panelNativePosition, _panelCenter, _panelLocalCenter, _dockBasisPosition;
        private static Quaternion _panelNativeRotation, _dockBasisRotation;
        private static Harmony _panelHarmony;
        private static int _panelAdvanceFrame = -1, _panelInputFrame = -1;
        private static readonly Vector3[] PanelCorners = new Vector3[4];

        internal static bool PanelOrbitCapturing { get { return PanelGesture.Capturing && PanelAvailable(); } }

        private static bool PanelAvailable()
        {
            var sc = SuperController.singleton;
            return _panelRoot != null && _presetList != null && _presetList.activeInHierarchy &&
                sc != null && sc.MainHUDVisible && !sc.isLoading && !_presetBrowsing && sc.lookCamera != null;
        }

        private static void BindPanelPresentation(object owner, GameObject list)
        {
            var canvas = Read(owner.GetType(), owner, "_canvas") as Canvas;
            var sc = SuperController.singleton;
            if (canvas == null || !list.transform.IsChildOf(canvas.transform) || sc == null ||
                (sc.mainHUD != null && (canvas.transform == sc.mainHUD || sc.mainHUD.IsChildOf(canvas.transform))))
                throw new InvalidOperationException("ACE canvas identity mismatch; main HUD retained");
            _panelCanvasOwner = owner;
            _panelRoot = canvas.transform;
            _panelNativePosition = _panelRoot.position;
            _panelNativeRotation = _panelRoot.rotation;
            _panelGroup = _panelRoot.GetComponent<CanvasGroup>();
            _panelBorrowedGroup = _panelGroup != null;
            if (_panelGroup == null) _panelGroup = _panelRoot.gameObject.AddComponent<CanvasGroup>();
            _panelAlpha = _panelGroup.alpha;
            _panelInteractable = _panelGroup.interactable;
            _panelRaycasts = _panelGroup.blocksRaycasts;

            // The toggle must not inherit the editor's hidden CanvasGroup.
            // It remains a world-space canvas registered with VaM's raycaster.
            Transform button = _editorVisibilityButton.transform;
            button.SetParent(null, true);
            _panelControlCanvas = button.gameObject.AddComponent<Canvas>();
            _panelControlCanvas.renderMode = RenderMode.WorldSpace;
            _panelControlCanvas.worldCamera = canvas.worldCamera;
            button.gameObject.AddComponent<GraphicRaycaster>();
            sc.AddCanvas(_panelControlCanvas);
            MethodInfo update = owner.GetType().GetMethod("Update", Flags, null, Type.EmptyTypes, null);
            if (update == null) throw new MissingMethodException("UIAssist canvas Update");
            _panelHarmony = new Harmony("Quest3TriggerUI.ace-panel-presentation");
            _panelHarmony.UnpatchAll(_panelHarmony.Id);
            _panelHarmony.Patch(update, postfix: new HarmonyMethod(typeof(UiAssistHudLink)
                .GetMethod("AfterNativePanelUpdate", Flags)));
            NotePanelDockLayout();
            ApplyPanelPresentation();
            Log("服装编辑器隐藏/显示及右Grip横向环绕已接入：" + owner.GetType().FullName);
        }

        private static void AfterNativePanelUpdate(object __instance)
        {
            if (!ReferenceEquals(__instance, _panelCanvasOwner) || _panelRoot == null) return;
            // Preserve native pose for teardown; never modify mainHUD or rig.
            _panelNativePosition = _panelRoot.position;
            _panelNativeRotation = _panelRoot.rotation;
            ApplyPanelPresentation();
        }

        private static void ToggleEditorVisibility()
        {
            if (!PanelAvailable()) return;
            if (!_panelHidden && _panelGroup != null)
            {
                _panelAlpha = _panelGroup.alpha;
                _panelInteractable = _panelGroup.interactable;
                _panelRaycasts = _panelGroup.blocksRaycasts;
            }
            _panelHidden = !_panelHidden;
            if (!_panelHidden) RestoreEditorGroup();
            _editorVisibilityButton.label = _panelHidden ? "显示" : "隐藏";
            ApplyPanelPresentation();
        }

        internal static void UpdatePanelOrbitInput(bool grip, Vector2 stick, bool blocked)
        {
            PanelGesture.Update(PanelAvailable(), grip, blocked, stick.x);
            _panelInputFrame = Time.frameCount;
        }

        internal static void ApplyPanelPresentation()
        {
            if (_panelRoot == null || _editorVisibilityButton == null) return;
            var sc = SuperController.singleton;
            bool visible = sc != null && sc.MainHUDVisible && !sc.isLoading &&
                _presetList != null && _presetList.activeInHierarchy && !_presetBrowsing;
            if (_editorVisibilityButton.gameObject.activeSelf != visible)
                _editorVisibilityButton.gameObject.SetActive(visible);
            if (!visible) { PanelGesture.Reset(); return; }
            if (_panelHidden && _panelGroup != null)
            {
                if (_panelGroup.alpha != 0f) _panelGroup.alpha = 0f;
                if (_panelGroup.interactable) _panelGroup.interactable = false;
                if (_panelGroup.blocksRaycasts) _panelGroup.blocksRaycasts = false;
            }
            var list = (RectTransform)_presetList.transform;
            if (sc.lookCamera != null)
            {
                float degrees = 0f;
                if (_panelInputFrame == Time.frameCount && _panelAdvanceFrame != Time.frameCount)
                {
                    _panelAdvanceFrame = Time.frameCount;
                    degrees = PanelGesture.Degrees(Time.unscaledDeltaTime);
                }
                if (Mathf.Abs(degrees) > 0.00001f)
                {
                    if (!_panelOrbit)
                    {
                        _panelCenter = list.TransformPoint(list.rect.center);
                        _panelLocalCenter = _panelRoot.InverseTransformPoint(_panelCenter);
                        _panelOrbit = true;
                    }
                    Vector3 pivot = sc.lookCamera.transform.position;
                    pivot.y = _panelCenter.y;
                    _panelCenter = pivot + Quaternion.AngleAxis(degrees, Vector3.up) * (_panelCenter - pivot);
                }
                if (_panelOrbit)
                {
                    Vector3 away = _panelCenter - sc.lookCamera.transform.position;
                    if (away.sqrMagnitude > 0.0001f && Vector3.Cross(away, Vector3.up).sqrMagnitude > 0.0001f)
                        _panelRoot.rotation = Quaternion.LookRotation(away, Vector3.up);
                    _panelRoot.position = _panelCenter - _panelRoot.TransformVector(_panelLocalCenter);
                    // Native canvas placement can run after our normal dock
                    // layout. Carry those independent canvases with the same
                    // rigid transform, without re-running thumbnail/UI logic.
                    Quaternion delta = _panelRoot.rotation * Quaternion.Inverse(_dockBasisRotation);
                    CarryPanelDock(_pdDock, delta, true);
                    CarryPanelDock(_favDock, delta, true);
                    CarryPanelDock(_banDock, delta, false);
                    CarryPanelDock(_lockDock, delta, false);
                }
            }
            NotePanelDockLayout();
            list.GetWorldCorners(PanelCorners);
            Transform toggle = _editorVisibilityButton.transform;
            toggle.position = PanelCorners[2] + list.TransformVector(new Vector3(36f, 64f, 0f));
            toggle.rotation = list.rotation;
            toggle.localScale = list.lossyScale;
        }

        private static void CarryPanelDock(RectTransform dock, Quaternion delta, bool faceViewer)
        {
            if (dock == null) return;
            dock.position = _panelRoot.position + delta * (dock.position - _dockBasisPosition);
            dock.rotation = delta * dock.rotation;
            var sc = SuperController.singleton;
            if (faceViewer && sc != null && sc.lookCamera != null)
            {
                Vector3 away = dock.position - sc.lookCamera.transform.position;
                if (away.sqrMagnitude > 0.0001f && Vector3.Cross(away, _panelRoot.up).sqrMagnitude > 0.0001f)
                    dock.rotation = Quaternion.LookRotation(away, _panelRoot.up);
            }
        }

        private static void NotePanelDockLayout()
        {
            if (_panelRoot == null) return;
            _dockBasisPosition = _panelRoot.position;
            _dockBasisRotation = _panelRoot.rotation;
        }

        private static void RestoreEditorGroup()
        {
            if (_panelGroup == null) return;
            _panelGroup.alpha = _panelAlpha;
            _panelGroup.interactable = _panelInteractable;
            _panelGroup.blocksRaycasts = _panelRaycasts;
        }

        private static void ClearPanelPresentation()
        {
            if (_panelHarmony != null) _panelHarmony.UnpatchAll(_panelHarmony.Id);
            _panelHarmony = null;
            if (_panelHidden) RestoreEditorGroup();
            if (_panelGroup != null && !_panelBorrowedGroup) UnityEngine.Object.Destroy(_panelGroup);
            if (_panelRoot != null && _panelOrbit)
            {
                _panelRoot.position = _panelNativePosition;
                _panelRoot.rotation = _panelNativeRotation;
            }
            if (_panelControlCanvas != null && SuperController.singleton != null)
                SuperController.singleton.RemoveCanvas(_panelControlCanvas);
            _panelCanvasOwner = null; _panelRoot = null; _panelGroup = null; _panelControlCanvas = null;
            _panelHidden = _panelOrbit = false;
            _panelAdvanceFrame = _panelInputFrame = -1;
            PanelGesture.Reset();
        }
    }
}
