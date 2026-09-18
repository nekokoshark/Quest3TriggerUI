using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    internal sealed partial class VrKeyboardOverlay
    {
        private const float CanvasWidth = 2260f;
        private const float CanvasHeight = 770f;
        private const string DefaultHelpText =
            "用头部/控制器光标指向按键，食指扳机按下/松开会产生真实 KeyDown/KeyUp；方向键可按住。" +
            " Ctrl / Shift / Alt / Win 为锁定键，再点一次释放；关闭键盘会自动释放所有按下的键。";
        private readonly Quest3TriggerUIPlugin _plugin;
        private readonly float _scale;
        private readonly float _distance;
        private readonly float _opacity;
        private readonly SceneQuickActions _quickActions;
        private readonly ScenePlaybackFlow _playback = new ScenePlaybackFlow();
        private GameObject _playbackPanel;
        private QuickActionDefinition _personDefinition;
        private QuickActionDefinition _playbackDefinition;
        private bool _playbackCatalogHierarchical;
        private int _quickActionRevision;
        private readonly List<QuickActionDefinition> _quickActionDefinitions;
        private readonly Dictionary<ushort, bool> _downKeys = new Dictionary<ushort, bool>();
        private struct RepeatKey
        {
            internal bool Extended;
            internal float Next;
        }
        private readonly Dictionary<ushort, RepeatKey> _repeats =
            new Dictionary<ushort, RepeatKey>();
        private readonly List<ushort> _repeatScratch = new List<ushort>(4);
		private readonly Dictionary<TemporaryShortcutGesture, List<ShortcutBindingTarget>> _shortcutBindings =
			new Dictionary<TemporaryShortcutGesture, List<ShortcutBindingTarget>>();
		private readonly List<IShortcutBindable> _bindingSelection = new List<IShortcutBindable>();
		private readonly List<ShortcutBindingTarget> _pendingShortcutKeys =
			new List<ShortcutBindingTarget>();
        private readonly Text[] _bindingTexts = new Text[ShortcutGestureBank.GestureCount];
        private readonly GameObject[] _bindingDeleteButtons = new GameObject[ShortcutGestureBank.GestureCount];
        private Canvas _canvas;
        private Font _font;
        private bool _dragging;
        private Vector3 _dragLocalPosition;
        private Quaternion _dragLocalRotation;
		private ShortcutActionButton _embodyButton;
		private ShortcutActionButton _standbyButton;
        private Image _shortcutModeButtonImage;
        private Text _helpText;
        private Slider _eyeGapSlider;
        private Text _eyeGapValueText;
        private bool _syncingEyeGapSlider;
        private bool _bindingMode;
        private int _shortcutReleaseFrame = -1;
        private float _nextEmbodySync;
        private VrHoverFeedback _pointerHoverFeedback;

        internal VrKeyboardOverlay(Quest3TriggerUIPlugin plugin, float scale, float distance, float opacity)
        {
            _plugin = plugin;
            _scale = scale;
            _distance = distance;
            _opacity = Mathf.Clamp(opacity, 0.2f, 1f);
            _quickActions = new SceneQuickActions(plugin);
            _quickActionDefinitions = BuildQuickActionDefinitions();
        }

        internal IList<QuickActionDefinition> QuickActions
        {
            get { return _quickActionDefinitions.AsReadOnly(); }
        }

        // The radial menu is built lazily.  This revision changes only when
        // the playback catalog changes shape (flat scene vs level/pose scene),
        // so the radial canvas can be rebuilt once instead of being scanned
        // every frame.
        internal int QuickActionRevision
        {
            get { return _quickActionRevision; }
        }

        internal bool Visible
        {
            get { return _canvas != null && _canvas.gameObject.activeSelf; }
        }

        internal void ThrottleStandbyFrame()
        {
            _quickActions.ThrottleStandbyFrame();
        }

        internal void LateTick()
        {
            VrTextInputBridge.Tick();
            TickImeUi();
            _quickActions.LateTickEyeGap();
        }

        internal bool BindingMode
        {
            get { return _bindingMode; }
        }

        internal bool EmbodyActive
        {
            get { return _quickActions != null && _quickActions.EmbodyActive; }
        }

        internal bool StandbyActive
        {
            get { return _quickActions != null && _quickActions.StandbyActive; }
        }

        internal void Toggle()
        {
            if (_canvas == null)
                Build();

            bool show = !_canvas.gameObject.activeSelf;
            if (!show)
            {
                VrTextInputBridge.Clear();
                SetBindingMode(false);
                EndDrag();
                ReleaseAllKeys();
                ClearPointerHover();
            }
            _canvas.gameObject.SetActive(show);
            if (show)
            {
                SyncEyeGapSlider();
                SyncDlssControls();
                VrPointerPresentation.EnsureVisible();
            }
        }

        internal void Tick()
        {
            TickRecording();
            if (Visible)
                SyncPointerHover();
            else
                ClearPointerHover();

            if (_repeats.Count > 0 && VrTextInputBridge.Active)
                TickKeyRepeat();

            if (_pendingShortcutKeys.Count > 0 && Time.frameCount >= _shortcutReleaseFrame)
                ReleasePendingShortcut();

            if (Visible && Time.unscaledTime >= _nextEmbodySync)
            {
                _nextEmbodySync = Time.unscaledTime + 0.25f;
                SetEmbodyButtonActive(_quickActions.EmbodyActive);
                SyncDlssControls();
            }

            if (!_dragging || _canvas == null)
                return;

            if (Quest3TriggerUIPlugin.Trigger == null ||
                Quest3TriggerUIPlugin.Trigger.AnalogValue <
                Quest3TriggerUIPlugin.Trigger.ReleaseThreshold)
            {
                EndDrag();
                return;
            }

            Transform anchor = DragAnchor();
            if (anchor == null)
                return;

            Vector3 targetPosition = anchor.TransformPoint(_dragLocalPosition);
            Quaternion targetRotation = anchor.rotation * _dragLocalRotation;
            float blend = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
            _canvas.transform.position = Vector3.Lerp(
                _canvas.transform.position, targetPosition, blend);
            _canvas.transform.rotation = Quaternion.Slerp(
                _canvas.transform.rotation, targetRotation, blend);
        }

        private static bool IsRepeatableTextKey(ushort virtualKey)
        {
            return virtualKey == 0x08 || virtualKey == 0x2E ||
                   virtualKey == 0x25 || virtualKey == 0x27;
        }

        // Held-key repeat for text editing used to run inside ~100 per-key
        // MonoBehaviour Update calls; the owner now iterates only held keys.
        private void TickKeyRepeat()
        {
            float now = Time.unscaledTime;
            _repeatScratch.Clear();
            foreach (KeyValuePair<ushort, RepeatKey> pair in _repeats)
                if (now >= pair.Value.Next)
                    _repeatScratch.Add(pair.Key);
            for (int i = 0; i < _repeatScratch.Count; i++)
            {
                ushort key = _repeatScratch[i];
                RepeatKey repeat;
                if (!_repeats.TryGetValue(key, out repeat))
                    continue;
                VrTextInputBridge.Send(key, repeat.Extended, _downKeys);
                repeat.Next = now + 0.055f;
                _repeats[key] = repeat;
            }
        }

        internal void HandleShortcutGestures(ShortcutGestureBank gestures, int frame)
        {
            if (gestures == null) return;
            for (int i = 0; i < ShortcutGestureBank.GestureCount; i++)
                if (gestures.Triggered((TemporaryShortcutGesture)i, frame))
                    HandleShortcutGesture((TemporaryShortcutGesture)i);
        }

		internal void ToggleBindingTarget(IShortcutBindable target)
        {
			if (!_bindingMode || target == null)
                return;

			int index = _bindingSelection.IndexOf(target);
            if (index >= 0)
            {
                _bindingSelection.RemoveAt(index);
				target.SetBindingSelected(false);
            }
            else
            {
				_bindingSelection.Add(target);
				target.SetBindingSelected(true);
            }
            UpdateHelpText();
        }

        internal void BeginDrag()
        {
            if (!Visible)
                return;

            Transform anchor = DragAnchor();
            if (anchor == null)
                return;

            _canvas.transform.SetParent(null, true);
            _dragLocalPosition = anchor.InverseTransformPoint(_canvas.transform.position);
            _dragLocalRotation = Quaternion.Inverse(anchor.rotation) *
                                 _canvas.transform.rotation;
            _dragging = true;
        }

        internal void EndDrag()
        {
            _dragging = false;
        }

        internal void Recenter()
        {
            if (_canvas == null || SuperController.singleton == null)
                return;

            EndDrag();
            Transform anchor = SuperController.singleton.centerCameraTarget.transform;
            _canvas.transform.SetParent(anchor, false);
            _canvas.transform.localPosition = new Vector3(0f, -0.20f, _distance);
            _canvas.transform.localRotation = Quaternion.identity;
            _canvas.transform.localScale = Vector3.one * _scale;
        }

        // Dock the keyboard just below a host canvas (e.g. the preset browser
        // that sits at the control panel) instead of following the view
        // centre. Positioned in world space so the keyboard's top edge clears
        // the host's bottom edge with a small gap.
        internal void DockAt(Transform host)
        {
            if (_canvas == null || host == null)
                return;
            EndDrag();
            RectTransform hr = host as RectTransform;
            float hostHalfWorld = (hr != null ? hr.rect.height : 1100f) *
                0.5f * host.lossyScale.y;
            float kbHalfWorld = CanvasHeight * 0.5f * _scale;
            _canvas.transform.SetParent(host, false);
            // Offset slightly toward the user: third-party panels parked
            // under the control panel (e.g. CUA Manager's helper bar)
            // would otherwise render on top of the docked keyboard and
            // swallow its raycasts.
            _canvas.transform.position =
                host.position - host.up * (hostHalfWorld + kbHalfWorld + 0.015f) +
                host.forward * 0.08f;
            _canvas.transform.rotation = host.rotation;
            float hs = host.lossyScale.x;
            _canvas.transform.localScale = Vector3.one *
                (hs > 1e-6f ? _scale / hs : _scale);
        }

        internal bool IsDockedUnder(Transform host)
        {
            return _canvas != null && host != null &&
                _canvas.transform.parent == host;
        }

        internal void KeyDown(ushort virtualKey, bool extended)
        {
            if (_downKeys.ContainsKey(virtualKey))
                return;

            // Doubao bridge owns the whole key path while active — every key
            // becomes a real keybd_event for the desktop IME; OS auto-repeat
            // replaces our repeat timer and the field commits natively.
            if (DoubaoImeBridge.KeyDown(virtualKey, extended))
                return;

            // Shift-tap candidate tracking for the IME 中/英 toggle — any
            // non-shift key while shift is held makes it a modifier use.
            if (virtualKey == 0xA0 || virtualKey == 0xA1)
                VrTextInputBridge.NoteShiftDown();
            else
                VrTextInputBridge.NoteOtherKey();

            if (VrTextInputBridge.HandleVoiceKey(virtualKey, false))
            {
                _downKeys.Add(virtualKey, extended);
                return;
            }

            if (VrTextInputBridge.Send(virtualKey, extended, _downKeys))
            {
                if (IsRepeatableTextKey(virtualKey))
                    _repeats[virtualKey] = new RepeatKey {
                        Extended = extended,
                        Next = Time.unscaledTime + 0.42f };
                return;
            }

            VirtualKeyboardState.KeyDown(virtualKey, extended);
            _downKeys.Add(virtualKey, extended);
            if (!WindowsKeyboard.Send(virtualKey, false, extended) &&
                Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogError(
                    "Targeted key-down post failed for VK 0x" + virtualKey.ToString("X2") +
                    "; Win32=" + Marshal.GetLastWin32Error());
        }

        internal void KeyUp(ushort virtualKey, bool extended)
        {
            _repeats.Remove(virtualKey);
            if (DoubaoImeBridge.KeyUp(virtualKey, extended))
                return;
            // Evaluate the tap BEFORE the _downKeys gate — a lone Shift tap
            // must reach the desktop IME even if down-state tracking lost it.
            if (virtualKey == 0xA0 || virtualKey == 0xA1)
                VrTextInputBridge.NoteShiftUp();
            if (!_downKeys.ContainsKey(virtualKey))
                return;

            bool downExtended;
            _downKeys.TryGetValue(virtualKey, out downExtended);
            if (VrTextInputBridge.HandleVoiceKey(virtualKey, true))
            {
                _downKeys.Remove(virtualKey);
                return;
            }
            VirtualKeyboardState.KeyUp(virtualKey, downExtended);
            if (!WindowsKeyboard.Send(virtualKey, true, downExtended) && Quest3TriggerUIPlugin.Log != null)
            {
                Quest3TriggerUIPlugin.Log.LogError(
                    "Targeted key-up post failed for VK 0x" + virtualKey.ToString("X2") +
                    "; Win32=" + Marshal.GetLastWin32Error());
            }
            _downKeys.Remove(virtualKey);
        }

        internal bool ToggleModifier(ushort virtualKey, bool extended)
        {
            if (_downKeys.ContainsKey(virtualKey))
                KeyUp(virtualKey, extended);
            else
                KeyDown(virtualKey, extended);
            return _downKeys.ContainsKey(virtualKey);
        }

        internal void ReleaseAllKeys()
        {
            ReleasePendingShortcut();
            DoubaoImeBridge.ReleaseAll();
            foreach (KeyValuePair<ushort, bool> key in _downKeys)
            {
                VirtualKeyboardState.KeyUp(key.Key, key.Value);
                WindowsKeyboard.Send(key.Key, true, key.Value);
            }
            _downKeys.Clear();
            _repeats.Clear();

            if (_canvas != null)
            {
                VirtualKeyButton[] buttons =
                    _canvas.GetComponentsInChildren<VirtualKeyButton>(true);
                for (int i = 0; i < buttons.Length; i++)
                    buttons[i].ResetVisual();
            }
        }

        private void ToggleBindingMode()
        {
            SetBindingMode(!_bindingMode);
        }

        private void SetBindingMode(bool active)
        {
            if (_bindingMode == active)
                return;

            _bindingMode = active;
            ClearBindingSelection();
            if (_shortcutModeButtonImage != null)
                _shortcutModeButtonImage.color = active
                    ? new Color(0.84f, 0.52f, 0.08f, 1f)
                    : new Color(0.11f, 0.38f, 0.48f, 1f);
            UpdateHelpText();
        }

        private void ClearBindingSelection()
        {
            for (int i = 0; i < _bindingSelection.Count; i++)
                _bindingSelection[i].SetBindingSelected(false);
            _bindingSelection.Clear();
        }

        private void HandleShortcutGesture(TemporaryShortcutGesture gesture)
        {
            if (_bindingMode)
            {
                if (_bindingSelection.Count == 0)
                {
                    UpdateHelpText("请先点选一个或多个键，再执行要绑定的手柄动作。");
                    return;
                }

				List<ShortcutBindingTarget> targets = new List<ShortcutBindingTarget>();
                for (int i = 0; i < _bindingSelection.Count; i++)
					targets.Add(_bindingSelection[i].BindingTarget);
				_shortcutBindings[gesture] = targets;
                ClearBindingSelection();
                RefreshBindingRows();
                UpdateHelpText(GestureLabel(gesture) + " 已完成绑定；可继续绑定，或再次点击临时快捷键退出。");
                return;
            }

			List<ShortcutBindingTarget> binding;
            if (_shortcutBindings.TryGetValue(gesture, out binding))
                ExecuteShortcut(binding);
        }

		private void ExecuteShortcut(List<ShortcutBindingTarget> targets)
        {
			if (targets == null || targets.Count == 0)
                return;

            ReleasePendingShortcut();
			for (int i = 0; i < targets.Count; i++)
            {
				ShortcutBindingTarget target = targets[i];
				if (target.IsAction)
				{
					target.InvokeAction();
					continue;
				}
				if (_downKeys.ContainsKey(target.VirtualKey))
                    continue;
				KeyDown(target.VirtualKey, target.Extended);
				_pendingShortcutKeys.Add(target);
            }
            _shortcutReleaseFrame = Time.frameCount + 1;
        }

        private void ReleasePendingShortcut()
        {
            for (int i = _pendingShortcutKeys.Count - 1; i >= 0; i--)
            {
				ShortcutBindingTarget target = _pendingShortcutKeys[i];
				KeyUp(target.VirtualKey, target.Extended);
            }
            _pendingShortcutKeys.Clear();
            _shortcutReleaseFrame = -1;
        }

        private void DeleteBinding(TemporaryShortcutGesture gesture)
        {
            _shortcutBindings.Remove(gesture);
            RefreshBindingRows();
        }

        private void RefreshBindingRows()
        {
            for (int i = 0; i < ShortcutGestureBank.GestureCount; i++)
            {
                TemporaryShortcutGesture gesture = (TemporaryShortcutGesture)i;
				List<ShortcutBindingTarget> targets;
				bool assigned = _shortcutBindings.TryGetValue(gesture, out targets) && targets.Count > 0;
                if (_bindingTexts[i] != null)
                    _bindingTexts[i].text = GestureLabel(gesture) + "\n" +
						(assigned ? ShortcutLabel(targets) : "未绑定");
                if (_bindingDeleteButtons[i] != null)
                    _bindingDeleteButtons[i].SetActive(assigned);
            }
        }

        private void UpdateHelpText()
        {
            UpdateHelpText(null);
        }

        private void UpdateHelpText(string message)
        {
            if (_helpText == null)
                return;
            if (!string.IsNullOrEmpty(message))
            {
                _helpText.text = message;
                return;
            }
            if (!_bindingMode)
            {
                _helpText.text = DefaultHelpText;
                return;
            }

            _helpText.text = string.IsNullOrEmpty(message)
				? "临时快捷键绑定模式：点选一个或多个键盘键/功能按钮（橙色高亮），然后执行 左/右手的食指或Grip连按2次/3次（双击等待0.32秒确认）。已选 " +
                  _bindingSelection.Count + " 个键。"
                : message;
        }

        private static string GestureLabel(TemporaryShortcutGesture gesture) { return ShortcutGestureBank.Label(gesture); }

		private static string ShortcutLabel(List<ShortcutBindingTarget> targets)
        {
            StringBuilder builder = new StringBuilder();
			for (int i = 0; i < targets.Count; i++)
            {
                if (i > 0) builder.Append(" + ");
				builder.Append(targets[i].Label);
            }
            return builder.ToString();
        }

        internal Camera PointerCamera()
        {
            if (SuperController.singleton != null && SuperController.singleton.lookCamera != null)
                return SuperController.singleton.lookCamera;
            return Camera.main;
        }

        private Transform DragAnchor()
        {
            if (SuperController.singleton != null && SuperController.singleton.OVRRig != null)
            {
                OVRCameraRig rig = SuperController.singleton.OVRRig.GetComponent<OVRCameraRig>();
                if (rig != null && rig.rightHandAnchor != null)
                    return rig.rightHandAnchor;
            }

            Camera camera = PointerCamera();
            return camera == null ? null : camera.transform;
        }

        internal void Dispose()
        {
            _quickActions.Dispose();
            VrTextInputBridge.Shutdown();
            ReleaseAllKeys();
            if (_canvas == null)
                return;

            if (SuperController.singleton != null)
                SuperController.singleton.RemoveCanvas(_canvas);
            KeyboardRaycastPriority.Canvas = null;
            UnityEngine.Object.Destroy(_canvas.gameObject);
            _canvas = null;
        }

        private void Build()
        {
            GameObject canvasObject = new GameObject("Quest3 Full VR Keyboard");
            _canvas = canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.pixelPerfect = false;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = 32767;
            KeyboardRaycastPriority.Canvas = _canvas;
            canvasObject.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1f;
            canvasObject.AddComponent<GraphicRaycaster>();
            CanvasGroup keyboardGroup = canvasObject.AddComponent<CanvasGroup>();
            keyboardGroup.alpha = _opacity;

            RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, CanvasWidth);
            canvasRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, CanvasHeight);
            SuperController.singleton.AddCanvas(_canvas);
            _font = (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");

            CreatePanelBackground(canvasRect);
            CreateQuickActions(canvasRect);
            CreateHeader(canvasRect);
            CreateKeyboardRows(canvasRect);
            CreateImeUi(canvasRect);
            CreateShortcutPanel(canvasRect);
            SetLayerRecursively(canvasObject, ResolveUiLayer());
            Recenter();
            canvasObject.SetActive(false);
        }

        private void SyncPointerHover()
        {
            VrPointerPresentation.EnsureVisible();
            GameObject target = VrPointerPresentation.CurrentLookTarget();
            VrHoverFeedback next = null;
            if (target != null && _canvas != null &&
                target.transform.IsChildOf(_canvas.transform))
            {
                Button button = target.GetComponentInParent<Button>();
                if (button != null)
                {
                    next = button.GetComponent<VrHoverFeedback>();
                    if (next == null)
                        next = button.gameObject.AddComponent<VrHoverFeedback>();
                }
            }

            if (ReferenceEquals(next, _pointerHoverFeedback))
                return;
            ClearPointerHover();
            if (next == null)
                return;

            _pointerHoverFeedback = next;
            next.SetHovered(true);
            VrHaptics.Hover();
        }

        private void ClearPointerHover()
        {
            if (_pointerHoverFeedback != null)
                _pointerHoverFeedback.SetHovered(false);
            _pointerHoverFeedback = null;
        }

        private void CreatePanelBackground(RectTransform parent)
        {
            GameObject background = CreateUiObject("Background", parent);
            RectTransform rect = background.GetComponent<RectTransform>();
            Stretch(rect);
            Image image = background.AddComponent<Image>();
            image.color = new Color(0.035f, 0.045f, 0.060f, 0.97f);
            image.raycastTarget = false;
            background.transform.SetAsFirstSibling();
        }

        private void CreateHeader(RectTransform parent)
        {
            GameObject header = CreateUiObject("Drag title bar", parent);
            RectTransform rect = header.GetComponent<RectTransform>();
            SetTopLeft(rect, 10f, 80f, 1480f, 58f);
            Image image = header.AddComponent<Image>();
            image.color = new Color(0.08f, 0.24f, 0.34f, 1f);
            KeyboardDragHandle drag = header.AddComponent<KeyboardDragHandle>();
            drag.Configure(this, _canvas.transform);
			AddText(header.transform,
				"全功能键盘  |  标题栏拖动  |  左右握把短按：归位  |  右食指长按：圆盘  |  右食指+握把短按：显示/隐藏，长按：抓取",
                26, TextAnchor.MiddleLeft, new Color(0.93f, 0.98f, 1f, 1f), 18f);

            CreateActionButton(parent, "归位", 1500f, 80f, 145f, 58f, Recenter);
            CreateActionButton(parent, "关闭", 1655f, 80f, 175f, 58f, Toggle);
        }

		private void CreateQuickActions(RectTransform parent)
        {
            const float startX = 10f;
            const float endX = 1830f;
            const float gap = 10f;
            float buttonWidth = (endX - startX -
                gap * (_quickActionDefinitions.Count - 1)) / _quickActionDefinitions.Count;
            float x = startX;
            for (int i = 0; i < _quickActionDefinitions.Count; i++)
            {
                QuickActionDefinition definition = _quickActionDefinitions[i];
				ShortcutActionButton button = CreateBindableActionButton(
                    parent, definition.Label, x, 10f, buttonWidth, 58f, definition.Action);
                if (definition.Label == "Embody")
                    _embodyButton = button;
                else if (definition.Id == "dlss")
                    _dlssRootButton = button;
                else if (definition.Label == "待机/恢复")
                {
                    _standbyButton = button;
                    if (_quickActions.StandbyActive)
                        _standbyButton.SetNormalColor(
                            new Color(0.08f, 0.68f, 0.36f, 1f));
                }
                x += buttonWidth + gap;
            }

            _shortcutModeButtonImage = CreateActionButton(
                parent, "临时快捷键", 1840f, 10f, 400f, 58f, ToggleBindingMode);
        }

        private List<QuickActionDefinition> BuildQuickActionDefinitions()
        {
            return new List<QuickActionDefinition> {
                new QuickActionDefinition("embody", "Embody", ToggleEmbody,
                    delegate { return _quickActions.EmbodyActive; },
                    new List<QuickActionDefinition> {
                        new QuickActionDefinition("embody.reset-navigation", "导航复位",
                            ResetEmbodyNavigation)
                    }),
                new QuickActionDefinition("undress", "脱衣", _quickActions.OpenUiAssistClothingEditor, null,
                    new QuickActionDefinition[] { new QuickActionDefinition("undress.manual", "手动", ClothingRegionMode.Toggle, delegate { return ClothingRegionMode.Active; }) }),
                BuildRecordingAction(),
                BuildPersonAction(),
                new QuickActionDefinition("light-linker", "灯光", _quickActions.OpenLightLinker),
                BuildExpressionAction(),
                BuildPlaybackAction(),
                BuildDlssAction(),
                new QuickActionDefinition("physics", "物理",
                    delegate { _quickActions.ApplyPhysicsPackage(UpdateHelpText); }),
                new QuickActionDefinition("physics.budget", "物理降载", PhysicsBudget.Cycle,
                    delegate { return PhysicsBudget.Level > 0; },
                    new List<QuickActionDefinition> {
                        new QuickActionDefinition("physics.budget.off", "关闭",
                            delegate { PhysicsBudget.SetLevelEntry(0); },
                            delegate { return PhysicsBudget.Level == 0; }),
                        new QuickActionDefinition("physics.budget.balanced", "均衡",
                            delegate { PhysicsBudget.SetLevelEntry(1); },
                            delegate { return PhysicsBudget.Level == 1; }),
                        new QuickActionDefinition("physics.budget.manual", "手动",
                            delegate { HairDebugMode.Toggle(); },
                            delegate { return HairDebugMode.Active; })
                    }),
                new QuickActionDefinition("optimize-memory", "优化内存", _quickActions.OptimizeMemory),
                new QuickActionDefinition("refresh-var", "刷新VAR", RefreshVars, null,
                    new List<QuickActionDefinition> {
                        new QuickActionDefinition("refresh-var.incremental", "增量刷新", RefreshVars),
                        new QuickActionDefinition("refresh-var.full", "完整扫描", delegate { _quickActions.FullRefreshVars(UpdateHelpText); })
                    }),
                new QuickActionDefinition("standby", "待机/恢复", ToggleStandby,
                    delegate { return _quickActions.StandbyActive; })
            };
        }

        private QuickActionDefinition BuildPersonAction()
        {
            _personDefinition = new QuickActionDefinition("person", "人物",
                delegate { _quickActions.OpenPersonControlPanel(); }, null,
                new List<QuickActionDefinition> {
                    new QuickActionDefinition("person.replace", "替换", _quickActions.OpenPersonPreset),
                    new QuickActionDefinition("appearance", "外观", _quickActions.OpenAppearancePresetWithoutClothing, null,
                        new List<QuickActionDefinition> {
                            new QuickActionDefinition("appearance.save", "保存", _quickActions.SaveAppearancePreset)
                        }),
                    new QuickActionDefinition("skin", "皮肤", _quickActions.OpenSkinPreset, null,
                        new List<QuickActionDefinition> {
                            new QuickActionDefinition("skin.save", "保存", _quickActions.SaveSkinPreset)
                        }),
                    new QuickActionDefinition("person.hair", "头发", _quickActions.OpenHairPreset, null,
                        new List<QuickActionDefinition> {
                            new QuickActionDefinition("person.hair.save", "保存", _quickActions.SaveHairPreset)
                        })
                });
            return _personDefinition;
        }

        private QuickActionDefinition BuildExpressionAction()
        {
            List<QuickActionDefinition> children = new List<QuickActionDefinition>();
            children.Add(new QuickActionDefinition(
                "expression.initialize", "表情初始化",
                delegate { _quickActions.InitializeExpressions(UpdateHelpText); },
                delegate { return _quickActions.ExpressionTimelinesInitialized; }));
            ExpressionDefinition[] expressions = SevenSeasonExpressionLibrary.All;
            for (int i = 0; i < expressions.Length; i++)
            {
                int expressionIndex = i;
                ExpressionDefinition expression = expressions[i];
                children.Add(new QuickActionDefinition(
                    "expression." + expressionIndex, expression.Label,
                    delegate {
                        _quickActions.PlayExpression(expressionIndex, UpdateHelpText);
                    },
                    delegate {
                        return _quickActions.IsExpressionActive(expressionIndex);
                    }));
            }
            return new QuickActionDefinition(
                "expression", "表情",
                delegate { _quickActions.ReplayActiveExpression(UpdateHelpText); },
                delegate { return _quickActions.ExpressionActive; },
                children);
        }

        private QuickActionDefinition BuildPlaybackAction()
        {
            _playbackDefinition = new QuickActionDefinition("playback", "播放", TogglePlaybackPanel,
                delegate { return _playback.Ready; },
                BuildPlaybackChildren(false));
            return _playbackDefinition;
        }

        private List<QuickActionDefinition> BuildPlaybackChildren(bool hierarchical)
        {
            List<QuickActionDefinition> children = new List<QuickActionDefinition>();
            children.Add(new QuickActionDefinition(
                "playback.initialize", "初始化",
                delegate {
                    _playback.Initialize(PlaybackStatus);
                },
                delegate { return _playback.Ready; }));
            if (hierarchical)
            {
                children.Add(new QuickActionDefinition(
                    "playback.level.next", "下一个 level",
                    delegate { _playback.StepLevel(true, PlaybackStatus); }));
                children.Add(new QuickActionDefinition(
                    "playback.pose.next", "下一个 pose",
                    delegate { _playback.StepPose(true, PlaybackStatus); }));
                children.Add(new QuickActionDefinition(
                    "playback.level.previous", "上一个 level",
                    delegate { _playback.StepLevel(false, PlaybackStatus); }));
                children.Add(new QuickActionDefinition(
                    "playback.pose.previous", "上一个 pose",
                    delegate { _playback.StepPose(false, PlaybackStatus); }));
            }
            else
            {
                children.Add(new QuickActionDefinition(
                    "playback.next", "下一个",
                    delegate { _playback.Step(true, PlaybackStatus); }));
                children.Add(new QuickActionDefinition(
                    "playback.previous", "上一个",
                    delegate { _playback.Step(false, PlaybackStatus); }));
            }
            return children;
        }

        private void RefreshPlaybackActionCatalog()
        {
            bool hierarchical = _playback.IsHierarchical;
            if (_playbackDefinition == null ||
                _playbackCatalogHierarchical == hierarchical)
                return;

            IList<QuickActionDefinition> children = _playbackDefinition.Children;
            children.Clear();
            List<QuickActionDefinition> replacement =
                BuildPlaybackChildren(hierarchical);
            for (int i = 0; i < replacement.Count; i++)
                children.Add(replacement[i]);
            _playbackCatalogHierarchical = hierarchical;
            _quickActionRevision++;
            RebuildPlaybackPanel();
        }

        private void PlaybackStatus(string message)
        {
            RefreshPlaybackActionCatalog();
            UpdateHelpText(message);
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo(message);
        }

        private void TogglePlaybackPanel()
        {
            if (_canvas == null) Build();
            if (!Visible) _canvas.gameObject.SetActive(true);
            if (_playbackPanel != null)
            {
                _playbackPanel.SetActive(!_playbackPanel.activeSelf);
                return;
            }
            CreatePlaybackPanel();
        }

        private void RebuildPlaybackPanel()
        {
            if (_playbackPanel == null)
                return;
            bool wasActive = _playbackPanel.activeSelf;
            UnityEngine.Object.Destroy(_playbackPanel);
            _playbackPanel = null;
            if (wasActive)
                CreatePlaybackPanel();
        }

        private void CreatePlaybackPanel()
        {
            _playbackPanel = CreateUiObject("PlaybackMenu", _canvas.GetComponent<RectTransform>());
            RectTransform rect = _playbackPanel.GetComponent<RectTransform>();
            IList<QuickActionDefinition> children = _playbackDefinition.Children;
            float panelWidth = 20f + children.Count * 245f +
                Math.Max(0, children.Count - 1) * 10f;
            SetTopLeft(rect, 950f, -110f, panelWidth, 100f);
            _playbackPanel.AddComponent<Image>().color = new Color(0.04f, 0.06f, 0.09f, 0.98f);
            for (int i = 0; i < children.Count; i++)
                CreateBindableActionButton(rect, "播放/" + children[i].Label,
                    10f + 255f * i, 10f, 245f, 80f, children[i].Action);
        }

        private void RefreshVars()
        {
            _quickActions.RefreshVars(UpdateHelpText);
        }

        private void ToggleStandby()
        {
            _quickActions.ToggleStandby(delegate(string message) {
                if (_standbyButton != null)
                    _standbyButton.SetNormalColor(_quickActions.StandbyActive
                        ? new Color(0.08f, 0.68f, 0.36f, 1f)
                        : new Color(0.11f, 0.38f, 0.48f, 1f));
                UpdateHelpText(message);
            });
        }

        private void ToggleEmbody()
        {
            if (_quickActions.EmbodyBusy)
                return;
            bool wasActive = _quickActions.EmbodyActive;
            if (_plugin != null)
                _plugin.BeforeEmbodyToggle(wasActive);
            _quickActions.ToggleEmbody(delegate(bool active) {
                if (_plugin != null)
                    _plugin.AfterEmbodyToggle(wasActive, active);
                SetEmbodyButtonActive(active);
            });
        }

        private void ResetEmbodyNavigation()
        {
            bool disabled = _quickActions.DeactivateEmbodyForNavigationReset();
            if (_plugin != null)
                _plugin.ForceEmbodyNavigationReset();
            SetEmbodyButtonActive(false);
            UpdateHelpText(disabled
                ? "已退出 Embody 并复位第三人称导航。"
                : "第三人称导航已复位。");
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo(
                    "Embody navigation reset: yaw preserved; pitch/roll cleared; head position preserved.");
        }

        private void SetEmbodyButtonActive(bool active)
        {
			if (_embodyButton != null)
				_embodyButton.SetNormalColor(active
                    ? new Color(0.08f, 0.68f, 0.36f, 1f)
					: new Color(0.11f, 0.38f, 0.48f, 1f));
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

        private void CreateKeyboardRows(RectTransform parent)
        {
            float y = 150f;
            AddRow(parent, 20f, y, new KeySpec[] {
                K("Esc", 0x1B), K("F1", 0x70), K("F2", 0x71), K("F3", 0x72),
                K("F4", 0x73), K("F5", 0x74), K("F6", 0x75), K("F7", 0x76),
                K("F8", 0x77), K("F9", 0x78), K("F10", 0x79), K("F11", 0x7A),
                K("F12", 0x7B), K("PrtSc", 0x2C, 105f, true),
                K("ScrLk", 0x91, 105f), K("Pause", 0x13, 105f)
            }, 101f, 6f, 62f);

            y = 224f;
            AddRow(parent, 20f, y, new KeySpec[] {
                K("` ~", 0xC0), K("1 !", 0x31), K("2 @", 0x32), K("3 #", 0x33),
                K("4 $", 0x34), K("5 %", 0x35), K("6 ^", 0x36), K("7 &", 0x37),
                K("8 *", 0x38), K("9 (", 0x39), K("0 )", 0x30), K("- _", 0xBD),
                K("= +", 0xBB), K("Back", 0x08, 142f)
            }, 80f, 6f, 68f);
            AddRow(parent, 1310f, y, new KeySpec[] {
                K("Ins", 0x2D, 76f, true), K("Home", 0x24, 76f, true), K("PgUp", 0x21, 76f, true)
            }, 76f, 6f, 68f);
            AddRow(parent, 1562f, y, new KeySpec[] {
                K("Num", 0x90, 61f), K("/", 0x6F, 61f, true), K("*", 0x6A, 61f), K("-", 0x6D, 61f)
            }, 61f, 6f, 68f);

            y = 298f;
            AddRow(parent, 20f, y, new KeySpec[] {
                K("Tab", 0x09, 108f), K("Q", 0x51), K("W", 0x57), K("E", 0x45),
                K("R", 0x52), K("T", 0x54), K("Y", 0x59), K("U", 0x55),
                K("I", 0x49), K("O", 0x4F), K("P", 0x50), K("[ {", 0xDB),
                K("] }", 0xDD), K("\\ |", 0xDC, 108f)
            }, 80f, 6f, 68f);
            AddRow(parent, 1310f, y, new KeySpec[] {
                K("Del", 0x2E, 76f, true), K("End", 0x23, 76f, true), K("PgDn", 0x22, 76f, true)
            }, 76f, 6f, 68f);
            AddRow(parent, 1562f, y, new KeySpec[] {
                K("7", 0x67, 61f), K("8", 0x68, 61f), K("9", 0x69, 61f), K("+", 0x6B, 61f)
            }, 61f, 6f, 68f);

            y = 372f;
            AddRow(parent, 20f, y, new KeySpec[] {
                M("Caps", 0x14, 124f), K("A", 0x41), K("S", 0x53), K("D", 0x44),
                K("F", 0x46), K("G", 0x47), K("H", 0x48), K("J", 0x4A),
                K("K", 0x4B), K("L", 0x4C), K("; :", 0xBA), K("' \"", 0xDE),
                K("Enter", 0x0D, 156f)
            }, 80f, 6f, 68f);
            AddRow(parent, 1562f, y, new KeySpec[] {
                K("4", 0x64, 61f), K("5", 0x65, 61f), K("6", 0x66, 61f), K("+", 0x6B, 61f)
            }, 61f, 6f, 68f);

            y = 446f;
            AddRow(parent, 20f, y, new KeySpec[] {
                M("L Shift", 0xA0, 148f), K("Z", 0x5A), K("X", 0x58), K("C", 0x43),
                K("V", 0x56), K("B", 0x42), K("N", 0x4E), K("M", 0x4D),
                K(", <", 0xBC), K(". >", 0xBE), K("/ ?", 0xBF), M("R Shift", 0xA1, 148f)
            }, 80f, 6f, 68f);
            AddRow(parent, 1392f, y, new KeySpec[] { K("↑", 0x26, 76f, true) }, 76f, 6f, 68f);
            AddRow(parent, 1562f, y, new KeySpec[] {
                K("1", 0x61, 61f), K("2", 0x62, 61f), K("3", 0x63, 61f), K("Enter", 0x0D, 61f, true)
            }, 61f, 6f, 68f);

            y = 520f;
            AddRow(parent, 20f, y, new KeySpec[] {
                M("Ctrl", 0xA2, 112f), M("Win", 0x5B, 112f, true), M("Alt", 0xA4, 112f),
                K("Space", 0x20, 430f), K("语音 RAlt", 0xA5, 112f, true),
                M("Win", 0x5C, 112f, true), M("Ctrl", 0xA3, 112f, true)
            }, 80f, 6f, 68f);
            AddRow(parent, 1310f, y, new KeySpec[] {
                K("←", 0x25, 76f, true), K("↓", 0x28, 76f, true), K("→", 0x27, 76f, true)
            }, 76f, 6f, 68f);
            AddRow(parent, 1562f, y, new KeySpec[] {
                K("0", 0x60, 128f), K(".", 0x6E, 61f, true), K("Enter", 0x0D, 61f, true)
            }, 61f, 6f, 68f);

            GameObject help = CreateUiObject("Help", parent);
            RectTransform helpRect = help.GetComponent<RectTransform>();
            SetTopLeft(helpRect, 20f, 606f, 1608f, 144f);
            _helpText = AddText(help.transform, DefaultHelpText,
                27, TextAnchor.MiddleCenter, new Color(0.76f, 0.84f, 0.90f, 1f), 20f);
        }

        private void CreateShortcutPanel(RectTransform parent)
        {
            GameObject panel = CreateUiObject("Temporary shortcut bindings", parent);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            SetTopLeft(panelRect, 1840f, 80f, 400f, 1000f);
            Image panelImage = panel.AddComponent<Image>();
            panelImage.color = new Color(0.055f, 0.075f, 0.095f, 0.98f);
            panelImage.raycastTarget = false;

            CreateEyeGapSlider(parent);

            GameObject title = CreateUiObject("Shortcut title", parent);
            RectTransform titleRect = title.GetComponent<RectTransform>();
            SetTopLeft(titleRect, 1850f, 218f, 380f, 38f);
            AddText(title.transform, "当前临时快捷键", 27, TextAnchor.MiddleCenter,
                new Color(0.93f, 0.98f, 1f, 1f), 4f);

            for (int i = 0; i < ShortcutGestureBank.GestureCount; i++)
            {
                TemporaryShortcutGesture gesture = (TemporaryShortcutGesture)i;
                float y = 264f + i * 84f;
                GameObject row = CreateUiObject("Shortcut " + gesture, parent);
                RectTransform rowRect = row.GetComponent<RectTransform>();
                SetTopLeft(rowRect, 1850f, y, 300f, 76f);
                Image rowImage = row.AddComponent<Image>();
                rowImage.color = new Color(0.12f, 0.16f, 0.20f, 1f);
                rowImage.raycastTarget = false;
                _bindingTexts[i] = AddText(row.transform, "", 24,
                    TextAnchor.MiddleLeft, Color.white, 12f);

                TemporaryShortcutGesture captured = gesture;
                Image delete = CreateActionButton(parent, "×", 2160f, y + 8f, 70f, 60f,
                    delegate { DeleteBinding(captured); });
                _bindingDeleteButtons[i] = delete.gameObject;
            }

            GameObject note = CreateUiObject("Shortcut note", parent);
            RectTransform noteRect = note.GetComponent<RectTransform>();
            SetTopLeft(noteRect, 1850f, 944f, 380f, 120f);
            AddText(note.transform,
				"临时绑定：先选键/功能，再连按。双击等待0.32秒；三击松手生效。× 删除。",
                23, TextAnchor.UpperLeft, new Color(0.74f, 0.83f, 0.90f, 1f), 12f);
            RefreshBindingRows();
        }

        private void CreateEyeGapSlider(RectTransform parent)
        {
            GameObject title = CreateUiObject("Eye gap title", parent);
            RectTransform titleRect = title.GetComponent<RectTransform>();
            SetTopLeft(titleRect, 1850f, 88f, 275f, 36f);
            _eyeGapValueText = AddText(title.transform, "调节眼缝 50%", 26,
                TextAnchor.MiddleCenter, new Color(0.93f, 0.98f, 1f, 1f), 4f);
            CreateActionButton(parent, "定标", 2135f, 88f, 95f, 36f,
                CalibrateEyeGap);

            GameObject sliderObject = CreateUiObject("Eye gap slider", parent);
            RectTransform sliderRect = sliderObject.GetComponent<RectTransform>();
            SetTopLeft(sliderRect, 1855f, 128f, 370f, 48f);
            Image background = sliderObject.AddComponent<Image>();
            background.color = new Color(0.10f, 0.14f, 0.18f, 1f);

            GameObject fillArea = CreateUiObject("Fill Area", sliderRect);
            RectTransform fillAreaRect = fillArea.GetComponent<RectTransform>();
            Stretch(fillAreaRect);
            fillAreaRect.offsetMin = new Vector2(12f, 13f);
            fillAreaRect.offsetMax = new Vector2(-12f, -13f);
            GameObject fill = CreateUiObject("Fill", fillAreaRect);
            RectTransform fillRect = fill.GetComponent<RectTransform>();
            Stretch(fillRect);
            Image fillImage = fill.AddComponent<Image>();
            fillImage.color = new Color(0.12f, 0.64f, 0.80f, 1f);
            fillImage.raycastTarget = false;

            GameObject handleArea = CreateUiObject("Handle Slide Area", sliderRect);
            RectTransform handleAreaRect = handleArea.GetComponent<RectTransform>();
            Stretch(handleAreaRect);
            handleAreaRect.offsetMin = new Vector2(18f, 0f);
            handleAreaRect.offsetMax = new Vector2(-18f, 0f);
            GameObject handle = CreateUiObject("Handle", handleAreaRect);
            RectTransform handleRect = handle.GetComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(38f, 54f);
            Image handleImage = handle.AddComponent<Image>();
            handleImage.color = new Color(0.96f, 0.98f, 1f, 1f);

            _eyeGapSlider = sliderObject.AddComponent<Slider>();
            _eyeGapSlider.direction = Slider.Direction.LeftToRight;
            _eyeGapSlider.minValue = 0f;
            _eyeGapSlider.maxValue = 1f;
            _eyeGapSlider.wholeNumbers = false;
            _eyeGapSlider.fillRect = fillRect;
            _eyeGapSlider.handleRect = handleRect;
            _eyeGapSlider.targetGraphic = handleImage;
            _eyeGapSlider.value = _quickActions.EyeGapSliderValue;
            _eyeGapSlider.onValueChanged.AddListener(OnEyeGapSliderChanged);

            GameObject hint = CreateUiObject("Eye gap hint", parent);
            RectTransform hintRect = hint.GetComponent<RectTransform>();
            SetTopLeft(hintRect, 1850f, 178f, 380f, 30f);
            AddText(hint.transform, "闭合  ←    眼皮间距    →  睁开", 21,
                TextAnchor.MiddleCenter, new Color(0.72f, 0.82f, 0.90f, 1f), 4f);
        }

        private void OnEyeGapSliderChanged(float sliderValue)
        {
            if (_syncingEyeGapSlider)
                return;

            float previous = _quickActions.EyeGapSliderValue;
            string message;
            if (!_quickActions.SetNearestFemaleEyeGap(sliderValue, out message))
            {
                _syncingEyeGapSlider = true;
                _eyeGapSlider.value = previous;
                _syncingEyeGapSlider = false;
            }
            UpdateEyeGapValueText(_quickActions.EyeGapSliderValue);
            UpdateHelpText(message);
        }

        private void CalibrateEyeGap()
        {
            string message;
            if (_quickActions.CalibrateNearestFemaleEyeGap(out message))
                SyncEyeGapSlider();
            UpdateHelpText(message);
        }

        private void SyncEyeGapSlider()
        {
            if (_eyeGapSlider == null)
                return;
            _syncingEyeGapSlider = true;
            _eyeGapSlider.value = _quickActions.EyeGapSliderValue;
            _syncingEyeGapSlider = false;
            UpdateEyeGapValueText(_quickActions.EyeGapSliderValue);
        }

        private void UpdateEyeGapValueText(float sliderValue)
        {
            if (_eyeGapValueText != null)
                _eyeGapValueText.text = "调节眼缝 " +
                    Mathf.RoundToInt(sliderValue * 100f) + "%";
        }

        private void AddRow(
            RectTransform parent,
            float startX,
            float y,
            KeySpec[] keys,
            float defaultWidth,
            float gap,
            float height)
        {
            float x = startX;
            for (int i = 0; i < keys.Length; i++)
            {
                KeySpec key = keys[i];
                float width = key.Width > 0f ? key.Width : defaultWidth;
                CreateKey(parent, key, x, y, width, height);
                x += width + gap;
            }
        }

        private void CreateKey(RectTransform parent, KeySpec key, float x, float y, float width, float height)
        {
            GameObject go = CreateUiObject("Key " + key.Label, parent);
            RectTransform rect = go.GetComponent<RectTransform>();
            SetTopLeft(rect, x, y, width, height);
            Image image = go.AddComponent<Image>();
            image.color = key.Modifier
                ? new Color(0.20f, 0.29f, 0.40f, 1f)
                : new Color(0.18f, 0.20f, 0.24f, 1f);
            Button button = go.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = image;
            Text label = AddText(go.transform, key.Label, 25, TextAnchor.MiddleCenter, Color.white, 4f);
            VirtualKeyButton keyButton = go.AddComponent<VirtualKeyButton>();
            keyButton.Configure(
                this, key.Label, key.VirtualKey, key.Extended, key.Modifier, image, label);
        }

        private Image CreateActionButton(
            RectTransform parent,
            string label,
            float x,
            float y,
            float width,
            float height,
            Action action)
        {
            GameObject go = CreateUiObject(label, parent);
            RectTransform rect = go.GetComponent<RectTransform>();
            SetTopLeft(rect, x, y, width, height);
            Image image = go.AddComponent<Image>();
            image.color = new Color(0.11f, 0.38f, 0.48f, 1f);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(delegate { action(); });
            AddText(go.transform, label, 27, TextAnchor.MiddleCenter, Color.white, 4f);
            return image;
        }

		private ShortcutActionButton CreateBindableActionButton(
			RectTransform parent,
			string label,
			float x,
			float y,
			float width,
			float height,
			Action action)
		{
			GameObject go = CreateUiObject(label, parent);
			RectTransform rect = go.GetComponent<RectTransform>();
			SetTopLeft(rect, x, y, width, height);
			Image image = go.AddComponent<Image>();
			image.color = new Color(0.11f, 0.38f, 0.48f, 1f);
			Button button = go.AddComponent<Button>();
			button.targetGraphic = image;
			ShortcutActionButton bindable = go.AddComponent<ShortcutActionButton>();
			bindable.Configure(this, label, action, image);
			button.onClick.AddListener(bindable.Click);
			AddText(go.transform, label, 27, TextAnchor.MiddleCenter, Color.white, 4f);
			return bindable;
		}

        private Text AddText(
            Transform parent,
            string value,
            int fontSize,
            TextAnchor alignment,
            Color color,
            float horizontalPadding)
        {
            GameObject textObject = CreateUiObject("Text", parent as RectTransform);
            RectTransform rect = textObject.GetComponent<RectTransform>();
            Stretch(rect);
            rect.offsetMin = new Vector2(horizontalPadding, 2f);
            rect.offsetMax = new Vector2(-horizontalPadding, -2f);
            Text text = textObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 14;
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

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetTopLeft(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static KeySpec K(string label, ushort virtualKey)
        {
            return new KeySpec(label, virtualKey, 0f, false, false);
        }

        private static KeySpec K(string label, ushort virtualKey, float width)
        {
            return new KeySpec(label, virtualKey, width, false, false);
        }

        private static KeySpec K(string label, ushort virtualKey, float width, bool extended)
        {
            return new KeySpec(label, virtualKey, width, extended, false);
        }

        private static KeySpec M(string label, ushort virtualKey, float width)
        {
            return new KeySpec(label, virtualKey, width, false, true);
        }

        private static KeySpec M(string label, ushort virtualKey, float width, bool extended)
        {
            return new KeySpec(label, virtualKey, width, extended, true);
        }

        private sealed class KeySpec
        {
            internal KeySpec(string label, ushort virtualKey, float width, bool extended, bool modifier)
            {
                Label = label;
                VirtualKey = virtualKey;
                Width = width;
                Extended = extended;
                Modifier = modifier;
            }

            internal string Label;
            internal ushort VirtualKey;
            internal float Width;
            internal bool Extended;
            internal bool Modifier;
        }
    }

internal interface IShortcutBindable
    {
		ShortcutBindingTarget BindingTarget { get; }
		void SetBindingSelected(bool selected);
    }

    internal sealed class VirtualKeyButton : MonoBehaviour,
		IPointerDownHandler, IPointerUpHandler, IPointerExitHandler,
        IPointerEnterHandler, IShortcutBindable
    {
        private VrKeyboardOverlay _owner;
        private ushort _virtualKey;
        private bool _extended;
        private bool _modifier;
        private bool _pressed;
        private bool _bindingSelected;
        private string _keyLabel;
        private Image _image;
        private Text _label;
        private Color _normalColor;

        internal void Configure(
            VrKeyboardOverlay owner,
            string keyLabel,
            ushort virtualKey,
            bool extended,
            bool modifier,
            Image image,
            Text label)
        {
            _owner = owner;
            _keyLabel = keyLabel;
            _virtualKey = virtualKey;
            _extended = extended;
            _modifier = modifier;
            _image = image;
            _label = label;
            _normalColor = image.color;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_owner != null) VrHaptics.Hover();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_owner == null)
                return;
            VrHaptics.Press();

            if (_owner.BindingMode)
            {
				_owner.ToggleBindingTarget(this);
                return;
            }

            if (_modifier)
            {
                bool active = _owner.ToggleModifier(_virtualKey, _extended);
                _image.color = active ? new Color(0.10f, 0.63f, 0.76f, 1f) : _normalColor;
                _label.color = Color.white;
                return;
            }

            _owner.KeyDown(_virtualKey, _extended);
            _pressed = true;
            _image.color = new Color(0.10f, 0.55f, 0.68f, 1f);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            ReleaseNormalKey();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            ReleaseNormalKey();
        }

        private void OnDisable()
        {
            ReleaseNormalKey();
        }

        private void ReleaseNormalKey()
        {
            if (!_pressed || _modifier || _owner == null)
                return;

            _owner.KeyUp(_virtualKey, _extended);
            _pressed = false;
            if (_image != null)
                _image.color = _normalColor;
        }

        internal void ResetVisual()
        {
            _pressed = false;
            if (_image != null)
                _image.color = _bindingSelected
                    ? new Color(0.88f, 0.50f, 0.08f, 1f)
                    : _normalColor;
            if (_label != null)
                _label.color = Color.white;
        }

		public ShortcutBindingTarget BindingTarget
        {
			get { return ShortcutBindingTarget.Key(_keyLabel, _virtualKey, _extended); }
        }

		public void SetBindingSelected(bool selected)
        {
            _bindingSelected = selected;
            if (_image != null)
                _image.color = selected
                    ? new Color(0.88f, 0.50f, 0.08f, 1f)
                    : _normalColor;
        }
    }

	internal sealed class ShortcutActionButton : MonoBehaviour, IShortcutBindable
	{
		private VrKeyboardOverlay _owner;
		private string _label;
		private Action _action;
		private Image _image;
		private Color _normalColor;
		private bool _bindingSelected;

		internal void Configure(
			VrKeyboardOverlay owner, string label, Action action, Image image)
		{
			_owner = owner;
			_label = label;
			_action = action;
			_image = image;
			_normalColor = image.color;
		}

		public void Click()
		{
			if (_owner == null) return;
			VrHaptics.Press();
			if (_owner.BindingMode)
			{
				_owner.ToggleBindingTarget(this);
				return;
			}
			if (_action != null) _action();
		}

		public ShortcutBindingTarget BindingTarget
		{
			get { return ShortcutBindingTarget.ActionButton(_label, _action); }
		}

		public void SetBindingSelected(bool selected)
		{
			_bindingSelected = selected;
			if (_image != null)
				_image.color = selected
					? new Color(0.88f, 0.50f, 0.08f, 1f)
					: _normalColor;
		}

		internal void SetNormalColor(Color color)
		{
			_normalColor = color;
			if (_image != null && !_bindingSelected)
				_image.color = color;
		}
	}

    internal sealed class KeyboardDragHandle : MonoBehaviour,
        IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        private VrKeyboardOverlay _owner;

        internal void Configure(VrKeyboardOverlay owner, Transform keyboard)
        {
            _owner = owner;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_owner != null)
                _owner.BeginDrag();
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_owner != null)
                _owner.Tick();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (_owner != null)
                _owner.EndDrag();
        }
    }

    internal static class WindowsKeyboard
    {
        private const uint WmKeyDown = 0x0100;
        private const uint WmKeyUp = 0x0101;
        private const uint MapVkToScanCode = 0;
        private static IntPtr _gameWindow;
        private static IntPtr _enumFallback;
        private static uint _currentProcessId;

        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(
            IntPtr window, uint message, IntPtr wordParameter, IntPtr longParameter);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(
            EnumWindowsCallback callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(
            IntPtr window, StringBuilder className, int maximumCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        internal static bool SendGlobal(ushort virtualKey, bool keyUp, bool extended)
        {
            IntPtr window = GetGameWindowHandle();
            if (window == IntPtr.Zero) return false;
            SetForegroundWindow(window);
            uint flags = (extended ? 1u : 0u) | (keyUp ? 2u : 0u);
            keybd_event((byte)virtualKey,
                (byte)MapVirtualKey(virtualKey, MapVkToScanCode),
                flags, UIntPtr.Zero);
            return true;
        }

        internal static bool Send(ushort virtualKey, bool keyUp, bool extended)
        {
            IntPtr window = GetGameWindowHandle();
            return window != IntPtr.Zero &&
                   SendToWindow(window, virtualKey, keyUp, extended);
        }

        internal static bool SendToWindow(
            IntPtr window, ushort virtualKey, bool keyUp, bool extended)
        {
            if (window == IntPtr.Zero || !IsWindow(window))
                return false;

            uint scanCode = MapVirtualKey(virtualKey, MapVkToScanCode);
            uint longParameter = 1u | (scanCode << 16);
            if (extended)
                longParameter |= 1u << 24;
            if (keyUp)
                longParameter |= (1u << 30) | (1u << 31);

            return PostMessage(
                window,
                keyUp ? WmKeyUp : WmKeyDown,
                new IntPtr(virtualKey),
                new IntPtr(unchecked((int)longParameter)));
        }

        internal static IntPtr GetGameWindowHandle()
        {
            if (_gameWindow != IntPtr.Zero && IsWindow(_gameWindow))
                return _gameWindow;

            Process process = Process.GetCurrentProcess();
            process.Refresh();
            _gameWindow = process.MainWindowHandle;
            if (_gameWindow != IntPtr.Zero && IsWindow(_gameWindow))
                return _gameWindow;

            _currentProcessId = (uint)process.Id;
            _enumFallback = IntPtr.Zero;
            EnumWindows(FindGameWindow, IntPtr.Zero);
            if (_gameWindow == IntPtr.Zero)
                _gameWindow = _enumFallback;
            return _gameWindow;
        }

        internal static IntPtr GetForegroundWindowHandle()
        {
            return GetForegroundWindow();
        }

        private static bool FindGameWindow(IntPtr window, IntPtr parameter)
        {
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (processId != _currentProcessId)
                return true;

            if (_enumFallback == IntPtr.Zero)
                _enumFallback = window;

            StringBuilder className = new StringBuilder(128);
            GetClassName(window, className, className.Capacity);
            if (className.ToString().IndexOf("UnityWndClass", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _gameWindow = window;
                return false;
            }
            return true;
        }
    }

    internal sealed class VrHoverFeedback : MonoBehaviour
    {
        private Outline _outline;

        internal void SetHovered(bool hovered)
        {
            if (_outline == null)
            {
                _outline = gameObject.AddComponent<Outline>();
                _outline.effectColor = new Color(1f, 0.18f, 0.08f, 1f);
                _outline.effectDistance = new Vector2(4f, -4f);
                _outline.useGraphicAlpha = false;
            }
            _outline.enabled = hovered;
        }
    }
}







