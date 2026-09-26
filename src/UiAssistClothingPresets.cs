using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal static partial class UiAssistHudLink
    {
        private static UIDynamicButton _editorVisibilityButton;
        private static GameObject _presetList;
        private static object _presetEditor;
        private static float _nextPresetCheck;
        private static bool _presetBrowsing;
        // Editor snapshot captured when the browser was opened; if a second
        // mode button is pressed while it is still open, FindEditor may no
        // longer see the (hidden) editor — reuse this so the button can
        // retarget the open dialog instead of going dead.
        private static Snapshot _presetState;

        private static void ClearPresetButtons()
        {
            ClearPanelPresentation();
            if (_editorVisibilityButton != null) UnityEngine.Object.Destroy(_editorVisibilityButton.gameObject);
            _editorVisibilityButton = null;
            _presetList = null;
            _presetEditor = null;
        }

        private static void UpdatePresetButtons(SuperController sc)
        {
            if (Time.unscaledTime < _nextPresetCheck) return;
            _nextPresetCheck = Time.unscaledTime + 0.5f;
            // Self-heal: a browser torn down without firing its callback
            // (host window rebuilt, open-time exception) latches these
            // forever — dock stays "picking" and new opens are blocked.
            // IsOpen reads activeSelf, which stays true when only the host
            // deactivated — HUD collapse still keeps the session alive.
            if ((_pdPicking || _presetBrowsing) && !VrPresetBrowser.IsOpen)
            {
                _pdPicking = false;
                _presetBrowsing = false;
                _presetState = null;
            }
            if (sc.isLoading || !sc.MainHUDVisible) { if (!_pdPicking) ClearPresetDock(); ClearFavoritesBar(); ClearBanBar(); ClearLockBar(); ClearPresetButtons(); return; }
            try
            {
                Snapshot state = FindEditor(sc);
                if (state == null) { if (!_pdPicking) ClearPresetDock(); ClearFavoritesBar(); ClearBanBar(); ClearLockBar(); ClearPresetButtons(); return; }
                GameObject list = Read(state.Editor.GetType(), state.Editor, "aceScrollListGO") as GameObject;
                if (list == null) { if (!_pdPicking) ClearPresetDock(); ClearFavoritesBar(); ClearBanBar(); ClearLockBar(); ClearPresetButtons(); return; }
                UpdatePresetDock(state, list);
                UpdateFavoritesBar(state, list);
                UpdateBanBar(state, list);
                UpdateLockBar(state, list);
                if (_presetList == list && ReferenceEquals(_presetEditor, state.Editor) &&
                    _editorVisibilityButton != null) return;
                ClearPresetButtons();
                object canvas = Read(state.Editor.GetType(), state.Editor, "_uiButtonCanvas");
                _presetList = list;
                _presetEditor = state.Editor;
                // Saving remains on the preset dock; this independent toggle
                // stays clickable when only the editor body is hidden.
                _editorVisibilityButton = CreatePresetButton(canvas, list, "隐藏", 36f,
                    ToggleEditorVisibility);
                BindPanelPresentation(canvas, list);
            }
            catch (Exception e) { ClearPresetButtons(); _nextPresetCheck = Time.unscaledTime + 5f; Error(e); }
        }

        private static UIDynamicButton CreatePresetButton(object canvas, GameObject list,
            string label, float offset, Action onClick)
        {
            MethodInfo create = canvas.GetType().GetMethod("CreateUIButtonInCanvas", Flags);
            ParameterInfo[] parameters = create.GetParameters();
            object[] args = new object[parameters.Length];
            object[] values = { label, 0f, 0f, 0f, 68f, 30f, 16 };
            for (int i = 0; i < args.Length; i++)
                args[i] = i < values.Length ? Convert.ChangeType(values[i], parameters[i].ParameterType) : parameters[i].DefaultValue;
            UIDynamicButton button = (UIDynamicButton)create.Invoke(canvas, args);
            RectTransform rect = (RectTransform)button.transform;
            RectTransform listRect = (RectTransform)list.transform;
            Vector3[] corners = new Vector3[4];
            listRect.GetWorldCorners(corners);
            // Dedicated header row above existing clear/scroll controls; same
            // native canvas/raycast path as the editor, not an independent overlay.
            rect.position = corners[2] + listRect.TransformVector(new Vector3(offset, 64f, 0f));
            rect.SetAsLastSibling();
            button.button.onClick.AddListener(delegate { onClick(); });
            return button;
        }

        private static void BrowseClothingPresetSave()
        {
            SuperController sc = SuperController.singleton;
            Snapshot state = FindEditor(sc);
            if ((state == null || state.Target == null) && _presetBrowsing)
                state = _presetState;
            if (state == null || state.Target == null) { Log("请先在服装编辑器中选择角色。"); return; }
            string scene = sc.LoadedSceneName;
            _presetBrowsing = true;
            _presetState = state;
            try
            {
                string startDir = PresetSaveDirs.Get("ClothingPresets", PluginPaths.ClothingPresetDir);
                string defaultName =
                    ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString() + ".vap";
                VrPresetBrowser.ShowDialogFull("保存服装预设", startDir, "vap",
                    true, defaultName,
                    delegate(string path, bool closed) {
                    try
                    {
                        if (sc.isLoading || sc.LoadedSceneName != scene || state.Owner == null ||
                            state.Target == null || sc.GetAtomByUid(state.Target.uid) != state.Target) return;
                        Log("服装保存对话框返回：" + (path ?? "<null>"));
                        if (!string.IsNullOrEmpty(path)) SaveEditorClothingPreset(state.Target, path);
                    }
                    catch (Exception e) { Error(e); }
                    finally
                    {
                        if (closed)
                            Quest3TriggerUIPlugin.Instance.StartCoroutine(RestorePresetEditor(state, scene));
                    }
                });
            }
            catch (Exception e) { _presetBrowsing = false; Error(e); }
        }

        // StorePreset resolves the output file purely from pm.presetName
        // (storeFolder + presetSubPath + storeName + "_" + presetSubName +
        // ".vap"), so this writes presetName directly and never touches
        // presetBrowsePath — the sync that could trigger a preset *load*
        // structurally cannot run.
        private static void SaveEditorClothingPreset(Atom target, string path)
        {
            MeshVR.PresetManagerControl presets = target.GetStorableByID("ClothingPresets") as MeshVR.PresetManagerControl;
            if (presets == null) throw new InvalidOperationException("ClothingPresets is unavailable.");
            JSONStorableString nameParam = presets.GetStringJSONParam("presetName");
            MeshVR.PresetManager pm = PresetManagerOf(presets);
            if (nameParam == null || pm == null) throw new InvalidOperationException("Clothing presetName is unavailable.");
            // VaM preset files are "Preset_<name>.vap"; a name typed without
            // the prefix is rejected by GetPresetNameFromFilePath.
            int slash = path.LastIndexOf('/');
            string dir = slash >= 0 ? path.Substring(0, slash) : path;
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            if (file.EndsWith(".vap", StringComparison.OrdinalIgnoreCase))
                file = file.Substring(0, file.Length - 4);
            if (!file.StartsWith("Preset_", StringComparison.OrdinalIgnoreCase))
                file = "Preset_" + file;
            path = SuperController.singleton.NormalizePath(dir + "/" + file + ".vap");
            string name = pm.GetPresetNameFromFilePath(path);
            if (string.IsNullOrEmpty(name))
                name = file.Substring("Preset_".Length); // outside store folder: store root
            if (string.IsNullOrEmpty(pm.storeName))
                pm.storeName = "Preset";
            nameParam.val = name;
            presets.CallAction("StorePresetWithScreenshot");
            int storeSlash = path.LastIndexOfAny(new char[] { '/', '\\' });
            if (storeSlash > 0)
                PresetSaveDirs.Set("ClothingPresets", path.Substring(0, storeSlash));
            Log("保存服装预设：" + path + " (presetName=" + name + ")");
        }

        private static readonly FieldInfo PresetManagerField =
            typeof(MeshVR.PresetManagerControl).GetField("pm",
                BindingFlags.NonPublic | BindingFlags.Instance);

        internal static MeshVR.PresetManager PresetManagerOf(
            MeshVR.PresetManagerControl presets)
        {
            try
            {
                return PresetManagerField == null
                    ? null
                    : PresetManagerField.GetValue(presets) as MeshVR.PresetManager;
            }
            catch { return null; }
        }


        // The preset dock's 新增 button: open the browser in pick mode —
        // every .vap click files it under the dock's active tab without
        // closing, so a whole batch can be saved in one session. The
        // browser docks over the editor (right of the dock); the favorites
        // bar and other side strips hide for the duration via
        // _presetBrowsing.
        private static void OpenPresetDockPicker()
        {
            SuperController sc = SuperController.singleton;
            Snapshot state = FindEditor(sc);
            if ((state == null || state.Target == null) && _presetBrowsing)
                state = _presetState;
            if (state == null || state.Target == null) { Log("请先在服装编辑器中选择角色。"); return; }
            string scene = sc.LoadedSceneName;
            _presetBrowsing = true;
            _presetState = state;
            _pdPicking = true;
            try
            {
                VrPresetBrowser.ShowDialogFull("收藏预设到「" +
                    PdTabNames[_pdTab] + "」", PdTabDir(_pdTab),
                    "vap", false, null,
                    delegate(string path, bool closed) {
                    try
                    {
                        if (sc.isLoading || sc.LoadedSceneName != scene || state.Owner == null ||
                            state.Target == null || sc.GetAtomByUid(state.Target.uid) != state.Target) return;
                        if (!string.IsNullOrEmpty(path)) FileDockPreset(_pdTab, path);
                    }
                    catch (Exception e) { Error(e); }
                    finally
                    {
                        if (closed)
                        {
                            _pdPicking = false;
                            Quest3TriggerUIPlugin.Instance.StartCoroutine(RestorePresetEditor(state, scene));
                        }
                    }
                }, pickMode: true, loadTarget: state.Target);
            }
            catch (Exception e) { _presetBrowsing = false; _pdPicking = false; Error(e); }
        }

        // 读取 — the radial 人物 sub-actions re-homed into the dock: open
        // the compact pick browser at the active tab's directory; every
        // click applies the preset through ApplyDockSlot's per-tab section
        // loader (替换=full person, 外观=minus clothing, 发型/服装/皮肤=
        // their sections, 化妆=makeup merge) without closing, so several
        // looks can be tried in one session.
        private static void OpenDockPresetLoader()
        {
            SuperController sc = SuperController.singleton;
            Snapshot state = FindEditor(sc);
            if ((state == null || state.Target == null) && _presetBrowsing)
                state = _presetState;
            if (state == null || state.Target == null) { Log("请先在服装编辑器中选择角色。"); return; }
            string scene = sc.LoadedSceneName;
            _presetBrowsing = true;
            _presetState = state;
            _pdPicking = true;
            try
            {
                VrPresetBrowser.ShowDialogFull("读取「" +
                    PdTabNames[_pdTab] + "」预设", PdTabDir(_pdTab),
                    "vap", false, null,
                    delegate(string path, bool closed) {
                    try
                    {
                        if (sc.isLoading || sc.LoadedSceneName != scene || state.Owner == null ||
                            state.Target == null || sc.GetAtomByUid(state.Target.uid) != state.Target) return;
                        if (!string.IsNullOrEmpty(path))
                            ApplyDockSlot(path,
                                VrPresetBrowser.LoadTarget ?? state.Target);
                    }
                    catch (Exception e) { Error(e); }
                    finally
                    {
                        if (closed)
                        {
                            _pdPicking = false;
                            Quest3TriggerUIPlugin.Instance.StartCoroutine(RestorePresetEditor(state, scene));
                        }
                    }
                }, pickMode: true, loadTarget: state.Target,
                personMode: _pdTab == 0);
            }
            catch (Exception e) { _presetBrowsing = false; _pdPicking = false; Error(e); }
        }

        // 保存 — same per-tab split as the radial person sub-buttons:
        // 替换/外观 write a full person preset into the shared Appearance
        // dir, 服装 picks a person .vap to merge the clothing section into,
        // 化妆 writes a standalone preset of only the worn makeup items.
        private static void OpenDockPresetSaver()
        {
            SuperController sc = SuperController.singleton;
            Snapshot state = FindEditor(sc);
            if ((state == null || state.Target == null) && _presetBrowsing)
                state = _presetState;
            if (state == null || state.Target == null) { Log("请先在服装编辑器中选择角色。"); return; }
            string scene = sc.LoadedSceneName;
            if (_pdQuick == null)
                _pdQuick = new SceneQuickActions(Quest3TriggerUIPlugin.Instance);
            Atom target = state.Target;
            _presetBrowsing = true;
            _presetState = state;
            _pdPicking = true;
            Action<bool> done = delegate(bool closed)
            {
                if (!closed) return;
                _pdPicking = false;
                Quest3TriggerUIPlugin.Instance.StartCoroutine(
                    RestorePresetEditor(state, scene));
            };
            try
            {
                switch (_pdTab)
                {
                    case 0: // 人物
                        _pdQuick.SavePersonPresetFor(target, "人物", true, done);
                        break;
                    case 1: // 发型
                        _pdQuick.SaveHairPresetFor(target, true, done);
                        break;
                    case 2: // 服装
                        _pdQuick.SaveClothingPresetFor(target, true, done);
                        break;
                    case 3: // 皮肤
                        _pdQuick.SaveSkinPresetFor(target, true, done);
                        break;
                    case 4: // 化妆
                        _pdQuick.SaveMakeupPresetFor(target, true, done);
                        break;
                }
            }
            catch (Exception e) { _presetBrowsing = false; _pdPicking = false; Error(e); }
        }

        private static IEnumerator RestorePresetEditor(Snapshot state, string scene)
        {
            yield return null; // Let the native browser finish its own close/hide callbacks.
            try
            {
                SuperController sc = SuperController.singleton;
                if (sc == null || sc.isLoading || sc.LoadedSceneName != scene || state.Owner == null || state.Target == null) yield break;
                if (!ReferenceEquals(Read(state.Globals, null, "mvrScript"), state.Owner)) yield break;
                CancelPending();
                Write(state.Globals, null, "hideGameControlUI", false);
                Write(state.Control, null, "gameControlDisplayMode", state.Ace);
                Write(state.Control, null, "gridsActivated", true);
                sc.activeUI = SuperController.ActiveUI.None;
                sc.ShowMainHUD(true, false);
                Call(state.Display.GetType(), state.Display, "DestroyUIButtons");
                Call(state.Display.GetType(), state.Display, "CreateUIButtons");
                state.Editor.GetType().GetMethod("RefreshClothing", Flags).Invoke(state.Editor, new object[] { state.Target.uid });
                Call(state.Control, null, "OnEnable");
                _nextPresetCheck = 0f;
            }
            finally { _presetBrowsing = false; _presetState = null; }
        }
    }
}

